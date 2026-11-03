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
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length >= 4 && args[0] == "--live-ask")
                return LiveSmoke.RunInteractive(args[1], (PermissionModeChoice)Enum.Parse(typeof(PermissionModeChoice), args[2], true), args[3]);
            if (args.Length > 0 && args[0] == "--live")
                return LiveSmoke.Run(args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? (PermissionModeChoice)Enum.Parse(typeof(PermissionModeChoice), args[2], true) : PermissionModeChoice.Plan,
                    args.Length > 3 ? int.Parse(args[3]) : 2);

            var tests = typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name.StartsWith("Test_")).ToList();
            int failed = 0;
            foreach (var t in tests)
            {
                try { t.Invoke(null, null); Console.WriteLine("PASS " + t.Name); }
                catch (TargetInvocationException ex) { failed++; Console.WriteLine("FAIL " + t.Name + ": " + ex.InnerException?.Message); }
            }
            Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
            return failed == 0 ? 0 : 1;
        }

        private static void Eq<T>(T expected, T actual, string what = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected <{expected}> got <{actual}>");
        }
        private static void True(bool c, string what) { if (!c) throw new Exception(what); }

        // ---------- CLI args ----------
        private static void Test_Args_Auto_OmitsModelAndEffort()
        {
            string a = ClaudeArgsBuilder.Build(new ClaudeLaunchOptions());
            True(a.StartsWith("--print --input-format stream-json --output-format stream-json --verbose"), a);
            True(!a.Contains("--model") && !a.Contains("--effort"), a);
            True(a.Contains("--permission-mode acceptEdits"), a);
        }

        private static void Test_Args_ModelEffortResume()
        {
            string a = ClaudeArgsBuilder.Build(new ClaudeLaunchOptions { Model = ModelChoice.Haiku, Effort = EffortChoice.Low, ResumeSessionId = "abc", AppendSystemPrompt = "Be brief." });
            True(a.Contains("--model haiku") && a.Contains("--effort low") && a.Contains("--resume abc") && a.Contains("--append-system-prompt \"Be brief.\""), a);
        }

        private static void Test_Quote_Windows()
        {
            Eq("plain", ClaudeArgsBuilder.Quote("plain"));
            Eq("\"a b\"", ClaudeArgsBuilder.Quote("a b"));
            Eq("\"say \\\"hi\\\"\"", ClaudeArgsBuilder.Quote("say \"hi\""));
            Eq("\"C:\\dir with space\\\\\"", ClaudeArgsBuilder.Quote("C:\\dir with space\\"));
        }

        private static void Test_Context_ErrorsAndFileReference()
        {
            var errors = new List<BuildError>();
            for (int i = 1; i <= 25; i++) errors.Add(new BuildError { FilePath = @"C:\sln\src\A.cs", Line = i, Code = "CS0103", Message = $"The name 'x{i}' does not exist" });
            errors.Add(new BuildError { FilePath = @"C:\sln\src\A.cs", Line = 1, Code = "CS0103", Message = "The name 'x1' does not exist" }); // duplicate
            errors.Add(new BuildError { Message = "multi\nline" });

            var b = new PromptContextBuilder(new ContextSettings { MaxErrors = 20 })
                .Build("fix these", @"C:\sln", extraFiles: new[] { @"C:\sln\src\B.cs" }, errors: errors);
            Eq(20, b.AttachedErrors, "capped");
            True(b.Text.Contains("Build errors (first 20 of 26):"), b.Text);
            True(b.Text.Contains("- `src/A.cs:L3` CS0103: The name 'x3' does not exist"), b.Text);
            True(b.Text.Contains("Relevant files: `src/B.cs`"), b.Text);
            True(b.Text.TrimEnd().EndsWith("fix these"), "prompt last");

            Eq("", PromptContextBuilder.FormatErrors(null, null, 20, out int n), "no errors");
            Eq(0, n, "count");
            True(PromptContextBuilder.FormatErrors(new[] { new BuildError { Message = "multi\nline" } }, null, 20, out _).Contains("- multi line\n"), "one line per error");
        }

        // ---------- Usage log ----------
        private static void Test_UsageLog_RoundTripAndSummary()
        {
            string path = Path.Combine(Path.GetTempPath(), "cvs-usage-" + Guid.NewGuid().ToString("N") + ".jsonl");
            try
            {
                var log = new UsageLog(path);
                log.Append(new UsageRecord { Solution = @"C:\a", ModelUsed = "haiku", InputTokens = 100, CacheReadTokens = 900, OutputTokens = 50, CostUsd = 0.01, CharsAvoided = 4000, Routed = true, RouteScore = -2 });
                log.Append(new UsageRecord { Solution = @"C:\a", ModelUsed = "opus", InputTokens = 1000, OutputTokens = 200, CostUsd = 0.10 });
                log.Append(new UsageRecord { Solution = @"C:\b", ModelUsed = "sonnet", InputTokens = 10, CostUsd = 1 });
                File.AppendAllText(path, "{ partial line\n");

                var all = log.ReadAll();
                Eq(3, all.Count, "malformed line skipped");
                Eq(-2.0, all[0].RouteScore, "route score round-trips");

                var a = UsageSummary.From(all, @"c:\A");
                Eq(2, a.Turns, "turns for solution a (case-insensitive)");
                True(Math.Abs(a.CostUsd - 0.11) < 1e-9, "cost " + a.CostUsd);
                Eq(1000L, a.TokensAvoided, "chars/4");
                Eq(1, a.TurnsByModel["haiku"], "by model");
                True(Math.Abs(a.CacheHitPercent - 45) < 0.01, "cache % " + a.CacheHitPercent); // 900 / 2000
                Eq(3, UsageSummary.From(all).Turns, "all solutions");
            }
            finally { File.Delete(path); }
        }

        private static void Test_UsageLog_RotatesAndReadsArchive()
        {
            string path = Path.Combine(Path.GetTempPath(), "cvs-rot-" + Guid.NewGuid().ToString("N") + ".jsonl");
            var log = new UsageLog(path, maxBytes: 300);
            try
            {
                for (int i = 0; i < 10; i++) log.Append(new UsageRecord { Solution = "s", ModelUsed = "sonnet", InputTokens = i });
                True(File.Exists(log.ArchivePath), "archive created");
                True(new FileInfo(path).Length < 300 + 200, "current file stays small");
                int n = log.ReadAll().Count;
                True(n > 1 && n <= 10, "reads archive + current: " + n); // older archives are dropped by design
                Eq(9L, log.ReadAll().Last().InputTokens, "newest last");
            }
            finally { File.Delete(path); File.Delete(log.ArchivePath); }
        }

        // ---------- Savings report ----------
        private static void Test_Savings_RoutingAndReferences()
        {
            var prices = new PriceTable(); // Opus 4/20, Sonnet 2/10, Haiku 0.1/0.5 per MTok
            var recs = new[]
            {
                // Sonnet turn: 1M input, 0.1M output → actual $3; at Opus prices $6.
                new UsageRecord { Solution = "s", ModelUsed = "claude-sonnet-5-5", Routed = true, InputTokens = 1_000_000, OutputTokens = 100_000, CostUsd = 3, CharsAvoided = 4_000_000 },
                // Opus turn counts the same in both.
                new UsageRecord { Solution = "s", ModelUsed = "claude-opus-5-5", InputTokens = 100_000, CostUsd = 0.4 },
                new UsageRecord { Solution = "other", ModelUsed = "claude-haiku-5-5", InputTokens = 1, CostUsd = 99 },
            };
            var r = SavingsReport.Build(recs, prices, "s");
            Eq(2, r.Turns, "filtered by solution");
            True(Math.Abs(r.ActualCostUsd - 3.4) < 1e-9, "actual " + r.ActualCostUsd);
            True(Math.Abs(r.OpusEquivalentUsd - 6.4) < 1e-9, "opus eq " + r.OpusEquivalentUsd);
            True(Math.Abs(r.RoutingSavedUsd - 3.0) < 1e-9, "saved " + r.RoutingSavedUsd);
            Eq(1_000_000L, r.TokensAvoided, "tokens avoided");
            True(Math.Abs(r.ReferencesSavedUsd - 2.0) < 1e-9, "refs priced at sonnet input " + r.ReferencesSavedUsd);
            True(r.ToText().Contains("Saved by model routing:     $3.00  (47%)"), r.ToText());
        }

        private static void Test_Pricing_ParseAndFamily()
        {
            var p = ModelPrice.Parse("4, 20, 5, 0.2");
            Eq(0.2, p.CacheRead, "parse");
            Eq(null, ModelPrice.Parse("4, 20"), "too few");
            Eq(null, ModelPrice.Parse("a,b,c,d"), "not numbers");
            Eq("sonnet", PriceTable.Family("claude-sonnet-5-5"), "family");
            Eq(null, PriceTable.Family("gpt"), "unknown");
            Eq(4.0, PriceTable.FromStrings("bad", null, null).Opus.Input, "falls back to default");
        }

        private static void Test_RouterReplay_FindsChangedDecisions()
        {
            var recs = new[]
            {
                new UsageRecord { Routed = true, RouteModel = "sonnet", PromptPreview = "fix the typo in this comment" }, // router now says haiku
                new UsageRecord { Routed = true, RouteModel = "haiku", PromptPreview = "fix the typo in this comment" },  // unchanged
                new UsageRecord { Routed = true, RouteModel = "opus" },                                                  // no preview: skipped
            };
            var diffs = RouterReplay.Compare(recs, R, out int replayed);
            Eq(2, replayed, "only records with preview");
            Eq(1, diffs.Count, "one changed");
            Eq("haiku", diffs[0].NowModel, "now haiku");
            Eq(200, UsageRecord.Preview(new string('x', 500)).Length, "preview capped");
        }

        // ---------- Markdown ----------
        private static void Test_Markdown_Blocks()
        {
            string md = "# Title\n\nSome **bold** and `code`.\nNext line.\n\n- one\n  continued\n- two\n  1. nested\n\n> quoted\n\n---\n```csharp\nvar x = 1;\n\nvar y = 2;\n```\nafter";
            var b = ClaudeVS.Core.Markdown.MarkdownParser.Parse(md);
            string kinds = string.Join(",", b.Select(x => x.Kind + (x.Kind == ClaudeVS.Core.Markdown.MdBlockKind.ListItem ? x.Indent.ToString() : "")));
            Eq("Heading,Paragraph,ListItem0,ListItem0,ListItem1,Quote,Rule,Code,Paragraph", kinds, "block kinds");
            Eq(1, b[0].Level, "h1");
            Eq("Text:Some ,Bold:bold,Text: and ,Code:code,Text:.\nNext line.", string.Join(",", b[1].Inlines), "inlines keep line break");
            Eq("Text:one,Text: ,Text:continued", string.Join(",", b[2].Inlines), "list continuation");
            Eq(1, b[4].Number, "ordered number");
            Eq("csharp", b[7].Language, "fence lang");
            Eq("var x = 1;\n\nvar y = 2;", b[7].Code, "code keeps blank line");
            True(b[7].Closed, "closed fence");
        }

        private static void Test_Markdown_StreamingUnclosedFence()
        {
            var b = ClaudeVS.Core.Markdown.MarkdownParser.Parse("Here:\n```js\nconst a = 1;");
            Eq(2, b.Count, "para + code");
            Eq(false, b[1].Closed, "open while streaming");
            Eq("const a = 1;", b[1].Code, "partial code");
            // A longer closing fence closes; a shorter one does not.
            Eq(true, ClaudeVS.Core.Markdown.MarkdownParser.Parse("````\nx\n````")[0].Closed, "4-tick");
            Eq(false, ClaudeVS.Core.Markdown.MarkdownParser.Parse("````\nx\n```")[0].Closed, "short fence is content");
        }

        private static void Test_Markdown_Inlines()
        {
            string Render(string s) => string.Join(",", ClaudeVS.Core.Markdown.MarkdownParser.ParseInlines(s));
            Eq("Text:call ,Code:my_func(a_b),Text: now", Render("call `my_func(a_b)` now"), "code");
            Eq("Text:snake_case_name stays", Render("snake_case_name stays"), "underscores in words are not italic");
            Eq("Italic:it,Text: and ,Bold:bo", Render("*it* and __bo__"), "italic + bold");
            Eq("Text:see ,Link:docs", Render("see [docs](https://x.y/z)"), "link");
            Eq("Text:2 * 3 = 6", Render("2 * 3 = 6"), "lone star literal");
            Eq("Text:**unclosed", Render("**unclosed"), "unclosed bold literal");
            Eq("Text:*not italic*", Render("\\*not italic\\*"), "escapes");
        }

        // ---------- Chat helpers ----------
        private static void Test_PromptHistory_BrowseAndDraft()
        {
            var h = new ClaudeVS.Core.Chat.PromptHistory(max: 3);
            h.Add("a"); h.Add("b"); h.Add("b"); h.Add("c"); h.Add("d"); // dup skipped, "a" evicted
            Eq(3, h.Count, "max + dedupe");
            Eq("d", h.Previous("draft"), "newest first");
            Eq("c", h.Previous("ignored"), "older");
            Eq("b", h.Previous(null), "oldest");
            Eq(null, h.Previous(null), "nothing older");
            Eq("c", h.Next(), "newer");
            Eq("d", h.Next(), "newest");
            Eq("draft", h.Next(), "draft restored");
            Eq(null, h.Next(), "not browsing");
        }

        private static void Test_SlashCommands_DiscoverAndMatch()
        {
            string root = Path.Combine(Path.GetTempPath(), "cvs-cmd-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "sln", ".claude", "commands", "git"));
                Directory.CreateDirectory(Path.Combine(root, "home", ".claude", "commands"));
                File.WriteAllText(Path.Combine(root, "sln", ".claude", "commands", "review.md"), "---\ndescription: \"Review the diff\"\n---\nDo it.");
                File.WriteAllText(Path.Combine(root, "sln", ".claude", "commands", "git", "pr.md"), "# Open a PR\nbody");
                File.WriteAllText(Path.Combine(root, "home", ".claude", "commands", "review.md"), "user version");
                File.WriteAllText(Path.Combine(root, "home", ".claude", "commands", "standup.md"), "Write my standup");

                var all = ClaudeVS.Core.Chat.SlashCommands.Discover(Path.Combine(root, "sln"), Path.Combine(root, "home"));
                Eq("compact,git:pr,review,standup", string.Join(",", all.Select(c => c.Name).OrderBy(n => n)), "names");
                Eq("Review the diff", all.First(c => c.Name == "review").Description, "project wins, front matter");
                Eq("Open a PR", all.First(c => c.Name == "git:pr").Description, "first line");

                Eq("/review", string.Join(",", ClaudeVS.Core.Chat.SlashCommands.Match(all, "/rev")), "/review");
                Eq(4, ClaudeVS.Core.Chat.SlashCommands.Match(all, "/").Count, "all");
                Eq(0, ClaudeVS.Core.Chat.SlashCommands.Match(all, "review").Count, "needs slash");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void Test_SessionHistory_ListAndTitles()
        {
            Eq("C--Users-Dejan-Ciev-source-myrepos-claude-vs-tokensaver",
               ClaudeVS.Core.Chat.SessionHistory.EncodeProjectPath(@"C:\Users\Dejan Ciev\source\myrepos\claude-vs-tokensaver\"), "CLI folder naming");

            string root = Path.Combine(Path.GetTempPath(), "cvs-hist-" + Guid.NewGuid().ToString("N"));
            string sln = @"C:\work\My App";
            string folder = Path.Combine(root, ClaudeVS.Core.Chat.SessionHistory.EncodeProjectPath(sln));
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "s1.jsonl"),
                    "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"content\":\"meta\"}}\n" +
                    "{\"type\":\"user\",\"message\":{\"content\":\"<command-name>/init</command-name>\"}}\n" +
                    "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Fix the\\nlogin bug\"}]}}\n");
                File.WriteAllText(Path.Combine(folder, "s2.jsonl"), "{\"type\":\"summary\",\"summary\":\"Price API refactor\"}\nnot json\n");
                File.WriteAllText(Path.Combine(folder, "empty.jsonl"), "{\"type\":\"system\"}\n");
                File.SetLastWriteTimeUtc(Path.Combine(folder, "s1.jsonl"), DateTime.UtcNow.AddHours(-1));

                var list = ClaudeVS.Core.Chat.SessionHistory.List(root, sln);
                Eq("s2,s1", string.Join(",", list.Select(s => s.Id)), "newest first, empty skipped");
                Eq("Price API refactor", list[0].Title, "summary");
                Eq("Fix the login bug", list[1].Title, "first real prompt, one line");
                Eq(0, ClaudeVS.Core.Chat.SessionHistory.List(root, @"C:\other").Count, "other project");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void Test_AtMention_Find()
        {
            var m = AtMention.Find("look at @Price", 14);
            Eq(8, m.Start, "start"); Eq("Price", m.Query, "query"); Eq(6, m.Length, "length");
            Eq("", AtMention.Find("@", 1).Query, "bare @ at start");
            Eq(null, AtMention.Find("mail me@host.com", 16), "e-mail ignored");
            Eq(null, AtMention.Find("@done and more", 14), "caret not in a mention");
            Eq("src/a", AtMention.Find("x @src/a", 8).Query, "slashes allowed");
        }

        private static void Test_FileIndex_SkipsNoiseAndRanks()
        {
            string root = Path.Combine(Path.GetTempPath(), "cvs-idx-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var f in new[] { "src/Services/PriceService.cs", "src/Api/PricesController.cs", "src/Api/OrderController.cs", "README.md",
                                          "src/bin/Debug/PriceService.dll", "node_modules/price/index.js", "wwwroot/lib/x.js" })
                {
                    string full = Path.Combine(root, f.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    File.WriteAllText(full, "");
                }
                var idx = FileIndex.Build(root);
                Eq(4, idx.Count, "noise folders skipped (bin, node_modules, wwwroot/lib)");
                Eq("src/Api/PricesController.cs,src/Services/PriceService.cs", string.Join(",", idx.Search("price")), "both start with 'price': shorter path first");
                Eq("src/Services/PriceService.cs", idx.Search("service").FirstOrDefault(), "name contains beats path contains");
                Eq("src/Api/OrderController.cs", idx.Search("ordctl").FirstOrDefault(), "subsequence");
                Eq("src/Services/PriceService.cs", idx.Search("svc/pri").FirstOrDefault(), "path-segment subsequence");
                Eq("README.md", idx.Search("").First(), "empty query: top-level files first");
                Eq(0, idx.Search("zzz").Count, "no match");

                var capped = FileIndex.Build(root, maxFiles: 2);
                True(capped.Truncated && capped.Count == 2, "cap");
            }
            finally { Directory.Delete(root, true); }
        }

        // ---------- Real CLI output ----------
        /// <summary>One real turn from Claude Code 2.1.316 (sanitized), captured by the --live smoke test.</summary>
        private static void Test_Parser_RealCli_2_1_316()
        {
            var parser = new ClaudeStreamParser();
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.316-turn.jsonl"))
                .SelectMany(l => parser.Parse(l)).ToList();

            var start = events.Single(e => e.Kind == ClaudeEventKind.SessionStarted);
            Eq("claude-haiku-5-5", start.Model, "init reports the requested model");
            Eq("PING", string.Concat(events.Where(e => e.Kind == ClaudeEventKind.TextDelta).Select(e => e.Text)), "streamed text");
            Eq(0, events.Count(e => e.Kind == ClaudeEventKind.AssistantText), "no duplicate full text when deltas were streamed");
            Eq("claude-sonnet-5-5", events.Last(e => e.Kind == ClaudeEventKind.AssistantUsage).Model, "model that actually answered");
            Eq(0, events.Count(e => e.Kind == ClaudeEventKind.Error), "status / rate_limit lines are ignored, not errors");

            var done = events.Single(e => e.Kind == ClaudeEventKind.TurnCompleted);
            True(!done.IsError, "success");
            Eq(0.0417311, done.TotalCostUsd, "cost");
            Eq(1_000_000L, done.ContextWindow, "context window from modelUsage");
            True(done.Usage.CacheRead > 0 && done.LastRequestUsage.ContextTokens > 20_000, "usage: " + done.Usage.Total);
            Eq("00000000-0000-0000-0000-000000000001", parser.SessionId, "session id");
        }

        /// <summary>Real AskUserQuestion and ExitPlanMode permission requests from Claude Code 2.1.316 (sanitized).</summary>
        private static void Test_InteractiveRequests_RealCli()
        {
            var parser = new ClaudeStreamParser();
            var reqs = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.316-permissions.jsonl"))
                .SelectMany(l => parser.Parse(l)).Where(e => e.Kind == ClaudeEventKind.PermissionRequest).ToList();
            Eq(2, reqs.Count, "two permission requests");

            var ask = reqs[0];
            Eq(AskUserQuestionTool.Name, ask.ToolName, "ask tool");
            Eq("req-1", ask.RequestId, "request id");
            var qs = AskUserQuestionTool.Parse(ask.ToolInput);
            Eq(1, qs.Count, "one question");
            Eq("Which color do you prefer?", qs[0].Text, "question text");
            Eq("Color", qs[0].Header, "header");
            Eq("Red,Blue", string.Join(",", qs[0].Options.Select(o => o.Label)), "options");
            Eq("Choose blue", qs[0].Options[1].Description, "description");

            // The exact answer shape the real CLI accepted (Claude replied with the chosen color).
            var answer = AskUserQuestionTool.BuildAnswer(ask.ToolInput, new Dictionary<string, IReadOnlyList<string>> { ["Which color do you prefer?"] = new[] { "Red" } });
            Eq("Red", (string)answer["answers"]["Which color do you prefer?"], "answers map");
            Eq(1, ((JArray)answer["questions"]).Count, "original input kept");
            Eq(null, ask.ToolInput["answers"], "request input not modified");
            Eq("A, C", (string)AskUserQuestionTool.BuildAnswer(new JObject(), new Dictionary<string, IReadOnlyList<string>> { ["q"] = new[] { "A", " ", "C" } })["answers"]["q"], "multi-select joined");

            var plan = reqs[1];
            Eq(ExitPlanModeTool.Name, plan.ToolName, "plan tool");
            string md = ExitPlanModeTool.Plan(plan.ToolInput);
            True(md.StartsWith("# Plan: add hello.txt") && md.Contains("## Steps"), md);
            True(ExitPlanModeTool.RevisionMessage("use README").EndsWith("use README"), "feedback included");
        }

        private static void Test_TimeAgo()
        {
            var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
            string Ago(TimeSpan t) => ClaudeVS.Core.Chat.SessionHistory.TimeAgo(now - t, now);
            Eq("now", Ago(TimeSpan.FromSeconds(30)), "now");
            Eq("5m", Ago(TimeSpan.FromMinutes(5)), "minutes");
            Eq("3h", Ago(TimeSpan.FromHours(3.5)), "hours");
            Eq("2d", Ago(TimeSpan.FromDays(2)), "days");
            Eq("3w", Ago(TimeSpan.FromDays(27)), "weeks");
            Eq("1y", Ago(TimeSpan.FromDays(400)), "years");
        }

        private static void Test_PermissionModes()
        {
            Eq("auto", PermissionModeChoice.Auto.ToCli(), "auto flag");
            Eq(null, PermissionModeChoice.Default.ToCli(), "manual = no flag");
            Eq("default", PermissionModeChoice.Default.ToControl(), "control needs 'default'");
            Eq("plan", PermissionModeChoice.Plan.ToControl(), "plan");
            Eq("Edit automatically", PermissionModeChoice.AcceptEdits.Describe().Label, "label");
            Eq(PermissionModeChoice.AcceptEdits, PermissionModeChoice.Default.Next(), "cycle 1");
            Eq(PermissionModeChoice.Default, PermissionModeChoice.Auto.Next(), "cycle wraps");
            Eq(PermissionModeChoice.Default, PermissionModeChoice.BypassPermissions.Next(), "bypass is not in the cycle");
            True(ClaudeArgsBuilder.Build(new ClaudeLaunchOptions { PermissionMode = PermissionModeChoice.Auto }).Contains("--permission-mode auto"), "launch flag");
        }

        // ---------- Edit diff ----------
        private static void Test_EditDiff_ReconstructBefore()
        {
            var edit = FileChange.FromToolUse("Edit", JObject.Parse("{\"file_path\":\"C:/a.cs\",\"old_string\":\"int x = 1;\",\"new_string\":\"int x = 2;\"}"));
            Eq("C:/a.cs", edit.FilePath, "path");
            Eq("a\nint x = 1;\nb", edit.ReconstructBefore("a\nint x = 2;\nb"), "single edit undone");
            Eq(null, edit.ReconstructBefore("a\nint x = 3;\nb"), "file changed since: null");
            Eq(null, edit.ReconstructBefore("int x = 2; int x = 2;"), "ambiguous: null");

            var multi = FileChange.FromToolUse("MultiEdit", JObject.Parse(
                "{\"file_path\":\"b.cs\",\"edits\":[{\"old_string\":\"foo\",\"new_string\":\"bar\",\"replace_all\":true},{\"old_string\":\"bar()\",\"new_string\":\"baz()\"}]}"));
            // before: "foo(); foo; x" → all foo→bar: "bar(); bar; x" → bar()→baz(): "baz(); bar; x"
            Eq("foo(); foo; x", multi.ReconstructBefore("baz(); bar; x"), "multi edit undone in reverse order");

            var lf = FileChange.FromToolUse("Edit", JObject.Parse("{\"file_path\":\"e.cs\",\"old_string\":\"a\\nb\",\"new_string\":\"a\\nc\"}"));
            Eq("x\r\na\r\nb\r\ny", lf.ReconstructBefore("x\r\na\r\nc\r\ny"), "LF edit text on a CRLF file");

            Eq(true, FileChange.FromToolUse("Write", JObject.Parse("{\"file_path\":\"c.cs\",\"content\":\"x\"}")).IsWrite, "write");
            Eq(null, FileChange.FromToolUse("Write", JObject.Parse("{\"file_path\":\"c.cs\"}")).ReconstructBefore("x"), "write can't be rebuilt");
            Eq(null, FileChange.FromToolUse("Read", JObject.Parse("{\"file_path\":\"c.cs\"}")), "read is not a change");
            Eq(null, FileChange.FromToolUse("Edit", JObject.Parse("{\"file_path\":\"d.cs\",\"old_string\":\"gone\",\"new_string\":\"\"}")).ReconstructBefore("abc"), "deletion: null");
        }

        // ---------- Error hints ----------
        private static void Test_ErrorHints_KnownAndUnknown()
        {
            True(ClaudeErrorHints.Explain("Invalid API key · Please run /login")?.Contains("/login") == true, "login");
            True(ClaudeErrorHints.Explain("API Error: 429 {\"type\":\"rate_limit_error\"}")?.Contains("Rate limited") == true, "rate limit");
            True(ClaudeErrorHints.Explain("API Error: 529 {\"type\":\"overloaded_error\"}")?.Contains("overloaded") == true, "overloaded");
            True(ClaudeErrorHints.Explain("Prompt is too long")?.Contains("/compact") == true, "too long");
            Eq(null, ClaudeErrorHints.Explain("Build failed at line 401"), "no false positive on numbers");
            Eq(null, ClaudeErrorHints.Explain(""), "empty");
        }

        // ---------- Parser ----------
        private static void Test_Parser_FullTurn()
        {
            var p = new ClaudeStreamParser();
            var all = new List<ClaudeEvent>();
            foreach (var line in new[]
            {
                "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s1\",\"model\":\"claude-sonnet-x\"}",
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{\"id\":\"m1\"}}}",
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hel\"}}}",
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"lo\"}}}",
                "{\"type\":\"assistant\",\"parent_tool_use_id\":null,\"message\":{\"id\":\"m1\",\"content\":[{\"type\":\"text\",\"text\":\"Hello\"},{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\",\"input\":{\"file_path\":\"src/A.cs\"}}],\"usage\":{\"input_tokens\":5,\"output_tokens\":7,\"cache_read_input_tokens\":1000,\"cache_creation_input_tokens\":200}}}",
                "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"0123456789\"}]}}",
                "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"Hello\",\"session_id\":\"s1\",\"total_cost_usd\":0.05,\"duration_ms\":1200,\"num_turns\":2,\"usage\":{\"input_tokens\":10,\"output_tokens\":20,\"cache_read_input_tokens\":3000,\"cache_creation_input_tokens\":400,\"iterations\":[{\"input_tokens\":2,\"output_tokens\":3,\"cache_read_input_tokens\":2000,\"cache_creation_input_tokens\":100}]},\"modelUsage\":{\"claude-sonnet-x\":{\"contextWindow\":1000000}}}",
            }) all.AddRange(p.Parse(line));

            Eq("s1", p.SessionId);
            Eq(2, all.Count(e => e.Kind == ClaudeEventKind.TextDelta), "deltas");
            Eq(0, all.Count(e => e.Kind == ClaudeEventKind.AssistantText), "duplicate full text suppressed");
            var tool = all.Single(e => e.Kind == ClaudeEventKind.ToolUse);
            Eq("Read: src/A.cs", tool.Text);
            Eq(10, all.Single(e => e.Kind == ClaudeEventKind.ToolResult).ResultChars);
            var done = all.Single(e => e.Kind == ClaudeEventKind.TurnCompleted);
            Eq(2102L, done.LastRequestUsage.ContextTokens, "context from last iteration");
            Eq(1000000L, done.ContextWindow ?? 0, "window");
            Eq(0.05, done.TotalCostUsd ?? 0);
        }

        private static void Test_Parser_NoDeltas_FallsBackToFullText()
        {
            var p = new ClaudeStreamParser();
            var ev = p.Parse("{\"type\":\"assistant\",\"message\":{\"id\":\"m9\",\"content\":[{\"type\":\"text\",\"text\":\"Full\"}]}}");
            Eq("Full", ev.Single(e => e.Kind == ClaudeEventKind.AssistantText).Text);
        }

        private static void Test_Parser_PermissionRequest_And_Garbage()
        {
            var p = new ClaudeStreamParser();
            var ev = p.Parse("{\"type\":\"control_request\",\"request_id\":\"r1\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Bash\",\"input\":{\"command\":\"dotnet build\"},\"tool_use_id\":\"u1\"}}").Single();
            Eq(ClaudeEventKind.PermissionRequest, ev.Kind);
            Eq("r1", ev.RequestId);
            Eq("Bash: dotnet build", ev.Text);
            Eq(ClaudeEventKind.Error, p.Parse("npm WARN something").Single().Kind);
        }

        // ---------- Token meter ----------
        private static void Test_Meter_Context_Cost_Advice()
        {
            var m = new TokenMeter(new TokenMeterSettings { LargeToolResultChars = 1000 });
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolUseId = "t", ToolName = "Bash" });
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolResult, ToolUseId = "t", ResultChars = 5000 });
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { Input = 100, CacheRead = 900 }, LastRequestUsage = new TokenUsage { Input = 10, CacheRead = 140_000 }, ContextWindow = 200_000, TotalCostUsd = 0.10 });
            True(Math.Abs(m.ContextPercent - 70.005) < 0.01, "pct " + m.ContextPercent);
            Eq(90.0, Math.Round(m.CacheHitPercent, 1));
            var adv = m.GetAdvice();
            True(adv.Any(a => a.SuggestedCommand == "/compact" && a.Level == AdviceLevel.Warning), "compact advice");
            True(adv.Any(a => a.Message.Contains("Bash")), "large tool result advice");

            // Process relaunch: cumulative cost restarts from 0.
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, TotalCostUsd = 0.02 });
            Eq(0.12, Math.Round(m.SessionCostUsd, 4));
            Eq(0.02, Math.Round(m.LastTurnCostUsd, 4));
        }

        // ---------- Router ----------
        private static ModelRouter R => new ModelRouter();

        private static void Test_Router_Simple_GoesHaiku()
        {
            var d = R.Route(new RouteRequest { Prompt = "fix the typo in this comment" });
            Eq(ModelChoice.Haiku, d.Model, d.ToString());
        }

        private static void Test_Router_Normal_GoesSonnet()
        {
            var d = R.Route(new RouteRequest { Prompt = "Add an endpoint GET /api/prices/{symbol} to PricesController that returns the latest price from the repository, with a unit test." });
            Eq(ModelChoice.Sonnet, d.Model, d.ToString());
        }

        private static void Test_Router_Hard_GoesOpus()
        {
            var d = R.Route(new RouteRequest { Prompt = "Investigate the root cause of the deadlock in the price ingestion pipeline and refactor the concurrency model across the solution." });
            Eq(ModelChoice.Opus, d.Model, d.ToString());
            Eq(EffortChoice.High, d.Effort, d.ToString());
        }

        private static void Test_Router_WholeWords_NoSubstringFalsePositives()
        {
            // "prefix"/"suffix" must not count as "fix", "airplane" not as "plan", "recreated" not as "create", "terror" not as "error".
            var d = R.Route(new RouteRequest { Prompt = "Show the prefix and suffix used by the airplane recreated terror sample" });
            True(!d.Reason.Contains("task keyword") && !d.Reason.Contains("hard keyword"), d.ToString());
        }

        private static void Test_Router_StemsAndEndings_StillMatch()
        {
            var hard = R.Route(new RouteRequest { Prompt = "We are optimizing this; investigating why the refactored code is slower" });
            True(hard.Reason.Contains("3 hard keyword"), hard.ToString());
            var med = R.Route(new RouteRequest { Prompt = "this fixes bugs and creates errors" });
            True(med.Reason.Contains("4 task keyword"), med.ToString()); // fixes, bugs, creates, errors
        }

        private static void Test_Router_Sticky_NoDowngradeInBigContext()
        {
            var d = R.Route(new RouteRequest { Prompt = "rename this variable", CurrentModel = ModelChoice.Opus, ContextTokens = 80_000 });
            Eq(ModelChoice.Opus, d.Model, d.ToString());
            True(d.Reason.Contains("cache"), d.Reason);
        }

        // ---------- Context builder ----------
        private static void Test_Context_SmallSelectionInlinedAndDedented()
        {
            var b = new PromptContextBuilder();
            var r = b.Build("Explain", @"C:\repo", new EditorSelection { FilePath = @"C:\repo\src\A.cs", StartLine = 10, EndLine = 11, Text = "        var x = 1;\n        return x;", Language = "csharp" });
            True(r.Text.Contains("`src/A.cs:L10-11`") || r.Text.Contains("A.cs:L10-11"), r.Text);
            True(r.Text.Contains("var x = 1;\nreturn x;"), "dedent: " + r.Text);
            Eq(2, r.InlinedLines);
        }

        private static void Test_Context_LargeSelectionReferenced()
        {
            var b = new PromptContextBuilder(new ContextSettings { InlineMaxLines = 5 });
            string text = string.Join("\n", Enumerable.Range(1, 50).Select(i => "line" + i));
            var r = b.Build("Review", "/repo", new EditorSelection { FilePath = "/repo/B.cs", StartLine = 1, EndLine = 50, Text = text });
            True(r.Text.Contains("not inlined") && !r.Text.Contains("line25"), r.Text);
            Eq(0, r.InlinedLines);
        }

        // ---------- Sessions ----------
        private static string TempFile(params string[] lines)
        {
            string f = Path.Combine(Path.GetTempPath(), "claudevs-sess-" + Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllText(f, string.Join("\n", lines) + "\n");
            return f;
        }

        private static void Test_Args_SessionName()
        {
            string a = ClaudeArgsBuilder.Build(new ClaudeLaunchOptions { SessionName = "Fix login\nbug" });
            True(a.Contains("--name \"Fix login bug\""), a);
            True(!ClaudeArgsBuilder.Build(new ClaudeLaunchOptions()).Contains("--name"), "no name, no flag");
        }

        private static void Test_SessionTitle_Priority()
        {
            const string prompt = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"First prompt\"}}";
            const string ai = "{\"type\":\"ai-title\",\"aiTitle\":\"AI title\",\"sessionId\":\"s\"}";
            const string custom = "{\"type\":\"custom-title\",\"customTitle\":\"My name\",\"sessionId\":\"s\"}";
            var files = new[] { TempFile(prompt), TempFile(prompt, ai), TempFile(prompt, ai, custom, ai), TempFile("{\"type\":\"user\",\"isSidechain\":true,\"message\":{\"content\":\"x\"}}") };
            try
            {
                Eq("First prompt", SessionHistory.ReadTitle(files[0]));
                Eq("AI title", SessionHistory.ReadTitle(files[1]));
                Eq("My name", SessionHistory.ReadTitle(files[2]), "custom title wins over a later AI title");
                Eq(null, SessionHistory.ReadTitle(files[3]), "nothing to resume");
            }
            finally { foreach (var f in files) File.Delete(f); }
        }

        private static void Test_SessionTitle_FoundInTailOfBigFile()
        {
            var lines = new List<string> { "{\"type\":\"user\",\"message\":{\"content\":\"First prompt\"}}" };
            string filler = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + new string('x', 2000) + "\"}]}}";
            for (int i = 0; i < 600; i++) lines.Add(filler); // ~1.2 MB: head and tail are read separately
            lines.Add("{\"type\":\"custom-title\",\"customTitle\":\"Named late\",\"sessionId\":\"s\"}");
            string f = TempFile(lines.ToArray());
            try { Eq("Named late", SessionHistory.ReadTitle(f)); }
            finally { File.Delete(f); }
        }

        private static void Test_SessionMeta_NameArchiveApply()
        {
            string dir = Path.Combine(Path.GetTempPath(), "claudevs-meta-" + Guid.NewGuid().ToString("N"));
            var store = new SessionMetaStore(Path.Combine(dir, "sessions.json"));
            try
            {
                Eq(null, store.Get("a"));
                store.SetName("a", "  Login\nwork ");
                store.SetArchived("b", true);
                Eq("Login work", store.Get("a").Name);
                var list = store.Apply(new List<SessionInfo> { new SessionInfo { Id = "a", Title = "cli" }, new SessionInfo { Id = "b", Title = "cli b" }, new SessionInfo { Id = "c", Title = "cli c" } });
                Eq("Login work", list[0].Title);
                Eq("cli b", list[1].Title);
                True(list[1].Archived && !list[0].Archived && !list[2].Archived, "archived flag");
                store.SetName("a", "");
                Eq(null, store.Get("a"), "clearing the name forgets the session");
                store.SetArchived("b", false);
                Eq(null, store.Get("b"));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        private static void Test_SessionTranscript_Read()
        {
            string f = TempFile(
                "{\"type\":\"custom-title\",\"customTitle\":\"n\"}",
                "{\"type\":\"user\",\"uuid\":\"u1\",\"message\":{\"content\":\"How does login work?\"}}",
                "{\"type\":\"user\",\"uuid\":\"u0\",\"isMeta\":true,\"message\":{\"content\":\"meta\"}}",
                "{\"type\":\"user\",\"uuid\":\"u2\",\"message\":{\"content\":\"<local-command-caveat>x</local-command-caveat>\"}}",
                "{\"type\":\"assistant\",\"uuid\":\"a1\",\"message\":{\"content\":[{\"type\":\"thinking\",\"thinking\":\"hmm\"}]}}",
                "{\"type\":\"assistant\",\"uuid\":\"a2\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Let me look.\"}]}}",
                "{\"type\":\"assistant\",\"uuid\":\"a3\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\",\"input\":{\"file_path\":\"C:/a/Login.cs\"}}]}}",
                "{\"type\":\"user\",\"uuid\":\"u3\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"0123456789\"}]}}",
                "{\"type\":\"assistant\",\"uuid\":\"a3\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"dup\",\"name\":\"Read\",\"input\":{}}]}}",
                "{\"type\":\"assistant\",\"uuid\":\"a4\",\"isSidechain\":true,\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"subagent\"}]}}",
                "{\"type\":\"assistant\",\"uuid\":\"a5\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"It uses **JWT**.\"}]}}",
                "{\"type\":\"assistant\",\"uuid\":\"a6\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Done.\"}]}}",
                "{\"type\":\"system\",\"uuid\":\"s1\",\"subtype\":\"compact_boundary\"}",
                "{\"type\":\"user\",\"uuid\":\"u4\",\"isCompactSummary\":true,\"message\":{\"content\":\"This session is being continued…\"}}",
                "{\"type\":\"user\",\"uuid\":\"u5\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"[Request interrupted by user]\"}]}}",
                "not json",
                "{\"type\":\"user\",\"uuid\":\"u6\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Thanks\"}]}}");
            try
            {
                var t = SessionTranscript.Read(f);
                Eq("User|Answer|Tool|Answer|Compacted|User", string.Join("|", t.Items.Select(i => i.Kind)));
                Eq("How does login work?", t.Items[0].Text);
                Eq("u1", t.Items[0].Uuid, "prompt keeps its message id (for rewind)");
                Eq("Read: C:/a/Login.cs", t.Items[2].Text);
                Eq(10, t.Items[2].ResultChars, "result attached to its tool call");
                Eq("It uses **JWT**.\n\nDone.", t.Items[3].Text, "consecutive text blocks are one answer");

                var last2 = SessionTranscript.Read(f, maxItems: 2);
                Eq(4, last2.Skipped);
                Eq(TranscriptItemKind.Compacted, last2.Items[0].Kind);
            }
            finally { File.Delete(f); }
        }

        private static void Test_Export_Markdown()
        {
            var t = new SessionTranscript();
            t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.User, Text = "How does login work?" });
            t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.Answer, Text = "Let me look." });
            t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.Tool, Text = "Read: C:/a/Login.cs", ResultChars = 8000 });
            t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.Tool, Text = "Bash: echo `x`", ResultChars = 0, IsError = true });
            t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.Answer, Text = "It uses **JWT**." });
            t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.Compacted });
            t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.User, Text = "Thanks" });
            string md = ConversationExport.ToMarkdown(t, "Login\nquestions", new DateTime(2026, 11, 3, 10, 5, 0));
            string expected =
                "# Login questions\n\n*Exported from Claude Code in Visual Studio on 2026-11-03 10:05*\n" +
                "\n## You\n\nHow does login work?\n" +
                "\n## Claude\n\nLet me look.\n" +
                "\n- 🔧 `Read: C:/a/Login.cs` · ~2.0k tok\n- 🔧 `Bash: echo 'x'` · failed\n" +
                "\nIt uses **JWT**.\n" +
                "\n---\n\n*Conversation compacted here; Claude continued from a summary of the part above.*\n" +
                "\n## You\n\nThanks\n";
            Eq(expected, md);
            True(!ConversationExport.ToMarkdown(t, "x", DateTime.Now, includeTools: false).Contains("🔧"), "tools left out");
        }

        private static void Test_Export_FileName()
        {
            Eq("Fix - the bug.md", ConversationExport.FileName("Fix / the bug?"));
            Eq("claude-conversation.md", ConversationExport.FileName("  "));
            Eq(63, ConversationExport.FileName(new string('a', 100)).Length);
            True(SessionHistory.SessionFile(@"C:\p", @"C:\src\Shop", "abc").EndsWith(@"C--src-Shop\abc.jsonl"), "session file path");
        }

        // ---------- Phase 7 ----------
        private static void Test_Question_PreviewsParsedAndAnnotated()
        {
            var input = JObject.Parse("{\"questions\":[{\"question\":\"Layout?\",\"header\":\"Layout\",\"multiSelect\":false,\"options\":[" +
                "{\"label\":\"Grid\",\"description\":\"cards\",\"preview\":\"[ ][ ]\\n[ ][ ]\"},{\"label\":\"List\",\"description\":\"rows\"}]}]}");
            var q = AskUserQuestionTool.Parse(input).Single();
            Eq("[ ][ ]\n[ ][ ]", q.Options[0].Preview);
            Eq(null, q.Options[1].Preview);
            var withPreview = AskUserQuestionTool.BuildAnswer(input, new Dictionary<string, IReadOnlyList<string>> { ["Layout?"] = new[] { "Grid" } });
            Eq("Grid", (string)withPreview["answers"]["Layout?"]);
            Eq("[ ][ ]\n[ ][ ]", (string)withPreview.SelectToken("annotations['Layout?'].preview"));
            var without = AskUserQuestionTool.BuildAnswer(input, new Dictionary<string, IReadOnlyList<string>> { ["Layout?"] = new[] { "List" } });
            True(without["annotations"] == null, "no preview, no annotations");
        }

        private static void Test_Sanitizer_RemovesInvisible()
        {
            string hidden = string.Concat("ignore previous".Select(ch => char.ConvertFromUtf32(0xE0000 + ch))); // tag-character smuggling
            string input = "Fix​ the‮ bug" + hidden + "﻿.";
            Eq("Fix the bug.", PromptSanitizer.RemoveInvisible(input, out int removed));
            Eq(3 + "ignore previous".Length, removed);
            Eq("👩‍💻 کتاب‌ها", PromptSanitizer.RemoveInvisible("👩‍💻 کتاب‌ها", out removed), "ZWJ emoji and ZWNJ kept");
            Eq(0, removed);
            string plain = "nothing to do";
            True(ReferenceEquals(plain, PromptSanitizer.RemoveInvisible(plain, out _)), "no copy when clean");
        }

        private static void Test_Sanitizer_MarksLongPastes()
        {
            string log = "line1\nline2\nline3\nline4";
            Eq("Why does this fail?\n[Pasted text]\nline1\nline2\nline3\nline4\n[End of pasted text]\nThanks",
               PromptSanitizer.MarkPastes("Why does this fail?" + log + "Thanks", new[] { log }));
            Eq("short paste", PromptSanitizer.MarkPastes("short paste", new[] { "short" }), "short pastes are not marked");
            Eq("edited", PromptSanitizer.MarkPastes("edited", new[] { log }), "paste no longer in the text");
            True(PromptSanitizer.IsLongPaste(new string('x', 801)) && !PromptSanitizer.IsLongPaste(new string('x', 800)), "800-char limit");
        }

        private static void Test_Compaction_SummaryReadAndCleaned()
        {
            string summary = "This session is being continued from a previous conversation that ran out of context. The summary below covers the earlier portion.\\n\\n<system-reminder>\\n<tools-used-before-summary>Read</tools-used-before-summary>\\n</system-reminder>\\n\\n## Summary\\n1. Fixed the login bug.";
            string f = TempFile(
                "{\"type\":\"user\",\"uuid\":\"u1\",\"message\":{\"content\":\"Fix login\"}}",
                "{\"type\":\"system\",\"uuid\":\"s1\",\"subtype\":\"compact_boundary\",\"compactMetadata\":{\"trigger\":\"auto\",\"preTokens\":697718}}",
                "{\"type\":\"user\",\"uuid\":\"u2\",\"isCompactSummary\":true,\"message\":{\"role\":\"user\",\"content\":\"" + summary + "\"}}",
                "{\"type\":\"user\",\"uuid\":\"u3\",\"message\":{\"content\":\"Next\"}}");
            try
            {
                var t = SessionTranscript.Read(f);
                Eq("User|Compacted|User", string.Join("|", t.Items.Select(i => i.Kind)));
                var c = t.Items[1];
                Eq(697718L, c.PreTokens);
                Eq("auto", c.Trigger);
                Eq("## Summary\n1. Fixed the login bug.", c.Text, "preamble and system-reminder removed");
                Eq(c.Text, SessionTranscript.ReadLastCompactSummary(f), "live read from the tail");
                True(ConversationExport.ToMarkdown(t, "x", DateTime.Now).Contains("<summary>Summary Claude continued from</summary>\n\n## Summary"), "export includes the summary");
                File.AppendAllText(f, "{\"type\":\"system\",\"uuid\":\"s2\",\"subtype\":\"compact_boundary\"}\n");
                Eq(null, SessionTranscript.ReadLastCompactSummary(f), "a new compaction whose summary is not written yet");
            }
            finally { File.Delete(f); }
        }

        private static void Test_Transcript_Timestamps()
        {
            string f = TempFile(
                "{\"type\":\"user\",\"uuid\":\"u1\",\"timestamp\":\"2026-11-02T09:42:22.258Z\",\"message\":{\"content\":\"Hi\"}}",
                "{\"type\":\"assistant\",\"uuid\":\"a1\",\"timestamp\":\"2026-11-02T09:42:25.000Z\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Hello\"}]}}",
                "{\"type\":\"assistant\",\"uuid\":\"a2\",\"timestamp\":\"2026-11-02T09:42:31.500Z\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"there\"}]}}",
                "{\"type\":\"user\",\"uuid\":\"u2\",\"message\":{\"content\":\"No time\"}}");
            try
            {
                var t = SessionTranscript.Read(f);
                Eq(new DateTime(2026, 11, 2, 9, 42, 22, 258, DateTimeKind.Utc), t.Items[0].TimestampUtc);
                Eq(DateTimeKind.Utc, t.Items[0].TimestampUtc.Value.Kind);
                Eq(new DateTime(2026, 11, 2, 9, 42, 31, 500, DateTimeKind.Utc), t.Items[1].TimestampUtc, "answer: time of its last part");
                Eq(null, t.Items[2].TimestampUtc);
            }
            finally { File.Delete(f); }
        }

        private static string Colored(string code, string lang) =>
            string.Join(" ", ClaudeVS.Core.Markdown.SyntaxHighlighter.Tokenize(code, lang).Select(s => s.Kind.ToString()[0] + ":" + code.Substring(s.Start, s.Length)));

        private static void Test_Syntax_CSharp()
        {
            Eq("K:public K:class K:int N:42 K:var S:\"a \\\" b\" C:// done", Colored("public class Foo { int x = 42; var s = \"a \\\" b\"; } // done", "csharp"));
            Eq("S:@\"C:\\x\"\"y\" C:/* multi\nline */", Colored("@\"C:\\x\"\"y\" /* multi\nline */", "cs"));
            Eq("", Colored("public class X", "text"), "plain text is not colored");
        }

        private static void Test_Syntax_OtherLanguages()
        {
            Eq("K:def S:'''doc''' C:# note K:return K:None", Colored("def f():\n    '''doc'''  # note\n    return None", "python"));
            Eq("K:SELECT K:from K:where S:'x' C:-- all", Colored("SELECT * from t where a = 'x' -- all", "sql"));
            Eq("T:<Project A:Sdk S:\"Microsoft.NET.Sdk\" T:> C:<!-- c --> T:</Project T:>", Colored("<Project Sdk=\"Microsoft.NET.Sdk\"><!-- c --></Project>", "xml"));
            Eq("S:\"a\" K:true S:\"b\" N:1.5", Colored("{\"a\": true, \"b\": 1.5}", "json"));
            Eq("K:const S:`t ${x}`", Colored("const s = `t ${x}`;", "ts"));
            Eq("S:\"s\" N:7 C:// c", Colored("x := \"s\" + 7 // c", "unknownlang"), "unknown languages still get strings, numbers and comments");
            Eq("K:$true K:-eq C:# c", Colored("$x = $true -eq $y # c", "powershell"));
            True(Colored("a.b(1)", "cs").Contains("N:1") && !Colored("x1 = 2", "cs").Contains("N:1"), "digits inside names are not numbers");
        }

        // ---------- Images ----------
        private static void Test_Images_TokensAndFit()
        {
            Eq(1600, ImageTokens.Estimate(1000, 1200)); // 1,200,000 / 750
            Eq(0, ImageTokens.Estimate(0, 100));
            Eq((1568, 882), ImageTokens.FitWithin(3840, 2160), "4K screenshot scaled to the API limit");
            Eq((800, 600), ImageTokens.FitWithin(800, 600), "small image unchanged");
            Eq((441, 1568), ImageTokens.FitWithin(1080, 3840), "tall image: long edge is the height");
            Eq("image/jpeg", ImageTokens.MediaTypeFor(@"C:\a\Photo.JPEG"));
            Eq(null, ImageTokens.MediaTypeFor("x.bmp"));
        }

        private static void Test_Images_InUserMessage()
        {
            var img = new PromptImage { MediaType = "image/png", Base64 = "iVBORw0KGgo=", Width = 10, Height = 10 };
            var o = JObject.Parse(ClaudeSession.BuildUserMessage("What is this?", "id1", new[] { img, new PromptImage() }));
            var content = (JArray)o.SelectToken("message.content");
            Eq(2, content.Count, "empty image skipped; image first, then text");
            Eq("image", (string)content[0]["type"]);
            Eq("base64", (string)content[0].SelectToken("source.type"));
            Eq("image/png", (string)content[0].SelectToken("source.media_type"));
            Eq("iVBORw0KGgo=", (string)content[0].SelectToken("source.data"));
            Eq("What is this?", (string)content[1]["text"]);
            var alone = (JArray)JObject.Parse(ClaudeSession.BuildUserMessage("", null, new[] { img })).SelectToken("message.content");
            Eq(1, alone.Count, "image without text: no empty text block");
        }

        // ---------- Rewind ----------
        private static void Test_Rewind_UserMessageCarriesId()
        {
            var o = JObject.Parse(ClaudeSession.BuildUserMessage("Fix it", "abc-1"));
            Eq("user", (string)o["type"]);
            Eq("abc-1", (string)o["uuid"]);
            Eq("Fix it", (string)o.SelectToken("message.content[0].text"));
            True(JObject.Parse(ClaudeSession.BuildUserMessage("x", null))["uuid"] == null, "no id, no uuid field");
        }

        private static void Test_Rewind_ParseRealResponses()
        {
            // Captured from Claude Code 2.1.317 (paths shortened): dry run, real rewind, and the errors it can return.
            var parser = new ClaudeStreamParser();
            var dry = parser.Parse("{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"r1\",\"response\":{\"canRewind\":true,\"filesChanged\":[\"C:\\\\w\\\\a.txt\"],\"insertions\":1,\"deletions\":1}}}").Single();
            var r = RewindResult.Parse(dry.Payload);
            True(r.Ok, "dry run ok");
            Eq(@"C:\w\a.txt", r.FilesChanged.Single());
            Eq("1 file, +1 −1", r.Summary());

            var real = RewindResult.Parse(JObject.Parse("{\"canRewind\":true,\"skippedLinks\":0}"));
            True(real.Ok && real.FilesChanged.Count == 0, "real rewind has no file list");

            var off = RewindResult.Parse(JObject.Parse("{\"canRewind\":false,\"error\":\"File rewinding is not enabled.\"}"));
            True(!off.Ok, "not enabled");
            Eq("File rewinding is not enabled.", off.Error);
        }

        // ---------- Context usage ----------
        private static void Test_ContextUsage_FromRealResponse()
        {
            string line = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.317-context-usage.jsonl")).Trim();
            var ev = new ClaudeStreamParser().Parse(line).Single();
            Eq(ClaudeEventKind.ControlResponse, ev.Kind);
            Eq("ctx1", ev.RequestId);
            True(!ev.IsError && ev.Payload != null, "payload");
            var u = ContextUsage.Parse(ev.Payload);
            Eq(16798L, u.TotalTokens);
            Eq(1_000_000L, u.MaxTokens);
            Eq(967_000L, u.AutoCompactThreshold);
            True(u.IsAutoCompactEnabled, "autocompact");
            Eq(16798L, u.Categories.Where(c => c.IsUsed).Sum(c => c.Tokens), "used categories add up to the total");
            Eq("deferred", u.Categories.Single(c => c.Id == "system_tools_deferred").Kind);
            Eq(1, u.MemoryFileCount);
            Eq(57L, u.MemoryFileTokens);
            Eq(0, u.Tips().Count(), "a fresh session needs no tips");
        }

        private static void Test_ContextUsage_TipsAndDefaults()
        {
            var u = ContextUsage.Parse(JObject.Parse("{\"categories\":[{\"id\":\"messages\",\"name\":\"Messages\",\"tokens\":120000}],\"maxTokens\":200000," +
                "\"autoCompactThreshold\":167000,\"isAutoCompactEnabled\":true,\"memoryFiles\":[{\"tokens\":6000}]," +
                "\"messageBreakdown\":{\"toolResultTokens\":90000,\"userMessageTokens\":5000,\"assistantMessageTokens\":25000}}"));
            Eq(120000L, u.TotalTokens, "total from categories when missing");
            Eq(60.0, u.Percentage, "percentage computed when missing");
            Eq("Tool results|Claude's messages|Your messages", string.Join("|", u.MessageParts().Select(p => p.Key)));
            var tips = u.Tips().ToList();
            Eq(2, tips.Count, string.Join(" / ", tips));
            True(tips[0].StartsWith("Tool results are 90"), tips[0]);
            True(tips[1].Contains("memory files"), tips[1]);
            True(ContextUsage.Parse(new JObject()).Categories.Count == 0, "empty response parses");
        }

        // ---------- Bookmarks ----------
        private static void Test_Bookmark_Title()
        {
            Eq("Fix the login bug", Bookmark.MakeTitle("\n  Fix the login bug\nmore", "answer"));
            Eq("Summary", Bookmark.MakeTitle("", "## Summary\nText"));
            Eq("Bookmark", Bookmark.MakeTitle(null, null));
            Eq(80, Bookmark.MakeTitle(new string('x', 200), null).Length);
        }

        private static void Test_Bookmark_StoreAddListRemove()
        {
            string dir = Path.Combine(Path.GetTempPath(), "claudevs-bm-" + Guid.NewGuid().ToString("N"));
            var store = new BookmarkStore(Path.Combine(dir, "bookmarks.json"));
            try
            {
                Eq(0, store.List().Count, "empty when missing");
                var t0 = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
                var a = Bookmark.Create(@"C:\src\Shop", "s1", "How does checkout work?", "It uses **Stripe**.", t0);
                var b = Bookmark.Create(@"C:\src\Shop\", "s1", "Rename Foo", "Done.", t0.AddMinutes(5));
                var c = Bookmark.Create(@"C:\src\Other", "s2", "Hi", "Hello", t0);
                store.Add(a); store.Add(b); store.Add(c);
                var shop = store.List(@"c:\src\shop");
                Eq(2, shop.Count, "per solution, case and trailing slash ignored");
                Eq(b.Id, shop[0].Id, "newest first");
                Eq(3, store.List().Count);
                True(shop[1].Matches("stripe checkout"), "matches answer and prompt words");
                True(!shop[1].Matches("paypal"), "no match");
                True(store.Remove(a.Id), "removed");
                True(!store.Remove(a.Id), "second remove is a no-op");
                Eq(1, store.List(@"C:\src\Shop").Count);

                File.WriteAllText(store.Path, "{ not json");
                Eq(0, store.List().Count, "damaged file reads as empty");
                store.Add(a);
                Eq(1, store.List().Count, "and is replaced on the next save");
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        private static void Test_Bookmark_LongAnswerIsCut()
        {
            var b = Bookmark.Create("d", null, "p", new string('a', Bookmark.MaxAnswerChars + 10), DateTime.UtcNow);
            True(b.Answer.Length < Bookmark.MaxAnswerChars + 20 && b.Answer.EndsWith("(cut)"), "cut");
        }

        // ---------- Setup ----------
        private static void Test_Setup_MergeIsIdempotentAndKeepsUserSettings()
        {
            var s = JObject.Parse("{\"permissions\":{\"deny\":[\"Read(./secrets/**)\"],\"allow\":[\"Bash(git status)\"]},\"model\":\"opus\"}");
            TokenSaverSetup.MergeSettings(s);
            string once = s.ToString();
            TokenSaverSetup.MergeSettings(s);
            Eq(once, s.ToString(), "idempotent");
            Eq("opus", (string)s["model"]);
            True(((JArray)s["permissions"]["deny"]).Any(t => (string)t == "Read(./secrets/**)"), "kept user deny");
            True(((JArray)s["permissions"]["deny"]).Any(t => (string)t == "Read(./**/bin/**)"), "added bin deny");
            Eq(1, ((JArray)s["hooks"]["PreToolUse"]).Count);
            Eq("15000", (string)s["env"]["BASH_MAX_OUTPUT_LENGTH"]);
        }

        private static void Test_Scanner_And_ClaudeMd()
        {
            string root = Path.Combine(Path.GetTempPath(), "claudevs-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Api"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "Api.Tests"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Api", "bin"));
            Directory.CreateDirectory(Path.Combine(root, "web"));
            File.WriteAllText(Path.Combine(root, "Shop.sln"), "");
            File.WriteAllText(Path.Combine(root, "src", "Api", "Api.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.EntityFrameworkCore.SqlServer\" Version=\"8.0.0\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "src", "Api", "bin", "Junk.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "tests", "Api.Tests", "Api.Tests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.0\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "web", "package.json"), "{\"name\":\"shop-web\",\"dependencies\":{\"@angular/core\":\"^19.0.0\",\"rxjs\":\"~7.8.0\"}}");
            try
            {
                var info = SolutionScanner.Scan(root);
                Eq(3, info.Projects.Count, "bin/ skipped");
                True(info.Projects.Single(p => p.Name == "Api").IsWeb, "web sdk");
                True(info.Projects.Single(p => p.Name == "Api.Tests").IsTest, "test project");
                string md = ClaudeMdGenerator.Generate(info);
                True(md.Contains("dotnet test Shop.sln"), md);
                True(md.Contains("Angular 19.0.0"), md);
                True(ClaudeMdGenerator.EstimateTokens(md) < 600, "CLAUDE.md stays small: " + ClaudeMdGenerator.EstimateTokens(md));

                var res = TokenSaverSetup.Apply(root);
                True(File.Exists(Path.Combine(root, ".claude", "hooks", TokenSaverSetup.HookFileName)), "hook written");
                var res2 = TokenSaverSetup.Apply(root);
                Eq(0, res2.Written.Count, "second apply writes nothing");
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
