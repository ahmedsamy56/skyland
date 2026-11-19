using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace DynamicIsland.Services;

public enum ClockKind { Focus, Timer }

/// <summary>How fresh the values are.</summary>
public enum ClockLink
{
    /// <summary>Read just now from the visible Clock window.</summary>
    Live,
    /// <summary>Clock is minimized: Windows hides its content, so the last values are shown frozen.</summary>
    Minimized,
    /// <summary>Clock is open on a page that shows no session (Alarm, Stopwatch...), so values are frozen.</summary>
    PageHidden
}

/// <param name="Label">Phase title for Focus ("Focus period"), timer name for Timer.</param>
/// <param name="Index">Current session number (Focus only).</param>
/// <param name="Count">Planned sessions (Focus only).</param>
/// <param name="UpNext">Text such as "Up next: 5 min break" (Focus only).</param>
public sealed record ClockSession(
    ClockKind Kind, bool IsBreak, string Label,
    TimeSpan Remaining, TimeSpan Total, bool Running,
    int? Index, int? Count, string? UpNext);

public sealed record ClockSnapshot(ClockSession Session, ClockLink Link);

/// <summary>
/// Mirrors the Windows Clock app's Focus sessions and Timers, read-only, through UI Automation.
/// There is no public API for Clock, so this reads the on-screen text: it needs the Clock window
/// to be open (not minimized) on the Focus or Timer page, and it only understands the English UI.
/// Only the countdown, phase and play/pause state are read; never the task list.
/// </summary>
public sealed class ClockSyncService
{
    private sealed class PollState
    {
        public ClockSnapshot? Last;
        public nint Frame;
    }

    private CancellationTokenSource? _cts;

    /// <summary>Raised on a background thread when the snapshot changes. Null means no session.</summary>
    public event EventHandler<ClockSnapshot?>? Updated;

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var state = new PollState();
        ClockSnapshot? published = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ClockSnapshot? snapshot;
                try { snapshot = Poll(state); }
                catch { snapshot = null; } // Clock closed or rebuilt its UI mid-read

                if (!Equals(snapshot, published))
                {
                    published = snapshot;
                    Updated?.Invoke(this, snapshot);
                }

                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    // ----- Polling -------------------------------------------------------

    private static ClockSnapshot? Poll(PollState state)
    {
        // Cheap gate: do nothing at all unless Clock is running.
        var processes = Process.GetProcessesByName("Time");
        bool running = processes.Length > 0;
        foreach (var p in processes) p.Dispose();
        if (!running)
        {
            state.Frame = 0;
            return state.Last = null;
        }

        if (state.Frame == 0 || !IsWindow(state.Frame))
            state.Frame = FindClockFrame();

        if (state.Frame == 0)
            return state.Last = null; // process alive but no window (closed to background)

        if (IsIconic(state.Frame))
            return state.Last is null ? null : state.Last with { Link = ClockLink.Minimized };

        var root = AutomationElement.FromHandle(state.Frame);

        ClockSession? session;
        if (IsSelected(root, "FocusButton")) session = ReadFocus(root);
        else if (IsSelected(root, "TimerButton")) session = ReadTimer(root);
        else return state.Last is null ? null : state.Last with { Link = ClockLink.PageHidden };

        return state.Last = session is null ? null : new ClockSnapshot(session, ClockLink.Live);
    }

