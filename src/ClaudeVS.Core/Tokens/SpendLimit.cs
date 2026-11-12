using System;
using System.Globalization;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// An optional hard limit on today's spend (shared by all chat windows) or on one conversation's (one per chat
    /// window): once the spend has passed <see cref="LimitUsd"/>, a prompt is sent only after you say so. Once allowed,
    /// prompts go through until the spend passes the next multiple of the limit ($100, $150 … for 50); a new day, or a
    /// new conversation (<see cref="Reset"/>), starts again. Call from one thread (the UI thread).
    /// </summary>
    public sealed class SpendLimit
    {
        private DateTime _day;
        private int _allowedSteps;

        /// <summary>The amount; 0 or less: never ask.</summary>
        public double LimitUsd { get; set; }

        /// <summary>Limits one conversation's cost instead of today's spend: no new start at midnight.</summary>
        public bool PerConversation { get; }

        public SpendLimit(double limitUsd = 0, bool perConversation = false) { LimitUsd = limitUsd; PerConversation = perConversation; }

        private int Steps(double spent) => (int)Math.Floor(spent / LimitUsd + 1e-9);

        /// <summary>A new conversation: ask again from the first multiple.</summary>
        public void Reset() => _allowedSteps = 0;

        private void NewDay(DateTime nowUtc)
        {
            if (PerConversation) return;
            var today = nowUtc.ToLocalTime().Date;
            if (today != _day) { _day = today; _allowedSteps = 0; }
        }

        /// <summary>The question to ask before sending, or null when the prompt may go.</summary>
        public string Ask(double spent, DateTime nowUtc)
        {
            if (LimitUsd <= 0 || double.IsNaN(spent) || spent < LimitUsd) return null;
            NewDay(nowUtc);
            int steps = Steps(spent);
            if (steps <= _allowedSteps) return null;
            var c = CultureInfo.InvariantCulture;
            string next = ((steps + 1) * LimitUsd).ToString("0.00", c);
            if (PerConversation)
                return $"This conversation has cost ${spent.ToString("0.00", c)}, past your conversation limit of "
                    + $"${LimitUsd.ToString("0.00", c)} (at API prices; Tools → Options → Claude Code → Ask before sending past conversation cost). "
                    + "Each prompt sends the whole conversation again; New chat or /compact starts small.\n\n"
                    + $"Send this prompt anyway? You won't be asked again in this conversation until ${next}.";
            return $"Today's Claude Code spend in Visual Studio is ${spent.ToString("0.00", c)}, past your daily limit of "
                + $"${LimitUsd.ToString("0.00", c)} (at API prices; Tools → Options → Claude Code → Ask before sending past daily cost).\n\n"
                + $"Send this prompt anyway? You won't be asked again today until ${next}.";
        }

        /// <summary>You said send: prompts go through until the next multiple of the limit (today, or in this conversation).</summary>
        public void Allow(double spent, DateTime nowUtc)
        {
            if (LimitUsd <= 0) return;
            NewDay(nowUtc);
            _allowedSteps = Math.Max(_allowedSteps, Steps(spent));
        }
    }
}
