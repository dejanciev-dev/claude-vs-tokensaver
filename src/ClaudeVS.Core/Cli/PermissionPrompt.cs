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
                p.AlwaysAllowScope = Scope((string)allow[0]["destination"]);
            }
            return p;
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
