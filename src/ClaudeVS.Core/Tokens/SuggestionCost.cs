using System;
using System.Globalization;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// What one of Claude Code's suggested next prompts (<c>--prompt-suggestions</c>) costs, estimated: the stream
    /// doesn't report it and <c>total_cost_usd</c> leaves it out. Claude Code 2.1.326's debug log shows each one as a
    /// forked request on the conversation's model after the turn: "Forked agent [prompt_suggestion] finished: …
    /// totalUsage: input=506 output=16 cacheRead=28058 cacheCreate=133" (Haiku, a 28k-token conversation). So it reads
    /// the whole context from the cache, and adds about <see cref="InputTokens"/> of its own instructions,
    /// <see cref="CacheWriteTokens"/> written and <see cref="OutputTokens"/> out.
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
        /// "Claude Code's suggestions: ~$0.012 for 4 (estimated; not in the costs above)" for the meter's tooltip;
        /// null with none.
        /// </summary>
        public static string Line(int count, double usd)
        {
            if (count <= 0) return null;
            var c = CultureInfo.InvariantCulture;
            string price = usd < 0.001 ? "<$0.001" : "~$" + usd.ToString("0.000", c);
            return $"Claude Code's suggestions: {price} for {count} in this conversation (estimated; not in the costs above)";
        }
    }
}
