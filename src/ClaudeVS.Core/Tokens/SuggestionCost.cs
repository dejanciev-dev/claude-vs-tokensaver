using System;
using System.Globalization;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// What one of Claude Code's suggested next prompts (<c>--prompt-suggestions</c>) costs, estimated, for the one
    /// after a conversation's last turn: the others are billed with the next turn's <c>total_cost_usd</c> and measured
    /// (<see cref="TokenMeter.LastTurnSide"/>). Claude Code 2.1.326's debug log shows each one as a forked request on
    /// the conversation's model after the turn: "Forked agent [prompt_suggestion] finished: … totalUsage: input=506
    /// output=16 cacheRead=28058 cacheCreate=133" (Haiku, a 28k-token conversation). So it reads the whole context from
    /// the cache, and adds about <see cref="InputTokens"/> of its own instructions, <see cref="CacheWriteTokens"/>
    /// written and <see cref="OutputTokens"/> out.
    /// </summary>
    public static class SuggestionCost
    {
        public const long InputTokens = 500;
        public const long CacheWriteTokens = 150;
        public const long OutputTokens = 20;

        /// <param name="contextTokens">The conversation's context after the turn (the meter's), read from the cache.</param>
        /// <param name="model">The model the conversation runs on (the last main answer's); unknown falls back to Sonnet's prices.</param>
        public static double EstimateUsd(long contextTokens, string model, PriceTable prices)
        {
            prices = prices ?? new PriceTable();
            var p = prices.For(model) ?? prices.Sonnet;
            long context = Math.Max(0, contextTokens);
            return (InputTokens * p.Input + OutputTokens * p.Output + CacheWriteTokens * p.CacheWrite + context * p.CacheRead) / 1_000_000.0
                * p.RequestMultiplier(context + InputTokens + CacheWriteTokens); // Haiku over 100k: 5x
        }

        /// <summary>
        /// "Claude Code's own requests in this conversation: suggestions $0.012, /compact $0.030 (in its cost, billed with
        /// the turn after them)" for the meter's tooltip; null with none.
        /// </summary>
        public static string Line(double suggestionsUsd, double compactUsd)
        {
            if (suggestionsUsd <= 0 && compactUsd <= 0) return null;
            var c = CultureInfo.InvariantCulture;
            string Price(double usd) => usd < 0.001 ? "<$0.001" : "$" + usd.ToString("0.000", c);
            var parts = new System.Collections.Generic.List<string>();
            if (suggestionsUsd > 0) parts.Add("suggestions " + Price(suggestionsUsd));
            if (compactUsd > 0) parts.Add("/compact " + Price(compactUsd));
            return $"Claude Code's own requests in this conversation: {string.Join(", ", parts)} (in its cost, billed with the turn after them)";
        }
    }
}
