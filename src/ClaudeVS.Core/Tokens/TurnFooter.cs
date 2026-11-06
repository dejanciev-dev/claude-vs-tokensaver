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
        public static string Extras(TokenUsage turn)
        {
            var parts = new List<string>();
            // Thinking is billed as output, the dearest kind of token: worth seeing per answer.
            if (turn.Thinking > 0) parts.Add($"{TokenMeter.Format(turn.Thinking)} thinking");
            return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts);
        }
    }
}
