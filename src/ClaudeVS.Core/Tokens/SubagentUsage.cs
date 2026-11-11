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

    /// <summary>
    /// Which kinds of subagent (Explore, general-purpose, Fork, a custom agent) a turn's subagent cost went to. Each
    /// Agent call's type comes from its input (or its <c>task_started</c>); its requests (<c>parent_tool_use_id</c>)
    /// are priced one by one as they stream. Those prices only give the shares: streamed output can be low, so the
    /// shares split the turn's exact subagent cost (<see cref="TokenMeter.LastTurnSubagentCostUsd"/>).
    /// </summary>
    public sealed class AgentTypeSplit
    {
        private readonly Dictionary<string, string> _typeByCall = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly AgentRequestCosts _turn = new AgentRequestCosts();

        public void Reset() { _typeByCall.Clear(); _turn.Clear(); }

        public void Observe(ClaudeEvent ev, Func<string, TokenUsage, double> price)
        {
            switch (ev.Kind)
            {
                case ClaudeEventKind.ToolUse when (ev.ToolName == "Agent" || ev.ToolName == "Task") && ev.ToolUseId != null:
                    _typeByCall[ev.ToolUseId] = ToolCallView.AgentLabel((string)ev.ToolInput?["subagent_type"]);
                    break;
                case ClaudeEventKind.Task when ev.Payload != null && ev.Payload.Value<string>("subtype") == "task_started":
                {
                    string id = ev.Payload.Value<string>("tool_use_id"), type = ev.Payload.Value<string>("subagent_type");
                    if (id != null && type != null) _typeByCall[id] = ToolCallView.AgentLabel(type); // the CLI's own word: it wins over the call's input
                    break;
                }
                case ClaudeEventKind.AssistantUsage when ev.IsSubagent:
                    _turn.Observe(ev, price);
                    break;
            }
        }

        /// <summary>
        /// The turn ended: <paramref name="agentsCostUsd"/> (its exact subagent cost) split by type, costliest first;
        /// null when no subagent request was seen. An Agent call of unknown type counts as "Agent".
        /// </summary>
        public List<AgentTypeCost> TurnCompleted(double agentsCostUsd)
        {
            var byType = _turn.Calls
                .GroupBy(c => _typeByCall.TryGetValue(c, out var t) ? t : "Agent", StringComparer.Ordinal)
                .Select(g => (Type: g.Key, Priced: g.Sum(_turn.Of)))
                .ToList();
            _turn.Clear();
            double priced = byType.Sum(t => t.Priced);
            if (priced <= 0) return null;
            double target = agentsCostUsd > 0 ? agentsCostUsd : priced;
            return byType.Where(t => t.Priced > 0)
                .Select(t => new AgentTypeCost { Type = t.Type, Share = t.Priced / priced, CostUsd = target * t.Priced / priced })
                .OrderByDescending(t => t.CostUsd).ToList();
        }
    }

    /// <summary>One kind of subagent's part of a turn's subagent cost.</summary>
    public sealed class AgentTypeCost
    {
        [Newtonsoft.Json.JsonProperty("type")] public string Type { get; set; }
        /// <summary>Its part of the turn's subagent cost, 0–1: the report applies it to the cost at its own prices.</summary>
        [Newtonsoft.Json.JsonProperty("share")] public double Share { get; set; }
        [Newtonsoft.Json.JsonIgnore] public double CostUsd { get; set; }
    }
}
