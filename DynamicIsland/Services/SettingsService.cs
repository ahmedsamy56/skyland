using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicIsland.Services;

public enum ExpandTrigger
{
    Hover,
    Click
}

/// <summary>
/// User settings stored in %AppData%\DynamicIsland\settings.json.
/// "Start with Windows" is not stored here: the registry Run key is its source of truth.
/// </summary>
public sealed class AppSettings
{
    public bool AutoPopOnTrackChange { get; set; } = false;

    /// <summary>Mirror Focus sessions and timers from the Windows Clock app.</summary>
    public bool SyncMicrosoftClock { get; set; } = true;

    /// <summary>Show Claude Code sessions (needs the hooks from Settings > Connect Claude Code).</summary>
    public bool ShowAgentStatus { get; set; } = true;

    /// <summary>Look for a newer SkyLand on start and about once a day, and offer it in the island.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>"Later" on the update prompt: do not offer again before this time.</summary>
    public DateTime? UpdateSnoozeUntilUtc { get; set; }

    /// <summary>Set once the installer's "start with Windows by default" has been applied, so an update never re-enables it after the user turned it off.</summary>
    public bool AutostartDefaultApplied { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExpandTrigger ExpandTrigger { get; set; } = ExpandTrigger.Hover;
}

public static class SettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DynamicIsland", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable file: fall back to defaults.
        }
        return new AppSettings();
    }

    /// <summary>Raised after every Save, on the calling (UI) thread.</summary>
    public static event Action? Changed;

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch
        {
            // Settings are a convenience; never crash the island over a failed save.
        }

        Changed?.Invoke();
    }
}
