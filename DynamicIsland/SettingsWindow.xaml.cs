using System.Windows;
using System.Windows.Interop;
using DynamicIsland.Native;
using DynamicIsland.Services;

namespace DynamicIsland;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        // Show the current values. Controls use Click (not Checked) events,
        // so setting them here does not trigger a save.
        var settings = SettingsService.Current;
        StartupToggle.IsChecked = StartupService.IsEnabled();
        AutoPopToggle.IsChecked = settings.AutoPopOnTrackChange;
        SyncClockToggle.IsChecked = settings.SyncMicrosoftClock;
        AgentToggle.IsChecked = settings.ShowAgentStatus;
        RefreshHookStatus();
        HoverOption.IsChecked = settings.ExpandTrigger == ExpandTrigger.Hover;
        ClickOption.IsChecked = settings.ExpandTrigger == ExpandTrigger.Click;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) =>
        NativeMethods.UseDarkTitleBar(new WindowInteropHelper(this).Handle);

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        StartupService.SetEnabled(StartupToggle.IsChecked == true);
        // Reflect what actually happened (e.g. the registry write was blocked).
        StartupToggle.IsChecked = StartupService.IsEnabled();
    }

    private void TriggerOption_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Current.ExpandTrigger =
            ClickOption.IsChecked == true ? ExpandTrigger.Click : ExpandTrigger.Hover;
        SettingsService.Save();
    }

    private void AgentToggle_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Current.ShowAgentStatus = AgentToggle.IsChecked == true;
        SettingsService.Save();
    }

    // Connect / Disconnect edit ~/.claude/settings.json (our entries only, with a one-time backup).
    private void HookButton_Click(object sender, RoutedEventArgs e)
    {
        string? error = ClaudeHookInstaller.IsConnected()
            ? ClaudeHookInstaller.Disconnect()
            : ClaudeHookInstaller.Connect();

        RefreshHookStatus();
        if (error is not null) HookStatusHint.Text = error;
    }

    // Tells the user whether Claude Code is actually reaching the island.
    private static string LastEventText()
    {
        if (AgentService.LastEventUtc is not { } last)
            return "No events received since the island started. Start a new Claude Code session and send a message.";

        var ago = DateTime.UtcNow - last;
        string when = ago.TotalSeconds < 60 ? $"{(int)ago.TotalSeconds} s ago"
            : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min ago"
            : $"{(int)ago.TotalHours} h ago";
        return $"Last event received {when}.";
    }

    private void RefreshHookStatus()
    {
        bool connected = ClaudeHookInstaller.IsConnected();
        HookStatusTitle.Text = connected ? "Claude Code hooks: connected" : "Claude Code hooks: not connected";
        HookStatusHint.Text = connected
            ? "Disconnect removes only the island's own entries. " + LastEventText()
            : "Adds the island's hooks to ~/.claude/settings.json (a backup is saved first). Applies to new Claude Code sessions.";
        HookButton.Content = connected ? "Disconnect" : "Connect Claude Code";
    }

    private void SyncClockToggle_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Current.SyncMicrosoftClock = SyncClockToggle.IsChecked == true;
        SettingsService.Save();
    }

    private void AutoPopToggle_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Current.AutoPopOnTrackChange = AutoPopToggle.IsChecked == true;
        SettingsService.Save();
    }
}
