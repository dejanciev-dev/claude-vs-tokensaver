using System;

namespace ClaudeVS.Core.Cli
{
    /// <summary>Turns raw CLI/API error text into an actionable hint for the user.</summary>
    public static class ClaudeErrorHints
    {
        /// <summary>Returns a friendly explanation, or null when the error is not a known case.</summary>
        public static string Explain(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return null;
            string e = error.ToLowerInvariant();

            if (Has(e, "not logged in", "please run /login", "/login", "invalid api key", "authentication_error", "unauthorized", "oauth token"))
                return "Claude Code is not logged in. Open a terminal, run `claude`, then type /login. Restart the chat afterwards (New).";
            if (Has(e, "credit balance", "insufficient_quota", "billing", "usage limit", "limit reached"))
                return "Your Claude plan or API credit limit was reached. Check your usage at claude.ai or console.anthropic.com.";
            if (Has(e, "rate_limit", "rate limit", "too many requests"))
                return "Rate limited by the API. Wait a moment and send again; consider a cheaper model for small prompts.";
            if (Has(e, "overloaded"))
                return "The API is overloaded right now. Try again in a minute.";
            if (Has(e, "prompt is too long", "context window", "context_length"))
                return "The conversation no longer fits the context window. Run /compact or start a New chat.";
            if (Has(e, "is not recognized as an internal or external command", "enoent", "cannot find the path"))
                return "Claude Code CLI could not be started. Install it or set its path under Tools > Options > Claude Code.";
            return null;
        }

        private static bool Has(string text, params string[] needles)
        {
            foreach (var n in needles) if (text.IndexOf(n, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
