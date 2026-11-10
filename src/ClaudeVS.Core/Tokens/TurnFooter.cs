using System.Collections.Generic;
using ClaudeVS.Core.Cli;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// The extra parts of the line under each answer, after "✓ 3.1s · 12.4k tok · $0.004 · model", from the turn's
    /// usage (the <c>result</c> message's top-level <c>usage</c>, summed over the turn's requests).
    /// </summary>
    public static class TurnFooter
    {
        /// <summary>" · 1.2k thinking" and the like; "" when there is nothing to add.</summary>
        /// <param name="cacheSavedUsd">What the cache saved this turn (<see cref="TokenMeter.LastTurnCacheSavedUsd"/>); shown from $0.001.</param>
        public static string Extras(TokenUsage turn, double cacheSavedUsd = 0)
        {
            var parts = new List<string>();
            // Fast mode doubles every price: say so where the turn's cost is shown
            if (turn.AnyFast) parts.Add(turn.FastTokens >= turn.Total ? "fast" : "partly fast");
            // Thinking is billed as output, the dearest kind of token: worth seeing per answer.
            if (turn.Thinking > 0) parts.Add($"{TokenMeter.Format(turn.Thinking)} thinking");
            if (CachedPercent(turn) is int cached)
                parts.Add($"{cached}% cached" + (cacheSavedUsd >= 0.0005 ? " (saved $" + cacheSavedUsd.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ")" : ""));
            return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts);
        }

        /// <summary>
        /// The turn's input served from the prompt cache (cached input costs a tenth of the full price): a low number
        /// right after a model switch or a long pause explains a dearer answer. Null below 1,000 input tokens.
        /// </summary>
        public static int? CachedPercent(TokenUsage turn)
        {
            long input = turn.Input + turn.CacheRead + turn.CacheCreation;
            if (input < 1000) return null;
            return (int)System.Math.Round(100.0 * turn.CacheRead / input);
        }
    }
}
