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
                        if (e.ToolName == "AskUserQuestion" && answer["questions"] is Newtonsoft.Json.Linq.JArray qs)
                        {
                            var answers = new Newtonsoft.Json.Linq.JObject();
                            foreach (var q in qs.OfType<Newtonsoft.Json.Linq.JObject>())
                                answers[(string)q["question"]] = (string)q["options"]?.First?["label"];
                            answer["answers"] = answers;
                            Console.WriteLine("    answering: " + answers.ToString(Newtonsoft.Json.Formatting.None));
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
