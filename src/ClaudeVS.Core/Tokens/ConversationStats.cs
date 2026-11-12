using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// What logged conversations cost (turns grouped by session id, from the usage log), to set the open one against:
    /// "This conversation $2.40 · 20× the median logged conversation ($0.12 of 32)". Loaded once, in the background;
    /// thread-safe. Turns logged without a session id are left out, as are conversations that cost nothing.
    /// </summary>
    public sealed class ConversationStats
    {
        private readonly object _gate = new object();
        private Dictionary<string, double> _bySession = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>Fewer other conversations than this say little: no comparison.</summary>
        public const int MinConversations = 3;

        public void Load(IEnumerable<UsageRecord> records)
        {
            var by = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var r in records ?? new UsageRecord[0])
                if (!string.IsNullOrEmpty(r.SessionId))
                    by[r.SessionId] = (by.TryGetValue(r.SessionId, out var v) ? v : 0) + r.CostUsd + r.RouterCostUsd;
            lock (_gate) _bySession = by;
        }

        /// <summary>The median cost and the number of logged conversations, leaving out <paramref name="exceptSession"/> (the open one).</summary>
        public (double MedianUsd, int Count) Median(string exceptSession = null)
        {
            List<double> costs;
            lock (_gate) costs = _bySession.Where(kv => kv.Value > 0 && kv.Key != exceptSession).Select(kv => kv.Value).OrderBy(v => v).ToList();
            if (costs.Count == 0) return (0, 0);
            int mid = costs.Count / 2;
            return (costs.Count % 2 == 1 ? costs[mid] : (costs[mid - 1] + costs[mid]) / 2, costs.Count);
        }

        /// <summary>
        /// For the meter's tooltip: "This conversation $2.40 · 20× the median logged conversation ($0.12 of 32)", or
        /// "… · the median logged conversation $0.12 (of 32)" when it is under twice the median; null with nothing spent
        /// yet or fewer than <see cref="MinConversations"/> others logged.
        /// </summary>
        public string Line(double conversationUsd, string sessionId)
        {
            var (median, count) = Median(sessionId);
            if (!(conversationUsd > 0) || count < MinConversations || median <= 0) return null;
            var c = CultureInfo.InvariantCulture;
            string now = "This conversation $" + conversationUsd.ToString("0.00", c);
            double times = conversationUsd / median;
            if (times >= 2)
                return now + " · " + times.ToString(times >= 10 ? "0" : "0.#", c) + "× the median logged conversation ($" + median.ToString("0.00", c) + " of " + count + ")";
            return now + " · the median logged conversation $" + median.ToString("0.00", c) + " (of " + count + ")";
        }
    }
}
