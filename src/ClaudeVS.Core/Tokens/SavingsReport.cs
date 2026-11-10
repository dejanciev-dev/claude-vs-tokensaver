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

        /// <summary>One request's (or a sum of requests') cost: 1-hour cache writes at twice the input price, the rest at <see cref="CacheWrite"/>.</summary>
        public double Cost(Cli.TokenUsage u)
        {
            long write1h = Math.Min(Math.Max(0, u.CacheCreation1h), Math.Max(0, u.CacheCreation));
            long write5m = Math.Max(0, u.CacheCreation) - write1h;
            return (u.Input * Input + u.Output * Output + write5m * CacheWrite + write1h * 2 * Input + u.CacheRead * CacheRead) / 1_000_000.0;
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
    /// Prices for the model families behind Claude Code's haiku / sonnet / opus / fable aliases.
    /// Defaults are Anthropic API list prices as of 2026-11 (Opus 5.5, Sonnet 5.5, Haiku 5.5, Fable 5.2);
    /// cache write = 1.25x input. Users can override them in Options when prices change.
    /// </summary>
    public sealed class PriceTable
    {
        public const string DefaultOpus = "4, 20, 5, 0.2";
        public const string DefaultSonnet = "2, 10, 2.5, 0.2";
        public const string DefaultHaiku = "0.1, 0.5, 0.125, 0.01";
        /// <summary>Fable 5.2 (Claude Code 2.1.321): $8 / $40 per Mtok, cache read $0.20.</summary>
        public const string DefaultFable = "8, 40, 10, 0.2";

        public ModelPrice Opus { get; set; } = ModelPrice.Parse(DefaultOpus);
        public ModelPrice Sonnet { get; set; } = ModelPrice.Parse(DefaultSonnet);
        public ModelPrice Haiku { get; set; } = ModelPrice.Parse(DefaultHaiku);
        public ModelPrice Fable { get; set; } = ModelPrice.Parse(DefaultFable);

        public static PriceTable FromStrings(string opus, string sonnet, string haiku, string fable = null) => new PriceTable
        {
            Opus = ModelPrice.Parse(opus) ?? ModelPrice.Parse(DefaultOpus),
            Sonnet = ModelPrice.Parse(sonnet) ?? ModelPrice.Parse(DefaultSonnet),
            Haiku = ModelPrice.Parse(haiku) ?? ModelPrice.Parse(DefaultHaiku),
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
        /// <summary>The folder's name ("linq-samples"), or "(no folder)".</summary>
        public string Name => string.IsNullOrEmpty(Solution) ? "(no folder)" : System.IO.Path.GetFileName(Solution.TrimEnd('\\', '/')) is string n && n.Length > 0 ? n : Solution;
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
        public List<ModelBreakdown> ByModel { get; } = new List<ModelBreakdown>();
        /// <summary>Turns routed by the Haiku router, how many picked the same model as the local rules, its cost and time.</summary>
        public int HaikuRouted { get; set; }
        public int HaikuAgreed { get; set; }
        public double RouterCostUsd { get; set; }
        public long RouterAverageMs { get; set; }
        /// <summary>The dearest turns, dearest first (at most <see cref="CostliestCount"/>).</summary>
        public List<UsageRecord> Costliest { get; } = new List<UsageRecord>();
        public const int CostliestCount = 5;

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

        public static SavingsReport Build(IEnumerable<UsageRecord> records, PriceTable prices, string solution = null, DateTime? sinceUtc = null, DateTime? nowUtc = null)
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
                rep.CacheReadTokens += r.CacheReadTokens;
                rep.CacheSavedUsd += (prices.For(r.ModelUsed) ?? prices.Sonnet).CacheSavedUsd(r.CacheReadTokens, r.CacheWriteTokens, r.CacheWrite1hTokens);
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
                rep.ByDay.Add(new DayBreakdown
                {
                    Day = day.Key,
                    Turns = day.Count(),
                    Tokens = day.Sum(r => r.TotalTokens),
                    CostUsd = day.Sum(r => r.CostUsd + r.RouterCostUsd),
                    CacheSavedUsd = day.Sum(r => (prices.For(r.ModelUsed) ?? prices.Sonnet).CacheSavedUsd(r.CacheReadTokens, r.CacheWriteTokens, r.CacheWrite1hTokens)),
                    CacheHitPercent = dIn > 0 ? 100.0 * dRead / dIn : 0,
                });
            }
            var today = (nowUtc ?? DateTime.UtcNow).ToLocalTime().Date;
            rep.Month = new DateTime(today.Year, today.Month, 1);
            rep.MonthToDateUsd = list.Where(r => r.Timestamp.ToLocalTime().Date >= rep.Month && r.Timestamp.ToLocalTime().Date <= today).Sum(r => r.CostUsd + r.RouterCostUsd);
            if (today.Day >= ProjectFromDay && rep.MonthToDateUsd > 0)
                rep.MonthProjectedUsd = rep.MonthToDateUsd / today.Day * DateTime.DaysInMonth(today.Year, today.Month);

            if (solution == null)
                rep.BySolution.AddRange(list
                    .GroupBy(r => (r.Solution ?? "").TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new SolutionBreakdown { Solution = g.Key.Length == 0 ? null : g.First().Solution, Turns = g.Count(), CostUsd = g.Sum(r => r.CostUsd + r.RouterCostUsd) })
                    .OrderByDescending(b => b.CostUsd).ThenByDescending(b => b.Turns));
            return rep;
        }

        /// <summary>"This month: $12.30 so far, ~$41.00 at this pace by Nov 30"; null with nothing spent this month.</summary>
        public string MonthLine()
        {
            if (MonthToDateUsd <= 0) return null;
            var monthEnd = Month.AddDays(DateTime.DaysInMonth(Month.Year, Month.Month) - 1);
            string line = $"This month:                 ${MonthToDateUsd:0.00} so far";
            return MonthProjectedUsd is double p
                ? line + $", ~${p:0.00} at this pace by {monthEnd:MMM d}"
                : line;
        }

        /// <summary>"By solution:" lines for a report over all solutions; empty with fewer than two solutions.</summary>
        public string SolutionsText()
        {
            if (BySolution.Count < 2) return "";
            var sb = new StringBuilder();
            sb.AppendLine("By solution:");
            foreach (var b in BySolution.Take(SolutionsShown))
                sb.AppendLine($"  {OneLine(b.Name, 30),-30} {b.Turns,5} turns  ${b.CostUsd,8:0.00}");
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
            sb.AppendLine("Claude Code — savings report" + (Solution != null ? " for " + Solution : " (all solutions)"));
            if (Turns == 0) { sb.AppendLine("No turns recorded yet. Turn on Options > Diagnostics > Keep local usage log."); return sb.ToString(); }

            sb.AppendLine($"Period: {Since.Value.ToLocalTime():yyyy-MM-dd} to today · {Turns} turns ({RoutedTurns} auto-routed)");
            sb.AppendLine();
            sb.AppendLine($"Actual cost:                ${ActualCostUsd:0.00}");
            if (MonthLine() is string month) sb.AppendLine(month);
            sb.AppendLine($"Same tokens at Opus prices: ${OpusEquivalentUsd:0.00}  (estimate)");
            sb.AppendLine($"Saved by model routing:     ${RoutingSavedUsd:0.00}  ({RoutingSavedPercent:0}%)");
            sb.AppendLine($"Saved by file references:   ~{TokenMeter.Format(TokensAvoided)} tokens ≈ ${ReferencesSavedUsd:0.00}");
            sb.AppendLine($"Saved by the prompt cache:  ${CacheSavedUsd:0.00}  ({TokenMeter.Format(CacheReadTokens)} tokens read from the cache instead of sent in full)");
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
