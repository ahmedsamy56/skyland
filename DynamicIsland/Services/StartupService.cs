using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>
/// Registers the app to start with Windows via the per-user Run key
/// (no admin rights needed).
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SkyLand";

    // The app used to be called DynamicIsland; its autostart entry is moved over at startup.
    private const string LegacyValueName = "DynamicIsland";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (enabled)
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null)
                key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Moves an autostart entry made under the old name to the new one, pointing at this exe.
    /// Also keeps the entry correct if the app was moved to another folder.
    /// </summary>
    public static void MigrateLegacyEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            bool hadLegacy = key.GetValue(LegacyValueName) is string;
            if (hadLegacy)
            {
                string? exe = Environment.ProcessPath;
                if (exe is not null) key.SetValue(ValueName, $"\"{exe}\"");
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Not critical: the user can switch Start with Windows off and on again in Settings.
        }
    }
}
