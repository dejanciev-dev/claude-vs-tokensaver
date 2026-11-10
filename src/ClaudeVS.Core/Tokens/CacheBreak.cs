using System;
using System.Globalization;
using ClaudeVS.Core.Cli;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// A request of the main agent that wrote the conversation to the prompt cache again although the cache was still
    /// warm: what it rebuilt, what that cost more than reading it, and the likely cause.
    /// </summary>
    public sealed class CacheBreak
    {
        /// <summary>Context tokens written again instead of read from the cache.</summary>
        public long Tokens { get; set; }
        /// <summary>What writing them cost more than reading them from the cache (USD at API prices).</summary>
        public double ExtraUsd { get; set; }
        /// <summary>The model family before, when the model changed ("opus"); null otherwise.</summary>
        public string FromFamily { get; set; }
        public string ToFamily { get; set; }
        public bool ModelChanged => FromFamily != null;
        /// <summary>Fast mode turned on or off on the same model: fast and standard speed have separate caches.</summary>
        public bool SpeedChanged { get; set; }
        /// <summary>The request after the change ran in fast mode.</summary>
        public bool NowFast { get; set; }

        private static string Name(string family) =>
            string.IsNullOrEmpty(family) ? "another model" : char.ToUpperInvariant(family[0]) + family.Substring(1);

        /// <summary>"The prompt cache was rebuilt: ~45k tokens … (~$0.11 more than reading them). The model changed …"</summary>
        public string Note()
        {
            string cost = ExtraUsd >= 0.01 ? $" (~${ExtraUsd.ToString("0.00", CultureInfo.InvariantCulture)} more than reading them)" : "";
            string why = ModelChanged
                ? $"The model changed from {Name(FromFamily)} to {Name(ToFamily)}, and each model has its own cache: keeping one model for a conversation avoids this."
                : SpeedChanged
                ? (NowFast ? "Fast mode started" : "Fast mode stopped (turned off, or Claude Code fell back to standard speed)")
                  + ", and fast and standard speed have separate caches."
                : "Something at the start of the conversation changed (CLAUDE.md, MCP servers or tools, settings or the system prompt): changes there take effect cheapest in a New chat.";
            return $"The prompt cache was rebuilt while still warm: ~{TokenMeter.Format(Tokens)} tokens of context were written again instead of read{cost}. {why}";
        }
    }

    /// <summary>
    /// Spots cache breaks from the main agent's requests. Normally each request reads from the cache everything the
    /// previous one sent; when it reads less than half of it, within the cache's lifetime and with no compaction in
    /// between, the cache was rebuilt. Each API message is seen once (the CLI repeats a message's usage per content block).
    /// </summary>
    public sealed class CacheBreakWatch
    {
        private string _lastMessageId;
        private TokenUsage _lastUsage;
        private string _lastModel;
        private long _lastContext;
        private DateTime? _lastUseUtc;
        private TimeSpan _lifetime = CacheWindow.Short;
        private bool _compacted;
        private bool _lastFast;

        public void Reset()
        {
            _lastMessageId = null; _lastUsage = default; _lastModel = null; _lastContext = 0; _lastUseUtc = null; _lastFast = false;
            _lifetime = CacheWindow.Short; _compacted = false;
        }

        /// <summary>The conversation was compacted: the next request starts a new cache on purpose.</summary>
        public void Compacted() => _compacted = true;

        /// <summary>A main-agent request finished at <paramref name="nowUtc"/>; returns the break it shows, else null.</summary>
        public CacheBreak Observe(TokenUsage usage, string model, string messageId, DateTime nowUtc, PriceTable prices)
        {
            if (usage.IsEmpty) return null;
            if (messageId != null ? messageId == _lastMessageId : SameUsage(usage, _lastUsage)) return null; // the same message again
            _lastMessageId = messageId;
            _lastUsage = usage;

            CacheBreak found = null;
            long missed = _lastContext - usage.CacheRead;
            bool warm = _lastUseUtc is DateTime last && nowUtc - last < _lifetime;
            if (warm && !_compacted && _lastContext >= CacheWindow.WorthMentioningTokens && usage.CacheRead < _lastContext / 2
                && missed >= CacheWindow.WorthMentioningTokens && usage.CacheCreation + usage.Input >= missed / 2)
            {
                prices = prices ?? new PriceTable();
                var p = prices.For(model) ?? prices.Sonnet;
                double write = usage.CacheCreation1h > 0 ? 2 * p.Input : p.CacheWrite;
                string from = PriceTable.Family(_lastModel), to = PriceTable.Family(model);
                bool changed = from != null && to != null && from != to;
                bool fast = usage.AnyFast;
                found = new CacheBreak
                {
                    Tokens = missed,
                    ExtraUsd = Math.Max(0, missed * (write - p.CacheRead) / 1_000_000.0 * ModelPrice.SpeedMultiplier(usage.FastTokens, usage.Total) * p.RequestMultiplier(usage.ContextTokens)),
                    FromFamily = changed ? from : null,
                    ToFamily = changed ? to : null,
                    SpeedChanged = !changed && fast != _lastFast,
                    NowFast = fast,
                };
            }

            if (usage.CacheRead > 0 || usage.CacheCreation > 0)
            {
                if (usage.CacheCreation1h > 0) _lifetime = CacheWindow.Long;
                _lastUseUtc = nowUtc;
            }
            _lastContext = usage.ContextTokens;
            _lastFast = usage.AnyFast;
            if (!string.IsNullOrEmpty(model)) _lastModel = model;
            _compacted = false;
            return found;
        }

        private static bool SameUsage(TokenUsage a, TokenUsage b) =>
            a.Input == b.Input && a.Output == b.Output && a.CacheRead == b.CacheRead && a.CacheCreation == b.CacheCreation;
    }
}
