using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace DynamicIsland.Services;

public enum DeviceKind { Bluetooth, UsbDrive, Usb }

/// <param name="Title">The device name ("AirPods Pro"), or "USB drive connected" for drives.</param>
/// <param name="Sub">"Connected", "Disconnected", "Kingston (E:)", "USB device connected"</param>
/// <param name="Trail">Battery level or drive size; may be empty.</param>
public sealed record DeviceEvent(DeviceKind Kind, bool Connected, string Title, string Sub, string Trail);

/// <summary>
/// Raises an event when a Bluetooth device connects or disconnects, or a USB drive or USB device is plugged in or
/// removed. Bluetooth: a WinRT device watcher on connected devices (battery level when Windows reports it). USB drives
/// and devices: WM_DEVICECHANGE messages, which the main window passes in. Nothing is read from the devices.
/// </summary>
public sealed class DeviceService
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevTypVolume = 2;
    private const int DbtDevTypDeviceInterface = 5;
    private static readonly Guid UsbDeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    // Windows keeps a battery level for Bluetooth devices that report one under this property key.
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    // Test seam (screenshots, tests): SKYLAND_DEVICE_FAKE=1 reads %TEMP%\skyland-device-fake.txt; every new line is one
    // event:  bt|+|AirPods Pro|Bluetooth|82%    usb|+|Kingston DataTraveler (E:)|USB drive|32 GB    usb|-|...
    private static readonly bool Fake = Environment.GetEnvironmentVariable("SKYLAND_DEVICE_FAKE") == "1";

    private readonly List<DeviceWatcher> _watchers = new();
    private readonly Dictionary<string, string> _bluetoothNames = new();
    private readonly Dictionary<char, (string Label, string Size)> _drives = new();
    private readonly Dictionary<string, DateTime> _recent = new();
    private readonly object _lock = new();
    private IntPtr _usbNotification;
    private Timer? _fakeTimer;
    private int _fakeLines;
    private bool _running;

    /// <summary>Raised on any thread.</summary>
    public event Action<DeviceEvent>? Event;

    public void Start(IntPtr windowHandle)
    {
        if (_running) return;
        _running = true;

        if (Fake)
        {
            _fakeLines = ReadFakeLines().Length;   // only lines added from now on count
            _fakeTimer = new Timer(_ => PumpFake(), null, 700, 700);
            return;
        }

        try { StartBluetooth(); } catch { /* no Bluetooth radio, or Bluetooth APIs unavailable */ }
        try { RegisterUsb(windowHandle); } catch { /* USB devices will not be announced; drives still are */ }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        _fakeTimer?.Dispose();
        _fakeTimer = null;

        foreach (var watcher in _watchers)
        {
            try { watcher.Stop(); } catch { }
        }
        _watchers.Clear();
        _bluetoothNames.Clear();

        if (_usbNotification != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_usbNotification);
            _usbNotification = IntPtr.Zero;
        }
    }

    // ----- Bluetooth -----------------------------------------------------

    private void StartBluetooth()
    {
        string[] filters =
        [
            BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
            BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected)
        ];

        foreach (string filter in filters)
        {
            var watcher = DeviceInformation.CreateWatcher(filter, new[] { BatteryKey });
            bool ready = false;   // the first pass lists what is already connected: not news

            watcher.Added += (_, info) =>
            {
                lock (_lock) _bluetoothNames[info.Id] = info.Name;
                if (!ready) return;

                string trail = info.Properties.TryGetValue(BatteryKey, out var battery) && battery is byte level and > 0 and <= 100
                    ? $"{level}%" : "";
                Raise(new DeviceEvent(DeviceKind.Bluetooth, true, Clean(info.Name), "Connected", trail));
            };
            watcher.Removed += (_, update) =>
            {
                string? name;
                lock (_lock) { if (!_bluetoothNames.Remove(update.Id, out name)) return; }
                if (ready) Raise(new DeviceEvent(DeviceKind.Bluetooth, false, Clean(name), "Disconnected", ""));
            };
            watcher.EnumerationCompleted += (_, _) => ready = true;
            watcher.Start();
            _watchers.Add(watcher);
        }
    }

    private static string Clean(string name) => string.IsNullOrWhiteSpace(name) ? "Bluetooth device" : name.Trim();

    // ----- USB drives and devices (WM_DEVICECHANGE) ----------------------

    private void RegisterUsb(IntPtr hwnd)
    {
        // DEV_BROADCAST_DEVICEINTERFACE_W: size, type, reserved, class guid, name[1]
        IntPtr filter = Marshal.AllocHGlobal(32);
        try
        {
            for (int i = 0; i < 32; i++) Marshal.WriteByte(filter, i, 0);
            Marshal.WriteInt32(filter, 0, 32);
            Marshal.WriteInt32(filter, 4, DbtDevTypDeviceInterface);
            Marshal.Copy(UsbDeviceInterface.ToByteArray(), 0, filter + 12, 16);
            _usbNotification = RegisterDeviceNotificationW(hwnd, filter, 0);   // 0 = the target is a window
        }
        finally
        {
            Marshal.FreeHGlobal(filter);
        }
    }

    /// <summary>Called from the main window's message hook for WM_DEVICECHANGE.</summary>
    public void HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (!_running || Fake || msg != WmDeviceChange || lParam == IntPtr.Zero) return;

        int what = wParam.ToInt32();
        if (what != DbtDeviceArrival && what != DbtDeviceRemoveComplete) return;
        bool arrived = what == DbtDeviceArrival;

        try
        {
            int type = Marshal.ReadInt32(lParam, 4);
            if (type == DbtDevTypVolume)
                OnVolume(arrived, (uint)Marshal.ReadInt32(lParam, 12), Marshal.ReadInt16(lParam, 16));
            else if (type == DbtDevTypDeviceInterface)
                OnUsbInterface(arrived, Marshal.PtrToStringUni(lParam + 28) ?? "");
        }
        catch
        {
            // A malformed broadcast must never break the island.
        }
    }

    private void OnVolume(bool arrived, uint unitMask, short flags)
    {
        if ((flags & 1) != 0) return;   // DBTF_MEDIA: an optical disc, not a drive

        for (int bit = 0; bit < 26; bit++)
        {
            if ((unitMask & (1u << bit)) == 0) continue;
            char letter = (char)('A' + bit);

            if (arrived)
            {
                try
                {
                    var drive = new DriveInfo(letter.ToString());
                    if (drive.DriveType != DriveType.Removable || !drive.IsReady) continue;

                    string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "USB drive" : drive.VolumeLabel.Trim();
                    string size = FormatSize(drive.TotalSize);
                    lock (_lock) _drives[letter] = (label, size);
                    Raise(new DeviceEvent(DeviceKind.UsbDrive, true, "USB drive connected", $"{label} ({letter}:)", size));
                }
                catch (IOException) { }
            }
            else
            {
                (string Label, string Size) known;
                lock (_lock) { if (!_drives.Remove(letter, out known)) continue; }   // not one we announced
                Raise(new DeviceEvent(DeviceKind.UsbDrive, false, "USB drive removed", $"{known.Label} ({letter}:)", ""));
            }
        }
    }

    // "\\?\USB#VID_046D&PID_C52B#5&2d0a2d3a&0&1#{a5dcbf10-...}" -> the device's name from the Enum registry key.
    private void OnUsbInterface(bool arrived, string path)
    {
        var parts = path.Split('#');
        if (parts.Length < 3) return;

        string? name = UsbName(parts[1], parts[2], out bool skip);
        if (skip || name is null) return;

        Raise(new DeviceEvent(DeviceKind.Usb, arrived, name, arrived ? "USB device connected" : "USB device disconnected", ""));
    }

    private static string? UsbName(string vidPid, string instance, out bool skip)
    {
        skip = false;
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\USB\{vidPid}\{instance}");
        if (key is null) return null;

        // Hubs and Bluetooth radios are plumbing, and USB drives are announced by their volume.
        string service = key.GetValue("Service") as string ?? "";
        if (service.StartsWith("usbhub", StringComparison.OrdinalIgnoreCase) || service.Equals("USBSTOR", StringComparison.OrdinalIgnoreCase)
            || service.StartsWith("BTH", StringComparison.OrdinalIgnoreCase))
        {
            skip = true;
            return null;
        }

        string? friendly = key.GetValue("FriendlyName") as string;
        string? desc = key.GetValue("DeviceDesc") as string;   // "@oem3.inf,%usb.devicedesc%;Logitech USB Receiver"
        string name = !string.IsNullOrWhiteSpace(friendly) ? friendly! : desc is null ? "" : desc[(desc.LastIndexOf(';') + 1)..];
        name = name.Trim();

        if (name.Length == 0) return "USB device";
        if (name.Contains("Hub", StringComparison.OrdinalIgnoreCase) || name.Contains("Root", StringComparison.OrdinalIgnoreCase))
        {
            skip = true;
            return null;
        }
        return name;
    }

    private static string FormatSize(long bytes)
    {
        double gb = bytes / 1e9;   // drives are sold in decimal gigabytes
        return gb >= 1000 ? $"{gb / 1000:0.#} TB" : gb >= 10 ? $"{gb:0} GB" : $"{gb:0.#} GB";
    }

    // ----- Shared --------------------------------------------------------

    // The same device often announces itself twice within a moment (Bluetooth classic and LE, composite USB devices).
    private void Raise(DeviceEvent e)
    {
        string key = $"{e.Kind}|{e.Connected}|{e.Title}";
        lock (_lock)
        {
            if (_recent.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(4)) return;
            _recent[key] = DateTime.UtcNow;
        }
        Event?.Invoke(e);
    }

    // ----- Test seam -----------------------------------------------------

    private static string[] ReadFakeLines()
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "skyland-device-fake.txt");
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch
        {
            return [];
        }
    }

    private void PumpFake()
    {
        var lines = ReadFakeLines();
        for (; _fakeLines < lines.Length; _fakeLines++)
        {
            var f = lines[_fakeLines].Split('|');
            if (f.Length < 5) continue;

            var kind = f[0].Trim().ToLowerInvariant() == "bt" ? DeviceKind.Bluetooth : DeviceKind.Usb;
            bool connected = f[1].Trim() == "+";
            Event?.Invoke(new DeviceEvent(kind, connected, f[2].Trim(), f[3].Trim(), f[4].Trim()));
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotificationW(IntPtr hRecipient, IntPtr notificationFilter, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);
}
