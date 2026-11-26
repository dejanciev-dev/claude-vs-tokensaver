using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeVS.Commands;
using System.Collections.Generic;
using ClaudeVS.Core.Chat;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Context;
using ClaudeVS.Core.Tokens;
using ClaudeVS.Diagnostics;
using ClaudeVS.Options;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.ToolWindow
{
    /// <summary>Events from the CLI, read on a background thread and handled on the UI thread, and what they lead to: usage records, the meter, diffs, and the question, plan and edit-review cards.</summary>
    public sealed partial class ClaudeChatControl
    {
        /// <summary>
        /// Reader thread. Every event goes through one FIFO queue that is drained on the UI thread, so text and
        /// tool rows keep their order, and events from a session that was replaced (New, Resume) are dropped.
        /// </summary>
        private void OnEvent(object sender, ClaudeEvent ev)
        {
            if (ev.Kind == ClaudeEventKind.Error || (ev.Kind == ClaudeEventKind.TurnCompleted && ev.IsError))
                ClaudeOutputPane.WriteLine("Error: " + ev.Text);

            // A Write replaces the whole file, so its old content can't be rebuilt afterwards. Copy it now, on the
            // reader thread, before the CLI runs the tool (best effort: a very fast tool may already have run).
            FileChange change = ev.Kind == ClaudeEventKind.ToolUse ? FileChange.FromToolUse(ev.ToolName, ev.ToolInput) : null;
            // A shell command that writes a file with a heredoc is a Write too (Claude does this a lot on Windows).
            if (change == null && ev.Kind == ClaudeEventKind.ToolUse && (ev.ToolName == "Bash" || ev.ToolName == "PowerShell")
                && InlineDiff.FromHeredoc((string)ev.ToolInput?["command"]) is InlineDiff heredoc
                && TurnChanges.FullPath(heredoc.FilePath, _sessionDir) is string written)
                change = new FileChange { FilePath = written, ToolName = ev.ToolName, IsWrite = true };
            // For an Edit the copy only gives the line numbers of the red / green lines.
            string writeSnapshot = change != null ? ReadSnapshot(change.FilePath) : null;

            _events.Enqueue(new QueuedEvent { Session = sender as ClaudeSession, Event = ev, Change = change, WriteSnapshot = writeSnapshot });
            System.Threading.Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
            if (System.Threading.Interlocked.Exchange(ref _drainScheduled, 1) == 0) OnUi(DrainEvents);
        }

        private sealed class QueuedEvent
        {
            public ClaudeSession Session;
            public ClaudeEvent Event;
            public FileChange Change;
            public string WriteSnapshot;
        }

        private readonly System.Collections.Concurrent.ConcurrentQueue<QueuedEvent> _events = new System.Collections.Concurrent.ConcurrentQueue<QueuedEvent>();
        private int _drainScheduled;

        private void DrainEvents()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Reset first: an event enqueued while draining schedules another drain instead of being missed.
            System.Threading.Volatile.Write(ref _drainScheduled, 0);
            while (_events.TryDequeue(out var q))
            {
                if (q.Session == null || q.Session != _session) continue; // from a session that was replaced
                if (q.Event.Kind == ClaudeEventKind.TextDelta || q.Event.Kind == ClaudeEventKind.AssistantText)
                {
                    lock (_pendingText) _pendingText.Append(q.Event.Text);
                    CountStreamedText(q.Event.Text?.Length ?? 0);
                    _flushTimer.Start(); // coalesce rendering of fast deltas
                    continue;
                }
                HandleEvent(q.Event, q.Change, q.WriteSnapshot);
            }
        }

        private void HandleEvent(ClaudeEvent ev, FileChange change, string writeSnapshot)
        {
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _meterModel?.Observe(ev);
                _turnFiles.Observe(ev); // reads and quiet commands, for the usage log
                // A subagent read a file the conversation already had (seen by _turnFiles for this very result)
                if (ev.Kind == ClaudeEventKind.ToolResult && _turnFiles.LastReread is var (rereadId, agentId, rereadPath, rereadTokens) && rereadId == ev.ToolUseId)
                {
                    if (_tasks.AddReread(agentId, rereadTokens)) RefreshAgentsPanel();
                    SubagentRereadTip(_tasks.SubagentTypeOf(agentId), rereadPath, rereadTokens);
                }
                if (ev.Kind == ClaudeEventKind.AssistantUsage && !ev.IsSubagent) Tip(SessionNotices.HaikuLongContext(ev.Model, ev.Usage.ContextTokens));
                switch (ev.Kind)
                {
                    case ClaudeEventKind.SessionStarted:
                        ClaudeOutputPane.WriteLine($"Session {ev.SessionId} started, model {ev.Model}");
                        OnTurnStarted(); // init comes at the start of every turn (2.1.318)
                        CheckCliVersion(ev.CliVersion);
                        OnCliCommands(ev);
                        OnSessionFacts(ev);
                        string named = _sessionName;
                        if (named != null && ev.SessionId != null && SessionMeta.Get(ev.SessionId)?.Name != named)
                            SaveSessionMeta(() => SessionMeta.SetName(ev.SessionId, named)); // named before the session had an id
                        break;
                    case ClaudeEventKind.AssistantUsage:
                        if (!ev.IsSubagent && !string.IsNullOrEmpty(ev.Model)) _turnModel = ev.Model;
                        else if (ev.IsSubagent) OnSubagentUsage(ev);
                        break;
                    case ClaudeEventKind.ToolUse:
                        FlushText();
                        if (_turnAnswer.Length > 0) _turnAnswer.Append("\n\n"); // text before and after a tool call are separate paragraphs
                        Action diff = null;
                        string changedPath = change != null ? TurnChanges.FullPath(change.FilePath, _sessionDir) : null;
                        if (change != null)
                        {
                            // Kept until the result, which may bring the CLI's own copy of the file (originalFile).
                            var call = new FileCall { ToolName = ev.ToolName, Input = ev.ToolInput, Path = changedPath, Snapshot = writeSnapshot };
                            call.FirstInTurn = changedPath != null && _turnChanges.Record(changedPath, writeSnapshot); // for "Files changed" under the answer
                            if (changedPath != null) _claudeWrote.Add(changedPath); // Claude knows what it wrote: no "changed since" notice
                            if (!string.IsNullOrEmpty(ev.ToolUseId)) _fileCalls[ev.ToolUseId] = call;
                            diff = () => { ThreadHelper.ThrowIfNotOnUIThread(); ShowDiff(change, call.Snapshot); };
                        }
                        // Red / green lines under the row. A Write whose old content could not be read gets none (the Diff button still works).
                        var inline = change?.IsWrite == true && writeSnapshot == null ? null
                            : InlineDiff.FromToolUse(ev.ToolName, ev.ToolInput, writeSnapshot == "" ? null : writeSnapshot);
                        _view.AppendTool(ev.ToolUseId, ev.Text, ev.ToolInput?.ToString(), diff, inline, ev.ToolName, ev.ToolInput, _sessionDir, changedPath);
                        break;
                    case ClaudeEventKind.ToolResult:
                        _view.CompleteTool(ev.ToolUseId, ev.ResultChars, ev.IsError, ev.ResultText, ConfirmFileChange(ev), ev.AllowedBy, ev.ResultData, ev.DeniedBy);
                        if (!ev.IsError && !ev.IsSubagent) LargeReadTip(ev); // a subagent's reads stay out of this context
                        CheckAlwaysAllowSaved(ev);
                        break;
                    case ClaudeEventKind.PermissionRequest:
                        FlushText();
                        ClearSuggestion();
                        _waitingForUser = true; // Claude waits for you; not a stall
                        SetStalled(false);
                        if (!ShowInteractiveCard(ev)) ShowPermission(ev);
                        NoticeIfOutOfView(ev); // a chat out of view says so in VS's main window (Phase 93)
                        break;
                    case ClaudeEventKind.Task:
                        OnTaskEvent(ev);
                        break;
                    case ClaudeEventKind.RateLimit:
                        _rateLimits = RateLimitInfo.Parse(ev.Payload);
                        UpdateLimitBanner();
                        break;
                    case ClaudeEventKind.WritesLost: // 2.1.332: part of the session couldn't be saved (said once per conversation)
                        FlushText();
                        Notice(SessionNotices.WritesLost(ev.Payload), important: true);
                        break;
                    case ClaudeEventKind.Compacted:
                        FlushText();
                        LoadCompactSummary(_view.AppendCompaction(ev.PreCompactTokens, null, null));
                        break;
                    case ClaudeEventKind.TurnCompleted:
                        FlushText();
                        SetBusy(_pendingUsages.Count > 1); // still busy if more prompts were queued
                        if (ev.IsError && !string.IsNullOrEmpty(ev.Text)) AppendError("[error] ", ev.Text);
                        if (ev.TerminalReason != null && ev.TerminalReason != "completed") ClaudeOutputPane.WriteLine("Turn ended: " + ev.TerminalReason);
                        OnSignInExpiry(ev);
                        // The model as Claude Code names it ("Haiku 5.5"); its ID goes in the tooltip
                        string modelName = _turnModel == null ? null : _session?.Models?.DisplayName(_turnModel);
                        string used = _turnModel != null ? " · " + (modelName ?? _turnModel) : "";
                        // The effort this turn was sent with: its record is next in line (RecordUsage takes it below)
                        if (TurnFooter.Effort(_pendingUsages.Count > 0 ? EffortUsed(_pendingUsages.Peek()) : null) is string effortSent) used += " · " + effortSent; // as Claude Code applied it
                        if (TurnFooter.Agents(ev.Subagents, _meterModel?.LastTurnSubagentTotal.Total ?? 0) is string agents) used += " · " + agents;
                        used += TurnFooter.Retries(_meterModel?.LastTurnOutputRetries ?? 0, _meterModel?.LastTurnOutputRetryCostUsd ?? 0); // answers cut off at the output limit
                        if (ev.Subagents?.Warning() is string agentTrouble)
                        {
                            FlushText();
                            Append("\n  ⚠ " + agentTrouble + "\n");
                            ClaudeOutputPane.WriteLine(agentTrouble);
                        }
                        CheckModelMismatch(_turnModel);
                        RecordUsage(ev, _turnModel);
                        _meterModel?.AddTurnEffort(_lastTurnEffort); // for Copy session summary
                        SaveModelUsage(ev.SessionId, ev.ModelUsage);
                        if (TurnFooter.ServedElsewhere(ev.ModelUsageSinceLastResult) != null) _servedElsewhere = true; // before CutOffTip
                        CheckCost();
                        CacheBreakTip();
                        if (_meterModel != null && SessionNotices.FailedTurnCost(ev.IsError, UsageRecord.EndOf(ev.IsError, ev.TerminalReason, ev.ResultSubtype), _meterModel.LastTurnCostUsd) is string failedCost)
                            Notice(failedCost);
                        if (_meterModel != null) Tip(SessionNotices.SubagentsDominate(_meterModel.LastTurnSubagentCostUsd, _meterModel.LastTurnCostUsd));
                        if (!LongConversationTip()) PriceyContextTip(_turnModel); // both say "the conversation is big": one per turn
                        CutOffTip(); // answers cut off at the output limit that cost enough to go on
                        OverkillModelTip(_turnModel);
                        HeavyThinkingTip(_turnModel);
                        UnsupportedEffortTip(_turnModel);
                        LongAnswerTip(_turnModel);
                        _tooLongAdvice = ev.IsError && ClaudeErrorHints.IsPromptTooLong(ev.Text); // the breakdown below names the large part (as VS Code 2.1.331)
                        RefreshContextUsage(show: false); // control request, no model call
                        _turnModel = null;
                        var turn = _turnPrompts.Count > 0 ? _turnPrompts.Dequeue() : new TurnPrompt();
                        string answer = _turnAnswer.ToString();
                        _turnAnswer.Clear();
                        // A turn far dearer than your usual one (the median logged turn, read once per VS session)
                        string usual = PastConversations.TurnNote(_meterModel?.LastTurnCostUsd ?? 0) is string u ? " · " + u : "";
                        _view.AppendTurnFooter($"✓ {ev.DurationMs / 1000.0:0.0}s · {TokenMeter.Format(ev.Usage.Total)} tok · {TurnFooter.Cost(_meterModel?.LastTurnCostUsd ?? 0, _meterModel?.LastTurnSubagentCostUsd ?? 0)}{usual}{used}{TurnFooter.Extras(ev.Usage, _meterModel?.LastTurnCacheSavedUsd ?? 0)}",
                            string.IsNullOrWhiteSpace(answer) ? null : BookmarkToggle(turn.Prompt, answer, turn.Anchor), answer,
                            tooltip: JoinLines(_meterModel == null ? null : TurnFooter.CostBreakdown(_meterModel.LastModel, _meterModel.LastTurn, _meterModel.LastTurnSubagentCostUsd, _meterModel.LastTurnCostUsd, _meterModel.Prices),
                                TurnFooter.ProviderNote(ev.ModelUsageSinceLastResult), _lastTurnReferences, modelName != null ? "Model: " + _turnModel : null));
                        _turnFiles.Reset(); // the next turn's reads
                        if (!string.IsNullOrWhiteSpace(answer)) _lastAnswer = answer;
                        SuggestFromAnswer(answer, ev, turn.Prompt); // grey text in the input box (after a failed turn: what to do); not while queued prompts run
                        CheckInstructionFiles(); // a CLAUDE.md or .mcp.json edited by hand while Claude worked
                        RestartAfterTurn(); // Restart Claude Code asked for during the turn
                        ShowTurnChanges(); // "Files changed" with Diff links
                        Announce(ev.IsError ? "Claude stopped with an error. " + Short(ev.Text) : ScreenReaderText.AnswerFinished(answer));
                        if (_queuedBubbles.Count > 0) { _queuedBubbles.Dequeue().Render(); RefreshQueuedPanel(); } // Claude starts on the next one now
                        break;
                    case ClaudeEventKind.Error:
                        AppendError("[claude] ", ev.Text);
                        break;
                    case ClaudeEventKind.PromptSuggestion:
                        SuggestFromCli(ev.Text); // only with "Ask Claude Code for suggestions" on
                        break;
                    case ClaudeEventKind.OutputLimitRetry:
                        // An answer cut off at the output limit: Claude Code asks for the rest (the meter prices it)
                        ClaudeOutputPane.WriteLine((ev.IsSubagent ? "A subagent's" : "Claude's") + " answer hit the output token limit; Claude Code asked it to go on");
                        if (_tasks.ObserveRetry(ev)) RefreshAgentsPanel();
                        break;
                }
                OnActivityEvent(ev);
                TrackTips(ev);
            }
        }

        /// <summary>
        /// The CLI can accept a model (its init event reports it) and still answer with another one, for example
        /// in plan mode. Routing to a cheaper model then silently costs more, so say so once per conversation.
        /// </summary>
        // The CLI versions already logged (and warned about, when old) in this Visual Studio session.
        private static readonly HashSet<string> _seenCliVersions = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Logs the CLI version once, and when it is older than the version this extension was verified with, says
        /// so once in the chat: many features rely on 2.1.316–2.1.318 control requests and messages.
        /// </summary>
        private void CheckCliVersion(string version)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(version) || !_seenCliVersions.Add(version)) return;
            ClaudeOutputPane.WriteLine($"Claude Code {version} (needs {CliVersion.Required}, tested with {CliVersion.Tested})");
            string notice = CliVersion.Notice(version);
            if (notice == null) return;
            Append("\n  ⚠ " + notice + "\n");
            Announce(notice);
        }

        private void CheckModelMismatch(string modelUsed)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string requested = _requestedFamilies.Count > 0 ? _requestedFamilies.Dequeue() : null;
            string used = PriceTable.Family(modelUsed);
            if (requested == null || used == null || string.Equals(requested, used, StringComparison.OrdinalIgnoreCase)) return;
            ClaudeOutputPane.WriteLine($"Model mismatch: requested {requested}, answered by {modelUsed}");
            if (_warnedModelMismatch) return;
            _warnedModelMismatch = true;
            Append($"\n  ⚠ Asked for {requested}, but {modelUsed} answered. The CLI changed the model (plan mode or your Claude Code settings can do this), so this turn cost {used} prices.\n");
        }

        /// <summary>Completes the record started in SendText and appends it to the local usage log (off the UI thread).</summary>
        private void RecordUsage(ClaudeEvent ev, string modelUsed)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Prompts sent while Claude is still answering are queued by the CLI and finish in order.
            _lastTurnEffort = null;
            _lastTurnPromptChars = 0;
            var side = _meterModel?.LastTurnSide ?? default;
            if (!side.IsEmpty)
                ClaudeOutputPane.WriteLine($"Claude Code's own request{(_meterModel.LastTurnSideIsCompact ? " (/compact summary)" : " (suggested prompt)")} billed with this turn: "
                    + $"{TokenMeter.Format(side.Total)} tok, ${_meterModel.LastTurnSideCostUsd:0.0000}");
            CountSuggestionCost(); // and the tip when Claude Code's suggestions don't pay
            _lastTurnReferences = null;
            if (_pendingUsages.Count == 0) return;
            var rec = _pendingUsages.Dequeue();
            foreach (var p in rec.RefPending ?? new List<string>())
            {
                // measured since the prompt went out; taken out of the cache, so a later attach measures it afresh
                if (_referenceSizes.TryGetValue(p, out var late) && late != null) { rec.RefFiles += late.Files; rec.RefCeilingTokens += late.Tokens; }
                if (late != null && !_attachedFiles.Contains(p, StringComparer.OrdinalIgnoreCase)) _referenceSizes.Remove(p);
            }
            if (rec.RefPaths != null && rec.RefPaths.Count > 0)
            {
                var (readFiles, readTokens) = _turnFiles.ReadUnder(rec.RefPaths);
                rec.RefReadFiles = readFiles;
                rec.RefReadTokens = readTokens;
                _lastTurnReferences = TurnFooter.References(readFiles, readTokens, rec.RefFiles, rec.RefCeilingTokens);
            }
            rec.SubagentRereadTokens = _turnFiles.SubagentRereadTokens;
            rec.RepeatReadTokens = _reads.RepeatedTokens - _repeatReadBase; // this turn's share of the conversation's
            _repeatReadBase = _reads.RepeatedTokens;
            if (_turnFiles.ReadTokensByFile.Count > 0)
            {
                var folders = TurnFileActivity.ByFolder(_turnFiles.ReadTokensByFile, WorkFolder);
                NoiseReadTip(folders, _turnFiles.ReadTokensByFile); // all of them: small build-output folders add up
                rec.ReadFolders = TurnFileActivity.Capped(folders); // the log keeps the 10 largest
            }
            if (_turnFiles.LoudRuns.Count > 0)
            {
                rec.LoudRuns = new System.Collections.Generic.Dictionary<string, int>(_turnFiles.LoudRuns, StringComparer.Ordinal);
                rec.LoudTokens = _turnFiles.LoudTokens;
            }
            if (_turnFiles.QuietRuns.Count > 0)
            {
                rec.QuietRuns = new System.Collections.Generic.Dictionary<string, int>(_turnFiles.QuietRuns, StringComparer.Ordinal);
                rec.QuietTokens = _turnFiles.QuietTokens;
            }
            rec.Effort = EffortUsed(rec); // as Claude Code applied it (read after the first prompt of a process went out)
            _lastTurnEffort = rec.Effort; // for the tips and the footer, kept also without the log
            _lastTurnPromptChars = rec.PromptChars;
            _conversationReads.Add(rec); // for Copy session summary, kept also without the log
            if (Options?.KeepUsageLog == false) return;
            rec.Timestamp = DateTime.UtcNow;
            rec.SessionId = ev.SessionId;
            rec.ModelUsed = modelUsed;
            rec.InputTokens = ev.Usage.Input;
            rec.OutputTokens = ev.Usage.Output;
            rec.ThinkingTokens = ev.Usage.Thinking;
            rec.CacheReadTokens = ev.Usage.CacheRead;
            rec.CacheWriteTokens = ev.Usage.CacheCreation;
            rec.CacheWrite1hTokens = ev.Usage.CacheCreation1h;
            rec.FastTokens = ev.Usage.FastTokens;
            rec.LargeContextTokens = _meterModel?.LastTurn.LargeContextTokens ?? 0; // per request: the meter counted it
            rec.Agents = AgentTokens.From(_meterModel?.LastTurnSubagents); // not in the counts above, which are the main agent's
            rec.Prices = PriceTable.LearnedPrices(new[] { modelUsed }.Concat(rec.Agents?.Select(a => a.Model) ?? Enumerable.Empty<string>())); // older models, for later reports
            rec.AgentTypes = _meterModel?.LastTurnAgentTypes;
            rec.ToolResultTokens = _meterModel?.LastTurnResultTokensByTool;
            rec.CostUsd = _meterModel?.LastTurnCostUsd ?? 0;
            rec.DurationMs = ev.DurationMs;
            rec.IsError = ev.IsError;
            rec.EndReason = UsageRecord.EndOf(ev.IsError, ev.TerminalReason, ev.ResultSubtype);
            rec.OutputRetries = _meterModel?.LastTurnOutputRetries ?? 0;
            rec.OutputRetryCostUsd = _meterModel?.LastTurnOutputRetryCostUsd ?? 0;
            if (!side.IsEmpty)
            {
                rec.SideKind = _meterModel.LastTurnSideIsCompact ? UsageRecord.SideCompact : UsageRecord.SideSuggestion;
                rec.SideTokens = side.Total;
                rec.SideCostUsd = _meterModel.LastTurnSideCostUsd;
            }
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { Usage.KeepMonths = Options?.KeepUsageMonths ?? 3; Usage.Append(rec); }
                catch (Exception ex) { ClaudeOutputPane.WriteLine("Could not write usage log: " + ex.Message); }
            });
        }

        private static void LogUsageSummary(string dir)
        {
            if (Options?.KeepUsageLog == false) return;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { ClaudeOutputPane.WriteLine($"Usage for this solution: {UsageSummary.From(Usage.ReadAll(), dir)} (log: {Usage.Path})"); }
                catch (Exception ex) { ClaudeOutputPane.WriteLine("Could not read usage log: " + ex.Message); }
            });
        }

        private const int MaxSnapshotBytes = 2 * 1024 * 1024;

        /// <summary>Current file content for a Write snapshot: "" for a new file, null if unreadable or huge.</summary>
        private static string ReadSnapshot(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return "";
                return new System.IO.FileInfo(path).Length <= MaxSnapshotBytes ? System.IO.File.ReadAllText(path) : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Diff of one Claude file change. Edit/MultiEdit: the "before" is rebuilt by undoing the replacements on the
        /// current file; if that fails (file changed again, edit not applied yet), only the changed text is compared.
        /// Write: compares against the copy taken when the tool call arrived.
        /// </summary>
        private void ShowDiff(FileChange change, string writeSnapshot)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string name = System.IO.Path.GetFileName(change.FilePath);
            try
            {
                bool exists = System.IO.File.Exists(change.FilePath);
                if (change.IsWrite)
                {
                    if (writeSnapshot == null || !exists) { Append($"\n  (no diff for {name}: the earlier content was not captured)\n"); return; }
                    VsContext.OpenDiff(writeSnapshot, "Before Claude's write", change.FilePath, null, "Current", $"{name}: Claude's write");
                    return;
                }

                string current = exists ? System.IO.File.ReadAllText(change.FilePath) : null;
                string before = change.ReconstructBefore(current);
                if (before != null)
                {
                    VsContext.OpenDiff(before, "Before Claude's edit", change.FilePath, null, "Current", $"{name}: Claude's edit");
                    return;
                }
                string oldText = string.Join("\n…\n", change.Edits.Select(x => x.OldText));
                string newText = string.Join("\n…\n", change.Edits.Select(x => x.NewText));
                VsContext.OpenDiff(oldText, "Replaced text", change.FilePath, newText, "New text", $"{name}: changed text only");
            }
            catch (Exception ex)
            {
                AppendError("[diff] ", ex.Message);
            }
        }

        private void OnMeterChanged(object sender, EventArgs e)
        {
            var m = _meterModel;
            if (m == null) return;
            _ctxBar.Value = Math.Min(100, m.ContextPercent);
            long window = m.ContextWindow > 0 ? m.ContextWindow : new TokenMeterSettings().DefaultContextWindow;
            RenderMeter(m, window);

            var advice = m.GetAdvice().OrderByDescending(a => a.Level).FirstOrDefault();
            _advice.Text = advice == null ? "" : (advice.Level == AdviceLevel.Critical ? "⚠ " : "💡 ") + advice.Message;
            if (advice?.Level == AdviceLevel.Critical) _advice.Foreground = Brushes.OrangeRed;
            else _advice.ClearValue(TextBlock.ForegroundProperty);
            _adviceCommand = advice?.SuggestedCommand;
            _adviceAction.Content = _adviceCommand;
            _adviceAction.Visibility = _adviceCommand == null ? Visibility.Collapsed : Visibility.Visible;
            UpdateMeterTooltip();
        }

        /// <summary>The meter's tooltip: what fills the context (once measured), what the prompt cache saved, then today's and this month's spend.</summary>
        private void UpdateMeterTooltip()
        {
            string tip = _contextUsage != null ? ContextTooltip(_contextUsage) : "Click for what fills the context window";
            if (_meterModel?.CacheSavedLine() is string cache) tip += "\n\n" + cache;
            if (SuggestionsCostLine() is string suggestions) tip += "\n" + suggestions;
            DailyCost.StepUsd = Options?.DailyCostWarningUsd ?? 20;
            MonthlyCost.StepUsd = Options?.MonthlyCostWarningUsd ?? 200;
            WeeklyCost.StepUsd = Options?.WeeklyCostWarningUsd ?? 0;
            LoadSpend(); // the month's earlier turns, once; the line shows them from the next refresh
            if (Core.Tokens.SpendWatch.BudgetLine(DailyCost, MonthlyCost, DateTime.UtcNow, WeeklyCost) is string spend) tip += "\n\n" + spend;
            if (RecentSpend.LastDaysLine(DateTime.UtcNow) is string days) tip += "\n" + days;
            if (_meterModel != null && PastConversations.Line(_meterModel.SessionCostUsd, _session?.SessionId ?? _resumeId) is string against) tip += "\n" + against;
            _meterArea.ToolTip = tip;
        }

        /// <summary>
        /// AskUserQuestion and ExitPlanMode need an answer, not just allow/deny, so they get a card in the
        /// transcript instead of the permission bar. Returns false for other tools.
        /// </summary>
        private bool ShowInteractiveCard(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var session = _session;
            void Respond(bool allow, Newtonsoft.Json.Linq.JObject input, string denyMessage, string note)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (session != _session || session == null) { Append("\n  (that conversation has ended; the answer was not sent)\n"); return; }
                ClearWaitingNotice();
                _waitingForUser = false;
                RenderActivity();
                System.Threading.Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
                Append($"  {note}\n");
                Fire(() => session.RespondToPermissionAsync(ev.RequestId, input ?? ev.ToolInput ?? new JObject(), allow, denyMessage));
            }

            if (ev.ToolName == AskUserQuestionTool.Name)
            {
                var questions = AskUserQuestionTool.Parse(ev.ToolInput);
                if (questions.Count == 0) return false;
                Announce("Claude has a question: " + Short(questions[0].Text) + (questions.Count > 1 ? $" And {questions.Count - 1} more." : ""), assertive: true);
                _view.AppendQuestionCard(questions, chosen =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    var summary = string.Join("; ", chosen.Values.Where(v => v.Count > 0).Select(v => string.Join(", ", v)));
                    Respond(true, AskUserQuestionTool.BuildAnswer(ev.ToolInput, chosen), null, "✔ answered: " + (summary.Length > 0 ? summary : "(no choice)"));
                });
                return true;
            }

            if (ev.ToolName == ExitPlanModeTool.Name)
            {
                string plan = ExitPlanModeTool.Plan(ev.ToolInput);
                string docPath = null; // the plan as a document, once opened
                Announce("Claude's plan is ready for your approval. " + Short(plan), assertive: true);
                _view.AppendPlanCard(plan,
                    () => { ThreadHelper.ThrowIfNotOnUIThread(); Respond(true, null, null, "✔ plan approved — Claude starts the work"); },
                    feedback =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        // Comments typed into the plan document (added or deleted lines) go along with the feedback box.
                        string comments = null;
                        if (docPath != null)
                        {
                            VsContext.SaveIfOpen(docPath);
                            try { comments = PlanComments.ToFeedback(PlanComments.Extract(plan, System.IO.File.ReadAllText(docPath))); }
                            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { AppendError("[plan] ", ex.Message); }
                        }
                        string all = string.Join("\n\n", new[] { feedback?.Trim(), comments }.Where(x => !string.IsNullOrEmpty(x)));
                        Respond(false, null, ExitPlanModeTool.RevisionMessage(all), comments != null ? "✎ changes requested, with your comments from the plan document" : "✎ changes requested");
                    },
                    () =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        try
                        {
                            if (docPath == null)
                            {
                                docPath = PlanDocumentPath(plan);
                                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(docPath));
                                System.IO.File.WriteAllText(docPath, plan, new UTF8Encoding(false));
                            }
                            VsContext.OpenFile(docPath);
                        }
                        catch (Exception ex) { AppendError("[plan] ", Core.SafeFile.Explain(ex, docPath)); }
                    });
                return true;
            }

            if (EditReview.Supports(ev.ToolName) && session?.Options.EditNotesHook == true)
            {
                string path = (string)ev.ToolInput?["file_path"];
                string current = ReadForReview(path);
                if (current == ReviewUnreadable) return false;
                var review = EditReview.Create(ev.ToolName, ev.ToolInput, current);
                if (review == null || review.Hunks.Count < 2) return false; // one change: the usual Allow / Deny bar
                string name = System.IO.Path.GetFileName(path ?? "");
                Announce($"Claude wants to {(ev.ToolName == "Write" ? "write" : "edit")} {name}: {review.Hunks.Count} changes to review in the conversation.", assertive: true);
                _view.AppendEditReviewCard(name, review,
                    onApply: () =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        if (review.AllAccepted) { Respond(true, null, null, "✔ applied all changes"); return; }
                        // If Stop ends the call while it runs, no hook comes; the note then goes with the next prompt,
                        // but only when the file holds exactly what this review approved.
                        string expected = EditReview.ProposedFile(ev.ToolName, review.BuildInput(), current);
                        session.SetToolResultNote(ev.ToolUseId, review.NoteForClaude(), name,
                            () => expected != null && System.IO.File.Exists(path) && System.IO.File.ReadAllText(path) == expected);
                        Respond(true, review.BuildInput(), null, $"✔ applied {review.Hunks.Count(h => h.Accepted)} of {review.Hunks.Count} changes; Claude is told which you rejected");
                    },
                    onRejectAll: () => { ThreadHelper.ThrowIfNotOnUIThread(); Respond(false, null, "The user rejected all changes in this edit.", "✖ rejected"); },
                    onOpenDiff: () =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        string proposed = EditReview.ProposedFile(ev.ToolName, ev.ToolInput, current);
                        if (proposed == null) { Append("\n  (the file changed since Claude read it, so the whole-file diff is not available)\n"); return; }
                        try { VsContext.OpenDiff(current ?? "", "Current", path, proposed, "Proposed by Claude", "Claude: " + name); }
                        catch (Exception ex) { AppendError("[diff] ", ex.Message); }
                    });
                return true;
            }
            return false;
        }

        private const string ReviewUnreadable = "\0unreadable";

        /// <summary>The file as it is now for a review: null when it does not exist yet; <see cref="ReviewUnreadable"/> when it can't be read or is huge.</summary>
        private static string ReadForReview(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return ReviewUnreadable;
                if (!System.IO.File.Exists(path)) return null;
                return new System.IO.FileInfo(path).Length <= MaxSnapshotBytes ? System.IO.File.ReadAllText(path) : ReviewUnreadable;
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) { return ReviewUnreadable; }
        }

        /// <summary>%LocalAppData%\claude-vs-tokensaver\plans\&lt;plan title&gt;-&lt;time&gt;.md (the plan's first heading names it).</summary>
        private static string PlanDocumentPath(string plan)
        {
            string heading = (plan ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("#"))?.TrimStart('#').Trim();
            string name = ConversationExport.FileName(string.IsNullOrEmpty(heading) ? "Plan" : heading);
            name = System.IO.Path.GetFileNameWithoutExtension(name) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md";
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-vs-tokensaver", "plans", name);
        }
    }
}
