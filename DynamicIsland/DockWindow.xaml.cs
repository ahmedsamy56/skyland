using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Win32;
using DynamicIsland.Native;

namespace DynamicIsland;

/// <summary>
/// The minimized island: a round icon docked on the left or right edge of a screen. Click it and the island comes back;
/// drag it and it snaps to the nearest edge, keeping its height.
/// </summary>
public partial class DockWindow : Window
{
    private const double IconSize = 44;      // the circle
    private const double EdgeGap = 6;        // from the circle to the screen edge
    private const double DragThreshold = 5;  // DIPs of movement before a press becomes a drag

    private bool _pressed, _moved;
    private Point _startMouse;
    private double _startLeft, _startTop;
    private bool _ringBreathing;

    /// <summary>Always dock on this side: true = left, false = right, null = the nearest edge.</summary>
    public bool? ForcedLeft { get; set; }

    /// <summary>The icon was clicked: bring the island back.</summary>
    public event Action? RestoreRequested;

    /// <summary>The icon was dropped on an edge: Left and Top of the window, in DIPs, to remember.</summary>
    public event Action<double, double>? Docked;

    public DockWindow()
    {
        InitializeComponent();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;   // resolution or monitor change: stay on a screen
        Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) =>
        NativeMethods.MakeOverlay(new WindowInteropHelper(this).Handle);

    private void OnDisplayChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() => { if (IsVisible) PlaceAt(Left, Top, animate: false); });

    // ----- Status ring ---------------------------------------------------

    /// <summary>Colored arcs around the circle: one color fills it, several share it equally. None = the quiet gray ring.</summary>
    public void SetStatus(IReadOnlyList<Brush> colors, string tooltip)
    {
        ToolTip = string.IsNullOrEmpty(tooltip) ? "SkyLand: click to show the island" : tooltip + " (click to show the island)";

        RingHost.Children.Clear();
        RingHost.BeginAnimation(OpacityProperty, null);
        RingHost.Opacity = 1;
        _ringBreathing = false;
        if (colors.Count == 0) return;

        const double thickness = 3;
        double units = Math.PI * (IconSize - thickness) / thickness;   // circumference in stroke widths
        for (int i = 0; i < colors.Count; i++)
        {
            double share = 1.0 / colors.Count;
            var arc = new Ellipse
            {
                Stroke = colors[i],
                StrokeThickness = thickness,
                StrokeDashArray = new DoubleCollection { units * share, units },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(-90 + 360 * share * i)
            };
            RingHost.Children.Add(arc);
        }

        // A gentle pulse while something wants attention. Looping animations repaint this see-through window in
        // software, so it is slow and low-rate.
        var breathe = new DoubleAnimation(1, 0.55, TimeSpan.FromMilliseconds(1200))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(breathe, 20);
        RingHost.BeginAnimation(OpacityProperty, breathe);
        _ringBreathing = true;
    }

    /// <summary>Stops the pulse while the icon is hidden, so nothing animates off screen.</summary>
    public void StopAnimations()
    {
        if (!_ringBreathing) return;
        RingHost.BeginAnimation(OpacityProperty, null);
        RingHost.Opacity = 1;
        _ringBreathing = false;
    }

    // ----- Placing and moving --------------------------------------------

    // The screen scaling. A window that has not been shown yet has none, so borrow the main window's.
    private Matrix ToDevice() =>
        (PresentationSource.FromVisual(this) ?? PresentationSource.FromVisual(Application.Current.MainWindow))
            ?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;

    /// <summary>The work area of the screen that holds the given point (DIPs), in DIPs.</summary>
    public Rect WorkAreaAt(double dipX, double dipY)
    {
        var toDevice = ToDevice();
        var toDips = toDevice;
        toDips.Invert();

        var px = toDevice.Transform(new Point(dipX, dipY));
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)px.X, (int)px.Y));
        var wa = screen.WorkingArea;
        var topLeft = toDips.Transform(new Point(wa.Left, wa.Top));
        var bottomRight = toDips.Transform(new Point(wa.Right, wa.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>Left and Top of the window when its circle sits on the given edge at the given height.</summary>
    public (double Left, double Top) EdgePosition(Rect area, bool left, double top)
    {
        double margin = (Width - IconSize) / 2;
        double x = left ? area.Left + EdgeGap - margin : area.Right - EdgeGap - IconSize - margin;
        double y = Math.Clamp(top, area.Top + 4, Math.Max(area.Top + 4, area.Bottom - Height - 4));
        return (x, y);
    }

    /// <summary>Snaps a free position to the nearest edge of its screen (and keeps it on that screen).</summary>
    public (double Left, double Top) Snap(double left, double top)
    {
        var area = WorkAreaAt(left + Width / 2, top + Height / 2);
        bool toLeft = ForcedLeft ?? (left + Width / 2 < area.Left + area.Width / 2);
        return EdgePosition(area, toLeft, top);
    }

    private void PlaceAt(double left, double top, bool animate)
    {
        var (x, y) = Snap(left, top);
        if (!animate)
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = x;
            Top = y;
            return;
        }
        GlideTo(x, y, TimeSpan.FromMilliseconds(240), null);
    }

    /// <summary>Moves the window smoothly. The animation is released at the end so Left/Top can be set normally again.</summary>
    public void GlideTo(double x, double y, TimeSpan duration, Action? done)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var moveX = new DoubleAnimation(x, duration) { EasingFunction = ease };
        var moveY = new DoubleAnimation(y, duration) { EasingFunction = ease };
        moveX.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = x;
            Top = y;
            done?.Invoke();
        };
        BeginAnimation(LeftProperty, moveX);
        BeginAnimation(TopProperty, moveY);
    }

    // ----- Mouse ---------------------------------------------------------

    private Point MouseOnScreen(MouseEventArgs e)
    {
        var pixels = PointToScreen(e.GetPosition(this));
        var toDips = ToDevice();
        toDips.Invert();
        return toDips.Transform(pixels);
    }

    private void Icon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        _moved = false;
        _startMouse = MouseOnScreen(e);
        _startLeft = Left;
        _startTop = Top;
        ((UIElement)sender).CaptureMouse();
    }

    private void Icon_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndPress((UIElement)sender, snap: _moved); return; }

        var mouse = MouseOnScreen(e);
        double dx = mouse.X - _startMouse.X, dy = mouse.Y - _startMouse.Y;
        if (!_moved && Math.Abs(dx) + Math.Abs(dy) < DragThreshold) return;

        _moved = true;
        Left = _startLeft + dx;
        Top = _startTop + dy;
    }

    private void Icon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool wasDrag = _moved;
        EndPress((UIElement)sender, snap: wasDrag);
        if (!wasDrag) RestoreRequested?.Invoke();   // a click, not a drag
    }

    private void EndPress(UIElement sender, bool snap)
    {
        _pressed = false;
        sender.ReleaseMouseCapture();
        if (!snap) return;

        var (x, y) = Snap(Left, Top);
        GlideTo(x, y, TimeSpan.FromMilliseconds(240), () => Docked?.Invoke(Left, Top));
    }
}
