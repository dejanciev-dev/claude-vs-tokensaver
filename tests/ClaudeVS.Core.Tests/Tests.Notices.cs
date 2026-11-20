using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ClaudeVS.Core.Chat;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Context;
using ClaudeVS.Core.Setup;
using ClaudeVS.Core.Tokens;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Tests
{
    internal static partial class Program
    {
        private static void Test_ErrorHints_KnownAndUnknown()
        {
            True(ClaudeErrorHints.Explain("Invalid API key · Please run /login")?.Contains("/login") == true, "login");
            True(ClaudeErrorHints.Explain("API Error: 429 {\"type\":\"rate_limit_error\"}")?.Contains("Rate limited") == true, "rate limit");
            True(ClaudeErrorHints.Explain("API Error: 529 {\"type\":\"overloaded_error\"}")?.Contains("overloaded") == true, "overloaded");
            True(ClaudeErrorHints.Explain("Prompt is too long")?.Contains("/compact") == true, "too long");
            Eq(null, ClaudeErrorHints.Explain("Build failed at line 401"), "no false positive on numbers");
            Eq(null, ClaudeErrorHints.Explain(""), "empty");
        }

        private static void Test_CliVersion_FromRealInit_2_1_316()
        {
            var parser = new ClaudeStreamParser();
            var init = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.316-turn.jsonl"))
                .SelectMany(l => parser.Parse(l)).First(e => e.Kind == ClaudeEventKind.SessionStarted);
            Eq("2.1.316", init.CliVersion, "claude_code_version from init");
            True(CliVersion.IsOlderThanRequired(init.CliVersion), "2.1.316 is older than required");
            True(CliVersion.Notice(init.CliVersion).StartsWith("Claude Code 2.1.316 is older than " + CliVersion.Required + ", which this extension needs"), "notice");
            True(CliVersion.Notice(init.CliVersion).Contains("claude update"), "says how to fix it");
        }

        private static void Test_CliVersion_Compare()
        {
            Eq(new Version(2, 1, 319), CliVersion.Parse("2.1.319 (Claude Code)"), "claude --version output");
            Eq(null, CliVersion.Parse("dev"), "not a version");
            Eq(null, CliVersion.Parse(null), "missing");
            True(!CliVersion.IsOlderThanRequired(CliVersion.Required), "the required version itself");
            True(!CliVersion.IsOlderThanRequired("2.1.319"), "newer");
            True(!CliVersion.IsOlderThanRequired("2.1.321"), "between required and tested: no notice");
            True(CliVersion.Parse(CliVersion.Tested) >= CliVersion.Parse(CliVersion.Required), "tested is at least required");
            True(!CliVersion.IsOlderThanRequired("2.2.0"), "newer minor");
            True(CliVersion.IsOlderThanRequired("2.1.99"), "numeric, not string, comparison");
            True(CliVersion.IsOlderThanRequired("1.0.128"), "old major");
            Eq(null, CliVersion.Notice("dev"), "unknown versions are not flagged");
            Eq(null, CliVersion.Notice(""), "empty");
        }

        /// <summary>Claude Code 2.1.319 with CLAUDE_CODE_MAX_OUTPUT_TOKENS=60: the cut-off answer ends as an error result.</summary>
        private static void Test_OutputLimit_RealResult_2_1_319()
        {
            var line = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-output-limit-result.jsonl")).Single();
            var done = new ClaudeStreamParser().Parse(line).Single();
            Eq(ClaudeEventKind.TurnCompleted, done.Kind);
            True(done.IsError, "is_error");
            Eq("api_error", done.TerminalReason, "terminal_reason");
            True(ClaudeErrorHints.Explain(done.Text)?.Contains("output token limit") == true, "hint: " + ClaudeErrorHints.Explain(done.Text));

            var normal = new ClaudeStreamParser().Parse("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"hi\",\"terminal_reason\":\"completed\"}").Single();
            Eq("completed", normal.TerminalReason, "normal turn");
            Eq("success", normal.ResultSubtype, "subtype");
            Eq(null, new ClaudeStreamParser().Parse("{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"hi\"}").Single().TerminalReason, "older CLIs");
        }

        /// <summary>Claude Code 2.1.326 asked for a model that doesn't exist: a synthetic message with the error kind, then the result.</summary>
        private static void Test_FailedTurn_RealResult_2_1_326()
        {
            var parser = new ClaudeStreamParser();
            var done = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.326-unknown-model.jsonl"))
                .SelectMany(l => parser.Parse(l)).Single(e => e.Kind == ClaudeEventKind.TurnCompleted);
            True(done.IsError, "is_error");
            Eq("api_error", done.TerminalReason);
            Eq("model_not_found", done.ErrorKind, "the kind, from the synthetic message");
            Eq(404, done.ApiErrorStatus, "api_error_status");
            True(done.Text.StartsWith("There's an issue with the selected model"), done.Text);

            // The kind belongs to that turn only; a turn that finished has none
            var next = parser.Parse("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"hi\"}").Single();
            Eq(null, next.ErrorKind, "the next turn");
            Eq(null, next.ApiErrorStatus);
            // A subagent's failed request is not the turn's
            var p2 = new ClaudeStreamParser();
            p2.Parse("{\"type\":\"assistant\",\"parent_tool_use_id\":\"toolu_1\",\"error\":\"rate_limit\",\"message\":{\"id\":\"m\",\"content\":[]}}");
            Eq(null, p2.Parse("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"x\"}").Single().ErrorKind, "a subagent's error");
        }

        /// <summary>get_settings with 2.1.326 in a project whose .claude/settings.json caps the effort (trimmed: the real answer lists every setting).</summary>
        private static void Test_ClaudeSettings_EffortCap()
        {
            var r = JObject.Parse("{\"effective\":{\"model\":\"opus\",\"maxEffortLevel\":\"medium\",\"promptSuggestionEnabled\":false},"
                + "\"sources\":[{\"source\":\"projectSettings\",\"settings\":{\"maxEffortLevel\":\"medium\",\"promptSuggestionEnabled\":false}}],"
                + "\"applied\":{\"model\":\"claude-haiku-5-5\",\"effort\":\"medium\"}}");
            var s = ClaudeSettings.Parse(r);
            Eq(EffortChoice.Medium, s.MaxEffort);
            Eq(false, s.PromptSuggestionEnabled);
            Eq("medium", s.AppliedEffort);
            var none = ClaudeSettings.Parse(JObject.Parse("{\"effective\":{\"model\":\"opus\"}}"));
            Eq(null, none.MaxEffort, "no cap"); Eq(null, none.PromptSuggestionEnabled, "not set");

            Eq(EffortChoice.Medium, ClaudeSettings.Cap(EffortChoice.High, EffortChoice.Medium), "above: the cap");
            Eq(EffortChoice.Low, ClaudeSettings.Cap(EffortChoice.Low, EffortChoice.Medium), "under: kept");
            Eq(EffortChoice.Auto, ClaudeSettings.Cap(EffortChoice.Auto, EffortChoice.Low), "Auto: Claude Code's default, capped by itself");
            Eq(EffortChoice.Max, ClaudeSettings.Cap(EffortChoice.Max, null), "no cap");
            Eq("medium", ClaudeSettings.Cap("xhigh", EffortChoice.Medium), "a logged name");
            Eq(null, ClaudeSettings.Cap((string)null, EffortChoice.Medium), "none logged");
            True(ClaudeSettings.Above(EffortChoice.Max, EffortChoice.High) && !ClaudeSettings.Above(EffortChoice.High, EffortChoice.High), "the menu greys out what is above");
            Eq(null, ClaudeSettings.Effort("ultra"), "an unknown level");
        }

        /// <summary>Real failed turns from 2.1.326: an invalid ANTHROPIC_API_KEY, and an empty CLAUDE_CONFIG_DIR (not logged in).</summary>
        private static void Test_ErrorHints_RealAuthErrors_2_1_326()
        {
            ClaudeEvent Done(string file) { var parser = new ClaudeStreamParser(); return File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", file))
                .SelectMany(l => parser.Parse(l)).Single(e => e.Kind == ClaudeEventKind.TurnCompleted); }
            var key = Done("cli-2.1.326-invalid-api-key.jsonl");
            Eq("authentication_failed", key.ErrorKind); Eq(401, key.ApiErrorStatus); Eq("Invalid API key · Fix external API key", key.Text);
            Eq("Invalid API key: fix or unset ANTHROPIC_API_KEY, then send it again · Tab puts your prompt back",
                ClaudeErrorHints.NextStep(key.IsError, key.TerminalReason, key.ErrorKind, key.ApiErrorStatus, key.Text, "hi").Display());
            True(ClaudeErrorHints.Explain(key.Text).Contains("ANTHROPIC_API_KEY"), "the transcript's explanation names the key, not the sign-in");

            var signedOut = Done("cli-2.1.326-not-logged-in.jsonl");
            Eq("authentication_failed", signedOut.ErrorKind); Eq(null, signedOut.ApiErrorStatus, "no request was made"); Eq("Not logged in · Please run /login", signedOut.Text);
            Eq("Sign in again, then send it again · Tab puts your prompt back",
                ClaudeErrorHints.NextStep(signedOut.IsError, signedOut.TerminalReason, signedOut.ErrorKind, signedOut.ApiErrorStatus, signedOut.Text, "hi").Display());
            True(ClaudeErrorHints.Explain(signedOut.Text).Contains("not logged in"), ClaudeErrorHints.Explain(signedOut.Text));
        }

        /// <summary>The shape of 2.1.326's get_settings answer (sources as in a probe with these settings in .claude/settings.json).</summary>
        private static void Test_SettingsCheck()
        {
            var r = JObject.Parse(@"{""effective"":{},""applied"":{""effort"":""low""},""sources"":[
                {""source"":""userSettings"",""settings"":{""model"":""sonnet"",""theme"":""dark"",""outputStyle"":""Explanatory""}},
                {""source"":""projectSettings"",""settings"":{""model"":""opus"",""alwaysThinkingEnabled"":true,""effortLevel"":""high"",""fastMode"":true,
                    ""enableAllProjectMcpServers"":true,""madeUpKey"":1,""env"":{""MAX_THINKING_TOKENS"":""32000"",""CLAUDE_CODE_MAX_OUTPUT_TOKENS"":""4000"",""DISABLE_PROMPT_CACHING"":""1""}}},
                {""source"":""localSettings"",""settings"":{""outputStyle"":""default""}},
                {""source"":""flagSettings"",""settings"":{""effortLevel"":""max""}}]}");
            var found = SettingsCheck.Find(r);
            var names = found.Select(f => f.Setting).ToList();
            Eq("model: opus|alwaysThinkingEnabled: true|env MAX_THINKING_TOKENS=32000|effortLevel: high|env DISABLE_PROMPT_CACHING=1|env CLAUDE_CODE_MAX_OUTPUT_TOKENS=4000|enableAllProjectMcpServers: true",
                string.Join("|", names), "known keys only; fastMode left out (not in this kind of session)");
            Eq(".claude/settings.json", found[0].Where, "the project's model wins over the user's");
            True(!names.Any(n => n.StartsWith("outputStyle")), "the project's local 'default' wins over the user's Explanatory");
            True(!names.Contains("effortLevel: max"), "the chat's own switch (flagSettings) is not a setting of yours");
            True(found[0].Why.Contains("Opus"), found[0].Why);
            True(SettingsCheck.Text(found).StartsWith("Claude Code settings that cost:\n• model: opus (.claude/settings.json): "), SettingsCheck.Text(found));
            Eq(found.Count, ClaudeSettings.Parse(r).Costly.Count, "parsed with the rest");

            var clean = SettingsCheck.Find(JObject.Parse(@"{""sources"":[{""source"":""userSettings"",""settings"":{""model"":""sonnet"",""effortLevel"":""medium"",""env"":{""MAX_THINKING_TOKENS"":""8000"",""CLAUDE_CODE_MAX_OUTPUT_TOKENS"":""32000""}}}]}"));
            Eq(0, clean.Count, "nothing dear");
            Eq("Claude Code settings that cost: none of the settings known to make turns dearer is set (managed, project or yours).", SettingsCheck.Text(clean));
            var managed = SettingsCheck.Find(JObject.Parse(@"{""sources"":[{""source"":""userSettings"",""settings"":{""model"":""haiku""}},{""source"":""policySettings"",""settings"":{""model"":""claude-fable-5-2""}}]}"));
            Eq("model: claude-fable-5-2", managed.Single().Setting); Eq("managed settings", managed.Single().Where, "managed wins");
            Eq(0, SettingsCheck.Find(null).Count, "no answer");
        }

        private static void Test_SessionNotices_CutOffTip()
        {
            Eq("Answers cut off at the output limit 3 times in this conversation, and going on cost $0.06: each retry reads the whole context again. "
                + "Ask for smaller steps, one file or one part at a time.", SessionNotices.CutOffTip(3, 0.06, null));
            Eq("Answers cut off at the output limit 1 time in this conversation, and going on cost $0.05: each retry reads the whole context again. "
                + "Ask for smaller steps, one file or one part at a time. CLAUDE_CODE_MAX_OUTPUT_TOKENS is 400: a higher limit cuts off less.", SessionNotices.CutOffTip(1, 0.05, 400));
            True(!SessionNotices.CutOffTip(2, 0.07, 32000).Contains("CLAUDE_CODE_MAX_OUTPUT_TOKENS"), "a limit that isn't low isn't named");
            Eq(null, SessionNotices.CutOffTip(4, 0.049, 400), "cheap so far");
            Eq(null, SessionNotices.CutOffTip(0, 1.0, null), "no cut-off");
        }

        private static void Test_ErrorHints_NextStep()
        {
            const string p = "Add a test for the parser";
            // The real failed turn (2.1.326, unknown model)
            var parser = new ClaudeStreamParser();
            var done = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.326-unknown-model.jsonl"))
                .SelectMany(l => parser.Parse(l)).Single(e => e.Kind == ClaudeEventKind.TurnCompleted);
            var model = ClaudeErrorHints.NextStep(done.IsError, done.TerminalReason, done.ErrorKind, done.ApiErrorStatus, done.Text, p);
            Eq("That model isn't available: pick another in the model menu, then send it again · Tab puts your prompt back", model.Display());
            Eq(p, model.Prompt, "Tab puts the failed prompt back");

            // The output limit (2.1.319 capture)
            var cut = new ClaudeStreamParser().Parse(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-output-limit-result.jsonl")).Single()).Single();
            Eq("The answer hit the output limit · Tab: Go on from where you stopped, in smaller parts",
                ClaudeErrorHints.NextStep(true, cut.TerminalReason, cut.ErrorKind, cut.ApiErrorStatus, cut.Text, p).Display());

            NextStep Of(string end = "api_error", string kind = null, int? status = null, string text = "API Error") =>
                ClaudeErrorHints.NextStep(true, end, kind, status, text, p);
            Eq(null, ClaudeErrorHints.NextStep(false, "completed", null, null, "hi", p), "a turn that finished");
            Eq("Stopped · Tab puts your prompt back", Of(end: "aborted_streaming").Display(), "Stop");
            Eq("The API is overloaded: try again · Tab puts your prompt back", Of(status: 529, text: "API Error: 529 {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}").Display());
            Eq("The request failed on the way: try again · Tab puts your prompt back", Of(kind: "server_error", status: 500).Display());
            Eq("The request failed on the way: try again · Tab puts your prompt back", Of(text: "API Error: Request timed out.").Display(), "a timeout");
            Eq("Rate or usage limit: send it again once it resets · Tab puts your prompt back", Of(kind: "rate_limit", status: 429).Display());
            Eq("Out of credit: send it again once you have more · Tab puts your prompt back", Of(text: "Credit balance is too low").Display());
            Eq("Sign in again, then send it again · Tab puts your prompt back", Of(kind: "authentication_failed", status: 401).Display());
            Eq("The conversation is too long for the context window · Tab: /compact", Of(text: "Prompt is too long").Display());
            // 2.1.329 no longer retries at the limit the API names: behind a gateway whose model takes fewer output tokens
            const string refused = "API Error: 400 {\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"max_tokens: 128000 > 64000, which is the maximum allowed number of output tokens for claude-sonnet-4-5\"}}";
            Eq(64000, ClaudeErrorHints.ModelOutputLimit(refused));
            Eq(null, ClaudeErrorHints.ModelOutputLimit("API Error: 400 max_tokens must be at least 1"), "another max_tokens error");
            Eq("The model takes at most 64,000 output tokens: set CLAUDE_CODE_MAX_OUTPUT_TOKENS=64000, start a New chat, then send it again · Tab puts your prompt back",
                Of(kind: "invalid_request", status: 400, text: refused).Display());
            True(ClaudeErrorHints.Explain(refused)?.Contains("CLAUDE_CODE_MAX_OUTPUT_TOKENS=64000") == true, "the error's explanation too");
            var policy = Of(text: "API Error: Claude Code is unable to respond to this request, which appears to violate our Usage Policy. Run /rewind and change the message, or run /clear.");
            Eq("Blocked by the usage policy: rewind your message to change it, or start a New chat", policy.Display(), "a policy block: the hint alone");
            Eq(null, policy.Prompt, "nothing for Tab: the same prompt would be blocked again");
            Eq("Stopped at the turn limit · Tab: Continue", Of(end: "error_max_turns").Display());
            Eq("The request failed · Tab puts your prompt back", Of(kind: "unknown", text: "something new").Display(), "anything else");
            Eq("Stopped", ClaudeErrorHints.NextStep(true, "aborted_streaming", null, null, null, "  ").Display(), "no prompt to put back");
        }

        private static JObject Init(string fields) => (JObject)new ClaudeStreamParser().Parse(
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"model\":\"claude-haiku-5-5\"" + (fields.Length > 0 ? "," + fields : "") + "}").Single().Payload;

        private static void Test_SessionNotices_ApiKey()
        {
            Eq(null, SessionNotices.ApiKey(Init("\"apiKeySource\":\"none\"")), "claude.ai sign-in (the real 2.1.319 value)");
            Eq(null, SessionNotices.ApiKey(Init("")), "older CLIs");
            True(SessionNotices.ApiKey(Init("\"apiKeySource\":\"ANTHROPIC_API_KEY\"")).Contains("ANTHROPIC_API_KEY environment variable"), "env var");
            True(SessionNotices.ApiKey(Init("\"apiKeySource\":\"apiKeyHelper\"")).Contains("apiKeyHelper"), "helper");
            True(SessionNotices.ApiKey(Init("\"apiKeySource\":\"/login managed key\"")).Contains("Console account"), "Console login");
            True(SessionNotices.ApiKey(Init("\"apiKeySource\":\"project\"")).Contains("(project)"), "other sources named");
        }

        private static void Test_SessionNotices_Auth_FromStatus()
        {
            // Row labels as /status gave them with 2.1.322 (values made up)
            List<StatusRow> Rows(params string[] lv) => Enumerable.Range(0, lv.Length / 2).Select(i => new StatusRow { Section = "Account", Label = lv[2 * i], Value = lv[2 * i + 1] }).ToList();

            Eq(null, SessionNotices.Auth(Rows("Login method", "Claude Pro account", "Organization", "Someone's Organization")), "claude.ai sign-in: nothing to say");
            Eq(null, SessionNotices.Auth(null), "no rows");

            string token = SessionNotices.Auth(Rows("Auth token", "ANTHROPIC_AUTH_TOKEN", "Model", "claude-haiku-5-5"));
            True(token != null && token.Contains("ANTHROPIC_AUTH_TOKEN") && token.Contains("not your claude.ai login"), token);

            string both = SessionNotices.Auth(Rows("Auth token", "ANTHROPIC_AUTH_TOKEN", "API key", "ANTHROPIC_API_KEY"));
            True(both != null && both.StartsWith("Both ANTHROPIC_AUTH_TOKEN and ANTHROPIC_API_KEY are set") && both.Contains("bills the API key"), both);

            string console = SessionNotices.Auth(Rows("Login method", "Claude Console account", "Organization", "Acme"));
            True(console != null && console.Contains("Claude Console organization (Acme)") && console.Contains("claude auth login"), console);
        }

        private static void Test_SessionNotices_McpServers()
        {
            // The real 2.1.319 list: one connector still connecting, one not signed in.
            string real = SessionNotices.McpServers(Init("\"mcp_servers\":[{\"name\":\"claude.ai Claude Docs\",\"status\":\"pending\",\"source\":\"claudeai\"},{\"name\":\"claude.ai Google Drive\",\"status\":\"needs-auth\",\"source\":\"claudeai\"}]"));
            Eq("MCP server “claude.ai Google Drive” needs you to sign in, so Claude can't use its tools. Fix it under / → MCP servers….", real, "needs-auth");
            string two = SessionNotices.McpServers(Init("\"mcp_servers\":[{\"name\":\"a\",\"status\":\"failed\"},{\"name\":\"b\",\"status\":\"needs-auth\"},{\"name\":\"c\",\"status\":\"connected\"}]"));
            Eq("MCP servers “b” needs you to sign in; “a” could not connect, so Claude can't use their tools. Fix it under / → MCP servers….", two, "both kinds");
            Eq(null, SessionNotices.McpServers(Init("\"mcp_servers\":[{\"name\":\"visualstudio\",\"status\":\"connected\"}]")), "all fine");
            Eq(null, SessionNotices.McpServers(Init("")), "older CLIs");
        }

        private static void Test_SessionNotices_PermissionMode()
        {
            var accept = Init("\"permissionMode\":\"acceptEdits\""); // as 2.1.319 reported for --permission-mode acceptEdits
            Eq(null, SessionNotices.PermissionMode(accept, PermissionModeChoice.AcceptEdits), "as asked");
            Eq("Claude Code started in Manual mode, not Auto: your settings, organization, account or provider don't allow Auto here. The mode picker now shows Manual.",
                SessionNotices.PermissionMode(Init("\"permissionMode\":\"default\""), PermissionModeChoice.Auto), "auto refused");
            Eq("Claude Code started in Auto mode, not Manual: your settings, organization, account or provider don't allow Manual here. The mode picker now shows Auto.",
                SessionNotices.PermissionMode(Init("\"permissionMode\":\"auto\""), PermissionModeChoice.Default), "Manual asked, auto by the organization's default (2.1.326)");
            Eq(null, SessionNotices.PermissionMode(Init("\"permissionMode\":\"default\""), PermissionModeChoice.Default), "Manual as asked");
            Eq(null, SessionNotices.PermissionMode(Init("\"permissionMode\":\"somethingNew\""), PermissionModeChoice.Auto), "unknown mode");
            Eq(PermissionModeChoice.BypassPermissions, ChoiceNames.FromCli("bypassPermissions"));

            // Only the first init of a process is checked.
            var p = new ClaudeStreamParser();
            string init = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"permissionMode\":\"plan\"}";
            True(p.Parse(init).Single().IsFirstInit, "first");
            True(!p.Parse(init).Single().IsFirstInit, "the next turn's init");
        }

        private static void Test_SessionNotices_OutputStyle()
        {
            Eq(null, SessionNotices.OutputStyle(Init("\"output_style\":\"default\"")), "default (the real 2.1.319 value)");
            Eq(null, SessionNotices.OutputStyle(Init("\"output_style\":\"Concise\"")), "concise saves tokens");
            Eq(null, SessionNotices.OutputStyle(Init("\"output_style\":\"my-team-style\"")), "custom: unknown");
            Eq(null, SessionNotices.OutputStyle(Init("")), "older CLIs");
            True(SessionNotices.OutputStyle(Init("\"output_style\":\"Explanatory\""))?.StartsWith("Output style “Explanatory” is on") == true, "explanatory");
            True(SessionNotices.OutputStyle(Init("\"output_style\":\"learning\""))?.Contains("more output tokens") == true, "learning, any case");
        }

        private static void Test_SessionNotices_WholeFileRead()
        {
            JObject Read(int numLines, int totalLines, int startLine = 1) => JObject.Parse(
                $"{{\"type\":\"text\",\"file\":{{\"filePath\":\"C:\\\\repo\\\\src\\\\Big.cs\",\"numLines\":{numLines},\"startLine\":{startLine},\"totalLines\":{totalLines}}}}}");
            string tip = SessionNotices.WholeFileRead(Read(2400, 2400), 120_000);
            True(tip?.StartsWith("Claude read all 2,400 lines of Big.cs (~40") == true, tip);
            True(tip.Contains("look at Save() in Big.cs"), "names the file in the example");
            var endsInNewline = Read(2401, 2401);
            ((JObject)endsInNewline["file"])["content"] = "a\nb\n";
            True(SessionNotices.WholeFileRead(endsInNewline, 120_000)?.StartsWith("Claude read all 2,400 lines") == true, "a newline at the end is no line (2.1.329)");
            Eq(null, SessionNotices.WholeFileRead(Read(200, 2400), 120_000), "only part of it");
            Eq(null, SessionNotices.WholeFileRead(Read(2400, 2400, 10), 120_000), "not from the start");
            Eq(null, SessionNotices.WholeFileRead(Read(300, 300), 9_000), "small file");
            Eq(null, SessionNotices.WholeFileRead(JObject.Parse("{\"type\":\"image\",\"file\":{\"base64\":\"x\"}}"), 200_000), "an image");

            // From the real 2.1.319 capture: part of big.txt (lines 10-29 of 301) earns no tip; results know their agent.
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-tool-data.jsonl"))
                .SelectMany(l => new ClaudeStreamParser().Parse(l)).Where(e => e.Kind == ClaudeEventKind.ToolResult).ToList();
            True(events.All(e => SessionNotices.WholeFileRead(e.ResultData, 1_000_000) == null), "partial read");
            True(events.All(e => !e.IsSubagent), "main agent's results");
            var fork = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-fork.jsonl"))
                .SelectMany(l => new ClaudeStreamParser().Parse(l)).Where(e => e.Kind == ClaudeEventKind.ToolResult).ToList();
            True(fork.All(e => !e.IsSubagent), "the Agent call's own result belongs to the main agent");
        }

        private static void Test_SessionNotices_BaseContext()
        {
            // The real 2.1.317 breakdown: no MCP tools, 57 tokens of memory, 4.5k of skills: nothing heavy.
            string line = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.317-context-usage.jsonl")).Trim();
            var ev = new ClaudeStreamParser().Parse(line).Single();
            var real = ContextUsage.Parse(ev.Payload);
            Eq(null, SessionNotices.BaseContext(real), "nothing heavy in the real breakdown");

            var heavy = ContextUsage.Parse(JObject.Parse("{\"totalTokens\":40000,\"maxTokens\":200000,\"categories\":[{\"name\":\"Skills\",\"tokens\":6000,\"kind\":\"used\"}],"
                + "\"mcpTools\":[{\"name\":\"a\",\"tokens\":7000},{\"name\":\"b\",\"tokens\":5000}],\"memoryFiles\":[{\"path\":\"CLAUDE.md\",\"tokens\":6000}]}"));
            string tip = SessionNotices.BaseContext(heavy);
            True(tip?.StartsWith("MCP tools (2) take ~12.0k tokens of every request.") == true, tip);
            var memory = ContextUsage.Parse(JObject.Parse("{\"totalTokens\":20000,\"categories\":[],\"memoryFiles\":[{\"path\":\"CLAUDE.md\",\"tokens\":9000}]}"));
            True(SessionNotices.BaseContext(memory)?.Contains("Generate Lean CLAUDE.md") == true, "memory files");
            Eq(null, SessionNotices.BaseContext(null), "no breakdown");
        }

        private static void Test_SignInExpiry_FromRealResult_2_1_318()
        {
            var parser = new ClaudeStreamParser();
            var done = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.318-thinking.jsonl"))
                .SelectMany(l => parser.Parse(l)).Single(e => e.Kind == ClaudeEventKind.TurnCompleted);
            Eq(new DateTime(2026, 11, 21, 13, 48, 10, 889, DateTimeKind.Utc), done.CredentialExpiresUtc, "credential_expires_at, in UTC");
            Eq(DateTimeKind.Utc, done.CredentialExpiresUtc.Value.Kind, "kind");

            var noField = new ClaudeStreamParser().Parse("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"hi\"}").Single();
            Eq(null, noField.CredentialExpiresUtc, "API-key users get no field");
            Eq(null, SignInExpiry.Parse(JValue.CreateNull()), "null");
            Eq(null, SignInExpiry.Parse(new JValue("soon")), "unreadable");
        }

        private static void Test_SignInExpiry_Notice()
        {
            var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
            Eq(LimitLevel.None, SignInExpiry.Notice(null, now).Level, "no expiry known");
            Eq(LimitLevel.None, SignInExpiry.Notice(now.AddDays(15), now).Level, "far off");
            Eq(LimitLevel.None, SignInExpiry.Notice(now.AddDays(10).AddMinutes(1), now).Level, "just past 10 days");

            var (level, text) = SignInExpiry.Notice(now.AddDays(5), now);
            Eq(LimitLevel.Warning, level, "within 10 days");
            True(text.StartsWith("Your Claude sign-in ends in "), "days: " + text);
            True(text.Contains("days ("), "with the date: " + text);

            var soon = SignInExpiry.Notice(now.AddMinutes(30), now);
            Eq(LimitLevel.Warning, soon.Level, "half an hour left");
            True(soon.Text.Contains("today at ") || soon.Text.Contains("tomorrow at "), "today (or tomorrow near local midnight): " + soon.Text);

            var gone = SignInExpiry.Notice(now.AddSeconds(-1), now);
            Eq(LimitLevel.Reached, gone.Level, "expired");
            Eq("Your Claude sign-in has expired.", gone.Text);
        }

        private static void Test_SignInExpiry_ExpiredError()
        {
            True(SignInExpiry.IsExpiredError("Your claude.ai login has expired. Run `claude auth login`, or `claude setup-token` for a long-lived token."), "2.1.318 wording");
            True(SignInExpiry.IsExpiredError("OAuth token has expired"), "oauth expired");
            True(!SignInExpiry.IsExpiredError("Not logged in · Please run /login"), "not logged in is a different case");
            True(!SignInExpiry.IsExpiredError("The certificate has expired"), "other expiries");
            True(ClaudeErrorHints.Explain("OAuth token has expired")?.Contains("claude auth login") == true, "hint names the command");
            True(ClaudeErrorHints.Explain("Not logged in · Please run /login")?.Contains("claude auth login") == true, "not-logged-in hint too");
        }

        private static void Test_RateLimit_FromRealEvent()
        {
            string line = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.317-rate-limit.jsonl")).Trim();
            var ev = new ClaudeStreamParser().Parse(line).Single();
            Eq(ClaudeEventKind.RateLimit, ev.Kind);
            var r = RateLimitInfo.Parse(ev.Payload);
            Eq("allowed", r.Status);
            Eq("five_hour", r.RateLimitType);
            Eq(new DateTime(2026, 11, 3, 15, 0, 0, DateTimeKind.Utc), r.ResetsAtUtc);
            Eq(2, r.Windows.Count);
            Eq(0.47, r.Windows.Single(w => w.Name == "seven_day").Utilization);
            Eq(LimitLevel.None, r.Banner(new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc)).Level, "19% and 47%: no banner");
            True(r.Details(DateTime.UtcNow).StartsWith("5-hour limit: 19% · resets at "), r.Details(DateTime.UtcNow));
        }

        private static void Test_RateLimit_Banners()
        {
            var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
            long reset = (long)(now.AddHours(2) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            RateLimitInfo Make(string status, double fiveHour, string windowStatus = "allowed") => RateLimitInfo.Parse(JObject.Parse(
                $"{{\"status\":\"{status}\",\"rateLimitType\":\"five_hour\",\"resetsAt\":{reset},\"unifiedWindows\":{{\"five_hour\":{{\"utilization\":{fiveHour.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"resetsAt\":{reset},\"status\":\"{windowStatus}\"}},\"seven_day\":{{\"utilization\":0.3,\"status\":\"allowed\"}}}}}}"));

            var high = Make("allowed", 0.86).Banner(now);
            Eq(LimitLevel.Warning, high.Level, "86% shows before the CLI warns");
            True(high.Text.StartsWith("5-hour limit 86% used. Resets at "), high.Text);

            var warned = Make("allowed_warning", 0.5).Banner(now);
            Eq(LimitLevel.Warning, warned.Level, "the CLI's warning always shows");

            var reached = Make("rejected", 1.0, "rejected").Banner(now);
            Eq(LimitLevel.Reached, reached.Level);
            True(reached.Text.StartsWith("5-hour limit reached. Resets at "), reached.Text);

            Eq("Weekly Opus limit", RateLimitInfo.DisplayName("seven_day_opus"));
        }

        /// <summary>The real event (2.1.317) says extra usage is off for lack of credits; the reached banner says so.</summary>
        private static void Test_RateLimit_ExtraUsage()
        {
            var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
            string line = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.317-rate-limit.jsonl")).Trim();
            var real = RateLimitInfo.Parse(new ClaudeStreamParser().Parse(line).Single().Payload);
            Eq("rejected", real.OverageStatus);
            Eq("out_of_credits", real.OverageDisabledReason);
            True(real.Details(now).EndsWith("\nExtra usage: off (no credits left)"), real.Details(now));

            long reset = (long)(now.AddHours(2) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            RateLimitInfo Reached(string overage) => RateLimitInfo.Parse(JObject.Parse(
                $"{{\"status\":\"rejected\",\"rateLimitType\":\"five_hour\",\"resetsAt\":{reset},{overage}\"unifiedWindows\":{{\"five_hour\":{{\"utilization\":1.0,\"resetsAt\":{reset},\"status\":\"rejected\"}}}}}}"));
            True(Reached("\"overageStatus\":\"rejected\",\"overageDisabledReason\":\"out_of_credits\",").Banner(now).Text.EndsWith(" Extra usage is off (no credits left)."), "reached, no extra usage");
            True(Reached("\"overageStatus\":\"allowed\",\"isUsingOverage\":true,").Banner(now).Text.EndsWith(" Using extra usage."), "reached, carried by extra usage");
            True(!Reached("").Banner(now).Text.Contains("Extra usage"), "older events: as before");
        }

        /// <summary>
        /// The token-saving setup against <c>get_settings</c> as 2.1.327 answers it (shape from a real answer: the
        /// project's hook and rules, a local file raising <c>bashOutputMaxChars</c> to 200,000 and adding a Write hook).
        /// </summary>
        /// <summary>The tip for reads from build output, and adding its rule to the project's settings.</summary>
        private static void Test_NoiseReadTip_AndDenyRule()
        {
            var tip = SessionNotices.NoiseReadTip(new Dictionary<string, long> { ["src/App/obj"] = 1500, ["src/Lib/obj"] = 900, ["src/Core"] = 9000 });
            True(tip != null, "obj folders together pass 2k");
            Eq("Read(./**/obj/**)", tip.Value.Rule);
            True(tip.Value.Text.Contains("~2.4k tokens from src/App/obj"), tip.Value.Text);
            Eq(null, SessionNotices.NoiseReadTip(new Dictionary<string, long> { ["src/App/obj"] = 1500 }), "under 2k");
            Eq(null, SessionNotices.NoiseReadTip(new Dictionary<string, long> { ["src/Core"] = 90000 }), "no build output");

            var files = new Dictionary<string, long> { [@"C:\repo\web\js\app.min.js"] = 1800, [@"C:\repo\web\js\lib.min.js"] = 900, [@"C:\repo\web\obj\x.min.js"] = 9000 };
            var gen = SessionNotices.NoiseReadTip(new Dictionary<string, long> { ["web/js"] = 2700 }, files);
            Eq("Read(./**/*.min.js)", gen?.Rule, "minified files, by their rule");
            True(gen.Value.Text.Contains("~2.7k tokens of *.min.js files (app.min.js…)"), gen.Value.Text);
            Eq("Read(./**/*.map)", SessionNotices.GeneratedFileRule("site.css.map"));
            Eq("Read(./**/*.min.css)", SessionNotices.GeneratedFileRule(@"C:\a\b.MIN.css"));
            Eq(null, SessionNotices.GeneratedFileRule(".map"), "the extension alone is not a file");
            Eq(null, SessionNotices.GeneratedFileRule("app.js"));
            var both = SessionNotices.NoiseReadTip(new Dictionary<string, long> { ["web/obj"] = 9000 }, new Dictionary<string, long> { [@"C:\repo\web\obj\x.min.js"] = 9000 });
            Eq("Read(./**/obj/**)", both?.Rule, "a minified file in obj counts for obj only");

            string dir = Path.Combine(Path.GetTempPath(), "cvs-deny-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, ".claude"));
                File.WriteAllText(Path.Combine(dir, ".claude", "settings.json"), "{\"model\":\"sonnet\",\"permissions\":{\"allow\":[\"Bash(ls)\"]}}");
                True(!TokenSaverSetup.HasDenyRule(dir, "Read(./**/obj/**)"), "not there yet");
                True(TokenSaverSetup.AddDenyRule(dir, "Read(./**/obj/**)"), "added");
                var s = JObject.Parse(File.ReadAllText(Path.Combine(dir, ".claude", "settings.json")));
                Eq("sonnet", (string)s["model"], "other settings kept");
                Eq("Bash(ls)", (string)s.SelectToken("permissions.allow[0]"), "allow rules kept");
                Eq("Read(./**/obj/**)", (string)s.SelectToken("permissions.deny[0]"));
                True(TokenSaverSetup.HasDenyRule(dir, "Read(./**/obj/**)"), "there now");
                True(!TokenSaverSetup.AddDenyRule(dir, "Read(./**/obj/**)"), "not twice");
                File.WriteAllText(Path.Combine(dir, ".claude", "settings.local.json"), "{\"permissions\":{\"deny\":[\"Read(./**/node_modules/**)\"]}}");
                True(TokenSaverSetup.HasDenyRule(dir, "Read(./**/node_modules/**)"), "the local file counts too");
                File.WriteAllText(Path.Combine(dir, ".claude", "settings.local.json"), "{ not json");
                True(!TokenSaverSetup.HasDenyRule(dir, "Read(./**/dist/**)"), "a broken file is skipped");
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        /// <summary>The setup check's fixes: which settings files to open, and whether reinstalling helps.</summary>
        private static void Test_SetupCheck_Fixes()
        {
            var problems = new List<CostlySetting>
            {
                new CostlySetting { Setting = "bashOutputMaxChars: 200,000", Where = ".claude/settings.local.json" },
                new CostlySetting { Setting = "disableAllHooks: true", Where = "~/.claude/settings.json" },
                new CostlySetting { Setting = "x", Where = "managed settings" },
                new CostlySetting { Setting = "y", Where = ".claude/settings.local.json" },
            };
            var files = SetupCheck.FilesToOpen(problems, @"C:\repo", @"C:\home");
            Eq(2, files.Count, "each file once, managed left out");
            Eq(Path.Combine(@"C:\repo", ".claude", "settings.local.json"), files[0]);
            Eq(Path.Combine(@"C:\home", ".claude", "settings.json"), files[1]);
            True(!SetupCheck.ReinstallHelps(problems), "overrides elsewhere: reinstalling the project's files doesn't fix them");
            True(SetupCheck.ReinstallHelps(new[] { new CostlySetting { Where = ".claude/hooks" } }), "a missing script: it does");
            True(SetupCheck.ReinstallHelps(new[] { new CostlySetting { Where = ".claude/settings.json" } }), "the project's file: it does");
            Eq(0, SetupCheck.FilesToOpen(null, @"C:\repo", null).Count, "none");
        }

        private static void Test_SetupCheck()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cvs-setup-" + Guid.NewGuid().ToString("N"));
            try
            {
                Eq(null, SetupCheck.Find(new JObject(), dir), "not installed: nothing to check");
                TokenSaverSetup.Apply(dir);
                var project = JObject.Parse(File.ReadAllText(Path.Combine(dir, ".claude", "settings.json")));
                JObject Response(JObject local)
                {
                    var effective = (JObject)project.DeepClone();
                    if (local != null)
                    {
                        effective.Merge(local, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Concat, MergeNullValueHandling = MergeNullValueHandling.Ignore });
                    }
                    var sources = new JArray(new JObject { ["source"] = "projectSettings", ["settings"] = project });
                    if (local != null) sources.Add(new JObject { ["source"] = "localSettings", ["settings"] = local });
                    return new JObject { ["effective"] = effective, ["sources"] = sources };
                }

                var ok = SetupCheck.Find(Response(null), dir);
                Eq(0, ok.Count, "just installed: all in effect. " + SetupCheck.Text(ok));
                True(SetupCheck.Text(ok).Contains("all in effect"), "text when fine");

                var raised = SetupCheck.Find(Response(JObject.Parse("{\"bashOutputMaxChars\":200000,\"hooks\":{\"PreToolUse\":[{\"matcher\":\"Write\",\"hooks\":[{\"type\":\"command\",\"command\":\"echo x\"}]}]}}")), dir);
                Eq(1, raised.Count, "only the raised cap: " + SetupCheck.Text(raised));
                Eq("bashOutputMaxChars: 200,000", raised[0].Setting, "the value");
                Eq(".claude/settings.local.json", raised[0].Where, "where it is raised");

                var off = SetupCheck.Find(Response(JObject.Parse("{\"disableAllHooks\":true}")), dir);
                True(off.Any(p => p.Setting == "disableAllHooks: true" && p.Where == ".claude/settings.local.json"), "hooks off");

                ((JArray)project.SelectToken("hooks.PreToolUse"))[0]["matcher"] = "Bash";
                ((JArray)project.SelectToken("permissions.deny")).RemoveAt(0);
                var broken = SetupCheck.Find(Response(null), dir);
                True(broken.Any(p => p.Setting == "quiet-build hook skips PowerShell"), "matcher for Bash only");
                True(!broken.Any(p => p.Setting == "quiet-build hook skips Bash"), "Bash still matched");
                True(broken.Any(p => p.Setting.StartsWith("1 of ", StringComparison.Ordinal) && p.Why.Contains("Read(./**/bin/**)")), "a deny rule removed");

                project["hooks"] = new JObject();
                File.Delete(Path.Combine(dir, ".claude", "hooks", TokenSaverSetup.HookFileName));
                File.WriteAllText(Path.Combine(dir, ".claude", "hooks", TokenSaverSetup.HookFileName), "# " + TokenSaverSetup.HookMarker);
                True(SetupCheck.Find(Response(null), dir).Any(p => p.Setting == "quiet-build hook missing"), "hook gone from settings");

                True(SetupCheck.MatcherMatches("", "PowerShell") && SetupCheck.MatcherMatches("*", "Bash"), "empty and * take all");
                True(SetupCheck.MatcherMatches("Bash|PowerShell", "PowerShell") && !SetupCheck.MatcherMatches("Bash", "PowerShell"), "list and name");
                True(!SetupCheck.MatcherMatches("Bash", "BashOutput"), "whole name only");
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }
    }
}
