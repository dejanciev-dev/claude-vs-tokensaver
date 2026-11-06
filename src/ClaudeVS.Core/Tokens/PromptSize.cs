namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// The size of the prompt being typed, shown under the input box once it is long: a prompt stays in the
    /// conversation and goes with every later request, so a big paste costs again and again.
    /// </summary>
    public static class PromptSize
    {
        /// <summary>From this many tokens the size is shown.</summary>
        public const int ShowFrom = 1000;
        /// <summary>From this many tokens a file reference is suggested instead.</summary>
        public const int LargeFrom = 8000;

        /// <summary>Rough token count, ~4 characters per token.</summary>
        public static int Estimate(string text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

        /// <summary>"~1.8k tokens", a longer note for a huge paste, or null for a short prompt.</summary>
        public static string Note(string text)
        {
            int tokens = Estimate(text);
            if (tokens < ShowFrom) return null;
            string size = $"~{TokenMeter.Format(tokens)} tokens";
            return tokens < LargeFrom ? size
                : size + ": it goes with every later request. Saving it to a file and attaching that as a reference (Ctrl+Alt+K, F) is cheaper.";
        }
    }
}
