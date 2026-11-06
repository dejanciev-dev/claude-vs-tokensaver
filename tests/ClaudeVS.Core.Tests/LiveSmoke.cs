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
    /// Opt-in check against the real claude CLI: `dotnet run --project tests/ClaudeVS.Core.Tests -- --live [rawLogPath]`.
    /// Asks for Haiku at low effort in plan (read-only) mode in an empty temp folder, so it cannot change files.
    /// Each request carries the CLI's ~25k-token system context: expect a few cents per run (about $0.08 when the
    /// CLI answered with Sonnet on 2026-11-02). Never runs in CI or `dotnet test`.
    /// </summary>
    internal static class LiveSmoke
    {
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
                Thread.Sleep(TimeSpan.FromSeconds(18)); // start-up happens while idle
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
                var s = new ClaudeSession(new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = PermissionModeChoice.Default, ResumeSessionId = resumeId });
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
            using (var session = new ClaudeSession(options))
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
            using (var session = new ClaudeSession(new ClaudeLaunchOptions { WorkingDirectory = dir, PermissionMode = PermissionModeChoice.Plan, EditNotesHook = true }))
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
            using (var session = new ClaudeSession(options))
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
        /// `-- --live-mcp`: starts the CLI with our in-process Error List server and checks that the CLI connects to it
        /// and lists its tool (mcp_status). Sends no prompt, so it makes no model call and costs nothing.
        /// </summary>
        public static int RunMcp()
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var options = new ClaudeLaunchOptions { WorkingDirectory = dir, PermissionMode = PermissionModeChoice.Plan, EditNotesHook = true }; // as the extension starts it
            options.SdkMcpServers.Add(Context.IdeMcpServer.Create(
                () => System.Threading.Tasks.Task.FromResult<IReadOnlyList<Context.BuildError>>(new Context.BuildError[0]), () => dir));
            using (var session = new ClaudeSession(options))
            {
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
                    Console.WriteLine(ok ? "PASS live MCP" : "FAIL live MCP");
                    return ok ? 0 : 1;
                }
                finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
            }
        }

        public static int Run(string rawLogPath, PermissionModeChoice mode = PermissionModeChoice.Plan, int turns = 2)
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

            var session = new ClaudeSession(new ClaudeLaunchOptions
            {
                WorkingDirectory = dir,
                Model = ModelChoice.Haiku,
                Effort = EffortChoice.Low,
                PermissionMode = mode,
                AppendSystemPrompt = "Answer in as few words as possible.",
            });
            session.EventReceived += (s, e) => events.Add(e);
            session.RawLine += (s, e) => { lock (raw) raw.Append(e.Direction).Append('\t').Append(e.Line).Append('\n'); };

            try
            {
                Console.WriteLine($"mode: {mode.ToCli()}, turns: {turns}");
                for (int turn = 1; turn <= turns; turn++)
                {
                    Console.WriteLine($"Turn {turn}:");
                    string prompt = turn == 1 ? "Reply with exactly the word: PING" : "Reply with exactly the word: PONG";
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
                    Check(text.IndexOf(turn == 1 ? "PING" : "PONG", StringComparison.OrdinalIgnoreCase) >= 0, "answer text parsed");
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
            var session = new ClaudeSession(new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = mode });
            var events = new BlockingCollection<ClaudeEvent>();
            session.EventReceived += (s, e) => events.Add(e);
            session.RawLine += (s, e) => { lock (raw) raw.Append(e.Direction).Append('\t').Append(e.Line).Append('\n'); };
            int requests = 0;
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
            Console.WriteLine($"permission requests: {requests}");
            return 0;
        }

        /// <summary>Events until a TurnCompleted (or timeout), skipping leftovers from before.</summary>
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
