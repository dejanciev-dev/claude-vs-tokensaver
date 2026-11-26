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
            var plain = ClaudeSession.BuildInitialize(editNotesHook: false);
            Eq("initialize", (string)plain.SelectToken("request.subtype"), "sent without the hook too (for the models' full IDs)");
            Eq(null, plain.SelectToken("request.hooks"), "no hooks then");

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
            var catalog = Models_2_1_328();
            Eq($"Done · Haiku 5.5 · 24.6k tok · {AgentTask.FormatCost(forkPriced.CostUsd)}", ToolCallView.AgentUsageLine(forkPriced, catalog), "named as Claude Code names it");
            Eq("Haiku 4.5", new AgentTask { Model = "claude-haiku-4-5-20251001" }.ModelName(catalog), "an older model stands out");
            Eq("haiku", new AgentTask { Model = "claude-haiku-9" }.ModelName(catalog), "not listed: the family");
            Eq(null, new AgentTask().ModelName(catalog));
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

        private static void Test_SessionAllowRules()
        {
            // As the prompt's answer sends them (2.1.329's suggestion, scoped to the session; `--live-session-rules`)
            var updated = JArray.Parse(@"[{""type"":""addRules"",""rules"":[{""toolName"":""Bash"",""ruleContent"":""mkdir -p cvs-folder""},{""toolName"":""WebFetch""}],""behavior"":""allow"",""destination"":""session""},
                {""type"":""addRules"",""rules"":[{""toolName"":""Bash"",""ruleContent"":""npm test""}],""behavior"":""allow"",""destination"":""localSettings""},
                {""type"":""setMode"",""mode"":""acceptEdits"",""destination"":""session""}]");
            Eq("Bash(mkdir -p cvs-folder)|WebFetch", string.Join("|", ClaudeSession.SessionRules(updated)), "the session's allow rules only: a settings file keeps the others itself");
            Eq(0, ClaudeSession.SessionRules(null).Count);

            var o = new ClaudeLaunchOptions();
            o.SessionAllowRules.Add("Bash(mkdir -p cvs-folder)");
            string a = ClaudeArgsBuilder.Build(o);
            True(a.Contains("--allowedTools \"Bash(mkdir -p cvs-folder)\""), a);
            o.SdkMcpServers.Add(TestIdeServer());
            a = ClaudeArgsBuilder.Build(o);
            True(a.Contains("--allowedTools \"mcp__visualstudio__get_error_list,Bash(mkdir -p cvs-folder)\""), "one flag with both: " + a);
            Eq(1, System.Text.RegularExpressions.Regex.Matches(a, "--allowedTools").Count);
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

        /// <summary>
        /// Real Bash permission requests from Claude Code 2.1.325 (temp folder replaced by C:\work). Since 2.1.325 an
        /// IDE or SDK host is offered a rule only when saving it would allow the command again: a compound command
        /// with a redirect comes with no suggestion, so the card has no "Always allow".
        /// </summary>
        private static void Test_PermissionPrompt_RealCli_Suggestions_2_1_325()
        {
            var parser = new ClaudeStreamParser();
            var prompts = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.325-permission-suggestions.jsonl"))
                .SelectMany(l => parser.Parse(l)).Where(e => e.Kind == ClaudeEventKind.PermissionRequest)
                .Select(e => PermissionPrompt.From(e.ToolName, e.ToolInput, e.Payload)).ToList();
            Eq(4, prompts.Count, "four permission requests");

            Eq("mkdir probe-dir && echo made > probe-dir/a.txt", prompts[0].Detail, "compound command");
            True(prompts[0].AlwaysAllow == null && prompts[0].AlwaysAllowRules.Count == 0, "no suggestion, no Always allow");

            Eq("Bash(curl -s https://example.invalid/probe325)", string.Join(",", prompts[1].AlwaysAllowRules), "exact rule for curl");
            Eq("Bash(mkdir probe-dir2 *)", string.Join(",", prompts[2].AlwaysAllowRules), "prefix rule for mkdir");
            Eq(1, prompts[2].AlwaysAllow.Count, "addDirectories and setMode are not sent back");
            Eq("Bash(echo made *)", string.Join(",", prompts[3].AlwaysAllowRules), "prefix rule for a single redirect");
            True(prompts.Skip(1).All(p => p.AlwaysAllowDestination == "localSettings"), "saved in settings.local.json");
        }

        /// <summary>
        /// A real request from Claude Code 2.1.327 when a PreToolUse hook answers "ask" with a reason: the card says
        /// which hook asks and why. A generic reason (2.1.325's "This command requires approval") is not shown.
        /// </summary>
        private static void Test_PermissionPrompt_RealCli_HookReason_2_1_327()
        {
            var line = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.327-hook-ask-reason.jsonl"));
            var ev = new ClaudeStreamParser().Parse(line).Single();
            Eq(ClaudeEventKind.PermissionRequest, ev.Kind, "permission request");
            var p = PermissionPrompt.From(ev.ToolName, ev.ToolInput, ev.Payload);
            Eq("echo hello > out.txt", p.Detail, "the command");
            Eq("PreToolUse hook (settings): Probe hook: confirm before running", p.HookReason, "the hook's reason");

            var generic = PermissionPrompt.From("Bash", JObject.Parse("{\"command\":\"ls\"}"),
                JObject.Parse("{\"decision_reason\":\"This command requires approval\",\"decision_reason_type\":\"other\"}"));
            Eq(null, generic.HookReason, "a reason that isn't a hook's is not shown");
            Eq(null, PermissionPrompt.From("Bash", JObject.Parse("{\"command\":\"ls\"}"), null).HookReason, "no request");
        }

        /// <summary>
        /// The models in Claude Code 2.1.328's initialize answer (only that list kept): each alias with the full ID it
        /// resolves to. Haiku's full ID is what keeps plan mode on Haiku (the alias becomes Sonnet there).
        /// </summary>
        private static void Test_ResolvedModelId_RealCli_2_1_328()
        {
            var init = Initialize_2_1_328();
            Eq("claude-haiku-5-5", ClaudeSession.ResolvedModelId(init, "haiku"));
            Eq("claude-sonnet-5-5", ClaudeSession.ResolvedModelId(init, "sonnet"));
            Eq("claude-haiku-5-5", ClaudeSession.ResolvedModelId(init, "HAIKU"), "any case");
            Eq(null, ClaudeSession.ResolvedModelId(init, "claude-opus-4-6"), "a full ID resolves to itself: nothing to switch to");
            Eq(null, ClaudeSession.ResolvedModelId(init, "nosuch"), "not listed");
            Eq(null, ClaudeSession.ResolvedModelId(new JObject(), "haiku"), "an older Claude Code without the list");
            Eq(null, ClaudeSession.ResolvedModelId(null, "haiku"));
        }

        /// <summary>The model picker: Claude Code's names and the prices from Options → Pricing.</summary>
        private static void Test_ModelPickerText()
        {
            var models = Models_2_1_328();
            var prices = new Tokens.PriceTable();
            Eq("Haiku 5.5", ModelPickerText.Label(ModelChoice.Haiku, models));
            Eq("Fable 5.2", ModelPickerText.Label(ModelChoice.Fable, models));
            Eq("Haiku", ModelPickerText.Label(ModelChoice.Haiku, null), "before Claude Code has said");
            Eq("Auto (route each prompt)", ModelPickerText.Label(ModelChoice.Auto, models));
            Eq("$0.10 / $0.50 per Mtok in / out", ModelPickerText.Description(ModelChoice.Haiku, models, prices));
            Eq("$4 / $20 per Mtok in / out", ModelPickerText.Description(ModelChoice.Opus, models, prices));
            Eq("$8 / $40 per Mtok in / out; 2× the Opus price; Auto never picks it", ModelPickerText.Description(ModelChoice.Fable, models, prices));
            Eq(null, ModelPickerText.Description(ModelChoice.Auto, models, prices));

            // ANTHROPIC_DEFAULT_HAIKU_MODEL pointing "haiku" at Haiku 4.5: its name, and no price until one is learned
            var older = ModelCatalog.Parse(JObject.Parse("{\"models\":[{\"value\":\"haiku\",\"resolvedModel\":\"claude-haiku-4-5-20251001\",\"displayName\":\"Haiku 4.5\"}]}"));
            Eq("Haiku 4.5", ModelPickerText.Label(ModelChoice.Haiku, older));
            Eq(null, ModelPickerText.Description(ModelChoice.Haiku, older, prices), "Options doesn't price it");
            prices.Learn(new Dictionary<string, ModelUsageEntry> { ["claude-haiku-4-5-20251001"] = new ModelUsageEntry { Usage = new TokenUsage { Input = 1000, Output = 1000 }, CostUsd = 0.006 } });
            Eq("$1 / $5 per Mtok in / out", ModelPickerText.Description(ModelChoice.Haiku, older, prices), "learned");
            Eq("$2.50", ModelPickerText.Usd(2.5));

            // The effort picker, for the model the next turn goes to
            Eq("claude-haiku-5-5", ModelPickerText.NextModel(ModelChoice.Haiku, models, "claude-opus-4-6"));
            Eq("claude-opus-4-6", ModelPickerText.NextModel(ModelChoice.Auto, models, "claude-opus-4-6"), "Auto: the last answer's");
            Eq("haiku", ModelPickerText.NextModel(ModelChoice.Haiku, null, null), "before Claude Code has said");
            Eq("Opus 4.6 takes no xhigh: Claude Code uses high", ModelPickerText.EffortNote(EffortChoice.XHigh, models, "claude-opus-4-6"));
            Eq(null, ModelPickerText.EffortNote(EffortChoice.Max, models, "claude-opus-4-6"));
            Eq("Haiku 4.5 takes no effort: Claude Code drops it", ModelPickerText.EffortNote(EffortChoice.Low, models, "claude-haiku-4-5-20251001"));
            Eq(null, ModelPickerText.EffortNote(EffortChoice.Auto, models, "claude-haiku-4-5-20251001"), "Auto sends none");
            Eq(null, ModelPickerText.EffortNote(EffortChoice.XHigh, models, null), "no model yet");
            Eq(null, ModelPickerText.EffortNote(EffortChoice.XHigh, null, "claude-opus-4-6"), "no catalog");
        }

        /// <summary>The same capture as a <see cref="ModelCatalog"/>: names, current models and effort levels.</summary>
        private static void Test_ModelCatalog_RealCli_2_1_328()
        {
            var models = Models_2_1_328();
            True(models != null && models.Models.Count == 14, "14 models listed");
            Eq("Haiku 5.5", models.DisplayName("claude-haiku-5-5"));
            Eq("Opus 5.5", models.DisplayName("claude-opus-5-5"), "Opus's own entry, not \"Default (recommended)\"");
            Eq("Opus 5.5", models.DisplayName("claude-opus-5-5[1m]"), "the 1M-context suffix");
            Eq("Opus 5.5", models.DisplayName("opus"), "an alias");
            Eq("Haiku 4.5", models.DisplayName("claude-haiku-4-5-20251001"));
            Eq(null, models.DisplayName("default"), "names a choice, not a model");
            Eq(null, models.DisplayName("claude-nosuch-1"));

            Eq(true, models.SupportsEffort("claude-opus-5-5", "XHigh"));
            Eq(false, models.SupportsEffort("claude-opus-4-6", "xhigh"), "Opus 4.6: low, medium, high, max");
            Eq(false, models.SupportsEffort("claude-haiku-4-5-20251001", "low"), "Haiku 4.5 takes no effort");
            Eq(null, models.SupportsEffort("claude-nosuch-1", "low"), "not listed: don't know");
            Eq(null, models.SupportsEffort("claude-opus-4-6", null));

            Eq(null, SessionNotices.UnsupportedEffort(models, "claude-opus-5-5", "max"));
            Eq(null, SessionNotices.UnsupportedEffort(null, "claude-opus-4-6", "xhigh"), "no catalog");
            Eq("Opus 4.6 answered, and it doesn't take xhigh effort: Claude Code uses high instead (it takes low, medium, high, max).",
                SessionNotices.UnsupportedEffort(models, "claude-opus-4-6", "xhigh"));
            Eq("Haiku 4.5 answered, and it takes no effort setting: Claude Code drops the high effort you picked, so it changes nothing.",
                SessionNotices.UnsupportedEffort(models, "claude-haiku-4-5-20251001", "high"));

            // What 2.1.328's get_settings said it applied, launched with each model and effort (no error in any case)
            var applied = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.328-effort-applied.json")));
            foreach (var c in applied["cases"])
                Eq((string)c["applied"]["effort"] ?? "", models.EffortApplied((string)c["sent"]["model"], (string)c["sent"]["effort"]), (string)c["sent"]["model"]);
            Eq("max", models.EffortApplied("claude-opus-4-6", "max"));
            Eq(null, models.EffortApplied("claude-nosuch-1", "high"), "not listed");

            var older = ModelCatalog.Parse(JObject.Parse("{\"models\":[{\"value\":\"haiku\",\"resolvedModel\":\"claude-haiku-5-5\",\"displayName\":\"Haiku 5.5\"}]}"));
            Eq(null, older.SupportsEffort("claude-haiku-5-5", "high"), "a Claude Code that lists no levels: don't know");
            Eq(null, ModelCatalog.Parse(new JObject()), "no list");
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
            Eq("default", PermissionModeChoice.Default.ToCli(), "Manual is spelled out: with no flag 2.1.326 can start in auto");
            True(ClaudeArgsBuilder.Build(new ClaudeLaunchOptions { PermissionMode = PermissionModeChoice.Default }).Contains("--permission-mode default"), "Manual launch flag");
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

        private static void Test_AlwaysAllow_NotSaved()
        {
            // 2.1.323: a secret-looking rule still comes as a localSettings suggestion, but is kept for the session only.
            var req = JObject.Parse("{\"permission_suggestions\":[{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"Bash\",\"ruleContent\":\"curl -s https://example.invalid/probe4\"}],\"behavior\":\"allow\",\"destination\":\"localSettings\"}]}");
            var p = PermissionPrompt.From("Bash", JObject.Parse("{\"command\":\"curl -s https://example.invalid/probe4\"}"), req);
            Eq("localSettings", p.AlwaysAllowDestination);
            Eq(Path.Combine("C:\\repo", ".claude", "settings.local.json"), PermissionPrompt.SettingsPath(p.AlwaysAllowDestination, "C:\\repo", "C:\\home"));
            Eq(Path.Combine("C:\\home", ".claude", "settings.json"), PermissionPrompt.SettingsPath("userSettings", "C:\\repo", "C:\\home"));
            Eq(null, PermissionPrompt.SettingsPath("session", "C:\\repo", "C:\\home"), "session rules have no file");

            // As 2.1.323 wrote it in an untracked repo, and left it in a tracked one.
            string saved = "{\n  \"permissions\": {\n    \"allow\": [\n      \"Bash(curl -s https://example.invalid/probe4)\"\n    ]\n  }\n}";
            Eq(0, PermissionPrompt.MissingRules(saved, p.AlwaysAllowRules).Count, "saved");
            Eq(1, PermissionPrompt.MissingRules("{}", p.AlwaysAllowRules).Count, "kept for the session only");
            Eq(1, PermissionPrompt.MissingRules(null, p.AlwaysAllowRules).Count, "no file");
            Eq(1, PermissionPrompt.MissingRules("not json", p.AlwaysAllowRules).Count, "unreadable file");
            True(PermissionPrompt.NotSavedNote(p.AlwaysAllowRules, ".claude/settings.local.json").Contains("Bash(curl -s https://example.invalid/probe4)"), "names the rule");
        }

        private static void Test_HookHeartbeat()
        {
            // Captured with 2.1.323: a PreToolUse hook callback answered after 65 s.
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.323-hook-heartbeat.jsonl"))
                .SelectMany(l => new ClaudeStreamParser().Parse(l)).ToList();
            Eq(2, events.Count);
            True(events.All(e => e.Kind == ClaudeEventKind.HookRunning && e.ToolName == "PreToolUse" && e.ToolUseId == "toolu_019aDiAWFMiXQD4guEnWjKqp"), "hook event and its tool call");
            Eq(30_000L, events[0].DurationMs);
            Eq(60_000L, events[1].DurationMs);
            Eq(0, new ClaudeStreamParser().Parse("{\"type\":\"tool_progress\",\"tool_use_id\":\"t1\",\"tool_name\":\"Bash\",\"elapsed_time_seconds\":5}").Count, "other tool progress is ignored");
        }

        /// <summary>The question card's keys (Phase 85's rules, in Core since Phase 86).</summary>
        private static void Test_QuestionCardKeys()
        {
            // Three options and the Other box: stops 0, 1, 2 and 3 (the Other box)
            var down = QuestionCardKeys.OnStop(CardKey.Down, 0, 4, multiSelect: false);
            True(down.Focus == 1 && down.PickFocused && !down.Next, "↓ on a single choice: the next option, picked");
            var multiDown = QuestionCardKeys.OnStop(CardKey.Down, 0, 4, multiSelect: true);
            True(multiDown.Focus == 1 && !multiDown.PickFocused, "on a multiple choice only the focus moves");
            var toOther = QuestionCardKeys.OnStop(CardKey.Down, 2, 4, false);
            True(toOther.Focus == 3 && !toOther.PickFocused, "into the Other box: focusing it picks Other, nothing to toggle");
            var fromOther = QuestionCardKeys.OnStop(CardKey.Up, 3, 4, false);
            True(fromOther.Focus == 2 && fromOther.PickFocused, "↑ out of the Other box after typing: the last option");
            True(QuestionCardKeys.OnStop(CardKey.Up, 0, 4, false).Focus == null, "the first: stays");
            True(QuestionCardKeys.OnStop(CardKey.Down, 3, 4, false).Focus == null, "the last: stays, not into the next question");
            var enter = QuestionCardKeys.OnStop(CardKey.Enter, 1, 4, true);
            True(enter.PickHere && enter.Next && enter.Focus == null, "Enter picks the focused option and goes on");
            var enterOther = QuestionCardKeys.OnStop(CardKey.Enter, 3, 4, false);
            True(!enterOther.PickHere && enterOther.Next, "Enter in the Other box goes on with what was typed");
            // The Other check box
            var otherUp = QuestionCardKeys.OnOtherCheckBox(CardKey.Up, 4, false);
            True(otherUp.Focus == 2 && otherUp.PickFocused, "↑: the last option");
            True(QuestionCardKeys.OnOtherCheckBox(CardKey.Enter, 4, false).Focus == 3, "Enter: its text box");
            True(QuestionCardKeys.OnOtherCheckBox(CardKey.Up, 1, false).Focus == 0, "no options: its text box");
            // Questions
            Eq(1, QuestionCardKeys.NextQuestion(0, 2));
            Eq(null, QuestionCardKeys.NextQuestion(1, 2), "after the last: answer");
        }

        private static void Test_TerminalText_Strip()
        {
            // Phase 104: what 2.1.332's Bash result held for printf '\033[32mgreen\033[0m plain\n'
            Eq("green plain", TerminalText.Strip("\u001b[32mgreen\u001b[0m plain"), "the probe's stdout");
            Eq("Passed!  - Failed: 0", TerminalText.Strip("\u001b[1;32mPassed!\u001b[0m  - Failed: \u001b[38;5;196m0\u001b[m"), "bold, 256 colors, a bare reset");
            Eq("abc", TerminalText.Strip("a\u001b[2Kb\u001b[1Gc"), "erase and cursor codes");
            Eq("title gone, link text", TerminalText.Strip("\u001b]0;my title\atitle gone, \u001b]8;;https://x.y\u001b\\link text\u001b]8;;\u001b\\"), "window title and a link");
            Eq("x", TerminalText.Strip("\u001b(Bx\u001b="), "two-character codes");
            Eq("red", TerminalText.Strip("\u009b31mred"), "the one-character CSI");
            Eq("cut", TerminalText.Strip("cut\u001b[3"), "a code cut off at the end");
            string plain = "no codes [32m here";
            True(ReferenceEquals(plain, TerminalText.Strip(plain)), "text without ESC is returned as it is");
            Eq(null, TerminalText.Strip(null), "null");
        }

        private static void Test_TerminalText_Spans()
        {
            // Phase 104: the colors the codes say
            string S(string t) => string.Join("|", TerminalText.Spans(t));
            Eq("c2:green|: plain", S("\u001b[32mgreen\u001b[0m plain"), "the probe's output");
            Eq("c9b:FAIL|b: x|:  y", S("\u001b[1;91mFAIL\u001b[39m x\u001b[22m  y"), "bright, bold, default color, normal weight");
            Eq("c9:a|c2:b|c11:c", S("\u001b[38;5;196ma\u001b[38;2;0;128;0mb\u001b[38;5;226mc"), "256-color and RGB as the nearest of 16");
            Eq("c1:redx", S("\u001b[31mred\u001b[2Kx"), "other codes left out; same style joined");
            Eq(":on blue", S("\u001b[44mon blue"), "a background isn't drawn");
            Eq("c4:fg", S("\u001b[48;5;21;34mfg"), "a 256-color background skipped, the foreground kept");
            Eq("c1:a|:z", S("\u001b[31ma\u001b[mz"), "a bare reset");
            Eq(0, TerminalText.Spans("").Count, "nothing");
            Eq(8, TerminalText.From256(240), "a dark gray");
        }

        private static void Test_TerminalText_Collapse()
        {
            // Phase 104: a progress bar's redraws keep their last state
            Eq("start\nDownloading 100%\ndone", TerminalText.Collapse("start\nDownloading 10%\rDownloading 55%\rDownloading 100%\ndone"), "the last state");
            Eq("a\nb", TerminalText.Collapse("a\r\nb"), "CRLF is a line break");
            Eq("Built", TerminalText.Collapse("Building...\rBuilt\r"), "a trailing \\r");
            Eq("\u001b[32m100%\u001b[0m", TerminalText.Collapse("10%\r\u001b[32m100%\u001b[0m\r\u001b[K"), "a redraw that only erases doesn't count");
            string plain = "no carriage returns";
            True(ReferenceEquals(plain, TerminalText.Collapse(plain)), "as it is");
        }

        private static void Test_OutputFold_LongOutput()
        {
            // Phase 105: long output shows its start and its end
            string Out(int n) => string.Join("\n", Enumerable.Range(1, n).Select(k => "line " + k)) + "\n";
            True(!ClaudeVS.Core.Markdown.OutputFold.Folds(Out(200)), "200 lines aren't folded");
            True(ClaudeVS.Core.Markdown.OutputFold.Folds(Out(201)), "201 are");
            string o = Out(1000);
            Eq(1000, ClaudeVS.Core.Markdown.OutputFold.Lines(o), "a last line break isn't a line");
            Eq("line 1", ClaudeVS.Core.Markdown.OutputFold.Head(o).Split('\n')[0], "the head starts at the start");
            Eq(20, ClaudeVS.Core.Markdown.OutputFold.Head(o).Split('\n').Length, "20 lines");
            Eq("line 921", ClaudeVS.Core.Markdown.OutputFold.Tail(o).Split('\n')[0], "the tail is the last 80");
            Eq("line 1000", ClaudeVS.Core.Markdown.OutputFold.Tail(o).Split('\n').Last(), "to the end");
            Eq(900, ClaudeVS.Core.Markdown.OutputFold.Hidden(o), "between");
            Eq("Show all 1000 lines", ClaudeVS.Core.Markdown.OutputFold.ShowAll(o), "the link");
            Eq(0, ClaudeVS.Core.Markdown.OutputFold.Lines(" \n"), "blank");
        }

        private static void Test_TerminalText_ForCopy()
        {
            // Phase 105: Copy output gives all of it, cleaned
            Eq("green plain\r\nProgress 100%\r\nend", TerminalText.ForCopy("\u001b[32mgreen\u001b[0m plain\nProgress 10%\rProgress 100%\r\nend\n\n"), "codes, progress, line breaks, blank end");
            string many = string.Join("\n", Enumerable.Range(1, 500).Select(k => "l" + k));
            Eq(500, TerminalText.ForCopy(many).Split(new[] { "\r\n" }, StringSplitOptions.None).Length, "all of a folded output");
            Eq("", TerminalText.ForCopy(null), "nothing");
        }

        private static void Test_RunResults_Tests()
        {
            // Phase 106: a test run's own summary on the row's ⎿ line
            string L(string o) => RunResults.Tests(o)?.Line;
            Eq("✗ 2 failed · 20 passed · 1 skipped", L("Build succeeded.\nFailed!  - Failed:     2, Passed:    10, Skipped:     1, Total:    13, Duration: 1 s - A.Tests.dll (net8.0)\n" +
                "Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 1 s - B.Tests.dll (net8.0)"), "dotnet test, two projects summed");
            Eq("✓ 12 passed", L("Test summary: total: 12, failed: 0, succeeded: 12, skipped: 0, duration: 1.1s"), "Microsoft.Testing.Platform");
            Eq("✗ 2 failed · 10 passed · 1 skipped", L("Test Suites: 1 failed, 3 passed, 4 total\nTests:       2 failed, 1 skipped, 10 passed, 13 total\nTime: 2 s"), "Jest");
            Eq("✗ 2 failed · 10 passed · 1 skipped", L(" Test Files  1 failed | 3 passed (4)\n      Tests  2 failed | 10 passed | 1 skipped (13)"), "Vitest");
            Eq("✗ 3 failed · 10 passed · 1 skipped", L("...\n===== 2 failed, 10 passed, 1 skipped, 1 error in 0.52s =====\n"), "pytest, an error is a failure");
            Eq("✓ 5 passed", L("============ 5 passed in 0.01s ============"), "pytest, all passed");
            Eq("✗ 1 package failed · 2 passed", L("ok  \texample.com/a\t0.01s\nok  \texample.com/b\t(cached)\n--- FAIL: TestX\nFAIL\texample.com/c\t0.02s"), "go test");
            Eq(null, L("Build succeeded.\n    0 Warning(s)"), "not a test run");
            Eq(null, L(null), "nothing");

            // The row: the summary, failed in red even though the command exited non-zero, the rest a click away
            var row = ToolCallView.Summarize("Bash", "Determining projects to restore...\nFailed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5", isError: true);
            Eq("✗ 1 failed · 4 passed", row.Lines.Single(), "the line");
            True(row.IsError, "red");
            Eq(2, row.MoreLines, "the output behind a click");
            Eq("Read 1 line", ToolCallView.Summarize("Read", "Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1", false).Lines.Single(), "only commands");
        }

        private static void Test_RunResults_Build()
        {
            // Phase 106: a build's summary on the row's ⎿ line
            string L(string o) => RunResults.Build(o)?.Line;
            Eq("Build succeeded · 3 warnings", L("  Demo -> C:\\r\\bin\\Demo.dll\n\nBuild succeeded.\n    3 Warning(s)\n    0 Error(s)\n\nTime Elapsed 00:00:02.10"), "MSBuild");
            Eq("Build failed · 2 errors · 1 warning", L("src\\a.cs(3,1): error CS1002\n\nBuild FAILED.\n\n    1 Warning(s)\n    2 Error(s)"), "MSBuild failed");
            Eq("Build failed · 2 errors · 1 warning", L("Restore complete (0.4s)\n  Demo failed with 2 error(s) and 1 warning(s) (0.9s)\n\nBuild failed with 2 error(s) and 1 warning(s) in 1.6s"), "dotnet build's terminal logger");
            Eq("Build succeeded", L("Build succeeded in 0.8s"), "terminal logger, clean");
            Eq("Build failed · 3 errors", L("src/a.ts:1:7 - error TS2322\n\nFound 3 errors in 2 files."), "tsc");
            Eq("Build failed · 2 errors", L("error[E0425]: cannot find value `x`\nerror: could not compile `app` (bin \"app\") due to 2 previous errors"), "Cargo failed");
            Eq("Build succeeded", L("   Compiling app v0.1.0\n    Finished `dev` profile [unoptimized + debuginfo] target(s) in 1.2s"), "Cargo");
            Eq(null, L("hello world"), "not a build");

            var row = ToolCallView.Summarize("Bash", "Determining projects to restore...\nBuild FAILED.\n    0 Warning(s)\n    1 Error(s)", isError: true);
            Eq("Build failed · 1 error", row.Lines.Single(), "the row");
            True(row.IsError, "red");
            Eq("✓ 4 passed", ToolCallView.Summarize("Bash", "Build succeeded.\n    0 Warning(s)\nPassed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4", false).Lines.Single(), "a test run builds first: tests win");
        }

        private static void Test_ToolData_Shapes()
        {
            Eq("Read 4 lines", ToolCallView.FromData("Read", JObject.Parse("{\"file\":{\"numLines\":4,\"startLine\":1,\"totalLines\":4}}")), "whole file");
            Eq("Read 2 lines", ToolCallView.FromData("Read", JObject.Parse("{\"file\":{\"content\":\"a\\nb\",\"numLines\":2,\"startLine\":1,\"totalLines\":2}}")), "no newline at the end");
            // 2.1.331: a gateway's tool call with no name fails with an error and the session goes on; its row still shows
            var parser = new ClaudeStreamParser();
            foreach (string nameField in new[] { "", "\"name\":\"\",", "\"name\":null," })
            {
                var call = parser.Parse("{\"type\":\"assistant\",\"message\":{\"id\":\"m" + nameField.Length + "\",\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_x\"," + nameField + "\"input\":{}}]}}").Single();
                Eq(ClaudeStreamParser.UnnamedTool, call.ToolName, "no name: " + nameField);
                Eq("Tool", ToolCallView.Title(call.ToolName, call.ToolInput).Name, "the row's name");
            }
            var activity = parser.Parse("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_y\",\"input\":{}}}}").Single();
            Eq("Tool", activity.ToolName, "the activity row while it is written");
            var unnamedError = ToolCallView.Summarize(ClaudeStreamParser.UnnamedTool, "Error: No such tool available: ", true);
            True(unnamedError.IsError && unnamedError.Lines.Count == 1, "its error shows");
            string unnamedFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(unnamedFile, "{\"type\":\"assistant\",\"uuid\":\"a1\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_z\",\"input\":{}}]}}\n");
                Eq("Tool", SessionTranscript.Read(unnamedFile).Items.Single(i => i.Kind == TranscriptItemKind.Tool).ToolName, "on resume too");
            }
            finally { File.Delete(unnamedFile); }
            // 2.1.332: a Web Fetch the site refused, or a redirect not followed, comes without is_error; the status says it
            var fetchParser = new ClaudeStreamParser();
            var fetches = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.332-webfetch-status.jsonl"))
                .SelectMany(l => fetchParser.Parse(l)).Where(e => e.Kind == ClaudeEventKind.ToolResult).ToList();
            Eq(2, fetches.Count, "two fetches");
            True(fetches.All(f => !f.IsError), "Claude Code flags neither as an error");
            var notFound = ToolCallView.Summarize("WebFetch", fetches[0].ResultText, fetches[0].IsError, data: fetches[0].ResultData);
            True(notFound.IsError, "a 404 is a failed row");
            Eq("HTTP 404 Not Found", notFound.Lines.Single());
            True(ToolCallView.FailedFetch("WebFetch", fetches[0].ResultData), "the chat marks it failed");
            var redirect = ToolCallView.Summarize("WebFetch", fetches[1].ResultText, fetches[1].IsError, data: fetches[1].ResultData);
            True(!redirect.IsError && !ToolCallView.FailedFetch("WebFetch", fetches[1].ResultData), "a redirect isn't a failure");
            Eq("Not followed: 302 Found → https://example.com/", redirect.Lines.Single());
            Eq(null, ToolCallView.WebFetchStatus(JObject.Parse("{\"code\":200,\"codeText\":\"OK\",\"bytes\":172}")), "a page that came: as before");
            Eq(null, ToolCallView.WebFetchStatus(null), "no data (older rows)");
            // On resume, from the session file's toolUseResult (the same fields)
            string fetchFile = Path.GetTempFileName();
            try
            {
                File.WriteAllLines(fetchFile, new[]
                {
                    "{\"type\":\"assistant\",\"uuid\":\"a1\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_f\",\"name\":\"WebFetch\",\"input\":{\"url\":\"https://httpbin.org/status/404\"}}]}}",
                    "{\"type\":\"user\",\"uuid\":\"u1\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"toolu_f\",\"type\":\"tool_result\",\"content\":\"The server returned HTTP 404 Not Found.\"}]},\"toolUseResult\":{\"bytes\":0,\"code\":404,\"codeText\":\"Not Found\",\"result\":\"The server returned HTTP 404 Not Found.\",\"durationMs\":790,\"url\":\"https://httpbin.org/status/404\"}}",
                });
                var row = SessionTranscript.Read(fetchFile).Items.Single(i => i.Kind == TranscriptItemKind.Tool);
                True(ToolCallView.FailedFetch(row.ToolName, row.ResultData), "resumed: still a failure");
                Eq("HTTP 404 Not Found", ToolCallView.Summarize(row.ToolName, row.ResultText, row.IsError, data: row.ResultData).Lines.Single(), "resumed");
            }
            finally { File.Delete(fetchFile); }
            // 2.1.330: a Grep pattern with a line break and no multiline is refused by ripgrep, not "No matches"
            var rejected = new ClaudeStreamParser().Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.330-grep-rejected.jsonl")).Trim())
                .Single(e => e.Kind == ClaudeEventKind.ToolResult);
            True(rejected.IsError && rejected.ResultData == null, "an error, its tool_use_result a string");
            var grepRow = ToolCallView.Summarize("Grep", rejected.ResultText, rejected.IsError, data: rejected.ResultData);
            True(grepRow.IsError, "the row is an error");
            Eq("ripgrep refused the pattern: a line break needs multiline: true", grepRow.Lines.Single(), "says why and what fixes it, in one line (Phase 83)");
            // Another refusal: ripgrep's own reason
            var glob = ToolCallView.Summarize("Grep", "Search failed — ripgrep rejected the pattern, glob, or file type without searching:\nrg: unrecognized file type: csharpp", true);
            True(glob.IsError, "an error");
            Eq("ripgrep refused it: unrecognized file type: csharpp", glob.Lines.Single());
            Eq("Permission denied", ToolCallView.Summarize("Grep", "Permission denied", true).Lines.Single(), "other Grep errors as before");
            // The real 2.1.329 reads: Claude Code counts the empty line after a final newline (and calls an empty file one line).
            var reads = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.329-read-lines.jsonl"))
                .Select(l => ToolCallView.FromData("Read", (JObject)JObject.Parse(l)["tool_use_result"])).ToList();
            Eq("Read an empty file", reads[0], "empty.txt: 1 of 1, content \"\"");
            Eq("Read 1 line", reads[1], "nl.txt, only a newline: 2 of 2");
            Eq("Read 4 of 10 lines (from line 7)", reads[2], "ten.txt from line 7 to the end: 5 of 11");
            Eq("Read 3 of 11 lines (from line 2)", reads[3], "ten.txt lines 2-4, the file not on this machine: Claude Code's count");
            // Before the end, the file on disk says whether its last line ends in a newline (Phase 74)
            var middle = (JObject)JObject.Parse(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.329-read-lines.jsonl"))[3])["tool_use_result"]["file"];
            Eq(10L, ToolCallView.ReadLines(middle, _ => true).Total, "ends in a newline: ten lines");
            Eq(11L, ToolCallView.ReadLines(middle, _ => false).Total, "no newline at the end: eleven");
            Eq(3L, ToolCallView.ReadLines(middle, _ => true).Read, "the lines read stay");
            // The transcript passes its own probe (a cache, then a background check, Phase 76): Summarize hands it on
            var middleData = new JObject { ["file"] = middle };
            Eq("Read 3 of 10 lines (from line 2)", ToolCallView.Summarize("Read", "", false, null, middleData, _ => true).Lines.Single(), "Summarize asks the probe");
            string asked = null;
            Eq("Read 3 of 11 lines (from line 2)", ToolCallView.Summarize("Read", "", false, null, middleData, p => { asked = p; return null; }).Lines.Single(), "not known yet: Claude Code's count");
            Eq("C:/repo/ten.txt", asked, "for that file");
            string tmp = Path.Combine(Path.GetTempPath(), "cvs-nl-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(tmp, "a\nb\n"); Eq(true, ToolCallView.FileEndsInNewline(tmp), "LF");
                File.WriteAllText(tmp, "a\r\nb\r\n"); Eq(true, ToolCallView.FileEndsInNewline(tmp), "CRLF");
                File.WriteAllText(tmp, "a\nb"); Eq(false, ToolCallView.FileEndsInNewline(tmp), "none");
                File.WriteAllText(tmp, "a\nb\n", new System.Text.UnicodeEncoding(false, false)); Eq(true, ToolCallView.FileEndsInNewline(tmp), "UTF-16 LE");
                File.WriteAllText(tmp, ""); Eq(null, ToolCallView.FileEndsInNewline(tmp), "empty");
            }
            finally { File.Delete(tmp); }
            Eq(null, ToolCallView.FileEndsInNewline(tmp), "gone");
            Eq("Read 2 lines", reads[4], "crlf.txt, two CRLF lines: content comes with \\n, 3 of 3");
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

        /// <summary>
        /// Real calls from Claude Code 2.1.327 that a PreToolUse hook decided without asking: one denied with a
        /// reason, one allowed. The rows say "denied by a hook" (the reason in the tooltip) and "allowed by a hook".
        /// </summary>
        private static void Test_Parser_HookDecisions_RealCli_2_1_327()
        {
            var parser = new ClaudeStreamParser();
            var results = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.327-hook-decisions.jsonl"))
                .SelectMany(l => parser.Parse(l)).Where(e => e.Kind == ClaudeEventKind.ToolResult).ToList();
            Eq(2, results.Count, "two tool results");

            var denied = results[0];
            True(denied.IsError, "the denied call is an error");
            Eq("hook", denied.DeniedBy, "rejected by a hook");
            Eq(null, denied.AllowedBy, "not allowed");
            var note = ToolCallView.DeniedNote(denied.DeniedBy, denied.ResultText);
            Eq("denied by a hook", note?.Text, "row note");
            True(note.Value.Tip.Contains("Its reason: Probe hook: no writes here"), "the hook's reason in the tooltip: " + note.Value.Tip);

            var allowed = results[1];
            Eq("hook", allowed.AllowedBy, "accepted by a hook");
            Eq(null, allowed.DeniedBy, "not denied");
            Eq("allowed by a hook", ToolCallView.AllowedNote("Bash", allowed.AllowedBy)?.Text, "row note");

            Eq("No writes", ToolCallView.HookDenyReason("Error: PreToolUse:Bash hook error: No writes"), "with Error: in front");
            Eq(null, ToolCallView.HookDenyReason("  "), "nothing");
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
