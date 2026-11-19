using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

public enum AgentState { Idle, Working, Waiting }

/// <summary>Immutable copy of a session, safe to read from the UI thread.</summary>
public sealed record AgentSessionInfo(string Id, string Project, AgentState State, string Action, DateTime? TurnStart);

public enum AgentNoticeKind { NeedsInput, Finished }

/// <param name="Detail">NeedsInput: what it wants ("Permission: Bash"). Finished: unused.</param>
/// <param name="Duration">Finished only: how long the turn took.</param>
public sealed record AgentNotice(AgentNoticeKind Kind, string Project, string Detail, TimeSpan? Duration);

/// <summary>
/// Tracks Claude Code sessions. Claude Code hooks run island-hook.exe, which sends one JSON line per
/// event over the named pipe below (current user only, nothing on the network). Read-only: the island
/// shows what the agent is doing and never answers it.
/// Events may be raised on a background thread.
/// </summary>
public sealed class AgentService
{
    public const string PipeName = "DynamicIsland.Agent";

    /// <summary>When the last hook event arrived (null: none since the island started). Shown in Settings to help diagnose.</summary>
    public static DateTime? LastEventUtc { get; private set; }

    // An interrupted turn (Esc) sends no Stop event, so a silent "working" session is eventually
    // treated as idle, and finished sessions are forgotten after a while.
    private static readonly TimeSpan StaleWorking = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StaleIdle = TimeSpan.FromHours(1);

    private sealed class Session
    {
        public string Id = "";
        public string Project = "";
        public AgentState State = AgentState.Idle;
        public string Action = "Idle";
        public DateTime? TurnStart;
        public DateTime LastEvent = DateTime.UtcNow;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private CancellationTokenSource? _cts;

    /// <summary>Any session was added, changed or removed.</summary>
    public event Action? Changed;

    /// <summary>A session needs the user, or finished a turn.</summary>
    public event Action<AgentNotice>? Notice;

    public IReadOnlyList<AgentSessionInfo> Snapshot()
    {
        lock (_lock)
        {
            return _sessions.Values
                .Select(s => new AgentSessionInfo(s.Id, s.Project, s.State, s.Action, s.TurnStart))
                .ToList();
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(() => ServeAsync(cts.Token));
        _ = Task.Run(() => PruneLoopAsync(cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        lock (_lock) _sessions.Clear();
    }

    // ----- Pipe server ---------------------------------------------------

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(ct);

                // Hand the connection to its own task so the next hook can connect immediately.
                var connected = server;
                server = null;
                _ = Task.Run(() => ReadClientAsync(connected, ct), ct);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch
            {
                server?.Dispose();
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ReadClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using (server)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));

                using var reader = new StreamReader(server);
                string? line = await reader.ReadLineAsync(timeout.Token);
                if (!string.IsNullOrWhiteSpace(line)) Apply(line);
            }
            catch
            {
                // A malformed or abandoned message is dropped; never crash the island.
            }
        }
    }

    // ----- Event handling ------------------------------------------------

    private void Apply(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject msg) return;

        string ev = Str(msg, "event");
        string id = Str(msg, "session");
        if (id.Length == 0) return;

        string cwd = Str(msg, "cwd");
        string project = Str(msg, "project");
        string tool = Str(msg, "tool");
        string detail = Str(msg, "detail");
        string message = Str(msg, "message");
        string type = Str(msg, "type");
        var now = DateTime.UtcNow;

        AgentNotice? notice = null;

