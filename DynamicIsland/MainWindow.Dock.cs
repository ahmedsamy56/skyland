using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

// Minimize: the minus button on every open card shrinks the island into a round SkyLand icon (a small window) that glides
// to the left or right edge of the screen. A colored ring around the icon shows what needs attention (amber = Claude needs
// you, orange = microphone, green = camera). Click the icon and the island comes back; drag the icon along an edge to move
// it. The state is remembered across restarts.
public partial class MainWindow
{
    private static readonly Brush DockAmber = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)));
    private static readonly Brush DockOrange = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)));
    private static readonly Brush DockGreen = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));

    private const double DockCircle = 44;
    private const double DockWindowSize = 52;

    private DockWindow? _dock;
    private bool _docked;          // the island is minimized: the round icon is what is on screen
    private bool _dockBusy;        // the minimize or restore animation is running
    private bool _startDocked;     // SkyLand was minimized when it last ran

    // ----- Start-up ------------------------------------------------------

    // Called from the constructor: a minimized island starts minimized, with no intro.
    private void InitDock()
    {
        _startDocked = SettingsService.Current.Minimized;
        if (_startDocked) Opacity = 0;   // the window is shown by StartupUri; hide it as soon as it has loaded
    }

    private void EnterDockedAtStart()
    {
        EnsureDock();
        var (x, y) = SavedOrDefaultDock(new Point(SystemParameters.PrimaryScreenWidth / 2, 0));
        _dock!.Left = x;
        _dock.Top = y;
        _dock.Show();
        Hide();
        Opacity = 1;
        _docked = true;
        UpdateDockStatus();
    }

    private void EnsureDock()
    {
        if (_dock is not null) return;

        _dock = new DockWindow { ForcedLeft = ForcedSide() };
        _dock.RestoreRequested += RestoreFromDock;
        _dock.Docked += (left, top) =>
        {
            var s = SettingsService.Current;
            s.DockLeft = left;
            s.DockTop = top;
            SettingsService.Save();
        };
    }

    // The remembered spot, or: the edge nearest to the island (the right edge when it is centered), a third down the screen.
    private (double Left, double Top) SavedOrDefaultDock(Point islandCentre)
    {
        var s = SettingsService.Current;
        if (s.DockLeft is double l && s.DockTop is double t) return _dock!.Snap(l, t);

        var area = _dock!.WorkAreaAt(islandCentre.X, islandCentre.Y);
        bool left = ForcedSide() ?? islandCentre.X < area.Left + area.Width / 2 - 1;
        return _dock.EdgePosition(area, left, area.Top + area.Height / 3);
    }

    // Settings > Island > Minimized icon: Left, Right, or Automatic (null).
    private static bool? ForcedSide() => SettingsService.Current.MinimizedSide switch
    {
        DockSide.Left => true,
        DockSide.Right => false,
        _ => null
    };

    // The choice changed: if the icon is on screen, it glides to the chosen edge at once.
    private void ApplyDockSetting()
    {
        if (_dock is null) return;

        _dock.ForcedLeft = ForcedSide();
        if (!_docked || _dockBusy || !_dock.IsVisible) return;

        var (x, y) = _dock.Snap(_dock.Left, _dock.Top);
        if (Math.Abs(x - _dock.Left) < 1 && Math.Abs(y - _dock.Top) < 1) return;

        _dock.GlideTo(x, y, TimeSpan.FromMilliseconds(420), () =>
        {
            var s = SettingsService.Current;
            s.DockLeft = _dock.Left;
            s.DockTop = _dock.Top;
            SettingsService.Save();
        });
    }

    // ----- Minimize ------------------------------------------------------

    private Point PillCentreOnScreen()
    {
        var pixels = Pill.PointToScreen(new Point(Pill.ActualWidth / 2, Pill.ActualHeight / 2));
        var toDips = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return toDips.Transform(pixels);
    }

    private void CardMinimize_Click(object sender, RoutedEventArgs e) => MinimizeToDock();

    /// <summary>Shrinks the island into the round icon and sends it to the screen edge.</summary>
    public void MinimizeToDock()
    {
        if (_docked || _dockBusy) return;
        _dockBusy = true;
        EnsureDock();

        _leaveTimer.Stop();
        _hovering = false;
        _hoverSide = false;
        _popping = false;
        ClearOverlaysForDock();

        // 1. The card (or capsule) fades and the pill shrinks into a 44 px circle where it is.
        var centre = PillCentreOnScreen();
        var shrink = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var duration = TimeSpan.FromMilliseconds(360);
        PillContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(DockCircle, duration) { EasingFunction = shrink });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(DockCircle, duration) { EasingFunction = shrink });
        BeginAnimation(PillRadiusProperty, new DoubleAnimation(DockCircle / 2, duration) { EasingFunction = shrink });

        // 2. Hand over to the icon window, which glides from the circle to its place on the edge.
        var handOver = new DispatcherTimer { Interval = duration + TimeSpan.FromMilliseconds(30) };
        handOver.Tick += (_, _) =>
        {
            handOver.Stop();

            _docked = true;
            _dockBusy = false;
            StopLoopsForDock();
            UpdateDockStatus();

            _dock!.Left = centre.X - DockWindowSize / 2;
            _dock.Top = centre.Y - DockWindowSize / 2;
            _dock.Show();
            Hide();

            var (x, y) = SavedOrDefaultDock(centre);
            var s = SettingsService.Current;
            s.Minimized = true;
            SettingsService.Save();
            _dock.GlideTo(x, y, TimeSpan.FromMilliseconds(520), () =>
            {
                s.DockLeft = _dock.Left;
                s.DockTop = _dock.Top;
                SettingsService.Save();
            });
        };
        handOver.Start();
    }

    // Bubbles and the strips under the island are not part of the circle: clear them at once.
    private void ClearOverlaysForDock()
    {
        foreach (var bubble in new[] { Bubble, PerfBubble })
        {
            bubble.BeginAnimation(OpacityProperty, null);
            bubble.Opacity = 1;
            bubble.Tag = null;
            bubble.Visibility = Visibility.Collapsed;
        }
        foreach (var strip in new[] { AlertStrip, PrivacyStrip })
        {
            strip.BeginAnimation(OpacityProperty, null);
            strip.Opacity = 0;
            strip.Visibility = Visibility.Collapsed;
        }
        _alertVisible = false;
        _privacyStripOn = false;
        PrivacyDots.Opacity = 1;
        _dotsVisibleState = true;
    }

    // Looping animations (status rings, equaliser) would keep running in a window nobody sees.
    private void StopLoopsForDock()
    {
        _claudeInCapsule = _claudeInBubble = _claudeInCard = false;
        RefreshClaude();
        _state = IslandState.Idle;
        SetEqualiser(_mediaPlaying);
        _viewKey = "";
    }

    // ----- Restore -------------------------------------------------------

    /// <summary>Brings the island back to the top of the screen (the round icon glides there and opens).</summary>
    public void RestoreIsland()
    {
        if (Dispatcher.CheckAccess()) RestoreFromDock();
        else Dispatcher.InvokeAsync(RestoreFromDock);
    }

    private void RestoreFromDock()
    {
        if (!_docked || _dockBusy || _dock is null) return;
        _dockBusy = true;

        // Where the circle will be once the island is back: the top centre of its window.
        double cx = Left + Width / 2, cy = Top + 6 + DockCircle / 2;
        _dock.GlideTo(cx - DockWindowSize / 2, cy - DockWindowSize / 2, TimeSpan.FromMilliseconds(440), () =>
        {
            // The island appears as a circle exactly where the icon landed, then opens into what it normally shows.
            Pill.BeginAnimation(WidthProperty, null);
            Pill.BeginAnimation(HeightProperty, null);
            BeginAnimation(PillRadiusProperty, null);
            Pill.Width = DockCircle;
            Pill.Height = DockCircle;
            PillRadius = DockCircle / 2;
            PillContent.BeginAnimation(OpacityProperty, null);
            PillContent.Opacity = 0;

            Opacity = 1;
            Show();
            _dock.StopAnimations();
            _dock.Hide();

            _docked = false;
            _dockBusy = false;
            var s = SettingsService.Current;
            s.Minimized = false;
            SettingsService.Save();

            _size = (DockCircle, DockCircle, DockCircle / 2);
            _viewKey = "";
            ApplyState();

            PillContent.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260))
            {
                BeginTime = TimeSpan.FromMilliseconds(220)
            });
        });
    }

    // ----- Status ring ---------------------------------------------------

    // The ring around the round icon: amber = Claude needs you, orange = microphone, green = camera.
    private void UpdateDockStatus()
    {
        if (_dock is null) return;

        var colors = new List<Brush>();
        var what = new List<string>();
        if (_agentSessions.Any(s => s.State == AgentState.Waiting)) { colors.Add(DockAmber); what.Add("Claude needs you"); }
        if (_usage.MicActive) { colors.Add(DockOrange); what.Add("Microphone in use: " + string.Join(", ", _usage.MicApps.Take(2))); }
        if (_usage.CameraActive) { colors.Add(DockGreen); what.Add("Camera in use: " + string.Join(", ", _usage.CameraApps.Take(2))); }

        // Only touch the icon when something changed: the ring pulse restarts every time it is rebuilt.
        string key = string.Join("|", what);
        if (key == _dockStatusKey) return;
        _dockStatusKey = key;
        _dock.SetStatus(colors, string.Join(" · ", what));
    }

    private string _dockStatusKey = "\u0001";
}
