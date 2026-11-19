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
