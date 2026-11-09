using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// Says when today's spend across all conversations passes another multiple of a set amount ($20, $40 … for 20).
    /// Today's total starts from the usage log (turns before this watch was made; <see cref="Load"/>) and adds each
    /// turn after that (<see cref="Add"/>), so a turn is never counted twice. What was spent before loading sets the
    /// starting step: restarting Visual Studio does not repeat a warning. A new day starts again from 0.
    /// Thread-safe; one watch is shared by all chat windows.
    /// </summary>
    public sealed class DailyCostWatch
    {
        private readonly object _gate = new object();
        private readonly DateTime _createdUtc;
        private DateTime _day;
        private double _spent;
        private int _warnedSteps;

        /// <summary>The amount; 0 or less turns the warning off.</summary>
        public double StepUsd { get; set; }

        public DailyCostWatch(double stepUsd = 20, DateTime? createdUtc = null)
        {
            StepUsd = stepUsd;
            _createdUtc = createdUtc ?? DateTime.UtcNow;
            _day = _createdUtc.ToLocalTime().Date;
        }

        /// <summary>Spent so far on the current day (USD at API prices).</summary>
        public double SpentToday { get { lock (_gate) return _spent; } }

        /// <summary>Adds the logged turns of today from before this watch was made, without warning about them.</summary>
        public void Load(IEnumerable<UsageRecord> records)
        {
            double before = 0;
            foreach (var r in records ?? new UsageRecord[0])
                if (r.Timestamp < _createdUtc && r.Timestamp.ToLocalTime().Date == _day) before += r.CostUsd + r.RouterCostUsd;
            lock (_gate)
            {
                if (_day != _createdUtc.ToLocalTime().Date) return; // the day has changed since: those turns are yesterday's
                _spent += before;
                if (StepUsd > 0) _warnedSteps = Math.Max(_warnedSteps, (int)Math.Floor(_spent / StepUsd + 1e-9));
            }
        }

        /// <summary>A turn finished at <paramref name="nowUtc"/>: the warning when today's spend passed a new step, else null.</summary>
        public string Add(double costUsd, DateTime nowUtc)
        {
            var c = CultureInfo.InvariantCulture;
            lock (_gate)
            {
                var today = nowUtc.ToLocalTime().Date;
                if (today != _day) { _day = today; _spent = 0; _warnedSteps = 0; }
                if (costUsd > 0 && !double.IsNaN(costUsd)) _spent += costUsd;
                if (StepUsd <= 0 || _spent < StepUsd) return null;
                int steps = (int)Math.Floor(_spent / StepUsd + 1e-9);
                if (steps <= _warnedSteps) return null;
                _warnedSteps = steps;
                return $"Today's Claude Code spend in Visual Studio has passed ${(steps * StepUsd).ToString("0.00", c)} "
                    + $"(${_spent.ToString("0.00", c)} so far in all conversations, at API prices). Tools → Claude Code → Show Savings Report lists the costliest turns.";
            }
        }
    }
}
