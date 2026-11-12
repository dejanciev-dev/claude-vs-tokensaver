using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// The usage log as CSV, one row per turn, oldest first, for a spreadsheet: RFC 4180 quoting, invariant numbers,
    /// local times. A text cell starting with = + - @ gets a leading ' so a spreadsheet does not run it as a formula.
    /// </summary>
    public static class UsageCsv
    {
        public static readonly string[] Columns =
        {
            "time", "solution", "model", "routed", "route_model", "route_effort", "router",
            "input", "output", "thinking", "cache_read", "cache_write", "cache_write_1h", "fast_tokens", "large_context_tokens", "agent_tokens", "agent_types",
            "cost_usd", "router_cost_usd", "duration_ms", "error", "tokens_avoided", "route_reason", "prompt", "session", "effort", "tools", "turn",
        };

        /// <summary>Writes the header and one row per record; returns the number of rows.</summary>
        public static int Write(IEnumerable<UsageRecord> records, TextWriter w, string solution = null)
        {
            var c = CultureInfo.InvariantCulture;
            w.Write(string.Join(",", Columns));
            w.Write("\r\n");
            int rows = 0;
            var turns = new Dictionary<string, int>(StringComparer.Ordinal); // turns so far per session id
            foreach (var r in (records ?? Enumerable.Empty<UsageRecord>())
                .Where(r => solution == null || string.Equals((r.Solution ?? "").TrimEnd('\\', '/'), solution.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Timestamp))
            {
                // The turn's number in its conversation; empty for lines logged without a session id
                string turn = "";
                if (!string.IsNullOrEmpty(r.SessionId))
                {
                    int n = (turns.TryGetValue(r.SessionId, out var had) ? had : 0) + 1;
                    turns[r.SessionId] = n;
                    turn = n.ToString(c);
                }
                var cells = new[]
                {
                    r.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", c),
                    Text(r.Solution), Text(r.ModelUsed), r.Routed ? "1" : "0", Text(r.RouteModel), Text(r.RouteEffort), Text(r.Router),
                    r.InputTokens.ToString(c), r.OutputTokens.ToString(c), r.ThinkingTokens.ToString(c),
                    r.CacheReadTokens.ToString(c), r.CacheWriteTokens.ToString(c), r.CacheWrite1hTokens.ToString(c), r.FastTokens.ToString(c), r.LargeContextTokens.ToString(c), r.AgentTokensTotal.ToString(c), Text(AgentTypes(r.AgentTypes)),
                    r.CostUsd.ToString("0.######", c), r.RouterCostUsd.ToString("0.######", c), r.DurationMs.ToString(c),
                    r.IsError ? "1" : "0", TokenEstimate.FromChars(r.CharsAvoided).ToString(c), Text(r.RouteReason), Text(r.PromptPreview), Text(r.SessionId), Text(r.Effort), Text(Tools(r.ToolResultTokens)), turn,
                };
                w.Write(string.Join(",", cells));
                w.Write("\r\n");
                rows++;
            }
            return rows;
        }

        /// <summary>
        /// "Explore:0.75;general-purpose:0.25": each kind of agent's share of the turn's subagent cost (see
        /// <see cref="UsageRecord.AgentTypes"/>), at most 4 decimals; "" when none.
        /// </summary>
        public static string AgentTypes(List<AgentTypeCost> types) =>
            types == null ? "" : string.Join(";", types.Where(t => !string.IsNullOrEmpty(t.Type) && t.Share > 0)
                .Select(t => t.Type + ":" + t.Share.ToString("0.####", CultureInfo.InvariantCulture)));

        /// <summary>
        /// "Read:1200;Shell:400": the tokens each tool's results added in the turn (see
        /// <see cref="UsageRecord.ToolResultTokens"/>), largest first; "" when none.
        /// </summary>
        public static string Tools(Dictionary<string, long> tools) =>
            tools == null ? "" : string.Join(";", tools.Where(kv => !string.IsNullOrEmpty(kv.Key) && kv.Value > 0)
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + ":" + kv.Value.ToString(CultureInfo.InvariantCulture)));

        /// <summary>A text cell: quoted when needed, guarded against formulas.</summary>
        public static string Text(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if ("=+-@".IndexOf(s[0]) >= 0) s = "'" + s;
            bool quote = s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || s[0] == ' ' || s[s.Length - 1] == ' ';
            return quote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
