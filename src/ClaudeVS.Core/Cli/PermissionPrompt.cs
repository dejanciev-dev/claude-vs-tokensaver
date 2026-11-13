using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    /// <summary>
    /// What the permission card shows for a <c>can_use_tool</c> request: a plain question, the command / file / URL,
    /// Claude's own description, and the CLI's "always allow" suggestion.
    ///
    /// Verified with Claude Code 2.1.318: a Bash request carries <c>description</c> and
    /// <c>permission_suggestions</c> such as <c>{type: "addRules", rules: [{toolName: "Bash", ruleContent:
    /// "echo probe-123 *"}], behavior: "allow", destination: "localSettings"}</c>. Sending those back as
    /// <c>updatedPermissions</c> with the allow answer makes the CLI save <c>Bash(echo probe-123 *)</c> in
    /// <c>.claude/settings.local.json</c>.
    /// </summary>
    public sealed class PermissionPrompt
    {
        public string ToolName { get; private set; }
        /// <summary>"Run this command?", "Edit this file?", …</summary>
        public string Title { get; private set; }
        /// <summary>The command, file path, URL or pattern; null when the tool has none.</summary>
        public string Detail { get; private set; }
        /// <summary>True when <see cref="Detail"/> is a command (shown in a code box).</summary>
        public bool DetailIsCode { get; private set; }
        /// <summary>Claude's description of the call (the request's or the input's <c>description</c>).</summary>
        public string Description { get; private set; }
        /// <summary>The "addRules / allow" suggestions, to send back as <c>updatedPermissions</c>; null when none.</summary>
        public JArray AlwaysAllow { get; private set; }
        /// <summary>The rules those suggestions add, as written in settings: <c>Bash(echo probe-123 *)</c>.</summary>
        public IReadOnlyList<string> AlwaysAllowRules { get; private set; } = Array.Empty<string>();
        /// <summary>Where they are saved, in words: "in this project, just for you (.claude/settings.local.json)".</summary>
        public string AlwaysAllowScope { get; private set; }
        /// <summary>Where they are saved, as the CLI names it: <c>localSettings</c>, <c>projectSettings</c>, <c>userSettings</c>, <c>session</c>.</summary>
        public string AlwaysAllowDestination { get; private set; }

        /// <summary>
        /// Said when an "Always allow" rule is not in its settings file after the call. Since 2.1.323 Claude Code keeps
        /// a rule for the session only when it looks like it holds a secret, or would be saved to a
        /// <c>settings.local.json</c> that git tracks, though it still suggests saving it there (verified with 2.1.323:
        /// the suggestion says <c>localSettings</c> and the call is reported as <c>user_temporary</c> either way).
        /// </summary>
        public static string NotSavedNote(IReadOnlyList<string> rules, string file) =>
            $"ⓘ Claude Code allows {string.Join(", ", rules)} until this session ends but didn't save it in {file}: it does that when a rule looks like it holds a secret, or when git tracks the file. Add it to the file yourself to keep it.";

        public static PermissionPrompt From(string toolName, JObject input, JObject request)
        {
            input = input ?? new JObject();
            string Arg(string k) => input[k]?.Type == JTokenType.String ? ((string)input[k]).Trim() : null;
            var p = new PermissionPrompt { ToolName = toolName };

            switch (toolName)
            {
                case "Bash":
                case "PowerShell":
                    p.Title = "Run this command?";
                    p.Detail = Arg("command");
                    p.DetailIsCode = true;
                    break;
                case "Write":
                    p.Title = "Create or overwrite this file?";
                    p.Detail = Arg("file_path");
                    break;
                case "Edit":
                case "MultiEdit":
                case "NotebookEdit":
                    p.Title = "Edit this file?";
                    p.Detail = Arg("file_path") ?? Arg("notebook_path");
                    break;
                case "Read":
                    p.Title = "Read this file?";
                    p.Detail = Arg("file_path");
                    break;
                case "Glob":
                case "Grep":
                    p.Title = "Search the files?";
                    p.Detail = Arg("pattern") + (Arg("path") != null ? "  in  " + Arg("path") : "");
                    break;
                case "WebFetch":
                    p.Title = "Open this web page?";
                    p.Detail = Arg("url");
                    break;
                case "WebSearch":
                    p.Title = "Search the web?";
                    p.Detail = Arg("query");
                    break;
                default:
                    string friendly = toolName != null && toolName.StartsWith("mcp__", StringComparison.Ordinal)
                        ? string.Join(" · ", toolName.Substring(5).Split(new[] { "__" }, StringSplitOptions.RemoveEmptyEntries))
                        : toolName;
                    p.Title = $"Allow {friendly}?";
                    string described = ClaudeStreamParser.DescribeTool(toolName, input);
                    p.Detail = described != toolName ? described : null;
                    break;
            }
            if (string.IsNullOrWhiteSpace(p.Detail)) p.Detail = null;

            string description = request?["description"]?.Type == JTokenType.String ? (string)request["description"] : Arg("description");
            if (!string.IsNullOrWhiteSpace(description) && description.Trim() != p.Detail) p.Description = description.Trim();

            var allow = new JArray((request?["permission_suggestions"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(s => (string)s["type"] == "addRules" && (string)s["behavior"] == "allow" && s["rules"] is JArray r && r.Count > 0));
            if (allow.Count > 0)
            {
                p.AlwaysAllow = allow;
                p.AlwaysAllowRules = allow.SelectMany(s => ((JArray)s["rules"]).OfType<JObject>())
                    .Select(r => string.IsNullOrEmpty((string)r["ruleContent"]) ? (string)r["toolName"] : $"{(string)r["toolName"]}({(string)r["ruleContent"]})")
                    .Where(r => !string.IsNullOrEmpty(r)).Distinct().ToList();
                p.AlwaysAllowDestination = (string)allow[0]["destination"];
                p.AlwaysAllowScope = Scope(p.AlwaysAllowDestination);
            }
            return p;
        }

        /// <summary>
        /// The settings file an "Always allow" goes to: <paramref name="projectDir"/>/.claude/settings.local.json,
        /// …/settings.json, or ~/.claude/settings.json; null for <c>session</c>, unknown destinations or no folder.
        /// </summary>
        public static string SettingsPath(string destination, string projectDir, string userHome)
        {
            switch (destination)
            {
                case "localSettings": return string.IsNullOrEmpty(projectDir) ? null : System.IO.Path.Combine(projectDir, ".claude", "settings.local.json");
                case "projectSettings": return string.IsNullOrEmpty(projectDir) ? null : System.IO.Path.Combine(projectDir, ".claude", "settings.json");
                case "userSettings": return string.IsNullOrEmpty(userHome) ? null : System.IO.Path.Combine(userHome, ".claude", "settings.json");
                default: return null;
            }
        }

        /// <summary>The rules not in <c>permissions.allow</c> of a settings file's text (all of them for a missing or unreadable file).</summary>
        public static IReadOnlyList<string> MissingRules(string settingsJson, IEnumerable<string> rules)
        {
            var saved = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (!string.IsNullOrWhiteSpace(settingsJson) && JObject.Parse(settingsJson).SelectToken("permissions.allow") is JArray allow)
                    foreach (var r in allow) if (r.Type == JTokenType.String) saved.Add(((string)r).Trim());
            }
            catch (Newtonsoft.Json.JsonException) { }
            return (rules ?? Array.Empty<string>()).Where(r => !saved.Contains(r)).ToList();
        }

        private static string Scope(string destination)
        {
            switch (destination)
            {
                case "localSettings": return "in this project, just for you (.claude/settings.local.json)";
                case "projectSettings": return "in this project, for everyone (.claude/settings.json)";
                case "userSettings": return "in all your projects (~/.claude/settings.json)";
                case "session": return "until this session ends";
                default: return destination;
            }
        }
    }
}
