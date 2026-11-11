using System.Globalization;
using System.Linq;
using System.Text;
using ClaudeVS.Core.Cli;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// A plain-text summary of the conversation's usage, for / → Copy session summary (to paste into a ticket, a
    /// commit message or a chat about what the work cost).
    /// </summary>
    public static class SessionSummary
    {
        public static string Format(TokenMeter m, string title = null, string sessionId = null)
        {
            if (m == null) return "";
            var s = m.SessionTotal;
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("Claude Code session").Append(string.IsNullOrWhiteSpace(title) ? "" : ": " + title.Trim()).Append('\n');
            sb.Append($"Turns: {m.Turns} · Cost: ${m.SessionCostUsd.ToString("0.00", c)} (API prices)\n");
            sb.Append($"Tokens: {TokenMeter.Format(s.Total)} (input {TokenMeter.Format(s.Input)}, output {TokenMeter.Format(s.Output)}");
            if (s.Thinking > 0) sb.Append($" of which thinking {TokenMeter.Format(s.Thinking)}");
            sb.Append($", cache read {TokenMeter.Format(s.CacheRead)}, cache write {TokenMeter.Format(s.CacheCreation)})\n");
            if (m.SessionSubagentTokens > 0) sb.Append($"Subagents: {TokenMeter.Format(m.SessionSubagentTokens)} tokens more (not in the line above; in the cost)\n");
            var kinds = m.SessionAgentCostByType.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ToList();
            if (kinds.Count > 0)
                sb.Append("Subagents by kind: ").Append(string.Join(" · ", kinds.Select(kv => $"{kv.Key} {AgentTask.FormatCost(kv.Value)}"))).Append('\n');
            sb.Append($"Cache hits: {m.CacheHitPercent.ToString("0", c)}%");
            if (m.SessionCacheSavedUsd >= 0.005) sb.Append($" (saved ${m.SessionCacheSavedUsd.ToString("0.00", c)})");
            if (m.ContextTokens > 0)
            {
                long window = m.ContextWindow > 0 ? m.ContextWindow : 0;
                sb.Append($" · Context now: {TokenMeter.Format(m.ContextTokens)}");
                if (window > 0) sb.Append($" of {TokenMeter.Format(window)} ({m.ContextPercent.ToString("0", c)}%)");
            }
            sb.Append('\n');
            var byTool = m.ResultTokensByTool().Where(kv => kv.Value > 0).Take(5).ToList();
            if (byTool.Count > 0)
                sb.Append("Tool results: ").Append(string.Join(" · ", byTool.Select(kv => $"{kv.Key} {TokenMeter.Format(kv.Value)}"))).Append('\n');
            if (m.CompactionsSaved > 0) sb.Append($"Compactions: {m.CompactionsSaved}\n");
            if (!string.IsNullOrEmpty(sessionId)) sb.Append($"Session: {sessionId}\n");
            return sb.ToString().TrimEnd('\n');
        }
    }
}
