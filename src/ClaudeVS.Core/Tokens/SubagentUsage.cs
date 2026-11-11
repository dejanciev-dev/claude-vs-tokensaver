using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeVS.Core.Cli;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// What a turn's subagents used, per model. The result's <c>usage</c> is the main agent's only, while its
    /// <c>modelUsage</c> counts everything the session used so far, per model (Fixtures/cli-2.1.319-fork.jsonl):
    /// the growth of <c>modelUsage</c> over the turn, less the main agent's usage, is exactly the subagents'. After
    /// <c>--resume</c> the CLI carries <c>modelUsage</c> on (seen with 2.1.322), so growth is only taken once an earlier
    /// reading exists, or the session is new (<see cref="StartsFromZero"/>: counted from 0). On a resumed session's
    /// first turn the subagents' own streamed messages are added up instead (input and cache
    /// exact; output can be low, as a message streams before it is finished).
    /// </summary>
    public sealed class SubagentUsageTracker
    {
        private readonly Dictionary<string, TokenUsage> _previous = new Dictionary<string, TokenUsage>(StringComparer.Ordinal);
        // This turn's subagent messages: id → (model, usage); the last copy of a message's usage is kept
        private readonly Dictionary<string, (string Model, TokenUsage Usage)> _streamed = new Dictionary<string, (string, TokenUsage)>(StringComparer.Ordinal);

        // A new session: modelUsage counts from 0, so its first reading is all growth (exact, as in the fork capture)
        private bool _fromZero;

        public void Reset() { _previous.Clear(); _streamed.Clear(); _fromZero = false; }

        /// <summary>The session is new (not resumed): take <c>modelUsage</c>'s growth from 0 on its first turn.</summary>
        public void StartsFromZero() { if (_previous.Count == 0) _fromZero = true; }

        /// <summary>A subagent's AssistantUsage.</summary>
        public void ObserveSubagent(ClaudeEvent ev)
        {
            if (!ev.IsSubagent || ev.Usage.IsEmpty || string.IsNullOrEmpty(ev.Model)) return;
            _streamed[ev.MessageId ?? "s" + _streamed.Count] = (ev.Model, ev.Usage);
        }

        /// <summary>
        /// The turn ended: the subagents' usage per model (empty when none). <paramref name="mainModel"/> answered the
        /// main agent's <paramref name="mainUsage"/>.
        /// </summary>
        public Dictionary<string, TokenUsage> TurnCompleted(Dictionary<string, ModelUsageEntry> modelUsage, string mainModel, TokenUsage mainUsage)
        {
            var result = new Dictionary<string, TokenUsage>(StringComparer.Ordinal);
            bool exact = (_previous.Count > 0 || _fromZero) && modelUsage != null && modelUsage.Count > 0;
            _fromZero = false;
            if (exact)
            {
                foreach (var kv in modelUsage)
                {
                    var now = kv.Value.Usage;
                    var grew = _previous.TryGetValue(kv.Key, out var before) && !Lower(now, before) ? TokenUsage.Minus(now, before) : now; // lower: counting started again
                    if (kv.Key == mainModel) grew = TokenUsage.Minus(grew, mainUsage);
                    if (grew.Total > 0) result[kv.Key] = grew;
                }
            }
            else
            {
                foreach (var g in _streamed.Values.GroupBy(s => s.Model))
                {
                    var sum = default(TokenUsage);
                    foreach (var s in g) sum += s.Usage;
                    if (sum.Total > 0) result[g.Key] = sum;
                }
            }
            _streamed.Clear();
            if (modelUsage != null) foreach (var kv in modelUsage) _previous[kv.Key] = kv.Value.Usage;
            return result;
        }

        private static bool Lower(TokenUsage now, TokenUsage before) =>
            now.Input < before.Input || now.Output < before.Output || now.CacheRead < before.CacheRead || now.CacheCreation < before.CacheCreation;
    }
}
