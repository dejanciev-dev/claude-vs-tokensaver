using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ClaudeVS.Core.Cli;

namespace ClaudeVS.Core.Tests
{
    /// <summary>
    /// Opt-in checks against the real claude CLI, each in an empty temp folder: `dotnet run --project
    /// tests/ClaudeVS.Core.Tests -- --live-all` runs them all (see <see cref="RunAll"/>), `--live …` one each.
    /// Each request carries the CLI's ~25k-token system context: a few cents per test on Haiku. Never runs in CI or
    /// `dotnet test`.
    /// </summary>
    internal static class LiveSmoke
    {
        private static readonly object CostGate = new object();
        private static double _spent;

        /// <summary>What the live tests of this run have cost so far (USD, the CLI's own total_cost_usd).</summary>
        public static double Spent { get { lock (CostGate) return _spent; } }

        private static void AddCost(double usd) { if (usd > 0) lock (CostGate) _spent += usd; }

        // The last total_cost_usd seen per Claude session id: a process started with --resume carries on the resumed
        // session's total (seen with 2.1.322: $0.0518 before the relaunch, $0.0539 after one $0.0021 turn), so counting
        // per process would count the first part twice.
        private static readonly Dictionary<string, double> LastCostBySession = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>A session whose turns' cost adds to <see cref="Spent"/>; every live test makes its sessions here.</summary>
        private static ClaudeSession NewSession(ClaudeLaunchOptions options)
        {
            var s = new ClaudeSession(options);
            double lastHere = 0; // when the session id is not known yet
            s.EventReceived += (_, ev) =>
            {
                if (ev.Kind != ClaudeEventKind.TurnCompleted || !ev.TotalCostUsd.HasValue) return;
                double now = ev.TotalCostUsd.Value; // cumulative for the session
                string id = s.SessionId;
                lock (CostGate)
                {
                    double last = id != null && LastCostBySession.TryGetValue(id, out var l) ? l : lastHere;
                    _spent += now >= last ? now - last : now; // lower: the total started again
                    if (id != null) LastCostBySession[id] = now;
                    lastHere = now;
                }
            };
            return s;
        }

