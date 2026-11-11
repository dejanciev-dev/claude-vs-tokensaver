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
        /// `--live-all`: every live test in order, with a summary and the total cost (about $0.15 on Haiku with
        /// 2.1.322). Nothing runs in plan mode: there the CLI answers with Sonnet even when Haiku is asked for
        /// (2.1.316-2.1.322), which costs ~20x more and fails the "answered by Haiku" check; `--live raw.jsonl Plan`
        /// checks that on purpose. Exit code 0 only when every test passed.
        /// </summary>
        public static int RunAll()
        {
            if (ClaudeCliLocator.Resolve("") == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            const string ask = "Use the AskUserQuestion tool once to ask me which fruits I like (multi-select: Apple, Banana, Cherry), then repeat my answer in one line.";
            var steps = new (string Name, Func<int> Run)[]
            {
                ("dialogs", RunDialogs),
                ("mcp", () => RunMcp()),
                ("haiku-router", RunHaikuRouter),
                ("setup", () => RunSetup(null)),
                ("edit-review", RunEditReview),
                ("edit-review-interrupt", () => RunEditReviewInterrupted(null)),
                ("edit-stop-allow", () => RunEditReviewInterrupted(null, allowPlain: true)),
                ("live (Accept Edits)", () => Run(null, PermissionModeChoice.AcceptEdits)),
                ("ask", () => RunInteractive(null, PermissionModeChoice.Default, ask)),
                ("session", RunSession),
                ("long-prompt", RunLongPrompt),
                ("prompt-tokens", RunPromptTokens),
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
            Console.WriteLine("  Not run: --live in plan mode (answers with Sonnet, a known limitation; check it with --live raw.jsonl Plan).");
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
        /// `--live-prices`: are the default prices what Claude Code bills? Per model (Haiku, Sonnet, Opus, Fable) two
        /// turns in a trimmed process (no tools, no settings, a ~4.5k-token system prompt cached for 1 hour): the
        /// first writes the cache, the second reads it. Each turn is one request, so its usage priced with
        /// <see cref="Tokens.PriceTable"/> must equal the growth of the CLI's total_cost_usd. About $0.19 (Opus and
        /// Fable cache writes), so not part of --live-all. Checked by hand this way in Phases 18 and 20.
        /// </summary>
        public static int RunPrices()
        {
            if (ClaudeCliLocator.Resolve("") == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            var sys = new StringBuilder("You answer in one word.");
            for (int i = 1; i <= 260; i++) sys.Append($" Rule {i}: keep answers short and plain, with no extra words.");
            string args = string.Join(" ", "--system-prompt", ClaudeArgsBuilder.Quote(sys.ToString()), "--tools", "\"\"",
                "--strict-mcp-config", "--disable-slash-commands", "--setting-sources", "\"\"", "--no-session-persistence");
            var prices = new Tokens.PriceTable();
            int failed = 0;
            foreach (var model in new[] { ModelChoice.Haiku, ModelChoice.Sonnet, ModelChoice.Opus, ModelChoice.Fable })
            {
                string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var turns = new BlockingCollection<ClaudeEvent>();
                try
                {
                    using (var session = NewSession(new ClaudeLaunchOptions { WorkingDirectory = dir, Model = model, PermissionMode = PermissionModeChoice.Default, ExtraArguments = args }))
                    {
                        session.EventReceived += (s, ev) => { if (ev.Kind == ClaudeEventKind.TurnCompleted) turns.Add(ev); };
                        double before = 0;
                        foreach (var prompt in new[] { "Say one.", "Say two." })
                        {
                            session.SendPromptAsync(prompt).Wait();
                            if (!turns.TryTake(out var t, TimeSpan.FromSeconds(120))) { Console.WriteLine($"  FAIL {model}: no answer"); failed++; break; }
                            double cli = (t.TotalCostUsd ?? 0) - before;
                            before = t.TotalCostUsd ?? before;
                            var u = t.Usage.AsRequest(); // one request per turn here
                            var price = prices.For(t.Model ?? model.ToCli()) ?? prices.For(model.ToCli());
                            double ours = price.Cost(u);
                            bool ok = Math.Abs(ours - cli) <= 1e-9 + cli * 1e-6;
                            if (!ok) failed++;
                            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {model,-6} {prompt,-9} in {u.Input,3} · out {u.Output,3} · read {u.CacheRead,5:N0} · write {u.CacheCreation,5:N0} (1h {u.CacheCreation1h:N0})"
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
                    using (var session = NewSession(new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = PermissionModeChoice.AcceptEdits })) // not Plan: plan mode answers with Sonnet
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
        /// Plan mode is the known exception: the CLI answers with Sonnet there (2.1.316-2.1.322), ~20x the cost.
        /// </summary>
        public static int Run(string rawLogPath, PermissionModeChoice mode = PermissionModeChoice.AcceptEdits, int turns = 2)
        {
            string exe = ClaudeCliLocator.Resolve("");
            if (exe == null) { Console.WriteLine("SKIP: claude CLI not found"); return 2; }
            Console.WriteLine("claude: " + exe);
            if (mode == PermissionModeChoice.Plan)
                Console.WriteLine("  note: plan mode answers with Sonnet even when Haiku is asked for (known since 2.1.316): ~20x the cost, and the Haiku check fails");

            string dir = Path.Combine(Path.GetTempPath(), "cvs-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var events = new BlockingCollection<ClaudeEvent>();
            var raw = new StringBuilder();
            var failures = new List<string>();
            void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) failures.Add(what); }

            var session = NewSession(new ClaudeLaunchOptions
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
            var session = NewSession(new ClaudeLaunchOptions { WorkingDirectory = dir, Model = ModelChoice.Haiku, Effort = EffortChoice.Low, PermissionMode = mode });
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
            return requests > 0 ? 0 : 1; // the prompt is meant to raise at least one
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
