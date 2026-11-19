using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using DynamicIsland.Native;
using DynamicIsland.Services;

namespace DynamicIsland;

public partial class MainWindow : Window
{
    private enum IslandState { Idle, Compact, FocusCompact, Expanded, Notice }
    private enum Page { Media, Focus }

    // Pill sizes, matching the approved design preview.
    private static readonly (double W, double H, double R) IdleSize = (200, 46, 23);
    private static readonly (double W, double H, double R) CompactSize = (230, 36, 18);
    private static readonly (double W, double H, double R) FocusCompactSize = (250, 36, 18);
    private static readonly (double W, double H, double R) ExpandedSize = (400, 132, 30);
    private static readonly (double W, double H, double R) NoticeSize = (230, 40, 20);

    // The Focus page is smaller than the media card: it only needs a ring and three lines of text.
    private static readonly (double W, double H, double R) FocusExpandedSize = (310, 116, 28);

    private static (double W, double H, double R) SizeOf(IslandState state, Page page) => state switch
    {
        IslandState.Expanded => page == Page.Focus ? FocusExpandedSize : ExpandedSize,
        IslandState.Compact => CompactSize,
        IslandState.FocusCompact => FocusCompactSize,
        IslandState.Notice => NoticeSize,
        _ => IdleSize
    };

