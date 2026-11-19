using System.Drawing;
using System.Windows.Forms;

namespace DynamicIsland.Services;

/// <summary>
/// System tray icon: opens Settings (double-click or menu) and exits the app.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _iconImage;

    public TrayService(Action openSettings, Action exit)
    {
        _iconImage = LoadIcon();

        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += (_, _) => openSettings();

        // Handy if the island was dragged somewhere awkward.
        var resetItem = new ToolStripMenuItem("Reset position");
        resetItem.Click += (_, _) => SettingsService.ResetPosition();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => exit();

        var menu = new ContextMenuStrip();
        menu.Items.Add(settingsItem);
        menu.Items.Add(resetItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = _iconImage,
            Text = "SkyLand",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => openSettings();
    }

    // The SkyLand icon embedded in the app. The .ico holds 16-256 px; Windows is asked for the
    // size that fits the tray at the current display scaling.
    private static Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/skyland.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Icon(stream, SystemInformation.SmallIconSize);
            }
        }
        catch
        {
            // fall through to a stock icon rather than failing to start
        }
        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        _icon.Visible = false; // removes the icon immediately instead of leaving a ghost
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _iconImage.Dispose();
    }
}
