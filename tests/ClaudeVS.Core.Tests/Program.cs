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
            if (args.Length > 0 && args[0] == "--live-all") return LiveSmoke.RunAll();
            if (args.Length > 0 && args[0].StartsWith("--live", StringComparison.Ordinal))
            {
                int code = RunOneLive(args);
                Console.WriteLine($"live cost: ${LiveSmoke.Spent:0.0000} (the CLI's total_cost_usd)");
                return code;
            }
            if (args.Length > 1 && args[0] == "--scan")
            {
                // Read-only: prints the CLAUDE.md that Generate Lean CLAUDE.md would write for that folder.
                string md = ClaudeMdGenerator.Generate(SolutionScanner.Scan(args[1]));
                Console.WriteLine(md);
                Console.WriteLine($"~{ClaudeMdGenerator.EstimateTokens(md)} tokens");
                return 0;
            }
            return RunUnitTests();
        }

        /// <summary>One `--live-…` test (see LiveSmoke); 2 = skipped or unknown switch.</summary>
        private static int RunOneLive(string[] args)
        {
            if (args.Length >= 4 && args[0] == "--live-ask")
                return LiveSmoke.RunInteractive(args[1], (PermissionModeChoice)Enum.Parse(typeof(PermissionModeChoice), args[2], true), args[3]);
            if (args.Length > 0 && args[0] == "--live-mcp") return LiveSmoke.RunMcp(args.Length > 1 ? args[1] : null);
            if (args.Length > 0 && args[0] == "--live-haiku-router") return LiveSmoke.RunHaikuRouter();
            if (args.Length > 0 && args[0] == "--live-session") return LiveSmoke.RunSession();
            if (args.Length > 0 && args[0] == "--live-setup") return LiveSmoke.RunSetup(args.Length > 1 ? args[1] : null);
            if (args.Length > 0 && args[0] == "--live-dialogs") return LiveSmoke.RunDialogs();
            if (args.Length > 0 && args[0] == "--live-edit-review") return LiveSmoke.RunEditReview();
            if (args.Length > 0 && args[0] == "--live-long-prompt") return LiveSmoke.RunLongPrompt();
            if (args.Length > 0 && args[0] == "--live-prices") return LiveSmoke.RunPrices();
            if (args.Length > 0 && args[0] == "--live-prompt-tokens") return LiveSmoke.RunPromptTokens();
            if (args.Length > 0 && args[0] == "--live-edit-review-interrupt") return LiveSmoke.RunEditReviewInterrupted(args.Length > 1 ? args[1] : null);
            if (args.Length > 0 && args[0] == "--live-edit-stop-allow") return LiveSmoke.RunEditReviewInterrupted(args.Length > 1 ? args[1] : null, allowPlain: true);
            if (args.Length > 0 && args[0] == "--live")
                return LiveSmoke.Run(args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? (PermissionModeChoice)Enum.Parse(typeof(PermissionModeChoice), args[2], true) : PermissionModeChoice.AcceptEdits,
                    args.Length > 3 ? int.Parse(args[3]) : 2);
            Console.WriteLine("Unknown live test " + args[0] + "; --live-all runs them all.");
            return 2;
        }

        private static int RunUnitTests()
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

        private static void Test_StoppedEdit_FileSaysWhetherWritten()
        {
            // The result text Claude Code 2.1.322 sends for an Edit that Stop ended while it ran
            const string stopped = "The user doesn't want to proceed with this tool use. The tool use was rejected (eg. if it was a file edit, the new_string was NOT written to the file). STOP what you are doing and wait for the user to tell you how to proceed. Correction to the text above: this call was stopped while running, not refused before it ran. It may have run in part or in full, and its changes may have been written.";
            True(StoppedEdit.IsStopped(true, stopped), "stopped");
            True(!StoppedEdit.IsStopped(false, stopped), "not an error: not stopped");
            True(!StoppedEdit.IsStopped(true, "The user doesn't want to proceed with this tool use. The tool use was rejected"), "a plain Deny is not a stop");

            var edit = JObject.Parse("{\"old_string\":\"beta\\ngamma\\ndelta\",\"new_string\":\"BETA\\ngamma\\nDELTA\"}");
            const string before = "alpha\nbeta\ngamma\ndelta\n", after = "alpha\nBETA\ngamma\nDELTA\n";
            Eq(StoppedEdit.State.Written, StoppedEdit.Check("Edit", edit, before, after), "written");
            Eq(StoppedEdit.State.NotWritten, StoppedEdit.Check("Edit", edit, before, before), "not written");
            Eq(StoppedEdit.State.Written, StoppedEdit.Check("Edit", edit, after, after), "our copy was taken after the write: still written");
            Eq(StoppedEdit.State.Unknown, StoppedEdit.Check("Edit", edit, before, "something else\n"), "changed by something else: unknown");
            Eq(StoppedEdit.State.Unknown, StoppedEdit.Check("Edit", edit, before, null), "file gone");
            var grow = JObject.Parse("{\"old_string\":\"x = 1;\",\"new_string\":\"x = 1;\\ny = 2;\"}"); // new text holds the old
            Eq(StoppedEdit.State.Written, StoppedEdit.Check("Edit", grow, "a\nx = 1;\nb\n", "a\nx = 1;\ny = 2;\nb\n"), "insertion written");
            Eq(StoppedEdit.State.NotWritten, StoppedEdit.Check("Edit", grow, "a\nx = 1;\nb\n", "a\nx = 1;\nb\n"), "insertion not written");
            var del = JObject.Parse("{\"old_string\":\"// old\\n\",\"new_string\":\"\"}");
            Eq(StoppedEdit.State.Written, StoppedEdit.Check("Edit", del, "a\n// old\nb\n", "a\nb\n"), "deletion written");
            Eq(StoppedEdit.State.NotWritten, StoppedEdit.Check("Edit", del, "a\n// old\nb\n", "a\n// old\nb\n"), "deletion not written");

            var write = JObject.Parse("{\"content\":\"new file\\n\"}");
            Eq(StoppedEdit.State.Written, StoppedEdit.Check("Write", write, null, "new file\n"), "write written");
            Eq(StoppedEdit.State.NotWritten, StoppedEdit.Check("Write", write, null, null), "new file never created");
            Eq(StoppedEdit.State.NotWritten, StoppedEdit.Check("Write", write, "old\n", "old\n"), "overwrite not written");

            Eq(null, StoppedEdit.Note("Edit", "a.cs", StoppedEdit.State.Unknown), "unknown: say nothing");
            string wn = StoppedEdit.Note("Edit", "notes.txt", StoppedEdit.State.Written, edit);
            True(wn.Contains("Visual Studio read notes.txt after the stop") && wn.Contains("applied in full") && wn.Contains(@"""BETA\ngamma\nDELTA"" where ""beta\ngamma\ndelta"" was"), wn);
            True(StoppedEdit.Note("Write", "b.txt", StoppedEdit.State.NotWritten).Contains("was not written; b.txt is unchanged"), "not-written note");
            True(StoppedEdit.Note("Edit", "notes.txt", StoppedEdit.State.NotWritten, edit).Contains(@"still has ""beta\ngamma\ndelta"""), "edit not applied");

            // The session queues it for the next prompt; a reviewed-edit note for the same call wins
            var s = new ClaudeSession(new ClaudeLaunchOptions());
            s.AddNextPromptNote("toolu_1", "About the stopped Edit of a.cs: its change is in the file; it was written before the stop.");
            string next = s.TakeStrandedNotes();
            True(ClaudeSession.IsEditorNote(next) && next.Contains("a.cs"), next);
            Eq(null, s.TakeStrandedNotes(), "given once");
            s.SetToolResultNote("toolu_2", "Rejected: line 4.", "b.cs", () => true);
            s.AddNextPromptNote("toolu_2", "About the stopped Edit of b.cs: its change is in the file.");
            s.MarkNotesStranded();
            string reviewed = s.TakeStrandedNotes();
            True(reviewed.Contains("Rejected: line 4.") && !reviewed.Contains("its change is in the file"), reviewed);

            // A reviewed edit that Stop caught before it was written: the reviewed note does not apply, the file check
            // says "not applied", and that is what Claude hears (before, nothing)
            s.SetToolResultNote("toolu_3", "Rejected: line 9.", "c.cs", () => false);
            s.AddNextPromptNote("toolu_3", StoppedEdit.Note("Edit", "c.cs", StoppedEdit.State.NotWritten, JObject.Parse("{\"old_string\":\"a\",\"new_string\":\"b\"}")));
            s.MarkNotesStranded();
            string notWritten = s.TakeStrandedNotes();
            True(notWritten != null && notWritten.Contains("was not applied: c.cs still has \"a\"") && !notWritten.Contains("Rejected: line 9."), notWritten);
            Eq(null, s.TakeStrandedNotes(), "given once");

            // Not written and the file says nothing either (Unknown): still nothing, rather than a guess
            s.SetToolResultNote("toolu_4", "Rejected: line 2.", "d.cs", () => false);
            s.AddNextPromptNote("toolu_4", StoppedEdit.Note("Edit", "d.cs", StoppedEdit.State.Unknown));
            s.MarkNotesStranded();
            Eq(null, s.TakeStrandedNotes(), "unknown: nothing");
        }

        private static void Test_EditNote_AfterStop_GoesWithNextPrompt()
        {
            var s = new ClaudeSession(new ClaudeLaunchOptions { EditNotesHook = true });
            bool written = true;
            s.SetToolResultNote("toolu_1", "Rejected: line 4.", "notes.txt", () => written);
            Eq(null, s.TakeStrandedNotes(), "the turn is still running: the hook may come");

            s.MarkNotesStranded(); // Stop: the turn ended, no hook ran
            string note = s.TakeStrandedNotes();
            True(note != null && ClaudeSession.IsEditorNote(note) && note.Contains("notes.txt") && note.Contains("Rejected: line 4."), note);
            Eq(null, s.TakeStrandedNotes(), "given once");

            written = false; // stopped before the change was written: nothing to say
            s.SetToolResultNote("toolu_2", "Rejected: line 9.", "a.cs", () => written);
            s.MarkNotesStranded();
            Eq(null, s.TakeStrandedNotes(), "not written");

            s.SetToolResultNote("toolu_3", "Rejected: line 2.", "b.cs", () => throw new System.IO.IOException("locked"));
            s.MarkNotesStranded();
            Eq(null, s.TakeStrandedNotes(), "a check that fails counts as not written");

            s.SetToolResultNote("toolu_4", "Rejected.", "c.cs"); // no check: dropped, never guessed
            s.MarkNotesStranded();
            Eq(null, s.TakeStrandedNotes(), "without a check");

            // A block of its own after any images; history and titles leave it out
            var msg = JObject.Parse(ClaudeSession.BuildUserMessage("did it work?", null, null, ClaudeSession.EditorNotePrefix + " x"));
            var blocks = (JArray)msg.SelectToken("message.content");
            Eq(2, blocks.Count, "note + prompt");
            True(ClaudeSession.IsEditorNote((string)blocks[0]["text"]) && (string)blocks[1]["text"] == "did it work?", msg.ToString());
            Eq(1, ((JArray)JObject.Parse(ClaudeSession.BuildUserMessage("p", null)).SelectToken("message.content")).Count, "no note: unchanged");

            // The session file keeps the user message as sent: resume and the sessions list show only the prompt
            string file = Path.Combine(Path.GetTempPath(), "cvs-note-" + Guid.NewGuid().ToString("N") + ".jsonl");
            try
            {
                var line = JObject.Parse(ClaudeSession.BuildUserMessage("did it work?", null, null, ClaudeSession.EditorNotePrefix + " About the stopped edit…"));
                line["uuid"] = "u1"; line["sessionId"] = "s1"; line["timestamp"] = "2026-11-10T13:00:00Z";
                File.WriteAllText(file, line.ToString(Newtonsoft.Json.Formatting.None) + "\n");
                Eq("did it work?", SessionHistory.ReadTitle(file), "title");
                var prompt = SessionTranscript.Read(file).Items.FirstOrDefault(i => i.Kind == TranscriptItemKind.User);
                Eq("did it work?", prompt?.Text, "redrawn prompt");
            }
            finally { File.Delete(file); }
        }

        private static void Test_FastMode_PricedAtTwice()
        {
            var prices = new PriceTable(); // Opus 5.5: 4 / 20; fast mode: 8 / 40
            var fast = TokenUsage.From(JObject.Parse("{\"input_tokens\":1000,\"output_tokens\":1000,\"speed\":\"fast\"}"));
            var std = TokenUsage.From(JObject.Parse("{\"input_tokens\":1000,\"output_tokens\":1000,\"speed\":\"standard\"}"));
            Eq(2000L, fast.FastTokens, "all of a fast request");
            Eq(0L, std.FastTokens, "standard");
            Eq(0.048, Math.Round(prices.Opus.Cost(fast), 6), "fast Opus: 1k in $0.008 + 1k out $0.04");
            Eq(0.024, Math.Round(prices.Opus.Cost(std), 6), "standard Opus");
            Eq(0.072, Math.Round(prices.Opus.Cost(fast + std), 6), "a turn that fell back to standard speed: each part at its price");

            // Real 2.1.322 result usage (Fable, standard): unchanged, to the CLI's own total_cost_usd
            var real = TokenUsage.From(JObject.Parse("{\"input_tokens\":2,\"cache_creation_input_tokens\":701,\"cache_read_input_tokens\":0,\"output_tokens\":5,\"cache_creation\":{\"ephemeral_1h_input_tokens\":701,\"ephemeral_5m_input_tokens\":0},\"speed\":\"standard\"}"));
            Eq(0.011432, Math.Round(prices.Fable.Cost(real), 6), "CLI total_cost_usd");

            // Cache savings double too (every price doubles)
            var cached = TokenUsage.From(JObject.Parse("{\"input_tokens\":10,\"output_tokens\":10,\"cache_read_input_tokens\":100000,\"speed\":\"fast\"}"));
            Eq(0.76, Math.Round(prices.Opus.CacheSavedUsd(cached), 6), "100k reads × (4 − 0.2) × 2");

            // Usage log: written only when fast; priced at twice
            var rec = new UsageRecord { ModelUsed = "claude-opus-5-5", InputTokens = 1000, OutputTokens = 1000 };
            True(!Newtonsoft.Json.JsonConvert.SerializeObject(rec).Contains("fastTokens"), "not written for standard turns");
            Eq(0.024, Math.Round(prices.Opus.Cost(rec), 6), "standard record");
            rec.FastTokens = 2000;
            True(Newtonsoft.Json.JsonConvert.SerializeObject(rec).Contains("\"fastTokens\":2000"), "written for fast turns");
            Eq(0.048, Math.Round(prices.Opus.Cost(rec), 6), "fast record");

            // Footer
            True(TurnFooter.Extras(fast).Contains("fast") && !TurnFooter.Extras(fast).Contains("partly"), TurnFooter.Extras(fast));
            True(TurnFooter.Extras(fast + std).Contains("partly fast"), TurnFooter.Extras(fast + std));
            True(!TurnFooter.Extras(std).Contains("fast"), "standard: nothing");

            // Cache break: fast and standard speed have separate caches
            var w = new CacheBreakWatch();
            var t0 = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);
            TokenUsage U(long read, long write, bool isFast) => new TokenUsage { Input = 5, CacheRead = read, CacheCreation = write, Output = 100, FastTokens = isFast ? 5 + read + write + 100 : 0 };
            True(w.Observe(U(0, 40_000, false), "claude-opus-5-5", "m1", t0, prices) == null, "first request");
            var b = w.Observe(U(0, 40_100, true), "claude-opus-5-5", "m2", t0.AddMinutes(1), prices);
            True(b != null && b.SpeedChanged && b.NowFast && !b.ModelChanged, "fast mode started while warm");
            True(b.Note().Contains("Fast mode started") && b.Note().Contains("separate caches"), b.Note());
            True(Math.Abs(b.ExtraUsd - 40_005 * (5 - 0.2) * 2 / 1e6) < 1e-9, "the rewrite at fast prices: " + b.ExtraUsd);
            var back = w.Observe(U(0, 40_200, false), "claude-opus-5-5", "m3", t0.AddMinutes(2), prices);
            True(back != null && back.SpeedChanged && !back.NowFast && back.Note().Contains("fell back to standard speed"), back?.Note());
        }

        private static void Test_Fable_ManualChoiceOnly()
        {
            Eq("fable", ModelChoice.Fable.ToCli(), "CLI alias");
            True(ClaudeArgsBuilder.Build(new ClaudeLaunchOptions { Model = ModelChoice.Fable }).Contains("--model fable"), "launch arg");

            // Picked by hand in a big conversation: the routers' cheaper picks keep Fable for its cache, as for any model
            var router = new ModelRouter { StickyContextTokens = 30000 };
            var big = new RouteRequest { Prompt = "x", CurrentModel = ModelChoice.Fable, ContextTokens = 50000 };
            Eq(ModelChoice.Fable, router.ApplyCacheStickiness(new RouteDecision { Model = ModelChoice.Opus, Effort = EffortChoice.High, Reason = "r" }, big, clearlyHard: true).Model, "Opus is no upgrade from Fable");

            // The routers never pick it
            foreach (var p in new[] { "rename x", "refactor the whole architecture to fix this deadlock across services", "explain this" })
                True(new ModelRouter().Route(new RouteRequest { Prompt = p }).Model != ModelChoice.Fable, p);
            Eq(null, HaikuRouter.Parse("{\"model\":\"fable\",\"effort\":\"high\",\"reason\":\"r\"}"), "Haiku router answer 'fable' is not taken");

            // What it costs next to Opus, where you pick it
            var prices = new PriceTable();
            Eq("2× the Opus price", prices.VersusOpus("fable"), "fable");
            Eq(null, prices.VersusOpus("opus"), "opus");
            Eq(null, prices.VersusOpus("sonnet"), "cheaper");
            Eq(null, prices.VersusOpus(null), "auto");
            Eq("2.5× the Opus price", PriceTable.FromStrings(null, null, null, "10, 50, 12.5, 0.25").VersusOpus("claude-fable-5-2"), "custom price");
        }

        // ---------- Haiku router ----------
        private static void Test_HaikuRouter_ParseAndRequest()
        {
            var d = HaikuRouter.Parse("{\"model\":\"sonnet\",\"effort\":\"medium\",\"reason\":\"Debugging a regression\"}");
            Eq(ModelChoice.Sonnet, d.Model);
            Eq(EffortChoice.Medium, d.Effort);
            Eq("Haiku router: Debugging a regression", d.Reason);
            Eq(EffortChoice.Medium, HaikuRouter.Parse("{\"model\":\"opus\"}").Effort, "missing effort = medium");
            Eq(null, HaikuRouter.Parse("{\"model\":\"gpt\"}"), "unknown model");
            Eq(null, HaikuRouter.Parse("not json"));
            Eq(null, HaikuRouter.Parse(""));

            string req = HaikuRouter.BuildRequest(new RouteRequest { Prompt = new string('x', 5000), AttachedFiles = 2, AttachedCodeLines = 40 });
            True(req.StartsWith("Request: 2 file(s) attached. 40 lines of code attached.\n"), req.Substring(0, 80));
            True(req.EndsWith("…(cut)") && req.Length < HaikuRouter.MaxPromptChars + 100, "prompt cut");

            string args = HaikuRouter.TrimmedArguments();
            foreach (var flag in new[] { "--system-prompt", "--tools \"\"", "--strict-mcp-config", "--disable-slash-commands", "--setting-sources \"\"", "--no-session-persistence", "--json-schema" })
                True(args.Contains(flag), "has " + flag);
        }

        private static void Test_Router_PlanModeSaysSonnet()
        {
            // Claude Code answers with Sonnet in plan mode when Haiku is asked for (2.1.316-2.1.322)
            var router = new ModelRouter();
            var simple = new RouteRequest { Prompt = "rename x to count" };
            Eq(ModelChoice.Haiku, router.Route(simple).Model, "outside plan mode: Haiku");
            simple.PlanMode = true;
            var d = router.Route(simple);
            Eq(ModelChoice.Sonnet, d.Model, "plan mode: Sonnet, what answers");
            True(d.Reason.EndsWith(ModelRouter.PlanModeNote), d.Reason);

            // The Haiku router's answer goes through the same step
            var fromHaiku = router.ApplyCacheStickiness(new RouteDecision { Model = ModelChoice.Haiku, Effort = EffortChoice.Low, Reason = "Haiku router: rename" }, new RouteRequest { PlanMode = true }, clearlyHard: false);
            Eq(ModelChoice.Sonnet, fromHaiku.Model, "Haiku router in plan mode");
            Eq(ModelChoice.Opus, router.ApplyCacheStickiness(new RouteDecision { Model = ModelChoice.Opus, Reason = "r" }, new RouteRequest { PlanMode = true }, false).Model, "other models unchanged");

            // In a big Sonnet conversation it stays Sonnet without a second note
            var big = router.Route(new RouteRequest { Prompt = "rename x to count", PlanMode = true, CurrentModel = ModelChoice.Sonnet, ContextTokens = 80_000 });
            Eq(ModelChoice.Sonnet, big.Model, "big conversation");
            True(!big.Reason.Contains("kept"), "no cache note needed: " + big.Reason);

            // A manual pick
            Eq(ModelChoice.Sonnet, ModelRouter.AnsweredBy(ModelChoice.Haiku, planMode: true), "Haiku by hand in plan mode");
            Eq(null, ModelRouter.AnsweredBy(ModelChoice.Haiku, planMode: false), "not in plan mode");
            Eq(null, ModelRouter.AnsweredBy(ModelChoice.Opus, planMode: true), "Opus");
        }

        private static void Test_Router_StickinessAppliesToHaikuAnswers()
        {
            var router = new ModelRouter { StickyContextTokens = 30000 };
            var big = new RouteRequest { Prompt = "x", CurrentModel = ModelChoice.Sonnet, ContextTokens = 50000 };
            var down = router.ApplyCacheStickiness(new RouteDecision { Model = ModelChoice.Haiku, Effort = EffortChoice.Low, Reason = "Haiku router: rename" }, big, clearlyHard: false);
            Eq(ModelChoice.Sonnet, down.Model, "no downgrade in a big conversation");
            True(down.Reason.Contains("kept sonnet to reuse the prompt cache"), down.Reason);
            Eq(ModelChoice.Opus, router.ApplyCacheStickiness(new RouteDecision { Model = ModelChoice.Opus, Effort = EffortChoice.High, Reason = "r" }, big, clearlyHard: true).Model, "clearly hard may upgrade");
            Eq(ModelChoice.Haiku, router.ApplyCacheStickiness(new RouteDecision { Model = ModelChoice.Haiku, Reason = "r" }, new RouteRequest { CurrentModel = ModelChoice.Sonnet, ContextTokens = 1000 }, false).Model, "small conversation: free to switch");
        }

        private static void Test_SavingsReport_HaikuRouter()
        {
            var records = new[]
            {
                new UsageRecord { Routed = true, Router = "haiku", RouteModel = "sonnet", LocalModel = "sonnet", ModelUsed = "claude-sonnet-5-5", CostUsd = 0.02, RouterCostUsd = 0.0001, RouterMs = 1500 },
                new UsageRecord { Routed = true, Router = "haiku", RouteModel = "haiku", LocalModel = "sonnet", ModelUsed = "claude-haiku-5-5", CostUsd = 0.002, RouterCostUsd = 0.0001, RouterMs = 2500 },
                new UsageRecord { Routed = true, RouteModel = "sonnet", ModelUsed = "claude-sonnet-5-5", CostUsd = 0.01, RouterCostUsd = 0.0001, RouterMs = 4000 }, // timed out: local rules
            };
            var rep = SavingsReport.Build(records, new PriceTable());
            Eq(2, rep.HaikuRouted);
            Eq(1, rep.HaikuAgreed);
            Eq(0.0003, Math.Round(rep.RouterCostUsd, 6));
            Eq(0.0323, Math.Round(rep.ActualCostUsd, 6), "router cost counted in the actual cost");
            Eq(2666L, rep.RouterAverageMs);
            string text = rep.ToText();
            True(text.Contains("Haiku router:               2 prompts, same model as the local rules 50% of the time"), text);
            Eq(0, rep.HaikuCheaper, "the second went to Haiku instead of Sonnet, but logged no tokens to price");
            Eq(0, rep.HaikuDearer, "none dearer");

            // With tokens: Sonnet instead of Haiku costs more, Haiku instead of Opus less
            var priced = new[]
            {
                new UsageRecord { Router = "haiku", RouteModel = "sonnet", LocalModel = "haiku", ModelUsed = "claude-sonnet-5-5", InputTokens = 10_000, OutputTokens = 1_000 },
                new UsageRecord { Router = "haiku", RouteModel = "haiku", LocalModel = "opus", ModelUsed = "claude-haiku-5-5", InputTokens = 10_000, OutputTokens = 1_000 },
                new UsageRecord { Router = "haiku", RouteModel = "haiku", LocalModel = "opus", ModelUsed = null, InputTokens = 10_000, OutputTokens = 1_000 }, // no model reported: the routed one
            };
            var p = SavingsReport.Build(priced, new PriceTable());
            Eq(1, p.HaikuDearer, "dearer");
            // Sonnet 10k×2 + 1k×10 = $0.030; Haiku 10k×0.1 + 1k×0.5 = $0.0015; Opus 10k×4 + 1k×20 = $0.060
            True(Math.Abs(p.HaikuDearerExtraUsd - 0.0285) < 1e-9, "Sonnet less Haiku: " + p.HaikuDearerExtraUsd);
            Eq(2, p.HaikuCheaper, "cheaper");
            True(Math.Abs(p.HaikuCheaperSavedUsd - 2 * 0.0585) < 1e-9, "Opus less Haiku, twice: " + p.HaikuCheaperSavedUsd);
            True(p.ToText().Contains("where they differed: 2 to a cheaper model (≈ $0.12 less), 1 to a dearer one (≈ $0.03 more), at the same tokens"), p.ToText());
            Eq(null, SavingsReport.Build(records.Take(1), new PriceTable()).HaikuDifferedLine(), "always agreed");

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(new UsageRecord { Routed = true });
            True(!json.Contains("router") && !json.Contains("localModel"), "local-only records stay as before: " + json);
        }

        // ---------- Screen reader text ----------
        private static void Test_ScreenReaderText_Plain()
        {
            Eq("Fix Use the real name", ScreenReaderText.Plain("# Fix\nUse the **real** name"));
            Eq("Steps: Run it (code block) Then see the docs and Program.cs.",
               ScreenReaderText.Plain("Steps:\n\n1. Run it\n```csharp\nvar x = 1;\n```\n- Then see [the docs](https://x.y) and `Program.cs`."));
            Eq("a b 1 2", ScreenReaderText.Plain("| a | b |\n|---|---|\n| 1 | 2 |"));
            Eq("quoted", ScreenReaderText.Plain("> quoted"));
            Eq("unclosed (code block)", ScreenReaderText.Plain("unclosed\n```\ncode never closed"));
            Eq("", ScreenReaderText.Plain(null));
        }

        private static void Test_ScreenReaderText_AnswerFinished()
        {
            Eq("Claude finished. Done.", ScreenReaderText.AnswerFinished("**Done.**"));
            Eq("Claude finished.", ScreenReaderText.AnswerFinished(""));
            string longAnswer = string.Join(" ", Enumerable.Repeat("This sentence is part of a long answer.", 20));
            string said = ScreenReaderText.AnswerFinished(longAnswer);
            True(said.EndsWith(ScreenReaderText.ReadAllHint), said);
            True(said.Length < ScreenReaderText.AnswerChars + 100, "short: " + said.Length);
            True(said.Contains("long answer. " + ScreenReaderText.ReadAllHint), "cut after a sentence: " + said);
            Eq("one two…", ScreenReaderText.Shorten("one two three", 9));
        }

        // ---------- The CLI's dialogs ----------
        private static JObject DialogFixture(string requestId) =>
            File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.318-dialogs.jsonl"))
                .Where(l => l.Trim().Length > 0).Select(JObject.Parse)
                .Single(j => (string)j.SelectToken("response.request_id") == requestId).SelectToken("response.response") as JObject;

        private static void Test_Dialogs_StatusAndOutputStyles()
        {
            var rows = StatusRow.Parse(DialogFixture("get_status"));
            Eq("2.1.318", rows.Single(r => r.Label == "Version").Value);
            Eq("Session", rows.First().Section);
            Eq("Environment", rows.Single(r => r.Label == "Model").Section);

            var styles = OutputStyles.Parse(DialogFixture("initialize"));
            Eq("Terse", styles.Current);
            Eq("default,Proactive,Concise,Explanatory,Learning,Terse", string.Join(",", styles.Available));
            Eq("Custom style", OutputStyles.Describe("Terse"));
            Eq("default", OutputStyles.Parse(new JObject()).Current, "no style = default");
            Eq(null, OutputStyles.Parse(null));
        }

        private static void Test_Dialogs_RulesAndHooks()
        {
            var rules = PermissionRuleRow.Parse(DialogFixture("list_permission_rules"));
            Eq(2, rules.Count);
            var ask = rules.Single(r => r.Behavior == "ask");
            Eq("Bash(git push:*)", ask.Rule);
            Eq("Any Bash command starting with git push", ask.Description);
            Eq("projectSettings", ask.Source);
            Eq(null, rules.Single(r => r.Behavior == "deny").Description);
            True(PermissionRuleRow.Order("deny") < PermissionRuleRow.Order("ask") && PermissionRuleRow.Order("ask") < PermissionRuleRow.Order("allow"), "deny first");

            var hooks = HookRow.Parse(DialogFixture("get_hooks_listing"));
            Eq(1, hooks.Count);
            Eq("PreToolUse", hooks[0].Event);
            Eq("Bash", hooks[0].Matcher);
            Eq("echo hi", hooks[0].Text);
            Eq("Project settings (.claude/settings.json)", hooks[0].SourceLabel);

            Eq(Path.Combine(@"C:\p", ".claude", "settings.local.json"), SettingsSources.PathFor("localSettings", @"C:\p"));
            Eq(Path.Combine(@"C:\home", ".claude", "settings.json"), SettingsSources.PathFor("userSettings", @"C:\p", @"C:\home"));
            Eq(null, SettingsSources.PathFor("policySettings", @"C:\p"));
            Eq("Local project settings", SettingsSources.Label("localSettings"));
        }

        private static void Test_Dialogs_MemoryAndUsage()
        {
            var mem = MemoryEntry.Parse(DialogFixture("get_memory_dialog"));
            Eq("on", mem.AutoMemoryStatus);
            var user = mem.Entries.Single(e => e.Kind == "user");
            True(!user.Exists && user.Path.EndsWith(@".claude\CLAUDE.md") && user.Label == "User instructions", "user file");
            Eq(3, mem.Entries.Count(e => e.Kind == "memory"));
            True(mem.Entries.Single(e => e.Label == "MEMORY.md").Modified.HasValue, "modified time");
            True(mem.Entries.Single(e => e.Kind == "auto").IsFolder, "auto-memory folder");

            var usage = UsageInfo.Parse(DialogFixture("get_usage"));
            Eq("Pro", usage.PlanName);
            Eq(2, usage.Limits.Count);
            var five = usage.Limits.Single(l => l.Kind == "session");
            Eq(33, five.Percent);
            Eq("5-hour limit", five.Name);
            Eq(new DateTime(2026, 11, 4, 16, 49, 59, DateTimeKind.Utc), five.ResetsAt.Value.AddTicks(-(five.ResetsAt.Value.Ticks % TimeSpan.TicksPerSecond)));
            Eq("Weekly limit (all models)", usage.Limits.Single(l => l.Kind == "weekly_all").Name);
            Eq("Weekly limit (opus)", new UsageLimit { Kind = "weekly_opus" }.Name);
            True(!usage.ExtraUsageEnabled, "extra usage off");
            Eq(0.0, usage.SessionCostUsd);
        }

        // ---------- Review each change of an edit ----------
        private static void Test_EditReview_Edit_PartialApply()
        {
            string file = "header\nalpha\nbeta\ngamma\ndelta\n";
            var input = JObject.Parse("{\"file_path\":\"C:/x/notes.txt\",\"old_string\":\"beta\\ngamma\\ndelta\",\"new_string\":\"BETA\\ngamma\\nDELTA\",\"replace_all\":false}");
            var r = EditReview.Create("Edit", input, file);
            Eq(2, r.Hunks.Count);
            Eq("line 3 (−1 +1)", r.Hunks[0].Label, "file line of old_string + offset");
            Eq("line 5 (−1 +1)", r.Hunks[1].Label);
            Eq("BETA\ngamma\nDELTA", (string)r.BuildInput()["new_string"], "all accepted = unchanged");
            Eq(null, r.NoteForClaude());

            r.Hunks[1].Accepted = false;
            var built = r.BuildInput();
            Eq("BETA\ngamma\ndelta", (string)built["new_string"]);
            Eq("beta\ngamma\ndelta", (string)built["old_string"]);
            Eq(false, (bool)built["replace_all"], "other fields kept");
            string note = r.NoteForClaude();
            True(note.StartsWith("The user reviewed this edit and applied only 1 of 2 changes."), note);
            True(note.Contains("- line 5 (−1 +1): kept\n    delta\n  instead of\n    DELTA\n"), note);
            Eq("BETA\ngamma\nDELTA", (string)input["new_string"], "original input not modified");
        }

        private static void Test_EditReview_Write_InsertDeleteAndCrlf()
        {
            string file = "a\r\nb\r\nc\r\nd\r\n";
            var input = JObject.Parse("{\"file_path\":\"f.txt\",\"content\":\"a\\nNEW\\nb\\nc\\n\"}"); // inserts after a, deletes d
            var r = EditReview.Create("Write", input, file);
            Eq(2, r.Hunks.Count, "line endings alone are not changes");
            Eq("after line 1 (+1)", r.Hunks[0].Label);
            Eq("line 4 (−1)", r.Hunks[1].Label);
            Eq("a\nNEW\nb\nc\n", (string)r.BuildInput()["content"], "all accepted = exactly Claude's content");
            r.Hunks[1].Accepted = false; // keep d, in the proposal's line endings
            Eq("a\nNEW\nb\nc\nd\n", (string)r.BuildInput()["content"]);
            r.Hunks[1].Accepted = true; r.Hunks[0].Accepted = false;
            Eq("a\nb\nc\n", (string)r.BuildInput()["content"]);

            var crlf = EditReview.Create("Write", JObject.Parse("{\"content\":\"A\\r\\nb\\r\\nC\\r\\n\"}"), "a\nb\nc\n");
            crlf.Hunks[1].Accepted = false;
            Eq("A\r\nb\r\nc\r\n", (string)crlf.BuildInput()["content"], "kept lines take CRLF from the proposal");
            True(r.NoteForClaude().Contains("kept\n    (nothing)\n  instead of\n    NEW\n"), r.NoteForClaude());

            Eq(null, EditReview.Create("Write", JObject.Parse("{\"content\":\"same\"}"), "same"), "no change, no review");
            Eq(null, EditReview.Create("Bash", JObject.Parse("{\"command\":\"ls\"}"), null), "not a file tool");
            Eq(1, EditReview.Create("Write", JObject.Parse("{\"content\":\"x\\ny\"}"), null).Hunks.Count, "new file = one change");
        }

        private static void Test_EditReview_ProposedFile()
        {
            var input = JObject.Parse("{\"old_string\":\"b\",\"new_string\":\"B\"}");
            Eq("aBcb", EditReview.ProposedFile("Edit", input, "abcb"));
            input["replace_all"] = true;
            Eq("aBcB", EditReview.ProposedFile("Edit", input, "abcb"));
            Eq(null, EditReview.ProposedFile("Edit", input, "xyz"), "old_string not found");
        }

        private static void Test_Hooks_InitializeAndCallback()
        {
            var init = ClaudeSession.BuildInitialize();
            Eq("initialize", (string)init.SelectToken("request.subtype"));
            Eq("Edit|Write", (string)init.SelectToken("request.hooks.PostToolUse[0].matcher"));
            Eq(ClaudeSession.EditNotesCallbackId, (string)init.SelectToken("request.hooks.PostToolUse[0].hookCallbackIds[0]"));
            Eq("{}", ClaudeSession.HookResponse(null).ToString(Newtonsoft.Json.Formatting.None));
            Eq("note", (string)ClaudeSession.HookResponse("note").SelectToken("hookSpecificOutput.additionalContext"));
            Eq("PostToolUse", (string)ClaudeSession.HookResponse("note").SelectToken("hookSpecificOutput.hookEventName"));
            True(!ClaudeArgsBuilder.Build(new ClaudeLaunchOptions { EditNotesHook = true }).Contains("hook"), "hooks are not a command-line flag");

            var evs = new ClaudeStreamParser().Parse("{\"type\":\"control_request\",\"request_id\":\"h1\",\"request\":{\"subtype\":\"hook_callback\",\"callback_id\":\"edit_notes\",\"tool_use_id\":\"toolu_9\",\"input\":{\"hook_event_name\":\"PostToolUse\"}}}");
            Eq(ClaudeEventKind.HookCallback, evs.Single().Kind);
            Eq("h1", evs.Single().RequestId);
            Eq("toolu_9", (string)evs.Single().Payload["tool_use_id"]);
        }

        // ---------- Subagents and background tasks ----------
        private static List<JObject> AgentFixture() =>
            File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.318-agents.jsonl"))
                .Where(l => l.Trim().Length > 0).Select(JObject.Parse).ToList();

        /// <summary>A fork subagent from Claude Code 2.1.319 (`claude -p`, sanitized): runs in the background.</summary>
        private static void Test_AgentTasks_Fork_RealCli_2_1_319()
        {
            var parser = new ClaudeStreamParser();
            var list = new AgentTaskList();
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-fork.jsonl"))
                .SelectMany(l => parser.Parse(l)).ToList();
            foreach (var ev in events.Where(e => e.Kind == ClaudeEventKind.Task)) list.Apply(ev.Payload);

            var call = events.Single(e => e.Kind == ClaudeEventKind.ToolUse);
            Eq(("Fork", "Count to three"), ToolCallView.Title(call.ToolName, call.ToolInput), "tool row");
            var result = events.Single(e => e.Kind == ClaudeEventKind.ToolResult);
            Eq("Running in the background", ToolCallView.Summarize(call.ToolName, result.ResultText, result.IsError).Lines.Single(), "no internal metadata on the ⎿ line");

            var fork = list.Find("a3930c38f13bbef71");
            True(fork != null, "the fork is in the agents panel");
            Eq(ToolCallView.ForkType, fork.SubagentType, "subagent_type");
            True(fork.IsAgent && fork.IsBackgrounded, "an agent, in the background");
            Eq(AgentTaskStatus.Completed, fork.Status, "finished");
            Eq("fork of this chat", ToolCallView.AgentKind(fork.SubagentType), "panel tag");

            // Its cost on its row: its requests (parent_tool_use_id = its Agent call), priced as they come, in stream order
            var meter = new TokenMeter();
            var priced = new AgentTaskList();
            foreach (var ev in events)
                if (ev.Kind == ClaudeEventKind.Task) priced.Apply(ev.Payload);
                else if (ev.Kind == ClaudeEventKind.AssistantUsage) priced.ObserveRequest(ev, meter.RequestCostUsd);
            var subReq = events.Where(e => e.Kind == ClaudeEventKind.AssistantUsage && e.IsSubagent).GroupBy(e => e.MessageId).Select(g => g.Last()).ToList();
            double expected = subReq.Sum(e => meter.RequestCostUsd(e.Model, e.Usage.AsRequest()));
            True(expected > 0 && Math.Abs(priced.Find("a3930c38f13bbef71").CostUsd - expected) < 1e-12, "the fork's cost: " + priced.Find("a3930c38f13bbef71").CostUsd);
            True(events.Where(e => e.Kind == ClaudeEventKind.AssistantUsage && !e.IsSubagent).All(e => !priced.ObserveRequest(e, meter.RequestCostUsd)), "the main agent's requests are not an agent's");

            // A request that comes before its task_started still counts; a removed task takes its cost with it
            var early = new AgentTaskList();
            var req = new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, IsSubagent = true, ParentToolUseId = "toolu_x", MessageId = "msg_1", Model = "claude-sonnet-5-5", Usage = new TokenUsage { Input = 1000, Output = 100 } };
            True(!early.ObserveRequest(req, (m, u) => 0.5), "no task yet");
            early.Apply(JObject.Parse("{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"t1\",\"tool_use_id\":\"toolu_x\",\"task_type\":\"local_agent\"}"));
            Eq(0.5, early.Find("t1").CostUsd, "picked up at task_started");
            Eq("sonnet", early.Find("t1").ModelShort, "its model too, from the earlier request");
            Eq("my-model", new AgentTask { Model = "my-model" }.ModelShort, "an unknown family: the id"); Eq(null, new AgentTask().ModelShort);
            True(early.ObserveRequest(req, (m, u) => 0.7), "the same message again");
            Eq(0.7, early.Find("t1").CostUsd, "its last copy counts, once");
            // The line under its Agent row once it finished: task_notification's usage (24,649 tokens, no tools) and its cost
            var forkPriced = priced.Find("a3930c38f13bbef71");
            Eq("claude-haiku-5-5", forkPriced.Model, "the model its requests ran on"); Eq("haiku", forkPriced.ModelShort);
            Eq($"Done · haiku · 24.6k tok · {AgentTask.FormatCost(forkPriced.CostUsd)}", ToolCallView.AgentUsageLine(forkPriced), "under the Agent row");
            Eq(null, ToolCallView.AgentUsageLine(new AgentTask { TaskType = "local_agent" }), "still running");
            Eq(null, ToolCallView.AgentUsageLine(new AgentTask { TaskType = "local_bash", Status = AgentTaskStatus.Completed }), "a shell command");
            Eq("Stopped · 1 tool", ToolCallView.AgentUsageLine(new AgentTask { TaskType = "local_agent", Status = AgentTaskStatus.Stopped, ToolUses = 1 }));

            // Past Options → Warn at agent cost: the row turns amber, and its tooltip says why
            var pricey = new AgentTask { TaskType = "local_agent", CostUsd = 0.62 };
            True(pricey.CostsOver(0.5) && !pricey.CostsOver(0) && !pricey.CostsOver(1), "over $0.50, no limit at 0, under $1");
            Eq("Cost $0.620 so far, past your warning at $0.50: Stop ends it.", pricey.CostWarning(0.5));
            pricey.Status = AgentTaskStatus.Completed;
            Eq("Cost $0.620 so far, past your warning at $0.50.", pricey.CostWarning(0.5), "finished: nothing to stop");
            Eq(null, pricey.CostWarning(1));
            True(!new AgentTask { TaskType = "local_bash", CostUsd = 9 }.CostsOver(0.5), "commands have no cost of their own");

            // Options → Stop agents at cost: a running agent past it is stopped, and the chat says so
            var runaway = new AgentTask { TaskType = "local_agent", Description = "Refactor everything", CostUsd = 1.25 };
            Eq("Stopped “Refactor everything” at $1.25: past your limit of $1.00 per agent (Options → Stop agents at cost). Claude carries on without its result.",
                SessionNotices.AgentAutoStop(runaway, 1.0));
            Eq(null, SessionNotices.AgentAutoStop(runaway, 0), "0: never");
            Eq(null, SessionNotices.AgentAutoStop(runaway, 2.0), "under the limit");
            runaway.Status = AgentTaskStatus.Completed;
            Eq(null, SessionNotices.AgentAutoStop(runaway, 1.0), "finished: nothing to stop");
            True(SessionNotices.AgentAutoStop(new AgentTask { TaskType = "local_agent", CostUsd = 1.25 }, 1.0).StartsWith("Stopped an agent at"), "no description");

            Eq("$0.700", AgentTask.FormatCost(0.7)); Eq("$1.23", AgentTask.FormatCost(1.234)); Eq("<$0.001", AgentTask.FormatCost(0.0002)); Eq(null, AgentTask.FormatCost(0));

            var text = events.Where(e => e.Kind == ClaudeEventKind.AssistantText).Select(e => e.Text).ToList();
            True(!text.Any(t => t.Contains("one two three") && t.StartsWith("Task:")), "the fork's own reply is not shown as Claude's answer");
            True(text.Contains("done"), "Claude's answer is");
        }

        private static void Test_AgentLabels()
        {
            Eq("Agent", ToolCallView.AgentLabel(null));
            Eq("general-purpose", ToolCallView.AgentLabel("general-purpose"));
            Eq("agent", ToolCallView.AgentKind(""));
            Eq("Explore", ToolCallView.AgentKind("Explore"));
            Eq("Agent", ToolCallView.Title("Agent", new JObject { ["description"] = "x" }).Item1, "no type");
        }

        private static void Test_AgentTasks_ReplayCapture()
        {
            var lines = AgentFixture();
            var parser = new ClaudeStreamParser();
            var list = new AgentTaskList();
            var t0 = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
            int i = 0;
            AgentTask A() => list.Find("a94500e2880720a6e");
            AgentTask B() => list.Find("a39987eeb9f3c67fc");
            void Upto(Func<JObject, bool> stopAfter)
            {
                for (; i < lines.Count; i++)
                {
                    foreach (var ev in parser.Parse(lines[i].ToString(Newtonsoft.Json.Formatting.None)).Where(e => e.Kind == ClaudeEventKind.Task))
                        list.Apply(ev.Payload, t0.AddSeconds(i));
                    if (stopAfter(lines[i])) { i++; return; }
                }
            }

            // Both agents started: A in the background, B in the foreground.
            Upto(l => (string)l["subtype"] == "task_started" && (string)l["task_id"] == "a39987eeb9f3c67fc");
            Eq(2, list.RunningCount);
            True(A().IsBackgrounded && !A().CanMoveToBackground, "A is in the background");
            True(!B().IsBackgrounded && B().CanMoveToBackground, "B can be moved");
            Eq("Sleeper B", B().Description);
            Eq("general-purpose", B().SubagentType);
            Eq("toolu_017ZarFmHV4amGtubxdAX7gr", B().ToolUseId);
            Eq("2 agents running", list.Headline());

            // Progress, then B moved to the background (task_updated patch).
            Upto(l => (string)l["subtype"] == "task_updated" && (string)l["task_id"] == "a39987eeb9f3c67fc");
            Eq("Running Sleep for 30 seconds", A().Activity);
            Eq(17940L, A().TotalTokens);
            True(B().IsBackgrounded && !B().CanMoveToBackground, "B moved");

            // A's own background shell command is nested under it.
            Upto(l => (string)l["subtype"] == "task_started" && (string)l["task_id"] == "b6ysjloo6");
            var tree = list.Tree();
            Eq("a94500e2880720a6e", tree[0].Task.TaskId);
            Eq("b6ysjloo6", tree[1].Task.TaskId);
            Eq(1, tree[1].Depth);
            True(!list.Find("b6ysjloo6").IsAgent, "a shell command is not an agent");
            Eq("2 agents running · 1 command running", list.Headline());

            // Stopping A (stop_task) kills it and its command.
            Upto(l => (string)l["subtype"] == "task_notification" && (string)l["task_id"] == "b6ysjloo6");
            Eq(AgentTaskStatus.Stopped, A().Status);
            Eq(AgentTaskStatus.Stopped, list.Find("b6ysjloo6").Status);
            True(A().EndedUtc.HasValue && A().Activity == null, "ended, no activity");
            Eq(null, A().Summary, "summary equal to the description is dropped");

            // B finishes with its answer as the summary.
            Upto(_ => false);
            Eq(AgentTaskStatus.Completed, B().Status);
            Eq("DONE-B", B().Summary);
            Eq(23097L, B().ReportedDurationMs.Value);
            Eq(0, list.RunningCount);
            Eq("4 done", list.Headline());
            True(list.RemoveFinished() && list.IsEmpty, "all finished tasks cleared");
        }

        private static void Test_AgentTasks_RemoveFinishedKeepsRunningFamilies()
        {
            var list = new AgentTaskList();
            list.Apply(JObject.Parse("{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"p\",\"task_type\":\"local_agent\",\"description\":\"Parent\"}"));
            list.Apply(JObject.Parse("{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"c\",\"parent_task_id\":\"p\",\"task_type\":\"local_bash\",\"description\":\"Child\"}"));
            list.Apply(JObject.Parse("{\"type\":\"system\",\"subtype\":\"task_notification\",\"task_id\":\"p\",\"status\":\"completed\",\"summary\":\"ok\"}"));
            list.Apply(JObject.Parse("{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"d\",\"task_type\":\"local_agent\",\"description\":\"Done one\"}"));
            list.Apply(JObject.Parse("{\"type\":\"system\",\"subtype\":\"task_notification\",\"task_id\":\"d\",\"status\":\"failed\"}"));
            Eq(AgentTaskStatus.Failed, list.Find("d").Status);
            list.RemoveFinished();
            Eq("p,c", string.Join(",", list.Tasks.Select(t => t.TaskId)), "finished parent kept while its child runs");

            // A background task we never saw start (e.g. the window reopened) is added from the background list.
            list.Apply(JObject.Parse("{\"type\":\"system\",\"subtype\":\"background_tasks_changed\",\"tasks\":[{\"task_id\":\"z\",\"task_type\":\"local_agent\",\"subagent_type\":\"Explore\",\"description\":\"Late\"}]}"));
            True(list.Find("z").IsBackgrounded && list.Find("z").IsRunning, "added as running in the background");
            True(!list.Apply(JObject.Parse("{\"type\":\"system\",\"subtype\":\"init\"}")), "other system messages are ignored");
        }

        private static void Test_Parser_SubagentToolRowsNested()
        {
            var evs = new ClaudeStreamParser().Parse("{\"type\":\"assistant\",\"parent_tool_use_id\":\"toolu_1\",\"message\":{\"id\":\"m\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t2\",\"name\":\"Bash\",\"input\":{\"command\":\"ls\"}}]}}");
            Eq("↳ Bash: ls", evs.Single(e => e.Kind == ClaudeEventKind.ToolUse).Text);
            var main = new ClaudeStreamParser().Parse("{\"type\":\"assistant\",\"parent_tool_use_id\":null,\"message\":{\"id\":\"m\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t3\",\"name\":\"Bash\",\"input\":{\"command\":\"ls\"}}]}}");
            Eq("Bash: ls", main.Single(e => e.Kind == ClaudeEventKind.ToolUse).Text);
        }

        // ---------- In-process MCP server (Error List tool) ----------
        private static SdkMcpServer TestIdeServer(params BuildError[] rows) =>
            IdeMcpServer.Create(() => System.Threading.Tasks.Task.FromResult<IReadOnlyList<BuildError>>(rows), () => @"C:\src\Shop");

        private static void Test_Args_SdkMcpServer_ConfigAndAllowedTools()
        {
            var o = new ClaudeLaunchOptions();
            o.SdkMcpServers.Add(TestIdeServer());
            string a = ClaudeArgsBuilder.Build(o);
            True(a.Contains("--mcp-config \"{\\\"mcpServers\\\":{\\\"visualstudio\\\":{\\\"type\\\":\\\"sdk\\\",\\\"name\\\":\\\"visualstudio\\\"}}}\""), a);
            True(a.Contains("--allowedTools mcp__visualstudio__get_error_list"), a);
            True(!ClaudeArgsBuilder.Build(new ClaudeLaunchOptions()).Contains("--mcp-config"), "no servers, no flag");
        }

        private static void Test_Parser_McpMessage()
        {
            // Captured from Claude Code 2.1.318.
            var evs = new ClaudeStreamParser().Parse("{\"type\":\"control_request\",\"request_id\":\"r1\",\"request\":{\"subtype\":\"mcp_message\",\"server_name\":\"visualstudio\",\"message\":{\"method\":\"tools/list\",\"jsonrpc\":\"2.0\",\"id\":1}}}");
            Eq(1, evs.Count);
            Eq(ClaudeEventKind.McpMessage, evs[0].Kind);
            Eq("r1", evs[0].RequestId);
            Eq("visualstudio", evs[0].ServerName);
            Eq("tools/list", (string)evs[0].Payload["method"]);
        }

        private static void Test_SdkMcpServer_Protocol()
        {
            var s = TestIdeServer(new BuildError { FilePath = @"C:\src\Shop\Api\Program.cs", Line = 12, Column = 5, Code = "CS0103", Message = "The name 'zork' does not exist" });
            var init = s.HandleAsync(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\"}}")).Result;
            Eq("2025-11-25", (string)init.SelectToken("result.protocolVersion"));
            Eq("visualstudio", (string)init.SelectToken("result.serverInfo.name"));
            Eq(0, (int)init["id"]);

            var note = s.HandleAsync(JObject.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}")).Result;
            True(note["id"] == null && note["result"] != null && note["error"] == null, "notification gets an empty result: " + note);

            var list = s.HandleAsync(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}")).Result;
            Eq("get_error_list", (string)list.SelectToken("result.tools[0].name"));
            Eq(true, (bool)list.SelectToken("result.tools[0].annotations.readOnlyHint"));

            var call = s.HandleAsync(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"get_error_list\",\"arguments\":{}}}")).Result;
            string text = (string)call.SelectToken("result.content[0].text");
            True(text.Contains("Api/Program.cs:12:5 error CS0103: The name 'zork' does not exist"), text);
            True(call.SelectToken("result.isError") == null, "not an error");

            var unknown = s.HandleAsync(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"nope\"}}")).Result;
            Eq(-32602, (int)unknown.SelectToken("error.code"));
            var method = s.HandleAsync(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"resources/list\"}")).Result;
            Eq(-32601, (int)method.SelectToken("error.code"));

            var failing = new SdkMcpServer("x").Add(new McpTool { Name = "boom", Handler = _ => throw new InvalidOperationException("no IDE") });
            var r = failing.HandleAsync(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"boom\"}}")).Result;
            Eq(true, (bool)r.SelectToken("result.isError"));
            True(((string)r.SelectToken("result.content[0].text")).Contains("no IDE"), r.ToString());
        }

        private static void Test_ErrorListTool_Format()
        {
            var rows = new[]
            {
                new BuildError { FilePath = @"C:\src\Shop\Web\Site.cs", Line = 3, Code = "CS0168", Message = "Unused variable", Severity = ErrorSeverity.Warning, Project = "Web" },
                new BuildError { FilePath = @"C:\src\Shop\Api\Program.cs", Line = 12, Code = "CS0103", Message = "Missing name", Project = "Api" },
                new BuildError { FilePath = @"C:\src\Shop\Api\Program.cs", Line = 12, Code = "CS0103", Message = "Missing name", Project = "Api" }, // build + IntelliSense
                new BuildError { FilePath = @"C:\src\Shop\Api\Util.cs", Line = 1, Message = "Info only", Severity = ErrorSeverity.Message },
            };
            string errorsOnly = ErrorListTool.Format(rows, @"C:\src\Shop", null);
            Eq("Error List: 1 error, 1 warning.\nApi/Program.cs:12 error CS0103: Missing name (Api)", errorsOnly);

            string withWarnings = ErrorListTool.Format(rows, @"C:\src\Shop", JObject.Parse("{\"severity\":\"warning\"}"));
            True(withWarnings.IndexOf("error CS0103") < withWarnings.IndexOf("warning CS0168"), "errors first: " + withWarnings);
            True(!withWarnings.Contains("Info only"), "messages need severity all");
            True(ErrorListTool.Format(rows, @"C:\src\Shop", JObject.Parse("{\"severity\":\"all\"}")).Contains("message Info only"), "all includes messages");

            string filtered = ErrorListTool.Format(rows, @"C:\src\Shop", JObject.Parse("{\"severity\":\"all\",\"file\":\"web\\\\site\"}"));
            True(filtered.Contains("Matching \"web/site\": 1.") && filtered.Contains("Site.cs:3 warning"), filtered);

            string onlyWarnings = ErrorListTool.Format(rows.Where(e => e.Severity == ErrorSeverity.Warning), @"C:\src\Shop", null);
            Eq("Error List: 0 errors, 1 warning. Pass severity \"warning\" to see the warnings.", onlyWarnings);

            var many = Enumerable.Range(1, 80).Select(i => new BuildError { FilePath = @"C:\src\Shop\A.cs", Line = i, Message = "e" + i }).ToArray();
            string capped = ErrorListTool.Format(many, @"C:\src\Shop", JObject.Parse("{\"limit\":5}"));
            True(capped.StartsWith("Error List: 80 errors, 0 warnings. Showing the first 5.\n"), capped);
            Eq(6, capped.Split('\n').Length, "count line + 5 rows");
        }

        private static void Test_DescribeTool_ErrorList()
        {
            Eq("Error List", ClaudeStreamParser.DescribeTool("mcp__visualstudio__get_error_list", new JObject()));
            Eq("Error List: Program.cs", ClaudeStreamParser.DescribeTool("mcp__visualstudio__get_error_list", JObject.Parse("{\"file\":\"Program.cs\"}")));
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
                Eq(1334L, a.TokensAvoided, "4,000 chars at 3 per token");
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
        private static void Test_Savings_Thinking()
        {
            var prices = new PriceTable(); // Sonnet output $10, Haiku output $0.5 per MTok
            var recs = new[]
            {
                new UsageRecord { ModelUsed = "claude-sonnet-5-5", OutputTokens = 400_000, ThinkingTokens = 300_000, CostUsd = 4 },
                new UsageRecord { ModelUsed = "claude-haiku-5-5", OutputTokens = 100_000, ThinkingTokens = 0, CostUsd = 0.1 },
            };
            var r = SavingsReport.Build(recs, prices);
            Eq(300_000L, r.ThinkingTokens);
            True(Math.Abs(r.ThinkingCostUsd - 3.0) < 1e-9, "priced at each turn's output price " + r.ThinkingCostUsd);
            True(r.ToText().Contains("Thinking:                   300k tokens ≈ $3.00  (60% of output tokens;") || r.ToText().Contains("Thinking:                   300.0k tokens ≈ $3.00  (60% of output tokens;"), r.ToText());
            True(!SavingsReport.Build(new[] { new UsageRecord { ModelUsed = "claude-haiku-5-5", OutputTokens = 10 } }, prices).ToText().Contains("Thinking:"), "no thinking logged: no line");

            // Older log lines have no "thinking"; it is written only when there is some.
            var old = Newtonsoft.Json.JsonConvert.DeserializeObject<UsageRecord>("{\"model\":\"m\",\"out\":5}");
            Eq(0L, old.ThinkingTokens, "old line");
            True(!Newtonsoft.Json.JsonConvert.SerializeObject(new UsageRecord()).Contains("\"thinking\""), "0 is left out");
        }

        private static void Test_Savings_PromptCache()
        {
            var prices = new PriceTable(); // Sonnet: input 2, cache write 2.5, cache read 0.1; Haiku: 0.1, 0.125, 0.01
            var recs = new[]
            {
                new UsageRecord { ModelUsed = "claude-sonnet-5-5", CacheReadTokens = 1_000_000, CacheWriteTokens = 100_000, CacheWrite1hTokens = 100_000 }, // 1.90 - 0.20
                new UsageRecord { ModelUsed = "claude-sonnet-5-5", CacheWriteTokens = 200_000 },                                                         // older line: 5-minute, -0.10
                new UsageRecord { ModelUsed = "claude-haiku-5-5", CacheReadTokens = 1_000_000 },                                                         // 0.09
            };
            var r = SavingsReport.Build(recs, prices);
            True(Math.Abs(r.CacheSavedUsd - 1.69) < 1e-9, "1.70 - 0.10 + 0.09 = " + r.CacheSavedUsd);
            Eq(2_000_000L, r.CacheReadTokens);
            True(r.ToText().Contains("Saved by the prompt cache:  $1.69  (2.0M tokens read from the cache instead of sent in full)"), r.ToText());

            var line = Newtonsoft.Json.JsonConvert.SerializeObject(new UsageRecord { CacheWriteTokens = 5, CacheWrite1hTokens = 5 });
            True(line.Contains("\"cacheWrite1h\":5"), line);
            True(!Newtonsoft.Json.JsonConvert.SerializeObject(new UsageRecord()).Contains("cacheWrite1h"), "0 left out");
        }

        private static void Test_Savings_Costliest()
        {
            var t = new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);
            var recs = Enumerable.Range(1, 7).Select(i => new UsageRecord
            {
                Timestamp = t.AddMinutes(i), ModelUsed = i == 7 ? "claude-opus-5-5" : "claude-sonnet-5-5", CostUsd = i * 0.1, InputTokens = i * 1000,
                PromptPreview = i == 7 ? "refactor the whole\nbilling module across all projects and update every test" : null,
                RouteReason = i == 6 ? "long prompt" : null,
            }).ToList();
            var r = SavingsReport.Build(recs, new PriceTable());
            Eq("0.7|0.6|0.5|0.4|0.3", string.Join("|", r.Costliest.Select(x => x.CostUsd.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))), "top five, dearest first");
            string text = r.ToText();
            True(text.Contains("Costliest turns:"), text);
            True(text.Contains("opus") && text.Contains("“refactor the whole billing module across all projects and u…”"), "prompt on one line, cut: " + text);
            True(text.Contains("(long prompt)"), "route reason when no prompt was logged");
            True(!SavingsReport.Build(recs.Take(1), new PriceTable()).ToText().Contains("Costliest turns:"), "one turn: no list");
        }

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
            Eq(1_333_334L, r.TokensAvoided, "tokens avoided (3 chars per token)");
            True(Math.Abs(r.ReferencesSavedUsd - 1_333_334 * 2.0 / 1e6) < 1e-9, "refs priced at sonnet input " + r.ReferencesSavedUsd);
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

            // Fable 5.2 (2.1.321): its own family, not priced as Sonnet
            var prices = new PriceTable();
            Eq("fable", PriceTable.Family("claude-fable-5-2"), "fable family");
            Eq("fable", PriceTable.Family("fable"), "fable alias");
            Eq(40.0, prices.For("claude-fable-5-2").Output, "fable output price");
            Eq(8.0, PriceTable.FromStrings(null, null, null).Fable.Input, "fable default");
            Eq(9.0, PriceTable.FromStrings(null, null, null, "9, 45, 11.25, 0.2").Fable.Input, "fable from options");
            var u = new TokenUsage { Input = 1_000, Output = 1_000, CacheRead = 100_000 };
            Eq(0.068, Math.Round(prices.For("claude-fable-5-2").Cost(u), 6), "fable turn: 1k in $0.008 + 1k out $0.04 + 100k read $0.02");
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
            Eq(null, diffs[0].CostChangeUsd, "no prices: not priced");
            Eq("", diffs[0].CostChangeText(), "no text");

            // Priced: Sonnet 10k in + 1k out = $0.030, the same on Haiku $0.0015
            var priced = new[]
            {
                new UsageRecord { Routed = true, RouteModel = "sonnet", ModelUsed = "claude-sonnet-5-5", PromptPreview = "fix the typo in this comment", InputTokens = 10_000, OutputTokens = 1_000 },
                new UsageRecord { Routed = true, RouteModel = "sonnet", PromptPreview = "fix the typo in this comment", InputTokens = 10_000, OutputTokens = 1_000 }, // no model: the routed one
            };
            var p = RouterReplay.Compare(priced, R, new PriceTable(), out _);
            Eq(2, p.Count, "both now Haiku");
            True(Math.Abs(p[0].CostChangeUsd.Value + 0.0285) < 1e-9, "cheaper: " + p[0].CostChangeUsd);
            Eq(" (≈ −$0.029)", p[0].CostChangeText());
            Eq("At the logged tokens: 2 cheaper (≈ $0.06 less), net ≈ $0.06 less.", RouterReplay.CostSummary(p));
            var mixed = new List<ReplayDiff> { new ReplayDiff { CostChangeUsd = -0.40 }, new ReplayDiff { CostChangeUsd = 0.10 }, new ReplayDiff { CostChangeUsd = null } };
            Eq("At the logged tokens: 1 cheaper (≈ $0.40 less), 1 dearer (≈ $0.10 more), net ≈ $0.30 less.", RouterReplay.CostSummary(mixed));
            Eq(" (≈ +$0.100)", mixed[1].CostChangeText());
            Eq(null, RouterReplay.CostSummary(diffs), "none priced");
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

        private static void Test_SlashCommands_LeadingSpaceAndInvisible()
        {
            // Claude Code 2.1.319 runs a command typed after a leading space, tab or line break; so do we, and the
            // list, the hint and sending agree. Invisible characters are never sent, so they don't hide a command.
            foreach (var t in new[] { "/compact", "  /compact", "\t/compact", "\n/compact", "​/compact", "﻿ /compact" })
            {
                True(SlashCommands.IsCommand(t), "command: " + Uri.EscapeDataString(t));
                Eq("compact", SlashCommands.TypedName(t), "typed name: " + Uri.EscapeDataString(t));
            }
            var cmds = new[] { new SlashCommand { Name = "compact", Source = "built-in" }, new SlashCommand { Name = "context", Source = "built-in" } };
            Eq("compact", SlashCommands.Match(cmds, "​ /comp").FirstOrDefault()?.Name, "list after invisible + space");
            foreach (var t in new[] { "compact /now", "a/b", "", null, "  ", "​" })
                True(!SlashCommands.IsCommand(t), "not a command: " + (t == null ? "null" : Uri.EscapeDataString(t)));
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
                Eq("/git:pr", string.Join(",", ClaudeVS.Core.Chat.SlashCommands.Match(all, "/pr")), "the part after the folder");
                Eq("/review", string.Join(",", ClaudeVS.Core.Chat.SlashCommands.Match(all, "/review now")), "arguments after the name");
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

        /// <summary>A turn with extended thinking from Claude Code 2.1.318 (stream_event and result lines, sanitized).</summary>
        private static void Test_Parser_Activity_RealCli_2_1_318()
        {
            var parser = new ClaudeStreamParser();
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.318-thinking.jsonl"))
                .SelectMany(l => parser.Parse(l)).ToList();
            var activity = events.Where(e => e.Kind == ClaudeEventKind.Activity).ToList();

            Eq(3, activity.Count, "thinking start, one estimate (the null one is skipped), writing start");
            Eq(ClaudeActivity.Thinking, activity[0].Activity, "thinking block first");
            Eq(null, activity[0].EstimatedTokens, "no estimate on the block start");
            Eq(50, activity[1].EstimatedTokens, "thinking_delta estimate");
            Eq(ClaudeActivity.Writing, activity[2].Activity, "then the text block");
            int writing = events.IndexOf(activity[2]);
            True(events.FindIndex(e => e.Kind == ClaudeEventKind.TextDelta) > writing, "writing comes before the first text delta");
            Eq(1, events.Count(e => e.Kind == ClaudeEventKind.TurnCompleted), "turn completes");
        }

        /// <summary>A real Bash permission request from Claude Code 2.1.318 (sanitized) and what the card shows.</summary>
        private static void Test_PermissionPrompt_RealCli_Bash()
        {
            var line = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.318-permission-bash.jsonl"));
            var ev = new ClaudeStreamParser().Parse(line).Single();
            Eq(ClaudeEventKind.PermissionRequest, ev.Kind, "permission request");
            var p = PermissionPrompt.From(ev.ToolName, ev.ToolInput, ev.Payload);
            Eq("Run this command?", p.Title, "title");
            Eq("echo probe-123 > out.txt", p.Detail, "the command");
            True(p.DetailIsCode, "shown as code");
            Eq("Write probe-123 into out.txt", p.Description, "Claude's description");
            Eq("Bash(echo probe-123 *)", string.Join(",", p.AlwaysAllowRules), "only the addRules suggestion, as a settings rule");
            Eq(1, p.AlwaysAllow.Count, "addDirectories is not sent back");
            True(p.AlwaysAllowScope.Contains("settings.local.json"), "scope: " + p.AlwaysAllowScope);
        }

        private static void Test_PermissionPrompt_OtherTools()
        {
            var edit = PermissionPrompt.From("Edit", JObject.Parse("{\"file_path\":\"C:\\\\work\\\\a.cs\",\"old_string\":\"x\",\"new_string\":\"y\"}"), null);
            Eq("Edit this file?", edit.Title, "edit title");
            Eq("C:\\work\\a.cs", edit.Detail, "edit path");
            True(!edit.DetailIsCode && edit.AlwaysAllow == null && edit.Description == null, "no code box, no suggestions, no description");
            var mcp = PermissionPrompt.From("mcp__github__create_issue", new JObject(), null);
            Eq("Allow github · create_issue?", mcp.Title, "MCP tool title");
            var same = PermissionPrompt.From("Bash", JObject.Parse("{\"command\":\"ls\",\"description\":\"ls\"}"), null);
            Eq(null, same.Description, "a description equal to the command is not repeated");
        }

        private static void Test_InlineDiff_Edit()
        {
            var input = JObject.Parse("{\"file_path\":\"C:\\\\work\\\\a.cs\",\"old_string\":\"a\\nb\\nc\\nd\",\"new_string\":\"a\\nB\\nc\\nd\\ne\"}");
            var d = InlineDiff.FromToolUse("Edit", input);
            Eq("C:\\work\\a.cs", d.FilePath, "path");
            Eq(" a|-b|+B| c| d|+e", string.Join("|", d.Lines), "removed before added, context kept");
            Eq(2, d.Added, "added");
            Eq(1, d.Removed, "removed");
            Eq(null, InlineDiff.FromToolUse("Read", input), "Read changes nothing");
        }

        private static void Test_InlineDiff_LineNumbers()
        {
            var input = JObject.Parse("{\"file_path\":\"a.cs\",\"old_string\":\"b\\nc\",\"new_string\":\"b\\nC\\nc2\"}");
            var d = InlineDiff.FromToolUse("Edit", input, "a\nb\nc\nd\n");
            Eq("2: b|3:-c|3:+C|4:+c2", string.Join("|", d.Lines.Select(l => l.Number + ":" + l)), "numbers from where old_string starts");
            var unknown = InlineDiff.FromToolUse("Edit", input, null);
            True(unknown.Lines.All(l => l.Number == null), "no file content: no numbers");
            var fresh = InlineDiff.FromToolUse("Write", JObject.Parse("{\"file_path\":\"n.txt\",\"content\":\"x\\ny\"}"));
            Eq("1,2", string.Join(",", fresh.Lines.Select(l => l.Number)), "a new file is numbered from 1");
        }

        private static void Test_TurnChanges()
        {
            var files = new Dictionary<string, string> { [@"C:\r\a.cs"] = "x\ny\nz\n", [@"C:\r\new.cs"] = "1\n2\n", [@"C:\r\same.cs"] = "s\n" };
            var t = new TurnChanges();
            t.Record(@"C:\r\a.cs", "x\nz\n");
            t.Record(@"C:\R\A.cs", "ignored: the first copy counts");
            t.Record(@"C:\r\new.cs", "");
            t.Record(@"C:\r\same.cs", "s\r\n");
            t.Record(@"C:\r\gone.cs", "old\n");
            var list = t.Collect(p => files.TryGetValue(p, out var c) ? c : "", p => files.ContainsKey(p));
            Eq(@"C:\r\a.cs|C:\r\new.cs|C:\r\gone.cs", string.Join("|", list.Select(f => f.Path)), "in order, unchanged left out");
            True(list[0].Added == 1 && list[0].Removed == 0 && !list[0].IsNew, "changed file counts");
            True(list[1].IsNew && list[1].Added == 2, "new file");
            True(list[2].IsDeleted, "deleted file");
            Eq(@"C:\r\src\b.cs", TurnChanges.FullPath("src/b.cs", @"C:\r"), "relative to the solution, OS separators");
            Eq(@"C:\x\y.cs", TurnChanges.FullPath("C:/x/y.cs", @"C:\r"), "a heredoc's forward-slash path");
        }

        private static void Test_RouteText()
        {
            Eq("Auto → Sonnet, low effort", RouteText.Note(true, true, ModelChoice.Sonnet, EffortChoice.Low), "both on Auto");
            Eq("Auto → Haiku", RouteText.Note(true, false, ModelChoice.Haiku, EffortChoice.High), "only the model");
            Eq("Auto → extra high effort", RouteText.Note(false, true, ModelChoice.Opus, EffortChoice.XHigh), "only the effort");
            Eq(null, RouteText.Note(false, false, ModelChoice.Opus, EffortChoice.High), "nothing on Auto");
            Eq("Picked by the Haiku router: short question", RouteText.Tip(true, "Haiku router: short question"), "Haiku prefix dropped");
            Eq("Picked by the local router", RouteText.Tip(false, "default"), "no reason");
            Eq("Picked by the local router: mentions refactor", RouteText.Tip(false, "mentions refactor"), "local reason");
        }

        private static void Test_ToolCallView()
        {
            Eq(("Update", "src/a.cs"), ToolCallView.Title("Edit", JObject.Parse("{\"file_path\":\"C:\\\\repo\\\\src\\\\a.cs\"}"), "C:/repo"), "path relative to the solution");
            Eq(("Bash", "cd x && cat > f <<'EOF' …"), ToolCallView.Title("Bash", new JObject { ["command"] = "cd x && cat > f <<'EOF'\nbody\nEOF" }), "first line of a command");
            Eq(("Search", "pattern: \"Foo\""), ToolCallView.Title("Grep", new JObject { ["pattern"] = "Foo" }), "grep");
            Eq(("Bash", "dotnet test"), ToolCallView.Title("Bash", new JObject { ["command"] = "cd \"C:/Users/me/repo\" && dotnet test" }, @"C:\Users\me\repo"), "cd to the working folder dropped");

            Eq("dotnet test", ToolCallView.WithoutCdToBase("cd \"C:/r\" && dotnet test", @"C:\r\"), "quoted, either slash style");
            Eq("ls", ToolCallView.WithoutCdToBase("cd '/c/r' && ls", "C:/r"), "git bash path");
            Eq("ls", ToolCallView.WithoutCdToBase("cd C:/r; ls", "c:/R"), "unquoted, semicolon, case");
            Eq("Get-ChildItem", ToolCallView.WithoutCdToBase("Set-Location \"C:\\r\"; Get-ChildItem", @"C:\r"), "PowerShell");
            Eq("cd \"C:/r/src\" && ls", ToolCallView.WithoutCdToBase("cd \"C:/r/src\" && ls", "C:/r"), "another folder stays");
            Eq("cd \"C:/r\"", ToolCallView.WithoutCdToBase("cd \"C:/r\"", "C:/r"), "a bare cd stays");
            Eq("ls && cd \"C:/r\" && ls", ToolCallView.WithoutCdToBase("ls && cd \"C:/r\" && ls", "C:/r"), "only at the start");
            Eq("cd \"C:/r\" && ls", ToolCallView.WithoutCdToBase("cd \"C:/r\" && ls", null), "no working folder");

            Eq("Read 2 files, ran 3 read-only commands", ToolCallView.LookSummary(2, 0, false, 3), "with read-only commands");
            Eq("Running 1 read-only command…", ToolCallView.LookSummary(0, 0, true, 1), "a command running");

            // From a real session: Claude reading with the shell.
            True(ToolCallView.IsReadOnlyCommand("git ls-files | head -50 && cat README.md | head -80"), "ls-files, cat, head");
            True(ToolCallView.IsReadOnlyCommand("sed -n 80,140p README.md; git ls-files | tail -30; cat src/LinqSamples/Samples/BatchingSamples.cs"), "sed -n");
            True(ToolCallView.IsReadOnlyCommand("cat tests/A.cs | head -40; grep -n \"Log\\|45\" tests/A.cs"), "grep with a quoted |");
            True(ToolCallView.IsReadOnlyCommand("cd \"C:/r\" && git log --oneline -5 2>&1"), "cd, git log, 2>&1");
            True(ToolCallView.IsReadOnlyCommand("git -C src status --short"), "git -C");
            True(ToolCallView.IsReadOnlyCommand("git branch --show-current"), "git branch listing");
            True(ToolCallView.IsReadOnlyCommand("find . -name '*.cs' 2>/dev/null | wc -l"), "find, /dev/null");
            True(ToolCallView.IsReadOnlyCommand("Get-ChildItem src | Select-Object Name"), "PowerShell listing");
            True(ToolCallView.IsReadOnlyCommand("grep -rn 'a > b' src"), "> inside quotes");

            True(!ToolCallView.IsReadOnlyCommand("cat a.cs > b.cs"), "redirect into a file");
            True(!ToolCallView.IsReadOnlyCommand("echo hi >> log.txt"), "append into a file");
            True(!ToolCallView.IsReadOnlyCommand("sed -i 's/a/b/' x.cs"), "sed -i");
            True(!ToolCallView.IsReadOnlyCommand("sed -ni 's/a/b/p' x.cs"), "sed -ni");
            True(!ToolCallView.IsReadOnlyCommand("cat > f.cs <<'EOF'"), "heredoc");
            True(!ToolCallView.IsReadOnlyCommand("ls && rm -rf bin"), "rm after ls");
            True(!ToolCallView.IsReadOnlyCommand("git ls-files | xargs rm"), "xargs");
            True(!ToolCallView.IsReadOnlyCommand("git checkout -b x"), "git checkout");
            True(!ToolCallView.IsReadOnlyCommand("git branch -D x"), "git branch -D");
            True(!ToolCallView.IsReadOnlyCommand("find . -name '*.tmp' -delete"), "find -delete");
            True(!ToolCallView.IsReadOnlyCommand("find . -exec rm {} ;"), "find -exec");
            True(!ToolCallView.IsReadOnlyCommand("cat $(ls)"), "command substitution");
            True(!ToolCallView.IsReadOnlyCommand("echo \"`whoami`\""), "backticks in double quotes");
            True(!ToolCallView.IsReadOnlyCommand("S=src && cat $S/a.cs"), "variable assignment");
            True(!ToolCallView.IsReadOnlyCommand("dotnet test"), "a build tool");
            True(!ToolCallView.IsReadOnlyCommand("sleep 5 &"), "background job");
            True(!ToolCallView.IsReadOnlyCommand("cat a\nrm b"), "two lines");
            True(!ToolCallView.IsReadOnlyCommand("cat 'unclosed"), "unclosed quote");

            Eq("Read 3 files, searched for 2 patterns", ToolCallView.LookSummary(3, 2, false), "reads and searches");
            Eq("Read 1 file", ToolCallView.LookSummary(1, 0, false), "one file");
            Eq("Searched for 1 pattern", ToolCallView.LookSummary(0, 1, false), "searches only");
            Eq("Reading 2 files, searching for 1 pattern…", ToolCallView.LookSummary(2, 1, true), "still running");
            True(ToolCallView.IsLookTool("Grep") && !ToolCallView.IsLookTool("Bash"), "look tools");

            Eq("(timeout 10m)", ToolCallView.RunNote("Bash", new JObject { ["command"] = "x", ["timeout"] = 600000 }), "timeout in minutes");
            Eq("(timeout 2m 30s, running in the background)", ToolCallView.RunNote("PowerShell", new JObject { ["timeout"] = 150000, ["run_in_background"] = true }), "timeout and background");
            Eq(null, ToolCallView.RunNote("Bash", new JObject { ["command"] = "x" }), "defaults: no note");
            Eq(null, ToolCallView.RunNote("Read", new JObject { ["timeout"] = 5000 }), "only shell tools");
            Eq("45s", ToolCallView.Duration(TimeSpan.FromSeconds(45)), "seconds");
            Eq("1h 30m", ToolCallView.Duration(TimeSpan.FromMinutes(90)), "hours");

            var bash = ToolCallView.Summarize("Bash", "l1\nl2\nl3\nl4\nl5\n", false);
            Eq("l1|l2|l3", string.Join("|", bash.Lines), "first three lines");
            Eq(2, bash.MoreLines, "… +2 lines");
            Eq("(No output)", ToolCallView.Summarize("PowerShell", "", false).Lines.Single(), "empty output");
            Eq("Read 3 lines", ToolCallView.Summarize("Read", "     1\ta\n     2\tb\n     3\tc", false).Lines.Single(), "read");
            var edit = InlineDiff.FromToolUse("Edit", JObject.Parse("{\"file_path\":\"a.cs\",\"old_string\":\"x\",\"new_string\":\"y\\nz\"}"));
            Eq("Added 2 lines, removed 1 line", ToolCallView.Summarize("Edit", "The file a.cs has been updated successfully.", false, edit).Lines.Single(), "edit summary from the diff");
            var err = ToolCallView.Summarize("Bash", "Exit code 1\nboom", true);
            True(err.IsError && err.Lines[0] == "Exit code 1", "error shows its first lines");
            Eq("ok", string.Join("|", ToolCallView.Summarize("Bash", "ok\n<system-reminder>\nnote\n</system-reminder>", false).Lines), "system reminders are not output");
        }

        private static void Test_InlineDiff_Write()
        {
            var input = JObject.Parse("{\"file_path\":\"x.txt\",\"content\":\"1\\n2\\n3\\n4\\n5\\n6\\n7\\n8\\nnine\\n10\\n\"}");
            var fresh = InlineDiff.FromToolUse("Write", input);
            True(fresh.IsNewFile && fresh.Added == 10 && fresh.Removed == 0, "new file: all added");
            var changed = InlineDiff.FromToolUse("Write", input, "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n");
            Eq(" 7| 8|-9|+nine| 10", string.Join("|", changed.Lines), "only the change with 2 context lines; the start is dropped");
        }

        private static void Test_InlineDiff_Heredoc()
        {
            string cmd = "cd \"C:/Users/me/repo\" && cat > src/Samples/WindowSamples.cs <<'EOF'\nnamespace X;\n\npublic static class W { }\nEOF\ncat > tests/T.cs <<'EOF'\nclass T { }\nEOF\ndotnet test";
            var d = InlineDiff.FromToolUse("Bash", new JObject { ["command"] = cmd });
            Eq("C:/Users/me/repo/src/Samples/WindowSamples.cs", d.FilePath, "first file, relative to the cd");
            Eq(3, d.Added, "the heredoc body only");
            True(!d.IsAppend, "replace");
            var tagFirst = InlineDiff.FromHeredoc("cat <<EOF >> \"/tmp/log.txt\"\nline\nEOF");
            Eq("/tmp/log.txt", tagFirst.FilePath, "tag before the redirect, quoted path");
            True(tagFirst.IsAppend && tagFirst.Added == 1, "append");
            Eq(null, InlineDiff.FromHeredoc("git commit -F - <<'EOF'\nmsg\nEOF"), "a heredoc that is not written to a file");
        }

        private static void Test_Parser_Activity_ToolAndSubagent()
        {
            var parser = new ClaudeStreamParser();
            var tool = parser.Parse("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"Bash\",\"input\":{}}},\"parent_tool_use_id\":null}").Single();
            Eq(ClaudeActivity.Tool, tool.Activity, "tool_use block");
            Eq("Bash", tool.ToolName, "tool name");
            Eq(0, parser.Parse("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\"}},\"parent_tool_use_id\":\"toolu_9\"}").Count, "subagent blocks are not the main agent's activity");
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

        /// <summary>A streamed thinking turn from Claude Code 2.1.319: the block's message carries thinking_duration_ms.</summary>
        private static void Test_Parser_ThinkingDuration_RealCli_2_1_319()
        {
            var parser = new ClaudeStreamParser();
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-thinking-duration.jsonl"))
                .SelectMany(l => parser.Parse(l)).ToList();
            var thought = events.Single(e => e.Kind == ClaudeEventKind.ThinkingDuration);
            Eq(209L, thought.DurationMs, "thinking_duration_ms");
            int at = events.IndexOf(thought);
            True(events.FindIndex(e => e.Kind == ClaudeEventKind.Activity && e.Activity == ClaudeActivity.Thinking) < at, "after thinking started");
            True(events.FindIndex(e => e.Kind == ClaudeEventKind.Activity && e.Activity == ClaudeActivity.Writing) > at, "before writing starts, so the strip can use it");

            // Older captures have no field; a subagent's thinking is not the main agent's.
            var old = new ClaudeStreamParser();
            True(!File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.318-thinking.jsonl"))
                .SelectMany(l => old.Parse(l)).Any(e => e.Kind == ClaudeEventKind.ThinkingDuration), "2.1.318 sends none");
            var sub = new ClaudeStreamParser().Parse("{\"type\":\"assistant\",\"parent_tool_use_id\":\"toolu_1\",\"thinking_duration_ms\":900,\"message\":{\"id\":\"m\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"\"}]}}");
            True(!sub.Any(e => e.Kind == ClaudeEventKind.ThinkingDuration), "subagent ignored");
        }

        /// <summary>Edit then Write from Claude Code 2.1.319: each result carries the file as it was (originalFile).</summary>
        private static void Test_Parser_OriginalFile_RealCli_2_1_319()
        {
            var parser = new ClaudeStreamParser();
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-edit-write.jsonl"))
                .SelectMany(l => parser.Parse(l)).ToList();
            var calls = events.Where(e => e.Kind == ClaudeEventKind.ToolUse).ToDictionary(e => e.ToolUseId);
            var results = events.Where(e => e.Kind == ClaudeEventKind.ToolResult).ToList();

            var edit = results.Single(r => calls[r.ToolUseId].ToolName == "Edit");
            Eq("alpha\nbeta\ngamma\n", edit.OriginalFile, "Edit: originalFile");
            var write = results.Single(r => calls[r.ToolUseId].ToolName == "Write");
            Eq("old content\n", write.OriginalFile, "Write (update): originalFile");
            True(results.Where(r => calls[r.ToolUseId].ToolName == "Read").All(r => r.OriginalFile == null), "Read results carry none");

            // The diff rebuilt from it: line numbers for the Edit, the old line for the Write.
            var editDiff = InlineDiff.FromToolUse("Edit", calls[edit.ToolUseId].ToolInput, edit.OriginalFile);
            Eq(1, editDiff.Removed, "edit removed");
            Eq(1, editDiff.Added, "edit added");
            True(editDiff.Lines.Any(l => l.Kind == DiffLineKind.Removed && l.Text == "beta" && l.Number == 2), "beta removed on line 2: " + string.Join("|", editDiff.Lines.Select(l => l.Kind + ":" + l.Number + ":" + l.Text)));
            var writeDiff = InlineDiff.FromToolUse("Write", calls[write.ToolUseId].ToolInput, write.OriginalFile);
            Eq(1, writeDiff.Removed, "write removed the old line");
            True(!writeDiff.IsNewFile, "not a new file");
        }

        /// <summary>Claude Code 2.1.319: an allow rule, --allowedTools and acceptEdits, each in tool_result_meta.</summary>
        private static void Test_Parser_AllowedBy_RealCli_2_1_319()
        {
            var parser = new ClaudeStreamParser();
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-permission-decisions.jsonl"))
                .SelectMany(l => parser.Parse(l)).ToList();
            var tool = events.Where(e => e.Kind == ClaudeEventKind.ToolUse).ToDictionary(e => e.ToolUseId, e => e.ToolName);
            var by = events.Where(e => e.Kind == ClaudeEventKind.ToolResult).ToDictionary(e => tool[e.ToolUseId], e => e.AllowedBy);
            Eq("subcommandResults", by["Bash"], "Bash(echo *) in settings.local.json");
            Eq("rule", by["Read"], "--allowedTools Read");
            Eq("mode", by["Edit"], "acceptEdits");

            Eq("allowed by a rule", ToolCallView.AllowedNote("Bash", by["Bash"])?.Text, "shell command, rule");
            Eq(null, ToolCallView.AllowedNote("Read", by["Read"]), "reads: no note");
            Eq(null, ToolCallView.AllowedNote("Edit", by["Edit"]), "edits in acceptEdits: no note on every one");
            Eq("allowed by a rule", ToolCallView.AllowedNote("Edit", "rule")?.Text, "an edit allowed by a rule");
            Eq("allowed by the mode", ToolCallView.AllowedNote("PowerShell", "mode")?.Text, "a command run on the mode alone");
            Eq(null, ToolCallView.AllowedNote("Bash", "somethingNew"), "unknown reasons");
        }

        /// <summary>Claude Code 2.1.319: part of a file read, a Glob, Grep in both modes and a failing command.</summary>
        private static void Test_ToolData_RealCli_2_1_319()
        {
            var parser = new ClaudeStreamParser();
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-tool-data.jsonl"))
                .SelectMany(l => parser.Parse(l)).ToList();
            var calls = events.Where(e => e.Kind == ClaudeEventKind.ToolUse).ToDictionary(e => e.ToolUseId);
            var lines = events.Where(e => e.Kind == ClaudeEventKind.ToolResult).Select(r =>
            {
                var call = calls[r.ToolUseId];
                return (call.ToolName, Mode: (string)call.ToolInput?["output_mode"], Line: ToolCallView.Summarize(call.ToolName, r.ResultText, r.IsError, null, r.ResultData).Lines.First());
            }).ToList();
            Eq("Read 20 of 301 lines (from line 10)", lines.Single(l => l.ToolName == "Read").Line, "partial read");
            Eq("Found 2 files", lines.Single(l => l.ToolName == "Glob").Line, "glob");
            Eq("Found 3 files", lines.Single(l => l.ToolName == "Grep" && l.Mode == "files_with_matches").Line, "grep files");
            Eq("Found 4 lines", lines.Single(l => l.ToolName == "Grep" && l.Mode == "content").Line, "grep content");
            True(lines.Single(l => l.ToolName == "Bash" || l.ToolName == "PowerShell").Line.Contains("Exit code 3"), "failed command keeps its error line");
        }

        /// <summary>Claude Code 2.1.319: `mkdir newdir` (noOutputExpected), whose result text is only the cwd-reset note.</summary>
        private static void Test_ToolData_NoOutput_RealCli_2_1_319()
        {
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-no-output.jsonl"))
                .SelectMany(l => new ClaudeStreamParser().Parse(l)).ToList();
            var call = events.Single(e => e.Kind == ClaudeEventKind.ToolUse);
            var result = events.Single(e => e.Kind == ClaudeEventKind.ToolResult);
            True(result.ResultText.StartsWith("Shell cwd was reset to "), "the CLI's note is the whole text");
            Eq("Done", ToolCallView.Summarize(call.ToolName, result.ResultText, false, null, result.ResultData).Lines.Single(), "nothing expected, nothing printed");

            Eq("(No output)", ToolCallView.Summarize("Bash", "Shell cwd was reset to C:\\repo", false).Lines.Single(), "no data: as before, without the note");
            Eq("hi", ToolCallView.Summarize("Bash", "hi\nShell cwd was reset to C:\\repo", false).Lines.Single(), "the note is dropped after real output");
        }

        private static void Test_ToolData_Shapes()
        {
            Eq("Read 4 lines", ToolCallView.FromData("Read", JObject.Parse("{\"file\":{\"numLines\":4,\"startLine\":1,\"totalLines\":4}}")), "whole file");
            Eq("Read 50 of 2,000 lines", ToolCallView.FromData("Read", JObject.Parse("{\"file\":{\"numLines\":50,\"startLine\":1,\"totalLines\":2000}}")), "the start of a file");
            Eq(null, ToolCallView.FromData("Read", JObject.Parse("{\"type\":\"image\",\"file\":{\"base64\":\"x\"}}")), "an image: no line counts");
            Eq("Found 1 file (list cut short)", ToolCallView.FromData("Glob", JObject.Parse("{\"numFiles\":1,\"truncated\":true}")), "truncated glob");
            Eq("No files found", ToolCallView.FromData("Glob", JObject.Parse("{\"numFiles\":0}")), "nothing");
            Eq(null, ToolCallView.FromData("Grep", JObject.Parse("{\"mode\":\"count\",\"numMatches\":3}")), "count mode: not known, falls back");
            Eq("Read 3 lines", ToolCallView.Summarize("Read", "     1\ta\n     2\tb\n     3\tc", false).Lines.Single(), "no data: as before");
        }

        /// <summary>Claude Code 2.1.319 with the deny rule Bash(echo *): the call is blocked by the settings.</summary>
        private static void Test_Parser_DeniedBy_RealCli_2_1_319()
        {
            var parser = new ClaudeStreamParser();
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-deny-rule.jsonl"))
                .SelectMany(l => parser.Parse(l)).ToList();
            var result = events.First(e => e.Kind == ClaudeEventKind.ToolResult);
            True(result.IsError, "blocked call is an error");
            Eq("subcommandResults", result.DeniedBy, "rejected by a config rule");
            Eq(null, result.AllowedBy, "not allowed");
            Eq("denied by a rule", ToolCallView.DeniedNote(result.DeniedBy)?.Text, "row note");
            Eq("denied by the mode", ToolCallView.DeniedNote("mode")?.Text, "mode");
            Eq(null, ToolCallView.DeniedNote(null), "you denied it (or nothing): no note");
        }

        private static void Test_Parser_AllowedBy_Shapes()
        {
            var meta = JArray.Parse("[{\"id\":\"a\",\"permission_decision\":{\"decision\":\"accept\",\"source\":\"config\",\"reason_type\":\"rule\"}},"
                + "{\"id\":\"b\",\"permission_decision\":{\"decision\":\"accept\",\"source\":\"user\",\"reason_type\":\"prompt\"}}]");
            Eq("rule", ClaudeStreamParser.AllowedBy(meta, "a"), "matched by id");
            Eq(null, ClaudeStreamParser.AllowedBy(meta, "b"), "you were asked: not from settings");
            Eq(null, ClaudeStreamParser.AllowedBy(meta, "c"), "no entry");
            Eq(null, ClaudeStreamParser.AllowedBy(null, "a"), "no meta");
        }

        private static void Test_Parser_OriginalFile_Shapes()
        {
            Eq("", ClaudeStreamParser.OriginalFile(JObject.Parse("{\"type\":\"create\",\"filePath\":\"C:\\\\r\\\\n.txt\",\"content\":\"x\",\"originalFile\":null}")), "created: empty");
            Eq(null, ClaudeStreamParser.OriginalFile(JObject.Parse("{\"type\":\"text\",\"file\":{\"filePath\":\"a\",\"content\":\"x\"}}")), "Read");
            Eq(null, ClaudeStreamParser.OriginalFile(new JValue("Async agent launched")), "not an object");
            Eq(null, ClaudeStreamParser.OriginalFile(null), "missing");

            // Two results in one message: the structured result can't be matched to one of them.
            var two = new ClaudeStreamParser().Parse("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"a\",\"content\":\"ok\"},{\"type\":\"tool_result\",\"tool_use_id\":\"b\",\"content\":\"ok\"}]},\"tool_use_result\":{\"filePath\":\"x\",\"originalFile\":\"old\"}}");
            True(two.All(e => e.OriginalFile == null), "ambiguous: none");

            var changes = new TurnChanges();
            True(changes.Record(@"C:\r\a.cs", null), "first change recorded");
            True(!changes.Record(@"C:\r\a.cs", "later"), "second keeps the first");
            changes.Confirm(@"C:\r\a.cs", "exact");
            Eq("exact", changes.Collect(p => "now", p => true).Single().Before, "confirmed copy is the before");
        }

        // ---------- CLI version ----------
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
            Eq(null, new ClaudeStreamParser().Parse("{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"hi\"}").Single().TerminalReason, "older CLIs");
        }

        // ---------- Skills in the / menu ----------
        /// <summary>
        /// init from Claude Code 2.1.319 (lists trimmed): skills and slash_commands. A project skill sent as
        /// "/hello-check banana" over stream-json ran and answered "SKILL-RAN-banana".
        /// </summary>
        private static void Test_Skills_FromInit_2_1_319()
        {
            var init = new ClaudeStreamParser().Parse("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"model\":\"claude-haiku-5-5\",\"claude_code_version\":\"2.1.319\","
                + "\"slash_commands\":[\"hello-check\",\"simplify\",\"compact\",\"context\",\"anthropic-skills:pdf\",\"review\"],"
                + "\"skills\":[\"hello-check\",\"simplify\",\"anthropic-skills:pdf\",\"claude-only\"],"
                + "\"terminal_slash_commands\":[\"doctor\"]}").Single();
            Eq(4, init.Skills.Count, "skills");
            Eq(6, init.SlashCommands.Count, "slash_commands");

            var files = new List<SlashCommand>(SlashCommands.BuiltIn) { new SlashCommand { Name = "review", Source = "project", Description = "ours" } };
            var all = SlashCommands.WithSkills(files, init.Skills, init.SlashCommands);
            var skills = all.Where(c => c.Source == "skill").Select(c => c.Name).ToList();
            Eq("hello-check|simplify|anthropic-skills:pdf", string.Join("|", skills), "runnable skills, in order");
            True(!all.Any(c => c.Name == "context"), "built-in commands that are not skills stay out");
            True(!all.Any(c => c.Name == "claude-only"), "a skill only Claude can run stays out");
            Eq("project", all.Single(c => c.Name == "review").Source, "your own command wins");
            Eq("from plugin anthropic-skills", all.Single(c => c.Name == "anthropic-skills:pdf").Description, "plugin named");
            Eq("hello-check", SlashCommands.Match(all, "/hel").Single().Name, "autocomplete finds skills");
            Eq(files.Count, SlashCommands.WithSkills(files, null, null).Count, "older CLIs: unchanged");
        }

        // ---------- Subagent stats ----------
        private static void Test_SubagentStats_FromRealResults()
        {
            // The fork run (2.1.319): one background fork that completed.
            var fork = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-fork.jsonl"))
                .SelectMany(l => new ClaudeStreamParser().Parse(l)).Last(e => e.Kind == ClaudeEventKind.TurnCompleted);
            Eq(1, fork.Subagents.Spawned, "spawned");
            Eq("1 agent", fork.Subagents.FooterNote(), "footer");
            Eq(null, fork.Subagents.Warning(), "nothing went wrong");

            // The footer adds what they used (from the meter: the fork's 23,917 cache reads + 462 writes + 4 input + output)
            var meter = new TokenMeter();
            foreach (var e in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-fork.jsonl")).SelectMany(l => new ClaudeStreamParser().Parse(l))) meter.Observe(e);
            True(meter.LastTurnSubagentTotal.Total > 24_383, "the fork's tokens: " + meter.LastTurnSubagentTotal.Total);
            Eq($"1 agent ({TokenMeter.Format(meter.LastTurnSubagentTotal.Total)} tok)", TurnFooter.Agents(fork.Subagents, meter.LastTurnSubagentTotal.Total), "footer with tokens");
            Eq("2 agents (31.0k tok)", TurnFooter.Agents(SubagentStats.Parse(JObject.Parse("{\"spawned\":2}")), 31_000));
            Eq("high effort", TurnFooter.Effort("high"), "effort sent");
            Eq("xhigh effort", TurnFooter.Effort(" XHigh "), "any case");
            Eq(null, TurnFooter.Effort(null), "Claude Code's default: nothing");
            Eq("1 agent", TurnFooter.Agents(fork.Subagents, 0), "tokens not known");
            Eq("agents 1.2k tok", TurnFooter.Agents(null, 1_234), "a background agent from an earlier turn, or no subagent_stats");
            Eq(null, TurnFooter.Agents(null, 0));

            // The footer's cost, with the agents' share
            Eq("$0.020 ($0.012 agents)", TurnFooter.Cost(0.020, 0.012));
            Eq("$0.020", TurnFooter.Cost(0.020, 0.0002), "too small to show");
            Eq("$0.010 ($0.010 agents)", TurnFooter.Cost(0.010, 0.013), "never more than the turn");
            Eq("$0.000", TurnFooter.Cost(0, 0.013), "the CLI sent no cost");
            // The fork run: a Haiku fork reading 24k cached tokens costs well under $0.001, so no share is shown
            True(meter.LastTurnSubagentCostUsd > 0 && meter.LastTurnSubagentCostUsd < 0.0005, "the fork's cost: " + meter.LastTurnSubagentCostUsd);
            Eq("$0.003", TurnFooter.Cost(meter.LastTurnCostUsd, meter.LastTurnSubagentCostUsd), "the fork run");
            // The session summary names the kinds (a FreshSession meter: the fork's exact cost, all Fork)
            var kindMeter = new TokenMeter();
            kindMeter.FreshSession();
            foreach (var e in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-fork.jsonl")).SelectMany(l => new ClaudeStreamParser().Parse(l))) kindMeter.Observe(e);
            True(SessionSummary.Format(kindMeter).Contains("\nSubagents by kind: Fork " + AgentTask.FormatCost(kindMeter.LastTurnSubagentCostUsd) + "\n"), SessionSummary.Format(kindMeter));
            Eq(1, kindMeter.SessionAgentCostByType.Count);

            // A turn without agents (2.1.319 thinking capture): no footer note.
            var plain = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-thinking-duration.jsonl"))
                .SelectMany(l => new ClaudeStreamParser().Parse(l)).Single(e => e.Kind == ClaudeEventKind.TurnCompleted);
            True(plain.Subagents != null && plain.Subagents.FooterNote() == null, "no agents, no note");

            var trouble = SubagentStats.Parse(JObject.Parse("{\"spawned\":4,\"completed\":1,\"failed\":1,\"killed\":{\"parent\":0,\"user\":1,\"system\":1},"
                + "\"refused\":{\"depth_limit\":0,\"concurrency_limit\":2,\"budget\":0}}"));
            Eq("4 agents", trouble.FooterNote());
            Eq("Claude Code refused 2 subagents (too many at once); 1 subagent failed; stopped 1 subagent.", trouble.Warning(), "warning");
            Eq(null, SubagentStats.Parse(null), "older CLIs");
        }

        // ---------- Session notices (from init) ----------
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

        // ---------- Prompt cache window ----------
        private static void Test_CacheWindow_FromRealUsage_2_1_319()
        {
            // The real 2.1.319 thinking turn: Claude Code wrote the cache with the 1-hour lifetime.
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-thinking-duration.jsonl"))
                .SelectMany(l => new ClaudeStreamParser().Parse(l)).ToList();
            var usage = events.Last(e => e.Kind == ClaudeEventKind.AssistantUsage && !e.IsSubagent).Usage;
            True(usage.CacheCreation1h > 0 || usage.CacheRead > 0, "cache used");
            var cache = new CacheWindow();
            var t0 = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
            cache.Use(usage, t0);
            if (usage.CacheCreation1h > 0) Eq(CacheWindow.Long, cache.Lifetime, "1-hour cache");
        }

        private static void Test_CacheWindow_Notes()
        {
            var t0 = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
            var big = new TokenUsage { Input = 10, CacheRead = 38_000, CacheCreation = 2_000, CacheCreation1h = 2_000, Output = 100 };
            var cache = new CacheWindow();
            Eq(null, cache.Status(t0), "nothing cached yet");
            cache.Use(big, t0);
            Eq("Prompt cache: warm for 60 more min (1-hour cache).", cache.Status(t0));
            Eq(null, cache.SoonNote(t0.AddMinutes(50)), "not yet");
            Eq("Prompt cache expires in 4 min: after that the next prompt sends ~40.0k tokens at the full input price.", cache.SoonNote(t0.AddMinutes(56)), "soon");
            Eq(null, cache.ExpiredNote(t0.AddMinutes(59)), "still warm");
            True(cache.ExpiredNote(t0.AddMinutes(61))?.StartsWith("Prompt cache expired (idle over an hour): the next prompt sends ~40.0k tokens") == true, cache.ExpiredNote(t0.AddMinutes(61)));
            cache.Use(new TokenUsage { CacheRead = 40_000 }, t0.AddMinutes(30)); // a read keeps it again
            Eq(null, cache.ExpiredNote(t0.AddMinutes(61)), "refreshed by a later request");

            var shortCache = new CacheWindow();
            shortCache.Use(new TokenUsage { CacheCreation = 20_000, CacheCreation5m = 20_000 }, t0);
            Eq(CacheWindow.Short, shortCache.Lifetime);
            Eq(null, shortCache.SoonNote(t0.AddMinutes(3)), "a 5-minute cache gets no 'soon' note");
            True(shortCache.ExpiredNote(t0.AddMinutes(6))?.Contains("idle over 5 minutes") == true, "5-minute expiry");

            var small = new CacheWindow();
            small.Use(new TokenUsage { CacheCreation = 3_000, CacheCreation1h = 3_000 }, t0);
            Eq(null, small.ExpiredNote(t0.AddHours(2)), "small context: not worth a note");

            var u = TokenUsage.From(JObject.Parse("{\"input_tokens\":4,\"cache_creation_input_tokens\":462,\"cache_read_input_tokens\":23917,\"cache_creation\":{\"ephemeral_5m_input_tokens\":0,\"ephemeral_1h_input_tokens\":462}}"));
            Eq(462L, u.CacheCreation1h, "parsed 1h");
            Eq(0L, u.CacheCreation5m, "parsed 5m");
        }

        // ---------- Repeated reads ----------
        private static void Test_ReadTracker()
        {
            var t = new ReadTracker();
            string body = new string('x', 12_000);
            ClaudeEvent Read(string id, string path, string extra = "") => new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolName = "Read", ToolUseId = id, ToolInput = JObject.Parse("{\"file_path\":\"" + path.Replace("\\", "\\\\") + "\"" + extra + "}") };
            ClaudeEvent Result(string id, string text, bool sub = false) => new ClaudeEvent { Kind = ClaudeEventKind.ToolResult, ToolUseId = id, ResultText = text, ResultChars = text.Length, IsSubagent = sub };

            t.Observe(Read("1", @"C:\r\Big.cs")); Eq(null, t.Observe(Result("1", body)), "first read");
            t.Observe(Read("2", @"c:\r\big.cs")); Eq((@"c:\r\big.cs", 4000), t.Observe(Result("2", body)), "same file, same text: repeated");
            Eq(4000L, t.RepeatedTokens);

            t.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolName = "Edit", ToolUseId = "3", ToolInput = JObject.Parse("{\"file_path\":\"C:\\\\r\\\\Big.cs\"}") });
            t.Observe(Read("4", @"C:\r\Big.cs")); Eq(null, t.Observe(Result("4", body)), "after Claude's edit: new");
            t.Observe(Read("5", @"C:\r\Big.cs")); Eq(null, t.Observe(Result("5", body + "y")), "changed by you: new");
            t.Observe(Read("6", @"C:\r\Big.cs", ",\"offset\":10,\"limit\":20")); Eq(null, t.Observe(Result("6", body + "y")), "part of the file: not compared");
            t.Observe(new ClaudeEvent { Kind = ClaudeEventKind.Compacted });
            t.Observe(Read("7", @"C:\r\Big.cs")); Eq(null, t.Observe(Result("7", body + "y")), "after compaction the earlier copy is gone");
            t.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolName = "Read", ToolUseId = "8", IsSubagent = true, ToolInput = JObject.Parse("{\"file_path\":\"C:\\\\r\\\\Big.cs\"}") });
            Eq(null, t.Observe(Result("8", body + "y", sub: true)), "a subagent's read: its own context");

            True(ReadTracker.Tip(@"C:\r\Big.cs", 3000).StartsWith("Claude read Big.cs again (~3.0k tokens)"), ReadTracker.Tip(@"C:\r\Big.cs", 3000));
            Eq(null, ReadTracker.Tip(@"C:\r\a.cs", 500), "small file: no tip");
        }

        // ---------- Large command output ----------
        private static void Test_LargeCommandOutput()
        {
            string without = SessionNotices.LargeCommandOutput("Bash", 48_000, hookInstalled: false);
            True(without?.StartsWith("That command put ~16.0k tokens of output into the context.") == true, without);
            True(without.Contains("Install Token-Saving Settings and Hooks"), "points to the setup");
            True(SessionNotices.LargeCommandOutput("PowerShell", 48_000, hookInstalled: true).Contains("Asking Claude to filter it"), "hook in place: filter");
            Eq(null, SessionNotices.LargeCommandOutput("Bash", 8_000, false), "small output");
            Eq(null, SessionNotices.LargeCommandOutput("Read", 80_000, false), "not a shell command");

            string dir = Path.Combine(Path.GetTempPath(), "cvs-hook-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                True(!TokenSaverSetup.IsInstalled(dir), "fresh folder");
                TokenSaverSetup.Apply(dir);
                True(TokenSaverSetup.IsInstalled(dir), "after Install Token-Saving Settings and Hooks");
                True(!TokenSaverSetup.IsInstalled(null), "no folder");
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        // ---------- Turn footer ----------
        private static void Test_TurnFooter_Thinking_RealCli_2_1_319()
        {
            var done = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-thinking-duration.jsonl"))
                .SelectMany(l => new ClaudeStreamParser().Parse(l)).Single(e => e.Kind == ClaudeEventKind.TurnCompleted);
            Eq(56L, done.Usage.Thinking, "output_tokens_details.thinking_tokens");
            True(TurnFooter.Extras(done.Usage).StartsWith(" · 56 thinking"), TurnFooter.Extras(done.Usage));
            Eq("", TurnFooter.Extras(new TokenUsage { Output = 10 }), "no thinking, nothing added");
            Eq(" · 1.2k thinking", TurnFooter.Extras(new TokenUsage { Output = 1500, Thinking = 1200 }));
        }

        private static void Test_TurnFooter_Cached_RealCli_2_1_319()
        {
            var done = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-thinking-duration.jsonl"))
                .SelectMany(l => new ClaudeStreamParser().Parse(l)).Single(e => e.Kind == ClaudeEventKind.TurnCompleted);
            Eq(" · 56 thinking · 60% cached", TurnFooter.Extras(done.Usage), "14,256 of 23,937 input tokens from the cache");
            Eq(null, TurnFooter.CachedPercent(new TokenUsage { Input = 500 }), "too small to say");
            Eq(0, TurnFooter.CachedPercent(new TokenUsage { Input = 100, CacheCreation = 30_000 }), "cache just written: 0%");
        }

        // ---------- Cache savings ----------
        private static void Test_CacheSaved_RealTurn_2_1_319()
        {
            // The real Haiku turn: 14,256 read from the cache, 9,679 written with the 1-hour lifetime (2x input).
            var meter = new TokenMeter();
            foreach (var e in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-thinking-duration.jsonl"))
                .SelectMany(l => new ClaudeStreamParser().Parse(l))) meter.Observe(e);
            True(Math.Abs(meter.LastTurnCacheSavedUsd - 0.00031514) < 1e-9, "haiku: 14,256 x (0.10-0.01) - 9,679 x 0.10, per MTok: " + meter.LastTurnCacheSavedUsd);
            Eq(meter.LastTurnCacheSavedUsd, meter.SessionCacheSavedUsd, "one turn so far");
            Eq(" · 56 thinking · 60% cached", TurnFooter.Extras(meter.LastTurn, meter.LastTurnCacheSavedUsd), "under $0.001: no amount");

            var sonnet = new PriceTable().Sonnet; // input 2, cache write 2.5, cache read 0.1
            True(Math.Abs(sonnet.CacheSavedUsd(14_256, 9_679, 9_679) - 0.0077284) < 1e-9, "the same turn on Sonnet");
            True(Math.Abs(sonnet.CacheSavedUsd(0, 10_000, 0) + 0.005) < 1e-9, "a 5-minute write only: -0.25 x 2 per MTok");
            True(Math.Abs(sonnet.CacheSavedUsd(1_000_000, 0, 0) - 1.9) < 1e-9, "1M read from the cache saves $1.90");
            Eq(" · 90% cached (saved $0.018)", TurnFooter.Extras(new TokenUsage { Input = 1000, CacheRead = 9000 }, 0.018), "footer with an amount");

            meter.Reset();
            Eq(0.0, meter.SessionCacheSavedUsd, "reset");
        }

        private static void Test_CacheBreak_ModelChange_And_Prefix()
        {
            var w = new CacheBreakWatch();
            var t0 = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
            var prices = new PriceTable();
            TokenUsage U(long input, long read, long write, long write1h = 0) => new TokenUsage { Input = input, CacheRead = read, CacheCreation = write, CacheCreation1h = write1h, Output = 100 };

            True(w.Observe(U(5, 0, 40_000), "claude-opus-5-5", "m1", t0, prices) == null, "first request writes the cache");
            True(w.Observe(U(5, 0, 40_000), "claude-opus-5-5", "m1", t0.AddSeconds(1), prices) == null, "the same message again (another content block)");
            True(w.Observe(U(5, 40_005, 2_000), "claude-opus-5-5", "m2", t0.AddMinutes(1), prices) == null, "normal: reads what the last request sent");

            var b = w.Observe(U(5, 0, 42_100), "claude-sonnet-5-5", "m3", t0.AddMinutes(2), prices);
            True(b != null && b.ModelChanged, "model changed while warm");
            Eq(42_010L, b.Tokens, "the previous context, written again");
            True(Math.Abs(b.ExtraUsd - 42_010 * (2.5 - 0.1) / 1e6) < 1e-9, "Sonnet 5-minute write minus cache read: " + b.ExtraUsd);
            True(b.Note().Contains("from Opus to Sonnet") && b.Note().Contains("~42.0k tokens") && b.Note().Contains("(~$0.10 more"), b.Note());

            var p = w.Observe(U(5, 0, 44_000, 44_000), "claude-sonnet-5-5", "m4", t0.AddMinutes(3), prices);
            True(p != null && !p.ModelChanged && p.Note().Contains("CLAUDE.md"), "same model: the prefix changed");
            True(Math.Abs(p.ExtraUsd - 42_105 * (4.0 - 0.1) / 1e6) < 1e-9, "1-hour write at 2x input: " + p.ExtraUsd);

            // After an hour-long cache's lifetime it is a cold cache, not a break; within it, still a break.
            True(w.Observe(U(5, 0, 44_100), "claude-sonnet-5-5", "m5", t0.AddMinutes(50), prices) != null, "1-hour cache still warm after 47 min");
            True(w.Observe(U(5, 0, 44_200), "claude-sonnet-5-5", "m6", t0.AddMinutes(115), prices) == null, "expired: the cold-cache note covers it");

            w.Compacted();
            True(w.Observe(U(5, 0, 8_000), "claude-sonnet-5-5", "m7", t0.AddMinutes(116), prices) == null, "after /compact a new cache is expected");
            True(w.Observe(U(5, 0, 9_000), "claude-sonnet-5-5", "m8", t0.AddMinutes(117), prices) == null, "small context: not worth a note");

            // Through the meter: AssistantUsage events of the main agent only.
            var meter = new TokenMeter();
            meter.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Usage = U(5, 0, 30_000), Model = "claude-opus-5-5", MessageId = "a" });
            meter.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Usage = U(5, 0, 31_000), Model = "claude-haiku-5-5", MessageId = "s", IsSubagent = true });
            True(meter.TakeCacheBreak() == null, "a subagent has its own cache");
            meter.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Usage = U(5, 0, 31_000), Model = "claude-sonnet-5-5", MessageId = "b" });
            var mb = meter.TakeCacheBreak();
            True(mb != null && mb.FromFamily == "opus", "meter reports the break");
            True(meter.TakeCacheBreak() == null, "taken once");
        }

        private static void Test_SessionCost_FromSessionFile()
        {
            string root = Path.Combine(Path.GetTempPath(), "claudevs-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "s1", "subagents"));
            string file = Path.Combine(root, "s1.jsonl");
            string Asst(string id, string model, string usage) =>
                "{\"type\":\"assistant\",\"message\":{\"id\":\"" + id + "\",\"model\":\"" + model + "\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}],\"usage\":" + usage + "}}";
            // Opus: 1M read + 1M 1-hour write + 1M output; repeated for its second content block.
            string big = "{\"input_tokens\":0,\"cache_read_input_tokens\":1000000,\"cache_creation_input_tokens\":1000000,\"cache_creation\":{\"ephemeral_1h_input_tokens\":1000000,\"ephemeral_5m_input_tokens\":0},\"output_tokens\":1000000}";
            File.WriteAllLines(file, new[]
            {
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}",
                Asst("m1", "claude-opus-5-5", big),
                Asst("m1", "claude-opus-5-5", big),
                Asst("m2", "<synthetic>", "{\"input_tokens\":0,\"output_tokens\":0}"),
                "not json",
                // A subagent's message (isSidechain) in the main file, should one be there: Haiku, 100k input, not over the tier: $0.01
                Asst("m4", "claude-haiku-5-5", "{\"input_tokens\":100000,\"output_tokens\":0}").Replace("{\"type\":\"assistant\",", "{\"type\":\"assistant\",\"isSidechain\":true,"),
            });
            // A subagent on Haiku: one request with 1M input, over the 100k tier: 5x (-e.50 per Mtok).
            File.WriteAllLines(Path.Combine(root, "s1", "subagents", "agent-a.jsonl"), new[] { Asst("m3", "claude-haiku-5-5", "{\"input_tokens\":1000000,\"output_tokens\":0}") });
            try
            {
                var info = SessionCost.Read(file, new PriceTable());
                Eq(3, info.Requests, "each message once; synthetic skipped; subagents counted");
                True(Math.Abs(info.CostUsd - (0.2 + 8 + 20 + 0.5 + 0.01)) < 1e-9, "opus read 0.2 + 1h write 2x4 + output 20, haiku input 0.5 (over 100k), sidechain 0.01: " + info.CostUsd);
                True(Math.Abs(info.AgentCostUsd - 0.51) < 1e-9, "the subagents: their file and the sidechain line: " + info.AgentCostUsd);
                Eq("$28.71 ($0.51 agents)", info.Short(), "short, with the agents' part");
                Eq("$0.42", new SessionCostInfo { CostUsd = 0.42, AgentCostUsd = 0.004 }.Short(), "agents under half a cent: not shown");
                Eq(null, new SessionCostInfo { CostUsd = 0.004 }.Short(), "under half a cent");
                Eq("$123", new SessionCostInfo { CostUsd = 123.4 }.Short(), "no cents from $100");

                var f = new FileInfo(file);
                Eq(null, SessionCost.Cached(file, f.LastWriteTimeUtc, f.Length), "not read through Get yet");
                var got = SessionCost.Get(file, new PriceTable());
                True(ReferenceEquals(got, SessionCost.Cached(file, f.LastWriteTimeUtc, f.Length)), "kept until the file changes");
                Eq(null, SessionCost.Cached(file, f.LastWriteTimeUtc, f.Length + 1), "a changed file is read again");

                var listed = new SessionInfo { FilePath = file, LastWriteUtc = f.LastWriteTimeUtc, SizeBytes = f.Length };
                var unread = new SessionInfo { FilePath = Path.Combine(root, "s2.jsonl"), LastWriteUtc = f.LastWriteTimeUtc, SizeBytes = 1 };
                Eq("1 session, $28.71 in total (API prices)", SessionCost.TotalLine(new[] { listed }), "footer");
                Eq("Reading what the sessions cost…", SessionCost.TotalLine(new[] { listed, unread }), "one not read yet");
                Eq(null, SessionCost.TotalLine(new SessionInfo[0]), "no sessions");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void Test_CacheSavedLine()
        {
            var m = new TokenMeter();
            Eq(null, m.CacheSavedLine(), "before the first turn");
            // Sonnet: first turn writes the cache (5-minute), the second reads it.
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Model = "claude-sonnet-5-5", Usage = new TokenUsage { Input = 10, CacheCreation = 100_000, CacheCreation5m = 100_000 } });
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { Input = 10, CacheCreation = 100_000, CacheCreation5m = 100_000 } });
            Eq("Prompt cache: 0% of input read from the cache, writing it cost $0.05 more than it saved so far (it pays off on the next prompts).", m.CacheSavedLine());
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Model = "claude-sonnet-5-5", Usage = new TokenUsage { Input = 10, CacheRead = 300_000 } });
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { Input = 10, CacheRead = 300_000 } });
            Eq("Prompt cache: 75% of input read from the cache, saved $0.52 in this conversation (API prices).", m.CacheSavedLine(), "0.57 saved - 0.05 for the write");
            True(SessionSummary.Format(m).Contains("\nCache hits: 75% (saved $0.52)"), "session summary: " + SessionSummary.Format(m));
        }

        private static void Test_TokenEstimate_KeptAcrossRestarts()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cvs-est-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(dir, "token-estimate.json");
            try
            {
                TokenEstimate.Reset();
                TokenEstimate.UseStore(file);             // nothing saved yet: the start value
                Eq(3.0, TokenEstimate.CharsPerToken, "no file: 3.0");
                for (int i = 0; i < 20; i++) TokenEstimate.Learn(8000, 2900);
                double learned = TokenEstimate.CharsPerToken;
                True(File.Exists(file), "saved after learning");
                True(!File.ReadAllText(file).Contains("8000 chars") && File.ReadAllText(file).Contains("\"charsPerToken\""), File.ReadAllText(file));

                TokenEstimate.Reset();                     // a restart
                Eq(3.0, TokenEstimate.CharsPerToken, "fresh process");
                TokenEstimate.UseStore(file);
                True(Math.Abs(TokenEstimate.CharsPerToken - learned) < 0.001, $"loaded {TokenEstimate.CharsPerToken} = {learned}");

                foreach (var bad in new[] { "not json", "{\"chars\":100,\"tokens\":10}", "{\"chars\":-5,\"tokens\":2}", "{}" })
                {
                    File.WriteAllText(file, bad);
                    TokenEstimate.Reset();
                    TokenEstimate.UseStore(file);
                    Eq(3.0, TokenEstimate.CharsPerToken, "ignored: " + bad);
                }
            }
            finally { TokenEstimate.Reset(); try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        private static void Test_TokenEstimate_LearnsFromTurns()
        {
            try
            {
                TokenEstimate.Reset();
                Eq(3.0, TokenEstimate.CharsPerToken, "start: 3 characters per token (measured 2.7-3.0 with 2.1.322)");
                Eq(2667, TokenEstimate.FromChars(8000));
                Eq(0, TokenEstimate.FromText(null));

                // Turns as --live-prompt-tokens saw them: context grows by the prompt + ~30 tokens of framing
                var learner = new PromptTokenLearner();
                int msg = 0;
                long ctx = 25_000;
                ClaudeEvent Usage(long context, long output) => new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, MessageId = "m" + (++msg), Usage = new TokenUsage { CacheRead = context, Output = output } };
                void Turn(int promptChars, long promptTokens, long extra = 0)
                {
                    learner.Observe(new ClaudeEvent { Kind = ClaudeEventKind.SessionStarted, IsFirstInit = msg == 0 });
                    if (promptChars > 0) learner.PromptSent(promptChars);
                    ctx += promptTokens + PromptTokenLearner.FramingTokens + extra + 5; // + the previous answer (5 output tokens)
                    learner.Observe(Usage(ctx, 5));
                    learner.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted });
                }
                Turn(14, 6);
                Turn(8000, 2700, extra: 2000);          // second turn of the process: its one-time extra is not learned from
                Eq(3.0, TokenEstimate.CharsPerToken, "first two turns of a process are skipped");
                Turn(8000, 2900);                       // code-like: 2.76 per token
                Eq(8000.0 / 2900, learner.LastMeasured, "measured");
                True(TokenEstimate.CharsPerToken < 3.0 && TokenEstimate.CharsPerToken > 2.76, "moved toward it: " + TokenEstimate.CharsPerToken);
                double before = TokenEstimate.CharsPerToken;
                Turn(500, 170);                         // short prompt: framing would dominate
                Eq(before, TokenEstimate.CharsPerToken, "short prompts are not used");
                learner.Observe(new ClaudeEvent { Kind = ClaudeEventKind.Compacted });
                ctx = 3_000;                            // after compaction the context is rebuilt
                Turn(8000, 2700);
                Eq(before, TokenEstimate.CharsPerToken, "not across a compaction");
                Eq(false, TokenEstimate.Learn(8000, 500), "16 chars per token: implausible, ignored");
                for (int i = 0; i < 40; i++) TokenEstimate.Learn(8000, 2700);
                True(Math.Abs(TokenEstimate.CharsPerToken - 8000.0 / 2700) < 0.05, "follows steady evidence: " + TokenEstimate.CharsPerToken);
            }
            finally { TokenEstimate.Reset(); }
        }

        private static void Test_PromptSize()
        {
            Eq(null, PromptSize.Note("fix the null check in Save()"), "short prompt");
            Eq(null, PromptSize.Note(""), "empty");
            Eq("~2.7k tokens", PromptSize.Note(new string('a', 8_000)), "long prompt: just the size");
            string huge = PromptSize.Note(new string('a', 40_000));
            True(huge.StartsWith("~13.3k tokens: it goes with every later request."), huge);
            True(huge.Contains("Ctrl+Alt+K, F"), "points to Attach Current File as Reference");
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

        private static void Test_SessionSummary()
        {
            Eq("", SessionSummary.Format(null), "no meter");
            // The real 2.1.319 thinking turn through the meter.
            var meter = new TokenMeter();
            foreach (var e in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-thinking-duration.jsonl"))
                .SelectMany(l => new ClaudeStreamParser().Parse(l))) meter.Observe(e);
            string s = SessionSummary.Format(meter, "Train times", "abc-123");
            var lines = s.Split('\n');
            Eq("Claude Code session: Train times", lines[0]);
            True(lines[1].StartsWith("Turns: 1 · Cost: $0.00"), lines[1]);
            True(lines[2].StartsWith("Tokens: ") && lines[2].Contains("of which thinking 56") && lines[2].Contains("cache read 14.3k"), lines[2]);
            True(lines[3].StartsWith("Cache hits: 60%"), lines[3]);
            Eq("Session: abc-123", lines.Last());
        }

        private static void Test_FileIndex_Sizes()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cvs-sizes-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "src"));
                File.WriteAllText(Path.Combine(dir, "src", "Big.cs"), new string('x', 12_800));
                File.WriteAllText(Path.Combine(dir, "src", "Empty.cs"), "");
                var index = FileIndex.Build(dir);
                var big = index.SearchWithSizes("big").Single();
                Eq("src/Big.cs", big.Path, "the path is what gets inserted");
                Eq("~4.3k tok", big.SizeLabel);
                Eq("src/Big.cs   ~4.3k tok", big.ToString(), "what the list shows");
                Eq("src/Empty.cs", index.SearchWithSizes("empty").Single().ToString(), "empty file: no size");
                Eq("", new FileHit { Path = "gone.cs" }.SizeLabel, "unreadable: no size");
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        private static void Test_ResultTokensByTool()
        {
            // The real 2.1.319 tool capture: a partial Read, Glob, two Greps and a failing command.
            var meter = new TokenMeter();
            foreach (var e in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-tool-data.jsonl"))
                .SelectMany(l => new ClaudeStreamParser().Parse(l))) meter.Observe(e);
            var groups = meter.ResultTokensByTool().Select(kv => kv.Key).ToList();
            True(groups.Contains("Read") && groups.Contains("Search") && groups.Contains("Shell"), string.Join(",", groups));
            True(!groups.Contains("Grep") && !groups.Contains("Glob"), "searches grouped");

            var m = new TokenMeter();
            void Call(string id, string tool, int chars, bool sub = false)
            {
                m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolUseId = id, ToolName = tool, IsSubagent = sub });
                m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolResult, ToolUseId = id, ResultChars = chars, IsSubagent = sub });
            }
            Call("1", "Read", 40_000); Call("2", "Bash", 8_000); Call("3", "PowerShell", 4_000); Call("4", "mcp__visualstudio__get_error_list", 400);
            Call("5", "Read", 99_000, sub: true); // a subagent's own context
            Eq("Read 13334|Shell 4000|MCP 134", string.Join("|", m.ResultTokensByTool().Select(kv => kv.Key + " " + kv.Value)));
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { Input = 10 } });
            True(SessionSummary.Format(m).EndsWith("\nTool results: Read 13.3k · Shell 4.0k · MCP 134"), SessionSummary.Format(m));
            Eq("MCP 134|Read 13334|Shell 4000", string.Join("|", m.LastTurnResultTokensByTool.OrderBy(kv => kv.Key).Select(kv => kv.Key + " " + kv.Value)), "the turn's, for the usage log");
            Call("6", "Grep", 3_000);
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { Input = 10 } });
            Eq("Search 1000", string.Join("|", m.LastTurnResultTokensByTool.Select(kv => kv.Key + " " + kv.Value)), "only the next turn's");
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { Input = 10 } });
            Eq(null, m.LastTurnResultTokensByTool, "a turn without tools");
            Eq(false, SessionSummary.Format(m).Contains("By effort:"), "no effort known: no line");
            m.AddTurnEffort(null);
            Eq(false, SessionSummary.Format(m).Contains("By effort:"), "only Claude Code's default: no line");

            var em = new TokenMeter();
            void Turn(double cumulativeCost, string effort)
            {
                em.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { Input = 10 }, TotalCostUsd = cumulativeCost });
                em.AddTurnEffort(effort);
            }
            Turn(0.10, "high"); Turn(0.15, null); Turn(0.45, "HIGH"); Turn(0.47, "low");
            True(SessionSummary.Format(em).EndsWith("\nBy effort: low 1 turn $0.02 · high 2 turns $0.40 · default 1 turn $0.05"), SessionSummary.Format(em));
            em.Reset();
            Eq(0, em.SessionByEffort.Count, "reset");

            m.Reset();
            Eq(0, m.ResultTokensByTool().Count, "reset");
        }

        private static void Test_TurnFooter_CostBreakdown()
        {
            var prices = new PriceTable();
            var turn = new TokenUsage { Input = 500, Output = 400, Thinking = 300, CacheRead = 40_000, CacheCreation = 2_000, CacheCreation1h = 2_000 };
            string tip = TurnFooter.CostBreakdown("claude-opus-5-5", turn, 0.012, 0.047, prices);
            var lines = tip.Split('\n');
            Eq("What this answer cost, at Opus prices:", lines[0]);
            True(lines[1].StartsWith("Input") && lines[1].EndsWith("500 tok  $0.0020"), lines[1]);
            True(lines[2].StartsWith("Output") && lines[2].EndsWith("400 tok  $0.0080"), lines[2]);
            Eq("  of which thinking 300 tok  $0.0060", lines[3]);
            True(lines[4].StartsWith("Cache reads") && lines[4].EndsWith("40.0k tok  $0.0080"), "40k × $0.20: " + lines[4]);
            True(lines[5].StartsWith("Cache writes") && lines[5].EndsWith("tok  $0.016"), "1-hour writes at 2× input: " + lines[5]);
            True(lines[6].StartsWith("Subagents") && lines[6].EndsWith("$0.012"), lines[6]);
            Eq("Total $0.046", lines[7], "within a cent of Claude Code's: no note");

            True(TurnFooter.CostBreakdown("claude-opus-5-5", turn, 0, 0.20, prices).EndsWith("(Claude Code counts $0.200; Options → Pricing may differ from its prices)"), "differs by a cent or more");
            var fast = new TokenUsage { Input = 1_000, Output = 100, FastTokens = 1_100 };
            True(TurnFooter.CostBreakdown("claude-opus-5-5", fast, 0, 0, prices).StartsWith("What this answer cost, at Opus prices ×2 (fast mode or long context):"), "fast mode");
            Eq(null, TurnFooter.CostBreakdown("some-other-model", turn, 0, 0, prices), "price not known");
            Eq(null, TurnFooter.CostBreakdown("claude-opus-5-5", default(TokenUsage), 0, 0, prices), "no tokens");
        }

        private static void Test_SavingsReport_CacheRewrites()
        {
            var t = DateTime.UtcNow.AddDays(-1);
            var recs = new[]
            {
                // a: the first turn writes (not a rewrite), the second reads, the third writes 50k again (1-hour) after a break
                new UsageRecord { Timestamp = t, SessionId = "a", ModelUsed = "claude-sonnet-5-5", CacheWriteTokens = 40_000, CacheWrite1hTokens = 40_000 },
                new UsageRecord { Timestamp = t.AddMinutes(5), SessionId = "a", ModelUsed = "claude-sonnet-5-5", CacheReadTokens = 40_000, CacheWriteTokens = 2_000 },
                new UsageRecord { Timestamp = t.AddHours(3), SessionId = "a", ModelUsed = "claude-sonnet-5-5", CacheReadTokens = 3_000, CacheWriteTokens = 50_000, CacheWrite1hTokens = 50_000 },
                // b: a 5-minute rewrite on Opus
                new UsageRecord { Timestamp = t, SessionId = "b", ModelUsed = "claude-opus-5-5", InputTokens = 10 },
                new UsageRecord { Timestamp = t.AddMinutes(1), SessionId = "b", ModelUsed = "claude-opus-5-5", CacheWriteTokens = 20_000 },
                // no session id: not judged; a small write: not counted
                new UsageRecord { Timestamp = t, CacheWriteTokens = 90_000 },
                new UsageRecord { Timestamp = t.AddMinutes(9), SessionId = "a", ModelUsed = "claude-sonnet-5-5", CacheWriteTokens = 9_000 },
            };
            var rep = SavingsReport.Build(recs, new PriceTable());
            Eq(2, rep.CacheRewriteTurns, "a's third turn and b's second");
            Eq(70_000L, rep.CacheRewriteTokens, "tokens");
            // Sonnet 1-hour: 50k × (2×2 − 0.1) = $0.195; Opus 5-minute: 20k × (5 − 0.2) = $0.096
            True(Math.Abs(rep.CacheRewriteExtraUsd - 0.291) < 1e-9, "extra: " + rep.CacheRewriteExtraUsd);
            True(rep.ToText().Contains("Cache written again:        $0.29 more than reading it  (2 turns, 70.0k tokens:"), rep.ToText());
            Eq(0, SavingsReport.Build(recs.Take(2), new PriceTable()).CacheRewriteTurns, "none");
            True(!SavingsReport.Build(recs.Take(2), new PriceTable()).ToText().Contains("Cache written again"), "no line");
        }

        private static void Test_SavingsReport_ByEffort()
        {
            var t = DateTime.UtcNow.AddDays(-1);
            var recs = new[]
            {
                new UsageRecord { Timestamp = t, Effort = "high", CostUsd = 0.30, ThinkingTokens = 12_000 },
                new UsageRecord { Timestamp = t, Effort = "high", CostUsd = 0.10, RouterCostUsd = 0.001, ThinkingTokens = 3_000 },
                new UsageRecord { Timestamp = t, Effort = "low", CostUsd = 0.02 },
                new UsageRecord { Timestamp = t, RouteEffort = "medium", CostUsd = 0.05 }, // older routed line: its route effort
                new UsageRecord { Timestamp = t, CostUsd = 0.07 },                         // Claude Code's default
                new UsageRecord { Timestamp = t, Effort = "max", CostUsd = 1.00, ThinkingTokens = 40_000 },
            };
            var rep = SavingsReport.Build(recs, new PriceTable());
            Eq("low,medium,high,max,default", string.Join(",", rep.ByEffort.Select(b => b.Effort)), "low to max, then the default");
            var high = rep.ByEffort[2];
            Eq(2, high.Turns, "turns");
            True(Math.Abs(high.CostUsd - 0.401) < 1e-9, "router cost included: " + high.CostUsd);
            Eq(15_000L, high.ThinkingTokens, "thinking");
            string text = rep.ToText();
            True(text.Contains("By effort:\r\n") || text.Contains("By effort:\n"), text);
            True(text.Contains("  high        2 turns  $    0.40  ($0.201 per turn)  15.0k thinking"), text);
            True(text.Contains("  default     1 turns  $    0.07  ($0.070 per turn)\r\n") || text.Contains("  default     1 turns  $    0.07  ($0.070 per turn)\n"), text);
            Eq(0, SavingsReport.Build(new[] { recs[4] }, new PriceTable()).ByEffort.Count, "no effort known: no list");
        }

        private static void Test_SavingsReport_ToolResults()
        {
            var t = DateTime.UtcNow.AddDays(-1);
            var recs = new[]
            {
                new UsageRecord { Timestamp = t, CostUsd = 0.1, ToolResultTokens = new Dictionary<string, long> { ["Read"] = 900_000, ["Shell"] = 50_000 } },
                new UsageRecord { Timestamp = t, CostUsd = 0.1, ToolResultTokens = new Dictionary<string, long> { ["Read"] = 300_000, ["Search"] = 120_000, ["MCP"] = 4_000, ["WebFetch"] = 3_000, ["Agent"] = 2_000, ["Edit"] = 1_000 } },
                new UsageRecord { Timestamp = t, CostUsd = 0.1 }, // an older line
            };
            var rep = SavingsReport.Build(recs, new PriceTable());
            Eq("Read 1.2M · Search 120.0k · Shell 50.0k · MCP 4.0k · WebFetch 3.0k · other 3.0k", rep.ToolResultsLine());
            True(rep.ToText().Contains("Tool results:               Read 1.2M"), rep.ToText());
            Eq(null, SavingsReport.Build(new[] { recs[2] }, new PriceTable()).ToolResultsLine(), "none logged");

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(recs[0], new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
            True(json.Contains("\"tools\":{\"Read\":900000,\"Shell\":50000}"), json);
            True(!Newtonsoft.Json.JsonConvert.SerializeObject(recs[2], new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore }).Contains("tools"), "left out when none");
        }

        private static void Test_ResumeNote()
        {
            var now = new DateTime(2026, 11, 6, 15, 0, 0, DateTimeKind.Utc);
            Eq(null, CacheWindow.ResumeNote(now.AddMinutes(-40), 50_000, now), "within the hour: may still be warm");
            Eq(null, CacheWindow.ResumeNote(now.AddHours(-5), 4_000, now), "small context");
            Eq("This conversation was last active 5 hours ago, so its prompt cache has expired: the first prompt sends ~45.0k tokens at the full input price. For a new task, a New chat is cheaper.",
                CacheWindow.ResumeNote(now.AddHours(-5), 45_000, now));
            True(CacheWindow.ResumeNote(now.AddDays(-3), 45_000, now).Contains("last active 3 days ago"), "days");
            True(CacheWindow.ResumeNote(now.AddMinutes(-70), 45_000, now).Contains("last active over an hour ago"), "just past the hour");

            // A session file: the last main answer's usage is the context; a compaction resets it until the next answer.
            string file = Path.Combine(Path.GetTempPath(), "cvs-resume-" + Guid.NewGuid().ToString("N") + ".jsonl");
            try
            {
                File.WriteAllLines(file, new[]
                {
                    "{\"type\":\"user\",\"uuid\":\"u1\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}",
                    "{\"type\":\"assistant\",\"uuid\":\"a1\",\"message\":{\"id\":\"m1\",\"content\":[{\"type\":\"text\",\"text\":\"hello\"}],\"usage\":{\"input_tokens\":4,\"cache_read_input_tokens\":30000,\"cache_creation_input_tokens\":2000,\"output_tokens\":10}}}",
                    "{\"type\":\"assistant\",\"uuid\":\"s1\",\"isSidechain\":true,\"message\":{\"id\":\"m2\",\"content\":[],\"usage\":{\"input_tokens\":99999}}}",
                });
                Eq(32_004L, SessionTranscript.Read(file).LastContextTokens, "last main answer; subagent lines skipped");
                File.AppendAllLines(file, new[] { "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"uuid\":\"c1\",\"compactMetadata\":{\"preTokens\":32004,\"trigger\":\"manual\"}}" });
                Eq(0L, SessionTranscript.Read(file).LastContextTokens, "right after a compaction: unknown");
            }
            finally { try { File.Delete(file); } catch (IOException) { } }
        }

        // ---------- Conversation cost warning ----------
        private static void Test_NextPromptPrice()
        {
            var t0 = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
            var prices = new PriceTable();
            var cache = new CacheWindow();
            cache.Use(new TokenUsage { CacheRead = 90_000, CacheCreation = 10_000, CacheCreation5m = 10_000 }, t0); // 100k context, 5-minute cache

            var warm = NextPromptPrice.Estimate(cache, "claude-sonnet-5-5", null, 1000, prices, t0.AddMinutes(1));
            True(warm.Cached && !warm.Expired && !warm.OtherModel, "warm, same model");
            True(Math.Abs(warm.InputUsd - (100_000 * 0.1 + 1000 * 2.5) / 1e6) < 1e-12, "context at cache read, prompt at cache write: " + warm.InputUsd);
            Eq("~$0.01 to send (context cached)", warm.Note(), "note: 100k cached tokens at Sonnet's $0.10");

            var cold = NextPromptPrice.Estimate(cache, "claude-sonnet-5-5", "sonnet", 1000, prices, t0.AddMinutes(6));
            True(cold.Expired && !cold.Cached, "expired after 5 minutes");
            True(Math.Abs(cold.InputUsd - 101_000 * 2.5 / 1e6) < 1e-12, "all written again: " + cold.InputUsd);
            Eq("~$0.25 to send (cache expired: ~100.0k tokens of context at the full price)", cold.Note(), "cold note");

            var other = NextPromptPrice.Estimate(cache, "claude-sonnet-5-5", "opus", 0, prices, t0.AddMinutes(1));
            True(other.OtherModel && !other.Cached && !other.Expired, "another model has no cache");
            True(Math.Abs(other.InputUsd - 100_000 * 5.0 / 1e6) < 1e-12, "Opus cache write: " + other.InputUsd);
            True(other.Note().StartsWith("~$0.50 to send (another model:"), other.Note());

            var hour = new CacheWindow();
            hour.Use(new TokenUsage { CacheCreation = 40_000, CacheCreation1h = 40_000 }, t0);
            var h = NextPromptPrice.Estimate(hour, "claude-haiku-5-5", null, 0, prices, t0.AddMinutes(61));
            True(h.Expired && Math.Abs(h.InputUsd - 40_000 * 0.2 / 1e6) < 1e-12, "1-hour write at 2x Haiku input: " + h.InputUsd);
            Eq("<$0.01 to send (cache expired: ~40.0k tokens of context at the full price)", h.Note(), "small amount");

            var meter = new TokenMeter();
            meter.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Usage = new TokenUsage { CacheCreation = 5000 }, Model = "claude-opus-5-5", MessageId = "a" });
            Eq("claude-opus-5-5", meter.LastModel, "the cached model");
            meter.Reset();
            Eq(null, meter.LastModel, "reset");
        }

        private static void Test_SavingsReport_Month_And_Solutions()
        {
            DateTime At(int month, int day) => new DateTime(2026, month, day, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var recs = new[]
            {
                new UsageRecord { Timestamp = At(10, 30), Solution = @"C:\src\old", CostUsd = 50 },              // last month
                new UsageRecord { Timestamp = At(11, 2), Solution = @"C:\src\shop\", CostUsd = 6, RouterCostUsd = 0.5 },
                new UsageRecord { Timestamp = At(11, 9), Solution = @"c:\src\SHOP", CostUsd = 2.5 },
                new UsageRecord { Timestamp = At(11, 9), Solution = null, CostUsd = 1 },
            };
            var rep = SavingsReport.Build(recs, new PriceTable(), nowUtc: At(11, 10));
            True(Math.Abs(rep.MonthToDateUsd - 10) < 1e-9, "November so far, router included: " + rep.MonthToDateUsd);
            True(rep.MonthProjectedUsd.HasValue && Math.Abs(rep.MonthProjectedUsd.Value - 30) < 1e-9, "$10 in 10 days → $30 in 30: " + rep.MonthProjectedUsd);
            True(rep.MonthLine().Contains("so far") && rep.MonthLine().Contains("at this pace by"), rep.MonthLine());

            Eq(3, rep.BySolution.Count, "one folder whatever its case or trailing slash, plus none");
            Eq("old", rep.BySolution[0].Name, "dearest first");
            Eq("shop", rep.BySolution[1].Name, "folder name");
            Eq(3, rep.BySolution.Sum(b => b.Turns) - 1, "all turns: 4");
            Eq("(no folder)", rep.BySolution[2].Name, "no folder");
            True(rep.ToText().Contains("By solution:"), "in the all-solutions report");
            True(!SavingsReport.Build(recs, new PriceTable(), solution: @"C:\src\old", nowUtc: At(11, 10)).ToText().Contains("By solution:"), "not in one solution's report");

            var early = SavingsReport.Build(recs, new PriceTable(), nowUtc: At(11, 2));
            True(early.MonthToDateUsd > 0 && early.MonthProjectedUsd == null, "no projection on day 2");
            True(!early.MonthLine().Contains("pace"), early.MonthLine());
            Eq(null, SavingsReport.Build(recs, new PriceTable(), nowUtc: At(12, 5)).MonthLine(), "nothing in December yet");
        }

        private static void Test_SavingsReport_CostliestConversations()
        {
            var t = new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var recs = new[]
            {
                new UsageRecord { Timestamp = t, SessionId = "a", Solution = @"C:\src\shop", CostUsd = 0.40, PromptPreview = null },
                new UsageRecord { Timestamp = t.AddMinutes(5), SessionId = "a", Solution = @"C:\src\shop", CostUsd = 0.50, PromptPreview = "fix the\ncart total", RouterCostUsd = 0.10 },
                new UsageRecord { Timestamp = t.AddMinutes(1), SessionId = "b", Solution = @"C:\src\blog", CostUsd = 0.70 },
                new UsageRecord { Timestamp = t.AddMinutes(2), CostUsd = 9 },                // older line: no session id
                new UsageRecord { Timestamp = t.AddMinutes(3), SessionId = "c", CostUsd = 0 }, // nothing spent
            };
            var rep = SavingsReport.Build(recs, new PriceTable());
            Eq("a,b", string.Join(",", rep.CostliestConversations.Select(b => b.SessionId)), "dearest first; no id and $0 left out");
            var a = rep.CostliestConversations[0];
            Eq(2, a.Turns, "turns");
            True(Math.Abs(a.CostUsd - 1.0) < 1e-9, "router cost included: " + a.CostUsd);
            Eq(t, a.Started, "its first turn");
            Eq("fix the\ncart total", a.FirstPrompt, "the first prompt logged");
            string text = rep.ToText();
            True(text.Contains("Costliest conversations:"), text);
            True(text.Contains("$  1.00     2 turns  shop  “fix the cart total”"), text);
            True(text.Contains("$  0.70     1 turn   blog"), text);
            True(!SavingsReport.Build(recs, new PriceTable(), solution: @"C:\src\shop").ToText().Contains("Costliest conversations:"), "one conversation: no list");
            Eq("shop", SolutionBreakdown.NameOf(@"C:\src\shop\"), "folder name");
        }

        private static void Test_SavingsReport_LastMonth()
        {
            DateTime At(int month, int day) => new DateTime(2026, month, day, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var recs = new[]
            {
                new UsageRecord { Timestamp = At(10, 3), CostUsd = 6, RouterCostUsd = 0.5 },
                new UsageRecord { Timestamp = At(10, 11), CostUsd = 2.5 },
                new UsageRecord { Timestamp = At(10, 12), CostUsd = 100 },   // after the same day: not compared
                new UsageRecord { Timestamp = At(11, 2), CostUsd = 12.33 },
            };
            var rep = SavingsReport.Build(recs, new PriceTable(), nowUtc: At(11, 11));
            Eq(new DateTime(2026, 10, 11), rep.LastMonthToDay, "Oct 1–11");
            True(Math.Abs(rep.LastMonthToDateUsd - 9) < 1e-9, "router cost included: " + rep.LastMonthToDateUsd);
            True(rep.LastMonthText().EndsWith(" 1–11: $9.00, +37%)"), rep.LastMonthText());
            True(rep.MonthLine().EndsWith(rep.LastMonthText()) && rep.MonthLine().Contains("at this pace"), rep.MonthLine());

            var lower = SavingsReport.Build(new[] { recs[0], new UsageRecord { Timestamp = At(11, 1), CostUsd = 3.25 } }, new PriceTable(), nowUtc: At(11, 11));
            True(lower.LastMonthText().EndsWith("$6.50, −50%)"), lower.LastMonthText());
            Eq(null, SavingsReport.Build(new[] { recs[3] }, new PriceTable(), nowUtc: At(11, 11)).LastMonthText(), "nothing last month");

            // March 31 against February: its last day
            var march = SavingsReport.Build(new[] { new UsageRecord { Timestamp = At(3, 30), CostUsd = 1 } }, new PriceTable(), nowUtc: At(3, 31));
            Eq(new DateTime(2026, 2, 28), march.LastMonthToDay, "February is shorter");
            // January against December of the year before
            Eq(new DateTime(2025, 12, 15), SavingsReport.Build(recs, new PriceTable(), nowUtc: new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Local).ToUniversalTime()).LastMonthToDay, "across the year");
        }

        private static void Test_SlashPicker_Matching()
        {
            var cmds = new List<SlashCommand>
            {
                new SlashCommand { Name = "compact", Source = "built-in" },
                new SlashCommand { Name = "anthropic-skills:docs", Source = "skill" },
                new SlashCommand { Name = "anthropic-skills:docx", Source = "skill" },
                new SlashCommand { Name = "docs-review", Source = "project" },
            };
            Eq("/docs-review,/anthropic-skills:docs", string.Join(",", SlashCommands.Match(cmds, "/docs")), "whole-name matches first, then the part after the plugin");
            Eq("/anthropic-skills:docs,/anthropic-skills:docx", string.Join(",", SlashCommands.Match(cmds, "/anthropic-skills:doc")), "full name typed");
            Eq(4, SlashCommands.Match(cmds, "/").Count, "all for a bare slash");
            Eq("/compact,/docs-review", string.Join(",", SlashCommands.Match(SlashCommands.ForPicker(cmds), "/")), "the list while typing leaves skills out");

            Eq("anthropic-skills:", cmds[1].Prefix, "prefix");
            Eq("docs", cmds[1].ShortName, "short name");
            Eq("", cmds[0].Prefix, "no prefix");
            Eq("compact", cmds[0].ShortName, "short = name");
            Eq("Built-in", cmds[0].SourceLabel, "label");

            Eq("doc", SlashCommands.TypedName("  /doc"), "typing the name");
            Eq("", SlashCommands.TypedName("/"), "just the slash");
            Eq(null, SlashCommands.TypedName("/docs write a memo"), "arguments: no list");
            Eq(null, SlashCommands.TypedName("/docs\n"), "line break: no list");
            Eq(null, SlashCommands.TypedName("fix /this"), "not a command");
        }

        private static void Test_Prices_MatchTheCliCosts()
        {
            // Two real turns per model with Claude Code 2.1.322 (trimmed: no tools, a ~4.5k-token system prompt cached
            // for 1 hour); the default prices must give the CLI's own costUSD for the same tokens
            var prices = new PriceTable();
            var seen = new List<string>();
            foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.322-model-costs.jsonl")).Where(l => l.Trim().Length > 0))
            {
                foreach (var kv in (JObject)JObject.Parse(line)["modelUsage"])
                {
                    var m = (JObject)kv.Value;
                    var u = new TokenUsage
                    {
                        Input = (long)m["inputTokens"], Output = (long)m["outputTokens"], CacheRead = (long)m["cacheReadInputTokens"],
                        CacheCreation = (long)m["cacheCreationInputTokens"], CacheCreation1h = (long)m["cacheCreationEphemeral1hInputTokens"],
                    };
                    double ours = prices.For(kv.Key).Cost(u), cli = (double)m["costUSD"];
                    True(Math.Abs(ours - cli) < 1e-9, $"{kv.Key}: ours ${ours:0.0000000} vs the CLI's ${cli:0.0000000}");
                    seen.Add(PriceTable.Family(kv.Key));
                }
            }
            Eq("haiku,sonnet,opus,fable", string.Join(",", seen), "all four families checked");
            Eq(0.1, prices.Sonnet.CacheRead, "Sonnet 5.5 cache reads: $0.10");

            // A Sonnet price saved with the old default is upgraded; one you typed stays
            Eq(PriceTable.DefaultSonnet, PriceTable.UpgradeStored("2, 10, 2.5, 0.2"), "old default");
            Eq(PriceTable.DefaultSonnet, PriceTable.UpgradeStored("2,10,2.5,0.20"), "old default, other spacing");
            Eq("3, 15, 3.75, 0.3", PriceTable.UpgradeStored("3, 15, 3.75, 0.3"), "your own price");
            Eq("junk", PriceTable.UpgradeStored("junk"), "unreadable: left alone");
        }

        private static void Test_SubagentUsage_FromRealFork()
        {
            // Replay the fork capture (2.1.319) through the parser and the meter
            var parser = new ClaudeStreamParser();
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-fork.jsonl"))
                .SelectMany(l => parser.Parse(l)).ToList();
            var sub = events.First(e => e.Kind == ClaudeEventKind.AssistantUsage && e.IsSubagent);
            True(sub.ParentToolUseId != null && sub.ParentToolUseId.StartsWith("toolu"), "the subagent's Agent call: " + sub.ParentToolUseId);
            var done = events.Last(e => e.Kind == ClaudeEventKind.TurnCompleted);
            True(done.ModelUsage.ContainsKey("claude-haiku-5-5") && done.ModelUsage["claude-haiku-5-5"].CostUsd > 0, "modelUsage parsed");

            // First turn this meter sees: from the subagent's streamed message (input and cache exact)
            var m = new TokenMeter();
            foreach (var e in events) m.Observe(e);
            var first = m.LastTurnSubagents["claude-haiku-5-5"];
            Eq(23_917L, first.CacheRead, "subagent cache reads");
            Eq(462L, first.CacheCreation, "subagent cache writes");
            Eq(4L, first.Input, "subagent input");
            True(m.LastTurnSubagentCostUsd > 0, "priced");
            Eq(first.Total, m.SessionSubagentTokens, "conversation total");

            // The same run as a new session (not resumed): modelUsage counts from 0, so the first turn is exact, output too
            var fresh = new TokenMeter();
            fresh.FreshSession();
            foreach (var e in events) fresh.Observe(e);
            var exactFirst = fresh.LastTurnSubagents["claude-haiku-5-5"];
            Eq(131L, exactFirst.Output, "output exact (the streamed copy said 2)");
            Eq(4L, exactFirst.Input); Eq(23_917L, exactFirst.CacheRead); Eq(462L, exactFirst.CacheCreation);
            True(first.Output < exactFirst.Output, "resumed: streamed, output low");
            // Resumed, with the session's last reading kept here: the growth from it agrees with the streamed input and
            // cache, so the output is exact too (the fork run's reading before the turn was 0)
            var resumed = new TokenMeter();
            resumed.ResumedSession(new Dictionary<string, TokenUsage> { ["claude-haiku-5-5"] = default });
            foreach (var e in events) resumed.Observe(e);
            Eq(131L, resumed.LastTurnSubagents["claude-haiku-5-5"].Output, "resumed with a reading: exact");

            // A reading the session has outgrown elsewhere (the terminal): the growth takes that work too, so it does not
            // agree with the streamed messages and they count instead
            var stale = new SubagentUsageTracker();
            var main = new TokenUsage { Input = 4, Output = 200, CacheRead = 30_000 };
            stale.ResumeFrom(new Dictionary<string, TokenUsage> { ["m"] = new TokenUsage { Input = 100, Output = 1000, CacheRead = 50_000 } });
            stale.ObserveSubagent(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, IsSubagent = true, MessageId = "s1", Model = "m", Usage = new TokenUsage { Input = 3, Output = 1, CacheRead = 9_000 } });
            var elsewhere = stale.TurnCompleted(new Dictionary<string, ModelUsageEntry> { ["m"] = new ModelUsageEntry { Usage = new TokenUsage { Input = 100 + 4 + 3 + 50, Output = 1000 + 200 + 90 + 400, CacheRead = 50_000 + 30_000 + 9_000 + 20_000 } } }, "m", main);
            Eq(1L, elsewhere["m"].Output, "streamed: the reading was out of date");
            Eq(9_000L, elsewhere["m"].CacheRead);
            var current = new SubagentUsageTracker();
            current.ResumeFrom(new Dictionary<string, TokenUsage> { ["m"] = new TokenUsage { Input = 100, Output = 1000, CacheRead = 50_000 } });
            current.ObserveSubagent(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, IsSubagent = true, MessageId = "s1", Model = "m", Usage = new TokenUsage { Input = 3, Output = 1, CacheRead = 9_000 } });
            var agreed = current.TurnCompleted(new Dictionary<string, ModelUsageEntry> { ["m"] = new ModelUsageEntry { Usage = new TokenUsage { Input = 100 + 4 + 3, Output = 1000 + 200 + 90, CacheRead = 50_000 + 30_000 + 9_000 } } }, "m", main);
            Eq(90L, agreed["m"].Output, "current reading: exact output");

            // The readings file: kept per session, read back, the oldest dropped past the limit
            string readingsPath = Path.Combine(Path.GetTempPath(), "cvs-model-usage-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var store = new ModelUsageReadings(readingsPath);
                Eq(null, store.Get("s"), "none yet");
                store.Save("s", done.ModelUsage);
                var back = store.Get("s")["claude-haiku-5-5"];
                Eq(done.ModelUsage["claude-haiku-5-5"].Usage.Output, back.Output); Eq(done.ModelUsage["claude-haiku-5-5"].Usage.CacheCreation1h, back.CacheCreation1h);
                var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                for (int i = 0; i < ModelUsageReadings.MaxSessions; i++) store.Save("x" + i, done.ModelUsage, t0.AddMinutes(i));
                Eq(null, store.Get("x0"), "the oldest dropped");
                True(store.Get("s") != null && store.Get("x1") != null, "the rest kept");
            }
            finally { try { File.Delete(readingsPath); } catch (IOException) { } }

            // task_notification's total_tokens is not the API's count: 24,649 against 24,514, so it cannot fix the output
            long reported = (long)events.Last(e => e.Kind == ClaudeEventKind.Task && (string)e.Payload["subtype"] == "task_notification").Payload["usage"]["total_tokens"];
            Eq(24_649L, reported); Eq(24_514L, exactFirst.Total);

            // A later turn: modelUsage's growth less the main agent's usage, exact (output too)
            var tr = new SubagentUsageTracker();
            var u1 = new Dictionary<string, ModelUsageEntry> { ["claude-haiku-5-5"] = new ModelUsageEntry { Usage = new TokenUsage { Input = 4, Output = 204, CacheRead = 38_169, CacheCreation = 12_175 } } };
            Eq(0, tr.TurnCompleted(u1, "claude-haiku-5-5", new TokenUsage { Input = 4, Output = 204, CacheRead = 38_169, CacheCreation = 12_175 }).Count, "a turn without agents");
            var u2 = new Dictionary<string, ModelUsageEntry> { ["claude-haiku-5-5"] = new ModelUsageEntry { Usage = new TokenUsage { Input = 12, Output = 539, CacheRead = 100_255, CacheCreation = 24_812 } } };
            var agents = tr.TurnCompleted(u2, "claude-haiku-5-5", new TokenUsage { Input = 4, Output = 204, CacheRead = 38_169, CacheCreation = 12_175 });
            var a = agents["claude-haiku-5-5"];
            Eq(4L, a.Input); Eq(131L, a.Output, "output exact"); Eq(23_917L, a.CacheRead); Eq(462L, a.CacheCreation);

            // A subagent on another model, and a fresh process whose counts start again
            var u3 = new Dictionary<string, ModelUsageEntry>
            {
                ["claude-haiku-5-5"] = u2["claude-haiku-5-5"],
                ["claude-sonnet-5-5"] = new ModelUsageEntry { Usage = new TokenUsage { Input = 10, Output = 50, CacheRead = 5_000 } },
            };
            var other = tr.TurnCompleted(u3, "claude-haiku-5-5", default);
            Eq(1, other.Count, "only the Sonnet agent grew");
            Eq(5_060L, other["claude-sonnet-5-5"].Total);

            // The report: Opus equivalent and cache savings take the agents too
            var prices = new PriceTable();
            var rec = new UsageRecord { ModelUsed = "claude-haiku-5-5", CacheReadTokens = 1_000_000, CostUsd = 0.01 + 0.1,
                Agents = new List<AgentTokens> { new AgentTokens { Model = "claude-sonnet-5-5", CacheReadTokens = 1_000_000 } } };
            var rep = SavingsReport.Build(new[] { rec }, prices);
            True(Math.Abs(rep.OpusEquivalentUsd - (0.2 + 0.2)) < 1e-9, "main 1M reads at Opus + agent 1M reads at Opus: " + rep.OpusEquivalentUsd);
            True(Math.Abs(rep.CacheSavedUsd - ((0.1 - 0.01) + (2.0 - 0.1))) < 1e-9, "both agents' cache savings: " + rep.CacheSavedUsd);
            Eq(2_000_000L, rep.CacheReadTokens, "reads of both");
            Eq(1, rep.AgentTurns);
            True(rep.ToText().Contains("Subagents:                  1.0M tokens ≈ $0.10  (1 turn; included above)"), rep.ToText());
            Eq(1_000_000L, rec.AgentTokensTotal);
            True(Newtonsoft.Json.JsonConvert.SerializeObject(rec).Contains("\"agents\":[{\"model\":\"claude-sonnet-5-5\""), "logged");
            True(!Newtonsoft.Json.JsonConvert.SerializeObject(new UsageRecord()).Contains("agents"), "none: not written");
            Eq(null, rep.AgentByTypeLine(), "no kinds logged: no line");

            // By kind of agent: the fork run is all Fork, its share is the whole exact agent cost
            var forkTypes = fresh.LastTurnAgentTypes;
            Eq(1, forkTypes.Count); Eq("Fork", forkTypes[0].Type); Eq(1.0, forkTypes[0].Share);
            True(Math.Abs(forkTypes[0].CostUsd - fresh.LastTurnSubagentCostUsd) < 1e-12, "the exact cost, not the streamed one");

            // Two kinds in one turn: shares from their priced requests; a type from task_started when the call had none
            var split = new AgentTypeSplit();
            Func<string, TokenUsage, double> perToken = (model, u) => u.Total; // whole numbers: exact shares
            split.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolName = "Agent", ToolUseId = "a", ToolInput = new JObject { ["subagent_type"] = "Explore" } }, perToken);
            split.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolName = "Agent", ToolUseId = "b", ToolInput = new JObject() }, perToken);
            split.Observe(new ClaudeEvent { Kind = ClaudeEventKind.Task, Payload = JObject.Parse("{\"type\":\"system\",\"subtype\":\"task_started\",\"tool_use_id\":\"b\",\"subagent_type\":\"general-purpose\"}") }, perToken);
            ClaudeEvent Req(string call, string id, long tokens) => new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, IsSubagent = true, ParentToolUseId = call, MessageId = id, Model = "claude-sonnet-5-5", Usage = new TokenUsage { CacheRead = tokens } };
            split.Observe(Req("a", "m1", 100_000), perToken);
            split.Observe(Req("a", "m1", 300_000), perToken); // the same message again: its last copy counts
            split.Observe(Req("b", "m2", 100_000), perToken);
            var kinds = split.TurnCompleted(0.80);
            Eq("Explore", kinds[0].Type, "costliest first"); Eq(0.75, kinds[0].Share); True(Math.Abs(kinds[0].CostUsd - 0.60) < 1e-12, "Explore: $0.60");
            Eq("Agent", ToolCallView.AgentLabel(null)); Eq("general-purpose", kinds[1].Type, "from task_started"); True(Math.Abs(kinds[1].CostUsd - 0.20) < 1e-12, "general-purpose: $0.20");
            Eq(null, split.TurnCompleted(0.5), "the next turn: none ran");

            // The report: each turn's agent cost at its prices, split by its shares; older turns under "not known"
            var typed = new UsageRecord { ModelUsed = "claude-haiku-5-5", CostUsd = 0.1, AgentTypes = kinds,
                Agents = new List<AgentTokens> { new AgentTokens { Model = "claude-sonnet-5-5", CacheReadTokens = 1_000_000 } } };
            var both = SavingsReport.Build(new[] { rec, typed }, prices);
            True(Math.Abs(both.AgentCostByType["Explore"] - 0.075) < 1e-12 && Math.Abs(both.AgentCostByType["general-purpose"] - 0.025) < 1e-12
                && Math.Abs(both.AgentCostByType[SavingsReport.NotKnownType] - 0.10) < 1e-12, "split at the report's prices");
            True(both.AgentByTypeLine().StartsWith("Explore $") && both.AgentByTypeLine().Contains(" · general-purpose $") && both.AgentByTypeLine().EndsWith(" · not known $" + 0.10.ToString("0.00")), both.AgentByTypeLine());
            True(both.ToText().Contains("  by kind of agent:         Explore"), both.ToText());
            // By model: each agent at its own model's price; the Opus ones also at Sonnet's
            var opusAgent = new UsageRecord { ModelUsed = "claude-haiku-5-5", CostUsd = 0.2,
                Agents = new List<AgentTokens> { new AgentTokens { Model = "claude-opus-5-5", CacheReadTokens = 1_000_000 } } };
            var byModel = SavingsReport.Build(new[] { rec, opusAgent }, prices);
            True(Math.Abs(byModel.AgentCostByModel["opus"] - 0.2) < 1e-12 && Math.Abs(byModel.AgentCostByModel["sonnet"] - 0.1) < 1e-12, "opus $0.20, sonnet $0.10");
            True(Math.Abs(byModel.OpusAgentsOnSonnetUsd - 0.1) < 1e-12, "the Opus agent's reads at Sonnet prices");
            True(byModel.AgentByModelLine().StartsWith("opus $") && byModel.AgentByModelLine().Contains(" on Sonnet) · sonnet $"), byModel.AgentByModelLine());
            True(byModel.ToText().Contains("  by model:                 opus $"), byModel.ToText());
            Eq(null, SavingsReport.Build(new[] { new UsageRecord { ModelUsed = "claude-haiku-5-5", CostUsd = 0.01 } }, prices).AgentByModelLine(), "no agents");

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(typed);
            True(json.Contains("\"agentTypes\":[{\"type\":\"Explore\",\"share\":0.75}"), json);
            Eq(0.75, Newtonsoft.Json.JsonConvert.DeserializeObject<UsageRecord>(json).AgentTypes[0].Share, "read back");
        }

        private static void Test_TurnUsage_IsMainAgentOnly()
        {
            // The fork capture (2.1.319): the result's usage is the main agent's; modelUsage and the cost add the
            // subagent. So a turn's long-context share must not take subagent requests (they are not in its tokens);
            // their tier is priced where their tokens are counted: the sessions list, per request.
            var result = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-fork.jsonl"))
                .Select(l => l.Trim()).Where(l => l.StartsWith("{")).Select(JObject.Parse).Last(o => (string)o["type"] == "result");
            var main = TokenUsage.From(result["usage"]);
            var all = (JObject)((JObject)result["modelUsage"]).Properties().Single().Value;
            True((long)all["cacheReadInputTokens"] > main.CacheRead, $"subagent reads not in the turn's usage: {main.CacheRead} < {all["cacheReadInputTokens"]}");
            True((long)all["outputTokens"] > main.Output, "nor its output");

            var m = new TokenMeter();
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Model = "claude-haiku-5-5", MessageId = "main", Usage = new TokenUsage { CacheRead = 50_000, Output = 10 } });
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Model = "claude-haiku-5-5", MessageId = "sub", IsSubagent = true, Usage = new TokenUsage { CacheRead = 150_000, Output = 10 } });
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { CacheRead = 50_000, Output = 10 } });
            Eq(0L, m.LastTurn.LargeContextTokens, "a subagent request over 100k does not count in the main agent's turn");
        }

        private static void Test_Prices_HaikuOver100k()
        {
            // Real turns with 2.1.322: a 74k-token prompt then a short one (under the tier), and the same with 111k
            // (over it). Each turn is one request; the CLI's total grows by exactly what our price gives for it.
            var haiku = new PriceTable().Haiku;
            var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.322-haiku-over-100k.jsonl")).Where(l => l.Trim().Length > 0).Select(JObject.Parse).ToList();
            Eq(4, lines.Count);
            for (int i = 0; i < 4; i += 2)
            {
                double before = 0;
                for (int k = i; k < i + 2; k++)
                {
                    var u = TokenUsage.From(lines[k]["usage"]).AsRequest();
                    double cli = (double)lines[k]["total_cost_usd"] - before, ours = haiku.Cost(u);
                    True(Math.Abs(ours - cli) < 1e-9, $"turn with {u.ContextTokens:N0} input tokens: ours ${ours:0.0000000}, the CLI's ${cli:0.0000000}");
                    Eq(u.ContextTokens > 100_000, u.LargeContextTokens > 0, "over 100k: " + u.ContextTokens);
                    before = (double)lines[k]["total_cost_usd"];
                }
            }

            // Only Haiku has the tier; 100,000 exactly is still the normal price
            var big = new TokenUsage { CacheRead = 150_000, Output = 10 }.AsRequest();
            Eq(new PriceTable().Sonnet.Cost(new TokenUsage { CacheRead = 150_000, Output = 10 }), new PriceTable().Sonnet.Cost(big), "Sonnet: no tier");
            Eq(1.0, haiku.RequestMultiplier(100_000), "100k exactly: normal");
            Eq(5.0, haiku.RequestMultiplier(100_001), "above: 5x");

            // A turn's sum: only the requests over 100k count (two requests: 60k and 120k of context)
            var m = new TokenMeter();
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Model = "claude-haiku-5-5", MessageId = "a", Usage = new TokenUsage { CacheRead = 60_000, Output = 10 } });
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Model = "claude-haiku-5-5", MessageId = "b", Usage = new TokenUsage { CacheRead = 120_000, Output = 10 } });
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Model = "claude-haiku-5-5", MessageId = "b", Usage = new TokenUsage { CacheRead = 120_000, Output = 20 } }); // same message again
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { CacheRead = 180_000, Output = 30 } });
            Eq(120_020L, m.LastTurn.LargeContextTokens, "the 120k request, its last copy");
            double turnCost = haiku.Cost(m.LastTurn);
            True(Math.Abs(turnCost - (60_000 * 0.01 + 10 * 0.5 + (120_000 * 0.01 + 20 * 0.5) * 5) / 1e6) < 1e-6, "priced near per request: " + turnCost);

            // The next prompt's price and the tip
            var cache = new CacheWindow();
            cache.Use(new TokenUsage { CacheRead = 95_000, CacheCreation = 1000, CacheCreation1h = 1000 }, DateTime.UtcNow);
            var over = NextPromptPrice.Estimate(cache, "claude-haiku-5-5", "claude-haiku-5-5", 10_000, new PriceTable(), DateTime.UtcNow);
            True(Math.Abs(over.InputUsd - (96_000 * 0.01 + 10_000 * 0.2) * 5 / 1e6) < 1e-9, "96k cached + 10k prompt passes 100k: 5x: " + over.InputUsd);
            Eq(null, SessionNotices.HaikuLongContext("claude-haiku-5-5", 89_999), "below 90k: no tip");
            True(SessionNotices.HaikuLongContext("claude-haiku-5-5", 92_000)?.Contains("5x its prices") == true, "near 100k: tip");
            Eq(null, SessionNotices.HaikuLongContext("claude-sonnet-5-5", 150_000), "not Haiku: no tip");

            // Reading a big conversation from the cache: most of a short turn's cost
            var prices = new PriceTable();
            var shortTurn = new TokenUsage { Input = 20, Output = 300, CacheRead = 150_000, CacheCreation = 400 };
            // Opus: reads $0.030, the turn 150k×0.2 + 20×4 + 300×20 + 400×5 per Mtok = $0.0381
            Eq("Reading this conversation (~150.4k tokens) back from the cache cost $0.03 of that turn's $0.04: every request sends the whole conversation again. "
                + "For a new topic, a New chat (or /clear) starts small; /compact keeps a summary of this one.",
                SessionNotices.PriceyContext("claude-opus-5-5", shortTurn, 150_400, prices));
            Eq(null, SessionNotices.PriceyContext("claude-sonnet-5-5", shortTurn, 150_400, prices), "Sonnet: $0.015 of reads, too little");
            Eq(null, SessionNotices.PriceyContext("claude-opus-5-5", shortTurn, 40_000, prices), "a small context after /compact");
            var longAnswer = new TokenUsage { Input = 20, Output = 3_000, CacheRead = 150_000, CacheCreation = 4_000 };
            Eq(null, SessionNotices.PriceyContext("claude-opus-5-5", longAnswer, 154_000, prices), "the answer itself cost most: $0.03 of $0.11");
            True(SessionNotices.PriceyContext("claude-fable-5-2", longAnswer, 154_000, prices) == null, "Fable: reads $0.03 of $0.21");
            True(SessionNotices.PriceyContext(null, new TokenUsage { CacheRead = 400_000, Output = 100 }, 100_000, prices)?.Contains("$0.04 of that turn's $0.04") == true, "unknown model: Sonnet prices; several requests in a turn");

            // A short answer with no tools on Opus / Fable picked by hand: what Haiku would have charged
            var quick = new TokenUsage { Input = 3_000, Output = 400, CacheRead = 20_000, CacheCreation = 1_000 };
            // Opus: 3000×4 + 400×20 + 20000×0.2 + 1000×5 = $0.029; Haiku: 3000×0.1 + 400×0.5 + 20000×0.01 + 1000×0.125 = $0.000825
            Eq("That short answer on Opus cost $0.03; the same tokens on Haiku cost about $0.001. For quick questions, Auto in the model picker (or Haiku) is cheaper. "
                + "A new model writes the conversation to its own cache once, so switch at the start of a task.",
                SessionNotices.OverkillModel("claude-opus-5-5", quick, 0, false, prices));
            True(SessionNotices.OverkillModel("claude-fable-5-2", quick, 0, false, prices)?.StartsWith("That short answer on Fable cost $0.05") == true, "Fable");
            Eq(null, SessionNotices.OverkillModel("claude-sonnet-5-5", quick, 0, false, prices), "Sonnet: no tip");
            Eq(null, SessionNotices.OverkillModel("claude-opus-5-5", quick, 1, false, prices), "a tool call: real work");
            Eq(null, SessionNotices.OverkillModel("claude-opus-5-5", quick, 0, true, prices), "subagents: real work");
            Eq(null, SessionNotices.OverkillModel("claude-opus-5-5", new TokenUsage { Input = 3_000, Output = 2_000, CacheRead = 20_000 }, 0, false, prices), "a long answer");
            Eq(null, SessionNotices.OverkillModel("claude-opus-5-5", new TokenUsage { Input = 10, Output = 200, CacheRead = 20_000 }, 0, false, prices), "too cheap to matter: $0.008");
            var toolMeter = new TokenMeter();
            toolMeter.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolUseId = "t1", ToolName = "Read" });
            toolMeter.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolUseId = "t2", ToolName = "Read", IsSubagent = true });
            toolMeter.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { Input = 10 } });
            Eq(1, toolMeter.LastTurnToolCalls, "the main agent's tool calls");
            toolMeter.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { Input = 10 } });
            Eq(0, toolMeter.LastTurnToolCalls, "counted per turn");

            // Thinking that was most of a short answer at high effort or above
            var thought = new TokenUsage { Input = 500, Output = 6_400, Thinking = 6_000, CacheRead = 20_000 };
            Eq("Claude thought for ~6.0k tokens ($0.12) to write a short answer at high effort. For questions like this, a lower effort in the effort picker (Medium, or Auto) thinks less.",
                SessionNotices.HeavyThinking("claude-opus-5-5", "high", thought, prices), "Opus: 6000 × $20 per Mtok");
            True(SessionNotices.HeavyThinking("claude-opus-5-5", "MAX", thought, prices)?.Contains("at max effort") == true && SessionNotices.HeavyThinking("claude-opus-5-5", "max", thought, prices).Contains("(High, or Auto)"), "max: try High");
            Eq(null, SessionNotices.HeavyThinking("claude-opus-5-5", "medium", thought, prices), "medium: no tip");
            Eq(null, SessionNotices.HeavyThinking("claude-opus-5-5", null, thought, prices), "Claude Code's default: not ours to judge");
            Eq(null, SessionNotices.HeavyThinking("claude-opus-5-5", "high", new TokenUsage { Output = 7_000, Thinking = 6_000 }, prices), "a long answer: 1000 tokens");
            Eq(null, SessionNotices.HeavyThinking("claude-opus-5-5", "high", new TokenUsage { Output = 2_500, Thinking = 1_900 }, prices), "little thinking");
            Eq(null, SessionNotices.HeavyThinking("claude-haiku-5-5", "high", thought, prices), "Haiku: $0.003, too cheap");
            True(SessionNotices.HeavyThinking("claude-sonnet-5-5", "xhigh", thought, prices)?.Contains("($0.06)") == true, "Sonnet: 6000 × $10");

            // Subagents costing more than the main agent: the turn's cost includes them
            True(SessionNotices.SubagentsDominate(0.30, 0.50)?.Contains("model: haiku") == true, "agents $0.30 of $0.50: tip");
            Eq(null, SessionNotices.SubagentsDominate(0.20, 0.50), "agents $0.20 of $0.50: the main agent cost more");
            Eq(null, SessionNotices.SubagentsDominate(0.04, 0.045), "too little to matter");
            Eq(null, SessionNotices.SubagentsDominate(0, 1), "no agents");
            Eq(null, SessionNotices.SubagentsDominate(0.30, 0), "no cost from the CLI");

            // A costly fork, once finished: the tip, without naming a switch that is not settled
            var costlyFork = new AgentTask { TaskType = "local_agent", SubagentType = ToolCallView.ForkType, Status = AgentTaskStatus.Completed, CostUsd = 0.08 };
            string forkTip = SessionNotices.ForkCost(costlyFork);
            True(forkTip?.Contains("Explore agent") == true && forkTip.Contains("No forks"), forkTip);

            // No forks: CLAUDE_CODE_FORK_SUBAGENT=0. Captured with 2.1.322: asked for a fork, the Agent call fails and
            // Claude answers without one (with the default, the same prompt ran a fork)
            Eq("0", new ClaudeLaunchOptions { NoForks = true }.Environment()["CLAUDE_CODE_FORK_SUBAGENT"]);
            Eq(0, new ClaudeLaunchOptions().Environment().Count, "nothing set by default");
            Eq("1", new ClaudeLaunchOptions { EnableFileCheckpointing = true }.Environment()["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"]);
            var offEvents = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.322-fork-off.jsonl")).SelectMany(l => new ClaudeStreamParser().Parse(l)).ToList();
            var forkCall = offEvents.Single(e => e.Kind == ClaudeEventKind.ToolUse);
            Eq(ToolCallView.ForkType, (string)forkCall.ToolInput["subagent_type"], "Claude asked for a fork");
            var refused = offEvents.Single(e => e.Kind == ClaudeEventKind.ToolResult);
            True(refused.IsError && refused.ResultText.StartsWith("Agent type 'fork' not found"), refused.ResultText);
            True(!offEvents.Any(e => e.Kind == ClaudeEventKind.AssistantUsage && e.IsSubagent), "no subagent ran");
            True(offEvents.Last().Kind == ClaudeEventKind.TurnCompleted && !offEvents.Last().IsError, "the turn still finished");
            Eq(null, SessionNotices.ForkCost(new AgentTask { TaskType = "local_agent", SubagentType = ToolCallView.ForkType, CostUsd = 0.08 }), "still running");
            Eq(null, SessionNotices.ForkCost(new AgentTask { TaskType = "local_agent", SubagentType = ToolCallView.ForkType, Status = AgentTaskStatus.Completed, CostUsd = 0.02 }), "cheap");
            Eq(null, SessionNotices.ForkCost(new AgentTask { TaskType = "local_agent", SubagentType = "Explore", Status = AgentTaskStatus.Completed, CostUsd = 0.08 }), "not a fork");
            Eq(null, SessionNotices.ForkCost(null));
        }

        private static void Test_SavingsReport_FableAndFastExtra()
        {
            var prices = new PriceTable(); // Opus 4 / 20, Fable 8 / 40; fast mode 2x
            var t = DateTime.UtcNow.AddHours(-1);
            var recs = new[]
            {
                new UsageRecord { Timestamp = t, ModelUsed = "claude-fable-5-2", InputTokens = 1000, OutputTokens = 1000, CostUsd = 0.048 },
                new UsageRecord { Timestamp = t, ModelUsed = "claude-opus-5-5", InputTokens = 1000, OutputTokens = 1000, FastTokens = 2000, CostUsd = 0.048 },
                new UsageRecord { Timestamp = t, ModelUsed = "claude-sonnet-5-5", InputTokens = 1000, OutputTokens = 1000, CostUsd = 0.012 },
            };
            var r = SavingsReport.Build(recs, prices);
            Eq(1, r.FableTurns);
            True(Math.Abs(r.FableExtraUsd - 0.024) < 1e-9, "Fable: $0.048 vs $0.024 on Opus: " + r.FableExtraUsd);
            Eq(1, r.FastTurns);
            True(Math.Abs(r.FastExtraUsd - 0.024) < 1e-9, "fast: $0.048 vs $0.024 at standard speed: " + r.FastExtraUsd);
            string text = r.ToText();
            True(text.Contains("Paid extra for Fable:       $0.02  (1 turn, above the same tokens on Opus)"), text);
            True(text.Contains("Paid extra for fast mode:   $0.02  (1 turn, above standard speed)"), text);

            var plain = SavingsReport.Build(recs.Skip(2), prices).ToText();
            True(!plain.Contains("Paid extra"), "no Fable or fast turns: no lines");

            // Haiku over 100k tokens: 1M cache-read tokens, all from requests over the tier: $0.05 instead of $0.01
            var big = new UsageRecord { Timestamp = t, ModelUsed = "claude-haiku-5-5", CacheReadTokens = 1_000_000, LargeContextTokens = 1_000_000, CostUsd = 0.05 };
            var small = new UsageRecord { Timestamp = t, ModelUsed = "claude-haiku-5-5", CacheReadTokens = 50_000, CostUsd = 0.0005 };
            var lr = SavingsReport.Build(new[] { big, small }, prices);
            Eq(1, lr.LongContextTurns, "only the turn over 100k");
            True(Math.Abs(lr.LongContextExtraUsd - 0.04) < 1e-9, "5x - 1x on $0.01: " + lr.LongContextExtraUsd);
            True(lr.ToText().Contains("Paid extra over 100k:       $0.04  (1 Haiku turn over 100k tokens, above Haiku's normal price; /compact keeps it under)"), lr.ToText());
            True(!SavingsReport.Build(new[] { small }, prices).ToText().Contains("over 100k"), "none over 100k: no line");
            var sonnetBig = new UsageRecord { Timestamp = t, ModelUsed = "claude-sonnet-5-5", CacheReadTokens = 1_000_000, LargeContextTokens = 1_000_000 };
            Eq(0, SavingsReport.Build(new[] { sonnetBig }, prices).LongContextTurns, "Sonnet has no such tier");
        }

        private static void Test_UsageCsv()
        {
            var t = new DateTime(2026, 11, 9, 9, 30, 0, DateTimeKind.Local).ToUniversalTime();
            var recs = new[]
            {
                new UsageRecord { Timestamp = t.AddMinutes(5), Solution = @"C:\src\b", ModelUsed = "claude-haiku-5-5", CostUsd = 0.0012345, PromptPreview = "=SUM(A1) say \"hi\", ok" },
                new UsageRecord { Timestamp = t, Solution = @"C:\src\a\", ModelUsed = "claude-opus-5-5", Routed = true, InputTokens = 5, OutputTokens = 1200, ThinkingTokens = 300,
                    CacheReadTokens = 9000, CacheWriteTokens = 100, CacheWrite1hTokens = 100, FastTokens = 10405, CostUsd = 0.25, RouterCostUsd = 0.0001, DurationMs = 4200, CharsAvoided = 400, RouteReason = "multi-file" },
            };
            var sw = new StringWriter();
            Eq(2, UsageCsv.Write(recs, sw), "rows");
            var lines = sw.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
            Eq(string.Join(",", UsageCsv.Columns), lines[0], "header");
            Eq(t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + @",C:\src\a\,claude-opus-5-5,1,,,,5,1200,300,9000,100,100,10405,0,0,,0.25,0.0001,4200,0,134,multi-file,,,,", lines[1], "oldest first, invariant numbers; fast_tokens, large_context_tokens, agent_tokens and agent_types after the cache columns");
            True(lines[2].EndsWith(",0,,0.001235,0,0,0,0,,\"'=SUM(A1) say \"\"hi\"\", ok\",,,"), "quoted, formula-guarded prompt: " + lines[2]);
            Eq("", lines[3], "ends with a line break");
            Eq("session,effort,tools", string.Join(",", UsageCsv.Columns.Skip(UsageCsv.Columns.Length - 3)), "session id, effort and tools last");
            var withSession = new StringWriter();
            UsageCsv.Write(new[] { new UsageRecord { Timestamp = t, SessionId = "abc-123", Effort = "high", ToolResultTokens = new Dictionary<string, long> { ["Shell"] = 400, ["Read"] = 1200, ["Edit"] = 0 } } }, withSession);
            True(withSession.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None)[1].EndsWith(",,abc-123,high,Read:1200;Shell:400"), "after the prompt: " + withSession);

            var one = new StringWriter();
            Eq(1, UsageCsv.Write(recs, one, solution: @"c:\SRC\a"), "one solution, any case or trailing slash");
            Eq("Explore:0.75;general-purpose:0.25", UsageCsv.AgentTypes(new List<AgentTypeCost> { new AgentTypeCost { Type = "Explore", Share = 0.75 }, new AgentTypeCost { Type = "general-purpose", Share = 0.25 } }), "agent_types");
            Eq("Fork:0.3333", UsageCsv.AgentTypes(new List<AgentTypeCost> { new AgentTypeCost { Type = "Fork", Share = 1.0 / 3 } }), "4 decimals");
            Eq("", UsageCsv.AgentTypes(null), "none");
            Eq("plain", UsageCsv.Text("plain"), "no quotes needed");
            Eq("\" padded\"", UsageCsv.Text(" padded"), "leading space kept by quoting");
            Eq("'-1", UsageCsv.Text("-1"), "minus guarded");
        }

        private static void Test_SavingsReport_ByDay()
        {
            var d1 = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var d2 = d1.AddDays(1);
            var recs = new[]
            {
                new UsageRecord { Timestamp = d1, ModelUsed = "claude-sonnet-5-5", CostUsd = 1, InputTokens = 100, CacheReadTokens = 900 },
                new UsageRecord { Timestamp = d1.AddHours(1), ModelUsed = "claude-sonnet-5-5", CostUsd = 1, RouterCostUsd = 0.5, OutputTokens = 1000 },
                new UsageRecord { Timestamp = d2, ModelUsed = "claude-sonnet-5-5", CostUsd = 5, CacheReadTokens = 1_000_000 },
            };
            var rep = SavingsReport.Build(recs, new PriceTable());
            Eq(2, rep.ByDay.Count, "two days");
            Eq(2, rep.ByDay[0].Turns, "turns on day 1");
            Eq(2000L, rep.ByDay[0].Tokens, "tokens on day 1");
            True(Math.Abs(rep.ByDay[0].CostUsd - 2.5) < 1e-9, "router cost included: " + rep.ByDay[0].CostUsd);
            Eq(90.0, rep.ByDay[0].CacheHitPercent, "900 of 1000 input from the cache");
            True(Math.Abs(rep.ByDay[1].CacheSavedUsd - 1.9) < 1e-9, "1M read on Sonnet saves $1.90: " + rep.ByDay[1].CacheSavedUsd);

            string text = rep.ToText();
            var lines = text.Replace("\r", "").Split('\n');
            True(lines.Contains("By day:"), text);
            string day2 = lines.Single(l => l.StartsWith("  " + d2.ToLocalTime().ToString("yyyy-MM-dd")) && l.Contains(" turns "));
            True(day2.EndsWith(new string('█', 20)), "the dearest day has the full bar: " + day2);
            True(day2.Contains("1 turns") && day2.Contains("cache 100%"), day2);
            string day1 = lines.Single(l => l.StartsWith("  " + d1.ToLocalTime().ToString("yyyy-MM-dd")) && l.Contains(" turns "));
            True(day1.EndsWith(" " + new string('█', 10)), "half the cost, half the bar: " + day1);
            Eq(1, SavingsReport.Build(recs.Take(1), new PriceTable()).ByDay.Count, "one day");
            True(!SavingsReport.Build(recs.Take(1), new PriceTable()).ToText().Contains("By day"), "no table for a single day");
        }

        private static void Test_DailySpendWatch()
        {
            // Local noon, so "today" is the same day in any time zone's local view of these instants.
            var start = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var w = new SpendWatch(SpendPeriod.Day, 20, start);
            w.Load(new[]
            {
                new UsageRecord { Timestamp = start.AddHours(-1), CostUsd = 21, RouterCostUsd = 0.5 },
                new UsageRecord { Timestamp = start.AddDays(-1), CostUsd = 100 },          // yesterday
                new UsageRecord { Timestamp = start.AddMinutes(1), CostUsd = 3 },           // added by Add already
            });
            True(Math.Abs(w.Spent - 21.5) < 1e-9, "today's turns before the watch, with router cost: " + w.Spent);
            Eq(null, w.Add(1, start.AddMinutes(1)), "$20 was passed before loading: no warning again");
            string warn = w.Add(18, start.AddMinutes(2));
            True(warn != null && warn.Contains("passed $40.00") && warn.Contains("($40.50 so far"), warn);
            Eq(null, w.Add(1, start.AddMinutes(3)), "once per step");
            Eq(null, w.Add(double.NaN, start.AddMinutes(4)), "NaN adds nothing");

            Eq(null, w.Add(5, start.AddDays(1)), "a new day starts from 0");
            True(Math.Abs(w.Spent - 5) < 1e-9, "new day: " + w.Spent);
            True(w.Add(15, start.AddDays(1).AddMinutes(1))?.Contains("passed $20.00") == true, "first step of the new day");

            var off = new SpendWatch(SpendPeriod.Day, 0, start);
            Eq(null, off.Add(1000, start), "0 turns it off");
        }

        private static void Test_SpendLimit()
        {
            var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var l = new SpendLimit(50);
            Eq(null, l.Ask(49.99, now), "below the limit");
            string q = l.Ask(52.3, now);
            Eq("Today's Claude Code spend in Visual Studio is $52.30, past your daily limit of $50.00 (at API prices; Tools → Options → Claude Code → Ask before sending past daily cost).\n\n"
                + "Send this prompt anyway? You won't be asked again today until $100.00.", q);
            Eq(q, l.Ask(52.3, now.AddMinutes(1)), "asked again until you say yes");
            l.Allow(52.3, now);
            Eq(null, l.Ask(80, now.AddMinutes(2)), "allowed up to the next multiple");
            True(l.Ask(101, now.AddMinutes(3))?.Contains("until $150.00") == true, "past $100: asked again");
            l.Allow(101, now.AddMinutes(3));
            True(l.Ask(60, now.AddDays(1)) != null, "a new day: asked again");
            Eq(null, new SpendLimit(0).Ask(1000, now), "0: never asks");
            Eq(null, l.Ask(double.NaN, now), "no spend known");
        }

        private static void Test_MonthlySpendWatch()
        {
            // Local noon on the 9th: the 1st and the 30th of the month before are in other months in any time zone.
            var start = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var w = new SpendWatch(SpendPeriod.Month, 200, start);
            w.Load(new[]
            {
                new UsageRecord { Timestamp = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Local).ToUniversalTime(), CostUsd = 150 },
                new UsageRecord { Timestamp = start.AddDays(-1), CostUsd = 60, RouterCostUsd = 0.25 },
                new UsageRecord { Timestamp = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Local).ToUniversalTime(), CostUsd = 500 }, // last month
            });
            True(Math.Abs(w.Spent - 210.25) < 1e-9, "this month's turns before the watch: " + w.Spent);
            Eq(null, w.Add(5, start.AddMinutes(1)), "$200 was passed before loading");
            string warn = w.Add(190, start.AddDays(3));
            True(warn != null && warn.StartsWith("This month's Claude Code spend") && warn.Contains("passed $400.00") && warn.Contains("($405.25 so far"), warn);
            Eq(null, w.Add(1, start.AddDays(4)), "once per step");
            var december = new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            Eq(null, w.Add(10, december), "a new month starts from 0");
            True(Math.Abs(w.Spent - 10) < 1e-9, "new month: " + w.Spent);
            True(w.Add(195, december.AddDays(1))?.Contains("passed $200.00") == true, "first step of the new month");

            True(new SpendWatch(SpendPeriod.Day, 20, start).Add(25, start)?.StartsWith("Today's") == true, "the daily text");

            // The meter's tooltip line
            var d = new SpendWatch(SpendPeriod.Day, 20, start);
            var m = new SpendWatch(SpendPeriod.Month, 200, start);
            Eq(null, SpendWatch.BudgetLine(d, m, start), "nothing spent yet");
            m.Load(new[] { new UsageRecord { Timestamp = start.AddDays(-2), CostUsd = 38 } });
            d.Add(3.2, start.AddMinutes(1)); m.Add(3.2, start.AddMinutes(1));
            Eq("Spent in Visual Studio: today $3.20 (next warning at $20.00) · this month $41.20 (next warning at $200.00), at API prices.", SpendWatch.BudgetLine(d, m, start.AddMinutes(2)));
            d.Add(20, start.AddMinutes(3));
            Eq("today $23.20 (next warning at $40.00)", d.Line(start.AddMinutes(3)), "past a step: the next one");
            Eq("today $0.00 (next warning at $20.00)", d.Line(start.AddDays(1)), "a new day before its first turn");
            True(d.IsPastWarning(start.AddMinutes(3)), "$23.20 past $20: amber meter");
            True(!m.IsPastWarning(start.AddMinutes(3)), "$61.20 of $200 this month");
            True(!d.IsPastWarning(start.AddDays(1)), "a new day");
            d.StepUsd = 0;
            Eq("today $23.20", d.Line(start.AddMinutes(3)), "warning off");
            True(!d.IsPastWarning(start.AddMinutes(3)), "warning off: never amber");
        }

        private static void Test_CostWatch()
        {
            var w = new CostWatch(5);
            Eq(null, w.Check(4.99), "below the step");
            Eq("This conversation has passed $5.00 ($5.12 so far at API prices). A New chat for the next task, or /compact, keeps the next prompts cheaper.", w.Check(5.12), "first step");
            Eq(null, w.Check(7.80), "same step: said already");
            True(w.Check(11.02)?.StartsWith("This conversation has passed $10.00 ($11.02") == true, "next step");
            Eq(null, w.Check(14.00), "still the second step");
            w.Reset();
            True(w.Check(5.00) != null, "a new conversation starts over");
            Eq(null, new CostWatch(0).Check(100), "0 turns it off");
            Eq(null, new CostWatch(5).Check(double.NaN), "no cost known");
        }

        // ---------- Sign-in expiry ----------
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
        private static void Test_Meter_ContextAfterCompactFromCli()
        {
            var m = new TokenMeter();
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, LastRequestUsage = new TokenUsage { CacheRead = 27000, Input = 782 }, ContextWindow = 200000 });
            Eq(27782L, m.ContextTokens);
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.Compacted, PreCompactTokens = 27782 });
            Eq(0L, m.ContextTokens, "usage alone says 0 after a compaction");
            m.ApplyContextUsage(9100, 1000000);
            Eq(9100L, m.ContextTokens, "the CLI's measurement fills it in");
            Eq(1000000L, m.ContextWindow);
            m.ApplyContextUsage(0, 0);
            Eq(9100L, m.ContextTokens, "an empty answer changes nothing");
        }

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

        private static void Test_Router_ShortChangeRequest_NotLowEffort()
        {
            // From a real session: short, with typos, but it asks for code in several files.
            var d = R.Route(new RouteRequest { Prompt = "add more samplees and updated exitesd with a news samples" });
            Eq(ModelChoice.Sonnet, d.Model, d.ToString());
            Eq(EffortChoice.Medium, d.Effort, d.ToString());
            True(!d.Reason.Contains("short prompt"), d.ToString());
            // A short question with no change still counts as short.
            var q = R.Route(new RouteRequest { Prompt = "what does this method return?" });
            True(q.Reason.Contains("short prompt"), q.ToString());
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
                True(!store.Get("b").Archived && store.Get("b").UnarchivedUtc != null, "unarchived, and remembered so auto-archive waits");
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
                "\n- 🔧 `Read: C:/a/Login.cs` · ~2.7k tok\n- 🔧 `Bash: echo 'x'` · failed\n" +
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

        private static void Test_Args_ForkSession()
        {
            string a = ClaudeArgsBuilder.Build(new ClaudeLaunchOptions { ResumeSessionId = "abc", ForkSession = true });
            True(a.Contains("--resume abc --fork-session"), a);
            True(!ClaudeArgsBuilder.Build(new ClaudeLaunchOptions { ForkSession = true }).Contains("--fork-session"), "fork needs a session to resume");
        }

        private static void Test_SessionMeta_GroupsAndAutoArchive()
        {
            string dir = Path.Combine(Path.GetTempPath(), "claudevs-meta2-" + Guid.NewGuid().ToString("N"));
            var store = new SessionMetaStore(Path.Combine(dir, "sessions.json"));
            try
            {
                var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
                List<SessionInfo> List() => store.Apply(new List<SessionInfo>
                {
                    new SessionInfo { Id = "fresh", Title = "a", LastWriteUtc = now.AddDays(-2) },
                    new SessionInfo { Id = "old", Title = "b", LastWriteUtc = now.AddDays(-20) },
                }, autoArchiveDays: 14, nowUtc: now);

                var l = List();
                True(!l[0].Archived && l[1].Archived && l[1].AutoArchived, "20 days idle is auto-archived");
                True(!store.Apply(new List<SessionInfo> { new SessionInfo { Id = "old", LastWriteUtc = now.AddDays(-20) } }, 0, now)[0].Archived, "0 days = off");

                store.SetArchived("old", false); // unarchiving restarts the clock (UnarchivedUtc = real now, after 'now - 20 days')
                True(!List()[1].Archived, "unarchived stays visible");

                store.SetGroup("fresh", " Work ");
                store.SetGroup("old", "Work");
                store.SetGroup("x", "Personal");
                Eq("Personal|Work", string.Join("|", store.Groups()));
                Eq("Work", List()[0].Group);
                store.RenameGroup("work", "Client");
                Eq("Client", List()[1].Group, "rename is case-insensitive");
                store.RenameGroup("Personal", "");
                Eq(null, store.Get("x"), "deleting the group forgets a session with nothing else stored");
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        private static void Test_RunningSessions_Registry()
        {
            string dir = Path.Combine(Path.GetTempPath(), "claudevs-run-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "100.json"), "{\"pid\":100,\"sessionId\":\"s1\",\"cwd\":\"C:\\\\src\",\"status\":\"busy\",\"entrypoint\":\"claude-vscode\"}");
                File.WriteAllText(Path.Combine(dir, "200.json"), "{\"pid\":200,\"sessionId\":\"s2\",\"status\":\"idle\",\"entrypoint\":\"cli\"}");
                File.WriteAllText(Path.Combine(dir, "300.json"), "{ broken");
                File.WriteAllText(Path.Combine(dir, "100.abc.key"), "{}");
                var running = RunningSessions.Read(dir, pid => pid == 100);
                Eq(1, running.Count, "dead process and broken file skipped");
                Eq("s1", running[0].SessionId);
                Eq("VS Code", running[0].Where);
                Eq("working", running[0].StatusText);
                Eq("a terminal", new RunningSession { Entrypoint = "cli" }.Where);
                Eq("needs input", new RunningSession { Status = "waiting" }.StatusText);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static void Test_PlanComments_FromEdits()
        {
            string plan = "# Plan\n\n## Step 1\nAdd the API.\n\n## Step 2\nWrite tests.\nUpdate docs.";
            string edited = "# Plan\nKeep it small please.\n\n## Step 1\n  Add the API.\nUse minimal APIs, not controllers.\n\n## Step 2\nWrite tests.";
            var c = PlanComments.Extract(plan, edited);
            Eq(3, c.Count, string.Join(" | ", c.Select(x => x.After + ">" + (x.Text ?? "-" + x.Removed))));
            Eq("# Plan", c[0].After);
            Eq("Keep it small please.", c[0].Text);
            Eq("Add the API.", c[1].After, "re-indenting a line is not a change");
            Eq("Use minimal APIs, not controllers.", c[1].Text);
            Eq("Update docs.", c[2].Removed);
            Eq("Comments on the plan (from the plan document):\n- After \"# Plan\": Keep it small please.\n- After \"Add the API.\": Use minimal APIs, not controllers.\n- Remove: \"Update docs.\"",
               PlanComments.ToFeedback(c));
            Eq(0, PlanComments.Extract(plan, plan.Replace("\n", "\r\n")).Count, "line endings alone are no change");
            Eq(null, PlanComments.ToFeedback(new List<PlanComments.Comment>()));
        }

        // ---------- Skills and MCP ----------
        private static void Test_SkillsAndMcp_FromRealResponses()
        {
            var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.317-skills-mcp.jsonl")).Where(l => l.Trim().Length > 0).ToList();
            var parser = new ClaudeStreamParser();
            var skills = SkillRow.ParseDialog(parser.Parse(lines[0]).Single().Payload);
            Eq(3, skills.Count);
            Eq("anthropic-skills:artifact-emulator", skills[0].Name);
            Eq("artifact-emulator", skills[0].DisplayName);
            Eq("claude.ai sync", skills[0].Source);
            Eq(240, skills[0].Tokens);
            True(skills[0].IsOn && skills[0].LockedBy == null, "on, not locked");

            var servers = McpServerRow.ParseStatus(parser.Parse(lines[1]).Single().Payload);
            Eq(2, servers.Count);
            Eq("needs-auth", servers[1].Status);
            Eq("Needs sign-in", McpServerRow.StatusText(servers[1].Status));
            Eq("claudeai-proxy", servers[0].Transport);
            Eq(-1, servers[0].ToolCount, "no tool list while pending");
        }

        private static void Test_Skills_EditAndLocks()
        {
            Eq("{\"name\":\"pdf\",\"state\":\"off\"}", SkillRow.OverrideEdit("pdf", "off"));
            var locked = SkillRow.ParseDialog(JObject.Parse("{\"skills\":[{\"name\":\"x\",\"state\":\"off\",\"locked_by\":\"policy\"}]}")).Single();
            True(!locked.IsOn, "off");
            Eq("set by your organization", SkillRow.LockReason(locked.LockedBy));
        }

        // ---------- Usage limits ----------
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

        private static void Test_ContextUsage_DeferredMcpTools_2_1_322()
        {
            // Real 2.1.322 answer with the extension's Error List server: its tool loads on demand ("isLoaded": false)
            string line = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.322-context-usage-mcp.jsonl")).Trim();
            var u = ContextUsage.Parse(new ClaudeStreamParser().Parse(line).Single().Payload);
            Eq(17640L, u.TotalTokens);
            Eq(2.0, u.Percentage, "the CLI rounds its percentage (1.764% → 2)");
            Eq(17640L, u.Categories.Where(c => c.IsUsed).Sum(c => c.Tokens), "used categories add up to the total");
            Eq("deferred", u.Categories.Single(c => c.Id == "mcp_tools_deferred").Kind, "MCP tools (deferred) is a category of its own");
            Eq(0, u.McpToolCount, "nothing loaded");
            Eq(0L, u.McpToolTokens, "so nothing on every request");
            Eq(1, u.McpDeferredToolCount, "one on demand");
            Eq(53L, u.McpDeferredToolTokens);

            // Many deferred tools: no tip (they are not in every request); loaded ones still earn it
            string Tools(bool loaded) => "[" + string.Join(",", Enumerable.Range(0, 40).Select(i => $"{{\"name\":\"t{i}\",\"tokens\":300,\"isLoaded\":{(loaded ? "true" : "false")}}}")) + "]";
            var deferredOnly = ContextUsage.Parse(JObject.Parse("{\"mcpTools\":" + Tools(false) + "}"));
            Eq(12000L, deferredOnly.McpDeferredToolTokens);
            Eq(null, SessionNotices.BaseContext(deferredOnly), "deferred tools earn no tip");
            True(!deferredOnly.Tips().Any(t => t.Contains("MCP")), "nor a meter tip");
            var loadedAll = ContextUsage.Parse(JObject.Parse("{\"mcpTools\":" + Tools(true) + "}"));
            True(SessionNotices.BaseContext(loadedAll)?.Contains("MCP tools (40)") == true, "loaded tools do");

            // Before 2.1.319 there was no isLoaded: every listed tool counts as loaded, as before
            var old = ContextUsage.Parse(JObject.Parse("{\"mcpTools\":[{\"name\":\"a\",\"tokens\":6000}]}"));
            Eq(6000L, old.McpToolTokens, "missing isLoaded = loaded");
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
            Eq("Bash|PowerShell", (string)s["hooks"]["PreToolUse"][0]["matcher"], "both shells");
            Eq("15000", (string)s["env"]["BASH_MAX_OUTPUT_LENGTH"]);
            Eq(15000, (int)s[TokenSaverSetup.OutputMaxCharsKey]);
        }

        private static void Test_Setup_UpgradesBashOnlyHookAndKeepsUserCap()
        {
            // As installed by an earlier version: Bash-only matcher; the user picked their own output cap.
            var s = JObject.Parse("{\"bashOutputMaxChars\":8000,\"hooks\":{\"PreToolUse\":[{\"matcher\":\"Bash\",\"hooks\":[{\"type\":\"command\",\"command\":\"powershell -File \\\"$CLAUDE_PROJECT_DIR/.claude/hooks/trim-output.ps1\\\"\"}]},{\"matcher\":\"Bash\",\"hooks\":[{\"type\":\"command\",\"command\":\"other.sh\"}]}]}}");
            TokenSaverSetup.MergeSettings(s);
            var pre = (JArray)s["hooks"]["PreToolUse"];
            Eq(2, pre.Count, "no duplicate hook added");
            Eq("Bash|PowerShell", (string)pre[0]["matcher"], "ours upgraded");
            Eq("Bash", (string)pre[1]["matcher"], "someone else's hook left alone");
            Eq(8000, (int)s["bashOutputMaxChars"], "user's cap kept");
            True(TokenSaverSetup.HookScript(false).Contains("$evt.tool_name -ne 'PowerShell'"), "script accepts PowerShell");
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

        // Gaps found by running the generator on real solutions (Phase 2.4).
        private static void Test_Scanner_LegacyFramework()
        {
            string root = Path.Combine(Path.GetTempPath(), "claudevs-test-" + Guid.NewGuid().ToString("N"));
            void Write(string rel, string text) { string f = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllText(f, text); }
            const string ns = "xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"";
            Write("Shop.sln", "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Shop.Web\", \"Shop.Web\\Shop.Web.csproj\", \"{1}\"\r\nEndProject\r\n"
                            + "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Shop.Tests\", \"Shop.Tests\\Shop.Tests.csproj\", \"{2}\"\r\nEndProject\r\n"
                            + "Project(\"{F184B08F-C81C-45F6-A57F-5ABD9991F28F}\") = \"Reports\", \"Reports\\Reports.vbproj\", \"{3}\"\r\nEndProject\r\n");
            Write("Shop.Web/Shop.Web.csproj", $"<Project ToolsVersion=\"15.0\" {ns}><PropertyGroup><TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion><ProjectTypeGuids>{{349c5851-65df-11da-9384-00065b846f21}};{{fae04ec0-301f-11d3-bf4b-00c04f79efbc}}</ProjectTypeGuids></PropertyGroup></Project>");
            Write("Shop.Web/packages.config", "<packages><package id=\"EntityFramework\" version=\"6.4.4\" /><package id=\"log4net\" version=\"2.0.8\" /></packages>");
            Write("Shop.Tests/Shop.Tests.csproj", $"<Project {ns}><PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup><ItemGroup><Reference Include=\"nunit.framework, Version=3.13.0.0\" /></ItemGroup></Project>");
            Write("Reports/Reports.vbproj", $"<Project {ns}><PropertyGroup><TargetFrameworkVersion>v4.5.2</TargetFrameworkVersion></PropertyGroup></Project>");
            try
            {
                var info = SolutionScanner.Scan(root);
                var web = info.Projects.Single(p => p.Name == "Shop.Web");
                True(web.IsLegacy && web.IsWeb && web.UsesPackagesConfig, "legacy web project");
                Eq("net472", web.Frameworks, "framework moniker");
                True(web.KeyPackages.Contains("EntityFramework") && web.KeyPackages.Contains("log4net"), "packages.config read");
                True(info.Projects.Single(p => p.Name == "Shop.Tests").IsTest, "test project from an assembly reference");
                True(info.Projects.Any(p => p.Name == "Reports"), ".vbproj found");

                string md = ClaudeMdGenerator.Generate(info);
                True(md.Contains("`msbuild Shop.sln -restore -p:RestorePackagesConfig=true -v:q -nologo`"), md);
                True(!md.Contains("Build: `dotnet build"), "no dotnet build for .NET Framework: " + md);
                True(md.Contains("`vstest.console.exe Shop.Tests/bin/Debug/Shop.Tests.dll`"), md);
            }
            finally { Directory.Delete(root, true); }
        }

        private static void Test_Scanner_SolutionMembership_And_FrontEnds()
        {
            string root = Path.Combine(Path.GetTempPath(), "claudevs-test-" + Guid.NewGuid().ToString("N"));
            void Write(string rel, string text) { string f = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllText(f, text); }
            Write("App.slnx", "<Solution><Project Path=\"src/Api/Api.csproj\" /><Project Path=\"../Shared/Shared.csproj\" /></Solution>");
            Write("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            Write("src/Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
            Write("src/Api/package.json", "{\"name\":\"api-assets\",\"devDependencies\":{\"esbuild\":\"^0.25.0\"},\"scripts\":{\"build\":\"node build.js\"}}");
            Write("samples/Old/Old.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            Write("web/package.json", "{\"name\":\"app-web\",\"scripts\":{\"build\":\"vite build\",\"test\":\"vitest run\",\"lint\":\"eslint .\"},\"dependencies\":{\"react\":\"19.0.0\"},\"devDependencies\":{\"vitest\":\"3.0.0\"}}");
            Write("package.json", "{\"devDependencies\":{\"esbuild\":\"^0.25.0\"}}");
            try
            {
                var info = SolutionScanner.Scan(root);
                Eq("net10.0", info.Projects.Single(p => p.Name == "Api").Frameworks, "framework from Directory.Build.props");
                True(!info.Projects.Single(p => p.Name == "Old").InSolution, "project the solution doesn't list");
                Eq("../Shared/Shared.csproj", info.ExternalProjects.Single(), "external project");
                True(!info.Projects.Any(p => p.RelativePath == "package.json"), "tooling-only package.json dropped");
                Eq("React 19.0.0", info.Projects.Single(p => p.Name == "app-web").Frameworks, "React detected");

                string md = ClaudeMdGenerator.Generate(info);
                True(md.Contains("Not in App.slnx (not built by it): `samples/` (1)"), md);
                True(md.Contains("outside this folder: `../Shared/Shared.csproj`"), md);
                True(md.Contains("app-web: `cd web && npm run build` / `npm test`"), md);
                True(md.Contains("api-assets: `cd src/Api && npm run build`"), md);
                True(!md.Contains("` api-assets"), "package.json beside a .NET project is not listed as a second project: " + md);
                True(!md.Contains("Old"), "projects outside the solution are not listed one by one");
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
