using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClaudeVS.Core.Cli
{
    /// <summary>Model picked in the UI. <see cref="Auto"/> lets <see cref="Tokens.ModelRouter"/> decide.</summary>
    public enum ModelChoice { Auto, Haiku, Sonnet, Opus }

    /// <summary>Values accepted by <c>claude --effort</c>. <see cref="Auto"/> = router decides / CLI default.</summary>
    public enum EffortChoice { Auto, Low, Medium, High, XHigh, Max }

    /// <summary>
    /// Permission modes, as in the VS Code panel: Default = "Manual" (ask before each edit), AcceptEdits = "Edit
    /// automatically", Plan, Auto (approve actions that pass the CLI's safety check). New values go at the end:
    /// the Options page stores the name, but keep the order stable anyway.
    /// </summary>
    public enum PermissionModeChoice { Default, AcceptEdits, Plan, BypassPermissions, Auto }

    public static class ChoiceNames
    {
        /// <summary>CLI aliases — always resolve to the newest model of that family, so no version ids to maintain.</summary>
        public static string ToCli(this ModelChoice m)
        {
            switch (m)
            {
                case ModelChoice.Haiku: return "haiku";
                case ModelChoice.Sonnet: return "sonnet";
                case ModelChoice.Opus: return "opus";
                default: return null;
            }
        }

        public static string ToCli(this EffortChoice e)
        {
            switch (e)
            {
                case EffortChoice.Low: return "low";
                case EffortChoice.Medium: return "medium";
                case EffortChoice.High: return "high";
                case EffortChoice.XHigh: return "xhigh";
                case EffortChoice.Max: return "max";
                default: return null;
            }
        }

        public static string ToCli(this PermissionModeChoice p)
        {
            switch (p)
            {
                case PermissionModeChoice.AcceptEdits: return "acceptEdits";
                case PermissionModeChoice.Plan: return "plan";
                case PermissionModeChoice.BypassPermissions: return "bypassPermissions";
                case PermissionModeChoice.Auto: return "auto";
                default: return null;
            }
        }

        /// <summary>
        /// Name for the <c>set_permission_mode</c> control request. Unlike the command-line flag, which omits the
        /// default, it needs "default" spelled out (verified with Claude Code 2.1.316).
        /// </summary>
        public static string ToControl(this PermissionModeChoice p) => p.ToCli() ?? "default";

        /// <summary>The CLI's mode name ("default", "acceptEdits", "plan", "bypassPermissions", "auto") as a choice; null when unknown.</summary>
        public static PermissionModeChoice? FromCli(string mode)
        {
            switch (mode)
            {
                case "default": return PermissionModeChoice.Default;
                case "acceptEdits": return PermissionModeChoice.AcceptEdits;
                case "plan": return PermissionModeChoice.Plan;
                case "bypassPermissions": return PermissionModeChoice.BypassPermissions;
                case "auto": return PermissionModeChoice.Auto;
                default: return null;
            }
        }

        /// <summary>Label and one-line description, as shown in the mode picker.</summary>
        public static (string Label, string Description) Describe(this PermissionModeChoice p)
        {
            switch (p)
            {
                case PermissionModeChoice.AcceptEdits: return ("Edit automatically", "Claude edits files without asking; shell commands still ask");
                case PermissionModeChoice.Plan: return ("Plan", "Claude explores the code and presents a plan before editing");
                case PermissionModeChoice.Auto: return ("Auto", "Claude approves actions that pass a safety check and pauses for anything risky");
                case PermissionModeChoice.BypassPermissions: return ("Bypass permissions", "Claude never asks. Use only in a sandbox");
                default: return ("Manual", "Claude asks for approval before each edit");
            }
        }

        /// <summary>The modes Shift+Tab cycles through, in order (bypass is left out on purpose).</summary>
        public static readonly PermissionModeChoice[] CycleOrder =
            { PermissionModeChoice.Default, PermissionModeChoice.AcceptEdits, PermissionModeChoice.Plan, PermissionModeChoice.Auto };

        public static PermissionModeChoice Next(this PermissionModeChoice p)
        {
            int i = Array.IndexOf(CycleOrder, p);
            return CycleOrder[(i + 1) % CycleOrder.Length];
        }
    }

    public sealed class ClaudeLaunchOptions
    {
        /// <summary>Full path to claude(.exe/.cmd). Empty = resolve from PATH.</summary>
        public string ClaudePath { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public ModelChoice Model { get; set; } = ModelChoice.Auto;
        public EffortChoice Effort { get; set; } = EffortChoice.Auto;
        public PermissionModeChoice PermissionMode { get; set; } = PermissionModeChoice.AcceptEdits;
        public string ResumeSessionId { get; set; }
        /// <summary>With <see cref="ResumeSessionId"/>: continue in a new session (a copy), leaving the original as it was (<c>--fork-session</c>).</summary>
        public bool ForkSession { get; set; }
        /// <summary>
        /// Lets the CLI keep a copy of each file before Claude changes it, so a prompt's edits can be rewound
        /// (<c>rewind_files</c>). Set through an environment variable; there is no command-line flag.
        /// </summary>
        public bool EnableFileCheckpointing { get; set; }
        public const string FileCheckpointingVariable = "CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING";

        /// <summary>Display name (<c>--name</c>): the CLI stores it as the session's title, shown in its /resume picker.</summary>
        public string SessionName { get; set; }
        /// <summary>Short extra system prompt (kept tiny on purpose — it is paid on every request).</summary>
        public string AppendSystemPrompt { get; set; }
        /// <summary>
        /// MCP servers that run inside this process (<see cref="SdkMcpServer"/>). Declared with <c>--mcp-config</c>;
        /// their read-only tools are added to <c>--allowedTools</c> so they run without a permission prompt.
        /// </summary>
        public List<SdkMcpServer> SdkMcpServers { get; } = new List<SdkMcpServer>();
        /// <summary>
        /// Registers a PostToolUse hook for Edit and Write, so a note can be added to the tool result Claude sees
        /// (<see cref="ClaudeSession.SetToolResultNote"/>), e.g. when the user applied only part of an edit.
        /// </summary>
        public bool EditNotesHook { get; set; }
        public string ExtraArguments { get; set; } = "";
    }

    public static class ClaudeArgsBuilder
    {
        /// <summary>
        /// Persistent, bidirectional headless session. One process per chat; prompts and control
        /// requests are written to stdin as JSON lines, events are read from stdout.
        /// <c>--permission-prompt-tool stdio</c> routes permission questions back to us.
        /// </summary>
        public static string Build(ClaudeLaunchOptions o)
        {
            if (o == null) throw new ArgumentNullException(nameof(o));
            var parts = new List<string>
            {
                "--print",
                "--input-format", "stream-json",
                "--output-format", "stream-json",
                "--verbose",
                "--include-partial-messages",
                "--permission-prompt-tool", "stdio",
            };

            string model = o.Model.ToCli();
            if (model != null) { parts.Add("--model"); parts.Add(model); }

            string effort = o.Effort.ToCli();
            if (effort != null) { parts.Add("--effort"); parts.Add(effort); }

            string perm = o.PermissionMode.ToCli();
            if (perm != null) { parts.Add("--permission-mode"); parts.Add(perm); }

            if (!string.IsNullOrWhiteSpace(o.ResumeSessionId))
            {
                parts.Add("--resume"); parts.Add(o.ResumeSessionId.Trim());
                if (o.ForkSession) parts.Add("--fork-session");
            }

            if (!string.IsNullOrWhiteSpace(o.SessionName)) { parts.Add("--name"); parts.Add(o.SessionName.Trim().Replace('\n', ' ').Replace('\r', ' ')); }

            if (!string.IsNullOrWhiteSpace(o.AppendSystemPrompt)) { parts.Add("--append-system-prompt"); parts.Add(o.AppendSystemPrompt.Trim()); }

            if (o.SdkMcpServers.Count > 0)
            {
                parts.Add("--mcp-config"); parts.Add(SdkMcpServer.ConfigJson(o.SdkMcpServers));
                var readOnly = o.SdkMcpServers.SelectMany(s => s.Tools.Where(t => t.ReadOnly).Select(s.QualifiedName)).ToList();
                if (readOnly.Count > 0) { parts.Add("--allowedTools"); parts.Add(string.Join(",", readOnly)); }
            }

            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(p));
            }
            if (!string.IsNullOrWhiteSpace(o.ExtraArguments)) sb.Append(' ').Append(o.ExtraArguments.Trim());
            return sb.ToString();
        }

        /// <summary>Windows command-line quoting (CommandLineToArgvW rules).</summary>
        public static string Quote(string arg)
        {
            if (arg == null) return "\"\"";
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '\n' }) < 0) return arg;

            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); }
                else { sb.Append('\\', backslashes).Append(c); }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }
    }
}
