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
