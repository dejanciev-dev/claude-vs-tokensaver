using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// Spend per local day across all conversations: the usage log's turns from before this history was made
    /// (<see cref="Load"/>) plus each turn after that (<see cref="Add"/>), so a turn is never counted twice. For the
    /// last days in the meter's tooltip. Thread-safe; one is shared by all chat windows.
    /// </summary>
    public sealed class SpendHistory
    {
        private readonly object _gate = new object();
        private readonly DateTime _createdUtc;
        private readonly Dictionary<DateTime, double> _byDay = new Dictionary<DateTime, double>();

        public SpendHistory(DateTime? createdUtc = null) { _createdUtc = createdUtc ?? DateTime.UtcNow; }

        /// <summary>Adds the logged turns from before this history was made.</summary>
        public void Load(IEnumerable<UsageRecord> records)
        {
            var before = new Dictionary<DateTime, double>();
            foreach (var r in records ?? new UsageRecord[0])
            {
                if (r.Timestamp >= _createdUtc) continue;
                var day = r.Timestamp.ToLocalTime().Date;
                before[day] = (before.TryGetValue(day, out var v) ? v : 0) + r.CostUsd + r.RouterCostUsd;
            }
            lock (_gate)
                foreach (var kv in before) _byDay[kv.Key] = (_byDay.TryGetValue(kv.Key, out var v) ? v : 0) + kv.Value;
        }

        /// <summary>A turn that finished at <paramref name="nowUtc"/> cost this much.</summary>
        public void Add(double costUsd, DateTime nowUtc)
        {
            if (!(costUsd > 0)) return;
            var day = nowUtc.ToLocalTime().Date;
            lock (_gate) _byDay[day] = (_byDay.TryGetValue(day, out var v) ? v : 0) + costUsd;
        }

        /// <summary>Spent on the local day <paramref name="day"/>.</summary>
        public double On(DateTime day) { lock (_gate) return _byDay.TryGetValue(day.Date, out var v) ? v : 0; }

        /// <summary>
        /// "Last 7 days: Thu $3.10 · Fri $5.40 · Sat – · Sun – · Mon $1.20 · Tue $8.00 · today $2.00 ($19.70 in all)",
        /// oldest first; null when nothing was spent in them.
        /// </summary>
        public string LastDaysLine(DateTime nowUtc, int days = 7)
        {
            var c = CultureInfo.InvariantCulture;
            var today = nowUtc.ToLocalTime().Date;
            var list = Enumerable.Range(0, days).Select(i => today.AddDays(i - days + 1)).Select(d => (Day: d, Usd: On(d))).ToList();
            double total = list.Sum(d => d.Usd);
            if (total <= 0) return null;
            return $"Last {days} days: " + string.Join(" · ", list.Select(d =>
                    (d.Day == today ? "today" : d.Day.ToString("ddd", c)) + " " + (d.Usd > 0 ? "$" + d.Usd.ToString("0.00", c) : "–")))
                + $" (${total.ToString("0.00", c)} in all)";
        }
    }
}
