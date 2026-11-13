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

        /// <summary>The logged turns that cost something, cheapest first (with or without a session id).</summary>
        private List<double> _turns = new List<double>();

        /// <summary>Fewer logged turns than this say little about your usual turn: no note.</summary>
        public const int MinTurns = 20;
        /// <summary>A turn this many times the median logged turn, and costing at least <see cref="TurnNoteFromUsd"/>, gets a note.</summary>
        public const double TurnNoteTimes = 5;
        public const double TurnNoteFromUsd = 0.05;

        public void Load(IEnumerable<UsageRecord> records)
        {
            var by = new Dictionary<string, double>(StringComparer.Ordinal);
            var turns = new List<double>();
            foreach (var r in records ?? new UsageRecord[0])
            {
                double cost = r.CostUsd + r.RouterCostUsd;
                if (cost > 0) turns.Add(cost);
                if (!string.IsNullOrEmpty(r.SessionId))
                    by[r.SessionId] = (by.TryGetValue(r.SessionId, out var v) ? v : 0) + cost;
            }
            turns.Sort();
            lock (_gate) { _bySession = by; _turns = turns; }
        }

        /// <summary>The median cost of the logged turns that cost something, and how many there are.</summary>
        public (double MedianUsd, int Count) TurnMedian()
        {
            List<double> costs;
            lock (_gate) costs = _turns;
            if (costs.Count == 0) return (0, 0);
            int mid = costs.Count / 2;
            return (costs.Count % 2 == 1 ? costs[mid] : (costs[mid - 1] + costs[mid]) / 2, costs.Count);
        }

        /// <summary>
        /// For the turn footer: "6× your usual turn" when <paramref name="turnUsd"/> is <see cref="TurnNoteTimes"/> or
        /// more the median logged turn and at least <see cref="TurnNoteFromUsd"/>; null otherwise, or with fewer than
        /// <see cref="MinTurns"/> logged turns.
        /// </summary>
        public string TurnNote(double turnUsd)
        {
            var (median, count) = TurnMedian();
            if (count < MinTurns || median <= 0 || turnUsd < TurnNoteFromUsd) return null;
            double times = turnUsd / median + 1e-9; // 0.12 / 0.02 is 5.999… in doubles
            if (times < TurnNoteTimes) return null;
            return ((int)Math.Floor(times)).ToString(CultureInfo.InvariantCulture) + "× your usual turn";
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
