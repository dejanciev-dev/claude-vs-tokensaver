using System;
using System.Globalization;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// An optional daily hard limit: once today's spend has passed <see cref="LimitUsd"/>, a prompt is sent only after
    /// you say so. Once allowed, prompts go through until the spend passes the next multiple of the limit ($100, $150 …
    /// for 50); a new day starts again. Shared by all chat windows; call from one thread (the UI thread).
    /// </summary>
    public sealed class SpendLimit
    {
        private DateTime _day;
        private int _allowedSteps;

        /// <summary>The amount; 0 or less: never ask.</summary>
        public double LimitUsd { get; set; }

        public SpendLimit(double limitUsd = 0) { LimitUsd = limitUsd; }

        private int Steps(double spentToday) => (int)Math.Floor(spentToday / LimitUsd + 1e-9);

        /// <summary>The question to ask before sending, or null when the prompt may go.</summary>
        public string Ask(double spentToday, DateTime nowUtc)
        {
            if (LimitUsd <= 0 || double.IsNaN(spentToday) || spentToday < LimitUsd) return null;
            var today = nowUtc.ToLocalTime().Date;
            if (today != _day) { _day = today; _allowedSteps = 0; }
            int steps = Steps(spentToday);
            if (steps <= _allowedSteps) return null;
            var c = CultureInfo.InvariantCulture;
            return $"Today's Claude Code spend in Visual Studio is ${spentToday.ToString("0.00", c)}, past your daily limit of "
                + $"${LimitUsd.ToString("0.00", c)} (at API prices; Tools → Options → Claude Code → Ask before sending past daily cost).\n\n"
                + $"Send this prompt anyway? You won't be asked again today until ${((steps + 1) * LimitUsd).ToString("0.00", c)}.";
        }

        /// <summary>You said send: prompts go through until the next multiple of the limit today.</summary>
        public void Allow(double spentToday, DateTime nowUtc)
        {
            if (LimitUsd <= 0) return;
            var today = nowUtc.ToLocalTime().Date;
            if (today != _day) { _day = today; _allowedSteps = 0; }
            _allowedSteps = Math.Max(_allowedSteps, Steps(spentToday));
        }
    }
}
