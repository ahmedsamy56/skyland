using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

// Bluetooth and USB device pop-ups: "AirPods Pro connected / Bluetooth / 82%", "USB drive connected / Kingston (E:) / 32 GB".
// They use the same 3-second pop-up and queue as the microphone and camera indicator, and leave no dot behind.
public partial class MainWindow
{
    private static readonly Brush DeviceBlue = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
    private static readonly Brush DeviceGone = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA3)));

    private static readonly Geometry BluetoothIcon = Freeze(Geometry.Parse("M7,7.5 L17,16.5 L12,21 V3 L17,7.5 L7,16.5"));
    private static readonly Geometry UsbIcon = Freeze(Geometry.Parse(
        "M12,3 V17 M12,17 A2,2 0 1 0 12,21 A2,2 0 1 0 12,17 M12,9 L16,6 M16,6 V8 M12,12 L8,9 M8,9 V11"));

    private const int MaxWaitingPopups = 3;   // a burst (for example after waking from sleep) must not queue a long parade

    private readonly DeviceService _devices = new();
    private IntPtr _windowHandle;

    private void InitDevices() => _devices.Event += e => Dispatcher.InvokeAsync(() => OnDeviceEvent(e));

    // Called once the window exists: WM_DEVICECHANGE (USB) arrives through this hook.
    private void HookDevices(IntPtr hwnd)
    {
        _windowHandle = hwnd;
        HwndSource.FromHwnd(hwnd)?.AddHook(DeviceMessageHook);
        ApplyDeviceSetting();
    }

    private IntPtr DeviceMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        _devices.HandleMessage(msg, wParam, lParam);
        return IntPtr.Zero;
    }

    private void ApplyDeviceSetting()
    {
        if (_windowHandle == IntPtr.Zero) return;

        if (SettingsService.Current.ShowDeviceEvents) _devices.Start(_windowHandle);
        else _devices.Stop();
    }

    private void OnDeviceEvent(DeviceEvent e)
    {
        if (!SettingsService.Current.ShowDeviceEvents) return;
        if (_privacyQueue.Count >= MaxWaitingPopups) return;

        _privacyQueue.Enqueue(new PopupItem(
            e.Kind == DeviceKind.Bluetooth ? BluetoothIcon : UsbIcon,
            e.Connected ? DeviceBlue : DeviceGone,
            e.Title, e.Sub, e.Trail, Live: false));

        if (!_privacyPopup) ShowNextPrivacyPopup();
    }
}
