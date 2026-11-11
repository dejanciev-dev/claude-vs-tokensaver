using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>The span a <see cref="SpendWatch"/> adds up: a local calendar day or month.</summary>
    public enum SpendPeriod { Day, Month }

    /// <summary>
    /// Says when the spend across all conversations in a day or a month passes another multiple of a set amount ($20,
    /// $40 … for 20). The total starts from the usage log (turns before this watch was made; <see cref="Load"/>) and
    /// adds each turn after that (<see cref="Add"/>), so a turn is never counted twice. What was spent before loading
    /// sets the starting step: restarting Visual Studio does not repeat a warning. A new day (month) starts again from 0.
    /// Thread-safe; one watch per period is shared by all chat windows.
    /// </summary>
    public sealed class SpendWatch
    {
        private readonly object _gate = new object();
        private readonly DateTime _createdUtc;
        private DateTime _start;
        private double _spent;
        private int _warnedSteps;

        public SpendPeriod Period { get; }

        /// <summary>The amount; 0 or less turns the warning off.</summary>
        public double StepUsd { get; set; }

        public SpendWatch(SpendPeriod period, double stepUsd, DateTime? createdUtc = null)
        {
            Period = period;
            StepUsd = stepUsd;
            _createdUtc = createdUtc ?? DateTime.UtcNow;
            _start = StartOf(_createdUtc);
        }

        /// <summary>The local day, or the first day of the local month, that <paramref name="utc"/> falls in.</summary>
        private DateTime StartOf(DateTime utc)
        {
            var d = utc.ToLocalTime().Date;
            return Period == SpendPeriod.Month ? new DateTime(d.Year, d.Month, 1) : d;
        }

        /// <summary>Spent so far in the current day or month (USD at API prices).</summary>
        public double Spent { get { lock (_gate) return _spent; } }

        /// <summary>Adds the logged turns of this period from before this watch was made, without warning about them.</summary>
        public void Load(IEnumerable<UsageRecord> records)
        {
            double before = 0;
            foreach (var r in records ?? new UsageRecord[0])
                if (r.Timestamp < _createdUtc && StartOf(r.Timestamp) == StartOf(_createdUtc)) before += r.CostUsd + r.RouterCostUsd;
            lock (_gate)
            {
                if (_start != StartOf(_createdUtc)) return; // a new period has started since: those turns belong to the last one
                _spent += before;
                if (StepUsd > 0) _warnedSteps = Math.Max(_warnedSteps, (int)Math.Floor(_spent / StepUsd + 1e-9));
            }
        }

        /// <summary>A turn finished at <paramref name="nowUtc"/>: the warning when the period's spend passed a new step, else null.</summary>
        public string Add(double costUsd, DateTime nowUtc)
        {
            var c = CultureInfo.InvariantCulture;
            lock (_gate)
            {
                var start = StartOf(nowUtc);
                if (start != _start) { _start = start; _spent = 0; _warnedSteps = 0; }
                if (costUsd > 0 && !double.IsNaN(costUsd)) _spent += costUsd;
                if (StepUsd <= 0 || _spent < StepUsd) return null;
                int steps = (int)Math.Floor(_spent / StepUsd + 1e-9);
                if (steps <= _warnedSteps) return null;
                _warnedSteps = steps;
                string which = Period == SpendPeriod.Month ? "This month's" : "Today's";
                return $"{which} Claude Code spend in Visual Studio has passed ${(steps * StepUsd).ToString("0.00", c)} "
                    + $"(${_spent.ToString("0.00", c)} so far in all conversations, at API prices). Tools → Claude Code → Show Savings Report lists the costliest turns.";
            }
        }
    }
}
