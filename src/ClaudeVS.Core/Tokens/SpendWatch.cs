using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>The span a <see cref="SpendWatch"/> adds up: a local calendar day, month, or week from Monday.</summary>
    public enum SpendPeriod { Day, Month, Week }

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

        /// <summary>Only this solution folder's turns count (its own conversations); null: all of them.</summary>
        public string Solution { get; }

        private bool InSolution(string folder) =>
            Solution == null || string.Equals((folder ?? "").TrimEnd('\\', '/'), Solution.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        /// <summary>The amount; 0 or less turns the warning off.</summary>
        public double StepUsd { get; set; }

        public SpendWatch(SpendPeriod period, double stepUsd, DateTime? createdUtc = null, string solution = null)
        {
            Period = period;
            Solution = string.IsNullOrEmpty(solution) ? null : solution;
            StepUsd = stepUsd;
            _createdUtc = createdUtc ?? DateTime.UtcNow;
            _start = StartOf(_createdUtc);
        }

        /// <summary>The local day, or the first day of the local month, that <paramref name="utc"/> falls in.</summary>
        private DateTime StartOf(DateTime utc)
        {
            var d = utc.ToLocalTime().Date;
            if (Period == SpendPeriod.Week) return d.AddDays(-(((int)d.DayOfWeek + 6) % 7)); // Monday, as the report's week
            return Period == SpendPeriod.Month ? new DateTime(d.Year, d.Month, 1) : d;
        }

        /// <summary>Spent so far in the current day or month (USD at API prices).</summary>
        public double Spent { get { lock (_gate) return _spent; } }

        /// <summary>Spent in the period that <paramref name="nowUtc"/> falls in: 0 when a new one has started since the last turn.</summary>
        public double SpentAt(DateTime nowUtc) { lock (_gate) return StartOf(nowUtc) == _start ? _spent : 0; }

        /// <summary>The period's spend at <paramref name="nowUtc"/> has passed the warning amount (false when the warning is off).</summary>
        public bool IsPastWarning(DateTime nowUtc) => StepUsd > 0 && SpentAt(nowUtc) >= StepUsd;

        /// <summary>
        /// The month's total at the pace of its days so far (local days, today included), from the
        /// <see cref="SavingsReport.ProjectFromDay"/>rd as the report does; null for a daily watch, earlier in the month
        /// or with nothing spent.
        /// </summary>
        public double? PaceUsd(DateTime nowUtc)
        {
            if (Period != SpendPeriod.Month) return null;
            var today = nowUtc.ToLocalTime().Date;
            double spent = SpentAt(nowUtc);
            if (today.Day < SavingsReport.ProjectFromDay || spent <= 0) return null;
            return spent / today.Day * DateTime.DaysInMonth(today.Year, today.Month);
        }

        /// <summary>The pace warning waits for this day of the month (a few days are too few to go by)…</summary>
        public const int PaceWarnFromDay = 7;
        /// <summary>…and is not given with fewer days than this left in the month (too late to slow down).</summary>
        public const int PaceWarnDaysLeft = 3;
        private DateTime _paceWarned; // the month (its 1st) of the last pace warning

        /// <summary>The next multiple of <see cref="StepUsd"/> above <paramref name="spent"/>.</summary>
        private double NextStep(double spent) => (Math.Floor(spent / StepUsd + 1e-9) + 1) * StepUsd;

        /// <summary>The month's pace at <paramref name="nowUtc"/> passes the next warning amount before the month ends.</summary>
        private bool PaceIsPastNext(DateTime nowUtc)
        {
            if (Period != SpendPeriod.Month || StepUsd <= 0) return false;
            var today = nowUtc.ToLocalTime().Date;
            if (today.Day < PaceWarnFromDay || DateTime.DaysInMonth(today.Year, today.Month) - today.Day < PaceWarnDaysLeft) return false;
            return PaceUsd(nowUtc) is double pace && pace >= NextStep(SpentAt(nowUtc));
        }

        /// <summary>
        /// After a turn: once a month, from the <see cref="PaceWarnFromDay"/>th and while there are
        /// <see cref="PaceWarnDaysLeft"/> days or more left, the note that this month's pace would pass the next warning
        /// amount by its end; else null. Not when the pace had passed it already when the usage log was loaded (so
        /// restarting Visual Studio does not repeat it).
        /// </summary>
        public string PaceWarning(DateTime nowUtc)
        {
            var c = CultureInfo.InvariantCulture;
            if (!PaceIsPastNext(nowUtc)) return null;
            double spent = SpentAt(nowUtc), pace = PaceUsd(nowUtc) ?? 0;
            lock (_gate)
            {
                if (_paceWarned == StartOf(nowUtc)) return null;
                _paceWarned = StartOf(nowUtc);
            }
            string where = Solution != null ? "on " + SolutionBreakdown.NameOf(Solution) : "in Visual Studio";
            string which = Solution != null ? "monthly warning per solution" : "monthly warning";
            return $"At this pace, this month's Claude Code spend {where} will reach ~${pace.ToString("0.00", c)} by "
                + $"{MonthEnd(nowUtc).ToString("MMM d", c)}, past your {which} at ${NextStep(spent).ToString("0.00", c)} "
                + $"(${spent.ToString("0.00", c)} so far, at API prices). Tools → Claude Code → Show Savings Report lists the costliest turns and conversations.";
        }

        /// <summary>The last day of the local month <paramref name="nowUtc"/> falls in.</summary>
        private static DateTime MonthEnd(DateTime nowUtc)
        {
            var today = nowUtc.ToLocalTime().Date;
            return new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        }

        /// <summary>
        /// "today $3.20 (next warning at $20.00)", "this month $41.00, ~$137.33 by Nov 30 at this pace" without a warning
        /// amount (the pace from the 3rd, <see cref="PaceUsd"/>); the next warning is the
        /// next multiple of <see cref="StepUsd"/> above what was spent.
        /// </summary>
        public string Line(DateTime nowUtc)
        {
            var c = CultureInfo.InvariantCulture;
            double spent = SpentAt(nowUtc);
            string line = (Period == SpendPeriod.Month ? "this month $" : Period == SpendPeriod.Week ? "this week $" : "today $") + spent.ToString("0.00", c);
            if (PaceUsd(nowUtc) is double pace)
                line += ", ~$" + pace.ToString("0.00", c) + " by " + MonthEnd(nowUtc).ToString("MMM d", c) + " at this pace";
            if (StepUsd <= 0) return line;
            return line + " (next warning at $" + NextStep(spent).ToString("0.00", c) + ")";
        }

        /// <summary>
        /// For the meter's tooltip: "Spent in Visual Studio: today $3.20 (next warning at $20.00) · this month $41.00
        /// (next warning at $200.00), at API prices."; null while nothing was spent this month. This week's spend comes
        /// between them while <paramref name="week"/> has a warning amount.
        /// </summary>
        public static string BudgetLine(SpendWatch day, SpendWatch month, DateTime nowUtc, SpendWatch week = null)
        {
            if (day == null || month == null || month.SpentAt(nowUtc) <= 0 && day.SpentAt(nowUtc) <= 0) return null;
            string weekly = week != null && week.StepUsd > 0 ? " · " + week.Line(nowUtc) : "";
            return "Spent in Visual Studio: " + day.Line(nowUtc) + weekly + " · " + month.Line(nowUtc) + ", at API prices.";
        }

        /// <summary>Adds the logged turns of this period from before this watch was made, without warning about them.</summary>
        public void Load(IEnumerable<UsageRecord> records)
        {
            double before = 0;
            foreach (var r in records ?? new UsageRecord[0])
                if (r.Timestamp < _createdUtc && StartOf(r.Timestamp) == StartOf(_createdUtc) && InSolution(r.Solution)) before += r.CostUsd + r.RouterCostUsd;
            lock (_gate)
            {
                if (_start != StartOf(_createdUtc)) return; // a new period has started since: those turns belong to the last one
                _spent += before;
                if (StepUsd > 0) _warnedSteps = Math.Max(_warnedSteps, (int)Math.Floor(_spent / StepUsd + 1e-9));
            }
            if (PaceIsPastNext(_createdUtc)) lock (_gate) _paceWarned = StartOf(_createdUtc); // said before the restart
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
                string which = Period == SpendPeriod.Month ? "This month's" : Period == SpendPeriod.Week ? "This week's" : "Today's";
                if (Solution != null)
                    return $"{which} Claude Code spend on {SolutionBreakdown.NameOf(Solution)} has passed ${(steps * StepUsd).ToString("0.00", c)} "
                        + $"(${_spent.ToString("0.00", c)} so far in its conversations, at API prices). Tools → Claude Code → Show Savings Report lists the costliest turns.";
                return $"{which} Claude Code spend in Visual Studio has passed ${(steps * StepUsd).ToString("0.00", c)} "
                    + $"(${_spent.ToString("0.00", c)} so far in all conversations, at API prices). Tools → Claude Code → Show Savings Report lists the costliest turns.";
            }
        }
    }
}
