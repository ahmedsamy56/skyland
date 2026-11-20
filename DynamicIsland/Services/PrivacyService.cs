using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DynamicIsland.Services;

/// <summary>Which apps are using the camera and the microphone right now.</summary>
public sealed record PrivacyUsage(IReadOnlyList<string> CameraApps, IReadOnlyList<string> MicApps)
{
    public static readonly PrivacyUsage None = new(Array.Empty<string>(), Array.Empty<string>());

    public bool CameraActive => CameraApps.Count > 0;
    public bool MicActive => MicApps.Count > 0;
}

/// <summary>
/// MICROPHONE: asks the Windows audio engine which apps have an active capture session (works on every Windows build),
/// and also reads the registry records below.
/// CAMERA: registry only. Some newer Windows builds (for example 26300) keep camera and microphone usage in a protected
/// database instead (the records carry <c>PersistedInDatabase = 1</c> and stop updating), so on those builds the camera is
/// NOT detected; no reliable signal for it is available to a normal app there.
///
/// The registry records: for every app, <c>HKCU\...\CapabilityAccessManager\ConsentStore\{webcam|microphone}</c>
/// keeps <c>LastUsedTimeStart</c> and <c>LastUsedTimeStop</c> (FILETIME). An app is using the device while it has a
/// start time and a stop time of 0. This is what powers the camera / microphone icon in the Windows taskbar, needs no
/// admin rights and reads no audio or video, only these timestamps and the app's name.
/// </summary>
public static class PrivacyService
{
    private const string StorePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    // An app killed while using the device never writes its stop time. Treat such a record as stale after this long.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);

    // Test seam for screenshots and tests: SKYLAND_PRIVACY_FAKE=1 reads %TEMP%\skyland-privacy-fake.txt instead of the
    // registry. One line per device, e.g.  camera=Zoom  or  microphone=Teams, Zoom
    private static readonly bool Fake = Environment.GetEnvironmentVariable("SKYLAND_PRIVACY_FAKE") == "1";

    public static PrivacyUsage Read()
    {
        if (Fake) return ReadFake();

        try
        {
            // Microphone: the audio system knows which apps are capturing right now (works on every Windows build).
            // The registry is added as a second source. Camera: registry only (see the class comment).
            var mic = ReadCaptureSessions().Concat(ReadDevice("microphone"))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
            return new PrivacyUsage(ReadDevice("webcam"), mic);
        }
        catch
        {
            return PrivacyUsage.None;   // a status display must never break the island
        }
    }

    // Apps with an active capture session on a microphone, from the Windows audio engine (WASAPI). No audio is read:
    // only each session's state and process id.
    private static List<string> ReadCaptureSessions()
    {
        var apps = new List<string>();
        try
        {
            int self = Environment.ProcessId;
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        if (session.State != AudioSessionState.AudioSessionStateActive) continue;

                        uint pid = session.GetProcessID;
                        if (pid == 0 || pid == self) continue;   // 0 = the system sounds session
                        try { apps.Add(FriendlyName(System.Diagnostics.Process.GetProcessById((int)pid).ProcessName, nonPackaged: true)); }
                        catch (ArgumentException) { /* the process ended */ }
                    }
                }
            }
        }
        catch
        {
            // No microphone, or the audio service is restarting: nothing is capturing.
        }
        return apps;
    }

    private static List<string> ReadDevice(string capability)
    {
        var apps = new List<string>();
        using var root = Registry.CurrentUser.OpenSubKey($@"{StorePath}\{capability}");
        if (root is null) return apps;

        foreach (string name in root.GetSubKeyNames())
        {
            if (name == "NonPackaged")
            {
                using var nonPackaged = root.OpenSubKey(name);
                if (nonPackaged is null) continue;
                foreach (string exe in nonPackaged.GetSubKeyNames())
                    AddIfInUse(nonPackaged, exe, nonPackaged: true, apps);
            }
            else
            {
                AddIfInUse(root, name, nonPackaged: false, apps);
            }
        }

        return apps.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddIfInUse(RegistryKey parent, string name, bool nonPackaged, List<string> apps)
    {
        using var key = parent.OpenSubKey(name);
        if (key is null) return;

        long start = AsLong(key.GetValue("LastUsedTimeStart"));
        long stop = AsLong(key.GetValue("LastUsedTimeStop"));
        if (start <= 0 || stop != 0) return;

        // Stale record from an app that was killed while it used the device.
        try
        {
            if (DateTime.UtcNow - DateTime.FromFileTimeUtc(start) > StaleAfter) return;
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        apps.Add(FriendlyName(name, nonPackaged));
    }

    private static long AsLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        byte[] { Length: 8 } b => BitConverter.ToInt64(b, 0),
        _ => 0
    };

    private static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Chrome", ["msedge"] = "Microsoft Edge", ["firefox"] = "Firefox", ["brave"] = "Brave",
        ["ms-teams"] = "Teams", ["teams"] = "Teams", ["zoom"] = "Zoom", ["slack"] = "Slack", ["discord"] = "Discord",
        ["whatsapp"] = "WhatsApp", ["skype"] = "Skype", ["obs64"] = "OBS Studio", ["code"] = "VS Code"
    };

    /// <summary>"C:#Program Files#Zoom#bin#Zoom.exe" -> "Zoom";  "Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "Windows Camera".</summary>
    internal static string FriendlyName(string key, bool nonPackaged)
    {
        string name;
        if (nonPackaged)
        {
            // The registry key name is the exe path with '\' replaced by '#'.
            name = Path.GetFileNameWithoutExtension(key.Replace('#', '\\'));
        }
        else
        {
            // Package family name: "Publisher.Product_hash". Keep the product part.
            name = key.Split('_')[0];
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        }

        if (KnownApps.TryGetValue(name, out var known)) return known;

        // "WindowsCamera" -> "Windows Camera"; "qemu-system-x86_64" stays as is.
        name = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
        return name.Length == 0 ? "An app" : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static PrivacyUsage ReadFake()
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "skyland-privacy-fake.txt");
            if (!File.Exists(path)) return PrivacyUsage.None;

            var cam = new List<string>();
            var mic = new List<string>();
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var apps = line[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                switch (line[..eq].Trim().ToLowerInvariant())
                {
                    case "camera": cam.AddRange(apps); break;
                    case "microphone": mic.AddRange(apps); break;
                }
            }
            return new PrivacyUsage(cam, mic);
        }
        catch
        {
            return PrivacyUsage.None;
        }
    }
}
