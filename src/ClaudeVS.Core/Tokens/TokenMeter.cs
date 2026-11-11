using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeVS.Core.Cli;

namespace ClaudeVS.Core.Tokens
{
    public enum AdviceLevel { Info, Warning, Critical }

    public sealed class TokenAdvice
    {
        public AdviceLevel Level { get; set; }
        public string Message { get; set; }
        /// <summary>Optional slash command the UI can offer as a one-click action (e.g. "/compact").</summary>
        public string SuggestedCommand { get; set; }
        public override string ToString() => $"[{Level}] {Message}";
    }

    public sealed class TokenMeterSettings
    {
        public double CompactSuggestPercent { get; set; } = 60;
        public double CompactUrgentPercent { get; set; } = 85;
        /// <summary>A single tool result above this many characters (~4 chars/token) is flagged.</summary>
        public int LargeToolResultChars { get; set; } = 40_000;
        public long DefaultContextWindow { get; set; } = 200_000;
    }

    /// <summary>
    /// Live per-session token accounting fed by <see cref="ClaudeEvent"/>s. Pure logic, no UI.
    /// </summary>
    public sealed class TokenMeter
    {
        private readonly TokenMeterSettings _s;
        private readonly Dictionary<string, string> _toolNames = new Dictionary<string, string>();
        private double _costOffset;
        private double _lastReportedCost;

        public TokenMeter(TokenMeterSettings settings = null) { _s = settings ?? new TokenMeterSettings(); }

        public TokenUsage SessionTotal { get; private set; }
        public TokenUsage LastTurn { get; private set; }
        /// <summary>What the last turn's subagents used, per model (empty when none); see <see cref="SubagentUsageTracker"/>.</summary>
        public IReadOnlyDictionary<string, TokenUsage> LastTurnSubagents { get; private set; } = new Dictionary<string, TokenUsage>();
        /// <summary>All the last turn's subagent tokens together.</summary>
        public TokenUsage LastTurnSubagentTotal => LastTurnSubagents.Values.Aggregate(default(TokenUsage), (a, b) => a + b);
        /// <summary>The last turn's subagents at <see cref="Prices"/>, each at its own model's price.</summary>
        public double LastTurnSubagentCostUsd { get; private set; }
        /// <summary>Subagent tokens in the whole conversation.</summary>
        public long SessionSubagentTokens { get; private set; }
        private readonly SubagentUsageTracker _subagents = new SubagentUsageTracker();
        /// <summary>The last turn's subagent cost by kind of agent, costliest first; null when none ran.</summary>
        public List<AgentTypeCost> LastTurnAgentTypes { get; private set; }
        /// <summary>The conversation's subagent cost by kind of agent (sums of <see cref="LastTurnAgentTypes"/>).</summary>
        public Dictionary<string, double> SessionAgentCostByType { get; } = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly AgentTypeSplit _agentTypes = new AgentTypeSplit();
        public long ContextTokens { get; private set; }
        public long ContextWindow { get; private set; }
        public int Turns { get; private set; }
        public double SessionCostUsd => _costOffset + _lastReportedCost;
        public double LastTurnCostUsd { get; private set; }
        /// <summary>What the prompt cache saved in the last turn and in the whole conversation (USD at <see cref="Prices"/>; can be negative for a turn that only wrote the cache).</summary>
        public double LastTurnCacheSavedUsd { get; private set; }
        public double SessionCacheSavedUsd { get; private set; }
        /// <summary>Prices for the cache saving; the defaults when not set (Options → Pricing).</summary>
        public PriceTable Prices { get; set; }
        private string _turnModel; // the main agent's model in the running turn
        /// <summary>The model of the main agent's last request: the one the conversation is cached for.</summary>
        public string LastModel { get; private set; }
        private static readonly PriceTable DefaultPrices = new PriceTable();

        /// <summary>
        /// The conversation is a new session (not <c>--resume</c>d): its <c>modelUsage</c> starts at 0, so the first
        /// turn's subagents are exact too. Call before its first turn.
        /// </summary>
        public void FreshSession() => _subagents.StartsFromZero();

        /// <summary>
        /// The conversation is resumed and <paramref name="lastReading"/> is its last <c>modelUsage</c> here
        /// (<see cref="ModelUsageReadings"/>): the first turn's subagents are exact when it is still current. Call
        /// before its first turn.
        /// </summary>
        public void ResumedSession(Dictionary<string, TokenUsage> lastReading) => _subagents.ResumeFrom(lastReading);

