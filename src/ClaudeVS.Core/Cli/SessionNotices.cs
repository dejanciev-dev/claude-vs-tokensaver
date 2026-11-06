using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    /// <summary>
    /// One-line notes about the session, from the CLI's <c>init</c> message (2.1.319), for things that change what a
    /// session costs or does without you seeing it. Each returns null when there is nothing to say. The chat says
    /// each note once per conversation (init comes at the start of every turn).
    /// </summary>
    public static class SessionNotices
    {
        /// <summary>
        /// Billing: <c>apiKeySource</c> is "none" when Claude Code uses your claude.ai sign-in. Anything else means an
        /// API key pays for the session, which is easy to miss when an <c>ANTHROPIC_API_KEY</c> is set for something else.
        /// </summary>
        public static string ApiKey(JObject init)
        {
            string source = (string)init?["apiKeySource"];
            switch (source)
            {
                case null:
                case "":
                case "none":
                    return null;
                case "ANTHROPIC_API_KEY":
                    return "Claude Code is using the ANTHROPIC_API_KEY environment variable, so this session is billed to that API key per token, not to your claude.ai plan.";
                case "apiKeyHelper":
                    return "Claude Code gets its API key from apiKeyHelper in your settings, so this session is billed to that key per token.";
                case "/login managed key":
                    return "Claude Code is signed in with an Anthropic Console account, so this session is billed per token to that account.";
                default:
                    return $"Claude Code is using an API key ({source}), so this session is billed per token to that key.";
            }
        }
    }
}
