using System;
using System.Text.RegularExpressions;

namespace ClaudeVS.Core.Cli
{
    /// <summary>
    /// The Claude Code version the CLI reports in each <c>init</c> message (<c>claude_code_version</c>), compared with
    /// the oldest one this extension needs. Much of it relies on 2.1.316–2.1.318 behaviour:
    /// <c>stop_task</c>, <c>update_settings</c>, <c>init</c> at the start of every turn, rewind checkpoints in headless
    /// mode, <c>~/.claude/sessions/&lt;pid&gt;.json</c>, and resume fixes (2.1.300, 2.1.311, 2.1.318). Newer fields
    /// (2.1.319's structured tool results, <c>thinking_duration_ms</c>, <c>subagent_stats</c>) are optional.
    /// </summary>
    public static class CliVersion
    {
        /// <summary>The oldest version the extension needs; below it the chat says so once. Raise it only when a feature starts to depend on a newer CLI.</summary>
        public const string Required = "2.1.318";

        /// <summary>The newest version everything was verified with (all live tests); raise it when re-verifying.</summary>
        public const string Tested = "2.1.323";

        private static readonly Regex Number = new Regex(@"^\s*v?(\d+)\.(\d+)\.(\d+)", RegexOptions.CultureInvariant);

        /// <summary>"2.1.316" or "2.1.316 (Claude Code)" → 2.1.316; null when it is not a version.</summary>
        public static Version Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = Number.Match(text);
            if (!m.Success) return null;
            try { return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)); }
            catch (OverflowException) { return null; }
        }

        /// <summary>True only for a readable version older than <see cref="Required"/>; unknown versions are not flagged.</summary>
        public static bool IsOlderThanRequired(string reported)
        {
            var v = Parse(reported);
            return v != null && v < Parse(Required);
        }

        /// <summary>The one-line notice for an old CLI, or null when the version is new enough or unknown.</summary>
        public static string Notice(string reported) =>
            IsOlderThanRequired(reported)
                ? $"Claude Code {Parse(reported)} is older than {Required}, which this extension needs. Some features (Stop for agents, rewind, settings changes, resuming after a crash) may not work. Run `claude update` and start a New chat."
                : null;
    }
}