    private static readonly Brush FocusOrange = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6A, 0x3D)));
    private static readonly Brush TimerBlue = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA3)));
    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)));
    private static readonly Brush PagerOff = Freeze(new SolidColorBrush(Color.FromRgb(0x46, 0x46, 0x4F)));

    // Ring: path diameter is 22 - 3 (stroke), dash lengths are measured in stroke widths.
    private const double RingDashUnits = Math.PI * (22 - 3) / 3;
    private const double BigRingDashUnits = Math.PI * (80 - 7) / 7;
    private static readonly Brush WarnAmber = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)));

    private static readonly Brush ChargingGreen = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));

    private static readonly Brush PlaceholderArt = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)));
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";

    private readonly MediaService _media = new();
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _popTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    // Charging / unplugged pop-up
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _notice;
    private bool _lastPluggedIn = SystemStatsService.ReadBattery().PluggedIn;

    // PC stats: sampled only while the idle pill is showing.
    private readonly SystemStatsService _stats = new();
    private readonly DispatcherTimer _statsTimer = new();
    private bool _sampling;

    private static readonly Brush NormalBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x7A)));

    // Microsoft Clock mirror (Focus sessions and timers)
    private readonly ClockSyncService _clock = new();
    private ClockSnapshot? _clockSnap;
    private Page _page = Page.Media;       // page the expanded card should show
    private Page _shownPage = Page.Media;  // page currently faded in

    private IslandState _state = IslandState.Idle;
    private (double W, double H, double R) _size = IdleSize;   // size the pill is animating to
    private MediaSnapshot? _current;
    private bool _hovering;          // expanded by the mouse (hover or click, per settings)
    private bool _popping;           // expanded briefly because the track changed
    private string? _lastTrackKey;
    private bool _equaliserRunning;
    private int _refreshVersion;
    private double _progressRatio;

    public static readonly DependencyProperty PillRadiusProperty = DependencyProperty.Register(
        nameof(PillRadius), typeof(double), typeof(MainWindow),
        new PropertyMetadata(IdleSize.R, (d, e) =>
            ((MainWindow)d).Pill.CornerRadius = new CornerRadius((double)e.NewValue)));

    public double PillRadius
    {
        get => (double)GetValue(PillRadiusProperty);
        set => SetValue(PillRadiusProperty, value);
    }

    public MainWindow()
    {
        InitializeComponent();

        // Centre on the primary monitor, flush with the top edge.
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;

        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            _hovering = false;
            ApplyState();
        };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _popTimer.Tick += (_, _) =>
        {
            _popTimer.Stop();
            _popping = false;
            ApplyState();
        };
        _statsTimer.Tick += async (_, _) => await UpdateStatsAsync();
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            _notice = false;
            ApplyState();
        };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _clock.Updated += (_, snapshot) => Dispatcher.InvokeAsync(() => OnClockUpdated(snapshot));
        SettingsService.Changed += ApplyClockSetting;

        Loaded += async (_, _) =>
        {
            ApplyClockSetting();

            _media.Changed += (_, _) => Dispatcher.InvokeAsync(RefreshAsync);
            await _media.InitializeAsync();
            await RefreshAsync();
            _progressTimer.Start();

            // ApplyState only reacts to changes, so show the stats if we start idle.
            if (_state == IslandState.Idle) SetStatsActive(true);
        };
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        NativeMethods.MakeOverlay(new WindowInteropHelper(this).Handle);
    }

    // ----- Media refresh -------------------------------------------------

    private async Task RefreshAsync()
    {
        // Events can overlap; only the latest refresh may update the UI.
        int version = ++_refreshVersion;
        var snapshot = await _media.GetSnapshotAsync();
        if (version != _refreshVersion) return;

        _current = snapshot;

        // Pop up only when the track changes while something was already known,
        // not on app start or when playback merely pauses/resumes.
        string? key = snapshot is null ? null : $"{snapshot.Title}|{snapshot.Artist}";
        bool trackChanged = key is not null && _lastTrackKey is not null && key != _lastTrackKey;
        _lastTrackKey = key;
        if (trackChanged && SettingsService.Current.AutoPopOnTrackChange)
        {
            _popping = true;
            _popTimer.Stop();
            _popTimer.Start();
        }

        if (snapshot is not null)
        {
            var art = snapshot.Art is null ? PlaceholderArt : Freeze(new ImageBrush(snapshot.Art) { Stretch = Stretch.UniformToFill });
            ArtSmall.Background = art;
            ArtLarge.Background = art;
            TitleSmall.Text = snapshot.Title;
            TitleLarge.Text = snapshot.Title;
            ArtistLarge.Text = snapshot.Artist;
            PlayButton.Content = snapshot.IsPlaying ? PauseGlyph : PlayGlyph;
            SetEqualiser(snapshot.IsPlaying);
        }
        else
        {
            SetEqualiser(false);
        }

        ApplyState();
        UpdateProgress();
    }

    // ----- State & animation ---------------------------------------------

    private void ApplyState()
    {
        bool hasMedia = _current is not null;
        bool hasClock = _clockSnap is not null;

        // The expanded card shows one page; it is forced when only one source exists.
        _page = !hasClock ? Page.Media : !hasMedia ? Page.Focus : _page;
        bool pageChanged = _page != _shownPage;

        var target = _notice ? IslandState.Notice
            : (hasMedia || hasClock) && (_hovering || _popping) ? IslandState.Expanded
            : hasMedia ? IslandState.Compact
            : hasClock ? IslandState.FocusCompact
            : IslandState.Idle;

        bool stateChanged = target != _state;
        var size = SizeOf(target, _page);
        bool sizeChanged = size != _size;
        UpdatePager(target, hasMedia && hasClock);
        if (!stateChanged && !pageChanged && !sizeChanged) return;

        if (stateChanged || sizeChanged)
        {
            bool growing = size.W * size.H > _size.W * _size.H;
            _size = size;
            _state = target;

            IEasingFunction ease = growing
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 }
                : new CubicEase { EasingMode = EasingMode.EaseInOut };
            var duration = TimeSpan.FromMilliseconds(growing ? 500 : 350);

            Pill.BeginAnimation(WidthProperty, new DoubleAnimation(size.W, duration) { EasingFunction = ease });
            Pill.BeginAnimation(HeightProperty, new DoubleAnimation(size.H, duration) { EasingFunction = ease });
            BeginAnimation(PillRadiusProperty, new DoubleAnimation(size.R, duration) { EasingFunction = ease });

            if (stateChanged) SetStatsActive(target == IslandState.Idle);
        }

        _shownPage = _page;
        FadeView(CompactView, target == IslandState.Compact);
        FadeView(FocusCompactView, target == IslandState.FocusCompact);
        FadeView(ExpandedView, target == IslandState.Expanded && _page == Page.Media);
        FadeView(FocusPageView, target == IslandState.Expanded && _page == Page.Focus);
        FadeView(NoticeView, target == IslandState.Notice);

        if (target == IslandState.Expanded) UpdateProgress();
    }

    // ----- Microsoft Clock (Focus sessions and timers) -------------------

    private void ApplyClockSetting()
    {
        if (SettingsService.Current.SyncMicrosoftClock)
        {
            _clock.Start();
        }
        else
        {
            _clock.Stop();
            if (_clockSnap is not null)
            {
                _clockSnap = null;
                ApplyState();
            }
        }
    }

    // Raised about once a second while a session runs (and once when it appears or ends).
    private void OnClockUpdated(ClockSnapshot? snapshot)
    {
        if (!SettingsService.Current.SyncMicrosoftClock) return; // late event after being switched off

        _clockSnap = snapshot;
        UpdateClockUi();
        ApplyState();
    }

    private void UpdateClockUi()
    {
        if (_clockSnap is not { } snap) return;

        var s = snap.Session;
        bool live = snap.Link == ClockLink.Live;
        Brush accent = s.Kind == ClockKind.Timer ? TimerBlue : s.IsBreak ? ChargingGreen : FocusOrange;
        double ratio = s.Total > TimeSpan.Zero ? Math.Clamp(1 - s.Remaining / s.Total, 0, 1) : 0;
        string countdown = FormatClock(s.Remaining);

        // Compact pill
        FocusCountdownSmall.Text = countdown;
        FocusCountdownSmall.Foreground = live ? NormalBrush : DimBrush;
        FocusRing.Stroke = live ? accent : DimBrush;
        FocusRing.StrokeDashArray = new DoubleCollection { ratio * RingDashUnits, 100 };
        FocusTitleSmall.Text = snap.Link switch
        {
            ClockLink.Minimized => "Clock minimized",
            ClockLink.PageHidden => "Open Focus in Clock",
            _ => s.Kind == ClockKind.Timer ? s.Label : s.IsBreak ? "Break" : "Focus"
        };
        BuildDots(FocusDotsSmall, s, accent, 6);

        // Expanded page: ring + chip + title + subtitle + session segments
        var ringColor = live ? accent : DimBrush;
        FocusBigRing.Stroke = ringColor;
        FocusBigRing.StrokeDashArray = new DoubleCollection { Math.Max(ratio, 0.002) * BigRingDashUnits, 100 };
        FocusGlow.Color = ((SolidColorBrush)ringColor).Color;
        FocusGlow.Opacity = live ? 0.45 : 0;

        FocusCountdown.Text = countdown;
        FocusCountdown.Foreground = live ? NormalBrush : DimBrush;

        var accentColor = ((SolidColorBrush)accent).Color;
        FocusChip.Background = new SolidColorBrush(Color.FromArgb(0x33, accentColor.R, accentColor.G, accentColor.B));
        FocusPhase.Foreground = accent;
        FocusPhase.Text = s.Kind == ClockKind.Timer ? "TIMER" : s.IsBreak ? "BREAK" : "FOCUS";

        FocusTitle.Text = s.Kind == ClockKind.Timer ? s.Label
            : s.IsBreak ? "Take a break"
            : s.Index is int i && s.Count is int c ? $"Session {i} of {c}" : s.Label;

        FocusSub.Text = snap.Link switch
        {
            ClockLink.Minimized => "Clock minimized, last known time",
            ClockLink.PageHidden => "Open Focus or Timer in Clock",
            _ when !s.Running => "Paused in Clock",
            _ when !string.IsNullOrWhiteSpace(s.UpNext) => s.UpNext!,
            _ => $"Ends at {DateTime.Now + s.Remaining:t}"
        };
        FocusSub.Foreground = live && s.Running ? DimBrush : WarnAmber;

        BuildSegments(FocusDots, s, accent);
    }

    // Wide bars instead of dots: solid = finished, dimmed accent = current, dark = still to come.
    private static void BuildSegments(Panel panel, ClockSession s, Brush accent)
    {
        panel.Children.Clear();
        if (s.Kind != ClockKind.Focus || s.Count is not int count || s.Index is not int index) return;
        if (count < 1 || count > 12) return;

        int done = s.IsBreak ? index : index - 1;
        for (int i = 0; i < count; i++)
        {
            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = count > 6 ? 12 : 22,
                Height = 5,
                RadiusX = 2.5,
                RadiusY = 2.5,
                Margin = new Thickness(i == 0 ? 0 : 5, 0, 0, 0),
                Fill = i < done || (i == done && !s.IsBreak) ? accent : TrackBrush,
                Opacity = i == done && !s.IsBreak ? 0.5 : 1
            };
            panel.Children.Add(bar);
        }
    }

    // Filled = finished sessions, outlined = the current one, dim = still to come.
    private static void BuildDots(Panel panel, ClockSession s, Brush accent, double size)
    {
        panel.Children.Clear();
        if (s.Kind != ClockKind.Focus || s.Count is not int count || s.Index is not int index) return;
        if (count < 1 || count > 12) return;

        int done = s.IsBreak ? index : index - 1;
        for (int i = 0; i < count; i++)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = size,
                Height = size,
                Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0)
            };

            if (i < done) dot.Fill = accent;
            else if (i == done && !s.IsBreak) { dot.Stroke = accent; dot.StrokeThickness = 1.5; }
            else dot.Fill = TrackBrush;

            panel.Children.Add(dot);
        }
    }

    private static string FormatClock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    private void UpdatePager(IslandState target, bool bothSources)
    {
        Pager.Visibility = bothSources && target == IslandState.Expanded ? Visibility.Visible : Visibility.Collapsed;
        PagerMediaDot.Fill = _page == Page.Media ? NormalBrush : PagerOff;
        PagerFocusDot.Fill = _page == Page.Focus ? NormalBrush : PagerOff;
    }

    private void PagerMedia_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _page = Page.Media;
        ApplyState();
        e.Handled = true;
    }

    private void PagerFocus_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _page = Page.Focus;
        ApplyState();
        e.Handled = true;
    }

    private static void FadeView(UIElement view, bool show)
    {
        view.IsHitTestVisible = show;
        var fade = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 220 : 100))
        {
            BeginTime = show ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero
        };
        view.BeginAnimation(OpacityProperty, fade);
    }

    private void SetEqualiser(bool playing)
    {
        if (playing == _equaliserRunning) return;
        _equaliserRunning = playing;

        var bars = new[] { Bar1, Bar2, Bar3, Bar4 };
        for (int i = 0; i < bars.Length; i++)
        {
            if (playing)
            {
                bars[i].BeginAnimation(HeightProperty, new DoubleAnimation(3, 14, TimeSpan.FromMilliseconds(450))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(i * 150),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
            }
            else
            {
                bars[i].BeginAnimation(HeightProperty, null);
                bars[i].Height = 4;
            }
        }
    }

    // ----- Power pop-up --------------------------------------------------

    // Raised by Windows on a background thread whenever AC/battery status changes.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;

        Dispatcher.InvokeAsync(() =>
        {
            var (percent, pluggedIn) = SystemStatsService.ReadBattery();

            // Only react to a real plug/unplug on a PC that has a battery.
            if (percent is null || pluggedIn == _lastPluggedIn) return;
            _lastPluggedIn = pluggedIn;

            ShowPowerNotice(pluggedIn, percent.Value);
        });
    }

    private void ShowPowerNotice(bool pluggedIn, int percent)
    {
        NoticeBolt.Visibility = pluggedIn ? Visibility.Visible : Visibility.Collapsed;
        NoticeBattery.Visibility = pluggedIn ? Visibility.Collapsed : Visibility.Visible;
        NoticeTitle.Text = pluggedIn ? "Charging" : "Unplugged";
        NoticeTitle.Foreground = pluggedIn ? ChargingGreen : NormalBrush;
        NoticePercent.Text = $"{percent}%";

        _notice = true;
        _noticeTimer.Stop();
        _noticeTimer.Start(); // restarts the 3 s countdown if it was already showing
        ApplyState();
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged; // static events: would otherwise keep the window alive
        SettingsService.Changed -= ApplyClockSetting;
        _clock.Stop();
        base.OnClosed(e);
    }

    // ----- PC stats ------------------------------------------------------

    private void SetStatsActive(bool active)
    {
        FadeView(StatsView, active);

        if (active)
        {
            _stats.Baseline();
            _statsTimer.Interval = TimeSpan.FromMilliseconds(600); // first reading soon, then every 2 s
            _statsTimer.Start();
        }
        else
        {
            _statsTimer.Stop();
        }
    }

    private async Task UpdateStatsAsync()
    {
        _statsTimer.Interval = TimeSpan.FromSeconds(2);
        if (_sampling) return;
        _sampling = true;

        try
        {
            // Reading the GPU counters takes a few ms; keep it off the UI thread.
            var s = await Task.Run(_stats.Sample);
            if (_state != IslandState.Idle) return; // media started while sampling

            SetValue(CpuValue, s.Cpu);
            SetValue(RamValue, s.Ram);
            SetValue(GpuValue, s.Gpu);
            GpuItem.Visibility = s.Gpu is null ? Visibility.Collapsed : Visibility.Visible;

            BatteryItem.Visibility = s.BatteryPercent is null ? Visibility.Collapsed : Visibility.Visible;
            if (s.BatteryPercent is int pct)
            {
                BoltIcon.Visibility = s.PluggedIn ? Visibility.Visible : Visibility.Collapsed;
                BatteryValue.Text = $"{pct}%";
                BatteryValue.Foreground = pct <= 20 && !s.PluggedIn ? WarnBrush : NormalBrush;
            }
        }
        finally
        {
            _sampling = false;
        }
    }

    // High load turns the value red so a glance is enough.
    private static void SetValue(TextBlock text, double? value)
    {
        if (value is null) { text.Text = "--"; text.Foreground = NormalBrush; return; }
        text.Text = $"{value:0}%";
        text.Foreground = value >= 85 ? WarnBrush : NormalBrush;
    }

    // ----- Progress ------------------------------------------------------

    private void UpdateProgress()
    {
        if (_state != IslandState.Expanded) return;

        var timeline = _media.GetTimeline();
        if (timeline is null)
        {
            _progressRatio = 0;
            CurrentTime.Text = "0:00";
            TotalTime.Text = "0:00";
        }
        else
        {
            _progressRatio = timeline.Position.TotalSeconds / timeline.Duration.TotalSeconds;
            CurrentTime.Text = FormatTime(timeline.Position);
            TotalTime.Text = FormatTime(timeline.Duration);
        }

        ProgressFill.Width = ProgressTrack.ActualWidth * _progressRatio;
    }

    private void ProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ProgressFill.Width = e.NewSize.Width * _progressRatio;

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    // ----- Input ---------------------------------------------------------

    private void Pill_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Always cancel a pending collapse; only Hover mode expands on entry.
        _leaveTimer.Stop();
        if (SettingsService.Current.ExpandTrigger != ExpandTrigger.Hover) return;

        _hovering = true;
        ApplyState();
    }

    private void Pill_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // In Click mode a click on the compact pill expands it (buttons are not
        // clickable until expanded, so this never fires for them).
        if (SettingsService.Current.ExpandTrigger != ExpandTrigger.Click) return;

        _leaveTimer.Stop();
        _hovering = true;
        ApplyState();
    }

    private void Pill_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Short delay so a quick wobble out of the pill doesn't collapse it.
        _leaveTimer.Stop();
        _leaveTimer.Start();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _media.PlayPauseAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
