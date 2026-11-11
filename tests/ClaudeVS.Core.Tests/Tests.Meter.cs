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
            Eq("Read 13334|Shell 4000|MCP visualstudio 134", string.Join("|", m.ResultTokensByTool().Select(kv => kv.Key + " " + kv.Value)));
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { Input = 10 } });
            True(SessionSummary.Format(m).EndsWith("\nTool results: Read 13.3k · Shell 4.0k · MCP visualstudio 134"), SessionSummary.Format(m));
            Eq("MCP visualstudio 134|Read 13334|Shell 4000", string.Join("|", m.LastTurnResultTokensByTool.OrderBy(kv => kv.Key).Select(kv => kv.Key + " " + kv.Value)), "the turn's, for the usage log");
            Eq("MCP github", TokenMeter.ToolGroup("mcp__github__create_issue"), "per server");
            Eq("MCP claude_ai_Claude_Docs", TokenMeter.ToolGroup("mcp__claude_ai_Claude_Docs__batch"), "a connector");
            Eq("MCP", TokenMeter.ToolGroup("mcp__"), "no server name");
            Eq("MCP", TokenMeter.ToolGroup("mcp__oddtool"), "no tool part");
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

        private static void Test_ResumeNote()
        {
            var now = new DateTime(2026, 11, 6, 15, 0, 0, DateTimeKind.Utc);
            Eq(null, CacheWindow.ResumeNote(now.AddMinutes(-40), 50_000, now), "within the hour: may still be warm");
            Eq(null, CacheWindow.ResumeNote(now.AddHours(-5), 4_000, now), "small context");
            Eq("This conversation was last active 5 hours ago, so its prompt cache has expired: the first prompt sends ~45.0k tokens at the full input price. For a new task, a New chat is cheaper.",
                CacheWindow.ResumeNote(now.AddHours(-5), 45_000, now));
            Eq("To go on here, /compact first: it reads the ~45.0k tokens once to summarize them, so the next prompts start small.", CacheWindow.ResumeCompactAdvice(now.AddHours(-5), 45_000, now), "the /compact button's line");
            Eq(null, CacheWindow.ResumeCompactAdvice(now.AddMinutes(-40), 45_000, now), "no note: no button");
            Eq(null, CacheWindow.ResumeCompactAdvice(now.AddHours(-5), 4_000, now), "small context: no button");
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
    }
}
