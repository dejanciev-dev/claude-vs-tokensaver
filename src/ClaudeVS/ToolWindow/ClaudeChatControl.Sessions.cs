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
    /// <summary>The Claude Code session: starting it, its commands, the sessions picker, resuming, drawing a resumed transcript, New.</summary>
    public sealed partial class ClaudeChatControl
    {
        private void EnsureSession(string dir)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_session != null && string.Equals(_sessionDir, dir, StringComparison.OrdinalIgnoreCase)) return;
            if (_session != null) { Append("\n— Solution changed, starting a new conversation —\n"); DisposeSession(); ClearWaitingNotice(); }

            var o = Options;
            _session = new ClaudeSession(new ClaudeLaunchOptions
            {
                ClaudePath = o?.ClaudePath ?? "",
                WorkingDirectory = dir,
                PermissionMode = _mode,
                AppendSystemPrompt = (o?.ConciseAnswers ?? true) ? ClaudeOptionsPage.BrevityPrompt : null,
                ExtraArguments = o?.ExtraArguments ?? "",
                ResumeSessionId = _resumeId,
                ForkSession = _forkNext && _resumeId != null,
                EnableFileCheckpointing = o?.FileCheckpoints ?? true,
                NoForks = o?.NoForks ?? false,
                PromptSuggestions = (o?.SuggestNextPrompt ?? true) && (o?.ClaudeCodeSuggestions ?? false),
                SessionName = _sessionName,
                EditNotesHook = true, // tells Claude which changes of an edit were rejected (review card)
            });
            if (o?.ErrorListTool ?? true)
                _session.Options.SdkMcpServers.Add(IdeMcpServer.Create(ReadErrorListAsync, () => dir));
            HaikuRouterIfOn(); // with Route with Haiku on: start its process now, so it is warm for the first prompt
            if (_resumeId == null && _meterModel?.Turns == 0) _meterModel.FreshSession(); // its modelUsage starts at 0: subagents exact from the first turn
            else if (_resumeId != null && _meterModel?.Turns == 0) _meterModel.ResumedSession(ReadModelUsage(_resumeId)); // the CLI carries it on from there
            _resumeId = null;
            _forkNext = false;
            _sessionDir = dir;
            UpdateFolderChip();
            if (!_thinking) { var fresh = _session; Fire(() => fresh.SetThinkingAsync(false)); } // applied when the process starts
            _session.EventReceived += OnEvent;
            if (o?.LogRawCliTraffic == true) _session.RawLine += OnRawLine;
            _session.Exited += (s, code) =>
            {
                ClaudeOutputPane.WriteLine($"claude process exited with code {code}");
                OnUi(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (s != _session) return; // an old session we stopped on purpose (New, Resume, solution change)
                    _pendingUsages.Clear();    // its unfinished turns will never complete
                    _requestedFamilies.Clear();
                    ClearTurns();
                    SetBusy(false);
                    FlushText();
                    if (code != 0 && code != 1) Append($"\n[claude exited with code {code}]\n");
                    DropOpenRequests(); // a permission or a card left open can't be answered any more (Phase 93)
                });
            };
            ClaudeOutputPane.WriteLine($"Starting claude in {dir}");
            LogUsageSummary(dir);
        }

        /// <summary>Custom slash commands for the current solution, re-read when the solution changes.</summary>
        private void RefreshCommands(bool force = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dir = VsContext.GetSolutionDirectory() == null ? null : WorkFolder; // the folder Claude Code runs in
            if (!force && _commandsDir != null && string.Equals(dir, _commandsDir, StringComparison.OrdinalIgnoreCase)) return;
            _commandsDir = dir ?? "";
            _fileCommands = SlashCommands.Discover(dir, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            _commands = SlashCommands.WithSkills(_fileCommands, _cliSkills, _cliSlashCommands);
        }

        // Your .claude/commands, and the skills Claude Code reported in its last init (2.1.319).
        private List<SlashCommand> _fileCommands = new List<SlashCommand>(SlashCommands.BuiltIn);
        private IReadOnlyList<string> _cliSkills, _cliSlashCommands;

        /// <summary>From each init: the session's skills join the / menu and autocomplete.</summary>
        private void OnCliCommands(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (ev.Skills == null || ev.SlashCommands == null) return;
            if (_cliSkills != null && _cliSkills.SequenceEqual(ev.Skills) && _cliSlashCommands.SequenceEqual(ev.SlashCommands)) return;
            _cliSkills = ev.Skills;
            _cliSlashCommands = ev.SlashCommands;
            _commands = SlashCommands.WithSkills(_fileCommands, _cliSkills, _cliSlashCommands);
        }

        /// <summary>Session picker like VS Code's: search box, titles, compact ages ("5m", "2d").</summary>
        /// <summary>
        /// Session picker like VS Code's: search box, titles, compact ages ("5m", "2d"). Hovering a row shows Rename and
        /// Archive; archived sessions are hidden until "Show archived" is chosen.
        /// </summary>
        private void ShowHistoryMenu()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ShowHistoryMenu(refresh: false);
        }

        private enum SessionFilter { All, Open, Closed }
        private SessionFilter _sessionFilter;
        private bool _sessionsByCost;

        private void ShowHistoryMenu(bool refresh)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dir = WorkFolder;
            var now = DateTime.UtcNow;
            List<SessionInfo> all;
            try { all = SessionMeta.Apply(SessionHistory.List(SessionHistory.DefaultProjectsRoot, dir, max: 80), Options?.AutoArchiveDays ?? 14, now); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { AppendError("[sessions] ", Core.SafeFile.Explain(ex, SessionMeta.Path)); return; }
            string currentId = _session?.SessionId ?? _resumeId;
            // Sessions open in another process right now (a terminal, VS Code, another chat window here…), from the CLI's registry.
            int? ownPid = _session?.ProcessId;
            HashSet<int> ourPids;
            lock (Instances) ourPids = new HashSet<int>(Instances.Select(i => i._session?.ProcessId).Where(p => p.HasValue).Select(p => p.Value));
            var open = RunningSessions.Read().Where(r => r.Pid != ownPid).GroupBy(r => r.SessionId).ToDictionary(g => g.Key, g => g.First());

            var visible = all.Where(s => _showArchived || !s.Archived)
                .Where(s => _sessionFilter == SessionFilter.All || (_sessionFilter == SessionFilter.Open) == (open.ContainsKey(s.Id) || s.Id == currentId))
                .ToList();
            // Costliest first: sessions not read yet go last until they are (the list refreshes then).
            if (_sessionsByCost) visible = visible.OrderByDescending(s => SessionCost.Cached(s)?.CostUsd ?? -1).ToList();
            // Your groups in your order (Move up / Move down in a session's group picker)
            var groupRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var g in SessionMeta.Groups()) groupRank[g] = groupRank.Count; }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { }
            var entries = visible.Select(s =>
            {
                open.TryGetValue(s.Id, out var elsewhere);
                return new ChatPopup.Entry
                {
                    // Ungrouped first (no header), then your groups, archived last.
                    Group = s.Archived ? "Archived" : s.Group,
                    Glyph = elsewhere != null ? ChatTheme.IconOpenRing : null,
                    Label = s.Title,
                    Description = elsewhere != null ? $"open in {(ourPids.Contains(elsewhere.Pid) ? "another chat window here" : elsewhere.Where)} · {elsewhere.StatusText}" : s.AutoArchived ? $"archived after {Options?.AutoArchiveDays ?? 14} days without use" : null,
                    Detail = SessionCost.Cached(s.FilePath, s.LastWriteUtc, s.SizeBytes)?.Short() is string cost ? cost + " · " + SessionHistory.TimeAgo(s.LastWriteUtc, now) : SessionHistory.TimeAgo(s.LastWriteUtc, now),
                    Checked = s.Id == currentId,
                    Run = () => ResumeSession(s),
                    Rename = name =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        if (s.Id == currentId) RenameCurrentSession(name);
                        else SaveSessionMeta(() => SessionMeta.SetName(s.Id, SessionMetaStore.Clean(name)));
                        ShowHistoryMenu(refresh: true);
                    },
                    Actions = new List<ChatPopup.EntryAction>
                    {
                        new ChatPopup.EntryAction
                        {
                            Glyph = ChatTheme.IconFork,
                            Tooltip = "Fork: continue in a new session with this history",
                            Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); _sessions.Close(); ResumeSession(s, fork: true); },
                        },
                        new ChatPopup.EntryAction
                        {
                            Glyph = ChatTheme.IconFolder,
                            Tooltip = "Move to group…",
                            Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); _sessions.Close(); ShowGroupPicker(s); },
                        },
                        new ChatPopup.EntryAction
                        {
                            Glyph = s.Archived ? ChatTheme.IconUnarchive : ChatTheme.IconArchive,
                            Tooltip = s.Archived ? "Unarchive" : "Archive (hide from this list)",
                            Run = () =>
                            {
                                ThreadHelper.ThrowIfNotOnUIThread();
                                SaveSessionMeta(() => SessionMeta.SetArchived(s.Id, !s.Archived));
                                ShowHistoryMenu(refresh: true);
                            },
                        },
                    },
                };
            })
            .OrderBy(e => e.Group == "Archived" ? 2 : e.Group == null ? 0 : 1)
            .ThenBy(e => e.Group != null && groupRank.TryGetValue(e.Group, out int rank) ? rank : int.MaxValue)
            .ThenBy(e => e.Group ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList(); // OrderBy is stable: newest first within each group

            if (entries.Count == 0)
                entries.Add(new ChatPopup.Entry { Label = all.Count == 0 ? "No earlier sessions for this folder" : _sessionFilter != SessionFilter.All ? "No sessions match the filter" : "All sessions are archived", Enabled = false });

            // Commands at the end, under a blank header (a separator).
            const string More = " ";
            entries.Add(new ChatPopup.Entry
            {
                Group = More,
                Glyph = ChatTheme.IconFilter,
                Label = _sessionFilter == SessionFilter.All ? "Showing all sessions" : _sessionFilter == SessionFilter.Open ? "Showing open sessions" : "Showing closed sessions",
                Description = "Click to switch: all, open (here or elsewhere), closed",
                Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); _sessionFilter = (SessionFilter)(((int)_sessionFilter + 1) % 3); ShowHistoryMenu(refresh: true); },
            });
            entries.Add(new ChatPopup.Entry
            {
                Group = More,
                Glyph = ChatTheme.IconSort,
                Label = _sessionsByCost ? "Costliest first" : "Newest first",
                Description = "Click to switch: newest first, costliest first",
                Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); _sessionsByCost = !_sessionsByCost; ShowHistoryMenu(refresh: true); },
            });
            int archived = all.Count(s => s.Archived);
            if (archived > 0)
                entries.Add(new ChatPopup.Entry
                {
                    Group = More,
                    Glyph = ChatTheme.IconArchive,
                    Label = _showArchived ? "Hide archived sessions" : $"Show archived sessions ({archived})",
                    Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); _showArchived = !_showArchived; ShowHistoryMenu(refresh: true); },
                });
            if (_showArchived && archived > 0)
                entries.Add(new ChatPopup.Entry
                {
                    Group = More,
                    Glyph = ChatTheme.IconUnarchive,
                    Label = "Unarchive all",
                    Run = () =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        SaveSessionMeta(() => { foreach (var s in all.Where(x => x.Archived)) SessionMeta.SetArchived(s.Id, false); });
                        ShowHistoryMenu(refresh: true);
                    },
                });
            const string hint = "Hover a session to rename, fork, group or archive it";
            _sessions.Footer = SessionCost.TotalLine(visible) is string total ? total + "\n" + hint : hint;
            _sessions.Show(entries, _historyBtn, keepFilter: refresh);
            LoadSessionCosts(visible);
        }

        private bool _loadingSessionCosts;

        /// <summary>
        /// Reads what each listed session cost that is not known yet (off the UI thread; session files can be large),
        /// then shows the list again with the amounts if it is still open.
        /// </summary>
        private void LoadSessionCosts(List<SessionInfo> sessions)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var missing = sessions.Where(s => SessionCost.Cached(s.FilePath, s.LastWriteUtc, s.SizeBytes) == null).ToList();
            if (missing.Count == 0 || _loadingSessionCosts) return;
            _loadingSessionCosts = true;
            var prices = Options?.ToPriceTable();
            Jtf.RunAsync(async () =>
            {
                try { await System.Threading.Tasks.Task.Run(() => { foreach (var s in missing) SessionCost.Get(s.FilePath, prices); }); }
                finally
                {
                    await Jtf.SwitchToMainThreadAsync();
                    _loadingSessionCosts = false;
                }
                // Only once: a session written meanwhile (the open one) shows its amount the next time.
                if (_sessions.IsOpen && missing.Any(s => SessionCost.Cached(s.FilePath, s.LastWriteUtc, s.SizeBytes) != null)) ShowHistoryMenu(refresh: true);
            }).FileAndForget("ClaudeVS/ChatControl/SessionCosts");
        }

        /// <summary>Move a session to a group: pick one, type a new name to create it, rename or delete groups.</summary>
        private void ShowGroupPicker(SessionInfo s)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            void Done(Action change)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                SaveSessionMeta(change);
                ShowHistoryMenu(refresh: false);
            }
            var entries = new List<ChatPopup.Entry>
            {
                new ChatPopup.Entry { Label = "No group", Checked = string.IsNullOrEmpty(s.Group), Run = () => Done(() => SessionMeta.SetGroup(s.Id, null)) },
            };
            // In your order; ↑/↓ move a group and keep the picker open on the new order (as VS Code got in 2.1.330)
            void Move(string group, int step)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                SaveSessionMeta(() => SessionMeta.MoveGroup(group, step));
                ShowGroupPicker(s);
            }
            var groups = SessionMeta.Groups();
            for (int gi = 0; gi < groups.Count; gi++)
            {
                string group = groups[gi];
                var actions = new List<ChatPopup.EntryAction>();
                if (gi > 0) actions.Add(new ChatPopup.EntryAction { Glyph = ChatTheme.IconUp, Tooltip = "Move up", Run = () => Move(group, -1) });
                if (gi < groups.Count - 1) actions.Add(new ChatPopup.EntryAction { Glyph = ChatTheme.IconDown, Tooltip = "Move down", Run = () => Move(group, 1) });
                actions.Add(new ChatPopup.EntryAction { Glyph = ChatTheme.IconDelete, Tooltip = "Delete group (its sessions stay, ungrouped)", Run = () => Done(() => SessionMeta.RenameGroup(group, null)) });
                entries.Add(new ChatPopup.Entry
                {
                    Group = "Groups",
                    Glyph = ChatTheme.IconFolder,
                    Label = group,
                    Checked = string.Equals(group, s.Group, StringComparison.OrdinalIgnoreCase),
                    Run = () => Done(() => SessionMeta.SetGroup(s.Id, group)),
                    Rename = name => Done(() => SessionMeta.RenameGroup(group, name)),
                    Actions = actions,
                });
            }
            _manage.Footer = "Type a name to create a new group";
            _manage.Show(entries, _historyBtn, fromFilter: text => new ChatPopup.Entry
            {
                Glyph = ChatTheme.IconAdd,
                Label = $"Create group “{SessionMetaStore.Clean(text)}”",
                Run = () => Done(() => SessionMeta.SetGroup(s.Id, text)),
            });
        }

        /// <summary>
        /// The next prompt continues an earlier conversation (claude --resume). Its earlier messages are read from the
        /// CLI's session file and redrawn (prompts, answers, tool rows), off the UI thread.
        /// </summary>
        private void ResumeSession(SessionInfo s)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ResumeSession(s, fork: false);
        }

        private void ResumeSession(SessionInfo s, bool fork)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DisposeSession(); ClearWaitingNotice();
            ResetNotices(); // another conversation (DisposeSession skips this when no process was running)
            ResetMeter();
            ClearTranscript();
            _resumeId = s.Id;
            _forkNext = fork;
            _sessionName = fork ? SessionMetaStore.Clean(s.Title + " (fork)") : SessionMeta.Get(s.Id)?.Name;
            SetBusy(false);
            SetEmptyState(false);
            SetTitle(fork ? _sessionName : s.Title);
            ClaudeOutputPane.WriteLine($"Next prompt {(fork ? "forks" : "resumes")} session {s.Id}");
            _prompt.Focus();

            int generation = ++_resumeGeneration;
            Jtf.RunAsync(async () =>
            {
                var transcript = await System.Threading.Tasks.Task.Run(() => SessionTranscript.Read(s.FilePath));
                await Jtf.SwitchToMainThreadAsync();
                if (generation != _resumeGeneration || _session != null) return; // another session was opened, or a prompt already sent
                RenderTranscript(transcript);
                var now = DateTime.UtcNow;
                Tip(CacheWindow.ResumeNote(s.LastWriteUtc, transcript.LastContextTokens, now)); // a cold cache costs the first prompt
                // …and a /compact button by the meter, until the conversation's first turn redraws the advice
                if (TipsOn && CacheWindow.ResumeCompactAdvice(s.LastWriteUtc, transcript.LastContextTokens, now) is string compact)
                {
                    _advice.Text = "💡 " + compact;
                    _advice.ClearValue(TextBlock.ForegroundProperty);
                    _adviceCommand = "/compact";
                    _adviceAction.Content = _adviceCommand;
                    _adviceAction.Visibility = Visibility.Visible;
                }
            }).FileAndForget("ClaudeVS/ChatControl/Resume");
        }

        private int _resumeGeneration;

        private void RenderTranscript(SessionTranscript t)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (t.Skipped > 0) Append($"{t.Skipped} earlier items are not shown.");
            string prompt = null;
            System.Windows.Documents.Block anchor = null;
            foreach (var item in t.Items)
            {
                switch (item.Kind)
                {
                    case TranscriptItemKind.User:
                        _view.AppendUserMessage(item.Text, null, RewindAction(item.Uuid, item.Text, item.TimestampUtc), null, item.TimestampUtc);
                        prompt = item.Text;
                        anchor = _view.LastUserMessage;
                        break;
                    case TranscriptItemKind.Answer:
                        _view.AppendAnswer(item.Text);
                        _view.EndAnswer();
                        // Earlier answers can be bookmarked too.
                        _view.AppendTurnFooter("", BookmarkToggle(prompt, item.Text, anchor), item.Text, item.TimestampUtc);
                        _lastAnswer = item.Text;
                        break;
                    case TranscriptItemKind.Tool:
                        _view.AppendTool(item.ToolUseId, item.Text, item.ToolInput?.ToString(), null,
                            item.ToolName == "Write" ? null : InlineDiff.FromToolUse(item.ToolName, item.ToolInput), // the content before an old Write is unknown
                            item.ToolName, item.ToolInput, _sessionDir);
                        if (item.ResultChars >= 0) _view.CompleteTool(item.ToolUseId, item.ResultChars, item.IsError, item.ResultText, resultData: item.ResultData);
                        break;
                    case TranscriptItemKind.Compacted:
                        _view.AppendCompaction(item.PreTokens, item.Trigger, item.Text);
                        break;
                }
            }
            Append(t.Items.Count == 0
                ? "Resumed. No earlier messages could be read, but Claude has the full context. The meter updates after the first answer."
                : "Resumed. Claude has the full context; the meter updates after the first answer.");
            _view.Box.ScrollToEnd();
        }

        private void NewChat()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DisposeSession(); ClearWaitingNotice();
            ResetMeter();
            ClearTranscript();
            _resumeId = null;
            _forkNext = false;
            _sessionName = null;
            _resumeGeneration++; // drop a resumed transcript that is still being read
            RefreshCommands(force: true);
            _fileIndex = null; // re-index on the next @ (files may have been added)
            SetBusy(false);
            ClearSuggestion();
            ForgetOffered();
            SetTitle(null);
            SetEmptyState(true);
            UpdateFolderChip();
        }
    }
}