        /// <summary>One request's cost at <see cref="Prices"/> (Sonnet's when the model is unknown), its long-context tier included.</summary>
        public double RequestCostUsd(string model, TokenUsage request)
        {
            var pt = Prices ?? DefaultPrices;
            return (pt.For(model) ?? pt.Sonnet).Cost(request);
        }
        public int LargestToolResultChars { get; private set; }
        public string LargestToolResultTool { get; private set; }
        public int CompactionsSaved { get; private set; }
        /// <summary>How long the conversation stays in the prompt cache (from the main agent's requests).</summary>
        public CacheWindow Cache { get; } = new CacheWindow();

        private readonly CacheBreakWatch _breaks = new CacheBreakWatch();
        private CacheBreak _pendingBreak;

        /// <summary>The cache break seen since the last call (a warm cache written again), then null; see <see cref="CacheBreakWatch"/>.</summary>
        public CacheBreak TakeCacheBreak() { var b = _pendingBreak; _pendingBreak = null; return b; }

        private readonly Dictionary<string, long> _resultCharsByTool = new Dictionary<string, long>(StringComparer.Ordinal);

        private readonly Dictionary<string, long> _turnResultCharsByTool = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>
        /// Tokens the main agent's tool results added in the last turn, per tool group (<see cref="ToolGroup"/>); null
        /// when none. Logged per turn (<see cref="UsageRecord.ToolResultTokens"/>).
        /// </summary>
        public Dictionary<string, long> LastTurnResultTokensByTool { get; private set; }

        private int _turnToolCalls;

        /// <summary>The main agent's tool calls in the last turn (a subagent's are its own).</summary>
        public int LastTurnToolCalls { get; private set; }

        /// <summary>Turns and cost per effort in this conversation ("high" …, "default" for Claude Code's own), from <see cref="AddTurnEffort"/>.</summary>
        public Dictionary<string, (int Turns, double CostUsd)> SessionByEffort { get; } = new Dictionary<string, (int, double)>(StringComparer.Ordinal);

        /// <summary>After a turn: the effort it was sent with (null: Claude Code's default) gets the turn and its cost (<see cref="LastTurnCostUsd"/>).</summary>
        public void AddTurnEffort(string effort)
        {
            string key = string.IsNullOrWhiteSpace(effort) ? SavingsReport.DefaultEffort : effort.Trim().ToLowerInvariant();
            var had = SessionByEffort.TryGetValue(key, out var v) ? v : (0, 0.0);
            SessionByEffort[key] = (had.Item1 + 1, had.Item2 + LastTurnCostUsd);
        }

        /// <summary>
        /// Tokens the main agent's tool results added to the conversation, per tool (shell commands and searches
        /// grouped, MCP tools as "MCP"), largest first.
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, long>> ResultTokensByTool() =>
            _resultCharsByTool.OrderByDescending(kv => kv.Value).Select(kv => new KeyValuePair<string, long>(kv.Key, TokenEstimate.FromChars(kv.Value))).ToList();

        /// <summary>
        /// "Prompt cache: 94% of input read from the cache, saved $0.42 in this conversation (API prices)." for the
        /// meter's tooltip; null before the first turn.
        /// </summary>
        public string CacheSavedLine()
        {
            if (Turns == 0) return null;
            var c = System.Globalization.CultureInfo.InvariantCulture;
            string share = $"Prompt cache: {CacheHitPercent.ToString("0", c)}% of input read from the cache, ";
            return SessionCacheSavedUsd >= 0
                ? share + $"saved ${SessionCacheSavedUsd.ToString("0.00", c)} in this conversation (API prices)."
                : share + $"writing it cost ${(-SessionCacheSavedUsd).ToString("0.00", c)} more than it saved so far (it pays off on the next prompts).";
        }

        /// <summary>"Shell" for Bash / PowerShell, "Search" for Grep / Glob, "MCP" for mcp__ tools, else the tool name.</summary>
        public static string ToolGroup(string tool)
        {
            switch (tool)
            {
                case "Bash": case "PowerShell": return "Shell";
                case "Grep": case "Glob": return "Search";
                case "Task": return "Agent";
                default: return tool != null && tool.StartsWith("mcp__", StringComparison.Ordinal) ? "MCP" : tool ?? "tool";
            }
        }

        public double ContextPercent => (ContextWindow > 0 ? ContextWindow : _s.DefaultContextWindow) == 0 ? 0
            : 100.0 * ContextTokens / (ContextWindow > 0 ? ContextWindow : _s.DefaultContextWindow);

