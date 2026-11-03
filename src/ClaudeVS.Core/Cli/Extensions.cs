using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    /// <summary>A row of the CLI's /skills menu (<c>get_skills_dialog</c>, 2.1.317).</summary>
    public sealed class SkillRow
    {
        /// <summary>Full name, the settings key ("plugin:skill" or "server:skill").</summary>
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        /// <summary>"user", "project", "plugin", "built-in", "claude.ai sync", …</summary>
        public string Source { get; set; }
        /// <summary>Tokens its listing takes in the system prompt, for the active model.</summary>
        public int Tokens { get; set; }
        /// <summary>"on", "name-only", "user-invocable-only" or "off".</summary>
        public string State { get; set; }
        /// <summary>Why it can't be changed here ("author", "plugin", "policy", "flag", "reserved-name"); null when it can.</summary>
        public string LockedBy { get; set; }

        public bool IsOn => State != "off";

        public static List<SkillRow> ParseDialog(JObject response) =>
            ((response?["skills"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>()).Select(s => new SkillRow
            {
                Name = (string)s["name"],
                DisplayName = (string)s["display_name"] ?? (string)s["name"],
                Description = (string)s["description"],
                Source = (string)s["source"],
                Tokens = s["tokens"]?.Type == JTokenType.Integer ? (int)s["tokens"] : 0,
                State = (string)s["state"] ?? "on",
                LockedBy = (string)s["locked_by"],
            }).Where(s => !string.IsNullOrEmpty(s.Name)).ToList();

        /// <summary>Plain words for <see cref="LockedBy"/>.</summary>
        public static string LockReason(string lockedBy)
        {
            switch (lockedBy)
            {
                case "author": return "the skill turns this off itself";
                case "plugin": return "set by its plugin";
                case "policy": return "set by your organization";
                case "flag": return "set by a launch option";
                case "reserved-name": return "its name can't be stored in settings";
                default: return lockedBy;
            }
        }

        /// <summary>The edit <c>claude edit-skill-overrides --json</c> reads from stdin.</summary>
        public static string OverrideEdit(string name, string state) =>
            new JObject { ["name"] = name, ["state"] = state }.ToString(Formatting.None);
    }

    /// <summary>An MCP server as <c>mcp_status</c> reports it (2.1.317).</summary>
    public sealed class McpServerRow
    {
        public string Name { get; set; }
        /// <summary>"connected", "pending", "needs-auth", "failed", "disabled", …</summary>
        public string Status { get; set; }
        /// <summary>"user", "project", "local", "claudeai", …</summary>
        public string Scope { get; set; }
        public string Transport { get; set; }
        /// <summary>Number of tools, when the CLI lists them (connected servers); -1 when unknown.</summary>
        public int ToolCount { get; set; } = -1;
        public string Error { get; set; }

        public bool IsEnabled => Status != "disabled";

        public static List<McpServerRow> ParseStatus(JObject response) =>
            ((response?["mcpServers"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>()).Select(s => new McpServerRow
            {
                Name = (string)s["name"],
                Status = (string)s["status"] ?? "unknown",
                Scope = (string)s["scope"],
                Transport = (string)s["transport"] ?? (string)s.SelectToken("config.type"),
                ToolCount = s["tools"] is JArray tools ? tools.Count : -1,
                Error = (string)s["error"],
            }).Where(s => !string.IsNullOrEmpty(s.Name)).ToList();

        /// <summary>"Connected", "Needs sign-in", …</summary>
        public static string StatusText(string status)
        {
            switch (status)
            {
                case "connected": return "Connected";
                case "pending": return "Connecting";
                case "needs-auth": return "Needs sign-in";
                case "failed": return "Failed";
                case "disabled": return "Off";
                default: return status;
            }
        }
    }

    /// <summary>One-shot CLI subcommands the VS Code extension also uses.</summary>
    public static class ClaudeSubcommands
    {
        /// <summary>
        /// Sets a skill's state through <c>claude edit-skill-overrides --json</c> (the edit is read from stdin).
        /// Returns null on success, else the CLI's message. The CLI refuses when the editor was started from inside
        /// a Claude Code session.
        /// </summary>
        public static async Task<string> SetSkillStateAsync(string claudePath, string workingDirectory, string name, string state)
        {
            string exe = ClaudeCliLocator.Resolve(claudePath);
            if (exe == null) return "Claude Code CLI not found.";
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = workingDirectory,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            };
            const string args = "edit-skill-overrides --json";
            if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                psi.Arguments = "/d /s /c \"" + ClaudeArgsBuilder.Quote(exe) + " " + args + "\"";
            }
            else { psi.FileName = exe; psi.Arguments = args; }

            using (var p = Process.Start(psi))
            {
                var stdin = new System.IO.StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
                await stdin.WriteAsync(SkillRow.OverrideEdit(name, state)).ConfigureAwait(false);
                stdin.Close();
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                var exited = await Task.Run(() => p.WaitForExit(30000)).ConfigureAwait(false);
                if (!exited) { try { p.Kill(); } catch (InvalidOperationException) { } return "Claude Code did not finish the change in time."; }
                string message = ((await errTask.ConfigureAwait(false)) + "\n" + (await outTask.ConfigureAwait(false))).Trim();
                return p.ExitCode == 0 ? null : (message.Length > 0 ? message : $"claude edit-skill-overrides exited with code {p.ExitCode}.");
            }
        }
    }
}