    // The Clock window is an ApplicationFrameWindow; recognise it by its navigation items.
    private static nint FindClockFrame()
    {
        var frames = AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ClassNameProperty, "ApplicationFrameWindow"));

        foreach (AutomationElement frame in frames)
        {
            if (Find(frame, "FocusButton") is not null)
                return frame.Current.NativeWindowHandle;
        }
        return 0;
    }

    private static ClockSession? ReadFocus(AutomationElement root)
    {
        var tick = Find(root, "TickTimer");
        if (tick is null) return null; // setup screen: no session running

        // "14 minutes 49 seconds, Remaining Of, 15 minutes 0 seconds"
        var parts = tick.Current.Name.Split(", ");
        if (parts.Length < 2) return null;
        var remaining = ParseSpan(parts[0]);
        var total = ParseSpan(parts[^1]);
        if (remaining is null || total is null || total.Value <= TimeSpan.Zero) return null;

        // "Focus period (1 of 3)"
        string title = Find(root, "TitleTextBlock")?.Current.Name ?? "";
        int? index = null, count = null;
        var m = Regex.Match(title, @"\((\d+)\D+(\d+)\)");
        if (m.Success)
        {
            index = int.Parse(m.Groups[1].Value);
            count = int.Parse(m.Groups[2].Value);
        }

        bool isBreak = title.Contains("break", StringComparison.OrdinalIgnoreCase);
        string label = Regex.Replace(title, @"\s*\(.*?\)", "").Trim();
        if (label.Length == 0) label = isBreak ? "Break" : "Focus";

        // The button offers "Pause focus session" while running.
        bool running = Find(root, "FocusSessionPlayPauseButton")?.Current.Name
            .StartsWith("Pause", StringComparison.OrdinalIgnoreCase) ?? false;

        string? upNext = Find(root, "UpcomingBlock")?.Current.Name;

        return new ClockSession(ClockKind.Focus, isBreak, label, remaining.Value, total.Value, running, index, count, upNext);
    }

    private static ClockSession? ReadTimer(AutomationElement root)
    {
        var grids = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "TimerViewGrid"));

        ClockSession? best = null;
        foreach (AutomationElement grid in grids)
        {
            // Running timers offer "Pause"; idle ones offer "Start".
            string button = Find(grid, "TimerPlayPauseButton")?.Current.Name ?? "";
            if (!button.Contains("Pause", StringComparison.OrdinalIgnoreCase)) continue;

            var remaining = ParseSpan(Find(grid, "TimerValueText")?.Current.Name ?? "");
            if (remaining is null) continue;

            // Grid name: "Edit timer, 1 min, Running, 59 seconds, Remaining Of, 1 minute 0 seconds, 10:27 AM"
            var totalMatch = Regex.Match(grid.Current.Name, @"Remaining Of,\s*([^,]+)", RegexOptions.IgnoreCase);
            var total = totalMatch.Success ? ParseSpan(totalMatch.Groups[1].Value) : null;
            if (total is null || total.Value < remaining.Value) total = remaining;

            string label = Find(grid, "TimerNameText")?.Current.Name ?? "";
            if (label.Length == 0) label = "Timer";

            var session = new ClockSession(ClockKind.Timer, false, label, remaining.Value, total.Value, true, null, null, null);

            // Several timers can run; show the one that finishes first.
            if (best is null || session.Remaining < best.Remaining) best = session;
        }
        return best;
    }

    // ----- Helpers -------------------------------------------------------

    private static AutomationElement? Find(AutomationElement scope, string automationId) =>
        scope.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    private static bool IsSelected(AutomationElement root, string automationId)
    {
        var item = Find(root, automationId);
        return item is not null
            && item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
            && ((SelectionItemPattern)pattern).Current.IsSelected;
    }

    // "1 hour 2 minutes 3 seconds", "14 minutes 49 seconds", "55 seconds"
    private static TimeSpan? ParseSpan(string text)
    {
        var matches = Regex.Matches(text, @"(\d+)\s*(hour|minute|second)", RegexOptions.IgnoreCase);
        if (matches.Count == 0) return null;

        double seconds = 0;
        foreach (Match match in matches)
        {
            int n = int.Parse(match.Groups[1].Value);
            seconds += char.ToLowerInvariant(match.Groups[2].Value[0]) switch
            {
                'h' => n * 3600,
                'm' => n * 60,
                _ => n
            };
        }
        return TimeSpan.FromSeconds(seconds);
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
}
