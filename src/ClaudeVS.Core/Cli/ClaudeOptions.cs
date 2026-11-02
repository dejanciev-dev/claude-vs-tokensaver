using System;
using System.Collections.Generic;
using System.Text;

namespace ClaudeVS.Core.Cli
{
    /// <summary>Model picked in the UI. <see cref="Auto"/> lets <see cref="Tokens.ModelRouter"/> decide.</summary>
    public enum ModelChoice { Auto, Haiku, Sonnet, Opus }

    /// <summary>Values accepted by <c>claude --effort</c>. <see cref="Auto"/> = router decides / CLI default.</summary>
    public enum EffortChoice { Auto, Low, Medium, High, XHigh, Max }

    public enum PermissionModeChoice { Default, AcceptEdits, Plan, BypassPermissions }

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
                default: return null;
            }
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
        /// <summary>Short extra system prompt (kept tiny on purpose — it is paid on every request).</summary>
        public string AppendSystemPrompt { get; set; }
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

            if (!string.IsNullOrWhiteSpace(o.ResumeSessionId)) { parts.Add("--resume"); parts.Add(o.ResumeSessionId.Trim()); }

            if (!string.IsNullOrWhiteSpace(o.AppendSystemPrompt)) { parts.Add("--append-system-prompt"); parts.Add(o.AppendSystemPrompt.Trim()); }

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
