using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

// Claude Code hook for the Dynamic Island.
//
// Claude Code runs this on hook events and passes the event as JSON on stdin. We forward a SMALL
// summary (event, session id, project folder, tool name, a short detail, notification text) to the
// island over a per-user named pipe. Prompts and file contents are never sent.
//
// Design rules: never write to stdout or stderr, never block, and always exit 0, so this tool can
// not change how Claude Code behaves. If the island is not running the connect simply times out.

const string PipeName = "DynamicIsland.Agent";

try
{
    string input = Console.In.ReadToEnd();
    if (JsonNode.Parse(input) is not JsonObject root) return 0;

    string tool = Str(root, "tool_name");
    var summary = new JsonObject
    {
        ["event"] = Str(root, "hook_event_name"),
        ["session"] = Str(root, "session_id"),
        ["cwd"] = Str(root, "cwd"),
        // cwd follows every `cd` the agent runs; the project folder stays put, so it names the session.
        ["project"] = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") ?? "",
        ["tool"] = tool,
        ["detail"] = Detail(tool, root["tool_input"] as JsonObject),
        ["message"] = Trim(Str(root, "message"), 200),
        ["type"] = Str(root, "notification_type")
    };

    using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
    pipe.Connect(150); // island not running: give up quickly
    byte[] bytes = Encoding.UTF8.GetBytes(summary.ToJsonString() + "\n");
    pipe.Write(bytes, 0, bytes.Length);
    pipe.Flush();
}
catch (Exception ex)
{
    // Swallow everything: a status display must never break the agent.
    // For troubleshooting, set ISLAND_HOOK_DEBUG=1 and the error is written to %TEMP%\island-hook.log.
    if (Environment.GetEnvironmentVariable("ISLAND_HOOK_DEBUG") == "1")
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "island-hook.log"), $"{DateTime.Now:O} {ex}\n"); }
        catch { }
    }
}

return 0;

static string Str(JsonObject o, string key) =>
    o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

static string Trim(string s, int max)
{
    s = s.ReplaceLineEndings(" ").Trim();
    return s.Length <= max ? s : s[..max];
}

// A short, human-friendly hint about what the tool is touching.
static string Detail(string tool, JsonObject? input)
{
    if (input is null) return "";

    switch (tool)
    {
        case "Edit":
        case "MultiEdit":
        case "Write":
        case "Read":
            return Leaf(Str(input, "file_path"));
        case "NotebookEdit":
            return Leaf(Str(input, "notebook_path"));
        case "Bash":
            return Trim(Str(input, "command"), 60);
        case "Grep":
        case "Glob":
            return Trim(Str(input, "pattern"), 40);
        case "Task":
        case "Agent":
            return Trim(Str(input, "description"), 40);
        case "WebFetch":
            return Trim(Str(input, "url"), 60);
        case "WebSearch":
            return Trim(Str(input, "query"), 40);
        default:
            return "";
    }
}

static string Leaf(string path)
{
    path = path.TrimEnd('\\', '/');
    int i = path.LastIndexOfAny(['\\', '/']);
    return i >= 0 ? path[(i + 1)..] : path;
}
