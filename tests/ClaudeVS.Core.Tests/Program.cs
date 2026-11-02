using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Context;
using ClaudeVS.Core.Setup;
using ClaudeVS.Core.Tokens;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Tests
{
    internal static class Program
    {
        private static int Main()
        {
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
