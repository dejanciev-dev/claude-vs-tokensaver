using System;
using System.Globalization;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// Says when a conversation's cost passes another multiple of a set amount ($5, $10, $15 … for 5). The cost is
    /// Claude Code's <c>total_cost_usd</c> (API prices; on a claude.ai plan it shows what the work would cost there).
    /// </summary>
    public sealed class CostWatch
    {
        private int _warnedSteps;

        /// <summary>The amount; 0 or less turns the warning off.</summary>
        public double StepUsd { get; set; }

        public CostWatch(double stepUsd = 5) { StepUsd = stepUsd; }

        public void Reset() => _warnedSteps = 0;

        /// <summary>After a turn: the warning when <paramref name="costUsd"/> passed a new step, else null.</summary>
        public string Check(double costUsd)
        {
            if (StepUsd <= 0 || double.IsNaN(costUsd) || costUsd < StepUsd) return null;
            int steps = (int)Math.Floor(costUsd / StepUsd + 1e-9);
            if (steps <= _warnedSteps) return null;
            _warnedSteps = steps;
            var c = CultureInfo.InvariantCulture;
            return $"This conversation has passed ${(steps * StepUsd).ToString("0.00", c)} (${costUsd.ToString("0.00", c)} so far at API prices). "
                + "A New chat for the next task, or /compact, keeps the next prompts cheaper.";
        }
    }
}
