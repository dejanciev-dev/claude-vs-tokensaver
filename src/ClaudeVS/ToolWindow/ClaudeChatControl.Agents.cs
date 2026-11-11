using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Tokens;
using ClaudeVS.Diagnostics;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// Agents panel above the input box: Claude's subagents and background commands as a tree, with what each is
    /// doing, its tokens, tool calls and time, and Run in background / Stop. Built from the CLI's task_* messages.
    /// </summary>
    public sealed partial class ClaudeChatControl
    {
        private const int MaxAgentRows = 12;

        private readonly AgentTaskList _tasks = new AgentTaskList();
        private readonly Border _agentsPanel = new Border
        {
            Visibility = Visibility.Collapsed,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = ChatTheme.Hover,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 0, 4),
        };
        private bool _agentsCollapsed;     // the user's choice while something runs
        private bool _agentsDoneOpen;      // opened by the user while everything is done (folded by default then)
        private DispatcherTimer _agentsTimer;
        // Detail lines that show elapsed time, refreshed each second while something runs.
        private readonly List<(TextBlock Line, AgentTask Task)> _agentDetails = new List<(TextBlock, AgentTask)>();
        /// <summary>The background task that finished last, to explain a turn the CLI starts on its own.</summary>
        private string _lastBackgroundDone;

        private void OnTaskEvent(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_tasks.Apply(ev.Payload)) return;
            if ((string)ev.Payload["subtype"] == "task_notification")
            {
                var t = _tasks.Find((string)ev.Payload["task_id"]);
                if (t != null && t.IsBackgrounded && t.ParentTaskId == null) _lastBackgroundDone = t.Description;
                _view.ShowAgentUsage(t?.ToolUseId, Core.Cli.ToolCallView.AgentUsageLine(t)); // stays when the panel is cleared
                Tip(SessionNotices.ForkCost(t));
            }
            RefreshAgentsPanel();
        }

        /// <summary>
        /// Called on each turn start (init). With no prompt of ours waiting, the CLI started the turn itself, which it
        /// does when a background task finishes: keep the per-prompt queues in step and say why Claude is answering.
        /// </summary>
        private void OnTurnStarted()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_pendingUsages.Count > 0) return; // one of our prompts
            _pendingUsages.Enqueue(new UsageRecord { Solution = _sessionDir, RouteReason = "started by Claude Code (background task finished)" });
            _requestedFamilies.Enqueue(null);
            _turnPrompts.Enqueue(new TurnPrompt());
            SetBusy(true);
            FlushText();
            string what = _lastBackgroundDone != null ? $"“{_lastBackgroundDone}” finished" : "A background task finished";
            _lastBackgroundDone = null;
            Append($"\n  ⚙ {what}; Claude continues.\n");
            Announce($"{what.Replace('“', '"').Replace('”', '"')}; Claude continues.");
        }

        /// <summary>A subagent's request: priced (at the meter's prices) onto its row.</summary>
        private void OnSubagentUsage(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var meter = _meterModel;
            string modelBefore = _tasks.Tasks.FirstOrDefault(x => x.ToolUseId == ev.ParentToolUseId)?.Model;
            if (meter == null || !_tasks.ObserveRequest(ev, meter.RequestCostUsd)) return;
            if (_tasks.Tasks.FirstOrDefault(x => x.ToolUseId == ev.ParentToolUseId)?.Model != modelBefore) { RefreshAgentsPanel(); return; } // the tag names it
            var now = DateTime.UtcNow;
            foreach (var (line, task) in _agentDetails) line.Text = AgentDetail(task, now); // running rows; a finished one is redrawn by its notification
        }

        /// <summary>Before a new prompt: finished tasks from earlier turns leave the panel.</summary>
        private void ForgetFinishedTasks()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_tasks.RemoveFinished()) RefreshAgentsPanel();
        }

        /// <summary>Empties the panel (called from ClearTurns, which runs on the UI thread).</summary>
        private void ClearTasks()
        {
            _tasks.Clear();
            _lastBackgroundDone = null;
            _agentDetails.Clear();
            _agentsTimer?.Stop();
            _agentsPanel.Child = null;
            _agentsPanel.Visibility = Visibility.Collapsed;
        }

        private void RefreshAgentsPanel()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _agentDetails.Clear();
            if (_tasks.IsEmpty)
            {
                _agentsPanel.Visibility = Visibility.Collapsed;
                _agentsPanel.Child = null;
                UpdateAgentsTimer();
                return;
            }

            var accents = ChatTheme.Accents(this);
            var stack = new StackPanel();
            // While something runs the list is open (unless the user folded it); once everything is done it folds
            // to the one-line headline, since the commands and their output are in the chat above.
            bool allDone = _tasks.RunningCount == 0;
            if (!allDone) _agentsDoneOpen = false;
            bool collapsed = allDone ? !_agentsDoneOpen : _agentsCollapsed;
            void Toggle()
            {
                if (allDone) _agentsDoneOpen = !_agentsDoneOpen;
                else _agentsCollapsed = !_agentsCollapsed;
                RefreshAgentsPanel();
            }

            var header = new DockPanel { Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
            if (_tasks.RunningCount < _tasks.Tasks.Count)
            {
                var clear = new TextBlock { Text = "Clear finished", FontSize = 11, Cursor = System.Windows.Input.Cursors.Hand, Foreground = accents.Link, VerticalAlignment = VerticalAlignment.Center };
                clear.MouseLeftButtonUp += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); e.Handled = true; ForgetFinishedTasks(); };
                DockPanel.SetDock(clear, Dock.Right);
                header.Children.Add(clear);
            }
            var title = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
            title.Inlines.Add(new System.Windows.Documents.Run(collapsed ? "▸ " : "▾ "));
            if (allDone) title.Inlines.Add(new System.Windows.Documents.Run("✓ ") { Foreground = accents.Success });
            else title.Inlines.Add(new System.Windows.Documents.Run("⚙ "));
            title.Inlines.Add(new System.Windows.Documents.Run(_tasks.Headline()));
            ChatTheme.Text(title);
            header.Children.Add(title);
            header.ToolTip = collapsed ? "Show agents and background tasks" : "Hide the list";
            header.MouseLeftButtonUp += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); Toggle(); };
            // Keyboard and screen readers: Tab to the header, Enter or Space toggles the list.
            header.Focusable = true;
            System.Windows.Automation.AutomationProperties.SetName(header, _tasks.Headline() + (collapsed ? ", collapsed" : ", expanded"));
            header.KeyDown += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (e.Key != System.Windows.Input.Key.Enter && e.Key != System.Windows.Input.Key.Space) return;
                e.Handled = true;
                Toggle();
            };
            stack.Children.Add(header);

            if (!collapsed)
            {
                var rows = _tasks.Tree();
                foreach (var (task, depth) in rows.Take(MaxAgentRows)) stack.Children.Add(AgentRow(task, depth, accents));
                if (rows.Count > MaxAgentRows)
                {
                    var more = new TextBlock { Text = $"+{rows.Count - MaxAgentRows} more", FontSize = 11, Margin = new Thickness(16, 2, 0, 0) };
                    ChatTheme.Muted(more);
                    stack.Children.Add(more);
                }
            }

            _agentsPanel.Child = stack;
            _agentsPanel.Visibility = Visibility.Visible;
            UpdateAgentsTimer();
        }

        private FrameworkElement AgentRow(AgentTask t, int depth, ChatTheme.AccentSet accents)
        {
            var row = new DockPanel { Margin = new Thickness(4 + depth * 14, 3, 0, 0) };

            if (t.IsRunning && _session != null)
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
                if (t.CanMoveToBackground)
                {
                    var bg = SmallButton("Background", "Run in background: Claude carries on now and is told when this agent finishes");
                    bg.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); bg.IsEnabled = false; MoveTaskToBackground(t); };
                    buttons.Children.Add(bg);
                }
                var stop = SmallButton("Stop", t.IsAgent ? "Stop this agent (and anything it started)" : "Stop this command");
                stop.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); stop.IsEnabled = false; StopTask(t); };
                buttons.Children.Add(stop);
                DockPanel.SetDock(buttons, Dock.Right);
                row.Children.Add(buttons);
            }

            var dot = new TextBlock
            {
                Text = "●",
                FontSize = 9,
                Margin = new Thickness(0, 3, 6, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = t.Status == AgentTaskStatus.Running ? accents.Running
                    : t.Status == AgentTaskStatus.Completed ? accents.Success
                    : t.Status == AgentTaskStatus.Failed ? accents.Failure : Brushes.Gray,
            };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);

            // One line: the name (cut with … when narrow), then status · tokens · time on the right, never cut.
            var detail = new TextBlock { FontSize = 11, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            ChatTheme.Muted(detail);
            detail.Text = AgentDetail(t, DateTime.UtcNow);
            DockPanel.SetDock(detail, Dock.Right);
            row.Children.Add(detail);
            if (t.IsRunning) _agentDetails.Add((detail, t));

            var name = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            string described = FirstLine(Core.Cli.ToolCallView.WithoutCdToBase(t.Description?.Trim(), _sessionDir));
            name.Inlines.Add(new System.Windows.Documents.Run(described ?? (t.IsAgent ? "Agent" : "Command")) { FontWeight = FontWeights.SemiBold });
            // Commands are most of the list: only agents (and background runs) say what kind they are.
            string kind = t.IsAgent ? Core.Cli.ToolCallView.AgentKind(t.SubagentType) : "command";
            // "Explore · haiku": the model decides most of what an agent costs
            string tag = t.IsAgent ? kind + (t.ModelShort != null ? " · " + t.ModelShort : "") + (t.IsBackgrounded ? " · background" : "") : t.IsBackgrounded ? "background" : null;
            if (tag != null) name.Inlines.Add(new System.Windows.Documents.Run("  " + tag) { Foreground = Brushes.Gray });
            ChatTheme.Text(name);
            row.Children.Add(name);

            string tip = StatusWord(t.Status);
            if (!string.IsNullOrWhiteSpace(t.Prompt)) tip += "\n\nTask:\n" + Shorten(t.Prompt, 600);
            if (!string.IsNullOrWhiteSpace(t.Summary)) tip += "\n\nResult:\n" + Shorten(t.Summary, 600);
            row.ToolTip = tip;
            System.Windows.Automation.AutomationProperties.SetName(row, $"{t.Description}, {kind}, {StatusWord(t.Status)}");
            return row;
        }

        /// <summary>
        /// "Read: Foo.cs · 20.1k tok · $0.042 · 3 tools · 0:23" while running, "Done · 0:08" once finished; short, since it sits
        /// on the row's right. The result is in the row's tooltip and in the chat.
        /// </summary>
        private static string AgentDetail(AgentTask t, DateTime nowUtc)
        {
            var parts = new List<string>();
            if (t.IsRunning) parts.Add(Shorten(t.Activity ?? (t.IsAgent ? "Starting…" : "Running…"), 40));
            else parts.Add(StatusWord(t.Status));
            if (t.TotalTokens > 0) parts.Add(TokenMeter.Format(t.TotalTokens) + " tok");
            string cost = AgentTask.FormatCost(t.CostUsd);
            if (cost != null) parts.Add(cost);
            if (t.ToolUses > 0) parts.Add(t.ToolUses + (t.ToolUses == 1 ? " tool" : " tools"));
            var elapsed = t.Elapsed(nowUtc);
            parts.Add(elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss"));
            return string.Join(" · ", parts);
        }

        private static string StatusWord(AgentTaskStatus s) =>
            s == AgentTaskStatus.Running ? "Running" : s == AgentTaskStatus.Completed ? "Done" : s == AgentTaskStatus.Failed ? "Failed" : "Stopped";

        private static string Shorten(string s, int max) => s.Length <= max ? s.Trim() : s.Substring(0, max - 1).Trim() + "…";

        /// <summary>
        /// A background command's description can be the whole command, heredoc and all: keep its first line
        /// (at most 120 characters) so the row stays one line. The full text is in the transcript.
        /// </summary>
        private static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            string first = nl < 0 ? s : s.Substring(0, nl).TrimEnd() + " …";
            return Shorten(first, 120);
        }

        private static Button SmallButton(string text, string tooltip)
        {
            var b = ChatTheme.TextButton(text, tooltip);
            b.FontSize = 11;
            b.Padding = new Thickness(6, 0, 6, 1);
            b.Margin = new Thickness(4, 0, 0, 0);
            b.VerticalAlignment = VerticalAlignment.Top;
            return b;
        }

        private void StopTask(AgentTask t)
        {
            var session = _session;
            if (session == null) return;
            Fire(async () =>
            {
                try { await session.StopTaskAsync(t.TaskId); }
                catch (InvalidOperationException ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ClaudeOutputPane.WriteLine($"stop_task {t.TaskId} failed: {ex.Message}");
                    AppendError("[agents] ", $"Could not stop “{t.Description}”: {ex.Message}");
                    RefreshAgentsPanel();
                }
            });
        }

        private void MoveTaskToBackground(AgentTask t)
        {
            var session = _session;
            if (session == null) return;
            Fire(async () =>
            {
                bool moved;
                try { moved = await session.MoveToBackgroundAsync(t.ToolUseId); }
                catch (InvalidOperationException ex) { ClaudeOutputPane.WriteLine($"background_tasks {t.ToolUseId} failed: {ex.Message}"); moved = false; }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!moved) { Append($"\n  Could not move “{t.Description}” to the background (it may have just finished).\n"); RefreshAgentsPanel(); }
            });
        }

        private void UpdateAgentsTimer()
        {
            bool needed = _agentDetails.Count > 0;
            if (needed && _agentsTimer == null)
            {
                _agentsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _agentsTimer.Tick += (s, e) =>
                {
                    var now = DateTime.UtcNow;
                    foreach (var (line, task) in _agentDetails) line.Text = AgentDetail(task, now);
                };
            }
            if (_agentsTimer == null) return;
            if (needed && !_agentsTimer.IsEnabled) _agentsTimer.Start();
            else if (!needed && _agentsTimer.IsEnabled) _agentsTimer.Stop();
        }
    }
}
