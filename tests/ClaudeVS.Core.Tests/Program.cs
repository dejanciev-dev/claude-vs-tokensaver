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
            if (args.Length > 0 && args[0] == "--live-mcp") return LiveSmoke.RunMcp();
            if (args.Length > 0 && args[0] == "--live-haiku-router") return LiveSmoke.RunHaikuRouter();
            if (args.Length > 0 && args[0] == "--live-session") return LiveSmoke.RunSession();
            if (args.Length > 0 && args[0] == "--live-setup") return LiveSmoke.RunSetup(args.Length > 1 ? args[1] : null);
            if (args.Length > 0 && args[0] == "--live-dialogs") return LiveSmoke.RunDialogs();
            if (args.Length > 0 && args[0] == "--live-edit-review") return LiveSmoke.RunEditReview();
            if (args.Length > 1 && args[0] == "--scan")
            {
                // Read-only: prints the CLAUDE.md that Generate Lean CLAUDE.md would write for that folder.
                string md = ClaudeMdGenerator.Generate(SolutionScanner.Scan(args[1]));
                Console.WriteLine(md);
                Console.WriteLine($"~{ClaudeMdGenerator.EstimateTokens(md)} tokens");
                return 0;
            }
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
            True(CliVersion.IsOlderThanTested(init.CliVersion), "2.1.316 is older than tested");
            True(CliVersion.Notice(init.CliVersion).StartsWith("Claude Code 2.1.316 is older than " + CliVersion.Tested), "notice");
            True(CliVersion.Notice(init.CliVersion).Contains("claude update"), "says how to fix it");
        }

        private static void Test_CliVersion_Compare()
        {
            Eq(new Version(2, 1, 319), CliVersion.Parse("2.1.319 (Claude Code)"), "claude --version output");
            Eq(null, CliVersion.Parse("dev"), "not a version");
            Eq(null, CliVersion.Parse(null), "missing");
            True(!CliVersion.IsOlderThanTested(CliVersion.Tested), "the tested version itself");
            True(!CliVersion.IsOlderThanTested("2.1.319"), "newer");
            True(!CliVersion.IsOlderThanTested("2.2.0"), "newer minor");
            True(CliVersion.IsOlderThanTested("2.1.99"), "numeric, not string, comparison");
            True(CliVersion.IsOlderThanTested("1.0.128"), "old major");
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
            True(tip?.StartsWith("Claude read all 2,400 lines of Big.cs (~30") == true, tip);
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
            t.Observe(Read("2", @"c:\r\big.cs")); Eq((@"c:\r\big.cs", 3000), t.Observe(Result("2", body)), "same file, same text: repeated");
            Eq(3000L, t.RepeatedTokens);

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
            True(without?.StartsWith("That command put ~12.0k tokens of output into the context.") == true, without);
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

        // ---------- Conversation cost warning ----------
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
