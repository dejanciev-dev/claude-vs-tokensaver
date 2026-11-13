using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ClaudeVS.Core.Cli;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>USD per million tokens for one model family.</summary>
    public sealed class ModelPrice
    {
        public double Input { get; set; }
        public double Output { get; set; }
        public double CacheWrite { get; set; }
        public double CacheRead { get; set; }

        /// <summary>
        /// Fast mode bills every token type at this multiple of the standard price (Opus 5.5: $8 / $40 instead of
        /// $4 / $20; Fable 5.2: $16 / $80); the cache multipliers apply on top. Source: the API's fast mode page, 2026-11.
        /// </summary>
        public const double FastMultiplier = 2.0;

        /// <summary>
        /// The price multiple for tokens of which <paramref name="fastTokens"/> of <paramref name="totalTokens"/> ran in
        /// fast mode: 1 for none, <see cref="FastMultiplier"/> for all, in between by token share for a mixed sum.
        /// </summary>
        public static double SpeedMultiplier(long fastTokens, long totalTokens) =>
            fastTokens <= 0 || totalTokens <= 0 ? 1.0 : 1.0 + (FastMultiplier - 1.0) * Math.Min(1.0, (double)fastTokens / totalTokens);

        /// <summary>
        /// A request with more input than this (fresh input + cache reads + cache writes) is billed at
        /// <see cref="LongContextMultiplier"/>× every price; 0 = no such tier. Haiku 5.5: over 100k, 5× ($0.50 / $2.50,
        /// cache read $0.05), measured with Claude Code 2.1.322 (Fixtures/cli-2.1.322-haiku-over-100k.jsonl).
        /// </summary>
        public long LongContextFrom { get; set; }
        public double LongContextMultiplier { get; set; } = 1;

        /// <summary>For one request with <paramref name="inputTokens"/> of input: <see cref="LongContextMultiplier"/> above the tier, else 1.</summary>
        public double RequestMultiplier(long inputTokens) =>
            LongContextFrom > 0 && LongContextMultiplier > 1 && inputTokens > LongContextFrom ? LongContextMultiplier : 1.0;

        /// <summary>For a sum of requests: by the share of tokens from requests over <see cref="Cli.TokenUsage.LargeContextFrom"/>.</summary>
        public double ContextMultiplier(long largeTokens, long totalTokens) =>
            LongContextFrom <= 0 || LongContextMultiplier <= 1 || largeTokens <= 0 || totalTokens <= 0 ? 1.0
                : 1.0 + (LongContextMultiplier - 1.0) * Math.Min(1.0, (double)largeTokens / totalTokens);

        /// <summary>Everything a logged turn's tokens are multiplied by: fast mode and the long-context tier.</summary>
        public double Multiplier(UsageRecord r) => r.SpeedMultiplier * ContextMultiplier(r.LargeContextTokens, r.TotalTokens);

        public double Cost(UsageRecord r) =>
            (r.InputTokens * Input + r.OutputTokens * Output + r.CacheWriteTokens * CacheWrite + r.CacheReadTokens * CacheRead) / 1_000_000.0 * Multiplier(r);

        /// <summary>
        /// One request's (or a sum of requests') cost: 1-hour cache writes at twice the input price, the rest at
        /// <see cref="CacheWrite"/>; fast tokens at <see cref="FastMultiplier"/>×, tokens of requests over the
        /// long-context tier at <see cref="LongContextMultiplier"/>× (see <see cref="Cli.TokenUsage.LargeContextTokens"/>).
        /// </summary>
        public double Cost(Cli.TokenUsage u)
        {
            long write1h = Math.Min(Math.Max(0, u.CacheCreation1h), Math.Max(0, u.CacheCreation));
            long write5m = Math.Max(0, u.CacheCreation) - write1h;
            return (u.Input * Input + u.Output * Output + write5m * CacheWrite + write1h * 2 * Input + u.CacheRead * CacheRead) / 1_000_000.0
                * SpeedMultiplier(u.FastTokens, u.Total) * ContextMultiplier(u.LargeContextTokens, u.Total);
        }

        /// <summary>
        /// What the prompt cache saved: every token read from it costs <see cref="CacheRead"/> instead of the full
        /// <see cref="Input"/> price, minus the extra for writing it (a 5-minute write costs <see cref="CacheWrite"/>,
        /// a 1-hour write twice the input price). Negative when a turn wrote more than it read (the first turn).
        /// </summary>
        public double CacheSavedUsd(long cacheRead, long cacheWrite, long cacheWrite1h)
        {
            long write1h = Math.Min(Math.Max(0, cacheWrite1h), Math.Max(0, cacheWrite));
            long write5m = Math.Max(0, cacheWrite) - write1h;
            double saved = cacheRead * (Input - CacheRead) - write5m * (CacheWrite - Input) - write1h * Input; // 1h write = 2x input
            return saved / 1_000_000.0;
        }

        /// <summary>As above; in fast mode every price doubles, and so does what the cache saves.</summary>
        public double CacheSavedUsd(Cli.TokenUsage u) => CacheSavedUsd(u.CacheRead, u.CacheCreation, u.CacheCreation1h)
            * SpeedMultiplier(u.FastTokens, u.Total) * ContextMultiplier(u.LargeContextTokens, u.Total);

        public double CacheSavedUsd(UsageRecord r) => CacheSavedUsd(r.CacheReadTokens, r.CacheWriteTokens, r.CacheWrite1hTokens) * Multiplier(r);

        /// <summary>Parses "input, output, cacheWrite, cacheRead" (invariant culture). Returns null when malformed.</summary>
        public static ModelPrice Parse(string text)
        {
            var parts = (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToArray();
            if (parts.Length != 4) return null;
            var v = new double[4];
            for (int i = 0; i < 4; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || v[i] < 0) return null;
            return new ModelPrice { Input = v[0], Output = v[1], CacheWrite = v[2], CacheRead = v[3] };
        }

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "{0}, {1}, {2}, {3}", Input, Output, CacheWrite, CacheRead);
    }

    /// <summary>
    /// Prices for the model families behind Claude Code's haiku / sonnet / opus / fable aliases.
    /// Defaults are Anthropic API list prices as of 2026-11 (Opus 5.5, Sonnet 5.5, Haiku 5.5, Fable 5.2);
    /// cache write = 1.25x input. Users can override them in Options when prices change. Checked against Claude
    /// Code 2.1.322's own costUSD for two real turns per model (Fixtures/cli-2.1.322-model-costs.jsonl): all match.
    /// </summary>
    public sealed class PriceTable
    {
        public const string DefaultOpus = "4, 20, 5, 0.2";
        /// <summary>Sonnet 5.5: cache reads are $0.10, half the other models' (the CLI's costUSD shows it).</summary>
        public const string DefaultSonnet = "2, 10, 2.5, 0.1";
        /// <summary>Defaults shipped before, for <see cref="UpgradeStored"/>.</summary>
        private static readonly string[] OldSonnetDefaults = { "2, 10, 2.5, 0.2" };
        public const string DefaultHaiku = "0.1, 0.5, 0.125, 0.01";
        /// <summary>Fable 5.2 (Claude Code 2.1.321): $8 / $40 per Mtok, cache read $0.20.</summary>
        public const string DefaultFable = "8, 40, 10, 0.2";

        public ModelPrice Opus { get; set; } = ModelPrice.Parse(DefaultOpus);
        public ModelPrice Sonnet { get; set; } = ModelPrice.Parse(DefaultSonnet);
        public ModelPrice Haiku { get; set; } = HaikuTier(ModelPrice.Parse(DefaultHaiku));
        public ModelPrice Fable { get; set; } = ModelPrice.Parse(DefaultFable);

        /// <summary>Haiku 5.5 bills a request over 100k input tokens at 5x every price (measured with 2.1.322).</summary>
        private static ModelPrice HaikuTier(ModelPrice p) { p.LongContextFrom = Cli.TokenUsage.LargeContextFrom; p.LongContextMultiplier = 5; return p; }

        /// <summary>
        /// A Sonnet price saved in Options that is an earlier default (nobody chose it: Options saves every value on
        /// OK) becomes the current default; a price you typed stays. Compared by value, so spacing does not matter.
        /// </summary>
        public static string UpgradeStored(string storedSonnet)
        {
            var stored = ModelPrice.Parse(storedSonnet);
            if (stored == null) return storedSonnet;
            foreach (var old in OldSonnetDefaults)
            {
                var o = ModelPrice.Parse(old);
                if (o.Input == stored.Input && o.Output == stored.Output && o.CacheWrite == stored.CacheWrite && o.CacheRead == stored.CacheRead)
                    return DefaultSonnet;
            }
            return storedSonnet;
        }

        public static PriceTable FromStrings(string opus, string sonnet, string haiku, string fable = null) => new PriceTable
        {
            Opus = ModelPrice.Parse(opus) ?? ModelPrice.Parse(DefaultOpus),
            Sonnet = ModelPrice.Parse(sonnet) ?? ModelPrice.Parse(DefaultSonnet),
            Haiku = HaikuTier(ModelPrice.Parse(haiku) ?? ModelPrice.Parse(DefaultHaiku)),
            Fable = ModelPrice.Parse(fable) ?? ModelPrice.Parse(DefaultFable),
        };

        /// <summary>"opus", "sonnet", "haiku", "fable" or null, from a model id such as "claude-sonnet-5-5" or an alias.</summary>
        public static string Family(string model)
        {
            string m = (model ?? "").ToLowerInvariant();
            if (m.Contains("opus")) return "opus";
            if (m.Contains("sonnet")) return "sonnet";
            if (m.Contains("haiku")) return "haiku";
            if (m.Contains("fable")) return "fable";
            return null;
        }

        public ModelPrice For(string model)
        {
            switch (Family(model))
            {
                case "opus": return Opus;
                case "sonnet": return Sonnet;
                case "haiku": return Haiku;
                case "fable": return Fable;
                default: return null;
            }
        }

        /// <summary>
        /// "2× the Opus price" for a model dearer than Opus (by its output price; Fable 5.2: $40 vs $20), else null.
        /// Said where you pick it by hand, since the routers never do.
        /// </summary>
        public string VersusOpus(string model)
        {
            var p = For(model);
            if (p == null || Opus == null || Opus.Output <= 0 || Family(model) == "opus") return null;
            double x = p.Output / Opus.Output;
            if (x < 1.05) return null;
            return (Math.Abs(x - Math.Round(x)) < 0.05 ? Math.Round(x).ToString("0", CultureInfo.InvariantCulture) : x.ToString("0.0", CultureInfo.InvariantCulture)) + "× the Opus price";
        }
    }

    /// <summary>One local day of the savings report.</summary>
    public sealed class DayBreakdown
    {
        public DateTime Day { get; set; }
        public int Turns { get; set; }
        public long Tokens { get; set; }
        /// <summary>Including what asking the Haiku router cost.</summary>
        public double CostUsd { get; set; }
        public double CacheSavedUsd { get; set; }
        public double CacheHitPercent { get; set; }
    }

    /// <summary>One solution folder of the savings report.</summary>
    public sealed class SolutionBreakdown
    {
        /// <summary>The folder; null for turns logged without one.</summary>
        public string Solution { get; set; }
        public int Turns { get; set; }
        /// <summary>Including what asking the Haiku router cost.</summary>
        public double CostUsd { get; set; }
        /// <summary>Spent this calendar month up to the report, whatever the period, router included.</summary>
        public double MonthUsd { get; set; }
        /// <summary>The folder's name ("linq-samples"), or "(no folder)".</summary>
        public string Name => NameOf(Solution);

        public static string NameOf(string folder) =>
            string.IsNullOrEmpty(folder) ? "(no folder)" : System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')) is string n && n.Length > 0 ? n : folder;
    }

    /// <summary>One conversation (a session id in the usage log) of the savings report.</summary>
    /// <summary>The turns of one local weekday, in the savings report.</summary>
    public sealed class WeekdayBreakdown
    {
        public DayOfWeek Day { get; set; }
        public int Turns { get; set; }
        /// <summary>Including what asking the Haiku router cost.</summary>
        public double CostUsd { get; set; }
    }

    /// <summary>Cache rewrites of one likely cause: how many, and what they cost more than reading the cache.</summary>
    public sealed class RewriteCauseBreakdown
    {
        public int Turns { get; set; }
        public double ExtraUsd { get; set; }
    }

    public sealed class ConversationBreakdown
    {
        public string SessionId { get; set; }
        /// <summary>Its first logged turn (UTC).</summary>
        public DateTime Started { get; set; }
        public int Turns { get; set; }
        /// <summary>Including what asking the Haiku router cost.</summary>
        public double CostUsd { get; set; }
        public string Solution { get; set; }
        /// <summary>The first prompt logged for it (only with Options → Log prompt preview); else null.</summary>
        public string FirstPrompt { get; set; }
        /// <summary>Its turns that wrote it to the cache again (<see cref="SavingsReport.IsCacheRewrite"/>).</summary>
        public int CacheRewrites { get; set; }
        /// <summary>Its turns that ended in an error.</summary>
        public int Errors { get; set; }

        /// <summary>The model families its turns ran on, by turns, most first ("other" for unknown models).</summary>
        public List<KeyValuePair<string, int>> Models { get; } = new List<KeyValuePair<string, int>>();

        /// <summary>From its first turn's start (logged time less duration) to its last turn's end.</summary>
        public TimeSpan Span { get; set; }

        /// <summary>"over 25 min", "over 2 h 10 min", "over 3 days"; null under a minute.</summary>
        public static string SpanText(TimeSpan span)
        {
            if (span < TimeSpan.FromMinutes(1)) return null;
            if (span >= TimeSpan.FromDays(2)) return $"over {(int)span.TotalDays} days";
            int h = (int)span.TotalHours, m = span.Minutes;
            return h == 0 ? $"over {m} min" : m == 0 ? $"over {h} h" : $"over {h} h {m} min";
        }

        /// <summary>Fills <see cref="Models"/> and <see cref="Span"/> from its turns, oldest first; returns this.</summary>
        public ConversationBreakdown WithTurns(IEnumerable<UsageRecord> turns)
        {
            var list = turns.ToList();
            if (list.Count > 0)
            {
                var start = list[0].Timestamp - TimeSpan.FromMilliseconds(Math.Max(0, list[0].DurationMs));
                Span = list[list.Count - 1].Timestamp - start;
            }
            Models.AddRange(list.GroupBy(r => PriceTable.Family(r.ModelUsed) ?? "other", StringComparer.Ordinal)
                .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal));
            return this;
        }

        /// <summary>
        /// What the row adds after the solution: "(opus 12, haiku 3; 2 cache rewrites, 1 error)", the models only when
        /// more than one; null with nothing to add.
        /// </summary>
        public string DetailsText()
        {
            var parts = new List<string>();
            if (Models.Count > 1) parts.Add(string.Join(", ", Models.Select(m => m.Key + " " + m.Value)));
            if (SpanText(Span) is string span) parts.Add(span);
            var trouble = new List<string>();
            if (CacheRewrites > 0) trouble.Add($"{CacheRewrites} cache {(CacheRewrites == 1 ? "rewrite" : "rewrites")}");
            if (Errors > 0) trouble.Add($"{Errors} {(Errors == 1 ? "error" : "errors")}");
            if (trouble.Count > 0) parts.Add(string.Join(", ", trouble));
            return parts.Count > 0 ? "(" + string.Join("; ", parts) + ")" : null;
        }
    }

    /// <summary>The turns sent with one effort, in the savings report.</summary>
    public sealed class EffortBreakdown
    {
        /// <summary>"low" … "max", or <see cref="SavingsReport.DefaultEffort"/>.</summary>
        public string Effort { get; set; }
        public int Turns { get; set; }
        public double CostUsd { get; set; }
        public long ThinkingTokens { get; set; }
    }

    public sealed class ModelBreakdown
    {
        public string Family { get; set; }
        public int Turns { get; set; }
        public double CostUsd { get; set; }
        public double CostPerTurnUsd => Turns > 0 ? CostUsd / Turns : 0;
        /// <summary>The main agent's input tokens read from the cache, in percent (subagents may run other models).</summary>
        public double CacheHitPercent { get; set; }
    }

    /// <summary>
    /// What auto-routing and context references saved, computed from the local usage log.
    /// "Opus-equivalent" re-prices each turn's actual token counts at Opus rates. It is an estimate:
    /// a different model would not produce exactly the same tokens, and switching models costs cache writes
    /// (already included in the actual cost).
    /// </summary>
    public sealed class SavingsReport
    {
        public string Solution { get; set; }
        public DateTime? Since { get; set; }
        /// <summary>The end of a period that ended (exclusive, UTC); null for one up to now.</summary>
        public DateTime? Until { get; set; }
        /// <summary>"November 2026", "the last 30 days"; null for everything logged.</summary>
        public string PeriodName { get; set; }
        public int Turns { get; set; }
        public int RoutedTurns { get; set; }
        public double ActualCostUsd { get; set; }
        public double OpusEquivalentUsd { get; set; }
        public double RoutingSavedUsd => Math.Max(0, OpusEquivalentUsd - ActualCostUsd);
        public double RoutingSavedPercent => OpusEquivalentUsd > 0 ? 100.0 * RoutingSavedUsd / OpusEquivalentUsd : 0;
        public long TokensAvoided { get; set; }
        /// <summary>Avoided tokens priced as input of the model that handled each turn.</summary>
        public double ReferencesSavedUsd { get; set; }
        public double CacheHitPercent { get; set; }
        /// <summary>
        /// What the prompt cache saved: reads at the cache-read price instead of the input price, minus the extra for
        /// writes (lines without the 1-hour split count as 5-minute writes). Can be negative.
        /// </summary>
        public double CacheSavedUsd { get; set; }
        public long CacheReadTokens { get; set; }
        public long OutputTokens { get; set; }
        /// <summary>Of <see cref="OutputTokens"/>: thinking, and what it cost at each turn's output price.</summary>
        public long ThinkingTokens { get; set; }
        public double ThinkingCostUsd { get; set; }
        /// <summary>Turns answered by Fable, and what they cost more than the same tokens at Opus prices.</summary>
        public int FableTurns { get; set; }
        public double FableExtraUsd { get; set; }
        /// <summary>Turns with requests in fast mode, and what that cost more than standard speed (fast tokens at <see cref="ModelPrice.FastMultiplier"/>×).</summary>
        public int FastTurns { get; set; }
        public double FastExtraUsd { get; set; }
        /// <summary>Turns with requests over a model's long-context tier (Haiku: 100k input tokens, 5×), and what that cost above the normal price.</summary>
        /// <summary>Turns with subagents, their tokens and what they cost at their own models' prices (already in <see cref="ActualCostUsd"/>).</summary>
        public int AgentTurns { get; set; }
        public long AgentTokens { get; set; }
        public double AgentCostUsd { get; set; }
        /// <summary>
        /// <see cref="AgentCostUsd"/> by kind of agent (Explore, general-purpose, Fork, …), from each turn's logged shares;
        /// turns logged before the shares were (or without them) under "not known". Empty when no turn has shares.
        /// </summary>
        public Dictionary<string, double> AgentCostByType { get; } = new Dictionary<string, double>(StringComparer.Ordinal);
        public const string NotKnownType = "not known";

        /// <summary><see cref="AgentCostUsd"/> by the model the agents ran on ("haiku", "sonnet", "opus", "fable", or the id).</summary>
        public Dictionary<string, double> AgentCostByModel { get; } = new Dictionary<string, double>(StringComparer.Ordinal);
        /// <summary>What the Opus agents' tokens would have cost at Sonnet prices (0 when none ran on Opus).</summary>
        public double OpusAgentsOnSonnetUsd { get; set; }

        /// <summary>
        /// "sonnet $0.40 · opus $1.20 (≈ $0.72 on Sonnet) · haiku $0.03": costliest first; null when no agent ran.
        /// </summary>
        public string AgentByModelLine()
        {
            if (AgentCostByModel.Count == 0) return null;
            return string.Join(" · ", AgentCostByModel.OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key} ${kv.Value:0.00}" + (kv.Key == "opus" && OpusAgentsOnSonnetUsd > 0 ? $" (≈ ${OpusAgentsOnSonnetUsd:0.00} on Sonnet)" : "")));
        }

        /// <summary>
        /// "Explore $0.40 · general-purpose $0.25 · not known $0.10": costliest first, "not known" last; null when no
        /// turn logged its kinds of agent.
        /// </summary>
        public string AgentByTypeLine()
        {
            if (!AgentCostByType.Keys.Any(k => k != NotKnownType)) return null;
            return string.Join(" · ", AgentCostByType
                .OrderBy(kv => kv.Key == NotKnownType).ThenByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key} ${kv.Value:0.00}"));
        }

        /// <summary>A turn's subagent cost at the report's prices, split by its logged shares (all "not known" without them).</summary>
        private static void AddAgentTypes(Dictionary<string, double> byType, List<AgentTypeCost> shares, double agentsCostUsd)
        {
            var parts = shares?.Where(s => s.Share > 0 && !string.IsNullOrEmpty(s.Type)).ToList();
            if (parts == null || parts.Count == 0) parts = new List<AgentTypeCost> { new AgentTypeCost { Type = NotKnownType, Share = 1 } };
            foreach (var p in parts)
                byType[p.Type] = (byType.TryGetValue(p.Type, out var sofar) ? sofar : 0) + agentsCostUsd * p.Share;
        }
        /// <summary>Tokens the main agents' tool results added, per tool group, from the turns that logged them.</summary>
        public Dictionary<string, long> ToolResultTokens { get; } = new Dictionary<string, long>(StringComparer.Ordinal);
        /// <summary>How many tool groups <see cref="ToolResultsLine"/> names before "other".</summary>
        public const int ToolsShown = 5;

        /// <summary>"Read 1.2M · Shell 410k · Search 120k · other 30k": largest first; null when no turn logged any.</summary>
        public string ToolResultsLine()
        {
            var sorted = ToolResultTokens.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            if (sorted.Count == 0) return null;
            var parts = sorted.Take(ToolsShown).Select(kv => $"{kv.Key} {TokenMeter.Format(kv.Value)}").ToList();
            long rest = sorted.Skip(ToolsShown).Sum(kv => kv.Value);
            if (rest > 0) parts.Add($"other {TokenMeter.Format(rest)}");
            return string.Join(" · ", parts);
        }

        /// <summary>
        /// Turns that wrote the conversation to the cache again (<see cref="IsCacheRewrite"/>), the tokens, and what writing
        /// them cost more than reading them would have. Only turns with a logged session id count.
        /// </summary>
        public int CacheRewriteTurns { get; set; }
        public long CacheRewriteTokens { get; set; }
        public double CacheRewriteExtraUsd { get; set; }

        /// <summary>The likely cause of a cache rewrite (<see cref="RewriteCause"/>).</summary>
        public const string CausePause = "pause", CauseModel = "model", CauseOther = "other";
        private static readonly (string Cause, string Text)[] CauseTexts = { (CausePause, "after a pause"), (CauseModel, "model switch"), (CauseOther, "other") };

        /// <summary>The cache rewrites per likely cause, with what they cost more than reading the cache.</summary>
        public Dictionary<string, RewriteCauseBreakdown> CacheRewriteByCause { get; } = new Dictionary<string, RewriteCauseBreakdown>(StringComparer.Ordinal);

        /// <summary>From <paramref name="previous"/>'s end (its logged time) to <paramref name="r"/>'s start (its logged time less its duration).</summary>
        public static TimeSpan PauseBefore(UsageRecord r, UsageRecord previous) =>
            r.Timestamp - TimeSpan.FromMilliseconds(Math.Max(0, r.DurationMs)) - previous.Timestamp;

        /// <summary>What writing the 1-hour cache cost above writing the same tokens to the 5-minute cache.</summary>
        public double OneHourExtraUsd { get; set; }
        /// <summary>
        /// Turns that came 5–60 minutes after the previous one of a conversation that wrote the 1-hour cache, and read
        /// from it; and what reading cost less than writing those tokens again, as the 5-minute cache would have made them.
        /// </summary>
        public int OneHourPaidTurns { get; set; }
        public double OneHourSavedUsd { get; set; }
        public bool WroteOneHourCache { get; set; }

        /// <summary>
        /// "$0.40 more to write, ≈ $1.20 saved on 6 turns after a pause of 5–60 min (net ≈ $0.80 saved)"; null when no
        /// turn wrote the 1-hour cache.
        /// </summary>
        public string OneHourCacheLine()
        {
            if (!WroteOneHourCache) return null;
            var c = CultureInfo.InvariantCulture;
            double net = OneHourSavedUsd - OneHourExtraUsd;
            return $"${OneHourExtraUsd.ToString("0.00", c)} more to write, ≈ ${OneHourSavedUsd.ToString("0.00", c)} saved on {OneHourPaidTurns} "
                + $"{(OneHourPaidTurns == 1 ? "turn" : "turns")} after a pause of 5–60 min (net ≈ ${Math.Abs(net).ToString("0.00", c)} {(net >= 0 ? "saved" : "lost")})";
        }

        /// <summary>
        /// Why a turn wrote the conversation to the cache again, judged against the previous turn of its conversation:
        /// <see cref="CausePause"/> when more than the cache's lifetime passed from that turn's end to this one's start
        /// (<see cref="PauseBefore"/>; 1 hour once the conversation wrote the 1-hour cache, else 5 minutes), else
        /// <see cref="CauseModel"/> when the model family changed, else <see cref="CauseOther"/> (a changed CLAUDE.md,
        /// MCP tools, settings or speed).
        /// </summary>
        public static string RewriteCause(UsageRecord r, UsageRecord previous, bool wrote1h)
        {
            var lifetime = wrote1h ? CacheWindow.Long : CacheWindow.Short;
            if (PauseBefore(r, previous) > lifetime) return CausePause;
            if (PriceTable.Family(r.ModelUsed) != PriceTable.Family(previous.ModelUsed)) return CauseModel;
            return CauseOther;
        }

        /// <summary>
        /// The turns that wrote their conversation to the cache again (<see cref="IsCacheRewrite"/>), each with its likely
        /// cause (<see cref="RewriteCause"/>), oldest first. Turns without a logged session id are not judged.
        /// </summary>
        public static List<KeyValuePair<UsageRecord, string>> CacheRewriteCauses(IEnumerable<UsageRecord> records)
        {
            var found = new List<KeyValuePair<UsageRecord, string>>();
            foreach (var session in (records ?? Enumerable.Empty<UsageRecord>()).Where(r => !string.IsNullOrEmpty(r.SessionId))
                .OrderBy(r => r.Timestamp).GroupBy(r => r.SessionId, StringComparer.Ordinal))
            {
                UsageRecord previous = null;
                bool wrote1h = false;
                foreach (var r in session)
                {
                    if (IsCacheRewrite(r, previous == null)) found.Add(new KeyValuePair<UsageRecord, string>(r, RewriteCause(r, previous, wrote1h)));
                    wrote1h |= r.CacheWrite1hTokens > 0;
                    previous = r;
                }
            }
            return found.OrderBy(kv => kv.Key.Timestamp).ToList();
        }

        /// <summary>"after a pause: 3 ($0.20), model switch: 1 ($0.05), other: 1 ($0.04)"; null with no rewrite.</summary>
        public string CacheRewriteCausesLine()
        {
            var c = CultureInfo.InvariantCulture;
            var parts = CauseTexts.Where(t => CacheRewriteByCause.ContainsKey(t.Cause))
                .Select(t => $"{t.Text}: {CacheRewriteByCause[t.Cause].Turns} (${CacheRewriteByCause[t.Cause].ExtraUsd.ToString("0.00", c)})").ToList();
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }
        /// <summary>A turn that wrote at least this many tokens to the cache, and more than it read, wrote the conversation again.</summary>
        public const long CacheRewriteFromTokens = 10_000;

        /// <summary>
        /// A turn after the first of its conversation that wrote at least <see cref="CacheRewriteFromTokens"/> to the cache
        /// and more than it read from it: the cache had expired, the model changed, or the start of the conversation did.
        /// </summary>
        public static bool IsCacheRewrite(UsageRecord r, bool firstOfSession) =>
            !firstOfSession && r.CacheWriteTokens >= CacheRewriteFromTokens && r.CacheWriteTokens > r.CacheReadTokens;

        /// <summary>What a turn's cache writes cost more than reading the same tokens from the cache.</summary>
        private static double RewriteExtra(UsageRecord r, ModelPrice p)
        {
            long write1h = Math.Min(Math.Max(0, r.CacheWrite1hTokens), r.CacheWriteTokens), write5m = r.CacheWriteTokens - write1h;
            return (write5m * (p.CacheWrite - p.CacheRead) + write1h * (2 * p.Input - p.CacheRead)) / 1_000_000.0 * p.Multiplier(r);
        }

        /// <summary>Turns past this one in their conversation each send a long history again.</summary>
        public const int LongConversationFromTurn = 30;
        /// <summary>Turns past the 30th of their conversation (logged session ids only), their cost and conversations.</summary>
        public int LongConversationTurns { get; set; }
        public double LongConversationUsd { get; set; }
        public int LongConversations { get; set; }
        /// <summary>Percent of the period's spend.</summary>
        public double LongConversationShare { get; set; }
        /// <summary>The cost per turn of turns up to the 30th of their conversation, to compare with.</summary>
        public double EarlierTurnUsd { get; set; }

        /// <summary>
        /// "$4.20 in 35 turns of 2 conversations (12% of the spend), $0.120 per turn against $0.040 in the first 30"; null
        /// with no turn past the 30th.
        /// </summary>
        public string LongConversationsLine()
        {
            if (LongConversationTurns == 0) return null;
            var c = CultureInfo.InvariantCulture;
            string line = $"${LongConversationUsd.ToString("0.00", c)} in {LongConversationTurns} {(LongConversationTurns == 1 ? "turn" : "turns")} of "
                + $"{LongConversations} {(LongConversations == 1 ? "conversation" : "conversations")} ({LongConversationShare.ToString("0", c)}% of the spend), "
                + $"${(LongConversationUsd / LongConversationTurns).ToString("0.000", c)} per turn";
            return EarlierTurnUsd > 0 ? line + $" against ${EarlierTurnUsd.ToString("0.000", c)} in the first {LongConversationFromTurn}" : line;
        }

        /// <summary>Turns that ended in an error (interrupted, a limit, an API error) and what they cost, router included.</summary>
        public int FailedTurns { get; set; }
        public double FailedUsd { get; set; }
        /// <summary>Percent of the period's spend.</summary>
        public double FailedShare { get; set; }

        /// <summary>The failed turns per <see cref="EndReasonText"/> of their logged end ("not logged" for older lines).</summary>
        public Dictionary<string, int> FailedByReason { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        private const string ReasonNotLogged = "not logged";

        /// <summary>
        /// A logged <see cref="UsageRecord.EndReason"/> in words: "stopped" (any <c>aborted_*</c>), "API error", "turn
        /// limit", "budget limit"; others as sent, underscores as spaces; null for none.
        /// </summary>
        public static string EndReasonText(string end)
        {
            if (string.IsNullOrEmpty(end)) return null;
            if (end.StartsWith("aborted", StringComparison.Ordinal)) return "stopped";
            switch (end)
            {
                case "api_error": return "API error";
                case "max_turns": case "error_max_turns": return "turn limit";
                case "error_max_budget_usd": return "budget limit";
                default: return end.Replace('_', ' ');
            }
        }

        /// <summary>
        /// "3 turns, $0.40 (1% of the spend; 2 stopped, 1 API error)", the reasons most first and only when some were
        /// logged; null when the failed turns cost nothing.
        /// </summary>
        public string FailedTurnsLine()
        {
            if (FailedTurns == 0 || FailedUsd <= 0) return null;
            var c = CultureInfo.InvariantCulture;
            string reasons = FailedByReason.Keys.Any(k => k != ReasonNotLogged)
                ? "; " + string.Join(", ", FailedByReason.OrderBy(kv => kv.Key == ReasonNotLogged).ThenByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{kv.Value} {kv.Key}"))
                : "";
            return $"{FailedTurns} {(FailedTurns == 1 ? "turn" : "turns")}, ${FailedUsd.ToString("0.00", c)} ({FailedShare.ToString("0", c)}% of the spend{reasons})";
        }

        public int LongContextTurns { get; set; }
        public double LongContextExtraUsd { get; set; }
        public List<ModelBreakdown> ByModel { get; } = new List<ModelBreakdown>();

        /// <summary>
        /// Turns, cost and thinking per effort, from low to max, then turns without a known effort (Claude Code's
        /// default, or logged before efforts were). Empty when no turn has a known effort.
        /// </summary>
        public List<EffortBreakdown> ByEffort { get; } = new List<EffortBreakdown>();
        public const string DefaultEffort = "default";
        private static readonly string[] EffortOrder = { "low", "medium", "high", "xhigh", "max" };
        /// <summary>Turns routed by the Haiku router, how many picked the same model as the local rules, its cost and time.</summary>
        public int HaikuRouted { get; set; }
        public int HaikuAgreed { get; set; }
        public double RouterCostUsd { get; set; }
        public long RouterAverageMs { get; set; }

        /// <summary>
        /// Of the Haiku router's prompts that went to another model than the local rules picked: how many went to a
        /// cheaper one and what that saved, how many to a dearer one and what that added, each the turn's main-agent
        /// tokens at the model that answered against the local rules' model (an estimate: another model would not
        /// write the same tokens).
        /// </summary>
        public int HaikuCheaper { get; set; }
        public double HaikuCheaperSavedUsd { get; set; }
        public int HaikuDearer { get; set; }
        public double HaikuDearerExtraUsd { get; set; }

        /// <summary>"2 to a cheaper model (≈ $0.12 less), 1 to a dearer one (≈ $0.30 more)"; null when they never differed in price.</summary>
        public string HaikuDifferedLine()
        {
            if (HaikuCheaper == 0 && HaikuDearer == 0) return null;
            var parts = new List<string>();
            if (HaikuCheaper > 0) parts.Add($"{HaikuCheaper} to a cheaper model (≈ ${HaikuCheaperSavedUsd:0.00} less)");
            if (HaikuDearer > 0) parts.Add($"{HaikuDearer} to a dearer one (≈ ${HaikuDearerExtraUsd:0.00} more)");
            return string.Join(", ", parts);
        }
        /// <summary>The dearest turns, dearest first (at most <see cref="CostliestCount"/>).</summary>
        public List<UsageRecord> Costliest { get; } = new List<UsageRecord>();
        public const int CostliestCount = 5;

        /// <summary>
        /// The dearest conversations, dearest first (at most <see cref="CostliestCount"/>): turns grouped by their logged
        /// session id; turns logged without one are left out.
        /// </summary>
        public List<ConversationBreakdown> CostliestConversations { get; } = new List<ConversationBreakdown>();

        /// <summary>Conversations with a logged session id that cost something, and what one cost and how long it ran.</summary>
        public int Conversations { get; set; }
        public double ConversationAverageUsd { get; set; }
        public double ConversationMedianUsd { get; set; }
        public double ConversationAverageTurns { get; set; }

        /// <summary>
        /// "Conversations: 32, $0.40 on average (median $0.12), 6.2 turns on average"; null with fewer than two. A median
        /// well under the average: a few long conversations cost most.
        /// </summary>
        public string ConversationsLine()
        {
            if (Conversations < 2) return null;
            var c = CultureInfo.InvariantCulture;
            return $"Conversations: {Conversations}, ${ConversationAverageUsd.ToString("0.00", c)} on average (median ${ConversationMedianUsd.ToString("0.00", c)}), "
                + $"{ConversationAverageTurns.ToString("0.#", c)} turns on average";
        }

        /// <summary>The list by weekday needs turns over this many local days, first to last, so each weekday has had two.</summary>
        public const int WeekdaysFromDays = 14;

        /// <summary>
        /// Turns and cost per local weekday, Monday first, weekdays without turns left out; empty when the period's turns
        /// span fewer than <see cref="WeekdaysFromDays"/> days.
        /// </summary>
        public List<WeekdayBreakdown> ByWeekday { get; } = new List<WeekdayBreakdown>();

        /// <summary>Turns, tokens, cost and cache per local day, oldest first.</summary>
        public List<DayBreakdown> ByDay { get; } = new List<DayBreakdown>();
        /// <summary>How many days the text shows (the latest).</summary>
        public const int DaysShown = 14;

        /// <summary>Spent this calendar month (local time) up to the report, and when there are 3 days or more of it, the month's total at that pace.</summary>
        public DateTime Month { get; set; }
        public double MonthToDateUsd { get; set; }
        public double? MonthProjectedUsd { get; set; }
        public const int ProjectFromDay = 3;

        /// <summary>Turns and cost per solution folder, dearest first (only in a report for all solutions).</summary>
        public List<SolutionBreakdown> BySolution { get; } = new List<SolutionBreakdown>();
        public const int SolutionsShown = 10;

        /// <summary>What the prompt cache saved in a logged turn: the main agent's and each subagent model's, at its own price.</summary>
        private static double CacheSaved(UsageRecord r, PriceTable prices) =>
            (prices.For(r.ModelUsed) ?? prices.Sonnet).CacheSavedUsd(r)
            + (r.Agents?.Sum(a => (prices.For(a.Model) ?? prices.Sonnet).CacheSavedUsd(a.ToUsage())) ?? 0);

        /// <param name="sinceUtc">Only turns from then; <paramref name="untilUtc"/>: only turns before then (a period that ended). The month and
        /// week lines always compare up to now, from all turns.</param>
        public static SavingsReport Build(IEnumerable<UsageRecord> records, PriceTable prices, string solution = null, DateTime? sinceUtc = null, DateTime? nowUtc = null, DateTime? untilUtc = null)
        {
            prices = prices ?? new PriceTable();
            var scoped = (records ?? Enumerable.Empty<UsageRecord>())
                .Where(r => solution == null || string.Equals(r.Solution, solution, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Timestamp)
                .ToList();
            var list = scoped
                .Where(r => !sinceUtc.HasValue || r.Timestamp >= sinceUtc.Value)
                .Where(r => !untilUtc.HasValue || r.Timestamp < untilUtc.Value)
                .OrderBy(r => r.Timestamp)
                .ToList();

            var rep = new SavingsReport { Solution = solution, Turns = list.Count, Since = list.Count > 0 ? list[0].Timestamp : (DateTime?)null, Until = untilUtc };
            long input = 0, cacheRead = 0;
            foreach (var r in list)
            {
                if (r.Routed) rep.RoutedTurns++;
                rep.ActualCostUsd += r.CostUsd + r.RouterCostUsd; // asking the router is part of what routing costs
                rep.RouterCostUsd += r.RouterCostUsd;
                if (r.Router == "haiku")
                {
                    rep.HaikuRouted++;
                    if (r.LocalModel != null && string.Equals(r.LocalModel, r.RouteModel, StringComparison.OrdinalIgnoreCase)) rep.HaikuAgreed++;
                    else if (r.LocalModel != null && prices.For(r.LocalModel) is ModelPrice localPrice && prices.For(r.ModelUsed ?? r.RouteModel) is ModelPrice usedPrice)
                    {
                        // The main agent's tokens only: subagents run on their own models whichever the router picked
                        double diff = localPrice.Cost(r) - usedPrice.Cost(r);
                        if (diff > 0) { rep.HaikuCheaper++; rep.HaikuCheaperSavedUsd += diff; }
                        else if (diff < 0) { rep.HaikuDearer++; rep.HaikuDearerExtraUsd -= diff; }
                    }
                }
                // The logged cost includes the turn's subagents; their tokens are logged apart (r.Agents), so both sides
                // of "same tokens at Opus prices" take them: main at Opus (or its own cost on Opus), plus each agent at Opus
                double agentsOwn = r.Agents?.Sum(a => (prices.For(a.Model) ?? prices.Sonnet).Cost(a.ToUsage())) ?? 0;
                rep.OpusEquivalentUsd += (PriceTable.Family(r.ModelUsed) == "opus" ? Math.Max(0, r.CostUsd - agentsOwn) : prices.Opus.Cost(r))
                    + (r.Agents?.Sum(a => prices.Opus.Cost(a.ToUsage())) ?? 0);
                if (r.Agents != null && r.Agents.Count > 0) { rep.AgentTurns++; rep.AgentTokens += r.AgentTokensTotal; rep.AgentCostUsd += agentsOwn; }
                if (agentsOwn > 0) AddAgentTypes(rep.AgentCostByType, r.AgentTypes, agentsOwn);
                foreach (var a in r.Agents ?? Enumerable.Empty<AgentTokens>())
                {
                    string model = PriceTable.Family(a.Model) ?? a.Model ?? "not known";
                    double agentCost = (prices.For(a.Model) ?? prices.Sonnet).Cost(a.ToUsage());
                    if (agentCost <= 0) continue;
                    rep.AgentCostByModel[model] = (rep.AgentCostByModel.TryGetValue(model, out var had) ? had : 0) + agentCost;
                    if (model == "opus") rep.OpusAgentsOnSonnetUsd += prices.Sonnet.Cost(a.ToUsage());
                }
                foreach (var kv in r.ToolResultTokens ?? new Dictionary<string, long>())
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value > 0)
                        rep.ToolResultTokens[kv.Key] = (rep.ToolResultTokens.TryGetValue(kv.Key, out var t) ? t : 0) + kv.Value;
                long avoided = TokenEstimate.FromChars(r.CharsAvoided);
                rep.TokensAvoided += avoided;
                rep.ReferencesSavedUsd += avoided * (prices.For(r.ModelUsed) ?? prices.Sonnet).Input / 1_000_000.0 * (prices.For(r.ModelUsed) ?? prices.Sonnet).Multiplier(r);
                input += r.InputTokens + r.CacheReadTokens + r.CacheWriteTokens + (r.Agents?.Sum(a => a.InputTokens + a.CacheReadTokens + a.CacheWriteTokens) ?? 0);
                long reads = r.CacheReadTokens + (r.Agents?.Sum(a => a.CacheReadTokens) ?? 0);
                cacheRead += reads;
                rep.CacheReadTokens += reads;
                rep.CacheSavedUsd += CacheSaved(r, prices);
                rep.OutputTokens += r.OutputTokens;
                rep.ThinkingTokens += r.ThinkingTokens;
                rep.ThinkingCostUsd += r.ThinkingTokens * (prices.For(r.ModelUsed) ?? prices.Sonnet).Output / 1_000_000.0 * (prices.For(r.ModelUsed) ?? prices.Sonnet).Multiplier(r);

                // What Fable and fast mode added: the same tokens at Opus prices / at standard speed, both at Options → Pricing
                var own = prices.For(r.ModelUsed) ?? prices.Sonnet;
                if (PriceTable.Family(r.ModelUsed) == "fable")
                {
                    rep.FableTurns++;
                    rep.FableExtraUsd += own.Cost(r) - prices.Opus.Cost(r);
                }
                if (r.FastTokens > 0)
                {
                    rep.FastTurns++;
                    rep.FastExtraUsd += own.Cost(r) - own.Cost(r) / r.SpeedMultiplier;
                }
                double tier = own.ContextMultiplier(r.LargeContextTokens, r.TotalTokens);
                if (tier > 1)
                {
                    rep.LongContextTurns++;
                    rep.LongContextExtraUsd += own.Cost(r) - own.Cost(r) / tier;
                }
            }
            rep.CacheHitPercent = input > 0 ? 100.0 * cacheRead / input : 0;
            foreach (var kv in CacheRewriteCauses(list))
            {
                var r = kv.Key;
                double extra = RewriteExtra(r, prices.For(r.ModelUsed) ?? prices.Sonnet);
                rep.CacheRewriteTurns++;
                rep.CacheRewriteTokens += r.CacheWriteTokens;
                rep.CacheRewriteExtraUsd += extra;
                if (!rep.CacheRewriteByCause.TryGetValue(kv.Value, out var b)) rep.CacheRewriteByCause[kv.Value] = b = new RewriteCauseBreakdown();
                b.Turns++;
                b.ExtraUsd += extra;
            }
            // The 1-hour cache: what its writes cost above 5-minute ones, against the turns it kept warm through a pause
            foreach (var session in list.Where(r => !string.IsNullOrEmpty(r.SessionId)).GroupBy(r => r.SessionId, StringComparer.Ordinal))
            {
                UsageRecord previous = null;
                bool wrote1h = false;
                foreach (var r in session) // oldest first
                {
                    var p = prices.For(r.ModelUsed) ?? prices.Sonnet;
                    long write1h = Math.Min(Math.Max(0, r.CacheWrite1hTokens), r.CacheWriteTokens);
                    if (write1h > 0)
                    {
                        rep.WroteOneHourCache = true;
                        rep.OneHourExtraUsd += write1h * (2 * p.Input - p.CacheWrite) / 1_000_000.0 * p.Multiplier(r);
                    }
                    if (previous != null && wrote1h && r.CacheReadTokens > 0 && PauseBefore(r, previous) is TimeSpan pause
                        && pause > CacheWindow.Short && pause <= CacheWindow.Long)
                    {
                        rep.OneHourPaidTurns++;
                        rep.OneHourSavedUsd += r.CacheReadTokens * (p.CacheWrite - p.CacheRead) / 1_000_000.0 * p.Multiplier(r);
                    }
                    wrote1h |= write1h > 0;
                    previous = r;
                }
            }
            // Long conversations: the turns past the 30th of their conversation, against the earlier ones
            double earlierUsd = 0; int earlierTurns = 0;
            foreach (var session in list.Where(r => !string.IsNullOrEmpty(r.SessionId)).GroupBy(r => r.SessionId, StringComparer.Ordinal))
            {
                int n = 0; bool counted = false;
                foreach (var r in session) // oldest first
                {
                    double cost = r.CostUsd + r.RouterCostUsd;
                    if (++n <= LongConversationFromTurn) { earlierUsd += cost; earlierTurns++; continue; }
                    rep.LongConversationTurns++;
                    rep.LongConversationUsd += cost;
                    if (!counted) { rep.LongConversations++; counted = true; }
                }
            }
            rep.EarlierTurnUsd = earlierTurns > 0 ? earlierUsd / earlierTurns : 0;
            var failed = list.Where(r => r.IsError).ToList();
            rep.FailedTurns = failed.Count;
            rep.FailedUsd = failed.Sum(r => r.CostUsd + r.RouterCostUsd);
            rep.FailedShare = rep.ActualCostUsd > 0 ? 100.0 * rep.FailedUsd / rep.ActualCostUsd : 0;
            foreach (var r in failed)
            {
                string reason = EndReasonText(r.EndReason) ?? ReasonNotLogged;
                rep.FailedByReason[reason] = (rep.FailedByReason.TryGetValue(reason, out var n) ? n : 0) + 1;
            }
            rep.LongConversationShare = rep.LongConversationTurns > 0 && list.Sum(r => r.CostUsd + r.RouterCostUsd) is double all && all > 0
                ? 100.0 * rep.LongConversationUsd / all : 0;
            var timed = list.Where(r => r.RouterMs > 0).ToList();
            rep.RouterAverageMs = timed.Count > 0 ? (long)timed.Average(r => r.RouterMs) : 0;

            rep.ByModel.AddRange(list
                .GroupBy(r => PriceTable.Family(r.ModelUsed) ?? "other")
                .Select(g =>
                {
                    long mIn = g.Sum(r => r.InputTokens + r.CacheReadTokens + r.CacheWriteTokens);
                    return new ModelBreakdown
                    {
                        Family = g.Key, Turns = g.Count(), CostUsd = g.Sum(r => r.CostUsd),
                        CacheHitPercent = mIn > 0 ? 100.0 * g.Sum(r => r.CacheReadTokens) / mIn : 0,
                    };
                })
                .OrderByDescending(b => b.Turns));

            if (list.Any(r => (r.Effort ?? r.RouteEffort) != null))
                rep.ByEffort.AddRange(list
                    .GroupBy(r => (r.Effort ?? r.RouteEffort)?.ToLowerInvariant() ?? DefaultEffort, StringComparer.Ordinal)
                    .Select(g => new EffortBreakdown { Effort = g.Key, Turns = g.Count(), CostUsd = g.Sum(r => r.CostUsd + r.RouterCostUsd), ThinkingTokens = g.Sum(r => r.ThinkingTokens) })
                    .OrderBy(b => Array.IndexOf(EffortOrder, b.Effort) is int i && i >= 0 ? i : EffortOrder.Length + (b.Effort == DefaultEffort ? 1 : 0))
                    .ThenBy(b => b.Effort, StringComparer.Ordinal));

            rep.Costliest.AddRange(list.Where(r => r.CostUsd > 0).OrderByDescending(r => r.CostUsd).Take(CostliestCount));
            var rewrites = CacheRewriteCauses(list).GroupBy(kv => kv.Key.SessionId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var conversations = list
                .Where(r => !string.IsNullOrEmpty(r.SessionId))
                .GroupBy(r => r.SessionId, StringComparer.Ordinal)
                .Select(g => new ConversationBreakdown
                {
                    SessionId = g.Key,
                    Started = g.First().Timestamp, // the list is oldest first
                    Turns = g.Count(),
                    CostUsd = g.Sum(r => r.CostUsd + r.RouterCostUsd),
                    Solution = g.First().Solution,
                    FirstPrompt = g.Select(r => r.PromptPreview).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
                    CacheRewrites = rewrites.TryGetValue(g.Key, out var n) ? n : 0,
                    Errors = g.Count(r => r.IsError),
                }.WithTurns(g))
                .Where(b => b.CostUsd > 0)
                .OrderByDescending(b => b.CostUsd)
                .ToList();
            rep.CostliestConversations.AddRange(conversations.Take(CostliestCount));
            rep.Conversations = conversations.Count;
            if (conversations.Count > 0)
            {
                rep.ConversationAverageUsd = conversations.Average(b => b.CostUsd);
                rep.ConversationAverageTurns = conversations.Average(b => b.Turns);
                var costs = conversations.Select(b => b.CostUsd).OrderBy(c => c).ToList();
                int mid = costs.Count / 2;
                rep.ConversationMedianUsd = costs.Count % 2 == 1 ? costs[mid] : (costs[mid - 1] + costs[mid]) / 2;
            }

            foreach (var day in list.GroupBy(r => r.Timestamp.ToLocalTime().Date))
            {
                long dIn = day.Sum(r => r.InputTokens + r.CacheReadTokens + r.CacheWriteTokens + (r.Agents?.Sum(a => a.InputTokens + a.CacheReadTokens + a.CacheWriteTokens) ?? 0));
                long dRead = day.Sum(r => r.CacheReadTokens + (r.Agents?.Sum(a => a.CacheReadTokens) ?? 0));
                rep.ByDay.Add(new DayBreakdown
                {
                    Day = day.Key,
                    Turns = day.Count(),
                    Tokens = day.Sum(r => r.TotalTokens),
                    CostUsd = day.Sum(r => r.CostUsd + r.RouterCostUsd),
                    CacheSavedUsd = day.Sum(r => CacheSaved(r, prices)),
                    CacheHitPercent = dIn > 0 ? 100.0 * dRead / dIn : 0,
                });
            }
            if (list.Count > 0 && (list[list.Count - 1].Timestamp.ToLocalTime().Date - list[0].Timestamp.ToLocalTime().Date).Days + 1 >= WeekdaysFromDays)
                rep.ByWeekday.AddRange(list
                    .GroupBy(r => r.Timestamp.ToLocalTime().DayOfWeek)
                    .Select(g => new WeekdayBreakdown { Day = g.Key, Turns = g.Count(), CostUsd = g.Sum(r => r.CostUsd + r.RouterCostUsd) })
                    .OrderBy(b => ((int)b.Day + 6) % 7)); // Monday first
            var today = (nowUtc ?? DateTime.UtcNow).ToLocalTime().Date;
            rep.Month = new DateTime(today.Year, today.Month, 1);
            rep.MonthToDateUsd = scoped.Where(r => r.Timestamp.ToLocalTime().Date >= rep.Month && r.Timestamp.ToLocalTime().Date <= today).Sum(r => r.CostUsd + r.RouterCostUsd);
            if (today.Day >= ProjectFromDay && rep.MonthToDateUsd > 0)
                rep.MonthProjectedUsd = rep.MonthToDateUsd / today.Day * DateTime.DaysInMonth(today.Year, today.Month);
            // This week from Monday, and last week up to the same weekday
            rep.WeekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            rep.WeekToDateUsd = scoped.Where(r => r.Timestamp.ToLocalTime().Date >= rep.WeekStart && r.Timestamp.ToLocalTime().Date <= today).Sum(r => r.CostUsd + r.RouterCostUsd);
            var todays = scoped.Where(r => r.Timestamp.ToLocalTime().Date == today).ToList();
            rep.TodayTurns = todays.Count;
            rep.TodayUsd = todays.Sum(r => r.CostUsd + r.RouterCostUsd);
            rep.YesterdayUsd = scoped.Where(r => r.Timestamp.ToLocalTime().Date == today.AddDays(-1)).Sum(r => r.CostUsd + r.RouterCostUsd);
            var lastWeek = rep.WeekStart.AddDays(-7);
            rep.LastWeekToDateUsd = scoped.Where(r => r.Timestamp.ToLocalTime().Date >= lastWeek && r.Timestamp.ToLocalTime().Date <= lastWeek.AddDays((today - rep.WeekStart).Days)).Sum(r => r.CostUsd + r.RouterCostUsd);
            // Last month up to the same day (its last day when it is shorter), to compare with
            var lastMonth = rep.Month.AddMonths(-1);
            rep.LastMonthToDay = lastMonth.AddDays(Math.Min(today.Day, DateTime.DaysInMonth(lastMonth.Year, lastMonth.Month)) - 1);
            rep.LastMonthToDateUsd = scoped.Where(r => r.Timestamp.ToLocalTime().Date >= lastMonth && r.Timestamp.ToLocalTime().Date <= rep.LastMonthToDay).Sum(r => r.CostUsd + r.RouterCostUsd);

            if (solution == null)
            {
                string Key(UsageRecord r) => (r.Solution ?? "").TrimEnd('\\', '/');
                // This month's spend per folder, whatever the period (as the month line)
                var month = scoped.Where(r => r.Timestamp.ToLocalTime().Date >= rep.Month && r.Timestamp.ToLocalTime().Date <= today)
                    .GroupBy(Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(r => r.CostUsd + r.RouterCostUsd), StringComparer.OrdinalIgnoreCase);
                rep.BySolution.AddRange(list
                    .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new SolutionBreakdown
                    {
                        Solution = g.Key.Length == 0 ? null : g.First().Solution, Turns = g.Count(), CostUsd = g.Sum(r => r.CostUsd + r.RouterCostUsd),
                        MonthUsd = month.TryGetValue(g.Key, out var m) ? m : 0,
                    })
                    .OrderByDescending(b => b.CostUsd).ThenByDescending(b => b.Turns));
            }
            return rep;
        }

        /// <summary>"This month: $12.30 so far, ~$41.00 at this pace by Nov 30"; null with nothing spent this month.</summary>
        public string MonthLine()
        {
            if (MonthToDateUsd <= 0) return null;
            var monthEnd = Month.AddDays(DateTime.DaysInMonth(Month.Year, Month.Month) - 1);
            string line = $"This month:                 ${MonthToDateUsd:0.00} so far";
            if (MonthProjectedUsd is double p) line += $", ~${p:0.00} at this pace by {monthEnd:MMM d}";
            return LastMonthText() is string last ? line + " " + last : line;
        }

        /// <summary>This week's Monday, what was spent since, and last week from its Monday up to the same weekday.</summary>
        public DateTime WeekStart { get; set; }
        public double WeekToDateUsd { get; set; }
        public double LastWeekToDateUsd { get; set; }

        /// <summary>
        /// "This week:                  $14.20 since Monday (last week by the same day: $11.00, +29%)"; the comparison left out
        /// when nothing was logged then; null with nothing spent this week.
        /// </summary>
        public string WeekLine()
        {
            if (WeekToDateUsd <= 0) return null;
            var c = CultureInfo.InvariantCulture;
            string line = $"This week:                  ${WeekToDateUsd.ToString("0.00", c)} since Monday";
            if (LastWeekToDateUsd <= 0) return line;
            return line + $" (last week by the same day: ${LastWeekToDateUsd.ToString("0.00", c)}, {Change(WeekToDateUsd, LastWeekToDateUsd)})";
        }

        /// <summary>Today's turns and spend (local day), and yesterday's spend, whatever the period (as the month line).</summary>
        public int TodayTurns { get; set; }
        public double TodayUsd { get; set; }
        public double YesterdayUsd { get; set; }

        /// <summary>
        /// "Today:                      $3.20 so far in 14 turns (yesterday $5.10)"; yesterday left out when it cost
        /// nothing; null with nothing spent today.
        /// </summary>
        public string TodayLine()
        {
            if (TodayUsd <= 0) return null;
            var c = CultureInfo.InvariantCulture;
            string line = $"Today:                      ${TodayUsd.ToString("0.00", c)} so far in {TodayTurns} {(TodayTurns == 1 ? "turn" : "turns")}";
            return YesterdayUsd > 0 ? line + $" (yesterday ${YesterdayUsd.ToString("0.00", c)})" : line;
        }

        /// <summary>"+37%", "−50%", "±0%".</summary>
        private static string Change(double now, double before)
        {
            double change = 100.0 * (now - before) / before;
            string sign = change >= 0.5 ? "+" : change <= -0.5 ? "−" : "±";
            return sign + Math.Abs(change).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>Last month from its 1st up to <see cref="LastMonthToDay"/> (the same day as today, or its last day).</summary>
        public DateTime LastMonthToDay { get; set; }
        public double LastMonthToDateUsd { get; set; }

        /// <summary>"(Oct 1–11: $9.00, +37%)"; null when nothing was logged then.</summary>
        public string LastMonthText()
        {
            if (LastMonthToDateUsd <= 0) return null;
            return $"({LastMonthToDay:MMM} 1–{LastMonthToDay.Day}: ${LastMonthToDateUsd:0.00}, {Change(MonthToDateUsd, LastMonthToDateUsd)})";
        }

        /// <summary>"By solution:" lines for a report over all solutions; empty with fewer than two solutions.</summary>
        public string SolutionsText()
        {
            if (BySolution.Count < 2) return "";
            var sb = new StringBuilder();
            sb.AppendLine("By solution:");
            // This month's part, when the period reaches today and is more than this month
            bool month = Until == null && Since.HasValue && Since.Value.ToLocalTime().Date < Month && BySolution.Any(b => b.MonthUsd > 0);
            foreach (var b in BySolution.Take(SolutionsShown))
                sb.AppendLine(($"  {OneLine(b.Name, 30),-30} {b.Turns,5} turns  ${b.CostUsd,8:0.00}"
                    + (month && b.MonthUsd > 0 ? $"  (${b.MonthUsd:0.00} this month)" : "")).TrimEnd());
            if (BySolution.Count > SolutionsShown)
                sb.AppendLine($"  … {BySolution.Count - SolutionsShown} more, ${BySolution.Skip(SolutionsShown).Sum(b => b.CostUsd):0.00}");
            return sb.ToString();
        }

        private static string OneLine(string s, int max)
        {
            string one = string.Join(" ", (s ?? "").Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return one.Length <= max ? one : one.Substring(0, max - 1) + "…";
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Claude Code — savings report" + (Solution != null ? " for " + Solution : " (all solutions)") + (PeriodName != null ? ", " + PeriodName : ""));
            if (Turns == 0)
            {
                sb.AppendLine(PeriodName == null ? "No turns recorded yet. Turn on Options > Diagnostics > Keep local usage log." : "No turns in this period.");
                return sb.ToString();
            }

            string to = Until is DateTime until ? until.ToLocalTime().AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "today";
            sb.AppendLine($"Period: {Since.Value.ToLocalTime():yyyy-MM-dd} to {to} · {Turns} turns ({RoutedTurns} auto-routed)");
            sb.AppendLine();
            sb.AppendLine($"Actual cost:                ${ActualCostUsd:0.00}");
            if (Until == null && MonthLine() is string month) sb.AppendLine(month); // "so far" lines: only for a period up to now
            if (Until == null && WeekLine() is string week) sb.AppendLine(week);
            if (Until == null && TodayLine() is string todayLine) sb.AppendLine(todayLine);
            sb.AppendLine($"Same tokens at Opus prices: ${OpusEquivalentUsd:0.00}  (estimate)");
            sb.AppendLine($"Saved by model routing:     ${RoutingSavedUsd:0.00}  ({RoutingSavedPercent:0}%)");
            sb.AppendLine($"Saved by file references:   ~{TokenMeter.Format(TokensAvoided)} tokens ≈ ${ReferencesSavedUsd:0.00}");
            sb.AppendLine($"Saved by the prompt cache:  ${CacheSavedUsd:0.00}  ({TokenMeter.Format(CacheReadTokens)} tokens read from the cache instead of sent in full)");
            sb.AppendLine($"Cache hit rate:             {CacheHitPercent:0}%");
            if (CacheRewriteTurns > 0)
                sb.AppendLine($"Cache written again:        ${CacheRewriteExtraUsd:0.00} more than reading it  ({CacheRewriteTurns} {(CacheRewriteTurns == 1 ? "turn" : "turns")}, {TokenMeter.Format(CacheRewriteTokens)} tokens: an expired cache, a model switch or a changed CLAUDE.md, MCP tools or settings)");
            if (CacheRewriteCausesLine() is string causes)
                sb.AppendLine($"                            {causes}");
            if (OneHourCacheLine() is string oneHour)
                sb.AppendLine($"1-hour cache:               {oneHour}");
            if (LongConversationsLine() is string longOnes)
                sb.AppendLine($"Past a conversation's 30th: {longOnes}  (each turn sends the whole conversation again; /compact or a new chat starts small)");
            if (FailedTurnsLine() is string failedOnes)
                sb.AppendLine($"Ended in an error:          {failedOnes}  (interrupted, a limit or an API error: paid for, nothing finished)");
            if (ThinkingTokens > 0)
                sb.AppendLine($"Thinking:                   {TokenMeter.Format(ThinkingTokens)} tokens ≈ ${ThinkingCostUsd:0.00}  ({(OutputTokens > 0 ? 100.0 * ThinkingTokens / OutputTokens : 0):0}% of output tokens; Thinking off or lower effort cuts it)");
            if (FableTurns > 0)
                sb.AppendLine($"Paid extra for Fable:       ${FableExtraUsd:0.00}  ({FableTurns} {(FableTurns == 1 ? "turn" : "turns")}, above the same tokens on Opus)");
            if (FastTurns > 0)
                sb.AppendLine($"Paid extra for fast mode:   ${FastExtraUsd:0.00}  ({FastTurns} {(FastTurns == 1 ? "turn" : "turns")}, above standard speed)");
            if (AgentTurns > 0)
                sb.AppendLine($"Subagents:                  {TokenMeter.Format(AgentTokens)} tokens ≈ ${AgentCostUsd:0.00}  ({AgentTurns} {(AgentTurns == 1 ? "turn" : "turns")}; included above)");
            if (AgentByTypeLine() is string byType)
                sb.AppendLine($"  by kind of agent:         {byType}");
            if (AgentTurns > 0 && AgentByModelLine() is string byModel)
                sb.AppendLine($"  by model:                 {byModel}");
            if (ToolResultsLine() is string tools)
                sb.AppendLine($"Tool results:               {tools} tokens  (into the context, read again by each later request until /compact)");
            if (LongContextTurns > 0)
                sb.AppendLine($"Paid extra over 100k:       ${LongContextExtraUsd:0.00}  ({LongContextTurns} Haiku {(LongContextTurns == 1 ? "turn" : "turns")} over 100k tokens, above Haiku's normal price; /compact keeps it under)");
            if (HaikuRouted > 0 || RouterCostUsd > 0)
            {
                sb.AppendLine($"Haiku router:               {HaikuRouted} prompts, same model as the local rules {(HaikuRouted > 0 ? 100.0 * HaikuAgreed / HaikuRouted : 0):0}% of the time");
                if (HaikuDifferedLine() is string differed)
                    sb.AppendLine($"                            where they differed: {differed}, at the same tokens");
                sb.AppendLine($"                            cost ${RouterCostUsd:0.000} (included above), {RouterAverageMs / 1000.0:0.0} s per prompt on average");
            }
            sb.AppendLine();
            sb.AppendLine("By model:");
            foreach (var b in ByModel)
                sb.AppendLine($"  {b.Family,-7} {b.Turns,5} turns  ${b.CostUsd,8:0.00}  ({100.0 * b.Turns / Turns:0}% of turns, ${b.CostPerTurnUsd:0.000} per turn, cache {b.CacheHitPercent:0}%)");
            if (ByEffort.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("By effort:");
                foreach (var b in ByEffort)
                {
                    string thinking = b.ThinkingTokens > 0 ? $"  {TokenMeter.Format(b.ThinkingTokens)} thinking" : "";
                    sb.AppendLine($"  {b.Effort,-7} {b.Turns,5} turns  ${b.CostUsd,8:0.00}  (${(b.Turns > 0 ? b.CostUsd / b.Turns : 0):0.000} per turn){thinking}");
                }
            }
            if (Solution == null && SolutionsText() is string solutions && solutions.Length > 0) sb.AppendLine().Append(solutions);
            if (Costliest.Count > 1)
            {
                // Where the money went: the turns worth looking at (and the prompt, when previews are logged).
                sb.AppendLine();
                sb.AppendLine("Costliest turns:");
                foreach (var r in Costliest)
                {
                    string what = !string.IsNullOrWhiteSpace(r.PromptPreview) ? "“" + OneLine(r.PromptPreview, 60) + "”"
                        : !string.IsNullOrEmpty(r.RouteReason) ? "(" + OneLine(r.RouteReason, 60) + ")" : "";
                    sb.AppendLine($"  {r.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm}  ${r.CostUsd,6:0.00}  {PriceTable.Family(r.ModelUsed) ?? "other",-6} {TokenMeter.Format(r.TotalTokens),7} tok  {what}".TrimEnd());
                }
            }
            if (CostliestConversations.Count > 1)
            {
                // Whole conversations: a long one of cheap turns can cost more than any single turn shows.
                sb.AppendLine();
                if (ConversationsLine() is string conversations) sb.AppendLine(conversations);
                sb.AppendLine("Costliest conversations:");
                foreach (var b in CostliestConversations)
                {
                    string where = Solution == null ? SolutionBreakdown.NameOf(b.Solution) + "  " : "";
                    string what = b.FirstPrompt != null ? "“" + OneLine(b.FirstPrompt, 50) + "”" : "";
                    string details = b.DetailsText() is string d ? d + "  " : "";
                    sb.AppendLine($"  {b.Started.ToLocalTime():yyyy-MM-dd HH:mm}  ${b.CostUsd,6:0.00}  {b.Turns,4} {(b.Turns == 1 ? "turn " : "turns")}  {where}{details}{what}".TrimEnd());
                }
            }
            if (ByWeekday.Count > 0)
            {
                // Which days of the week cost most: a busy Monday, or weekend sessions that run long
                sb.AppendLine();
                sb.AppendLine("By weekday:");
                foreach (var w in ByWeekday)
                    sb.AppendLine($"  {CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(w.Day),-7} {w.Turns,5} turns  ${w.CostUsd,8:0.00}  (${(w.Turns > 0 ? w.CostUsd / w.Turns : 0):0.000} per turn)");
            }
            if (ByDay.Count > 1)
            {
                // Where the money went over time: the bar is the day's cost against the dearest day shown.
                var days = ByDay.Skip(Math.Max(0, ByDay.Count - DaysShown)).ToList();
                double max = days.Max(d => d.CostUsd);
                sb.AppendLine();
                sb.AppendLine(ByDay.Count > DaysShown ? $"By day (last {DaysShown} days with turns):" : "By day:");
                foreach (var d in days)
                {
                    string bar = max > 0 ? new string('█', (int)Math.Round(20 * d.CostUsd / max)) : "";
                    string saved = d.CacheSavedUsd >= 0.005 ? $" (saved ${d.CacheSavedUsd:0.00})" : "";
                    sb.AppendLine($"  {d.Day:yyyy-MM-dd} {d.Turns,5} turns {TokenMeter.Format(d.Tokens),7} tok  ${d.CostUsd,7:0.00}  cache {d.CacheHitPercent,3:0}%{saved,-16}  {bar}".TrimEnd());
                }
            }
            return sb.ToString();
        }
    }

    public sealed class ReplayDiff
    {
        public string PromptPreview { get; set; }
        public string LoggedModel { get; set; }
        public string NowModel { get; set; }
        public string NowReason { get; set; }
        /// <summary>
        /// The logged turn's main-agent tokens at the model the rules pick now, less at the model that answered; negative
        /// = cheaper. Null when either model's price is not known. An estimate: another model writes other tokens.
        /// </summary>
        public double? CostChangeUsd { get; set; }

        /// <summary>" (≈ −$0.030)" / " (≈ +$0.012)"; "" when not known.</summary>
        public string CostChangeText() =>
            CostChangeUsd is double d ? " (≈ " + (d < 0 ? "−" : "+") + "$" + Math.Abs(d).ToString("0.000", CultureInfo.InvariantCulture) + ")" : "";
    }

    /// <summary>
    /// Re-runs logged prompts (only those recorded with the opt-in prompt preview) through the current
    /// router, to see the effect of tuning keywords and thresholds on real usage.
    /// </summary>
    public static class RouterReplay
    {
        /// <summary>
        /// "At the logged tokens: 12 cheaper (≈ $0.40 less), 3 dearer (≈ $0.10 more), net ≈ $0.30 less."; null when no
        /// difference could be priced.
        /// </summary>
        public static string CostSummary(IReadOnlyCollection<ReplayDiff> diffs)
        {
            var priced = (diffs ?? new ReplayDiff[0]).Where(d => d.CostChangeUsd.HasValue && d.CostChangeUsd.Value != 0).ToList();
            if (priced.Count == 0) return null;
            var c = CultureInfo.InvariantCulture;
            var cheaper = priced.Where(d => d.CostChangeUsd < 0).ToList();
            var dearer = priced.Where(d => d.CostChangeUsd > 0).ToList();
            var parts = new List<string>();
            if (cheaper.Count > 0) parts.Add($"{cheaper.Count} cheaper (≈ ${(-cheaper.Sum(d => d.CostChangeUsd.Value)).ToString("0.00", c)} less)");
            if (dearer.Count > 0) parts.Add($"{dearer.Count} dearer (≈ ${dearer.Sum(d => d.CostChangeUsd.Value).ToString("0.00", c)} more)");
            double net = priced.Sum(d => d.CostChangeUsd.Value);
            return "At the logged tokens: " + string.Join(", ", parts)
                + $", net ≈ ${Math.Abs(net).ToString("0.00", c)} {(net <= 0 ? "less" : "more")}.";
        }

        public static List<ReplayDiff> Compare(IEnumerable<UsageRecord> records, ModelRouter router, out int replayed) =>
            Compare(records, router, null, out replayed);

        /// <summary>As above; with <paramref name="prices"/>, each difference is priced (<see cref="ReplayDiff.CostChangeUsd"/>).</summary>
        public static List<ReplayDiff> Compare(IEnumerable<UsageRecord> records, ModelRouter router, PriceTable prices, out int replayed)
        {
            var diffs = new List<ReplayDiff>();
            replayed = 0;
            foreach (var r in records ?? Enumerable.Empty<UsageRecord>())
            {
                if (!r.Routed || string.IsNullOrEmpty(r.PromptPreview) || string.IsNullOrEmpty(r.RouteModel)) continue;
                replayed++;
                // Replay as a fresh conversation: logged context size is not stored, so stickiness is left out.
                var d = router.Route(new RouteRequest { Prompt = r.PromptPreview, AttachedCodeLines = r.AttachedLines, AttachedFiles = r.AttachedFiles });
                if (!string.Equals(d.Model.ToCli(), r.RouteModel, StringComparison.OrdinalIgnoreCase))
                {
                    double? change = null;
                    if (prices != null && prices.For(d.Model.ToCli()) is ModelPrice now && prices.For(r.ModelUsed ?? r.RouteModel) is ModelPrice then)
                        change = now.Cost(r) - then.Cost(r);
                    diffs.Add(new ReplayDiff { PromptPreview = r.PromptPreview, LoggedModel = r.RouteModel, NowModel = d.Model.ToCli(), NowReason = d.Reason, CostChangeUsd = change });
                }
            }
            return diffs;
        }
    }
}
