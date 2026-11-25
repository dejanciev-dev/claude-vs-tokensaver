using System;

namespace ClaudeVS.Core.Cli
{
    /// <summary>After a failed turn: what to do, and the prompt Tab puts in the box for it (null: nothing to send).</summary>
    public sealed class NextStep
    {
        public string Hint { get; set; }
        public string Prompt { get; set; }
        /// <summary><see cref="Prompt"/> is the prompt that failed, sent again as it was or edited.</summary>
        public bool PromptIsLast { get; set; }

        /// <summary>
        /// The grey text in the empty box: "The API is overloaded: try again · Tab puts your prompt back",
        /// "… · Tab: /compact", or the hint alone.
        /// </summary>
        public string Display() =>
            Prompt == null ? Hint
            : PromptIsLast ? Hint + " · Tab puts your prompt back"
            : Hint + " · Tab: " + Prompt;
    }

    /// <summary>Turns raw CLI/API error text into an actionable hint for the user.</summary>
    public static class ClaudeErrorHints
    {
        /// <summary>Returns a friendly explanation, or null when the error is not a known case.</summary>
        public static string Explain(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return null;
            string e = error.ToLowerInvariant();

            if (SignInExpiry.IsExpiredError(error))
                return "Your Claude sign-in has expired. Click Sign in under the meter, or run `claude auth login` in a terminal, then send again.";
            if (Has(e, "external api key") || Has(e, "invalid api key") && !Has(e, "/login"))
                return "Claude Code was given an API key that the API refuses (ANTHROPIC_API_KEY, or an apiKeyHelper). Fix it, or unset it to use your Claude sign-in, then restart the chat (New).";
            if (Has(e, "not logged in", "please run /login", "/login", "authentication_error", "unauthorized", "oauth token"))
                return "Claude Code is not logged in. Run `claude auth login` in a terminal (or `claude`, then /login). Restart the chat afterwards (New).";
            if (Has(e, "output token maximum", "max_output_tokens"))
                return "Claude's answer hit the output token limit and was cut off. Ask it to continue, or to work in smaller steps (one file or one part at a time).";
            if (ModelOutputLimit(error) is int most)
                return $"The model takes at most {most.ToString(System.Globalization.CultureInfo.InvariantCulture)} output tokens, fewer than Claude Code asked for (often a gateway's model). Set CLAUDE_CODE_MAX_OUTPUT_TOKENS={most.ToString(System.Globalization.CultureInfo.InvariantCulture)} in Claude Code's settings.json (env) or Visual Studio's environment, restart the chat (New), then send again. Or update Claude Code (`claude update`): 2.1.330 and newer retry at that limit by themselves.";
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
            if (CompressionCut(e))
                return "A proxy or gateway cut off or damaged Claude's compressed response. Set CLAUDE_CODE_DISABLE_RESPONSE_COMPRESSION=1 in Claude Code's settings.json (env) or Visual Studio's environment (Claude Code 2.1.331 and newer), restart the chat (New), then send again.";
            if (DiskFull(e))
                return "The disk is full: Claude Code couldn't write its output or its session file. Free some space (the drive with your user folder, where ~/.claude is, or the temp folder), then send again.";
            return null;
        }

