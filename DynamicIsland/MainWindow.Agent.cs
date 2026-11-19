using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

// Coding agent (Claude Code) display: collapsed pill, one-session-at-a-time card, pop-ups.
public partial class MainWindow
{
    // Own brushes (not shared with MainWindow.xaml.cs): static initialisation order across partial files is unspecified.
    private static readonly Brush AgentWorkingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)));
    private static readonly Brush AgentWaitingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)));
    private static readonly Brush AgentIdleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x5B, 0x66)));
    private static readonly Brush AgentDoneBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
    private static readonly Brush AgentRingTrack = Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2C)));
    private static readonly Brush AgentAvatarBg = Freeze(new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1C)));
    private static readonly Brush AgentDim = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA3)));
    private static readonly Brush AgentPipOff = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)));

    private static readonly Color[] AvatarHues =
    [
        Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0x60, 0xA5, 0xFA), Color.FromRgb(0x34, 0xD3, 0x99),
        Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0xFB, 0x92, 0x3C)
    ];

    private readonly AgentService _agent = new();
    private IReadOnlyList<AgentSessionInfo> _agentSessions = Array.Empty<AgentSessionInfo>();
    private readonly DispatcherTimer _agentSwitchTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private int _agentCarousel;
    private AgentState? _orbState;
    private string _dotsKey = "";

    // A finished turn shorter than this does not deserve a pop-up.
    private static readonly TimeSpan MinDonePopup = TimeSpan.FromSeconds(5);

    /// <summary>A grid that remembers which session it shows, so updates can reuse it.</summary>
    private sealed class AgentRow : Grid
    {
        public string Key = "";
        public string SessionId = "";
        public TextBlock? Time;
    }

    private void InitAgent()
    {
        _agent.Changed += () => Dispatcher.InvokeAsync(OnAgentChanged);
        _agent.Notice += notice => Dispatcher.InvokeAsync(() => OnAgentNotice(notice));
        _agentSwitchTimer.Tick += (_, _) => AgentSwitchTick();
        SettingsService.Changed += ApplyAgentSetting;
    }

    private void ApplyAgentSetting()
    {
        if (SettingsService.Current.ShowAgentStatus)
        {
            _agent.Start();
            _agentSwitchTimer.Start();
        }
        else
        {
            _agent.Stop();
            _agentSwitchTimer.Stop();
            if (_agentSessions.Count > 0)
            {
                _agentSessions = Array.Empty<AgentSessionInfo>();
                AgentCompactSlot.Children.Clear();
                AgentCardSlot.Children.Clear();
                ApplyState();
            }
        }
    }

    private void OnAgentChanged()
    {
        if (!SettingsService.Current.ShowAgentStatus) return;

        _agentSessions = _agent.Snapshot();
        UpdateAgentCompact(false);
        UpdateAgentCard(false);
        ApplyState();
    }

    private void OnAgentNotice(AgentNotice notice)
    {
        if (!SettingsService.Current.ShowAgentStatus) return;

        if (notice.Kind == AgentNoticeKind.NeedsInput)
        {
            ShowNotice($"{notice.Project} needs you", notice.Detail, AgentWaitingBrush, NoticeIcon.Dot, wide: true);
        }
        else if (notice.Duration is { } took && took >= MinDonePopup)
        {
            ShowNotice($"Done · {FormatClock(took)}", notice.Project, AgentDoneBrush, NoticeIcon.Dot, wide: true);
        }
    }

    // ----- Which sessions are shown --------------------------------------

    // Sessions that need you win: they are shown instead of the rest, so you cannot miss them.
    // Otherwise the collapsed pill cycles through working sessions, and the card through all of them.
    private List<AgentSessionInfo> Candidates(bool collapsed)
    {
        var waiting = _agentSessions
            .Where(s => s.State == AgentState.Waiting)
            .OrderBy(s => s.TurnStart ?? DateTime.MaxValue)
            .ThenBy(s => s.Id)
            .ToList();
        if (waiting.Count > 0) return waiting;

        var working = _agentSessions
            .Where(s => s.State == AgentState.Working)
            .OrderBy(s => s.TurnStart ?? DateTime.MaxValue)
            .ThenBy(s => s.Id);

        if (collapsed) return working.ToList();

        var idle = _agentSessions
            .Where(s => s.State == AgentState.Idle)
            .OrderBy(s => s.Project)
            .ThenBy(s => s.Id);

        return working.Concat(idle).Take(4).ToList();
    }

    // Card: 8 px padding, 44 px row, 12 px dots under it (more than one session), plus room for the page dots.
    private double AgentCardHeight() =>
        (Candidates(false).Count > 1 ? 68 : 60) + (_multiPage ? 10 : 0);

    private void AgentSwitchTick()
    {
        if (_state == IslandState.AgentCompact && Candidates(true).Count > 1)
        {
            _agentCarousel++;
            UpdateAgentCompact(true);
        }
        else if (_state == IslandState.Expanded && _page == Page.Agent && Candidates(false).Count > 1)
        {
            _agentCarousel++;
            UpdateAgentCard(true);
        }
    }

    // ----- Collapsed pill ------------------------------------------------

    private void UpdateAgentCompact(bool animate)
    {
        var cands = Candidates(true);
        if (cands.Count == 0)
        {
            AgentCompactSlot.Children.Clear();
            AgentPips.Children.Clear();
            AgentBadge.Visibility = Visibility.Collapsed;
            return;
        }

        int idx = _agentCarousel % cands.Count;
        var s = cands[idx];

        SetAgentOrb(s.State);

        int active = _agentSessions.Count(x => x.State != AgentState.Idle);
        AgentBadge.Visibility = active > 1 ? Visibility.Visible : Visibility.Collapsed;
        AgentBadgeText.Text = active.ToString();

        AgentPips.Children.Clear();
        if (cands.Count > 1)
        {
            for (int i = 0; i < cands.Count; i++)
            {
                AgentPips.Children.Add(new Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0),
                    Fill = i == idx ? NormalBrush : AgentPipOff
                });
            }
        }

        string key = $"{s.Id}|{s.State}";
        var current = AgentCompactSlot.Children.OfType<AgentRow>().LastOrDefault();
        if (current is not null && current.Key == key)
        {
            if (current.Time is not null) current.Time.Text = Elapsed(s);
            return;
        }

        bool slide = current is not null && (animate || current.SessionId != s.Id);
        SlideSwap(AgentCompactSlot, BuildCompactRow(s, key), slide, 36);
    }

    private static AgentRow BuildCompactRow(AgentSessionInfo s, string key)
    {
        var row = new AgentRow { Key = key, SessionId = s.Id, Height = 36 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = s.Project,
            Foreground = NormalBrush,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        row.Time = TimeText(Elapsed(s), 12, Thickness_Left(8));
        Grid.SetColumn(row.Time, 1);

        row.Children.Add(name);
        row.Children.Add(row.Time);
        return row;
    }

    private void SetAgentOrb(AgentState state)
    {
        var brush = state switch
        {
            AgentState.Waiting => AgentWaitingBrush,
            AgentState.Working => AgentWorkingBrush,
            _ => AgentIdleBrush
        };
        AgentOrbCore.Fill = brush;
        AgentOrbPing.Stroke = brush;

        if (_orbState == state) return;
        _orbState = state;

        // A soft ping ring: slower while working, urgent while it needs you.
        AgentOrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AgentOrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AgentOrbPing.BeginAnimation(OpacityProperty, null);

        if (state == AgentState.Idle)
        {
            AgentOrbPing.Opacity = 0;
            return;
        }

        var period = TimeSpan.FromMilliseconds(state == AgentState.Waiting ? 900 : 1400);
        var scale = new DoubleAnimation(0.5, 1.6, period) { RepeatBehavior = RepeatBehavior.Forever };
        var fade = new DoubleAnimation(0.9, 0, period) { RepeatBehavior = RepeatBehavior.Forever };
        AgentOrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        AgentOrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        AgentOrbPing.BeginAnimation(OpacityProperty, fade);
    }

    // ----- Expanded card -------------------------------------------------

    private void UpdateAgentCard(bool animate)
    {
        AgentCardContent.Margin = new Thickness(14, _multiPage ? 16 : 8, 14, 0);

        var cands = Candidates(false);
        if (cands.Count == 0)
        {
            AgentCardSlot.Children.Clear();
            AgentDots.Children.Clear();
            _dotsKey = "";
            return;
        }

        int idx = _agentCarousel % cands.Count;
        var s = cands[idx];

        UpdateAgentDots(cands.Count, idx);

        string key = $"{s.Id}|{s.State}|{s.Action}";
        var current = AgentCardSlot.Children.OfType<AgentRow>().LastOrDefault();
        if (current is not null && current.Key == key)
        {
            if (current.Time is not null) current.Time.Text = Elapsed(s);
            return;
        }

        bool slide = current is not null && (animate || current.SessionId != s.Id);
        SlideSwap(AgentCardSlot, BuildCardRow(s, key), slide, 44);
    }

    // Row: ring avatar, project and what it is doing, elapsed time.
    private static AgentRow BuildCardRow(AgentSessionInfo s, string key)
    {
        var row = new AgentRow { Key = key, SessionId = s.Id, Height = 44 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = BuildAvatar(s, 28);
        avatar.VerticalAlignment = VerticalAlignment.Center;

        var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
        meta.Children.Add(new TextBlock
        {
            Text = s.Project,
            Foreground = NormalBrush,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        meta.Children.Add(new TextBlock
        {
            Text = s.Action,
            Foreground = s.State == AgentState.Waiting ? AgentWaitingBrush : AgentDim,
            FontSize = 11,
            FontWeight = s.State == AgentState.Waiting ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(meta, 1);

        row.Time = TimeText(Elapsed(s), 12, new Thickness(0));
        row.Time.FontWeight = FontWeights.SemiBold;
        row.Time.Foreground = s.State == AgentState.Waiting ? AgentWaitingBrush : AgentDim;
        Grid.SetColumn(row.Time, 2);

        row.Children.Add(avatar);
        row.Children.Add(meta);
        row.Children.Add(row.Time);
        return row;
    }

    // Round avatar (first letter of the project) inside a status ring:
    // spinning arc = working, breathing full ring = needs you, empty ring = idle.
    private static FrameworkElement BuildAvatar(AgentSessionInfo s, double size)
    {
        const double thickness = 3;
        var accent = s.State switch
        {
            AgentState.Waiting => AgentWaitingBrush,
            AgentState.Working => AgentWorkingBrush,
            _ => AgentIdleBrush
        };

        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new Ellipse { Stroke = AgentRingTrack, StrokeThickness = thickness });

        if (s.State != AgentState.Idle)
        {
            var arc = new Ellipse
            {
                Stroke = accent,
                StrokeThickness = thickness,
                StrokeDashCap = PenLineCap.Round,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var rotate = new RotateTransform(-90);
            arc.RenderTransform = rotate;

            if (s.State == AgentState.Working)
            {
                double units = Math.PI * (size - thickness) / thickness; // circumference in stroke widths
                arc.StrokeDashArray = new DoubleCollection { units * 0.28, units };
                rotate.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(-90, 270, TimeSpan.FromMilliseconds(1600)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else
            {
                arc.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(600))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    });
            }
            grid.Children.Add(arc);
        }

        string letter = s.Project.Length > 0 ? char.ToUpperInvariant(s.Project[0]).ToString() : "?";
        grid.Children.Add(new Border
        {
            Width = size * 0.74,
            Height = size * 0.74,
            CornerRadius = new CornerRadius(size),
            Background = AgentAvatarBg,
            Child = new TextBlock
            {
                Text = letter,
                Foreground = new SolidColorBrush(HueFor(s.Project)),
                FontSize = size * 0.4,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        return grid;
    }

    private static Color HueFor(string project) =>
        AvatarHues[project.Sum(c => c) % AvatarHues.Length];

    // Row of dots under the session, one per session; click one to jump to it.
    private void UpdateAgentDots(int count, int selected)
    {
        string key = $"{count}|{selected}";
        if (key == _dotsKey) return;
        _dotsKey = key;

        AgentDots.Children.Clear();
        if (count < 2) return;

        for (int i = 0; i < count; i++)
        {
            int index = i;
            var hit = new Border
            {
                Width = 14,
                Height = 12,
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = new Ellipse
                {
                    Width = i == selected ? 5.5 : 4,
                    Height = i == selected ? 5.5 : 4,
                    Fill = i == selected ? NormalBrush : AgentPipOff
                }
            };
            hit.MouseLeftButtonUp += (_, e) =>
            {
                _agentCarousel = index;
                _agentSwitchTimer.Stop(); // a deliberate pick restarts the 3 s countdown
                _agentSwitchTimer.Start();
                UpdateAgentCard(true);
                e.Handled = true;
            };
            AgentDots.Children.Add(hit);
        }
    }

    // ----- Shared helpers ------------------------------------------------

    // New row slides in from below while the old one slides up and fades out.
    private static void SlideSwap(Grid slot, AgentRow next, bool slide, double height)
    {
        var rows = slot.Children.OfType<AgentRow>().ToList();
        if (!slide || rows.Count == 0)
        {
            slot.Children.Clear();
            slot.Children.Add(next);
            return;
        }

        var current = rows[^1];
        foreach (var stale in rows.Take(rows.Count - 1)) slot.Children.Remove(stale);

        var duration = TimeSpan.FromMilliseconds(450);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var incoming = new TranslateTransform(0, height);
        next.RenderTransform = incoming;
        next.Opacity = 0;
        slot.Children.Add(next);
        incoming.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(height, 0, duration) { EasingFunction = ease });
        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));

        var outgoing = new TranslateTransform();
        current.RenderTransform = outgoing;
        var slideOut = new DoubleAnimation(0, -height, duration) { EasingFunction = ease };
        slideOut.Completed += (_, _) => slot.Children.Remove(current);
        outgoing.BeginAnimation(TranslateTransform.YProperty, slideOut);
        current.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration));
    }

    // Called every 500 ms: keeps the visible duration counting without rebuilding anything.
    private void UpdateAgentTimes()
    {
        if (_agentSessions.Count == 0) return;

        if (_state == IslandState.AgentCompact)
            RefreshTime(AgentCompactSlot, Candidates(true));
        else if (_state == IslandState.Expanded && _page == Page.Agent)
            RefreshTime(AgentCardSlot, Candidates(false));
    }

    private static void RefreshTime(Grid slot, List<AgentSessionInfo> candidates)
    {
        var row = slot.Children.OfType<AgentRow>().LastOrDefault();
        if (row?.Time is null) return;

        var session = candidates.FirstOrDefault(c => c.Id == row.SessionId);
        if (session is not null) row.Time.Text = Elapsed(session);
    }

    private static string Elapsed(AgentSessionInfo s) =>
        s.State != AgentState.Idle && s.TurnStart is { } start
            ? FormatClock(DateTime.UtcNow - start < TimeSpan.Zero ? TimeSpan.Zero : DateTime.UtcNow - start)
            : "";

    private static TextBlock TimeText(string text, double size, Thickness margin)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = AgentDim,
            FontSize = size,
            Margin = margin,
            VerticalAlignment = VerticalAlignment.Center
        };
        Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        return tb;
    }

    private static Thickness Thickness_Left(double left) => new(left, 0, 0, 0);
}
