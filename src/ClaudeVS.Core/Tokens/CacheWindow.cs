using System;
using ClaudeVS.Core.Cli;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// How long the conversation stays in the prompt cache. Each request that reads or writes the cache keeps it for
    /// its lifetime again: 1 hour when Claude Code writes with <c>ephemeral_1h</c> (as 2.1.319 did on a claude.ai
    /// plan), else 5 minutes. After that the next prompt sends the whole context again at the full input price.
    /// </summary>
    public sealed class CacheWindow
    {
        public static readonly TimeSpan Short = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan Long = TimeSpan.FromHours(1);

        /// <summary>Below this context size an expired cache costs too little to mention.</summary>
        public const long WorthMentioningTokens = 10_000;

        /// <summary>The main agent's last request that used the cache; null before the first.</summary>
        public DateTime? LastUseUtc { get; private set; }
        /// <summary>The lifetime seen in use (1 hour once any request wrote with it).</summary>
        public TimeSpan Lifetime { get; private set; } = Short;
        /// <summary>The context the next request would send (cached until the cache expires).</summary>
        public long ContextTokens { get; private set; }

        /// <summary>A request of the main agent finished at <paramref name="nowUtc"/>.</summary>
        public void Use(TokenUsage usage, DateTime nowUtc)
        {
            if (usage.CacheRead == 0 && usage.CacheCreation == 0) return; // nothing cached (yet)
            if (usage.CacheCreation1h > 0) Lifetime = Long;
            else if (usage.CacheCreation5m > 0 && Lifetime != Long) Lifetime = Short;
            LastUseUtc = nowUtc;
            ContextTokens = usage.ContextTokens;
        }

        public void Reset() { LastUseUtc = null; Lifetime = Short; ContextTokens = 0; }

        public DateTime? ExpiresUtc => LastUseUtc + Lifetime;

        /// <summary>Time left before the cache expires; zero once expired, null when nothing is cached.</summary>
        public TimeSpan? Remaining(DateTime nowUtc) =>
            ExpiresUtc is DateTime e ? (e > nowUtc ? e - nowUtc : TimeSpan.Zero) : (TimeSpan?)null;

        /// <summary>"Prompt cache: warm for 42 more min (1-hour cache)." or "… expired …"; null before the first request.</summary>
        public string Status(DateTime nowUtc)
        {
            if (!(Remaining(nowUtc) is TimeSpan left)) return null;
            string kind = Lifetime == Long ? "1-hour cache" : "5-minute cache";
            if (left <= TimeSpan.Zero) return $"Prompt cache: expired ({kind}).";
            string mins = left.TotalMinutes >= 1 ? $"{(int)Math.Ceiling(left.TotalMinutes)} more min" : "less than a minute";
            return $"Prompt cache: warm for {mins} ({kind}).";
        }

        /// <summary>Within this of the end of a 1-hour cache, say it is about to expire.</summary>
        public static readonly TimeSpan SoonBefore = TimeSpan.FromMinutes(5);

        /// <summary>
        /// The last minutes of a 1-hour cache with a context worth mentioning: "Prompt cache expires in 4 min: …".
        /// (A 5-minute cache is always nearly over, so it gets no such note.) Null otherwise.
        /// </summary>
        public string SoonNote(DateTime nowUtc)
        {
            if (Lifetime != Long || !(Remaining(nowUtc) is TimeSpan left) || left <= TimeSpan.Zero || left > SoonBefore || ContextTokens < WorthMentioningTokens) return null;
            string mins = left.TotalMinutes >= 1 ? $"{(int)Math.Ceiling(left.TotalMinutes)} min" : "less than a minute";
            return $"Prompt cache expires in {mins}: after that the next prompt sends ~{TokenMeter.Format(ContextTokens)} tokens at the full input price.";
        }

        /// <summary>
        /// Once the cache has expired with a context worth mentioning: what the next prompt costs. Null otherwise.
        /// </summary>
        public string ExpiredNote(DateTime nowUtc)
        {
            if (!(Remaining(nowUtc) is TimeSpan left) || left > TimeSpan.Zero || ContextTokens < WorthMentioningTokens) return null;
            string idle = Lifetime == Long ? "over an hour" : "over 5 minutes";
            return $"Prompt cache expired (idle {idle}): the next prompt sends ~{TokenMeter.Format(ContextTokens)} tokens of context at the full input price, not the cached one.";
        }
    }
}