        lock (_lock)
        {
            if (ev == "SessionEnd")
            {
                if (_sessions.Remove(id)) goto changed;
                return;
            }

            if (!_sessions.TryGetValue(id, out var s))
            {
                s = new Session { Id = id, Project = "session" };
                _sessions[id] = s;
            }

            s.LastEvent = now;
            LastEventUtc = now;

            // Name the session after the stable project folder. cwd changes with every `cd`, so it is only
            // a fallback for the first event of a session, and is never allowed to rename it later.
            if (project.Length > 0) s.Project = Leaf(project);
            else if (s.Project == "session" && cwd.Length > 0) s.Project = Leaf(cwd);

            switch (ev)
            {
                case "UserPromptSubmit":
                    s.State = AgentState.Working;
                    s.TurnStart = now;
                    s.Action = "Thinking";
                    break;

                case "PreToolUse":
                    s.State = AgentState.Working;
                    s.TurnStart ??= now;
                    s.Action = Describe(tool, detail);
                    break;

                case "PostToolUse":
                    // The user answered a permission prompt (or the tool simply finished).
                    if (s.State == AgentState.Waiting)
                    {
                        s.State = AgentState.Working;
                        s.Action = "Working";
                    }
                    break;

                case "Notification":
                    if (NeedsUser(type, message))
                    {
                        s.State = AgentState.Waiting;
                        s.TurnStart ??= now;
                        s.Action = WaitingText(message);
                        notice = new AgentNotice(AgentNoticeKind.NeedsInput, s.Project, WaitingShort(message), null);
                    }
                    break; // other notifications (e.g. "waiting for your input" after a finished turn) are ignored

                case "Stop":
                    TimeSpan? took = s.TurnStart is { } start ? now - start : null;
                    s.State = AgentState.Idle;
                    s.Action = "Idle";
                    s.TurnStart = null;
                    notice = new AgentNotice(AgentNoticeKind.Finished, s.Project, "", took);
                    break;

                case "SessionStart":
                    break; // session now exists, idle

                default:
                    return;
            }
        }

    changed:
        Changed?.Invoke();
        if (notice is not null) Notice?.Invoke(notice);
    }

    private async Task PruneLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                if (Prune()) Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool Prune()
    {
        var now = DateTime.UtcNow;
        bool changed = false;

        lock (_lock)
        {
            foreach (var s in _sessions.Values.ToList())
            {
                var silent = now - s.LastEvent;

                if (s.State != AgentState.Idle && silent > StaleWorking)
                {
                    s.State = AgentState.Idle;
                    s.Action = "Idle";
                    s.TurnStart = null;
                    changed = true;
                }
                else if (s.State == AgentState.Idle && silent > StaleIdle)
                {
                    _sessions.Remove(s.Id);
                    changed = true;
                }
            }
        }
        return changed;
    }

    // ----- Text helpers --------------------------------------------------

    private static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static string Leaf(string path)
    {
        path = path.TrimEnd('\\', '/');
        int i = path.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? path[(i + 1)..] : path;
    }

    // Only prompts that stop the agent until the user acts. "idle_prompt" just means the turn is over.
    private static bool NeedsUser(string type, string message) =>
        type is "permission_prompt" or "elicitation_dialog"
        || (type.Length == 0 && message.Contains("permission", StringComparison.OrdinalIgnoreCase));

    private static string Describe(string tool, string detail) => tool switch
    {
        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => With("Editing", detail, "Editing files"),
        "Read" => With("Reading", detail, "Reading files"),
        "Bash" => With("Running", detail, "Running a command"),
        "Grep" or "Glob" => With("Searching", detail, "Searching the code"),
        "Task" or "Agent" => With("Subagent:", detail, "Running a subagent"),
        "WebFetch" or "WebSearch" => With("Web:", detail, "Searching the web"),
        "TodoWrite" => "Planning",
        "" => "Working",
        _ => $"Using {tool}"
    };

    private static string With(string verb, string detail, string fallback) =>
        detail.Length > 0 ? $"{verb} {detail}" : fallback;

    // "Claude needs your permission to use Bash" -> action "Needs permission: Bash", pop-up "Permission: Bash"
    private static string WaitingText(string message)
    {
        var tool = PermissionTool(message);
        return tool is null ? (message.Length > 0 ? message : "Needs your input") : $"Needs permission: {tool}";
    }

    private static string WaitingShort(string message)
    {
        var tool = PermissionTool(message);
        return tool is null ? "Needs your input" : $"Permission: {tool}";
    }

    private static string? PermissionTool(string message)
    {
        var m = Regex.Match(message, @"permission to use (\S+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
