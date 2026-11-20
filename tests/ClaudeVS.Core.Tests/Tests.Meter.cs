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

        /// <summary>
        /// Two Haiku turns and /compact with --prompt-suggestions (2.1.326). The debug log of that run showed the
        /// suggestion after turn 1 (input 506, output 4, cache read 27,517, cache write 52), the one after turn 2
        /// (506, 83, 30,217, 53) and the compact summary (2,133, 1,223, 29,697, 384); each is billed with the next result.
        /// </summary>
        private static void Test_Meter_SideRequests_RealCli_2_1_326()
        {
            var meter = new TokenMeter();
            meter.FreshSession();
            var turns = new List<(TokenUsage Side, bool Compact, int Agents, double Cost, double SideCost)>();
            foreach (var e in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.326-side-requests.jsonl")).SelectMany(l => new ClaudeStreamParser().Parse(l)))
            {
                meter.Observe(e);
                if (e.Kind == ClaudeEventKind.TurnCompleted)
                    turns.Add((meter.LastTurnSide, meter.LastTurnSideIsCompact, meter.LastTurnSubagents.Count, meter.LastTurnCostUsd, meter.LastTurnSideCostUsd));
            }
            Eq(3, turns.Count, "two turns and /compact");
            True(turns[0].Side.IsEmpty && turns[0].Agents == 0, "turn 1: nothing before it");
            var s1 = turns[1].Side;
            Eq("506/4/27517/52", $"{s1.Input}/{s1.Output}/{s1.CacheRead}/{s1.CacheCreation}", "turn 2 carries the suggestion after turn 1, exactly as the debug log has it");
            Eq(0, turns[1].Agents, "not a subagent");
            True(!turns[1].Compact, "a suggestion");
            var s2 = turns[2].Side;
            Eq("2639/1306/59914/437", $"{s2.Input}/{s2.Output}/{s2.CacheRead}/{s2.CacheCreation}", "the compact summary plus the suggestion after turn 2");
            True(turns[2].Compact, "the turn compacted");
            Eq(0, turns[2].Agents, "not a subagent either");
            // Priced at Haiku's list prices, as Claude Code bills it: the side request is part of the turn's reported cost
            var h = new PriceTable().Haiku;
            Eq(Math.Round((506 * h.Input + 4 * h.Output + 27517 * h.CacheRead + 52 * 2 * h.Input) / 1e6, 9), Math.Round(turns[1].SideCost, 9), "suggestion priced (its cache write is 1-hour: twice the input price)");
            True(turns[1].SideCost < turns[1].Cost, $"inside the turn's cost: {turns[1].SideCost} of {turns[1].Cost}");
            True(Math.Abs(turns[2].Cost - turns[2].SideCost) < 1e-6, $"/compact's whole cost is the side requests: {turns[2].SideCost} of {turns[2].Cost}");
            Eq(turns[1].SideCost, meter.SessionSuggestionsUsd, "the compact turn's share goes to compaction, not suggestions");
            Eq(1, meter.SessionSuggestionTurns);
            Eq(turns[2].SideCost, meter.SessionCompactUsd);

            // The 2.1.325 capture (Phase 50) shows the same: turn 2 carries a suggestion (506 in, as every one so far)
            var old = new TokenMeter();
            old.FreshSession();
            foreach (var e in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.325-prompt-suggestion.jsonl")).SelectMany(l => new ClaudeStreamParser().Parse(l))) old.Observe(e);
            Eq(506L, old.LastTurnSide.Input, "2.1.325: the suggestion's input");
            Eq(0, old.LastTurnSubagents.Count, "2.1.325: not a subagent");

            // A subagent and a suggestion on the main model in the same turn: the agent's streamed message (output low)
            // takes its part, the rest is the side request; a leftover of output alone stays the agent's
            var tr = new SubagentUsageTracker();
            tr.StartsFromZero();
            var main = new TokenUsage { Input = 4, Output = 300, CacheRead = 40_000, CacheCreation = 1_000 };
            var agent = new TokenUsage { Input = 3, Output = 120, CacheRead = 20_000, CacheCreation = 400 };
            var suggestion = new TokenUsage { Input = 506, Output = 9, CacheRead = 30_000, CacheCreation = 60 };
            tr.ObserveSubagent(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, IsSubagent = true, MessageId = "a", Model = "m", Usage = new TokenUsage { Input = 3, Output = 2, CacheRead = 20_000, CacheCreation = 400 } });
            var agents = tr.TurnCompleted(new Dictionary<string, ModelUsageEntry> { ["m"] = new ModelUsageEntry { Usage = main + agent + suggestion } }, "m", main);
            Eq("506/127/30000/60", $"{tr.LastSide.Input}/{tr.LastSide.Output}/{tr.LastSide.CacheRead}/{tr.LastSide.CacheCreation}", "the side request takes the output the agent's stream didn't show");
            Eq("3/2/20000/400", $"{agents["m"].Input}/{agents["m"].Output}/{agents["m"].CacheRead}/{agents["m"].CacheCreation}", "the agent: its streamed counts");
            var only = new SubagentUsageTracker();
            only.StartsFromZero();
            only.ObserveSubagent(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, IsSubagent = true, MessageId = "a", Model = "m", Usage = new TokenUsage { Input = 3, Output = 2, CacheRead = 20_000, CacheCreation = 400 } });
            var alone = only.TurnCompleted(new Dictionary<string, ModelUsageEntry> { ["m"] = new ModelUsageEntry { Usage = main + agent } }, "m", main);
            Eq(120L, alone["m"].Output, "no side request: the agent's output is exact");
            True(only.LastSide.IsEmpty, "nothing on the side");
        }

        /// <summary>
        /// 2.1.326: one Haiku turn with --prompt-suggestions (a suggestion came after it), the process closed, then the
        /// session resumed in a new process for one more turn. The resumed result's total_cost_usd and modelUsage carry on
        /// the first process's, its last suggestion included (input 4 + 506 + 2); model_usage_since_last_result has
        /// only the new turn.
        /// </summary>
        private static void Test_Meter_ResumedFirstTurn_RealCli_2_1_326()
        {
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.326-resume-after-suggestion.jsonl"))
                .SelectMany(l => new ClaudeStreamParser().Parse(l)).ToList();
            var results = events.Where(e => e.Kind == ClaudeEventKind.TurnCompleted).ToList();
            Eq(2, results.Count, "a turn, then the resumed one");
            Eq(1, events.Count(e => e.Kind == ClaudeEventKind.PromptSuggestion), "the suggestion after the first");
            var resumed = results[1];
            Eq(512L, resumed.ModelUsage["claude-haiku-5-5"].Usage.Input, "modelUsage carries on, the suggestion included");
            Eq(2L, resumed.ModelUsageSinceLastResult["claude-haiku-5-5"].Usage.Input, "since the last result: this turn only");

            // The chat resumes with a new meter: the turn costs what it billed, not the conversation's carried total
            var meter = new TokenMeter();
            meter.ResumedSession(null);
            meter.Observe(resumed);
            True(meter.LastTurnCostBilled, "billed since the last result");
            Eq(0.0004764, Math.Round(meter.LastTurnCostUsd, 7), "this turn's cost");
            Eq(resumed.TotalCostUsd.Value, meter.SessionCostUsd, "the conversation's total, as billed");
            True(meter.LastTurnSide.IsEmpty && meter.LastTurnSubagents.Count == 0, "no side request, no subagent: the earlier process's suggestion isn't this turn's");
            // So the first process's last suggestion was billed only in the carried total: the turn costs leave it out
            double firstTurn = results[0].TotalCostUsd.Value;
            True(resumed.TotalCostUsd.Value - firstTurn - meter.LastTurnCostUsd > 0.0003, "the unbilled suggestion, about $0.0004, is in the carried total only");

            // An older CLI without the field: the growth of the total, as before
            var old = new TokenMeter();
            old.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, TotalCostUsd = 0.01 });
            old.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, TotalCostUsd = 0.015 });
            True(!old.LastTurnCostBilled, "no field");
            Eq(0.005, Math.Round(old.LastTurnCostUsd, 6), "the growth of the total");
        }

        /// <summary>
        /// 2.1.326 with CLAUDE_CODE_MAX_OUTPUT_TOKENS=400: a background subagent asked for a 700-word essay, then the
        /// main agent's own answer, each cut off and asked to go on (synthetic "Output token limit hit." messages).
        /// </summary>
        /// <summary>
        /// 2.1.327 with CLAUDE_CODE_MAX_OUTPUT_TOKENS=150 (the init line left out): the main agent's answer is cut off three
        /// times, each asked to go on, and then the turn fails ("API Error: Claude's response exceeded the 150 output
        /// token maximum", terminal_reason api_error). The retries count in the failed turn, and the box says what to do.
        /// </summary>
        private static void Test_Meter_CutOffThenFailed_RealCli_2_1_327()
        {
            var parser = new ClaudeStreamParser();
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.327-cut-off-then-failed.jsonl"))
                .SelectMany(l => parser.Parse(l)).ToList();
            var retries = events.Where(e => e.Kind == ClaudeEventKind.OutputLimitRetry).ToList();
            Eq(3, retries.Count, "three cut-offs");
            True(retries.All(e => !e.IsSubagent), "all the main agent's");

            var done = events.Single(e => e.Kind == ClaudeEventKind.TurnCompleted);
            True(done.IsError, "the turn failed");
            Eq("api_error", done.TerminalReason, "terminal_reason");
            True(ClaudeErrorHints.Explain(done.Text)?.Contains("output token limit") == true, "explained: " + ClaudeErrorHints.Explain(done.Text));

            var meter = new TokenMeter();
            meter.FreshSession();
            foreach (var e in events) meter.Observe(e);
            Eq(3, meter.LastTurnOutputRetries, "counted in the failed turn");
            True(meter.LastTurnOutputRetryCostUsd > 0, "and priced: " + meter.LastTurnOutputRetryCostUsd);
        }

        private static void Test_Meter_OutputLimitRetries_RealCli_2_1_326()
        {
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.326-output-limit-retries.jsonl"))
                .SelectMany(l => new ClaudeStreamParser().Parse(l)).ToList();
            var retries = events.Where(e => e.Kind == ClaudeEventKind.OutputLimitRetry).ToList();
            Eq(6, retries.Count, "every cut-off");
            Eq(3, retries.Count(e => e.IsSubagent), "the subagent's");
            True(retries.Where(e => e.IsSubagent).All(e => e.ParentToolUseId != null && e.ParentToolUseId.StartsWith("toolu")), "whose: its Agent call");
            True(retries.Any(e => e.Text.Contains("give the whole answer again, more briefly")), "2.1.325's ask for the whole answer again");
            Eq(0, events.Count(e => e.Kind == ClaudeEventKind.OutputLimitRetry && !e.Text.StartsWith(ClaudeStreamParser.OutputLimitRetryText)), "only those");

            var meter = new TokenMeter();
            meter.FreshSession();
            var perTurn = new List<(int Retries, double Usd)>();
            foreach (var e in events)
            {
                meter.Observe(e);
                if (e.Kind == ClaudeEventKind.TurnCompleted) perTurn.Add((meter.LastTurnOutputRetries, meter.LastTurnOutputRetryCostUsd));
            }
            Eq(2, perTurn.Count);
            Eq(6, perTurn.Sum(t => t.Retries), "counted in the turns they came in");
            Eq(6, meter.SessionOutputRetries);
            True(meter.SessionOutputRetryCostUsd > 0, "the requests that went on: " + meter.SessionOutputRetryCostUsd);
            True(perTurn.All(t => t.Retries == 0 || t.Usd > 0), "each turn with retries priced them");

            // The agent's row
            var tasks = new AgentTaskList();
            foreach (var e in events) { if (e.Kind == ClaudeEventKind.Task) tasks.Apply(e.Payload); tasks.ObserveRetry(e); }
            Eq(3, tasks.Tasks.Single(t => t.IsAgent).OutputRetries, "on the subagent's task");

            // Footer and report
            Eq(" · cut off 2× ($0.004 to go on)", TurnFooter.Retries(2, 0.004));
            Eq(" · cut off 1×", TurnFooter.Retries(1, 0.0002), "too small to price");
            Eq("", TurnFooter.Retries(0, 0));
            var t0 = DateTime.UtcNow.AddDays(-1);
            var rep = SavingsReport.Build(new[]
            {
                new UsageRecord { Timestamp = t0, CostUsd = 0.05, OutputRetries = 2, OutputRetryCostUsd = 0.02 },
                new UsageRecord { Timestamp = t0, CostUsd = 0.03, OutputRetries = 3, OutputRetryCostUsd = 0.01 },
                new UsageRecord { Timestamp = t0, CostUsd = 0.01 },
            }, new PriceTable());
            Eq("5 retries in 2 turns, $0.030 for the requests that went on", rep.OutputRetriesLine());
            True(rep.ToText().Contains("Cut off, asked to go on:    5 retries"), rep.ToText());
            Eq(null, SavingsReport.Build(new[] { new UsageRecord { Timestamp = t0, CostUsd = 0.01 } }, new PriceTable()).OutputRetriesLine(), "none: no line");
            var csv = new StringWriter();
            UsageCsv.Write(new[] { new UsageRecord { Timestamp = t0, CostUsd = 0.05, OutputRetries = 3, OutputRetryCostUsd = 0.0123 } }, csv);
            { var row = CsvRows(csv.ToString())[0]; Eq("3|0.0123", row["output_retries"] + "|" + row["output_retry_cost_usd"], "csv: output_retries, output_retry_cost_usd"); }
        }

        /// <summary>
        /// The quiet-build hook from `--live-setup` with 2.1.327: Claude asks for `dotnet build`, the hook rewrites it and
        /// asks with its reason, the quiet build runs. Counted as one quiet `dotnet build`; also the auto-approve path,
        /// refused and unrelated commands, the rules, the report line and the CSV.
        /// </summary>
        private static void Test_QuietRuns_RealCli_2_1_327()
        {
            var parser = new ClaudeStreamParser();
            var files = new TurnFileActivity();
            foreach (var e in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.327-quiet-build-hook.jsonl")).SelectMany(l => parser.Parse(l)))
                files.Observe(e);
            Eq("dotnet build:1", UsageCsv.Quiet(files.QuietRuns), "one quiet dotnet build");
            True(files.QuietTokens > 0 && files.QuietTokens < 100, "its short output: " + files.QuietTokens);

            void Run(string id, string command, string allowedBy = null, bool asked = false, string result = "ok", bool error = false)
            {
                files.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolUseId = id, ToolName = "PowerShell", ToolInput = new JObject { ["command"] = command } });
                if (asked) files.Observe(new ClaudeEvent { Kind = ClaudeEventKind.PermissionRequest, ToolUseId = id, Payload = new JObject { ["decision_reason"] = TurnFileActivity.QuietReasonPrefix + " x" } });
                files.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolResult, ToolUseId = id, ResultChars = result.Length, ResultText = result, AllowedBy = allowedBy, IsError = error });
            }
            files.Reset();
            Run("1", "git log", allowedBy: ClaudeStreamParser.HookDecision);               // auto-approve: allowed by the hook
            Run("2", "git log --oneline", allowedBy: "rule");                              // a rule ran it as written
            Run("3", "dotnet test", asked: true, error: true, result: "The user doesn't want to proceed with this tool use."); // refused
            Run("4", "ls", asked: true);                                                   // not a kind the hook rewrites
            Eq("git log:1", UsageCsv.Quiet(files.QuietRuns), "only the run the hook made quiet");

            Eq("dotnet build", TurnFileActivity.QuietKind("dotnet build src/A.csproj"));
            Eq("dotnet test", TurnFileActivity.QuietKind("  dotnet test"));
            Eq("msbuild", TurnFileActivity.QuietKind("msbuild.exe x.sln"));
            Eq("npm test", TurnFileActivity.QuietKind("npm run test"));
            Eq(null, TurnFileActivity.QuietKind("dotnet build -v q"), "verbosity given");
            Eq(null, TurnFileActivity.QuietKind("git log -n 5"), "a count given");
            Eq(null, TurnFileActivity.QuietKind("dotnet build | tail"), "a pipe");
            Eq(null, TurnFileActivity.QuietKind("npm test --watch"), "watch");

            var t0 = DateTime.UtcNow.AddDays(-1);
            var rep = SavingsReport.Build(new[]
            {
                new UsageRecord { Timestamp = t0, CostUsd = 0.01, QuietRuns = new Dictionary<string, int> { ["dotnet build"] = 2, ["git log"] = 1 }, QuietTokens = 900 },
                new UsageRecord { Timestamp = t0, CostUsd = 0.01, QuietRuns = new Dictionary<string, int> { ["dotnet build"] = 7, ["git log"] = 2 }, QuietTokens = 300 },
            }, new PriceTable());
            Eq("12 commands made quiet (dotnet build 9, git log 3), ~1.2k tok came back", rep.QuietRunsLine());
            True(rep.ToText().Contains("Reads and commands:") && rep.ToText().Contains("  Quiet-build hook:         12 commands"), rep.ToText());
            var csv = new StringWriter();
            UsageCsv.Write(new[] { new UsageRecord { Timestamp = t0, QuietRuns = new Dictionary<string, int> { ["git log"] = 1, ["dotnet build"] = 2 }, QuietTokens = 40 } }, csv);
            { var row = CsvRows(csv.ToString())[0]; Eq("dotnet build:2;git log:1|40", row["quiet_runs"] + "|" + row["quiet_tokens"], "csv: quiet_runs, quiet_tokens"); }
        }

        /// <summary>Builds, tests and git logs the hook left alone and that ran loud: counted, the tip, the report and the CSV.</summary>
        private static void Test_LoudRuns()
        {
            var files = new TurnFileActivity();
            void Run(string id, string command, int chars, string allowedBy = null, bool asked = false)
            {
                files.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolUseId = id, ToolName = "Bash", ToolInput = new JObject { ["command"] = command } });
                if (asked) files.Observe(new ClaudeEvent { Kind = ClaudeEventKind.PermissionRequest, ToolUseId = id, Payload = new JObject { ["decision_reason"] = TurnFileActivity.QuietReasonPrefix + " x" } });
                files.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolResult, ToolUseId = id, ResultChars = chars, ResultText = "out", AllowedBy = allowedBy });
            }
            Run("1", "dotnet build -v detailed", 40_000);
            Run("2", "git log | head -500", 30_000);
            Run("3", "dotnet build -v n", 600);                 // small: not counted
            Run("4", "dotnet build", 40_000, asked: true);      // made quiet: not loud
            Run("5", "ls -R", 90_000);                          // not a build, test or log
            Eq("dotnet build:1;git log:1", UsageCsv.Quiet(files.LoudRuns), "two loud");
            Eq(TokenEstimate.FromChars(40_000) + TokenEstimate.FromChars(30_000), (int)files.LoudTokens);
            Eq("2", files.LastLoud?.ToolUseId, "the last one, for the tip");

            Eq("dotnet build", TurnFileActivity.BaseKind("dotnet build -v d | more"));
            Eq("git log", TurnFileActivity.BaseKind("git log -n 300"));
            Eq(null, TurnFileActivity.BaseKind("echo dotnet build"));

            string verbosity = TurnFileActivity.LoudTip("dotnet build -v detailed", "dotnet build", 12000, hookInstalled: true);
            True(verbosity.Contains("~12.0k tokens") && verbosity.Contains("because it named a verbosity") && verbosity.Contains("the plain `dotnet build`"), verbosity);
            True(TurnFileActivity.LoudTip("git log | head -500", "git log", 9000, true).Contains("piped or chained"), "pipe");
            True(TurnFileActivity.LoudTip("git log -n 500", "git log", 9000, true).Contains("named a count"), "count");
            True(TurnFileActivity.LoudTip("dotnet build", "dotnet build", 9000, true).Contains("ran as written"), "plain but not through the hook");
            True(TurnFileActivity.LoudTip("dotnet build", "dotnet build", 9000, false).Contains("Install Token-Saving Settings and Hooks"), "no hook: install it");
            Eq("Run the plain `git log` instead, with no verbosity, pipe or count, so the quiet-build hook can make it quiet.", TurnFileActivity.PlainCommandRequest("git log"), "the link's request");
            Eq("You already have Big.cs from earlier in this conversation, unchanged; use that instead of reading it again.", ReadTracker.AlreadyHaveRequest(@"C:\repo\src\Big.cs"));

            var t0 = DateTime.UtcNow.AddDays(-1);
            var rep = SavingsReport.Build(new[]
            {
                new UsageRecord { Timestamp = t0, CostUsd = 0.01, LoudRuns = new Dictionary<string, int> { ["dotnet build"] = 2 }, LoudTokens = 12000 },
                new UsageRecord { Timestamp = t0, CostUsd = 0.01, LoudRuns = new Dictionary<string, int> { ["git log"] = 1 }, LoudTokens = 6000 },
            }, new PriceTable());
            Eq("3 (dotnet build 2, git log 1), ~18.0k tok", rep.LoudRunsLine());
            True(rep.ToText().Contains("  Loud builds and logs:     3"), rep.ToText());
            var csv = new StringWriter();
            UsageCsv.Write(new[] { new UsageRecord { Timestamp = t0, LoudRuns = new Dictionary<string, int> { ["git log"] = 1 }, LoudTokens = 6000 } }, csv);
            { var row = CsvRows(csv.ToString())[0]; Eq("git log:1|6000", row["loud_runs"] + "|" + row["loud_tokens"], "csv: loud_runs, loud_tokens"); }
        }

        /// <summary>A subagent's tool result carries its Agent call, and its re-reads count on that agent's task.</summary>
        private static void Test_SubagentReread_OnAgentRow()
        {
            var ev = new ClaudeStreamParser().Parse("{\"type\":\"user\",\"parent_tool_use_id\":\"toolu_agent1\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_r1\",\"content\":\"x\"}]}}")
                .Single(e => e.Kind == ClaudeEventKind.ToolResult);
            True(ev.IsSubagent, "a subagent's");
            Eq("toolu_agent1", ev.ParentToolUseId, "its Agent call");
            Eq(null, new ClaudeStreamParser().Parse("{\"type\":\"user\",\"parent_tool_use_id\":null,\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t\",\"content\":\"x\"}]}}")
                .Single().ParentToolUseId, "the main agent's: none");

            var tasks = new AgentTaskList();
            tasks.Apply(JObject.Parse("{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"a1\",\"tool_use_id\":\"toolu_agent1\",\"description\":\"Look\",\"task_type\":\"local_agent\",\"subagent_type\":\"Explore\"}"));
            True(tasks.AddReread("toolu_agent1", 3000) && tasks.AddReread("toolu_agent1", 1000), "counted");
            True(!tasks.AddReread("toolu_other", 3000) && !tasks.AddReread(null, 3000), "no such task");
            Eq(4000L, tasks.Tasks.Single().RereadTokens);
            Eq("Explore", tasks.SubagentTypeOf("toolu_agent1"));

            Eq(null, SessionNotices.SubagentRereadTip(4999, "Explore", @"C:\r\A.cs"), "under 5k");
            string tip = SessionNotices.SubagentRereadTip(6000, "Explore", @"C:\r\A.cs");
            True(tip.StartsWith("An Explore agent read A.cs again") && tip.Contains("~6.0k tokens") && tip.Contains("asking it directly is cheaper"), tip);
            True(SessionNotices.SubagentRereadTip(6000, "general-purpose", "B.cs").StartsWith("A general-purpose agent"), "a / an");
            True(SessionNotices.SubagentRereadTip(6000, "fork", "B.cs").StartsWith("A fork read"), "a fork");
            True(SessionNotices.SubagentRereadTip(6000, null, "B.cs").StartsWith("A subagent read"), "unknown kind");
        }

        /// <summary>Subagents reading files the main agent already read: kept across turns, cleared by a compaction or a new conversation.</summary>
        private static void Test_SubagentRereads()
        {
            var files = new TurnFileActivity();
            int n = 0;
            void Read(string path, int chars, bool subagent)
            {
                string id = "r" + (++n);
                files.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolUseId = id, ToolName = "Read", ToolInput = new JObject { ["file_path"] = path }, IsSubagent = subagent });
                files.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolResult, ToolUseId = id, ResultChars = chars, IsSubagent = subagent });
            }
            Read(@"C:\repo\A.cs", 9000, subagent: false);
            Read(@"C:\repo\B.cs", 9000, subagent: true);   // not read by the main agent: a new read
            Eq(0L, files.SubagentRereadTokens, "nothing re-read yet");
            files.Reset();                                  // the next turn
            Read(@"C:\repo\a.cs", 6000, subagent: true);    // the main agent had it (path case aside)
            Eq((long)TokenEstimate.FromChars(6000), files.SubagentRereadTokens, "re-read in a later turn");
            Eq(@"C:\repo\a.cs", files.LastReread?.Path, "the last re-read, for the agent's row and the tip");
            Eq("r" + n, files.LastReread?.ReadToolUseId, "matched by the Read call");
            files.Observe(new ClaudeEvent { Kind = ClaudeEventKind.Compacted });
            files.Reset();
            Read(@"C:\repo\A.cs", 6000, subagent: true);
            Eq(0L, files.SubagentRereadTokens, "after a compaction the main agent no longer has it");
            Read(@"C:\repo\C.cs", 3000, subagent: false);
            files.ResetConversation();
            Read(@"C:\repo\C.cs", 3000, subagent: true);
            Eq(0L, files.SubagentRereadTokens, "a new conversation");

            var t0 = DateTime.UtcNow.AddDays(-1);
            var rep = SavingsReport.Build(new[]
            {
                new UsageRecord { Timestamp = t0, CostUsd = 0.01, SubagentRereadTokens = 20000 },
                new UsageRecord { Timestamp = t0, CostUsd = 0.01, SubagentRereadTokens = 10000 },
            }, new PriceTable());
            Eq("~30.0k tok in 2 turns", rep.SubagentRereadsLine());
            True(rep.ToText().Contains("  Subagents re-reading:     ~30.0k tok"), rep.ToText());
            var reads = new ConversationReads();
            reads.Add(new UsageRecord { SubagentRereadTokens = 4000 });
            Eq("Subagents re-reading what the conversation had: ~4.0k tok", reads.Lines().Single());
            var csv = new StringWriter();
            UsageCsv.Write(new[] { new UsageRecord { Timestamp = t0, SubagentRereadTokens = 4000 } }, csv);
            Eq("4000", CsvRows(csv.ToString())[0]["agent_reread_tokens"], "csv: agent_reread_tokens");
        }

        /// <summary>Copy session summary's reads: the conversation's turns added up.</summary>
        private static void Test_ConversationReads_InSummary()
        {
            var reads = new ConversationReads();
            reads.Add(new UsageRecord
            {
                ReadFolders = new Dictionary<string, long> { ["src/Core"] = 30000, ["src/App/obj"] = 2000 },
                RefCeilingTokens = 40000, RefReadTokens = 8000, RepeatReadTokens = 3000,
                QuietRuns = new Dictionary<string, int> { ["dotnet build"] = 2 }, QuietTokens = 200,
            });
            reads.Add(new UsageRecord
            {
                ReadFolders = new Dictionary<string, long> { ["src/Core"] = 10000, ["a"] = 1, ["b"] = 1, ["c"] = 1, ["d"] = 1 },
                RefCeilingTokens = 10000, RefReadTokens = 2000,
                LoudRuns = new Dictionary<string, int> { ["git log"] = 1 }, LoudTokens = 9000,
            });
            var lines = reads.Lines();
            Eq("Reads by folder: src/Core 40.0k · src/App/obj 2.0k · a 1 · b 1 · c 1 · other 1", lines[0]);
            Eq("References: Claude read ~10.0k of the ~50.0k tok attached (20%)", lines[1]);
            Eq("Read again, unchanged: ~3.0k tok", lines[2]);
            Eq("Builds and logs: 2 made quiet (~200 tok back) · 1 loud (~9.0k tok)", lines[3]);

            var meter = new TokenMeter();
            string text = SessionSummary.Format(meter, "t", null, reads);
            True(text.Contains("\nReads by folder: src/Core 40.0k") && text.Contains("\nBuilds and logs: "), text);
            Eq(SessionSummary.Format(meter, "t"), SessionSummary.Format(meter, "t", null, new ConversationReads()), "nothing read: no lines");
            reads.Reset();
            Eq(0, reads.Lines().Count, "reset with the conversation");
        }

        /// <summary>A turn's folders in the usage log: the 10 largest, the rest added up.</summary>
        private static void Test_ReadFolders_Capped()
        {
            var many = Enumerable.Range(1, 14).ToDictionary(i => "f" + i, i => (long)i * 100);
            var capped = TurnFileActivity.Capped(many);
            Eq(11, capped.Count, "10 and the rest");
            True(capped.ContainsKey("f14") && capped.ContainsKey("f5") && !capped.ContainsKey("f4"), "the largest kept");
            Eq(100L + 200 + 300 + 400, capped[TurnFileActivity.OtherFolders], "f1–f4 added up");
            Eq(many.Values.Sum(), capped.Values.Sum(), "nothing lost");
            Eq(3, TurnFileActivity.Capped(new Dictionary<string, long> { ["a"] = 1, ["b"] = 2, ["c"] = 3 }).Count, "few: as they are");
            Eq(null, TurnFileActivity.Capped(null));
        }

        /// <summary>Repeated reads over time: the report's line and the CSV column.</summary>
        private static void Test_RepeatReads_InReport()
        {
            var t0 = DateTime.UtcNow.AddDays(-1);
            var rep = SavingsReport.Build(new[]
            {
                new UsageRecord { Timestamp = t0, CostUsd = 0.01, RepeatReadTokens = 9000 },
                new UsageRecord { Timestamp = t0, CostUsd = 0.01, RepeatReadTokens = 5000 },
                new UsageRecord { Timestamp = t0, CostUsd = 0.01 },
            }, new PriceTable());
            Eq("~14.0k tok in 2 turns", rep.RepeatReadsLine());
            True(rep.ToText().Contains("  Read again, unchanged:    ~14.0k tok in 2 turns"), rep.ToText());
            Eq(null, SavingsReport.Build(new[] { new UsageRecord { Timestamp = t0, CostUsd = 0.01 } }, new PriceTable()).RepeatReadsLine(), "none");
            True(!SavingsReport.Build(new[] { new UsageRecord { Timestamp = t0, CostUsd = 0.01 } }, new PriceTable()).ToText().Contains("Reads and commands:"), "nothing read: no section");
            string text = rep.ToText();
            True(text.IndexOf("Tool results", StringComparison.Ordinal) < 0 || text.IndexOf("Reads and commands:", StringComparison.Ordinal) > text.IndexOf("Tool results", StringComparison.Ordinal), "the section comes after Tool results");
            var csv = new StringWriter();
            UsageCsv.Write(new[] { new UsageRecord { Timestamp = t0, RepeatReadTokens = 9000 } }, csv);
            Eq("9000", CsvRows(csv.ToString())[0]["repeat_read_tokens"], "csv: repeat_read_tokens");
        }

        /// <summary>Reads by folder: grouping under the working folder, the report's top folders and the deny-rule hint.</summary>
        private static void Test_ReadFolders()
        {
            var byFile = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\repo\src\Core\A.cs"] = 9000,
                [@"C:\repo\src\Core\Deep\B.cs"] = 1000,
                [@"C:\repo\src\App\obj\Debug\x.g.cs"] = 4000,
                [@"C:\repo\README.md"] = 500,
                [@"D:\elsewhere\notes.txt"] = 300,
            };
            var folders = TurnFileActivity.ByFolder(byFile, @"C:\repo\");
            Eq(10000L, folders["src/Core"], "two levels: Deep is counted in src/Core");
            Eq(4000L, folders["src/App/obj"], "build output keeps its obj, deeper than two levels");
            Eq(500L, folders["."], "the working folder's top");
            Eq(300L, folders[TurnFileActivity.OutsideFolder], "outside: grouped, its path not kept");
            True(!folders.Keys.Any(k => k.Contains("elsewhere")), "no outside paths");
            var noisy = TurnFileActivity.ByFolder(new Dictionary<string, long>
            {
                [@"C:\repo\node_modules\react\index.js"] = 10,
                [@"C:\repo\web\wwwroot\lib\jquery\jquery.js"] = 20,
                [@"C:\repo\web\wwwroot\css\site.css"] = 30,
            }, @"C:\repo");
            Eq("node_modules,web/wwwroot,web/wwwroot/lib", string.Join(",", noisy.Keys.OrderBy(k => k, StringComparer.Ordinal)), "cut at the vendored folder; other wwwroot files by depth");

            Eq("Read(./**/obj/**)", SavingsReport.NoiseRule("src/App/obj"), "build output");
            Eq("Read(./**/wwwroot/lib/**)", SavingsReport.NoiseRule("web/wwwroot/lib"), "a two-part noise folder");
            Eq(null, SavingsReport.NoiseRule("src/Objects"), "a name that only starts like one");
            Eq(null, SavingsReport.NoiseRule(TurnFileActivity.OutsideFolder));

            var t0 = DateTime.UtcNow.AddDays(-1);
            var rep = SavingsReport.Build(new[]
            {
                new UsageRecord { Timestamp = t0, CostUsd = 0.01, ReadFolders = new Dictionary<string, long> { ["src/Core"] = 6000, ["node_modules/react"] = 3000 } },
                new UsageRecord { Timestamp = t0, CostUsd = 0.01, ReadFolders = new Dictionary<string, long> { ["src/Core"] = 1000 } },
            }, new PriceTable());
            var top = rep.TopReadFolders();
            Eq(2, top.Count);
            True(top[0].StartsWith("src/Core") && top[0].Contains("~7.0k tok (70%)"), top[0]);
            True(top[1].Contains("add Read(./**/node_modules/**) to permissions.deny"), top[1]);
            True(rep.ToText().Contains("Folders read most:"), rep.ToText());
            Eq(0, SavingsReport.Build(new[] { new UsageRecord { Timestamp = t0, CostUsd = 0.01 } }, new PriceTable()).TopReadFolders().Count, "none: no section");

            // Two solutions: the same relative folder is two rows, named by solution; one solution: no names
            var two = SavingsReport.Build(new[]
            {
                new UsageRecord { Timestamp = t0, CostUsd = 0.01, Solution = @"C:\src\Shop\", ReadFolders = new Dictionary<string, long> { ["src/Core"] = 5000, ["src/App/obj"] = 3000 } },
                new UsageRecord { Timestamp = t0, CostUsd = 0.01, Solution = @"C:\src\Blog", ReadFolders = new Dictionary<string, long> { ["src/Core"] = 4000 } },
                new UsageRecord { Timestamp = t0, CostUsd = 0.01, Solution = @"C:\src\Shop", ReadFolders = new Dictionary<string, long> { ["src/Core"] = 1000 } },
            }, new PriceTable());
            Eq(6000L, two.ReadFolders["Shop: src/Core"], "Shop's src/Core, with and without a trailing slash");
            Eq(4000L, two.ReadFolders["Blog: src/Core"], "Blog's apart");
            True(two.TopReadFolders().Any(l => l.StartsWith("Shop: src/App/obj") && l.Contains("Read(./**/obj/**)")), "the rule still found: " + string.Join(" | ", two.TopReadFolders()));
            var one = SavingsReport.Build(new[] { new UsageRecord { Timestamp = t0, CostUsd = 0.01, Solution = @"C:\src\Shop", ReadFolders = new Dictionary<string, long> { ["src/Core"] = 5000 } } }, new PriceTable());
            True(one.ReadFolders.ContainsKey("src/Core"), "one solution: plain folders");
            var csv = new StringWriter();
            UsageCsv.Write(new[] { new UsageRecord { Timestamp = t0, ReadFolders = new Dictionary<string, long> { ["src/App/obj"] = 2000, ["src/Core"] = 40000 } } }, csv);
            var lines = csv.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
            True(lines[0].Split(',').Contains("read_folders"), "header: " + lines[0]);
            Eq("src/Core:40000;src/App/obj:2000", CsvRows(csv.ToString())[0]["read_folders"], "largest first");
        }

        /// <summary>A turn's reads against its attached references: the footer's tooltip, the report and the CSV.</summary>
        private static void Test_References_ReadAgainstCeiling()
        {
            var files = new TurnFileActivity();
            void Read(string id, string path, int chars, bool subagent = false, bool error = false)
            {
                files.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolUseId = id, ToolName = "Read", ToolInput = new JObject { ["file_path"] = path }, IsSubagent = subagent });
                files.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolResult, ToolUseId = id, ResultChars = chars, IsError = error, IsSubagent = subagent });
            }
            Read("a", @"C:\repo\src\Core\A.cs", 9000);
            Read("b", @"C:\repo\src\Core\Deep\B.cs", 3000, subagent: true); // a subagent's read is paid too
            Read("c", @"C:\repo\docs\README.md", 6000);                     // not under a reference
            Read("d", @"C:\repo\src\Core\Gone.cs", 500, error: true);       // failed: nothing came in
            Read("e", @"C:\repo\src\Core\A.cs", 9000);                      // read again: counted again

            var (n, tok) = files.ReadUnder(new[] { @"C:\repo\src\Core\", @"C:\repo\Other.cs" });
            Eq(2, n, "two files under the references");
            Eq((long)(TokenEstimate.FromChars(9000) * 2 + TokenEstimate.FromChars(3000)), tok, "their tokens, each read");
            Eq(0, files.ReadUnder(new[] { @"C:\repo\src\Co" }).Files, "a folder name's prefix is not the folder");

            Eq("References: Claude read 2 of 12 files, ~8.0k of ~40.0k tok (20%)", TurnFooter.References(2, 8000, 12, 40000));
            Eq("References: Claude read none of the 1 file (~3.2k tok if read whole)", TurnFooter.References(0, 0, 1, 3200));
            Eq(null, TurnFooter.References(0, 0, 0, 0), "nothing attached");

            var t0 = DateTime.UtcNow.AddDays(-1);
            var rep = SavingsReport.Build(new[]
            {
                new UsageRecord { Timestamp = t0, CostUsd = 0.05, RefFiles = 12, RefCeilingTokens = 40000, RefReadFiles = 2, RefReadTokens = 8000 },
                new UsageRecord { Timestamp = t0, CostUsd = 0.03, RefFiles = 1, RefCeilingTokens = 10000, RefReadFiles = 1, RefReadTokens = 2000 },
                new UsageRecord { Timestamp = t0, CostUsd = 0.01 },
            }, new PriceTable());
            Eq("in 2 turns Claude read ~10.0k of the ~50.0k tok attached (20%)", rep.ReferencesReadLine());
            True(rep.ToText().Contains("  References read:          in 2 turns"), rep.ToText());
            Eq(null, SavingsReport.Build(new[] { new UsageRecord { Timestamp = t0, CostUsd = 0.01 } }, new PriceTable()).ReferencesReadLine(), "none: no line");

            var csv = new StringWriter();
            UsageCsv.Write(new[] { new UsageRecord { Timestamp = t0, CostUsd = 0.05, RefCeilingTokens = 40000, RefReadTokens = 8000 } }, csv);
            var lines = csv.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
            True(ColumnsInOrder(lines[0].Split(','), "ref_ceiling_tokens", "ref_read_tokens"), "header: " + lines[0]);
            { var row = CsvRows(csv.ToString())[0]; Eq("40000|8000", row["ref_ceiling_tokens"] + "|" + row["ref_read_tokens"], "row"); }
            True(new UsageRecord { RefPaths = new List<string> { "x" } }.RefPaths != null
                 && !Newtonsoft.Json.JsonConvert.SerializeObject(new UsageRecord { RefPaths = new List<string> { "x" } }).Contains("\"x\""), "paths are not logged");
        }

        private static void Test_Meter_DefaultWindowIs1M()
        {
            // Every current model reports 1M, and 2.1.323 assumes 1M for an unknown model ID.
            var m = new TokenMeter();
            Eq(1_000_000L, new TokenMeterSettings().DefaultContextWindow);
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, LastRequestUsage = new TokenUsage { CacheRead = 50_000 } });
            Eq(5.0, Math.Round(m.ContextPercent, 1), "50k of 1M before the CLI reports a window");
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
            Eq(0.002, Math.Round(meter.SessionCostByModel["claude-haiku-5-5"], 3), "billed per model");
            True(!s.Contains("By model:"), "one model: no line");

            // A second model (an agent on Haiku 4.5): cost by model, named as Claude Code names them, most first
            var models = Models_2_1_328();
            meter.Observe(new ClaudeEvent
            {
                Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { Input = 10 },
                ModelUsageSinceLastResult = new Dictionary<string, ModelUsageEntry>
                {
                    ["claude-haiku-5-5"] = new ModelUsageEntry { CostUsd = 0.01 },
                    ["claude-haiku-4-5-20251001"] = new ModelUsageEntry { CostUsd = 0.05 },
                },
            });
            string two = SessionSummary.Format(meter, "t", null, null, models);
            True(two.Split('\n')[2].StartsWith("By model: Haiku 4.5 $0.050 · Haiku 5.5 $0.01"), two);
            True(SessionSummary.Format(meter).Contains("By model: claude-haiku-4-5-20251001 "), "without the list: IDs");
            meter.Reset();
            Eq(0, meter.SessionCostByModel.Count, "reset");
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

        private static void Test_TurnFooter_ProviderNote()
        {
            // As 2.1.329 sends it on the Anthropic API (a probe's result, numbers trimmed)
            var firstParty = ModelUsageEntry.Parse(JObject.Parse(@"{""claude-haiku-5-5"":{""inputTokens"":2,""outputTokens"":4,""costUSD"":0.0018,""provider"":""firstParty"",""costBasis"":""list""}}"));
            Eq("firstParty", firstParty["claude-haiku-5-5"].Provider); Eq("list", firstParty["claude-haiku-5-5"].CostBasis);
            Eq(null, TurnFooter.ProviderNote(firstParty), "the Anthropic API: no note");
            var bedrock = ModelUsageEntry.Parse(JObject.Parse(@"{""claude-sonnet-5-5"":{""costUSD"":0.02,""provider"":""bedrock"",""costBasis"":""list""}}"));
            Eq("Served by Amazon Bedrock, which bills you itself: these costs are Claude Code's estimate at list prices, not your bill.", TurnFooter.ProviderNote(bedrock));
            Eq(null, TurnFooter.ProviderNote(ModelUsageEntry.Parse(JObject.Parse(@"{""claude-haiku-5-5"":{""costUSD"":0.001}}"))), "an older Claude Code doesn't say");
            Eq(null, TurnFooter.ProviderNote(null));
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

            // An alias resolved to another model of the same family (ANTHROPIC_DEFAULT_HAIKU_MODEL → Haiku 4.5): its own cache and price
            prices.Learn(new Dictionary<string, ModelUsageEntry> { ["claude-haiku-4-5-20251001"] = new ModelUsageEntry { Usage = new TokenUsage { Input = 1000, Output = 1000 }, CostUsd = 0.006 } });
            var haikuCache = new CacheWindow();
            haikuCache.Use(new TokenUsage { CacheRead = 100_000 }, t0);
            var older = NextPromptPrice.Estimate(haikuCache, "claude-haiku-5-5", "claude-haiku-4-5-20251001", 0, prices, t0.AddMinutes(1));
            True(older.OtherModel && !older.Cached, "Haiku 4.5 doesn't read Haiku 5.5's cache");
            True(Math.Abs(older.InputUsd - 100_000 * 1.25 / 1e6) < 1e-12, "written at Haiku 4.5's learned $1.25: " + older.InputUsd);
            True(NextPromptPrice.Estimate(haikuCache, "claude-haiku-5-5", "haiku", 0, prices, t0.AddMinutes(1)).Cached, "the alias, unresolved: by family");
            True(NextPromptPrice.Estimate(haikuCache, "claude-haiku-5-5", "claude-haiku-5-5[1m]", 0, prices, t0.AddMinutes(1)).Cached, "the same ID");

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