        /// <summary>Share of input tokens served from the prompt cache (higher = cheaper).</summary>
        public double CacheHitPercent
        {
            get
            {
                long denom = SessionTotal.Input + SessionTotal.CacheRead + SessionTotal.CacheCreation;
                return denom == 0 ? 0 : 100.0 * SessionTotal.CacheRead / denom;
            }
        }

        public event EventHandler Changed;

        public void Reset()
        {
            SessionTotal = default; LastTurn = default; ContextTokens = 0; ContextWindow = 0; Turns = 0;
            _costOffset = 0; _lastReportedCost = 0; LastTurnCostUsd = 0; LargestToolResultChars = 0; LargestToolResultTool = null;
            _toolNames.Clear();
            _resultCharsByTool.Clear();
            _turnResultCharsByTool.Clear(); LastTurnResultTokensByTool = null;
            _turnToolCalls = 0; LastTurnToolCalls = 0;
            SessionByEffort.Clear();
            Cache.Reset();
            _breaks.Reset(); _pendingBreak = null;
            Learner.Reset();
            _subagents.Reset(); _agentTypes.Reset(); LastTurnAgentTypes = null; SessionAgentCostByType.Clear(); LastTurnSubagents = new Dictionary<string, TokenUsage>(); LastTurnSubagentCostUsd = 0; SessionSubagentTokens = 0;
            _turnLargeByMessage.Clear();
            LastTurnCacheSavedUsd = 0; SessionCacheSavedUsd = 0; _turnModel = null; LastModel = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // This turn's main-agent requests over the long-context tier: tokens by message id
        private readonly Dictionary<string, long> _turnLargeByMessage = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Learns the characters-per-token estimate from this conversation's turns (<see cref="TokenEstimate"/>).</summary>
        public PromptTokenLearner Learner { get; } = new PromptTokenLearner();

        public void Observe(ClaudeEvent ev)
        {
            Learner.Observe(ev);
            _agentTypes.Observe(ev, RequestCostUsd);
            switch (ev.Kind)
            {
                case ClaudeEventKind.AssistantUsage when ev.IsSubagent:
                    _subagents.ObserveSubagent(ev);
                    return;
                case ClaudeEventKind.AssistantUsage when !ev.IsSubagent:
                    // A request over the long-context tier: its tokens, by message (the last copy of a message's usage is final)
                    var req = ev.Usage.AsRequest();
                    _turnLargeByMessage[ev.MessageId ?? "m" + _turnLargeByMessage.Count] = req.LargeContextTokens;
                    _pendingBreak = _breaks.Observe(req, ev.Model, ev.MessageId, DateTime.UtcNow, Prices ?? DefaultPrices) ?? _pendingBreak;
                    Cache.Use(ev.Usage, DateTime.UtcNow);
                    if (!string.IsNullOrEmpty(ev.Model)) _turnModel = LastModel = ev.Model;
                    ContextTokens = ev.Usage.ContextTokens; // live update while a long turn runs
                    break;
                case ClaudeEventKind.ToolUse:
                    if (ev.ToolUseId != null) _toolNames[ev.ToolUseId] = ev.ToolName;
                    if (!ev.IsSubagent) _turnToolCalls++;
                    return;
                case ClaudeEventKind.ToolResult:
                    if (!ev.IsSubagent && ev.ResultChars > 0 && ev.ToolUseId != null && _toolNames.TryGetValue(ev.ToolUseId, out var tool))
                    {
                        string group = ToolGroup(tool);
                        _resultCharsByTool[group] = (_resultCharsByTool.TryGetValue(group, out var sofar) ? sofar : 0) + ev.ResultChars;
                        _turnResultCharsByTool[group] = (_turnResultCharsByTool.TryGetValue(group, out var inTurn) ? inTurn : 0) + ev.ResultChars;
                    }
                    if (ev.ResultChars > LargestToolResultChars)
                    {
                        LargestToolResultChars = ev.ResultChars;
                        LargestToolResultTool = ev.ToolUseId != null && _toolNames.TryGetValue(ev.ToolUseId, out var n) ? n : "tool";
                    }
                    return;
                case ClaudeEventKind.Compacted:
                    CompactionsSaved++;
                    _breaks.Compacted();
                    ContextTokens = 0;
                    break;
                case ClaudeEventKind.SessionStarted:
                    return;
                case ClaudeEventKind.TurnCompleted:
                    Turns++;
                    // The turn's usage sums all its requests; which were over the long-context tier only the requests say
                    var turn = ev.Usage;
                    turn.LargeContextTokens = Math.Min(turn.Total, _turnLargeByMessage.Values.Sum());
                    _turnLargeByMessage.Clear();
                    LastTurnResultTokensByTool = _turnResultCharsByTool.Count == 0 ? null
                        : _turnResultCharsByTool.ToDictionary(kv => kv.Key, kv => (long)TokenEstimate.FromChars(kv.Value), StringComparer.Ordinal);
                    _turnResultCharsByTool.Clear();
                    LastTurnToolCalls = _turnToolCalls; _turnToolCalls = 0;
                    LastTurn = turn;
                    LastTurnSubagents = _subagents.TurnCompleted(ev.ModelUsage, _turnModel, turn);
                    var pt = Prices ?? DefaultPrices;
                    LastTurnSubagentCostUsd = LastTurnSubagents.Sum(kv => (pt.For(kv.Key) ?? pt.Sonnet).Cost(kv.Value)); // a sum of requests: no per-request tier
                    SessionSubagentTokens += LastTurnSubagentTotal.Total;
                    LastTurnAgentTypes = _agentTypes.TurnCompleted(LastTurnSubagentCostUsd);
                    foreach (var k in LastTurnAgentTypes ?? new List<AgentTypeCost>())
                        SessionAgentCostByType[k.Type] = (SessionAgentCostByType.TryGetValue(k.Type, out var had) ? had : 0) + k.CostUsd;
                    // Priced at the model that answered (Sonnet when unknown).
                    var prices = Prices ?? DefaultPrices;
                    LastTurnCacheSavedUsd = (prices.For(_turnModel) ?? prices.Sonnet).CacheSavedUsd(turn);
                    SessionCacheSavedUsd += LastTurnCacheSavedUsd;
                    _turnModel = null;
                    SessionTotal += turn;
                    if (!ev.LastRequestUsage.IsEmpty) ContextTokens = ev.LastRequestUsage.ContextTokens;
                    if (ev.ContextWindow.HasValue && ev.ContextWindow.Value > 0) ContextWindow = ev.ContextWindow.Value;
                    if (ev.TotalCostUsd.HasValue)
                    {
                        // total_cost_usd is cumulative. With 2.1.322 a relaunch (--resume) carries on the session's total;
                        // should a CLI start it again at 0, the lower value adds an offset, so both add up.
                        double reported = ev.TotalCostUsd.Value;
                        double before = SessionCostUsd;
                        if (reported + 1e-9 < _lastReportedCost) _costOffset += _lastReportedCost;
                        _lastReportedCost = reported;
                        LastTurnCostUsd = Math.Max(0, SessionCostUsd - before);
                    }
                    break;
                default:
                    return;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// The CLI's own measurement (<c>get_context_usage</c>, fetched after each turn). It also covers what the
        /// usage numbers miss, such as the context right after /compact, which they would show as 0.
        /// </summary>
        public void ApplyContextUsage(long totalTokens, long maxTokens)
        {
            if (totalTokens <= 0) return;
            ContextTokens = totalTokens;
            if (maxTokens > 0) ContextWindow = maxTokens;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public IReadOnlyList<TokenAdvice> GetAdvice()
        {
            var list = new List<TokenAdvice>();
            double pct = ContextPercent;
            if (pct >= _s.CompactUrgentPercent)
                list.Add(new TokenAdvice { Level = AdviceLevel.Critical, Message = $"Context {pct:0}% full. Every request re-reads it — compact now, or start a new chat if the topic changed.", SuggestedCommand = "/compact" });
            else if (pct >= _s.CompactSuggestPercent)
                list.Add(new TokenAdvice { Level = AdviceLevel.Warning, Message = $"Context {pct:0}% full. /compact keeps the summary and drops the rest.", SuggestedCommand = "/compact" });

            if (LargestToolResultChars >= _s.LargeToolResultChars)
                list.Add(new TokenAdvice { Level = AdviceLevel.Warning, Message = $"A {LargestToolResultTool} result was ~{TokenEstimate.FromChars(LargestToolResultChars):N0} tokens. Install the output-trimming hooks or ask for narrower reads (line ranges, grep)." });

            if (Turns >= 4 && CacheHitPercent < 50)
                list.Add(new TokenAdvice { Level = AdviceLevel.Info, Message = $"Cache hit rate only {CacheHitPercent:0}%. Switching model or editing CLAUDE.md mid-chat resets the cache." });

            return list;
        }

        public static string Format(long tokens)
        {
            if (tokens >= 1_000_000) return (tokens / 1_000_000.0).ToString("0.0") + "M";
            if (tokens >= 1_000) return (tokens / 1_000.0).ToString("0.0") + "k";
            return tokens.ToString();
        }
    }
}
