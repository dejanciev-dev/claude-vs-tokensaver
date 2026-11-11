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

        private static void Test_Args_SessionName()
        {
            string a = ClaudeArgsBuilder.Build(new ClaudeLaunchOptions { SessionName = "Fix login\nbug" });
            True(a.Contains("--name \"Fix login bug\""), a);
            True(!ClaudeArgsBuilder.Build(new ClaudeLaunchOptions()).Contains("--name"), "no name, no flag");
        }

        private static void Test_Args_ForkSession()
        {
            string a = ClaudeArgsBuilder.Build(new ClaudeLaunchOptions { ResumeSessionId = "abc", ForkSession = true });
            True(a.Contains("--resume abc --fork-session"), a);
            True(!ClaudeArgsBuilder.Build(new ClaudeLaunchOptions { ForkSession = true }).Contains("--fork-session"), "fork needs a session to resume");
        }

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
    }
}
