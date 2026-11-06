using System;
using System.Text.RegularExpressions;

namespace ClaudeVS.Core.Cli
{
    /// <summary>
    /// The Claude Code version the CLI reports in each <c>init</c> message (<c>claude_code_version</c>), compared with
    /// the version this extension was verified against. Much of it relies on 2.1.316–2.1.318 behaviour:
    /// <c>stop_task</c>, <c>update_settings</c>, <c>init</c> at the start of every turn, rewind checkpoints in headless
    /// mode, <c>~/.claude/sessions/&lt;pid&gt;.json</c>, and resume fixes (2.1.300, 2.1.311, 2.1.318).
    /// </summary>
    public static class CliVersion
    {
        /// <summary>The newest version everything was verified with; raise it when re-verifying.</summary>
        public const string Tested = "2.1.318";

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

        /// <summary>True only for a readable version older than <see cref="Tested"/>; unknown versions are not flagged.</summary>
        public static bool IsOlderThanTested(string reported)
        {
            var v = Parse(reported);
            return v != null && v < Parse(Tested);
        }

        /// <summary>The one-line notice for an old CLI, or null when the version is current, newer or unknown.</summary>
        public static string Notice(string reported) =>
            IsOlderThanTested(reported)
                ? $"Claude Code {Parse(reported)} is older than {Tested}, which this extension is tested with. Some features (Stop for agents, rewind, settings changes, resuming after a crash) may not work. Run `claude update` and start a New chat."
                : null;
    }
}
