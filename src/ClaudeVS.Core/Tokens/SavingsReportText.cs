using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// The whole text of Tools → Claude Code → Show Savings Report: the report for the open solution (or all), the
    /// all-solutions summary, and the router replay. Also as Markdown, for Save Savings Report as Markdown.
    /// </summary>
    public static class SavingsReportText
    {
        /// <param name="solution">The open solution's folder; null for all solutions.</param>
        /// <param name="logFolder">Where the usage log is, for the last line; null leaves it out.</param>
        public static string Full(IReadOnlyList<UsageRecord> records, PriceTable prices, string solution, DateTime nowUtc, string logFolder = null)
        {
            records = records ?? new UsageRecord[0];
            var sb = new StringBuilder();
            sb.Append(SavingsReport.Build(records, prices, solution, nowUtc: nowUtc).ToText());
            if (solution != null)
            {
                var all = SavingsReport.Build(records, prices, nowUtc: nowUtc);
                if (all.Turns > 0)
                {
                    sb.AppendLine().AppendLine($"All solutions: {all.Turns} turns, ${all.ActualCostUsd:0.00} actual, ~${all.RoutingSavedUsd:0.00} saved by routing.");
                    if (all.MonthLine() is string month) sb.AppendLine(month);
                    string solutions = all.SolutionsText();
                    if (solutions.Length > 0) sb.Append(solutions);
                }
            }

            var diffs = RouterReplay.Compare(records, new ModelRouter(), prices, out int replayed);
            sb.AppendLine();
            if (replayed == 0)
                sb.AppendLine("Router replay: no prompt previews logged (Options > Diagnostics > Log prompt preview).");
            else
            {
                sb.AppendLine($"Router replay: {diffs.Count} of {replayed} logged prompts would route differently with the current rules.");
                foreach (var d in diffs.Take(15))
                    sb.AppendLine($"  {d.LoggedModel} → {d.NowModel}{d.CostChangeText()}: \"{Shorten(d.PromptPreview, 70)}\" ({d.NowReason})");
                if (RouterReplay.CostSummary(diffs) is string replayCost) sb.AppendLine("  " + replayCost);
            }
            if (logFolder != null) sb.AppendLine().AppendLine($"Log: {logFolder}");
            return sb.ToString();
        }

        /// <summary>
        /// The report as a Markdown file: a heading, when it was made, and the text in a fenced block (its columns are
        /// aligned with spaces, which only a fixed-width block keeps).
        /// </summary>
        public static string Markdown(string reportText, DateTime nowUtc)
        {
            string text = (reportText ?? "").Replace("\r\n", "\n").Trim('\n');
            // A line of three or more backticks inside would end the block early: fence with more than the longest run
            int longest = 0, run = 0;
            foreach (char ch in text) { run = ch == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
            string fence = new string('`', Math.Max(3, longest + 1));
            return "# Claude Code savings report\n\n"
                + "Made " + nowUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ", at API prices.\n\n"
                + fence + "text\n" + text + "\n" + fence + "\n";
        }

        private static string Shorten(string s, int max)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }
    }
}