        /// <summary>
        /// What to do after a failed turn, for the empty input box (as Claude Code 2.1.326's "hint in the empty prompt
        /// that says what to do after a request fails"): from why it ended (<paramref name="endReason"/>), the error kind
        /// and HTTP status Claude Code sent, and its message. Null for a turn that didn't fail.
        /// </summary>
        /// <param name="lastPrompt">The prompt that failed: what Tab puts back when trying again is the step.</param>
        public static NextStep NextStep(bool isError, string endReason, string errorKind, int? apiStatus, string text, string lastPrompt)
        {
            if (!isError) return null;
            string e = (text ?? "").ToLowerInvariant();
            string kind = errorKind ?? "";
            string again = string.IsNullOrWhiteSpace(lastPrompt) ? null : lastPrompt.Trim();
            NextStep Step(string hint, string prompt) => new NextStep { Hint = hint, Prompt = prompt, PromptIsLast = prompt != null && prompt == again };

            if ((endReason ?? "").StartsWith("aborted", StringComparison.Ordinal)) return Step("Stopped", again);
            if (endReason == "max_turns" || endReason == "error_max_turns") return Step("Stopped at the turn limit", "Continue");
            if (endReason == "error_max_budget_usd") return Step("Stopped at the budget limit", null);
            if (Has(e, "usage policy", "violate"))
                return Step("Blocked by the usage policy: rewind your message to change it, or start a New chat", null);
            if (kind == "max_output_tokens" || Has(e, "output token maximum", "max_output_tokens"))
                return Step("The answer hit the output limit", "Go on from where you stopped, in smaller parts");
            if (ModelOutputLimit(text) is int most)
                return Step($"The model takes at most {most.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} output tokens: set CLAUDE_CODE_MAX_OUTPUT_TOKENS={most.ToString(System.Globalization.CultureInfo.InvariantCulture)} (the link above sets it), then send it again", again);
            if (Has(e, "prompt is too long", "context window", "context_length"))
                return Step("The conversation is too long for the context window", "/compact");
            // "Invalid API key · Fix external API key" (2.1.326, authentication_failed, 401): a key in the environment, not the sign-in
            if (Has(e, "external api key") || Has(e, "invalid api key") && !Has(e, "/login"))
                return Step("Invalid API key: fix or unset ANTHROPIC_API_KEY, then send it again", again);
            if (kind == "authentication_failed" || apiStatus == 401 || SignInExpiry.IsExpiredError(text)
                || Has(e, "not logged in", "please run /login", "invalid api key", "authentication_error", "oauth token"))
                return Step("Sign in again, then send it again", again);
            if (kind == "billing_error" || apiStatus == 402 || Has(e, "credit balance", "insufficient_quota", "billing"))
                return Step("Out of credit: send it again once you have more", again);
            if (kind == "rate_limit" || apiStatus == 429 || Has(e, "rate_limit", "rate limit", "too many requests", "usage limit", "limit reached"))
                return Step("Rate or usage limit: send it again once it resets", again);
            if (kind == "model_not_found" || Has(e, "issue with the selected model"))
                return Step("That model isn't available: pick another in the model menu, then send it again", again);
            if (CompressionCut(e))
                return Step("A proxy cut the compressed response: set CLAUDE_CODE_DISABLE_RESPONSE_COMPRESSION=1, restart (New), then send it again", again);
            if (kind == "server_error" || apiStatus >= 500 || Has(e, "overloaded", "timed out", "timeout", "connection error", "econnreset", "socket"))
                return Step(Has(e, "overloaded") || apiStatus == 529 ? "The API is overloaded: try again" : "The request failed on the way: try again", again);
            if (DiskFull(e)) return Step("The disk is full: free some space, then send it again", again);
            return Step("The request failed", again);
        }

        private static readonly System.Text.RegularExpressions.Regex MaxTokensRefused = new System.Text.RegularExpressions.Regex(
            @"max_tokens:\s*\d+\s*>\s*(\d+),\s*which is the maximum allowed", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// The hint after the refusal when managed settings set <c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c> (to
        /// <paramref name="managedValue"/>): the project's files rank below them, so only whoever manages Claude Code can
        /// change it. Null when they don't set it, and the link can.
        /// </summary>
        public static string ManagedOutputLimit(int most, Newtonsoft.Json.Linq.JObject settingsResponse)
        {
            var (value, source) = SettingsCheck.EnvAsUsed(settingsResponse, "CLAUDE_CODE_MAX_OUTPUT_TOKENS", _ => null);
            if (source != "policySettings") return null;
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return $"The model takes at most {most.ToString("N0", c)} output tokens, and managed settings set CLAUDE_CODE_MAX_OUTPUT_TOKENS={value}: ask whoever manages Claude Code here to set it to {most.ToString(c)}";
        }

        /// <summary>
        /// The output limit the model takes, from the API's refusal of a larger one ("max_tokens: 128000 > 64000, which
        /// is the maximum allowed number of output tokens for …"), or null. Claude Code 2.1.326–2.1.328 and 2.1.330 on
        /// send the request again at that limit; before 2.1.326 and in 2.1.329 (2.1.330 reverted it), behind a gateway
        /// whose model takes fewer output tokens, the turn fails until <c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c> is set to it.
        /// </summary>
        public static int? ModelOutputLimit(string error)
        {
            var m = MaxTokensRefused.Match(error ?? "");
            return m.Success && int.TryParse(m.Groups[1].Value, out int most) && most > 0 ? most : (int?)null;
        }

        /// <summary>
        /// Node's error for a full disk (lower-cased text). Since 2.1.330 <c>claude -p</c> says so and exits 1 when its
        /// output can't be written; before, it exited 0 with nothing. The wording isn't captured: Node's code and message.
        /// </summary>
        private static bool DiskFull(string lowered) => SafeFile.IsDiskFullText(lowered);

        /// <summary>
        /// A proxy or gateway cut or damaged a compressed response: before 2.1.332 the turn ends in raw "ZlibError
        /// fetching" text (the runtime's decompression error); 2.1.332 says it in words that name the compressed
        /// response. 2.1.331 added <c>CLAUDE_CODE_DISABLE_RESPONSE_COMPRESSION</c> for it.
        /// </summary>
        private static bool CompressionCut(string lowered) =>
            Has(lowered, "zliberror", "decompression error", "z_data_error", "invalid compressed data")
            || Has(lowered, "compressed response", "compressed reply") && Has(lowered, "proxy", "gateway", "cut", "damag");

        private static bool Has(string text, params string[] needles)
        {
            foreach (var n in needles) if (text.IndexOf(n, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
