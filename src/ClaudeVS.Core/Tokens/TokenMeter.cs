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
        private static readonly PriceTable DefaultPrices = new PriceTable();
        public int LargestToolResultChars { get; private set; }
        public string LargestToolResultTool { get; private set; }
        public int CompactionsSaved { get; private set; }
        /// <summary>How long the conversation stays in the prompt cache (from the main agent's requests).</summary>
        public CacheWindow Cache { get; } = new CacheWindow();

        private readonly Dictionary<string, long> _resultCharsByTool = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>
        /// Tokens the main agent's tool results added to the conversation, per tool (shell commands and searches
        /// grouped, MCP tools as "MCP"), largest first.
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, long>> ResultTokensByTool() =>
            _resultCharsByTool.OrderByDescending(kv => kv.Value).Select(kv => new KeyValuePair<string, long>(kv.Key, kv.Value / 4)).ToList();

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
            Cache.Reset();
            LastTurnCacheSavedUsd = 0; SessionCacheSavedUsd = 0; _turnModel = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Observe(ClaudeEvent ev)
        {
            switch (ev.Kind)
            {
                case ClaudeEventKind.AssistantUsage when !ev.IsSubagent:
                    Cache.Use(ev.Usage, DateTime.UtcNow);
                    if (!string.IsNullOrEmpty(ev.Model)) _turnModel = ev.Model;
                    ContextTokens = ev.Usage.ContextTokens; // live update while a long turn runs
                    break;
                case ClaudeEventKind.ToolUse:
                    if (ev.ToolUseId != null) _toolNames[ev.ToolUseId] = ev.ToolName;
                    return;
                case ClaudeEventKind.ToolResult:
                    if (!ev.IsSubagent && ev.ResultChars > 0 && ev.ToolUseId != null && _toolNames.TryGetValue(ev.ToolUseId, out var tool))
                    {
                        string group = ToolGroup(tool);
                        _resultCharsByTool[group] = (_resultCharsByTool.TryGetValue(group, out var sofar) ? sofar : 0) + ev.ResultChars;
                    }
                    if (ev.ResultChars > LargestToolResultChars)
                    {
                        LargestToolResultChars = ev.ResultChars;
                        LargestToolResultTool = ev.ToolUseId != null && _toolNames.TryGetValue(ev.ToolUseId, out var n) ? n : "tool";
                    }
                    return;
                case ClaudeEventKind.Compacted:
                    CompactionsSaved++;
                    ContextTokens = 0;
                    break;
                case ClaudeEventKind.SessionStarted:
                    return;
                case ClaudeEventKind.TurnCompleted:
                    Turns++;
                    LastTurn = ev.Usage;
                    // Priced at the model that answered (Sonnet when unknown); the turn's usage sums all its requests.
                    var prices = Prices ?? DefaultPrices;
                    LastTurnCacheSavedUsd = (prices.For(_turnModel) ?? prices.Sonnet).CacheSavedUsd(ev.Usage);
                    SessionCacheSavedUsd += LastTurnCacheSavedUsd;
                    _turnModel = null;
                    SessionTotal += ev.Usage;
                    if (!ev.LastRequestUsage.IsEmpty) ContextTokens = ev.LastRequestUsage.ContextTokens;
                    if (ev.ContextWindow.HasValue && ev.ContextWindow.Value > 0) ContextWindow = ev.ContextWindow.Value;
                    if (ev.TotalCostUsd.HasValue)
                    {
                        // total_cost_usd is cumulative for the CLI process. A relaunch (--resume) restarts it at 0.
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
                list.Add(new TokenAdvice { Level = AdviceLevel.Warning, Message = $"A {LargestToolResultTool} result was ~{LargestToolResultChars / 4:N0} tokens. Install the output-trimming hooks or ask for narrower reads (line ranges, grep)." });

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
