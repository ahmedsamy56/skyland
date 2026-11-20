using System.Windows;
using DynamicIsland.Services;

namespace DynamicIsland;

// Update prompt in the island: "SkyLand 1.1.0 available [Install] [Later]", then download progress.
public partial class MainWindow
{
    private bool _updatePrompt;
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };

    private void InitUpdates()
    {
        App.Updates.Changed += () => Dispatcher.InvokeAsync(OnUpdateChanged);
        _updateTimer.Tick += async (_, _) => await CheckIfDueAsync();
    }

    // Check shortly after start (not while the island is still settling in), then every few hours,
    // but at most about once a day.
    private async Task StartUpdatesAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        await CheckAsync();
        _updateTimer.Start();
    }

    private async Task CheckIfDueAsync()
    {
        var last = SettingsService.Current.LastUpdateCheckUtc;
        if (last is { } l && DateTime.UtcNow - l < TimeSpan.FromHours(23)) return;
        await CheckAsync();
    }

    private async Task CheckAsync()
    {
        if (!SettingsService.Current.AutoCheckUpdates) return;
        if (!await App.Updates.CheckAsync()) return;

        // "Later" snoozes the prompt for a day; the Settings page can still install at any time.
        if (SettingsService.Current.UpdateSnoozeUntilUtc is { } until && until > DateTime.UtcNow) return;

        _updatePrompt = true;
        RefreshUpdateView();
        ApplyState();
    }

    // Status changes from anywhere (including the Settings button) land here.
    private void OnUpdateChanged()
    {
        var status = App.Updates.Status;

        // Installing from Settings should also show progress in the island.
        if (status is UpdateStatus.Downloading or UpdateStatus.Installing && !_updatePrompt)
        {
            _updatePrompt = true;
            ApplyState();
        }

        if (_updatePrompt) RefreshUpdateView();
    }

    private void RefreshUpdateView()
    {
        var updates = App.Updates;
        bool offer = updates.Status == UpdateStatus.Available;
        bool failed = updates.Status == UpdateStatus.Failed;

        // Two lines, like the capsules: what it is, and the detail under it.
        (UpdateTitle.Text, UpdateSub.Text) = updates.Status switch
        {
            UpdateStatus.Available when updates.Available is { } u => ($"Update {UpdateService.Format(u.Version)}", "Ready to install"),
            UpdateStatus.Downloading => ("Updating SkyLand", updates.StatusText),
            UpdateStatus.Installing => ("Updating SkyLand", "Installing..."),
            UpdateStatus.Failed => ("Update failed", "Try again in Settings"),
            _ => ("SkyLand", updates.StatusText)
        };

        UpdateInstallButton.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
        UpdateLaterButton.Content = failed ? "OK" : "Later";
        UpdateLaterButton.Visibility = offer || failed ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void UpdateInstall_Click(object sender, RoutedEventArgs e) => await App.Updates.InstallAsync();

    private void UpdateLater_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Current.UpdateSnoozeUntilUtc = DateTime.UtcNow.AddHours(24);
        SettingsService.Save();

        _updatePrompt = false;
        ApplyState();
    }
}
