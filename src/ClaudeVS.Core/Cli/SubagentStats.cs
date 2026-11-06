using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    /// <summary>
    /// The turn's subagents, from <c>subagent_stats</c> in the <c>result</c> message (2.1.319):
    /// <c>{ spawned, completed, failed, killed{parent,user,system}, refused{depth_limit,concurrency_limit,budget}, by_type{fork:1} }</c>.
    /// </summary>
    public sealed class SubagentStats
    {
        public int Spawned { get; private set; }
        public int Completed { get; private set; }
        public int Failed { get; private set; }
        /// <summary>Killed by the system (not by you or the parent agent).</summary>
        public int KilledBySystem { get; private set; }
        /// <summary>Refusals by reason ("depth_limit", "concurrency_limit", "budget"), only those above zero.</summary>
        public IReadOnlyDictionary<string, int> Refused { get; private set; } = new Dictionary<string, int>();

        /// <summary>Null when the field is missing or not an object (older CLIs).</summary>
        public static SubagentStats Parse(JToken token)
        {
            if (!(token is JObject s)) return null;
            int N(JToken t) => t != null && t.Type == JTokenType.Integer ? (int)t : 0;
            return new SubagentStats
            {
                Spawned = N(s["spawned"]),
                Completed = N(s["completed"]),
                Failed = N(s["failed"]),
                KilledBySystem = N(s["killed"]?["system"]),
                Refused = (s["refused"] as JObject)?.Properties().Where(p => N(p.Value) > 0).ToDictionary(p => p.Name, p => N(p.Value))
                          ?? new Dictionary<string, int>(),
            };
        }

        /// <summary>"1 agent" / "3 agents" for the per-turn footer; null when the turn started none.</summary>
        public string FooterNote() => Spawned <= 0 ? null : Spawned == 1 ? "1 agent" : $"{Spawned} agents";

        /// <summary>
        /// One line when something went wrong with the turn's subagents: refused by Claude Code (and why), failed, or
        /// stopped by the system. Null when all is well.
        /// </summary>
        public string Warning()
        {
            var parts = new List<string>();
            int refused = Refused.Values.Sum();
            if (refused > 0)
                parts.Add($"refused {Agents(refused)} ({string.Join(", ", Refused.Select(r => ReasonText(r.Key)))})");
            if (Failed > 0) parts.Add($"{Agents(Failed)} failed");
            if (KilledBySystem > 0) parts.Add($"stopped {Agents(KilledBySystem)}");
            if (parts.Count == 0) return null;
            string text = string.Join("; ", parts);
            return "Claude Code " + text + ".";
        }

        private static string Agents(int n) => n == 1 ? "1 subagent" : $"{n} subagents";

        private static string ReasonText(string reason)
        {
            switch (reason)
            {
                case "depth_limit": return "too deeply nested";
                case "concurrency_limit": return "too many at once";
                case "budget": return "over the budget";
                default: return reason.Replace('_', ' ');
            }
        }
    }
}
