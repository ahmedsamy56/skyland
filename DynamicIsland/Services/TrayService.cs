using System.Drawing;
using System.Drawing.Drawing2D;
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
        _iconImage = CreateIcon();

        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += (_, _) => openSettings();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => exit();

        var menu = new ContextMenuStrip();
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = _iconImage,
            Text = "Dynamic Island",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => openSettings();
    }

    // A small black pill with a light outline, readable on light and dark taskbars.
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(2, 10, 27, 12);

            using var path = new GraphicsPath();
            int d = rect.Height;
            path.AddArc(rect.X, rect.Y, d, d, 90, 180);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 180);
            path.CloseFigure();

            using var fill = new SolidBrush(Color.Black);
            using var outline = new Pen(Color.FromArgb(200, 255, 255, 255), 2);
            g.FillPath(fill, path);
            g.DrawPath(outline, path);
        }

        nint hIcon = bmp.GetHicon();
        using var temp = Icon.FromHandle(hIcon);
        var icon = (Icon)temp.Clone(); // own copy, so the native handle can be released
        DestroyIcon(hIcon);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);

    public void Dispose()
    {
        _icon.Visible = false; // removes the icon immediately instead of leaving a ghost
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _iconImage.Dispose();
    }
}
