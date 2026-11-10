using System;
using System.Globalization;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>What sending the prompt costs in input, and why.</summary>
    public sealed class NextPromptEstimate
    {
        public double InputUsd { get; set; }
        /// <summary>The conversation so far, sent again with the prompt.</summary>
        public long ContextTokens { get; set; }
        public int PromptTokens { get; set; }
        /// <summary>The context is read from the cache (warm, same model).</summary>
        public bool Cached { get; set; }
        /// <summary>The cache has expired since the last request.</summary>
        public bool Expired { get; set; }
        /// <summary>The prompt goes to another model than the cached one, which has no cache for it.</summary>
        public bool OtherModel { get; set; }

        /// <summary>"~$0.02 to send (context cached)" for the line under the input box.</summary>
        public string Note()
        {
            var c = CultureInfo.InvariantCulture;
            string price = InputUsd < 0.01 ? "<$0.01" : "~$" + InputUsd.ToString("0.00", c);
            string why = Cached ? "context cached"
                : OtherModel ? $"another model: ~{TokenMeter.Format(ContextTokens)} tokens of context cached again"
                : Expired ? $"cache expired: ~{TokenMeter.Format(ContextTokens)} tokens of context at the full price"
                : "new conversation";
            return $"{price} to send ({why})";
        }
    }

    /// <summary>
    /// Estimates the input cost of the next request: the conversation's context (read from the cache while it is warm
    /// and the model stays the same, else written to it again) plus the prompt (written to the cache). The request's
    /// output and any further requests of the turn (tool calls) come on top, so this is the least the prompt costs.
    /// </summary>
    public static class NextPromptPrice
    {
        /// <param name="cache">The conversation's cache (context size, lifetime, last use).</param>
        /// <param name="cachedModel">The model the context is cached for (the last main answer's); null when unknown.</param>
        /// <param name="sendModel">The model the prompt will go to; null = the cached one.</param>
        public static NextPromptEstimate Estimate(CacheWindow cache, string cachedModel, string sendModel, int promptTokens, PriceTable prices, DateTime nowUtc)
        {
            prices = prices ?? new PriceTable();
            string model = sendModel ?? cachedModel;
            var p = prices.For(model) ?? prices.Sonnet;
            long context = cache?.ContextTokens ?? 0;
            bool expired = cache?.Remaining(nowUtc) is TimeSpan left && left <= TimeSpan.Zero;
            string from = PriceTable.Family(cachedModel), to = PriceTable.Family(model);
            bool otherModel = context > 0 && from != null && to != null && from != to;
            bool cached = context > 0 && !expired && !otherModel;
            // Claude Code writes with the lifetime it has used in this conversation (1 hour: twice the input price).
            double write = cache != null && cache.Lifetime == CacheWindow.Long ? 2 * p.Input : p.CacheWrite;
            double usd = ((cached ? context * p.CacheRead : context * write) + Math.Max(0, promptTokens) * write) / 1_000_000.0
                * p.RequestMultiplier(context + Math.Max(0, promptTokens)); // Haiku over 100k: 5x
            return new NextPromptEstimate
            {
                InputUsd = usd, ContextTokens = context, PromptTokens = Math.Max(0, promptTokens),
                Cached = cached, Expired = expired && !otherModel, OtherModel = otherModel,
            };
        }
    }
}
