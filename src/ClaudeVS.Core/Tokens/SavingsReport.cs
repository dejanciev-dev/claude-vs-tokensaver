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

        public double Cost(UsageRecord r) =>
            (r.InputTokens * Input + r.OutputTokens * Output + r.CacheWriteTokens * CacheWrite + r.CacheReadTokens * CacheRead) / 1_000_000.0;

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

        public double CacheSavedUsd(Cli.TokenUsage u) => CacheSavedUsd(u.CacheRead, u.CacheCreation, u.CacheCreation1h);

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
    /// Prices for the model families behind Claude Code's haiku / sonnet / opus aliases.
    /// Defaults are Anthropic API list prices as of 2026-10 (Opus 5.5, Sonnet 5.5, Haiku 5.5);
    /// cache write = 1.25x input. Users can override them in Options when prices change.
    /// </summary>
    public sealed class PriceTable
    {
        public const string DefaultOpus = "4, 20, 5, 0.2";
        public const string DefaultSonnet = "2, 10, 2.5, 0.2";
        public const string DefaultHaiku = "0.1, 0.5, 0.125, 0.01";

        public ModelPrice Opus { get; set; } = ModelPrice.Parse(DefaultOpus);
        public ModelPrice Sonnet { get; set; } = ModelPrice.Parse(DefaultSonnet);
        public ModelPrice Haiku { get; set; } = ModelPrice.Parse(DefaultHaiku);

        public static PriceTable FromStrings(string opus, string sonnet, string haiku) => new PriceTable
        {
            Opus = ModelPrice.Parse(opus) ?? ModelPrice.Parse(DefaultOpus),
            Sonnet = ModelPrice.Parse(sonnet) ?? ModelPrice.Parse(DefaultSonnet),
            Haiku = ModelPrice.Parse(haiku) ?? ModelPrice.Parse(DefaultHaiku),
        };

        /// <summary>"opus", "sonnet", "haiku" or null, from a model id such as "claude-sonnet-5-5" or an alias.</summary>
        public static string Family(string model)
        {
            string m = (model ?? "").ToLowerInvariant();
            if (m.Contains("opus")) return "opus";
            if (m.Contains("sonnet")) return "sonnet";
            if (m.Contains("haiku")) return "haiku";
            return null;
        }

        public ModelPrice For(string model)
        {
            switch (Family(model))
            {
                case "opus": return Opus;
                case "sonnet": return Sonnet;
                case "haiku": return Haiku;
                default: return null;
            }
        }
    }

    public sealed class ModelBreakdown
    {
        public string Family { get; set; }
        public int Turns { get; set; }
        public double CostUsd { get; set; }
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
        public long OutputTokens { get; set; }
        /// <summary>Of <see cref="OutputTokens"/>: thinking, and what it cost at each turn's output price.</summary>
        public long ThinkingTokens { get; set; }
        public double ThinkingCostUsd { get; set; }
        public List<ModelBreakdown> ByModel { get; } = new List<ModelBreakdown>();
        /// <summary>Turns routed by the Haiku router, how many picked the same model as the local rules, its cost and time.</summary>
        public int HaikuRouted { get; set; }
        public int HaikuAgreed { get; set; }
        public double RouterCostUsd { get; set; }
        public long RouterAverageMs { get; set; }
        /// <summary>The dearest turns, dearest first (at most <see cref="CostliestCount"/>).</summary>
        public List<UsageRecord> Costliest { get; } = new List<UsageRecord>();
        public const int CostliestCount = 5;

        /// <summary>Cache hit % per day, oldest first.</summary>
        public List<KeyValuePair<DateTime, double>> CacheTrend { get; } = new List<KeyValuePair<DateTime, double>>();

        public static SavingsReport Build(IEnumerable<UsageRecord> records, PriceTable prices, string solution = null, DateTime? sinceUtc = null)
        {
            prices = prices ?? new PriceTable();
            var list = (records ?? Enumerable.Empty<UsageRecord>())
                .Where(r => solution == null || string.Equals(r.Solution, solution, StringComparison.OrdinalIgnoreCase))
                .Where(r => !sinceUtc.HasValue || r.Timestamp >= sinceUtc.Value)
                .OrderBy(r => r.Timestamp)
                .ToList();

            var rep = new SavingsReport { Solution = solution, Turns = list.Count, Since = list.Count > 0 ? list[0].Timestamp : (DateTime?)null };
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
                }
                rep.OpusEquivalentUsd += PriceTable.Family(r.ModelUsed) == "opus" ? r.CostUsd : prices.Opus.Cost(r);
                long avoided = r.CharsAvoided / 4;
                rep.TokensAvoided += avoided;
                rep.ReferencesSavedUsd += avoided * (prices.For(r.ModelUsed) ?? prices.Sonnet).Input / 1_000_000.0;
                input += r.InputTokens + r.CacheReadTokens + r.CacheWriteTokens;
                cacheRead += r.CacheReadTokens;
                rep.OutputTokens += r.OutputTokens;
                rep.ThinkingTokens += r.ThinkingTokens;
                rep.ThinkingCostUsd += r.ThinkingTokens * (prices.For(r.ModelUsed) ?? prices.Sonnet).Output / 1_000_000.0;
            }
            rep.CacheHitPercent = input > 0 ? 100.0 * cacheRead / input : 0;
            var timed = list.Where(r => r.RouterMs > 0).ToList();
            rep.RouterAverageMs = timed.Count > 0 ? (long)timed.Average(r => r.RouterMs) : 0;

            rep.ByModel.AddRange(list
                .GroupBy(r => PriceTable.Family(r.ModelUsed) ?? "other")
                .Select(g => new ModelBreakdown { Family = g.Key, Turns = g.Count(), CostUsd = g.Sum(r => r.CostUsd) })
                .OrderByDescending(b => b.Turns));

            rep.Costliest.AddRange(list.Where(r => r.CostUsd > 0).OrderByDescending(r => r.CostUsd).Take(CostliestCount));

            foreach (var day in list.GroupBy(r => r.Timestamp.ToLocalTime().Date))
            {
                long dIn = day.Sum(r => r.InputTokens + r.CacheReadTokens + r.CacheWriteTokens);
                long dRead = day.Sum(r => r.CacheReadTokens);
                rep.CacheTrend.Add(new KeyValuePair<DateTime, double>(day.Key, dIn > 0 ? 100.0 * dRead / dIn : 0));
            }
            return rep;
        }

        private static string OneLine(string s, int max)
        {
            string one = string.Join(" ", (s ?? "").Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return one.Length <= max ? one : one.Substring(0, max - 1) + "…";
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Claude Code — savings report" + (Solution != null ? " for " + Solution : " (all solutions)"));
            if (Turns == 0) { sb.AppendLine("No turns recorded yet. Turn on Options > Diagnostics > Keep local usage log."); return sb.ToString(); }

            sb.AppendLine($"Period: {Since.Value.ToLocalTime():yyyy-MM-dd} to today · {Turns} turns ({RoutedTurns} auto-routed)");
            sb.AppendLine();
            sb.AppendLine($"Actual cost:                ${ActualCostUsd:0.00}");
            sb.AppendLine($"Same tokens at Opus prices: ${OpusEquivalentUsd:0.00}  (estimate)");
            sb.AppendLine($"Saved by model routing:     ${RoutingSavedUsd:0.00}  ({RoutingSavedPercent:0}%)");
            sb.AppendLine($"Saved by file references:   ~{TokenMeter.Format(TokensAvoided)} tokens ≈ ${ReferencesSavedUsd:0.00}");
            sb.AppendLine($"Cache hit rate:             {CacheHitPercent:0}%");
            if (ThinkingTokens > 0)
                sb.AppendLine($"Thinking:                   {TokenMeter.Format(ThinkingTokens)} tokens ≈ ${ThinkingCostUsd:0.00}  ({(OutputTokens > 0 ? 100.0 * ThinkingTokens / OutputTokens : 0):0}% of output tokens; Thinking off or lower effort cuts it)");
            if (HaikuRouted > 0 || RouterCostUsd > 0)
            {
                sb.AppendLine($"Haiku router:               {HaikuRouted} prompts, same model as the local rules {(HaikuRouted > 0 ? 100.0 * HaikuAgreed / HaikuRouted : 0):0}% of the time");
                sb.AppendLine($"                            cost ${RouterCostUsd:0.000} (included above), {RouterAverageMs / 1000.0:0.0} s per prompt on average");
            }
            sb.AppendLine();
            sb.AppendLine("By model:");
            foreach (var b in ByModel)
                sb.AppendLine($"  {b.Family,-7} {b.Turns,5} turns  ${b.CostUsd,8:0.00}  ({100.0 * b.Turns / Turns:0}% of turns)");
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
            if (CacheTrend.Count > 1)
            {
                sb.AppendLine();
                sb.AppendLine("Cache hit rate by day:");
                foreach (var d in CacheTrend.Skip(Math.Max(0, CacheTrend.Count - 14)))
                    sb.AppendLine($"  {d.Key:yyyy-MM-dd}  {d.Value,3:0}%  {new string('█', (int)Math.Round(d.Value / 5))}");
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
    }

    /// <summary>
    /// Re-runs logged prompts (only those recorded with the opt-in prompt preview) through the current
    /// router, to see the effect of tuning keywords and thresholds on real usage.
    /// </summary>
    public static class RouterReplay
    {
        public static List<ReplayDiff> Compare(IEnumerable<UsageRecord> records, ModelRouter router, out int replayed)
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
                    diffs.Add(new ReplayDiff { PromptPreview = r.PromptPreview, LoggedModel = r.RouteModel, NowModel = d.Model.ToCli(), NowReason = d.Reason });
            }
            return diffs;
        }
    }
}
