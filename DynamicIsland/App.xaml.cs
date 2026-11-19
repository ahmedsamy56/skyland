using System.Windows;
using DynamicIsland.Services;

namespace DynamicIsland;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;

    /// <summary>One updater for the whole app, shared by the island and the Settings window.</summary>
    public static UpdateService Updates { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Run by the installer just before an uninstall: remove "Start with Windows" and leave.
        if (e.Args.Contains("--uninstall"))
        {
            StartupService.SetEnabled(false);
            Shutdown();
            return;
        }

        // One island per user session (matters now that it can start with Windows).
        _singleInstance = new Mutex(true, "DynamicIsland.SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        // The app used to be called DynamicIsland: carry its autostart entry and Claude Code hooks over.
        StartupService.MigrateLegacyEntry();
        ClaudeHookInstaller.RepairIfMoved();

        // Started by the installer. "Start with Windows" is on by default, but only the first time: an update
        // runs the installer again and must not switch it back on after the user turned it off.
        if (e.Args.Contains("--installed") && !SettingsService.Current.AutostartDefaultApplied)
        {
            StartupService.SetEnabled(true);
            SettingsService.Current.AutostartDefaultApplied = true;
            SettingsService.Save();
        }

        Updates.ExitRequested += () => Dispatcher.InvokeAsync(Shutdown);

        base.OnStartup(e);
        _tray = new TrayService(ShowSettings, Shutdown);
    }

    /// <summary>Opens the Settings window, or brings the existing one to the front.</summary>
    private void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;

        _settingsWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
