using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeVS.Core.Tokens
{
    public sealed partial class PriceTable
    {
        // Models that aren't the four current ones. Options → Pricing prices four models (the defaults are theirs). A
        // model set another way (settings.json, a hook, ANTHROPIC_DEFAULT_HAIKU_MODEL, an older ID) has its family's name
        // but not its price: Haiku 4.5 bills 10× Haiku 5.5. Its price is learned from Claude Code's own costUSD for it
        // (checked live with --live-prices older), kept for the VS session and logged with each turn. Not taken from
        // what the aliases resolve to: with that variable set, "haiku" resolves to Haiku 4.5.

        /// <summary>The models Options → Pricing prices; raise with the defaults when Claude Code's aliases move on.</summary>
        public static readonly IReadOnlyCollection<string> PricedIds = new HashSet<string>(
            new[] { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-2" }, StringComparer.OrdinalIgnoreCase);

        private static readonly object LearnGate = new object();
        private static readonly Dictionary<string, ModelPrice> Learned = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True for an alias or one of <see cref="PricedIds"/>: priced by Options → Pricing as is.</summary>
        public static bool IsCurrent(string model)
        {
            string id = Cli.ModelCatalog.BareId(model);
            if (id == null || !id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) return true; // an alias ("haiku")
            return PricedIds.Contains(id);
        }

        /// <summary>Forgets learned prices (tests).</summary>
        public static void ResetLearned()
        {
            lock (LearnGate) Learned.Clear();
        }

        /// <summary>A learned price for a model that isn't current; null when none.</summary>
        public static ModelPrice LearnedPrice(string model)
        {
            string id = Cli.ModelCatalog.BareId(model);
            if (id == null) return null;
            lock (LearnGate) return Learned.TryGetValue(id, out var p) ? p : null;
        }

        /// <summary>Below this much spent on a model, its costUSD is too coarse to learn a price from.</summary>
        public const double LearnFromUsd = 0.0005;

        /// <summary>
        /// Learns the price of each model in <paramref name="billed"/> (a result's <c>model_usage_since_last_result</c>)
        /// that isn't current: its family's price here, scaled to what Claude Code says it cost. Not from a turn that
        /// ran partly fast (the per-model lines don't say which tokens) or one with requests over the long-context tier.
        /// </summary>
        public void Learn(IReadOnlyDictionary<string, Cli.ModelUsageEntry> billed, bool skip = false)
        {
            if (billed == null || skip) return;
            foreach (var kv in billed)
            {
                if (IsCurrent(kv.Key) || kv.Value == null || kv.Value.CostUsd < LearnFromUsd) continue;
                var family = For(Family(kv.Key)); // the family's own price, by alias
                if (family == null) continue;
                var plain = new ModelPrice { Input = family.Input, Output = family.Output, CacheWrite = family.CacheWrite, CacheRead = family.CacheRead };
                double atFamily = plain.Cost(kv.Value.Usage);
                if (atFamily <= 0) continue;
                double x = kv.Value.CostUsd / atFamily;
                double R(double v) => Math.Round(v * x, 6); // "1, 5, 1.25, 0.1" in the log, not 0.9999999999
                var learned = new ModelPrice { Input = R(plain.Input), Output = R(plain.Output), CacheWrite = R(plain.CacheWrite), CacheRead = R(plain.CacheRead) };
                lock (LearnGate) Learned[Cli.ModelCatalog.BareId(kv.Key)] = learned;
            }
        }

        /// <summary>
        /// For a logged turn (<see cref="UsageRecord.Prices"/>): the learned prices of <paramref name="models"/> that aren't
        /// current, as "input, output, cacheWrite, cacheRead"; null when none.
        /// </summary>
        public static Dictionary<string, string> LearnedPrices(IEnumerable<string> models)
        {
            Dictionary<string, string> d = null;
            foreach (var m in models ?? Enumerable.Empty<string>())
            {
                if (IsCurrent(m) || !(LearnedPrice(m) is ModelPrice p)) continue;
                (d = d ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[Cli.ModelCatalog.BareId(m)] = p.ToString();
            }
            return d;
        }

        /// <summary>
        /// Takes the prices logged with past turns (<see cref="UsageRecord.Prices"/>), oldest first, so a report in a later
        /// VS session prices an older model as it was billed. A price learned in this VS session wins.
        /// </summary>
        public static void Remember(IEnumerable<UsageRecord> records)
        {
            var logged = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in (records ?? Enumerable.Empty<UsageRecord>()).Where(r => r?.Prices != null).OrderBy(r => r.Timestamp))
                foreach (var kv in r.Prices)
                    if (!IsCurrent(kv.Key) && ModelPrice.Parse(kv.Value) is ModelPrice p) logged[Cli.ModelCatalog.BareId(kv.Key)] = p;
            if (logged.Count == 0) return;
            lock (LearnGate)
                foreach (var kv in logged)
                    if (!Learned.ContainsKey(kv.Key)) Learned[kv.Key] = kv.Value;
        }
    }
}
