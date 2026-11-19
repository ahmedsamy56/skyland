using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicIsland.Services;

namespace DynamicIsland;

// Coding agent (Claude Code) display: the Claude capsule, its bubble, the session list card, and pop-ups.
public partial class MainWindow
{
    // Own brushes (not shared with MainWindow.xaml.cs): static initialisation order across partial files is unspecified.
    private static readonly Brush AgentWorkingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)));
    private static readonly Brush AgentWaitingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)));
    private static readonly Brush AgentIdleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x5B, 0x66)));
    private static readonly Brush AgentDoneBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
    private static readonly Brush AgentRingTrack = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)));
    private static readonly Brush AgentAvatarBg = Freeze(new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1C)));
    private static readonly Brush AgentDim = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA3)));
    private static readonly Brush ClaudeCoral = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57)));
    private static readonly Brush RowDivider = Freeze(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)));

    private static readonly Color[] AvatarHues =
    [
        Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0x60, 0xA5, 0xFA), Color.FromRgb(0x34, 0xD3, 0x99),
        Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0xFB, 0x92, 0x3C)
    ];

    private readonly AgentService _agent = new();
    private IReadOnlyList<AgentSessionInfo> _agentSessions = Array.Empty<AgentSessionInfo>();

    // Which Claude views are on screen right now. They are built only while visible: their rings spin
    // forever, and hidden (zero opacity) elements still run their animations.
    private bool _claudeInCapsule, _claudeInBubble, _claudeInCard;
    private string _capsuleKey = "", _bubbleKey = "", _listKey = "";

    private const double ClaudeRowHeight = 46;
    private const int MaxClaudeRows = 5;

    // Frame rate for looping decorative animations. They are small and slow, so 20 fps looks the same as 60
    // but costs a third of the repainting.
    private const int LoopFrameRate = 20;

    // A finished turn shorter than this does not deserve a pop-up.
    private static readonly TimeSpan MinDonePopup = TimeSpan.FromSeconds(5);

    /// <summary>A grid that remembers which session it shows, so updates can reuse it.</summary>
    private sealed class AgentRow : Grid
    {
        public string SessionId = "";
        public TextBlock? Time;
    }

    private void InitAgent()
    {
        _agent.Changed += () => Dispatcher.InvokeAsync(OnAgentChanged);
        _agent.Notice += notice => Dispatcher.InvokeAsync(() => OnAgentNotice(notice));
        SettingsService.Changed += ApplyAgentSetting;
    }

    private void ApplyAgentSetting()
    {
        if (SettingsService.Current.ShowAgentStatus)
        {
            _agent.Start();
        }
        else
        {
            _agent.Stop();
            if (_agentSessions.Count > 0)
            {
                _agentSessions = Array.Empty<AgentSessionInfo>();
                ApplyState();
            }
        }
    }

    private void OnAgentChanged()
    {
        if (!SettingsService.Current.ShowAgentStatus) return;

        _agentSessions = _agent.Snapshot();
        ApplyState();
    }

    private void OnAgentNotice(AgentNotice notice)
    {
        if (!SettingsService.Current.ShowAgentStatus) return;

        if (notice.Kind == AgentNoticeKind.NeedsInput)
        {
            ShowNotice($"{notice.Project} needs you", notice.Detail, AgentWaitingBrush, NoticeIcon.Dot);
        }
        else if (notice.Duration is { } took && took >= MinDonePopup)
        {
            ShowNotice($"Done · {FormatClock(took)}", notice.Project, AgentDoneBrush, NoticeIcon.Dot);
        }
    }

    // ----- What the capsule says -----------------------------------------

    private sealed record ClaudeSummary(AgentSessionInfo Head, List<AgentSessionInfo> Shown, int Active, int Waiting)
    {
        public bool Multi => Shown.Count > 1;
    }

    private static int StateRank(AgentState state) => state switch
    {
        AgentState.Waiting => 0,
        AgentState.Working => 1,
        _ => 2
    };

    // The sessions that count are the active ones; when none are active, the open (idle) ones.
    // The headline is the one that needs you, otherwise the one that has been working longest.
    private ClaudeSummary? Summarize()
    {
        if (_agentSessions.Count == 0) return null;

        var active = _agentSessions.Where(s => s.State != AgentState.Idle).ToList();
        var shown = active.Count > 0 ? active : _agentSessions.ToList();
        var head = shown
            .OrderBy(s => StateRank(s.State))
            .ThenBy(s => s.TurnStart ?? DateTime.MaxValue)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .First();
        return new ClaudeSummary(head, shown, active.Count, shown.Count(s => s.State == AgentState.Waiting));
    }

    private double ClaudeCapsuleWidth()
    {
        var v = Summarize();
        if (v is null) return 252;
        return v.Multi ? 262 : v.Head.State == AgentState.Waiting ? 276 : 252;
    }

    private int ClaudeRowCount() => Math.Max(1, Math.Min(_agentSessions.Count, MaxClaudeRows));

    private static Brush StateBrush(AgentState state) => state switch
    {
        AgentState.Waiting => AgentWaitingBrush,
        AgentState.Working => AgentWorkingBrush,
        _ => AgentIdleBrush
    };

    // "Needs permission: Bash" -> "Bash"
    private static string ToolOf(AgentSessionInfo s)
    {
        const string prefix = "Needs permission: ";
        return s.Action.StartsWith(prefix, StringComparison.Ordinal) ? s.Action[prefix.Length..] : "needs you";
    }

    // Called from ApplyState: builds only the Claude views that are on screen, and clears the others.
    private void RefreshClaude()
    {
        var v = Summarize();

        if (_claudeInCapsule && v is not null) UpdateClaudeCapsule(v);
        else if (!_claudeInCapsule && _capsuleKey.Length > 0) { ClaudeLead.Children.Clear(); _capsuleKey = ""; }

        if (_claudeInBubble && v is not null) UpdateClaudeBubble(v);
        else if (!_claudeInBubble && _bubbleKey.Length > 0) { BubbleClaudeHost.Children.Clear(); _bubbleKey = ""; }

        if (_claudeInCard && v is not null) UpdateClaudeCard(v);
        else if (!_claudeInCard && _listKey.Length > 0) { ClaudeList.Children.Clear(); _listKey = ""; }
    }

    // ----- Capsule -------------------------------------------------------

    private void UpdateClaudeCapsule(ClaudeSummary v)
    {
        string key = $"{v.Multi}|{v.Head.Id}|{v.Head.State}|{v.Shown.Count}|{v.Waiting}|{v.Active}|" +
                     (v.Multi ? string.Join(",", v.Shown.Take(3).Select(s => s.Id + s.State)) : "");

        if (key != _capsuleKey)
        {
            _capsuleKey = key;
            ClaudeLead.Children.Clear();

            if (v.Multi)
            {
                // Several sessions: a stack of round avatars, "N sessions", and who needs you.
                var stack = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var s in v.Shown.Take(3))
                {
                    var avatar = BuildAvatar(s, 24);
                    avatar.Margin = new Thickness(stack.Children.Count == 0 ? 0 : -8, 0, 0, 0);
                    stack.Children.Add(avatar);
                }
                ClaudeLead.Children.Add(stack);

                ClaudeName.Text = $"{v.Shown.Count} sessions";
                ClaudeSub.Visibility = Visibility.Collapsed;
                (ClaudeTrail.Text, ClaudeTrail.Foreground) =
                    v.Waiting > 0 ? ($"{v.Waiting} needs you", AgentWaitingBrush)
                    : v.Active > 0 ? ("all working", AgentWorkingBrush)
                    : ("idle", AgentDim);
            }
            else
            {
                ClaudeLead.Children.Add(BuildStatusMark(v.Head.State, 28, 2.5));
                ClaudeName.Text = v.Head.Project;
                ClaudeSub.Text = v.Head.State == AgentState.Waiting ? "needs your permission" : "idle";
                ClaudeSub.Visibility = v.Head.State == AgentState.Working ? Visibility.Collapsed : Visibility.Visible;
                ClaudeTrail.Foreground = v.Head.State == AgentState.Waiting ? AgentWaitingBrush : AgentWorkingBrush;
            }
        }

        if (!v.Multi)
        {
            ClaudeTrail.Text = v.Head.State switch
            {
                AgentState.Waiting => ToolOf(v.Head),
                AgentState.Working => Elapsed(v.Head),
                _ => ""
            };
        }
    }

    // ----- Bubble --------------------------------------------------------

    private void UpdateClaudeBubble(ClaudeSummary v)
    {
        string key = $"{v.Head.Id}|{v.Head.State}|{v.Shown.Count}";
        if (key == _bubbleKey) return;
        _bubbleKey = key;

        BubbleClaudeHost.Children.Clear();
        BubbleClaudeHost.Children.Add(BuildStatusMark(v.Head.State, 40, 3));

        // How many sessions, in the colour of the most urgent one.
        if (v.Shown.Count > 1)
        {
            BubbleClaudeHost.Children.Add(new Border
            {
                MinWidth = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = StateBrush(v.Head.State),
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -3, -4, 0),
                Child = new TextBlock
                {
                    Text = v.Shown.Count.ToString(),
                    Foreground = Brushes.Black,
                    FontSize = 9.5,
                    FontWeight = FontWeights.ExtraBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, 0, 3, 0)
                }
            });
        }
    }

    // ----- Card ----------------------------------------------------------

    // Waiting first, then working, then idle; the ones that need you are never cut off the list.
    private List<AgentSessionInfo> CardSessions() => _agentSessions
        .OrderBy(s => StateRank(s.State))
        .ThenBy(s => s.TurnStart ?? DateTime.MaxValue)
        .ThenBy(s => s.Project, StringComparer.OrdinalIgnoreCase)
        .ThenBy(s => s.Id, StringComparer.Ordinal)
        .Take(MaxClaudeRows)
        .ToList();

    private void UpdateClaudeCard(ClaudeSummary v)
    {
        ClaudeCountText.Text = _agentSessions.Count == 1 ? "1 session" : $"{_agentSessions.Count} sessions";
        ClaudeCountText.Foreground = StateBrush(v.Head.State);

        var list = CardSessions();
        string key = string.Join("|", list.Select(s => $"{s.Id}:{s.State}:{s.Action}"));
        if (key != _listKey)
        {
            _listKey = key;
            ClaudeList.Children.Clear();
            for (int i = 0; i < list.Count; i++) ClaudeList.Children.Add(BuildCardRow(list[i], i > 0));
        }
        else
        {
            UpdateAgentTimes();
        }
    }

    // Row: ring avatar, project and what it is doing, elapsed time.
    private static AgentRow BuildCardRow(AgentSessionInfo s, bool divider)
    {
        var row = new AgentRow { SessionId = s.Id, Height = ClaudeRowHeight };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (divider)
        {
            row.Children.Add(new Border
            {
                Height = 1,
                Background = RowDivider,
                VerticalAlignment = VerticalAlignment.Top
            });
            Grid.SetColumnSpan((Border)row.Children[0], 3);
        }

        var avatar = BuildAvatar(s, 30);
        avatar.VerticalAlignment = VerticalAlignment.Center;

        var waiting = s.State == AgentState.Waiting;
        var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 8, 0) };
        meta.Children.Add(new TextBlock
        {
            Text = s.Project,
            Foreground = NormalBrush,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        meta.Children.Add(new TextBlock
        {
            Text = s.Action,
            Foreground = waiting ? AgentWaitingBrush : AgentDim,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(meta, 1);

        row.Time = TimeText(Elapsed(s), 12.5, new Thickness(0));
        row.Time.FontWeight = FontWeights.SemiBold;
        row.Time.Foreground = StateBrush(s.State) == AgentIdleBrush ? AgentDim : StateBrush(s.State);
        Grid.SetColumn(row.Time, 2);

        row.Children.Add(avatar);
        row.Children.Add(meta);
        row.Children.Add(row.Time);
        return row;
    }

    // ----- Rings and avatars ---------------------------------------------

    // Status ring: spinning arc = working, breathing full ring = needs you, empty track = idle.
    private static Grid BuildRing(AgentState state, double size, double thickness)
    {
        var accent = StateBrush(state);
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new Ellipse { Stroke = AgentRingTrack, StrokeThickness = thickness });

        if (state == AgentState.Idle) return grid;

        var arc = new Ellipse
        {
            Stroke = accent,
            StrokeThickness = thickness,
            StrokeDashCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        var rotate = new RotateTransform(-90);
        arc.RenderTransform = rotate;

        if (state == AgentState.Working)
        {
            double units = Math.PI * (size - thickness) / thickness; // circumference in stroke widths
            arc.StrokeDashArray = new DoubleCollection { units * 0.28, units };
            var spin = new DoubleAnimation(-90, 270, TimeSpan.FromMilliseconds(1600)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(spin, LoopFrameRate);
            rotate.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else
        {
            var breathe = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(600))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(breathe, LoopFrameRate);
            arc.BeginAnimation(OpacityProperty, breathe);
        }
        grid.Children.Add(arc);
        return grid;
    }

    // The Claude symbol inside a status ring.
    private static FrameworkElement BuildStatusMark(AgentState state, double size, double thickness)
    {
        var grid = BuildRing(state, size, thickness);
        double symbol = size * 0.56;
        grid.Children.Add(new Path
        {
            Data = (Geometry)Application.Current.FindResource("ClaudeSymbol"),
            Fill = ClaudeCoral,
            Stretch = Stretch.Uniform,
            Width = symbol,
            Height = symbol,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    // Round avatar (first letter of the project) inside a status ring.
    private static FrameworkElement BuildAvatar(AgentSessionInfo s, double size)
    {
        double thickness = size >= 28 ? 3 : 2.5;
        var grid = BuildRing(s.State, size, thickness);

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

    // ----- Live times ----------------------------------------------------

    // Called every 500 ms: keeps the visible durations counting without rebuilding anything.
    private void UpdateAgentTimes()
    {
        if (_agentSessions.Count == 0) return;

        if (_claudeInCapsule)
        {
            var v = Summarize();
            if (v is not null && !v.Multi && v.Head.State == AgentState.Working)
                ClaudeTrail.Text = Elapsed(v.Head);
        }

        if (_claudeInCard)
        {
            foreach (var row in ClaudeList.Children.OfType<AgentRow>())
            {
                var session = _agentSessions.FirstOrDefault(c => c.Id == row.SessionId);
                if (session is not null && row.Time is not null) row.Time.Text = Elapsed(session);
            }
        }
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
}
