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
        /// Billing that <c>init</c> does not show, from the /status rows (<c>get_status</c>, no model call), read once per
        /// Claude Code process. Seen with 2.1.322: an <c>ANTHROPIC_AUTH_TOKEN</c> alone leaves <c>apiKeySource</c> at
        /// "none", as a claude.ai sign-in does, while /status lists "Auth token: ANTHROPIC_AUTH_TOKEN"; with an
        /// <c>ANTHROPIC_API_KEY</c> as well, both rows appear and <c>apiKeySource</c> names the API key. Since 2.1.319
        /// Claude Code can also go on with an <c>ant auth login</c> sign-in, billed to a Claude Console organization, when
        /// the claude.ai login ends: a "Login method" that names the Console says so (that case not captured).
        /// </summary>
        public static string Auth(System.Collections.Generic.IEnumerable<StatusRow> rows)
        {
            var list = rows?.ToList() ?? new System.Collections.Generic.List<StatusRow>();
            string Value(string label) => list.FirstOrDefault(r => string.Equals(r.Label, label, System.StringComparison.OrdinalIgnoreCase))?.Value;
            string token = Value("Auth token"), key = Value("API key"), login = Value("Login method"), org = Value("Organization");
            if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(key))
                return $"Both {token} and {key} are set for Claude Code; it bills the API key. Unset the one you don't mean to pay with, so it is clear which pays.";
            if (!string.IsNullOrEmpty(token))
                return $"Claude Code is signing in with {token} (an auth token, often for a gateway or proxy), not your claude.ai login, so this session is billed to whoever issued that token.";
            if (login != null && login.IndexOf("Console", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return "Claude Code is signed in to a Claude Console organization" + (string.IsNullOrEmpty(org) ? "" : $" ({org})")
                    + ", so this session is billed per token to it, not to a claude.ai plan. If your claude.ai login ended, `claude auth login` signs in again.";
            return null;
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

        /// <summary>A Haiku conversation this large (context tokens of one request) gets the long-context tip.</summary>
        public const long HaikuLongContextTipFrom = 90_000;

        /// <summary>
        /// A token tip when a Haiku conversation nears Haiku 5.5's long-context tier: a request over 100k input tokens
        /// costs 5× every price (measured with 2.1.322; cache reads too), so from there each turn costs about as
        /// much as on Sonnet. Null for other models or a smaller context. The text is fixed, so it is said once.
        /// </summary>
        public static string HaikuLongContext(string model, long contextTokens)
        {
            if (Tokens.PriceTable.Family(model) != "haiku" || contextTokens < HaikuLongContextTipFrom) return null;
            return "This Haiku conversation is near 100k tokens of context. Above that, Haiku bills every request at 5x its prices ($0.50 / $2.50 per million tokens, cache reads too), so /compact or a New chat keeps it cheap.";
        }

        /// <summary>A whole-file read this large (in tokens, from the result size) earns the tip.</summary>
        public const int LargeReadTokens = 8000;

        /// <summary>
        /// A token tip when Claude read all of a large file (Read's <c>tool_use_result.file</c>: <c>numLines ==
        /// totalLines</c> from line 1, verified with 2.1.319) of at least <see cref="LargeReadTokens"/>: the whole
        /// file now sits in the context and is sent again with every request. Null otherwise.
        /// </summary>
        public static string WholeFileRead(JObject readData, int resultChars)
        {
            if (!(readData?["file"] is JObject file)) return null;
            long? lines = file["numLines"]?.Type == JTokenType.Integer ? (long?)file["numLines"] : null;
            long? total = file["totalLines"]?.Type == JTokenType.Integer ? (long?)file["totalLines"] : null;
            long start = file["startLine"]?.Type == JTokenType.Integer ? (long)file["startLine"] : 1;
            int tokens = Tokens.TokenEstimate.FromChars(resultChars);
            if (lines == null || total == null || lines < total || start > 1 || tokens < LargeReadTokens) return null;
            string name = System.IO.Path.GetFileName((string)file["filePath"] ?? "") is string n && n.Length > 0 ? n : "a file";
            return $"Claude read all {total:N0} lines of {name} (~{Tokens.TokenMeter.Format(tokens)} tokens), which now go with every request. "
                + "Naming the method or lines you mean (\"look at Save() in " + name + "\") reads less.";
        }

        /// <summary>A shell command's output this large (in tokens) earns the tip.</summary>
        public const int LargeOutputTokens = 5000;

        /// <summary>
        /// A token tip after a shell command printed a lot into the context: without the output-trimming hook, point
        /// to Install Token-Saving Settings and Hooks (it caps shell output and quiets builds, tests and git log);
        /// with it, suggest asking for filtered output. Null for other tools and smaller outputs.
        /// </summary>
        public static string LargeCommandOutput(string toolName, int resultChars, bool hookInstalled)
        {
            if ((toolName != "Bash" && toolName != "PowerShell") || Tokens.TokenEstimate.FromChars(resultChars) < LargeOutputTokens) return null;
            string size = $"That command put ~{Tokens.TokenMeter.Format(Tokens.TokenEstimate.FromChars(resultChars))} tokens of output into the context.";
            return hookInstalled
                ? size + " Asking Claude to filter it (only errors, the first lines, a quieter flag) keeps it small."
                : size + " Tools → Claude Code → Install Token-Saving Settings and Hooks caps shell output and runs dotnet build / test, msbuild and git log quietly.";
        }

        /// <summary>A fixed part of the context this large (tokens) earns the tip.</summary>
        public const long HeavyPartTokens = 5000;

        /// <summary>
        /// What goes with every request before you type anything, from the CLI's context breakdown
        /// (<c>get_context_usage</c>): the largest of MCP tools, memory / instruction files and skill listings when it
        /// passes <see cref="HeavyPartTokens"/> (skills: twice that), with where to cut it. Null otherwise.
        /// </summary>
        public static string BaseContext(Tokens.ContextUsage u)
        {
            if (u == null) return null;
            long skills = u.Categories.Where(c => c.IsUsed && c.Name == "Skills").Sum(c => c.Tokens);
            var parts = new[]
            {
                (Tokens: u.McpToolTokens, Min: HeavyPartTokens,
                 Text: $"MCP tools ({u.McpToolCount}) take ~{Tokens.TokenMeter.Format(u.McpToolTokens)} tokens of every request. Turning off servers you don't need (/ → MCP servers…) shrinks that."),
                (Tokens: u.MemoryFileTokens, Min: HeavyPartTokens,
                 Text: $"Memory and instruction files (CLAUDE.md…) take ~{Tokens.TokenMeter.Format(u.MemoryFileTokens)} tokens of every request. / → Memory & instructions… shows what each costs; Tools → Claude Code → Generate Lean CLAUDE.md writes a short one."),
                (Tokens: skills, Min: 2 * HeavyPartTokens,
                 Text: $"Skill listings take ~{Tokens.TokenMeter.Format(skills)} tokens of every request. Turning off skills you don't use (/ → Skills…) shrinks that."),
            };
            var worst = parts.Where(p => p.Tokens >= p.Min).OrderByDescending(p => p.Tokens).FirstOrDefault();
            return worst.Text;
        }

        private static string Names(System.Collections.Generic.List<string> names) => string.Join(", ", names.Select(n => "“" + n + "”"));
    }
}
