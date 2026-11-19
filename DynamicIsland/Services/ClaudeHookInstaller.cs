using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicIsland.Services;

/// <summary>
/// Adds or removes the island's hooks in Claude Code's user settings (~/.claude/settings.json).
/// Only our own entries are touched (identified by "island-hook" in the command); everything else
/// in the file is preserved, and a one-time backup is written next to it. Runs only when the user
/// clicks Connect or Disconnect in Settings.
/// </summary>
public static class ClaudeHookInstaller
{
    private const string Marker = "island-hook";

    // Events the island understands. PostToolUse is needed to notice that a permission prompt was answered.
    private static readonly string[] Events =
    [
        "SessionStart", "SessionEnd", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop"
    ];

    private static readonly string[] ToolEvents = ["PreToolUse", "PostToolUse"];

    // ISLAND_CLAUDE_SETTINGS lets tests point at a throwaway file instead of the real settings.
    public static string SettingsPath =>
        Environment.GetEnvironmentVariable("ISLAND_CLAUDE_SETTINGS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// <summary>hook\island-hook.exe next to the island.</summary>
    public static string HookExePath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "hook", "island-hook.exe");

    public static bool IsConnected()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            var hooks = JsonNode.Parse(File.ReadAllText(SettingsPath))?["hooks"] as JsonObject;
            return hooks?.Any(e => e.Value is JsonArray a && a.Any(IsOurs)) ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns null on success, otherwise a message for the user.</summary>
    public static string? Connect()
    {
        try
        {
            if (!File.Exists(HookExePath))
                return "island-hook.exe was not found next to the island. Rebuild or republish the app.";

            var root = LoadSettings(out string? error);
            if (root is null) return error;

            Backup();

            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            root["hooks"] = hooks;

            // Forward slashes work in both bash and cmd, which Claude Code may use to run the command.
            string command = "\"" + HookExePath.Replace('\\', '/') + "\"";

            foreach (string ev in Events)
            {
                var list = hooks[ev] as JsonArray ?? new JsonArray();
                RemoveOurs(list);

                var entry = new JsonObject();
                if (ToolEvents.Contains(ev)) entry["matcher"] = "*";
                entry["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = 5
                });
                list.Add(entry);
                hooks[ev] = list;
            }

            Save(root);
            return null;
        }
        catch (Exception ex)
        {
            return "Could not update Claude Code settings: " + ex.Message;
        }
    }

    public static string? Disconnect()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;

            var root = LoadSettings(out string? error);
            if (root is null) return error;
            if (root["hooks"] is not JsonObject hooks) return null;

            foreach (string ev in hooks.Select(h => h.Key).ToList())
            {
                if (hooks[ev] is not JsonArray list) continue;
                RemoveOurs(list);
                if (list.Count == 0) hooks.Remove(ev);
            }
            if (hooks.Count == 0) root.Remove("hooks");

            Save(root);
            return null;
        }
        catch (Exception ex)
        {
            return "Could not update Claude Code settings: " + ex.Message;
        }
    }

    // ----- Helpers -------------------------------------------------------

    private static JsonObject? LoadSettings(out string? error)
    {
        error = null;
        if (!File.Exists(SettingsPath)) return new JsonObject();

        try
        {
            string text = File.ReadAllText(SettingsPath);
            if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) as JsonObject;
        }
        catch (JsonException)
        {
            error = "Claude Code's settings.json is not valid JSON, so I left it untouched.";
            return null;
        }
    }

    private static void Backup()
    {
        string backup = SettingsPath + ".island-backup";
        if (File.Exists(SettingsPath) && !File.Exists(backup))
            File.Copy(SettingsPath, backup);
    }

    private static void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    // An entry is ours when any of its hook commands mentions island-hook.
    private static bool IsOurs(JsonNode? entry) =>
        entry?["hooks"] is JsonArray inner
        && inner.Any(h => h?["command"]?.GetValue<string>()?.Contains(Marker, StringComparison.OrdinalIgnoreCase) == true);

    private static void RemoveOurs(JsonArray list)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (IsOurs(list[i])) list.RemoveAt(i);
    }
}