        /// <summary>
        /// `--live-all`: every live test in order, with a summary and the total cost (about $0.26 on Haiku with
        /// 2.1.328). Plan mode is in it since Phase 67: Claude Code turns the alias "haiku" into Sonnet there, and the
        /// session switches to Haiku's full ID, so "answered by Haiku" checks that this still works. Exit code 0 only
        /// when every test passed.
        /// </summary>
        public static int RunAll()
        {
            if (ClaudeCliLocator.Resolve("") == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            const string ask = "Use the AskUserQuestion tool once to ask me which fruits I like (multi-select: Apple, Banana, Cherry), then repeat my answer in one line.";
            var steps = new (string Name, Func<int> Run)[]
            {
                ("dialogs", RunDialogs),
                ("mcp", () => RunMcp()),
                ("mcp-restart", RunMcpRestart), // an MCP server turned off stays off after a restart in place (Phase 77); no cost
                ("haiku-router", RunHaikuRouter),
                ("setup", () => RunSetup(null)),
                ("edit-review", RunEditReview),
                ("edit-review-interrupt", () => RunEditReviewInterrupted(null)),
                ("edit-stop-allow", () => RunEditReviewInterrupted(null, allowPlain: true)),
                ("live (Accept Edits)", () => Run(null, PermissionModeChoice.AcceptEdits)),
                ("live (Plan)", () => Run(null, PermissionModeChoice.Plan)), // Haiku's full ID keeps plan mode on Haiku
                ("ask", () => RunInteractive(null, PermissionModeChoice.Default, ask)),
                ("webfetch", RunWebFetch), // a site's 404 is a failed row though Claude Code sends no error (Phase 89)
                ("session", RunSession),
                ("long-prompt", RunLongPrompt),
                ("prompt-tokens", RunPromptTokens),
                ("env", RunEnv), // a setting written, the process restarted into the same conversation (Phase 73)
                ("restart", RunRestart), // a CLAUDE.md written mid-conversation, read after a restart in place (Phase 76)
                ("session-rules", RunSessionRules), // what you allowed for the session, kept across a restart in place (Phase 79)
            };
            var results = new List<(string Name, int Code, double Cost, TimeSpan Time)>();
            foreach (var (name, run) in steps)
            {
                Console.WriteLine($"=== {name}");
                double before = Spent;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int code;
                try { code = run(); }
                catch (Exception ex) { Console.WriteLine("  FAIL threw: " + (ex.InnerException ?? ex).Message); code = 1; }
                results.Add((name, code, Spent - before, sw.Elapsed));
                Console.WriteLine();
            }
            Console.WriteLine("=== summary");
            foreach (var r in results)
                Console.WriteLine($"  {(r.Code == 0 ? "PASS" : r.Code == 2 ? "SKIP" : "FAIL"),-4}  {r.Name,-24} ${r.Cost,7:0.0000}  {r.Time.TotalSeconds,5:0} s");
            int failed = results.Count(r => r.Code != 0);
            Console.WriteLine($"  {results.Count - failed} of {results.Count} passed · total ${Spent:0.0000} · {TimeSpan.FromTicks(results.Sum(r => r.Time.Ticks)).TotalMinutes:0.0} min");
            return failed == 0 ? 0 : 1;
        }
        /// <summary>
        /// `-- --live-haiku-router`: warms up the trimmed Haiku router, routes four prompts, and compares each with the
        /// local rules. Checks the answers are usable, warm routes take under 4 s, and the cost stays tiny (~$0.001).
        /// </summary>
        public static int RunHaikuRouter()
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            var failures = new List<string>();
            void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) failures.Add(what); }
            var local = new Tokens.ModelRouter();
            using (var router = new Tokens.HaikuRouter(""))
            {
                var t0 = DateTime.UtcNow;
                router.WarmUp();
                Thread.Sleep(TimeSpan.FromSeconds(25)); // start-up and the warm-up request (slow since 2.1.323) happen while idle
                string[] prompts =
                {
                    "rename the variable x to count in Program.cs",
                    "design a plugin architecture for our billing service across 6 projects and plan the migration of the old code",
                    "the login page throws a NullReferenceException since the last merge, find and fix it",
                    "write a commit message for these changes",
                };
                foreach (var p in prompts)
                {
                    var req = new Tokens.RouteRequest { Prompt = p };
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var d = router.RouteAsync(req, TimeSpan.FromSeconds(15)).Result;
                    sw.Stop();
                    var l = local.Route(req);
                    Console.WriteLine($"  {sw.ElapsedMilliseconds,5} ms  haiku → {(d == null ? "none" : d.Model.ToCli() + "/" + d.Effort.ToCli())}  local → {l.Model.ToCli()}/{l.Effort.ToCli()}  · {p}");
                    if (d != null) Console.WriteLine("         " + d.Reason);
                    Check(d != null, "usable answer");
                    Check(sw.ElapsedMilliseconds < 4000, "answered in under 4 s (warm)");
                }
                AddCost(router.TotalCostUsd); // its own trimmed process, not a NewSession
                Console.WriteLine($"  {router.Routes} routes, total ${router.TotalCostUsd:0.00000} (last ${router.LastRouteCostUsd:0.00000}), {(DateTime.UtcNow - t0).TotalSeconds:0} s including warm-up");
                Check(router.Routes == prompts.Length, "every route counted");
                Check(router.TotalCostUsd > 0 && router.TotalCostUsd < 0.01, "cost under a cent for all routes");
            }
            Console.WriteLine(failures.Count == 0 ? "PASS live haiku router" : "FAIL live haiku router");
            return failures.Count == 0 ? 0 : 1;
        }

        /// <summary>
        /// `-- --live-session`: the meter against the real CLI. Haiku turn → context % vs get_context_usage; switch to
        /// Sonnet/medium mid-session (set_model, apply_flag_settings) → which model answered; relaunch with --resume →
        /// session cost keeps adding up; /compact → context drops. About $0.05–0.10 (one Sonnet turn writes the cache).
        /// </summary>
        /// <summary>
        /// `--live-reads [rawPath]` (not in --live-all, about $0.02 on Haiku): the main agent reads a ~6k-token file, then
        /// an Explore agent reads it again. Checks what Phases 59–63 assume of the stream: a subagent's tool results
        /// carry its Agent call (<c>parent_tool_use_id</c>), Read paths match, and <see cref="Tokens.TurnFileActivity"/>
        /// counts the agent's read as a re-read of the conversation's file, on the agent's task.
        /// </summary>
        public static int RunReads(string rawLogPath)
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var sbFile = new StringBuilder("namespace Probe\n{\n    public class Big\n    {\n");
            for (int i = 0; i < 300; i++) sbFile.Append($"        public int Method{i:000}(int x) => x * {i} + {i % 7}; // keeps the file large enough to matter\n");
            sbFile.Append("    }\n}\n");
            string big = Path.Combine(dir, "Big.cs");
            File.WriteAllText(big, sbFile.ToString());

            var failures = new List<string>();
            void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) failures.Add(what); }
            var raw = new StringBuilder();
            var files = new Tokens.TurnFileActivity();
            var tasks = new AgentTaskList();
            var done = new AutoResetEvent(false);
            int subagentResults = 0, subagentResultsWithParent = 0;
            string rereadAgent = null;
            double cost = 0;
            var options = new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = PermissionModeChoice.Default };
            using (var session = NewSession(options))
            {
                session.RawLine += (s, e) => { lock (raw) raw.Append(e.Direction == RawLineDirection.Stdin ? ">> " : "<< ").AppendLine(e.Line); };
                session.EventReceived += (s, ev) =>
                {
                    files.Observe(ev);
                    if (ev.Kind == ClaudeEventKind.Task) tasks.Apply(ev.Payload);
                    if (ev.Kind == ClaudeEventKind.ToolResult && ev.IsSubagent) { subagentResults++; if (!string.IsNullOrEmpty(ev.ParentToolUseId)) subagentResultsWithParent++; }
                    if (ev.Kind == ClaudeEventKind.ToolResult && files.LastReread is var (readId, agentId, _, t) && readId == ev.ToolUseId)
                    { rereadAgent = agentId; tasks.AddReread(agentId, t); }
                    if (ev.Kind == ClaudeEventKind.PermissionRequest) session.RespondToPermissionAsync(ev.RequestId, ev.ToolInput, true, null).Wait();
                    if (ev.Kind == ClaudeEventKind.TurnCompleted) { cost = ev.TotalCostUsd ?? cost; done.Set(); }
                };
                try
                {
                    session.SendPromptAsync("Use the Read tool to read Big.cs in full, then reply with only the number of methods in it.").Wait();
                    Check(done.WaitOne(TimeSpan.FromSeconds(180)), "read turn finished");
                    Check(files.ReadTokensByFile.Keys.Any(k => k.EndsWith("Big.cs", StringComparison.OrdinalIgnoreCase)), "the main agent's read counted: " + string.Join(", ", files.ReadTokensByFile.Keys));
                    Check(files.SubagentRereadTokens == 0, "no re-read yet");
                    files.Reset();

                    session.SendPromptAsync("Use the Agent tool with subagent_type Explore and run_in_background false, and wait for its answer. Tell the agent to read Big.cs itself with the Read tool and return the name of its last method. Then reply with that name only.").Wait();
                    Check(done.WaitOne(TimeSpan.FromSeconds(240)), "agent turn finished");
                    // 2.1.327 may start the agent in the background anyway (is_backgrounded: true) and end the turn at
                    // once: wait for the agent to finish, and for the turn Claude Code starts with its answer
                    var until = DateTime.UtcNow.AddSeconds(150);
                    while (tasks.Tasks.Any(x => x.IsAgent && x.IsRunning) && DateTime.UtcNow < until) Thread.Sleep(500);
                    var agentTask = tasks.Tasks.FirstOrDefault(x => x.IsAgent);
                    Console.WriteLine($"  agent: {agentTask?.SubagentType}, backgrounded {agentTask?.IsBackgrounded}, {agentTask?.Status}");
                    if (agentTask?.IsBackgrounded == true) done.WaitOne(TimeSpan.FromSeconds(60));
                    Console.WriteLine($"  subagent tool results: {subagentResults}, with their Agent call: {subagentResultsWithParent}");
                    Check(subagentResults > 0 && subagentResultsWithParent == subagentResults, "a subagent's tool results carry parent_tool_use_id");
                    Console.WriteLine($"  subagent re-read: ~{files.SubagentRereadTokens:N0} tok");
                    Check(files.SubagentRereadTokens > 1000, "the agent's read of Big.cs counted as a re-read of the conversation's file");
                    var task = tasks.Tasks.FirstOrDefault(x => x.ToolUseId == rereadAgent);
                    Check(task != null && task.IsAgent && task.RereadTokens > 0, $"on the agent's task ({task?.SubagentType}, re-read ~{task?.RereadTokens:N0})");
                    Console.WriteLine($"  cost ${cost:0.0000}");
                }
                finally
                {
                    if (!string.IsNullOrEmpty(rawLogPath)) lock (raw) File.WriteAllText(rawLogPath, raw.ToString());
                }
            }
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Console.WriteLine(failures.Count == 0 ? "PASS live reads" : "FAIL live reads");
            return failures.Count == 0 ? 0 : 1;
        }

        /// <summary>
        /// `--live-models [rawPath]` (not in --live-all, about $0.05): Haiku answers and an Explore agent runs on Sonnet.
        /// Checks what Phases 68–70 assume of a conversation on two models: each model's billed cost
        /// (<c>model_usage_since_last_result</c>) adds up to Claude Code's total, the agent's task knows its model and is
        /// named as Claude Code names it, and Copy session summary has the <c>By model:</c> line.
        /// </summary>
        public static int RunModels(string rawLogPath)
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Notes.txt"), "The answer is 42.\n");
            var failures = new List<string>();
            void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) failures.Add(what); }
            var raw = new StringBuilder();
            var meter = new Tokens.TokenMeter();
            var tasks = new AgentTaskList();
            var done = new AutoResetEvent(false);
            double total = 0;
            using (var session = NewSession(new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = PermissionModeChoice.Default }))
            {
                session.RawLine += (s, e) => { lock (raw) raw.Append(e.Direction == RawLineDirection.Stdin ? ">> " : "<< ").AppendLine(e.Line); };
                session.EventReceived += (s, ev) =>
                {
                    lock (meter)
                    {
                        meter.Observe(ev);
                        if (ev.Kind == ClaudeEventKind.Task) tasks.Apply(ev.Payload);
                        if (ev.Kind == ClaudeEventKind.AssistantUsage && ev.IsSubagent) tasks.ObserveRequest(ev, meter.RequestCostUsd); // as the chat does
                    }
                    if (ev.Kind == ClaudeEventKind.PermissionRequest) session.RespondToPermissionAsync(ev.RequestId, ev.ToolInput, true, null).Wait();
                    if (ev.Kind == ClaudeEventKind.TurnCompleted) { total = ev.TotalCostUsd ?? total; done.Set(); }
                };
                try
                {
                    session.SendPromptAsync("Use the Agent tool with subagent_type Explore, model sonnet and run_in_background false, and wait for its answer. Tell the agent to read Notes.txt and return the number in it. Then reply with that number only.").Wait();
                    Check(done.WaitOne(TimeSpan.FromSeconds(240)), "turn finished");
                    // The agent may still run in the background (see --live-reads): wait for it, and the turn its answer starts
                    var until = DateTime.UtcNow.AddSeconds(150);
                    while (tasks.Tasks.Any(x => x.IsAgent && x.IsRunning) && DateTime.UtcNow < until) Thread.Sleep(500);
                    var agent = tasks.Tasks.FirstOrDefault(x => x.IsAgent);
                    if (agent?.IsBackgrounded == true) done.WaitOne(TimeSpan.FromSeconds(60));

                    var models = session.Models;
                    Console.WriteLine($"  agent: {agent?.SubagentType}, model {agent?.Model}, {agent?.Status}");
                    Check(models != null, "the models list came with initialize");
                    Check(agent?.Model?.IndexOf("sonnet", StringComparison.OrdinalIgnoreCase) >= 0, "the agent ran on Sonnet");
                    Check(agent?.ModelName(models) == "Sonnet 5.5", $"named as Claude Code names it ({agent?.ModelName(models)})");

                    Dictionary<string, double> byModel;
                    lock (meter) byModel = meter.SessionCostByModel.ToDictionary(kv => kv.Key, kv => kv.Value);
                    Console.WriteLine("  billed: " + string.Join(", ", byModel.Select(kv => $"{kv.Key} ${kv.Value:0.0000}")) + $"; Claude Code's total ${total:0.0000}");
                    Check(byModel.Count == 2 && byModel.Keys.Any(k => k.Contains("haiku")) && byModel.Keys.Any(k => k.Contains("sonnet")), "billed per model: Haiku and Sonnet");
                    Check(Math.Abs(byModel.Values.Sum() - total) < 1e-6, "they add up to Claude Code's total");

                    string summary;
                    lock (meter) summary = Tokens.SessionSummary.Format(meter, "live", null, null, models);
                    string line = summary.Split('\n').FirstOrDefault(l => l.StartsWith("By model: ", StringComparison.Ordinal));
                    Console.WriteLine("  " + (line ?? "(no By model line)"));
                    Check(line != null && line.Contains("Sonnet 5.5 ") && line.Contains("Haiku 5.5 "), "Copy session summary: By model, both named");
                }
                finally
                {
                    if (!string.IsNullOrEmpty(rawLogPath)) lock (raw) File.WriteAllText(rawLogPath, raw.ToString());
                }
            }
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Console.WriteLine(failures.Count == 0 ? "PASS live models" : "FAIL live models");
            return failures.Count == 0 ? 0 : 1;
        }

        public static int RunSession()
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var failures = new List<string>();
            void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) failures.Add(what); }
            var meter = new Tokens.TokenMeter();
            var done = new AutoResetEvent(false);
            string turnModel = null;
            bool compacted = false;

            ClaudeSession Open(string resumeId)
            {
                var s = NewSession(new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = PermissionModeChoice.Default, ResumeSessionId = resumeId });
                s.EventReceived += (o, ev) =>
                {
                    meter.Observe(ev);
                    if (ev.Kind == ClaudeEventKind.AssistantUsage && !ev.IsSubagent && ev.Model != null) turnModel = ev.Model;
                    if (ev.Kind == ClaudeEventKind.Compacted) compacted = true;
                    if (ev.Kind == ClaudeEventKind.PermissionRequest) s.RespondToPermissionAsync(ev.RequestId, ev.ToolInput, false, "Not in this test.").Wait();
                    if (ev.Kind == ClaudeEventKind.TurnCompleted) done.Set();
                };
                return s;
            }
            bool Turn(ClaudeSession s, string prompt) { turnModel = null; s.SendPromptAsync(prompt).Wait(); return done.WaitOne(TimeSpan.FromSeconds(180)); }

            string sessionId;
            double costBeforeRelaunch;
            using (var s1 = Open(null))
            {
                Check(Turn(s1, "Reply with the single word: one"), "haiku turn finished");
                Check(turnModel?.Contains("haiku") == true, "answered by " + turnModel);
                var usage = s1.GetContextUsageAsync().Result;
                Console.WriteLine($"  meter: {meter.ContextTokens:N0} tok ({meter.ContextPercent:0.0}%) · CLI /context: {usage?.TotalTokens:N0} tok ({usage?.Percentage:0.0}%)");
                Check(usage != null && Math.Abs(meter.ContextPercent - usage.Percentage) <= 2.0, "context % within 2 points of the CLI's /context");
                Check(usage != null && Math.Abs(meter.ContextTokens - usage.TotalTokens) <= usage.TotalTokens * 0.02, "context tokens within 2% of the CLI's /context");

                s1.SetModelAsync(ModelChoice.Sonnet).Wait();
                s1.SetEffortAsync(EffortChoice.Medium).Wait();
                // The switch must take: Claude Code says the effort it applies (sent as "effort", 2.1.326 kept the launch one)
                var applied = s1.AppliedEffort; // read by the switch itself (get_settings), as the chat uses it
                Check(applied == "medium", "effort switched mid-session: applied.effort = " + (applied ?? "(none)"));
                Check(Turn(s1, "Reply with the single word: two"), "turn after switching finished");
                Check(turnModel?.Contains("sonnet") == true, "set_model mid-session: answered by " + turnModel);
                s1.SetModelAsync(ModelChoice.Haiku).Wait();
                sessionId = s1.SessionId;
                costBeforeRelaunch = meter.SessionCostUsd;
                Console.WriteLine($"  cost before relaunch ${costBeforeRelaunch:0.0000}");
            }

            using (var s2 = Open(sessionId))
            {
                Check(Turn(s2, "Reply with the single word: three"), "turn after relaunch (--resume) finished");
                Console.WriteLine($"  cost after relaunch ${meter.SessionCostUsd:0.0000} (last turn ${meter.LastTurnCostUsd:0.0000})");
                Check(meter.SessionCostUsd > costBeforeRelaunch && meter.LastTurnCostUsd > 0, "session cost kept adding up across the relaunch");

                long before = meter.ContextTokens;
                Check(Turn(s2, "/compact"), "/compact finished");
                Console.WriteLine($"  context before /compact {before:N0} tok, after {meter.ContextTokens:N0} tok");
                Check(compacted, "compact_boundary seen");
                // As the chat does after every turn: take the CLI's own measurement.
                var after = s2.GetContextUsageAsync().Result;
                if (after != null) meter.ApplyContextUsage(after.TotalTokens, after.MaxTokens);
                Console.WriteLine($"  after /compact the CLI measures {after?.TotalTokens:N0} tok; meter shows {meter.ContextTokens:N0}");
                Check(meter.ContextTokens > 0 && meter.ContextTokens < before, "meter shows the smaller, non-zero context after compaction");
                Console.WriteLine($"  total cost ${meter.SessionCostUsd:0.0000}");
            }
            try { Directory.Delete(dir, true); } catch (IOException) { }
            Console.WriteLine(failures.Count == 0 ? "PASS live session" : "FAIL live session");
            return failures.Count == 0 ? 0 : 1;
        }

        /// <summary>
        /// `-- --live-setup [rawLogPath]`: installs the token-saving settings and hook into a temp .NET project, then
        /// has Haiku (1) run `dotnet build` and (2) read a file under bin/. Checks that the command that runs is the
        /// quiet one and that bin/ is not readable. About $0.02; needs the .NET SDK.
        /// </summary>
        public static int RunSetup(string rawLogPath)
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "bin", "Debug"));
            File.WriteAllText(Path.Combine(dir, "Hello.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(dir, "Program.cs"), "System.Console.WriteLine(\"hi\");");
            File.WriteAllText(Path.Combine(dir, "bin", "Debug", "secret.txt"), "BIN-SECRET-42");
            Console.WriteLine("  setup: " + Setup.TokenSaverSetup.Apply(dir));

            var failures = new List<string>();
            void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) failures.Add(what); }
            var raw = new StringBuilder();
            var commands = new List<string>();      // shell commands as Claude Code asked to run them (after hooks)
            var shellTools = new List<string>();
            var reply = new StringBuilder();
            var resultSizes = new List<int>();
            var done = new AutoResetEvent(false);
            double cost = 0;
            var options = new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = PermissionModeChoice.Default };
            using (var session = NewSession(options))
            {
                session.RawLine += (s, e) => { lock (raw) raw.Append(e.Direction == RawLineDirection.Stdin ? ">> " : "<< ").AppendLine(e.Line); };
                session.EventReceived += (s, ev) =>
                {
                    if (ev.Kind == ClaudeEventKind.ToolUse && (ev.ToolName == "Bash" || ev.ToolName == "PowerShell")) shellTools.Add(ev.ToolName + ": " + (string)ev.ToolInput?["command"]);
                    if (ev.Kind == ClaudeEventKind.PermissionRequest)
                    {
                        string cmd = (string)ev.ToolInput?["command"];
                        if (cmd != null) commands.Add(ev.ToolName + ": " + cmd);
                        bool allow = cmd != null && (cmd.TrimStart().StartsWith("dotnet build", StringComparison.OrdinalIgnoreCase) || cmd.TrimStart().StartsWith("1..800"));
                        session.RespondToPermissionAsync(ev.RequestId, ev.ToolInput, allow, allow ? null : "Not allowed in this test.").Wait();
                    }
                    if (ev.Kind == ClaudeEventKind.ToolResult) lock (resultSizes) resultSizes.Add(ev.ResultChars);
                    if (ev.Kind == ClaudeEventKind.TextDelta || ev.Kind == ClaudeEventKind.AssistantText) lock (reply) reply.Append(ev.Text);
                    if (ev.Kind == ClaudeEventKind.TurnCompleted) { cost = ev.TotalCostUsd ?? cost; done.Set(); }
                };
                try
                {
                    session.SendPromptAsync("Run exactly this shell command in the current folder: dotnet build  (nothing else, no extra flags). Then reply with one word: ok or failed.").Wait();
                    Check(done.WaitOne(TimeSpan.FromSeconds(240)), "build turn finished");
                    Console.WriteLine("  shell tool calls: " + string.Join(" | ", shellTools));
                    Console.WriteLine("  permission asked for: " + string.Join(" | ", commands));
                    Check(commands.Concat(shellTools).Any(c => c.Contains("-v q -nologo")), "dotnet build ran in its quiet form (-v q -nologo)");

                    reply.Clear();
                    session.SendPromptAsync("Use the Read tool to read bin/Debug/secret.txt and reply with its exact contents, or say BLOCKED if you cannot read it.").Wait();
                    Check(done.WaitOne(TimeSpan.FromSeconds(120)), "read turn finished");
                    string r = reply.ToString().Trim();
                    Console.WriteLine("  reply: " + r);
                    Check(!r.Contains("BIN-SECRET-42"), "bin/ content not read");

                    // About 22k characters of output (between our 15k cap and the CLI default of 30k); with bashOutputMaxChars 15000 Claude should get a capped preview.
                    lock (resultSizes) resultSizes.Clear();
                    session.SendPromptAsync("Run exactly this PowerShell command: 1..800 | ForEach-Object { \"line $_ of a long test output\" }   Then reply with one word: done.").Wait();
                    Check(done.WaitOne(TimeSpan.FromSeconds(120)), "long output turn finished");
                    int biggest; lock (resultSizes) biggest = resultSizes.DefaultIfEmpty(0).Max();
                    Console.WriteLine($"  largest tool result Claude received: {biggest:N0} chars");
                    Check(biggest > 0 && biggest <= Setup.TokenSaverSetup.DefaultBashMaxOutput + 2000, "shell output capped near " + Setup.TokenSaverSetup.DefaultBashMaxOutput);
                    Console.WriteLine($"  cost ${cost:0.0000}");
                }
                finally
                {
                    if (!string.IsNullOrEmpty(rawLogPath)) lock (raw) File.WriteAllText(rawLogPath, raw.ToString());
                }
            }
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Console.WriteLine(failures.Count == 0 ? "PASS live setup" : "FAIL live setup");
            return failures.Count == 0 ? 0 : 1;
        }

        /// <summary>
        /// `-- --live-dialogs`: reads every dialog (status, rules, hooks, memory, usage, output styles) and sets an output
        /// style, in a temp folder. Sends no prompt, so it makes no model call and costs nothing.
        /// </summary>
        public static int RunDialogs()
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, ".claude"));
            File.WriteAllText(Path.Combine(dir, ".claude", "settings.json"), "{\"permissions\":{\"deny\":[\"Read(./secrets/**)\"]},\"hooks\":{\"PreToolUse\":[{\"matcher\":\"Bash\",\"hooks\":[{\"type\":\"command\",\"command\":\"echo hi\"}]}]}}");
            var failures = new List<string>();
            void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) failures.Add(what); }
            using (var session = NewSession(new ClaudeLaunchOptions { WorkingDirectory = dir, PermissionMode = PermissionModeChoice.Plan, EditNotesHook = true }))
            {
                try
                {
                    var status = session.GetStatusAsync().Result;
                    Check(status?.Any(r => r.Label == "Version") == true, "status: " + status?.FirstOrDefault(r => r.Label == "Version")?.Value);
                    var rules = session.GetPermissionRulesAsync().Result;
                    Check(rules?.Any(r => r.Behavior == "deny" && r.Rule == "Read(./secrets/**)") == true, $"permission rules: {rules?.Count}");
                    var hooks = session.GetHooksAsync().Result;
                    Check(hooks?.Any(h => h.Event == "PreToolUse" && h.Text == "echo hi") == true, $"hooks: {hooks?.Count}");
                    var memory = session.GetMemoryAsync().Result;
                    Check(memory?.Entries.Any(e => e.Kind == "project") == true, $"memory: {memory?.Entries.Count} entries");
                    var usage = session.GetUsageAsync().Result;
                    Check(usage != null, $"usage: {usage?.PlanName}, {usage?.Limits.Count} limits");
                    var styles = session.GetOutputStylesAsync().Result;
                    Check(styles?.Available.Contains("Explanatory") == true, $"output styles: {(styles == null ? "none" : string.Join(", ", styles.Available))} (current {styles?.Current})");
                    Check(session.SetOutputStyleAsync("Explanatory").Result, "set output style");
                    string local = Path.Combine(dir, ".claude", "settings.local.json");
                    Check(File.Exists(local) && File.ReadAllText(local).Contains("Explanatory"), "written to settings.local.json");
                    Check(session.GetOutputStylesAsync().Result?.Current == "Explanatory", "current style updated");
                }
                finally { session.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }
            }
            Console.WriteLine(failures.Count == 0 ? "PASS live dialogs" : "FAIL live dialogs");
            return failures.Count == 0 ? 0 : 1;
        }

        /// <summary>
        /// `--live-webfetch` (in --live-all, about a cent): a Web Fetch the site refuses (Phase 89). Claude Code sends
        /// no error for it, only <c>tool_use_result.code</c>/<c>codeText</c>; the row drawn from the real stream, as the
        /// chat draws it, must be a failure reading "HTTP 404 Not Found". Haiku hands fetches to a web-fetch subagent
        /// unless Agent is disallowed. A fetch that fails another way (no network, the site down) is a SKIP.
        /// </summary>
        public static int RunWebFetch()
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            const string url = "https://httpbin.org/status/404";
            var events = new BlockingCollection<ClaudeEvent>();
            var session = NewSession(new ClaudeLaunchOptions
            {
                WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low,
                ExtraArguments = "--disallowedTools Agent --allowedTools WebFetch",
            });
            session.EventReceived += (s, e) => events.Add(e);
            ClaudeEvent result = null;
            try
            {
                session.SendPromptAsync($"Call WebFetch yourself once on {url} with the prompt 'say what the page is'. Do not fetch anything else. Then reply with one word: done.").GetAwaiter().GetResult();
                string fetchId = null;
                var deadline = DateTime.UtcNow.AddSeconds(120);
                while (DateTime.UtcNow < deadline)
                {
                    if (!events.TryTake(out var e, TimeSpan.FromMilliseconds(200))) continue;
                    if (e.Kind == ClaudeEventKind.ToolUse && e.ToolName == "WebFetch" && !e.IsSubagent) fetchId = e.ToolUseId;
                    if (e.Kind == ClaudeEventKind.ToolResult && e.ToolUseId != null && e.ToolUseId == fetchId) result = e;
                    if (e.Kind == ClaudeEventKind.TurnCompleted) { Console.WriteLine($"  turn completed: error={e.IsError}, cost=${e.TotalCostUsd:0.0000}"); break; }
                }
            }
            finally { session.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }

            if (result == null) { Console.WriteLine("FAIL live webfetch: no WebFetch result from the main agent"); return 1; }
            int? code = result.ResultData?["code"]?.Type == Newtonsoft.Json.Linq.JTokenType.Integer ? (int?)result.ResultData["code"] : null;
            Console.WriteLine($"  is_error={result.IsError}, code={code?.ToString() ?? "none"}, text: {(result.ResultText ?? "").Split('\n')[0]}");
            if (code != 404) { Console.WriteLine("SKIP live webfetch: the site didn't answer 404 (network or site), nothing to check"); return 2; }
            var row = ToolCallView.Summarize("WebFetch", result.ResultText, result.IsError, data: result.ResultData);
            Console.WriteLine("  row: " + string.Join(" | ", row.Lines) + (row.IsError ? " (failed)" : ""));
            bool ok = row.IsError && row.Lines.SingleOrDefault() == "HTTP 404 Not Found" && ToolCallView.FailedFetch("WebFetch", result.ResultData);
            Console.WriteLine(ok ? "PASS live webfetch" : "FAIL live webfetch: the row should be a failure reading \"HTTP 404 Not Found\"");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// `--live-prices`: are the default prices what Claude Code bills? Per model (Haiku, Sonnet, Opus, Fable) two
        /// turns in a trimmed process (no tools, no settings, a ~4.5k-token system prompt cached for 1 hour): the
        /// first writes the cache, the second reads it. Each turn is one request, so its usage priced with
        /// <see cref="Tokens.PriceTable"/> must equal the growth of the CLI's total_cost_usd. About $0.19 (Opus and
        /// Fable cache writes), so not part of --live-all. Checked by hand this way in Phases 18 and 20. Then Haiku 4.5, a
        /// model Options doesn't price: its first turn teaches its price from costUSD, which must give the second turn's
        /// bill. `--live-prices older` runs only that (about $0.01).
        /// </summary>
        /// <summary>
        /// `--live-env`: the one-click output-limit fix (Phase 73) against the real CLI. Haiku answers once; then
        /// <c>CLAUDE_CODE_MAX_OUTPUT_TOKENS=100</c> goes into the project's <c>.claude/settings.local.json</c>
        /// (<see cref="Setup.TokenSaverSetup.SetLocalEnv"/>). A long answer before the restart shows whether the running
        /// process picks it up by itself; then <see cref="ClaudeSession.StopProcess"/>, and the same long answer must be
        /// cut off at the limit (Claude Code's "Output token limit hit." retry), in the same conversation. The result's
        /// <c>modelUsage.maxOutputTokens</c> is the model's maximum (128000 for Haiku 5.5 even with the variable set), so
        /// the cut-off is the proof. About $0.01.
        /// </summary>
        /// <summary>
        /// `--live-restart`: Restart Claude Code (Phase 76) against the real CLI. Haiku answers once; then a
        /// <c>CLAUDE.md</c> appears that asks for a code word at the end of every answer. The same process answers
        /// again (does it see the file?), then <see cref="ClaudeSession.StopProcess"/>, and the next answer, in the same
        /// conversation, must end with the word. About $0.005.
        /// </summary>
        public static int RunRestart()
        {
            if (ClaudeCliLocator.Resolve("") == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var turns = new BlockingCollection<ClaudeEvent>();
            int failed = 0, starts = 0;
            void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failed++; }
            const string word = "PINEAPPLE";
            try
            {
                using (var session = NewSession(new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = PermissionModeChoice.Default, ExtraArguments = "--tools \"\"" }))
                {
                    session.RawLine += (s, e) => { if (e.Direction == RawLineDirection.Stdin && e.Line.Contains("\"subtype\":\"initialize\"")) Interlocked.Increment(ref starts); };
                    session.EventReceived += (s, ev) => { if (ev.Kind == ClaudeEventKind.TurnCompleted) turns.Add(ev); };
                    ClaudeEvent Ask(string prompt, string label)
                    {
                        session.SendPromptAsync(prompt).Wait();
                        if (!turns.TryTake(out var t, TimeSpan.FromSeconds(120))) throw new TimeoutException("no answer to " + label);
                        Console.WriteLine($"  {label}: \"{(t.Text ?? "").Trim()}\" · cache read {t.Usage.CacheRead:N0} · write {t.Usage.CacheCreation:N0} · session {t.SessionId}");
                        return t;
                    }

                    var first = Ask("Name a colour, one word.", "turn 1");
                    Check(!(first.Text ?? "").Contains(word), "turn 1 without the word");
                    File.WriteAllText(Path.Combine(dir, "rules.md"), "# More rules\n\nNone yet.\n");
                    File.WriteAllText(Path.Combine(dir, "CLAUDE.md"), $"# Rule\n\nEnd every answer with the word {word}, in capitals, as its last word.\n\nMore rules: @rules.md\n");
                    var same = Ask("Name a fruit, one word.", "turn 2, same process");
                    Console.WriteLine((same.Text ?? "").Contains(word) ? "  note the running process read the new CLAUDE.md" : "  note the running process didn't read the new CLAUDE.md: a restart is needed");

                    session.StopProcess();
                    var after = Ask("Name an animal, one word.", "turn 3, restarted");
                    Check(starts == 2, $"started again ({starts} processes)");
                    Check(after.SessionId == first.SessionId, "same conversation (--resume)");
                    Check((after.Text ?? "").Contains(word), "turn 3 follows the new CLAUDE.md");

                    // What a restart costs when nothing changed (Phase 79): the next turn reads the cache, or writes it again?
                    session.StopProcess();
                    var plain = Ask("Name a tree, one word.", "turn 4, restarted, nothing changed");
                    Check(plain.SessionId == first.SessionId, "turn 4 in the same conversation");
                    Console.WriteLine(plain.Usage.CacheCreation > plain.Usage.CacheRead
                        ? "  note a restart writes the cache again even with nothing changed"
                        : "  note a restart with nothing changed reads the cache");

                    // A file CLAUDE.md imports (Phase 81): edited alone, read only after a restart too?
                    const string first2 = "KIWI";
                    File.WriteAllText(Path.Combine(dir, "rules.md"), $"# More rules\n\nStart every answer with the word {first2}, in capitals, as its first word.\n");
                    var importSame = Ask("Name a river, one word.", "turn 5, rules.md edited, same process");
                    Console.WriteLine((importSame.Text ?? "").Contains(first2) ? "  note the running process read the edited import" : "  note the running process didn't read the edited import: a restart is needed");
                    session.StopProcess();
                    var importAfter = Ask("Name a mountain, one word.", "turn 6, restarted");
                    Check((importAfter.Text ?? "").Contains(first2), "turn 6 follows the edited import");
                }
            }
            catch (TimeoutException ex) { Console.WriteLine("FAIL live restart: " + ex.Message); return 1; }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
            Console.WriteLine(failed == 0 ? "PASS live restart" : $"FAIL live restart: {failed} check(s)");
            return failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// `--live-session-rules`: a shell command allowed "for this session" (the prompt's suggestion, with
        /// <c>destination: "session"</c>), then a restart in place (Phase 79): does Claude Code ask again? It must not, or
        /// the extension answers for you. About $0.01.
        /// </summary>
        public static int RunSessionRules()
        {
            if (ClaudeCliLocator.Resolve("") == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var turns = new BlockingCollection<ClaudeEvent>();
            int asked = 0, failed = 0;
            void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failed++; }
            string Prompt() => "Run this exact shell command with the Bash tool, even if the folder exists, then reply with the word done: mkdir -p cvs-folder";
            try
            {
                using (var session = NewSession(new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = PermissionModeChoice.Default }))
                {
                    session.EventReceived += (s, ev) =>
                    {
                        if (ev.Kind == ClaudeEventKind.TurnCompleted) turns.Add(ev);
                        if (ev.Kind != ClaudeEventKind.PermissionRequest) return;
                        Interlocked.Increment(ref asked);
                        // Allow, and for this session: Claude Code's own suggestion, scoped to the session
                        var suggestions = (ev.Payload as Newtonsoft.Json.Linq.JObject)?["permission_suggestions"] as Newtonsoft.Json.Linq.JArray;
                        var forSession = new Newtonsoft.Json.Linq.JArray();
                        foreach (var sug in suggestions ?? new Newtonsoft.Json.Linq.JArray())
                            if (sug is Newtonsoft.Json.Linq.JObject o && (string)o["type"] == "addRules") { var c = (Newtonsoft.Json.Linq.JObject)o.DeepClone(); c["destination"] = "session"; forSession.Add(c); }
                        Console.WriteLine($"  asked for {ev.ToolName}; allowing for the session: {forSession.ToString(Newtonsoft.Json.Formatting.None)}");
                        session.RespondToPermissionAsync(ev.RequestId, ev.ToolInput, true, null, forSession).Wait();
                    };
                    ClaudeEvent Ask(string label)
                    {
                        int before = asked;
                        session.SendPromptAsync(Prompt()).Wait();
                        if (!turns.TryTake(out var t, TimeSpan.FromSeconds(120))) throw new TimeoutException("no answer to " + label);
                        Console.WriteLine($"  {label}: \"{(t.Text ?? "").Trim()}\" · asked {asked - before}× · session {t.SessionId}");
                        return t;
                    }
                    int a0 = asked; var first = Ask("turn 1"); int firstAsked = asked - a0;
                    Check(firstAsked == 1, "turn 1 asked once");
                    int a1 = asked; Ask("turn 2, same process"); Check(asked - a1 == 0, "turn 2 not asked: allowed for the session");
                    session.StopProcess();
                    int a2 = asked; var third = Ask("turn 3, restarted");
                    Check(third.SessionId == first.SessionId, "same conversation");
                    Console.WriteLine(asked - a2 == 0 ? "  note the session's allow survived the restart" : "  note a restart forgets what you allowed for the session");
                    Check(asked - a2 == 0, "turn 3 not asked after the restart");
                }
            }
            catch (TimeoutException ex) { Console.WriteLine("FAIL live session rules: " + ex.Message); return 1; }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
            Console.WriteLine(failed == 0 ? "PASS live session rules" : $"FAIL live session rules: {failed} check(s)");
            return failed == 0 ? 0 : 1;
        }

        public static int RunEnv()
        {
            if (ClaudeCliLocator.Resolve("") == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var turns = new BlockingCollection<(ClaudeEvent Turn, int CutOffs)>();
            int cutOffs = 0, failed = 0, starts = 0;
            void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failed++; }
            const string longAnswer = "Count from 1 to 120, numbers only, separated by commas, on one line.";
            try
            {
                using (var session = NewSession(new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = PermissionModeChoice.Default, ExtraArguments = "--tools \"\"" }))
                {
                    session.RawLine += (s, e) => { if (e.Direction == RawLineDirection.Stdin && e.Line.Contains("\"subtype\":\"initialize\"")) Interlocked.Increment(ref starts); };
                    session.EventReceived += (s, ev) =>
                    {
                        if (ev.Kind == ClaudeEventKind.OutputLimitRetry) Interlocked.Increment(ref cutOffs);
                        if (ev.Kind == ClaudeEventKind.TurnCompleted) turns.Add((ev, Interlocked.Exchange(ref cutOffs, 0)));
                    };
                    (ClaudeEvent Turn, int CutOffs) Ask(string prompt, string label)
                    {
                        session.SendPromptAsync(prompt).Wait();
                        if (!turns.TryTake(out var t, TimeSpan.FromSeconds(120))) throw new TimeoutException("no answer to " + label);
                        Console.WriteLine($"  {label}: {t.Turn.Usage.Output} output tokens · cut off {t.CutOffs}× · {(t.Turn.IsError ? "error: " + t.Turn.Text : "answered")} · session {t.Turn.SessionId}");
                        return t;
                    }

                    var first = Ask(longAnswer, "turn 1, no limit");
                    Check(first.CutOffs == 0 && !first.Turn.IsError, "turn 1 not cut off");

                    Check(Setup.TokenSaverSetup.SetLocalEnv(dir, "CLAUDE_CODE_MAX_OUTPUT_TOKENS", "100"), "CLAUDE_CODE_MAX_OUTPUT_TOKENS=100 written to .claude/settings.local.json");
                    var before = Ask(longAnswer, "turn 2, same process");
                    Console.WriteLine(before.CutOffs > 0 || before.Turn.IsError ? "  note the running process picked the limit up by itself" : "  note the running process keeps its limit: a restart is needed");

                    session.StopProcess();
                    Check(!session.IsRunning, "process stopped");
                    var after = Ask(longAnswer, "turn 3, restarted");
                    Check(starts == 2, $"started again ({starts} processes)");
                    Check(after.Turn.SessionId == first.Turn.SessionId, "same conversation (--resume)");
                    Check(after.CutOffs > 0 || after.Turn.IsError, "turn 3 cut off at the new limit");
                    var settings = session.GetSettingsAsync().Result;
                    Check((string)settings?.Response?.SelectToken("effective.env.CLAUDE_CODE_MAX_OUTPUT_TOKENS") == "100", "get_settings: effective env has it");
                }
            }
            catch (TimeoutException ex) { Console.WriteLine("FAIL live env: " + ex.Message); return 1; }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
            Console.WriteLine(failed == 0 ? "PASS live env" : $"FAIL live env: {failed} check(s)");
            return failed == 0 ? 0 : 1;
        }

        public static int RunPrices(bool olderOnly = false)
        {
            if (ClaudeCliLocator.Resolve("") == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            var sys = new StringBuilder("You answer in one word.");
            for (int i = 1; i <= 260; i++) sys.Append($" Rule {i}: keep answers short and plain, with no extra words.");
            string args = string.Join(" ", "--system-prompt", ClaudeArgsBuilder.Quote(sys.ToString()), "--tools", "\"\"",
                "--strict-mcp-config", "--disable-slash-commands", "--setting-sources", "\"\"", "--no-session-persistence");
            var prices = new Tokens.PriceTable();
            int failed = 0;
            // Haiku 4.5 (an older model Options doesn't price): its first turn teaches the price, its second must match
            const string olderId = "claude-haiku-4-5-20251001";
            foreach (var (model, olderModel) in new[] { (ModelChoice.Haiku, (string)null), (ModelChoice.Sonnet, null), (ModelChoice.Opus, null), (ModelChoice.Fable, null), (ModelChoice.Auto, olderId) }.Where(m => !olderOnly || m.Item2 != null))
            {
                string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var turns = new BlockingCollection<ClaudeEvent>();
                try
                {
                    string label = olderModel != null ? "Haiku 4.5" : model.ToString();
                    using (var session = NewSession(new ClaudeLaunchOptions { WorkingDirectory = dir, Model = model, PermissionMode = PermissionModeChoice.Default, ExtraArguments = args + (olderModel != null ? " --model " + olderModel : "") }))
                    {
                        session.EventReceived += (s, ev) => { if (ev.Kind == ClaudeEventKind.TurnCompleted) turns.Add(ev); };
                        double before = 0;
                        foreach (var prompt in new[] { "Say one.", "Say two." })
                        {
                            session.SendPromptAsync(prompt).Wait();
                            if (!turns.TryTake(out var t, TimeSpan.FromSeconds(120))) { Console.WriteLine($"  FAIL {label}: no answer"); failed++; break; }
                            double cli = (t.TotalCostUsd ?? 0) - before;
                            before = t.TotalCostUsd ?? before;
                            var u = t.Usage.AsRequest(); // one request per turn here
                            bool learning = olderModel != null && prompt == "Say one.";
                            if (learning) prices.Learn(t.ModelUsageSinceLastResult); // as the meter does
                            var price = prices.For(olderModel ?? t.Model ?? model.ToCli()) ?? prices.For(model.ToCli());
                            double ours = price.Cost(u);
                            bool ok = Math.Abs(ours - cli) <= 1e-9 + cli * 1e-6;
                            if (!ok) failed++;
                            if (learning) Console.WriteLine($"  learned {olderModel}: {price}");
                            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {label,-9} {prompt,-9} in {u.Input,3} · out {u.Output,3} · read {u.CacheRead,5:N0} · write {u.CacheCreation,5:N0} (1h {u.CacheCreation1h:N0})"
                                + $"   ours ${ours:0.0000000} · CLI ${cli:0.0000000}" + (ok ? "" : $"   off by ${ours - cli:0.0000000}"));
                        }
                    }
                }
                finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
            }
            Console.WriteLine(failed == 0 ? "PASS live prices: every default price matches Claude Code's bill" : $"FAIL live prices: {failed} turn(s) priced differently; update PriceTable (and Options → Pricing)");
            return failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// `--live-prompt-tokens`: how many tokens prompts of known size really take, next to the chars / 4 estimate.
        /// One session (Haiku, Accept Edits); per prompt: the growth of the first request's context over the last
        /// request of the previous turn, less that request's output, and the growth of the CLI's userMessageTokens
        /// (get_context_usage). About $0.01.
        /// </summary>
        public static int RunPromptTokens()
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var prose = new StringBuilder();
            while (prose.Length < 8000) prose.Append("The quick brown fox jumps over the lazy dog, and then it walks back home to rest for the evening. ");
            var code = new StringBuilder();
            for (int i = 0; code.Length < 8000; i++) code.Append($"    public int Compute{i}(int value) {{ return value * {i} + _offset; }}\n");
            var dense = new StringBuilder();
            for (int i = 0; dense.Length < 8000; i++) dense.Append("Line ").Append(i).Append(": filler text to make the prompt long.\n");
            // "tiny" turns measure what a turn adds besides the prompt (reminders, the previous answer's framing)
            var prompts = new[] { ("warm-up", "Reply with OK."), ("tiny", "Reply with OK."), ("prose", prose + "\nReply with OK."), ("tiny", "Reply with OK."),
                ("code", code + "\nReply with OK."), ("tiny", "Reply with OK."), ("dense", dense + "\nReply with OK."), ("tiny", "Reply with OK.") };

            var usages = new BlockingCollection<ClaudeEvent>();
            using (var session = NewSession(new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = PermissionModeChoice.AcceptEdits }))
            {
                // The extension's learner on the same events, as TokenMeter feeds it
                var learner = new Tokens.PromptTokenLearner();
                var learned = new List<string>();
                session.EventReceived += (s, ev) =>
                {
                    double was = learner.LastMeasured;
                    learner.Observe(ev);
                    if (learner.LastMeasured != was) lock (learned) learned.Add(learner.LastMeasured.ToString("0.00"));
                    if ((ev.Kind == ClaudeEventKind.AssistantUsage && !ev.IsSubagent) || ev.Kind == ClaudeEventKind.TurnCompleted) usages.Add(ev);
                };
                try
                {
                    long lastCtx = 0, lastOut = 0, lastUser = 0;
                    foreach (var (name, text) in prompts)
                    {
                        learner.PromptSent(text.Length);
                        session.SendPromptAsync(text).Wait();
                        TokenUsage first = default, last = default;
                        string firstId = null, lastId = null;
                        var deadline = DateTime.UtcNow.AddSeconds(120);
                        while (DateTime.UtcNow < deadline && usages.TryTake(out var ev, TimeSpan.FromSeconds(120)))
                        {
                            if (ev.Kind == ClaudeEventKind.TurnCompleted) break;
                            if (firstId == null) { firstId = ev.MessageId; first = ev.Usage; }
                            if (ev.MessageId != lastId) { lastId = ev.MessageId; }
                            last = ev.Usage; // the CLI repeats a message's usage per block; the last copy is final
                        }
                        var cu = session.GetContextUsageAsync().Result;
                        long user = cu?.UserMessageTokens ?? 0;
                        if (name != "warm-up")
                        {
                            long api = first.ContextTokens - lastCtx - lastOut;
                            Console.WriteLine($"  {name,-6} {text.Length,6:N0} chars · est {Tokens.PromptSize.Estimate(text),5:N0} · API {api,5:N0} ({(api > 0 ? (double)text.Length / api : 0):0.00} ch/tok) · userMessageTokens +{user - lastUser,5:N0}");
                        }
                        lastCtx = last.ContextTokens; lastOut = last.Output; lastUser = user;
                    }
                    double now = Tokens.TokenEstimate.CharsPerToken;
                    string list; lock (learned) list = string.Join(", ", learned);
                    Console.WriteLine($"  learner measured: {(list.Length == 0 ? "nothing" : list)} → estimate now {now:0.00} characters per token");
                    // The long prompts are measured (not the warm-up, the process's second turn or the tiny ones), and land near 2.7-3.0
                    bool ok = learned.Count >= 2 && now > 2.5 && now < 3.2;
                    Console.WriteLine(ok ? "PASS live prompt tokens" : "FAIL live prompt tokens");
                    return ok ? 0 : 1;
                }
                finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
            }
        }

        /// <summary>
        /// `--live-long-prompt`: does stream-json input keep a long prompt whole? (The VS Code panel cut prompts over
        /// 50,000 characters silently before 2.1.319.) Sends prompts of 60,000 and 200,000 characters to Haiku, each
        /// ending in a unique marker, and checks the marker comes back. About $0.02 on Haiku (2.1.322: both came back whole).
        /// </summary>
        public static int RunLongPrompt()
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            bool ok = true;
            try
            {
                foreach (int chars in new[] { 60_000, 200_000 })
                {
                    string marker = "END-" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
                    string head = "Reply with only the code word on the very last line of this message, nothing else.\n";
                    var sb = new StringBuilder(head);
                    for (int i = 0; sb.Length < chars - 40; i++) sb.Append("Line ").Append(i).Append(": filler text to make the prompt long.\n");
                    sb.Append("Code word: ").Append(marker);
                    string prompt = sb.ToString();

                    var done = new ManualResetEventSlim();
                    string reply = "";
                    ClaudeEvent result = null;
                    using (var session = NewSession(new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = PermissionModeChoice.AcceptEdits }))
                    {
                        session.EventReceived += (s, ev) =>
                        {
                            if (ev.Kind == ClaudeEventKind.TextDelta) reply += ev.Text;
                            if (ev.Kind == ClaudeEventKind.TurnCompleted) { result = ev; done.Set(); }
                        };
                        session.SendPromptAsync(prompt).Wait();
                        bool finished = done.Wait(TimeSpan.FromSeconds(180));
                        long input = result == null ? 0 : result.Usage.Input + result.Usage.CacheRead + result.Usage.CacheCreation;
                        bool whole = finished && result?.IsError == false && reply.Contains(marker);
                        Console.WriteLine($"  {prompt.Length:N0} chars (~{Tokens.PromptSize.Estimate(prompt):N0} tokens est.): {(whole ? "marker came back" : "MARKER MISSING")}, "
                            + $"input {input:N0} tokens, cost ${result?.TotalCostUsd:0.0000}, reply \"{reply.Trim()}\"");
                        ok &= whole;
                    }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
            Console.WriteLine(ok ? "PASS live long prompt" : "FAIL live long prompt");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// `-- --live-edit-review`: Haiku in Manual mode edits two lines of a temp file; the test rejects one change
        /// (EditReview), allows the rest with a PostToolUse note, and checks the file and that Claude was told.
        /// About $0.005.
        /// </summary>
        public static int RunEditReview()
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "notes.txt");
            File.WriteAllText(file, "alpha\nbeta\ngamma\ndelta\n");
            var options = new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = PermissionModeChoice.Default, EditNotesHook = true };
            var done = new ManualResetEventSlim();
            string reply = "";
            bool reviewed = false;
            using (var session = NewSession(options))
            {
                session.EventReceived += (s, ev) =>
                {
                    if (ev.Kind == ClaudeEventKind.PermissionRequest)
                    {
                        var review = ev.ToolName == "Edit" ? Context.EditReview.Create(ev.ToolName, ev.ToolInput, File.ReadAllText(file)) : null;
                        if (review != null && review.Hunks.Count >= 2)
                        {
                            review.Hunks.Last().Accepted = false;
                            reviewed = true;
                            Console.WriteLine($"  reviewing {review.Hunks.Count} changes, rejecting: {review.Hunks.Last().Label}");
                            session.SetToolResultNote(ev.ToolUseId, review.NoteForClaude());
                            session.RespondToPermissionAsync(ev.RequestId, review.BuildInput(), true).Wait();
                        }
                        else session.RespondToPermissionAsync(ev.RequestId, ev.ToolInput, ev.ToolName == "Read").Wait();
                    }
                    if (ev.Kind == ClaudeEventKind.TextDelta || ev.Kind == ClaudeEventKind.AssistantText) reply += ev.Text;
                    if (ev.Kind == ClaudeEventKind.TurnCompleted) { Console.WriteLine($"  cost ${ev.TotalCostUsd:0.0000}"); done.Set(); }
                };
                session.SendPromptAsync("In notes.txt, change \"beta\" to \"BETA\" and \"delta\" to \"DELTA\" with one Edit call whose old_string spans lines 2-4. Then say in one sentence what the tool result told you.").Wait();
                bool finished = done.Wait(TimeSpan.FromSeconds(120));
                string content = File.ReadAllText(file);
                Console.WriteLine("  file: " + content.Replace("\n", "\\n"));
                Console.WriteLine("  reply: " + reply.Trim());
                bool ok = finished && reviewed && content == "alpha\nBETA\ngamma\ndelta\n" && reply.IndexOf("reject", StringComparison.OrdinalIgnoreCase) >= 0;
                Console.WriteLine(ok ? "PASS live edit review" : "FAIL live edit review");
                try { Directory.Delete(dir, true); } catch (IOException) { }
                return ok ? 0 : 1;
            }
        }

        /// <summary>
        /// `--live-edit-review-interrupt [rawPath]`: as <see cref="RunEditReview"/>, but Stop and a new prompt follow the
        /// reviewed approval at once. Since Claude Code 2.1.319 PostToolUse hooks also run on tool calls that finish
        /// after a new message interrupts the turn, so the edit-review hook_callback can arrive late. Checks that it is
        /// answered (its note for its own tool_use_id) and that Claude knows which change was rejected. About $0.01.
        /// <paramref name="allowPlain"/> (`--live-edit-stop-allow`): the edit is allowed as proposed, no review; when
        /// its result says it was stopped while running, the file is checked as the chat does (<see cref="Context.StoppedEdit"/>)
        /// and Claude is told with the next prompt whether the change is in.
        /// </summary>
        public static int RunEditReviewInterrupted(string rawLogPath, bool allowPlain = false)
        {
            Context.StoppedEdit.State stopState = Context.StoppedEdit.State.Unknown;
            bool sawStopped = false;
            string snapshot = null;
            Newtonsoft.Json.Linq.JObject editInput = null;
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "notes.txt");
            File.WriteAllText(file, "alpha\nbeta\ngamma\ndelta\n");
            var options = new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = PermissionModeChoice.Default, EditNotesHook = true };
            var raw = new StringBuilder();
            var turns = new BlockingCollection<ClaudeEvent>();
            var replies = new List<StringBuilder> { new StringBuilder() };
            string editId = null;
            int ended = 0;
            using (var session = NewSession(options))
            {
                session.RawLine += (s, e) => { lock (raw) raw.Append(e.Direction).Append('\t').Append(e.Line).Append('\n'); };
                session.EventReceived += (s, ev) =>
                {
                    if (ev.Kind == ClaudeEventKind.PermissionRequest && allowPlain && ev.ToolName == "Edit" && editId == null)
                    {
                        editId = ev.ToolUseId;
                        snapshot = File.ReadAllText(file);
                        editInput = ev.ToolInput;
                        Console.WriteLine("  allowing the edit as proposed; then Stop at once");
                        session.RespondToPermissionAsync(ev.RequestId, ev.ToolInput, true).Wait();
                        session.InterruptAsync().Wait();
                    }
                    else if (ev.Kind == ClaudeEventKind.PermissionRequest)
                    {
                        var review = ev.ToolName == "Edit" && editId == null ? Context.EditReview.Create(ev.ToolName, ev.ToolInput, File.ReadAllText(file)) : null;
                        if (review != null && review.Hunks.Count >= 2)
                        {
                            review.Hunks.Last().Accepted = false;
                            editId = ev.ToolUseId;
                            Console.WriteLine($"  reviewing {review.Hunks.Count} changes, rejecting: {review.Hunks.Last().Label}; then Stop at once");
                            string before = File.ReadAllText(file);
                            string expected = Context.EditReview.ProposedFile(ev.ToolName, review.BuildInput(), before);
                            session.SetToolResultNote(ev.ToolUseId, review.NoteForClaude(), "notes.txt", () => File.ReadAllText(file) == expected);
                            session.RespondToPermissionAsync(ev.RequestId, review.BuildInput(), true).Wait();
                            session.InterruptAsync().Wait();
                        }
                        else session.RespondToPermissionAsync(ev.RequestId, ev.ToolInput, false).Wait();
                    }
                    if (ev.Kind == ClaudeEventKind.ToolResult && allowPlain && ev.ToolUseId == editId && Context.StoppedEdit.IsStopped(ev.IsError, ev.ResultText))
                    {
                        // What the chat does (ClaudeChatControl.TellIfStoppedEditWritten)
                        sawStopped = true;
                        stopState = Context.StoppedEdit.Check("Edit", editInput, snapshot, File.ReadAllText(file));
                        Console.WriteLine($"  result: stopped while running; the file says: {stopState}");
                        session.AddNextPromptNote(ev.ToolUseId, Context.StoppedEdit.Note("Edit", "notes.txt", stopState, editInput));
                    }
                    if (ev.Kind == ClaudeEventKind.TextDelta || ev.Kind == ClaudeEventKind.AssistantText) lock (replies) replies[replies.Count - 1].Append(ev.Text);
                    if (ev.Kind == ClaudeEventKind.TurnCompleted)
                    {
                        lock (replies) replies.Add(new StringBuilder());
                        turns.Add(ev);
                        // As a person would: after the stopped turn ends, ask about it (the stranded note goes along)
                        if (Interlocked.Increment(ref ended) == 1 && editId != null)
                            System.Threading.Tasks.Task.Run(() => session.SendPromptAsync("Without using any tool: did your Edit to notes.txt keep every change you asked for? One sentence, naming any line that stayed as it was."));
                    }
                };
                try
                {
                    session.SendPromptAsync("In notes.txt, change \"beta\" to \"BETA\" and \"delta\" to \"DELTA\" with one Edit call whose old_string spans lines 2-4.").Wait();
                    var deadline = DateTime.UtcNow.AddSeconds(150);
                    int completed = 0;
                    while (completed < 2 && DateTime.UtcNow < deadline)
                        if (turns.TryTake(out var t, TimeSpan.FromMilliseconds(500)))
                        {
                            completed++;
                            Console.WriteLine($"  turn {completed} ended: error={t.IsError}, reason={t.TerminalReason ?? "-"}, cost=${t.TotalCostUsd:0.0000}");
                        }
                    Thread.Sleep(1000); // a straggling hook_callback would come now
                    string content = File.ReadAllText(file);
                    string log; lock (raw) log = raw.ToString();
                    var lines = log.Split('\n');
                    int hookIn = Array.FindIndex(lines, l => l.Contains("\"hook_callback\"") && editId != null && l.Contains(editId));
                    int hookOut = hookIn < 0 ? -1 : Array.FindIndex(lines, hookIn, l => l.Contains("additionalContext"));
                    int interrupt = Array.FindIndex(lines, l => l.Contains("\"subtype\":\"interrupt\""));
                    string last; lock (replies) last = replies.Where(r => r.Length > 0).LastOrDefault()?.ToString().Trim() ?? "";
                    int carried = Array.FindIndex(lines, l => l.StartsWith("Stdin") && l.Contains("\"type\":\"user\"") && l.Contains(ClaudeSession.EditorNotePrefix));
                    Console.WriteLine($"  raw: interrupt at line {interrupt}; hook_callback for the edit at {hookIn}, answered at {hookOut}; note sent with the next prompt at {carried}");
                    Console.WriteLine("  file: " + content.Replace("\n", "\\n"));
                    Console.WriteLine("  reply: " + last);
                    // Claude must learn which change was rejected: from the hook (when it ran) or from the next prompt (when Stop skipped it)
                    bool told = (hookIn >= 0 && hookOut > hookIn) || carried >= 0;
                    if (allowPlain)
                    {
                        // The call may or may not have been written before Stop; the file must say which, and Claude be told
                        bool known = stopState != Context.StoppedEdit.State.Unknown;
                        bool fileMatches = stopState == Context.StoppedEdit.State.Written ? content == "alpha\nBETA\ngamma\nDELTA\n" : content == "alpha\nbeta\ngamma\ndelta\n";
                        // and Claude must take it: a bare claim was not believed in an earlier run ("I can't confirm …")
                        bool believed = last.IndexOf("confirm", StringComparison.OrdinalIgnoreCase) < 0;
                        bool okAllow = completed >= 2 && (!sawStopped || (known && fileMatches && carried >= 0 && believed));
                        if (!sawStopped) Console.WriteLine("  (the edit finished before Stop took effect: nothing to tell; run again to hit the race)");
                        Console.WriteLine(okAllow ? "PASS live edit stop after Allow" : "FAIL live edit stop after Allow");
                        return okAllow ? 0 : 1;
                    }
                    bool ok = completed >= 2 && editId != null && told
                        && content == "alpha\nBETA\ngamma\ndelta\n" && last.IndexOf("delta", StringComparison.OrdinalIgnoreCase) >= 0;
                    Console.WriteLine(ok ? "PASS live edit review after interrupt" : "FAIL live edit review after interrupt");
                    return ok ? 0 : 1;
                }
                finally
                {
                    if (!string.IsNullOrEmpty(rawLogPath)) File.WriteAllText(rawLogPath, raw.ToString());
                    try { Directory.Delete(dir, true); } catch (IOException) { }
                }
            }
        }

        /// <summary>
        /// `--live-mcp-restart`: an MCP server turned off in *MCP servers…* (<c>mcp_toggle</c>) and a restart in place
        /// (<see cref="ClaudeSession.StopProcess"/>, Phase 77): is it still off in the new process? No prompt, no cost:
        /// a settings request starts the process.
        /// </summary>
        public static int RunMcpRestart()
        {
            if (ClaudeCliLocator.Resolve("") == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var options = new ClaudeLaunchOptions { WorkingDirectory = dir, PermissionMode = PermissionModeChoice.Plan, EditNotesHook = true };
            options.SdkMcpServers.Add(Context.IdeMcpServer.Create(
                () => System.Threading.Tasks.Task.FromResult<IReadOnlyList<Context.BuildError>>(new Context.BuildError[0]), () => dir));
            int failed = 0;
            void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failed++; }
            McpServerRow Row(ClaudeSession s, Func<McpServerRow, bool> until)
            {
                McpServerRow row = null;
                for (int i = 0; i < 20; i++)
                {
                    if (i > 0) Thread.Sleep(500);
                    row = s.GetMcpServersAsync().Result?.FirstOrDefault(r => r.Name == Context.IdeMcpServer.ServerName);
                    if (row != null && until(row)) break;
                }
                return row;
            }
            try
            {
                using (var session = NewSession(options))
                {
                    session.GetSettingsAsync(start: true).Wait();
                    var on = Row(session, r => r.Status == "connected");
                    Console.WriteLine($"  start: {on?.Status ?? "missing"}");
                    Check(on?.Status == "connected", "connected at the start");
                    var toggled = session.SetMcpServerEnabledAsync(Context.IdeMcpServer.ServerName, false).Result;
                    var off = Row(session, r => r.Status != "connected");
                    Console.WriteLine($"  after mcp_toggle off: {off?.Status ?? "missing"} · answer {toggled?.ToString(Newtonsoft.Json.Formatting.None)}");
                    Check(off != null && off.Status != "connected", "off after mcp_toggle");

                    // A server added to .mcp.json while it runs (approved in the local settings): listed now, or only after a restart?
                    File.WriteAllText(Path.Combine(dir, ".mcp.json"), "{\"mcpServers\":{\"probe\":{\"command\":\"cvs-no-such-command\",\"args\":[]}}}");
                    Directory.CreateDirectory(Path.Combine(dir, ".claude"));
                    File.WriteAllText(Path.Combine(dir, ".claude", "settings.local.json"), "{\"enabledMcpjsonServers\":[\"probe\"]}");
                    Thread.Sleep(2000); // a file watcher, if there is one
                    var added = session.GetMcpServersAsync().Result?.FirstOrDefault(r => r.Name == "probe");
                    Console.WriteLine($"  .mcp.json server, same process: {added?.Status ?? "not listed"}");
                    Console.WriteLine(added == null ? "  note .mcp.json is read when Claude Code starts" : "  note the running process read .mcp.json");

                    session.StopProcess();
                    session.GetSettingsAsync(start: true).Wait();
                    var again = Row(session, r => r.Status != "pending");
                    Console.WriteLine($"  after the restart: {again?.Status ?? "missing"}");
                    Check(again != null && again.Status != "connected", "still off after a restart in place");
                    var afterRestart = session.GetMcpServersAsync().Result?.FirstOrDefault(r => r.Name == "probe");
                    Console.WriteLine($"  .mcp.json server, after the restart: {afterRestart?.Status ?? "not listed"}");
                    Check(afterRestart != null, ".mcp.json read after a restart in place");
                }
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
            Console.WriteLine(failed == 0 ? "PASS live mcp restart" : $"FAIL live mcp restart: {failed} check(s)");
            return failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// `--live-mcp [rawPath]`: the in-process Error List server connects, and the context breakdown
        /// (<c>get_context_usage</c>, no model call) shows how its tool is counted. No prompt, no cost.
        /// </summary>
        public static int RunMcp(string rawLogPath = null)
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var options = new ClaudeLaunchOptions { WorkingDirectory = dir, PermissionMode = PermissionModeChoice.Plan, EditNotesHook = true }; // as the extension starts it
            options.SdkMcpServers.Add(Context.IdeMcpServer.Create(
                () => System.Threading.Tasks.Task.FromResult<IReadOnlyList<Context.BuildError>>(new Context.BuildError[0]), () => dir));
            var raw = new StringBuilder();
            using (var session = NewSession(options))
            {
                session.RawLine += (s, e) => { lock (raw) raw.Append(e.Direction).Append('\t').Append(e.Line).Append('\n'); };
                try
                {
                    McpServerRow row = null;
                    for (int i = 0; i < 20 && row?.Status != "connected"; i++)
                    {
                        if (i > 0) Thread.Sleep(500);
                        row = session.GetMcpServersAsync().Result?.FirstOrDefault(r => r.Name == Context.IdeMcpServer.ServerName);
                    }
                    Console.WriteLine($"visualstudio: {row?.Status ?? "missing"}, tools {row?.ToolCount}, transport {row?.Transport}");
                    bool ok = row?.Status == "connected" && row.ToolCount == 1;

                    var usage = session.GetContextUsageAsync().Result;
                    if (usage == null) { Console.WriteLine("  FAIL no context usage"); ok = false; }
                    else
                    {
                        Console.WriteLine($"  context: {usage.TotalTokens:N0} of {usage.MaxTokens:N0} ({usage.Percentage}%), model {usage.Model}");
                        foreach (var c in usage.Categories) Console.WriteLine($"    {c.Kind,-8} {c.Name,-28} {c.Tokens,9:N0}");
                        Console.WriteLine($"  MCP tools: {usage.McpToolCount} counted, {usage.McpToolTokens:N0} tokens");
                        Console.WriteLine($"  base context tip: {SessionNotices.BaseContext(usage) ?? "(none)"}");
                    }
                    Console.WriteLine(ok ? "PASS live MCP" : "FAIL live MCP");
                    return ok ? 0 : 1;
                }
                finally
                {
                    if (!string.IsNullOrEmpty(rawLogPath)) File.WriteAllText(rawLogPath, raw.ToString());
                    try { Directory.Delete(dir, true); } catch (IOException) { }
                }
            }
        }

        /// <summary>
        /// `--live [rawLogPath] [mode] [turns]`: short turns asking for Haiku, Accept Edits unless another mode is named.
        /// In plan mode Claude Code answers the alias "haiku" with Sonnet; the session switches to Haiku's full ID first.
        /// Plan mode adds two turns: one after the process was killed (a crash: restarted with --resume), one in a new
        /// session that resumes the conversation (as reopening it in the chat does). Both must still be answered by Haiku.
        /// </summary>
        public static int Run(string rawLogPath, PermissionModeChoice mode = PermissionModeChoice.AcceptEdits, int turns = 2)
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            Console.WriteLine("claude: " + exe);

            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var events = new BlockingCollection<ClaudeEvent>();
            var raw = new StringBuilder();
            var failures = new List<string>();
            void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) failures.Add(what); }

            ClaudeSession Open(string resumeId) { var s = NewSession(new ClaudeLaunchOptions
            {
                WorkingDirectory = dir,
                Model = ModelChoice.Haiku,
                Effort = EffortChoice.Low,
                PermissionMode = mode,
                AppendSystemPrompt = "Answer in as few words as possible.",
                ResumeSessionId = resumeId,
            });
            s.EventReceived += (_, e) => events.Add(e);
            s.RawLine += (_, e) => { lock (raw) raw.Append(e.Direction).Append('\t').Append(e.Line).Append('\n'); };
            return s; }
            var session = Open(null);
            bool plan = mode == PermissionModeChoice.Plan;
            int crashTurn = plan ? turns + 1 : 0, resumeTurn = plan ? turns + 2 : 0; // plan mode: Haiku after a restart too
            string[] words = { "PING", "PONG", "PANG", "PUNG" };

            try
            {
                Console.WriteLine($"mode: {mode.ToCli()}, turns: {turns}" + (plan ? " + one after a crash, one resumed in a new session" : ""));
                for (int turn = 1; turn <= Math.Max(turns, resumeTurn); turn++)
                {
                    Console.WriteLine($"Turn {turn}" + (turn == crashTurn ? " (after the process was killed)" : turn == resumeTurn ? " (a new session, --resume)" : "") + ":");
                    string word = turn <= words.Length ? words[turn - 1] : "PONG";
                    if (turn == crashTurn)
                    {
                        int? pid = session.ProcessId;
                        if (pid != null) try { System.Diagnostics.Process.GetProcessById(pid.Value).Kill(entireProcessTree: true); } catch (ArgumentException) { }
                        for (int i = 0; i < 50 && session.IsRunning; i++) Thread.Sleep(100);
                        Check(!session.IsRunning, "process killed");
                    }
                    if (turn == resumeTurn)
                    {
                        string id = session.SessionId;
                        session.Dispose();
                        Check(!string.IsNullOrEmpty(id), "a session id to resume");
                        session = Open(id);
                    }
                    string prompt = "Reply with exactly the word: " + word;
                    session.SendPromptAsync(prompt).GetAwaiter().GetResult();
                    var seen = Collect(events, TimeSpan.FromSeconds(120));
                    string text = string.Concat(seen.Where(e => e.Kind == ClaudeEventKind.TextDelta).Select(e => e.Text));
                    if (text.Length == 0) text = string.Concat(seen.Where(e => e.Kind == ClaudeEventKind.AssistantText).Select(e => e.Text));
                    var done = seen.LastOrDefault(e => e.Kind == ClaudeEventKind.TurnCompleted);

                    Console.WriteLine("  events: " + string.Join(", ", seen.GroupBy(e => e.Kind).Select(g => $"{g.Key}×{g.Count()}")));
                    Console.WriteLine($"  text: \"{text.Trim()}\"");
                    if (turn == 1) Check(seen.Any(e => e.Kind == ClaudeEventKind.SessionStarted && !string.IsNullOrEmpty(e.SessionId)), "SessionStarted with a session id");
                    Check(done != null, "TurnCompleted received");
                    Check(done != null && !done.IsError, "turn is not an error" + (done?.IsError == true ? ": " + done.Text : ""));
                    Check(text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0, "answer text parsed");
                    Check(done != null && done.Usage.Total > 0, $"usage reported ({done?.Usage.Total ?? 0} tokens)");
                    Check(done?.TotalCostUsd != null, $"cost reported (${done?.TotalCostUsd:0.00000})");
                    var model = seen.LastOrDefault(e => e.Kind == ClaudeEventKind.AssistantUsage && !string.IsNullOrEmpty(e.Model))?.Model;
                    Check(model != null && model.IndexOf("haiku", StringComparison.OrdinalIgnoreCase) >= 0, "answered by Haiku (" + (model ?? "no model reported") + ")");
                    if (turn == 2)
                    {
                        Check(session.IsRunning, "same process kept alive between prompts");
                        Check(done?.LastRequestUsage.CacheRead > 0, $"prompt cache reused on turn 2 ({done?.LastRequestUsage.CacheRead ?? 0} cache-read tokens)");
                    }
                }
            }
            catch (Exception ex) { Check(false, "exception: " + ex.Message); }
            finally
            {
                session.Dispose();
                if (!string.IsNullOrEmpty(rawLogPath)) { File.WriteAllText(rawLogPath, raw.ToString()); Console.WriteLine("raw lines: " + rawLogPath); }
                try { Directory.Delete(dir, true); } catch { }
            }

            Console.WriteLine(failures.Count == 0 ? "LIVE: all checks passed" : $"LIVE: {failures.Count} check(s) failed");
            return failures.Count == 0 ? 0 : 1;
        }

        /// <summary>
        /// One turn that is expected to raise permission requests (AskUserQuestion, ExitPlanMode). Prints each
        /// request, answers it (first option of every question; plans approved) and prints the final answer, so the
        /// request shape and the accepted response format can be checked against the real CLI.
        /// `--live-ask rawPath mode "prompt"`
        /// </summary>
        public static int RunInteractive(string rawLogPath, PermissionModeChoice mode, string prompt)
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var raw = new StringBuilder();
            var session = NewSession(new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = mode });
            var events = new BlockingCollection<ClaudeEvent>();
            session.EventReceived += (s, e) => events.Add(e);
            session.RawLine += (s, e) => { lock (raw) raw.Append(e.Direction).Append('\t').Append(e.Line).Append('\n'); };
            int requests = 0;
            // Phase 87: the row an answered question gets from the real stream (tool_use_result), as the chat draws it
            var askIds = new HashSet<string>();
            var expectedLines = new List<string>();
            string answerRow = null;
            try
            {
                Console.WriteLine($"mode: {mode.ToCli()}");
                session.SendPromptAsync(prompt).GetAwaiter().GetResult();
                var text = new StringBuilder();
                var deadline = DateTime.UtcNow.AddSeconds(180);
                while (DateTime.UtcNow < deadline)
                {
                    if (!events.TryTake(out var e, TimeSpan.FromMilliseconds(200))) continue;
                    if (e.Kind == ClaudeEventKind.TextDelta) text.Append(e.Text);
                    if (e.Kind == ClaudeEventKind.ToolUse) Console.WriteLine($"  tool_use: {e.ToolName}");
                    if (e.Kind == ClaudeEventKind.ToolUse && e.ToolName == AskUserQuestionTool.Name && e.ToolUseId != null) askIds.Add(e.ToolUseId);
                    if (e.Kind == ClaudeEventKind.ToolResult && e.ToolUseId != null && askIds.Contains(e.ToolUseId))
                    {
                        answerRow = string.Join(" | ", ToolCallView.Summarize(AskUserQuestionTool.Name, e.ResultText, e.IsError, data: e.ResultData).Lines);
                        Console.WriteLine("  answer row: " + answerRow);
                    }
                    if (e.Kind == ClaudeEventKind.PermissionRequest)
                    {
                        requests++;
                        Console.WriteLine($"  permission request: {e.ToolName} (request {e.RequestId})");
                        Console.WriteLine("    input: " + e.ToolInput?.ToString(Newtonsoft.Json.Formatting.None));
                        var answer = (Newtonsoft.Json.Linq.JObject)(e.ToolInput?.DeepClone() ?? new Newtonsoft.Json.Linq.JObject());
                        if (e.ToolName == AskUserQuestionTool.Name)
                        {
                            // Through the extension's own answer builder: the first option, or the first two of a
                            // multi-select question (sent joined with ", ").
                            var chosen = AskUserQuestionTool.Parse(e.ToolInput).ToDictionary(q => q.Text,
                                q => (IReadOnlyList<string>)q.Options.Take(q.MultiSelect ? 2 : 1).Select(o => o.Label).ToList());
                            answer = AskUserQuestionTool.BuildAnswer(e.ToolInput, chosen);
                            foreach (var q in AskUserQuestionTool.Parse(e.ToolInput))
                                expectedLines.Add((string.IsNullOrWhiteSpace(q.Header) ? q.Text : q.Header) + ": " + string.Join(", ", chosen[q.Text]));
                            Console.WriteLine("    answering: " + answer["answers"]?.ToString(Newtonsoft.Json.Formatting.None));
                        }
                        session.RespondToPermissionAsync(e.RequestId, answer, allow: true).GetAwaiter().GetResult();
                    }
                    if (e.Kind == ClaudeEventKind.TurnCompleted)
                    {
                        Console.WriteLine($"  turn completed: error={e.IsError}, cost=${e.TotalCostUsd:0.0000}");
                        break;
                    }
                }
                Console.WriteLine("  final text: \"" + text.ToString().Trim() + "\"");
            }
            finally
            {
                session.Dispose();
                if (!string.IsNullOrEmpty(rawLogPath)) File.WriteAllText(rawLogPath, raw.ToString());
                try { Directory.Delete(dir, true); } catch { }
            }
            if (expectedLines.Count > 0)
            {
                string expected = string.Join(" | ", expectedLines);
                bool same = answerRow == expected;
                Console.WriteLine((same ? "  ok   " : "  FAIL ") + $"the answered question's row reads \"{expected}\" (got \"{answerRow}\")");
                if (!same) return 1;
            }
            Console.WriteLine($"permission requests: {requests}");
            return requests > 0 ? 0 : 1; // the prompt is meant to raise at least one
        }

        /// <summary>Events until a TurnCompleted (or timeout), skipping leftovers from before.</summary>
        /// <summary>
        /// Phase 50 probe: the same two turns with <see cref="ClaudeLaunchOptions.PromptSuggestions"/> and without. Prints each turn's
        /// cost (total_cost_usd is cumulative per session) and the <c>prompt_suggestion</c> line that follows the
        /// result, so whether a suggestion is sent, when, and whether its cost is reported can be read off.
        /// `--live-suggestions [rawPath]` (rawPath gets "-on"/"-off" before the extension)
        /// </summary>
        public static int RunSuggestions(string rawLogPath)
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            var failures = new List<string>();
            void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) failures.Add(what); }
            string[] prompts =
            {
                "Write a file notes.txt with three short lines about LINQ. Then tell me in one sentence what you could add next.",
                "Yes, add one more line to notes.txt about deferred execution.",
            };

            foreach (bool on in new[] { true, false })
            {
                Console.WriteLine(on ? "With --prompt-suggestions true:" : "Without:");
                string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var events = new BlockingCollection<ClaudeEvent>();
                var suggestions = new BlockingCollection<string>();
                var raw = new StringBuilder();
                var session = NewSession(new ClaudeLaunchOptions
                {
                    WorkingDirectory = dir,
                    Model = ModelChoice.Haiku,
                    Effort = EffortChoice.Low,
                    PermissionMode = PermissionModeChoice.AcceptEdits,
                    PromptSuggestions = on,
                });
                session.EventReceived += (s, e) => { if (e.Kind == ClaudeEventKind.PromptSuggestion) suggestions.Add(e.Text); else events.Add(e); };
                session.RawLine += (s, e) => { lock (raw) raw.Append(e.Direction).Append('\t').Append(e.Line).Append('\n'); };
                int got = 0;
                try
                {
                    double before = 0;
                    for (int turn = 0; turn < prompts.Length; turn++)
                    {
                        session.SendPromptAsync(prompts[turn]).GetAwaiter().GetResult();
                        var done = Collect(events, TimeSpan.FromSeconds(120)).LastOrDefault(e => e.Kind == ClaudeEventKind.TurnCompleted);
                        double total = done?.TotalCostUsd ?? 0;
                        Console.WriteLine($"  turn {turn + 1}: ${total - before:0.00000} (session ${total:0.00000})" + (done?.IsError == true ? " ERROR " + done.Text : ""));
                        before = total;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        if (suggestions.TryTake(out var suggestion, TimeSpan.FromSeconds(on ? 30 : 5)))
                        {
                            got++;
                            Console.WriteLine($"    suggestion after {sw.Elapsed.TotalSeconds:0.0} s: \"{suggestion}\"");
                        }
                        else Console.WriteLine("    no suggestion");
                    }
                    // 2.1.325 often sends none after a turn (3 of 6 turns over three runs), so one is enough.
                    Check(on ? got > 0 : got == 0, on ? $"a prompt_suggestion followed {got} of {prompts.Length} results" : "no prompt_suggestion without the flag");
                }
                catch (Exception ex) { Check(false, "exception: " + ex.Message); }
                finally
                {
                    session.Dispose();
                    if (!string.IsNullOrEmpty(rawLogPath))
                    {
                        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rawLogPath)),
                            Path.GetFileNameWithoutExtension(rawLogPath) + (on ? "-on" : "-off") + Path.GetExtension(rawLogPath));
                        File.WriteAllText(path, raw.ToString());
                        Console.WriteLine("  raw lines: " + path);
                    }
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            Console.WriteLine(failures.Count == 0 ? "LIVE: all checks passed" : $"LIVE: {failures.Count} check(s) failed");
            return failures.Count == 0 ? 0 : 1;
        }

        private static List<ClaudeEvent> Collect(BlockingCollection<ClaudeEvent> events, TimeSpan timeout)
        {
            var list = new List<ClaudeEvent>();
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!events.TryTake(out var e, TimeSpan.FromMilliseconds(200))) continue;
                list.Add(e);
                if (e.Kind == ClaudeEventKind.TurnCompleted) break;
            }
            return list;
        }
    }
}
