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
            Eq("Claude Code started in Manual mode, not Auto: your settings, account or provider don't allow Auto here. The mode picker now shows Manual.",
                SessionNotices.PermissionMode(Init("\"permissionMode\":\"default\""), PermissionModeChoice.Auto), "auto refused");
            Eq(null, SessionNotices.PermissionMode(accept, PermissionModeChoice.Default), "Manual: your settings' defaultMode decides");
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
    }
}
