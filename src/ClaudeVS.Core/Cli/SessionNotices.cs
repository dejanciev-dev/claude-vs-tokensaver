using System.Linq;
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

        /// <summary>
        /// MCP servers Claude can't use: <c>mcp_servers[] = {name, status, source}</c>, where 2.1.319 reported
        /// "needs-auth" (a claude.ai connector not signed in) and "pending" (still connecting, not mentioned).
        /// </summary>
        public static string McpServers(JObject init)
        {
            if (!(init?["mcp_servers"] is JArray servers)) return null;
            var needAuth = new System.Collections.Generic.List<string>();
            var failed = new System.Collections.Generic.List<string>();
            foreach (var s in servers.OfType<JObject>())
            {
                string name = (string)s["name"];
                if (string.IsNullOrEmpty(name)) continue;
                switch ((string)s["status"])
                {
                    case "needs-auth": needAuth.Add(name); break;
                    case "failed": failed.Add(name); break;
                }
            }
            if (needAuth.Count == 0 && failed.Count == 0) return null;
            var parts = new System.Collections.Generic.List<string>();
            if (needAuth.Count > 0) parts.Add(Names(needAuth) + (needAuth.Count == 1 ? " needs" : " need") + " you to sign in");
            if (failed.Count > 0) parts.Add(Names(failed) + " could not connect");
            return "MCP " + (needAuth.Count + failed.Count == 1 ? "server " : "servers ") + string.Join("; ", parts)
                + ", so Claude can't use " + (needAuth.Count + failed.Count == 1 ? "its" : "their") + " tools. Fix it under / → MCP servers….";
        }

        /// <summary>
        /// The mode the CLI actually started in (<c>permissionMode</c>, verified "acceptEdits" with 2.1.319) when it is
        /// not the one asked for, e.g. Auto where the account or provider does not offer it (2.1.302 starts
        /// <c>-p</c> sessions in default mode on third-party providers). Check only the first init of a process:
        /// approving a plan leaves plan mode on purpose.
        /// </summary>
        public static string PermissionMode(JObject init, PermissionModeChoice asked)
        {
            var actual = ChoiceNames.FromCli((string)init?["permissionMode"]);
            // Manual is launched with no flag, so your settings' defaultMode decides: not a refusal.
            if (actual == null || actual == asked || asked == PermissionModeChoice.Default) return null;
            return $"Claude Code started in {actual.Value.Describe().Label} mode, not {asked.Describe().Label}: your settings, account or "
                + $"provider don't allow {asked.Describe().Label} here. The mode picker now shows {actual.Value.Describe().Label}.";
        }

        /// <summary>
        /// Output styles that make every answer longer: the built-in Explanatory (adds insights) and Learning (asks you
        /// to write parts yourself, with explanations). <c>output_style</c> was "default" with 2.1.319; Concise and
        /// custom styles get no note.
        /// </summary>
        public static string OutputStyle(JObject init)
        {
            string style = (string)init?["output_style"];
            switch (style?.ToLowerInvariant())
            {
                case "explanatory":
                case "learning":
                    return $"Output style “{style}” is on: it adds explanation to every answer, so answers use more output tokens. "
                        + "Switch under / → Output style… (Concise uses the fewest).";
                default:
                    return null;
            }
        }

        private static string Names(System.Collections.Generic.List<string> names) => string.Join(", ", names.Select(n => "“" + n + "”"));
    }
}
