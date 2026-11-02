using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
    /// <summary>
    /// The chat panel. Built in code (no XAML) to keep the project simple; colors come from the
    /// VS theme so it works in Light, Dark and Blue in both VS 2022 and VS 2026.
    /// </summary>
    public sealed class ClaudeChatControl : UserControl, IDisposable
    {
        private readonly ComboBox _model = new ComboBox { Width = 90, Margin = new Thickness(0, 0, 4, 0) };
        private readonly ComboBox _effort = new ComboBox { Width = 80, Margin = new Thickness(0, 0, 4, 0) };
        private readonly Button _new = Btn("New", "Start a new conversation (fresh context = cheapest)");
        private readonly Button _compact = Btn("/compact", "Summarize the conversation to shrink context");
        private readonly Button _stop = Btn("Stop", "Interrupt the current turn");
        private readonly Button _historyBtn = Btn("History", "Resume an earlier conversation in this solution");
        private readonly ProgressBar _ctxBar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 4, 0, 2) };
        private readonly TextBlock _meter = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _advice = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        private readonly Button _adviceAction = Btn("", "");
        private readonly TranscriptView _view = new TranscriptView();
        private readonly StackPanel _permission = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4), Visibility = Visibility.Collapsed };
        private readonly TextBlock _permissionText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 400 };
        private readonly TextBlock _route = new TextBlock { FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _attachment = new TextBlock { FontSize = 11, Visibility = Visibility.Collapsed };
        private readonly TextBox _prompt = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly Button _send = Btn("Send", "Enter = send, Shift+Enter = new line");

        private readonly StringBuilder _pendingText = new StringBuilder();
        private readonly DispatcherTimer _flushTimer;
        private readonly DispatcherTimer _routeTimer;
        private readonly ModelRouter _router = new ModelRouter();

        private ClaudeSession _session;
        private TokenMeter _meterModel;
        private EditorSelection _selection;
        private string _sessionDir;
        private string _resumeId;
        private readonly PromptHistory _promptHistory = new PromptHistory();
        private List<SlashCommand> _commands = new List<SlashCommand>(SlashCommands.BuiltIn);
        private string _commandsDir;
        private ClaudeEvent _pendingPermission;
        private string _adviceCommand;
        private string _turnModel;
        private string _lastHint;
        private readonly System.Collections.Generic.List<string> _attachedFiles = new System.Collections.Generic.List<string>();
        private System.Collections.Generic.IReadOnlyList<BuildError> _attachedErrors;
        private UsageRecord _pendingUsage;
        internal static readonly UsageLog Usage = new UsageLog(UsageLog.DefaultPath);

        public ClaudeChatControl()
        {
            foreach (var m in Enum.GetValues(typeof(ModelChoice))) _model.Items.Add(m);
            foreach (var e in Enum.GetValues(typeof(EffortChoice))) _effort.Items.Add(e);
            _model.SelectedItem = Options?.DefaultModel ?? ModelChoice.Auto;
            _effort.SelectedItem = EffortChoice.Auto;
            _model.ToolTip = "Auto picks Haiku / Sonnet / Opus per prompt";
            _effort.ToolTip = "Thinking effort. Auto follows the model choice";

            Content = BuildLayout();
            this.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            this.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            foreach (var tb in new Control[] { _prompt })
            {
                tb.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
                tb.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            }

            _send.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); SendFromBox(); };
            _new.Click += (s, e) => NewChat();
            _historyBtn.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); ShowHistoryMenu(); };
            _stop.Click += (s, e) => Fire(() => _session?.InterruptAsync());
            _compact.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); SendText("/compact", routed: false); };
            _adviceAction.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); if (_adviceCommand != null) SendText(_adviceCommand, routed: false); };
            _prompt.PreviewKeyDown += OnPromptKeyDown;
            _prompt.TextChanged += (s, e) => { _routeTimer.Stop(); _routeTimer.Start(); };
            _model.SelectionChanged += (s, e) => UpdateRouteHint();

            _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (s, e) => FlushText(), Dispatcher);
            _routeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (s, e) => { _routeTimer.Stop(); UpdateRouteHint(); }, Dispatcher);
            _flushTimer.Stop();
            _routeTimer.Stop();

            ResetMeter();
            Append("Claude Code for Visual Studio — token saver edition.\nTip: select code and press Ctrl+Alt+K, S to ask about it; only the selection is sent.\n\n");
        }

        private static ClaudeOptionsPage Options => ClaudeVSPackage.Instance?.Options;

        private static Microsoft.VisualStudio.Threading.JoinableTaskFactory Jtf => ClaudeVSPackage.Instance?.JoinableTaskFactory ?? ThreadHelper.JoinableTaskFactory;

        // ---------------- Layout ----------------

        private UIElement BuildLayout()
        {
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            toolbar.Children.Add(_model);
            toolbar.Children.Add(_effort);
            toolbar.Children.Add(_new);
            toolbar.Children.Add(_compact);
            toolbar.Children.Add(_stop);
            toolbar.Children.Add(_historyBtn);

            var adviceRow = new DockPanel();
            _adviceAction.Visibility = Visibility.Collapsed;
            _adviceAction.Margin = new Thickness(6, 2, 0, 0);
            DockPanel.SetDock(_adviceAction, Dock.Right);
            adviceRow.Children.Add(_adviceAction);
            adviceRow.Children.Add(_advice);

            var allow = Btn("Allow", "");
            var deny = Btn("Deny", "");
            allow.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); AnswerPermission(true); };
            deny.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); AnswerPermission(false); };
            _permission.Children.Add(_permissionText);
            _permission.Children.Add(allow);
            _permission.Children.Add(deny);

            var top = new StackPanel { Margin = new Thickness(6) };
            top.Children.Add(toolbar);
            top.Children.Add(_ctxBar);
            top.Children.Add(_meter);
            top.Children.Add(adviceRow);

            var promptRow = new DockPanel();
            _send.Margin = new Thickness(4, 0, 0, 0);
            _send.VerticalAlignment = VerticalAlignment.Bottom;
            DockPanel.SetDock(_send, Dock.Right);
            promptRow.Children.Add(_send);
            promptRow.Children.Add(_prompt);

            var bottom = new StackPanel { Margin = new Thickness(6) };
            bottom.Children.Add(_permission);
            bottom.Children.Add(_attachment);
            bottom.Children.Add(_route);
            bottom.Children.Add(promptRow);

            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(top);
            root.Children.Add(bottom);
            _view.Box.Margin = new Thickness(6, 0, 6, 0);
            root.Children.Add(_view.Box);
            return root;
        }

        private static Button Btn(string text, string tip) => new Button
        {
            Content = text, ToolTip = string.IsNullOrEmpty(tip) ? null : tip, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), MinWidth = 50,
        };

        // ---------------- Public API ----------------

        public void AttachSelection(EditorSelection selection)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _selection = selection;
            UpdateAttachments(focus: selection != null);
        }

        /// <summary>Attach a file as a path reference only (Claude reads what it needs).</summary>
        public void AttachFile(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(path)) return;
            if (!_attachedFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) _attachedFiles.Add(path);
            UpdateAttachments(focus: true);
        }

        /// <summary>Attach error-list entries (one line each, capped when the prompt is built).</summary>
        public void AttachErrors(System.Collections.Generic.IReadOnlyList<BuildError> errors)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _attachedErrors = errors != null && errors.Count > 0 ? errors : null;
            UpdateAttachments(focus: _attachedErrors != null);
        }

        private void ClearAttachments()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _selection = null;
            _attachedFiles.Clear();
            _attachedErrors = null;
            UpdateAttachments(focus: false);
        }

        private bool HasAttachments => _selection != null || _attachedFiles.Count > 0 || _attachedErrors != null;

        private void UpdateAttachments(bool focus)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var parts = new System.Collections.Generic.List<string>();
            string dir = VsContext.GetSolutionDirectory();
            if (_selection != null)
            {
                int lines = _selection.EndLine - _selection.StartLine + 1;
                bool inline = lines <= (Options?.InlineMaxLines ?? 60);
                parts.Add($"{PromptContextBuilder.Relative(dir, _selection.FilePath)}:L{_selection.StartLine}-{_selection.EndLine} ({lines} lines, {(inline ? "inlined" : "sent as reference")})");
            }
            foreach (var f in _attachedFiles) parts.Add(PromptContextBuilder.Relative(dir, f) + " (reference)");
            if (_attachedErrors != null)
            {
                int max = Options?.MaxAttachedErrors ?? 20;
                parts.Add(_attachedErrors.Count > max ? $"{max} of {_attachedErrors.Count} errors" : $"{_attachedErrors.Count} error(s)");
            }

            if (parts.Count == 0) { _attachment.Visibility = Visibility.Collapsed; UpdateRouteHint(); return; }
            _attachment.Text = "📎 " + string.Join(" · ", parts) + " — Esc to remove";
            _attachment.Visibility = Visibility.Visible;
            if (focus) _prompt.Focus();
            UpdateRouteHint();
        }

        // ---------------- Sending ----------------

        private void OnPromptKeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                SendFromBox();
            }
            else if ((e.Key == Key.Up || e.Key == Key.Down) && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                string text = e.Key == Key.Up ? _promptHistory.Previous(_prompt.Text) : _promptHistory.Next();
                if (text != null) { _prompt.Text = text; _prompt.CaretIndex = text.Length; }
            }
            else if (e.Key == Key.Escape && HasAttachments)
            {
                e.Handled = true;
                ClearAttachments();
            }
        }

        private void SendFromBox()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string text = _prompt.Text.Trim();
            if (text.Length == 0) return;
            _promptHistory.Add(text);
            _prompt.Clear();
            SendText(text, routed: !text.StartsWith("/"));
        }

        private void SendText(string userText, bool routed)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var opts = Options;
            string dir = VsContext.GetSolutionDirectory() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var built = routed
                ? new PromptContextBuilder(opts?.ToContextSettings()).Build(userText, dir, _selection, _attachedFiles, _attachedErrors)
                : new BuiltPrompt { Text = userText };
            int attachedLines = _selection == null ? 0 : _selection.EndLine - _selection.StartLine + 1;
            int attachedFiles = routed ? _attachedFiles.Count : 0;
            ClearAttachments();

            Fire(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                EnsureSession(dir);

                ModelChoice model = (ModelChoice)_model.SelectedItem;
                EffortChoice effort = (EffortChoice)_effort.SelectedItem;
                string routeNote = null;
                RouteDecision decision = null;
                if (routed && (model == ModelChoice.Auto || effort == EffortChoice.Auto))
                {
                    decision = Route(userText, attachedLines, attachedFiles);
                    if (model == ModelChoice.Auto) model = decision.Model;
                    if (effort == EffortChoice.Auto) effort = decision.Effort;
                    routeNote = $"auto → {model.ToCli()}/{effort.ToCli()}";
                    ClaudeOutputPane.WriteLine($"Route: {decision} | prompt {userText.Length} chars, {attachedLines} attached lines, {attachedFiles} files, context {_meterModel?.ContextTokens ?? 0} tok → using {model.ToCli()}/{effort.ToCli()}");
                }

                _pendingUsage = new UsageRecord
                {
                    Solution = dir,
                    Routed = decision != null,
                    RouteModel = decision != null ? model.ToCli() : null,
                    RouteEffort = decision != null ? effort.ToCli() : null,
                    RouteScore = decision?.Score,
                    RouteReason = decision?.Reason,
                    PromptChars = userText.Length,
                    AttachedLines = attachedLines,
                    AttachedFiles = attachedFiles,
                    PromptPreview = decision != null && Options?.LogPromptPreview == true ? UsageRecord.Preview(userText) : null,
                    CharsAvoided = built.CharsAvoided,
                };

                await _session.SetModelAsync(model);
                await _session.SetEffortAsync(effort);

                Append($"\n▶ You{(routeNote != null ? "  [" + routeNote + "]" : "")}\n{userText}\n");
                if (built.CharsAvoided > 0) Append($"  (context: sent {built.InlinedLines} inline lines, avoided ~{built.CharsAvoided / 4:N0} tokens of file content)\n");
                if (built.AttachedErrors > 0) Append($"  (attached {built.AttachedErrors} error(s) from the Error List)\n");
                Append("\n◀ Claude\n");
                await _session.SendPromptAsync(built.Text);
            });
        }

        private RouteDecision Route(string prompt, int attachedLines, int attachedFiles)
        {
            _router.StickyContextTokens = Options?.StickyContextTokens ?? 30000;
            return _router.Route(new RouteRequest
            {
                Prompt = prompt,
                AttachedCodeLines = attachedLines,
                AttachedFiles = attachedFiles,
                CurrentModel = _session?.IsRunning == true ? _session.CurrentModel : ModelChoice.Auto,
                ContextTokens = _meterModel?.ContextTokens ?? 0,
            });
        }

        private void UpdateRouteHint()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_prompt.Text.TrimStart().StartsWith("/"))
            {
                RefreshCommands();
                var matches = SlashCommands.Match(_commands, _prompt.Text);
                _route.Text = matches.Count == 0
                    ? "No matching command. Custom commands live in .claude/commands/*.md"
                    : string.Join("   ", matches.Take(6).Select(c => c + (string.IsNullOrEmpty(c.Description) ? "" : " — " + c.Description)));
                return;
            }
            if ((ModelChoice)_model.SelectedItem != ModelChoice.Auto || string.IsNullOrWhiteSpace(_prompt.Text) || _prompt.Text.TrimStart().StartsWith("/"))
            {
                _route.Text = "";
                return;
            }
            int lines = _selection == null ? 0 : _selection.EndLine - _selection.StartLine + 1;
            var d = Route(_prompt.Text, lines, _attachedFiles.Count);
            _route.Text = $"Auto: {d.Model.ToCli()} / {d.Effort.ToCli()} — {d.Reason}";
        }

        private void EnsureSession(string dir)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_session != null && string.Equals(_sessionDir, dir, StringComparison.OrdinalIgnoreCase)) return;
            if (_session != null) { Append("\n— Solution changed, starting a new conversation —\n"); DisposeSession(); }

            var o = Options;
            _session = new ClaudeSession(new ClaudeLaunchOptions
            {
                ClaudePath = o?.ClaudePath ?? "",
                WorkingDirectory = dir,
                PermissionMode = o?.PermissionMode ?? PermissionModeChoice.AcceptEdits,
                AppendSystemPrompt = (o?.ConciseAnswers ?? true) ? ClaudeOptionsPage.BrevityPrompt : null,
                ExtraArguments = o?.ExtraArguments ?? "",
                ResumeSessionId = _resumeId,
            });
            _resumeId = null;
            _sessionDir = dir;
            _session.EventReceived += OnEvent;
            if (o?.LogRawCliTraffic == true) _session.RawLine += OnRawLine;
            _session.Exited += (s, code) =>
            {
                ClaudeOutputPane.WriteLine($"claude process exited with code {code}");
                OnUi(() => { FlushText(); if (code != 0 && code != 1) Append($"\n[claude exited with code {code}]\n"); });
            };
            ClaudeOutputPane.WriteLine($"Starting claude in {dir}");
            LogUsageSummary(dir);
        }

        /// <summary>Custom slash commands for the current solution, re-read when the solution changes.</summary>
        private void RefreshCommands(bool force = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dir = VsContext.GetSolutionDirectory();
            if (!force && _commandsDir != null && string.Equals(dir, _commandsDir, StringComparison.OrdinalIgnoreCase)) return;
            _commandsDir = dir ?? "";
            _commands = SlashCommands.Discover(dir, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        private void ShowHistoryMenu()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dir = VsContext.GetSolutionDirectory() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var sessions = SessionHistory.List(SessionHistory.DefaultProjectsRoot, dir);
            var menu = new ContextMenu { PlacementTarget = _historyBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            if (sessions.Count == 0)
                menu.Items.Add(new MenuItem { Header = "No earlier conversations for this folder", IsEnabled = false });
            foreach (var s in sessions)
            {
                var item = new MenuItem
                {
                    Header = $"{s.LastWriteUtc.ToLocalTime():MMM d HH:mm}   {s.Title}",
                    ToolTip = $"{s.Id} · {s.SizeBytes / 1024:N0} KB on disk",
                };
                string id = s.Id, title = s.Title;
                item.Click += (o, e) => { ThreadHelper.ThrowIfNotOnUIThread(); ResumeSession(id, title); };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        /// <summary>The next prompt continues an earlier conversation (claude --resume). Earlier messages are not redrawn.</summary>
        private void ResumeSession(string sessionId, string title)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DisposeSession();
            ResetMeter();
            _view.Clear();
            _resumeId = sessionId;
            Append($"Resuming: {title}\nEarlier messages are not shown here, but Claude has the full context. The meter updates after the first answer.\n");
            ClaudeOutputPane.WriteLine($"Next prompt resumes session {sessionId}");
            _prompt.Focus();
        }

        private void NewChat()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DisposeSession();
            ResetMeter();
            _view.Clear();
            _resumeId = null;
            RefreshCommands(force: true);
            Append("New conversation.\n");
        }

        // ---------------- Events from the CLI (background thread) ----------------

        private void OnEvent(object sender, ClaudeEvent ev)
        {
            if (ev.Kind == ClaudeEventKind.TextDelta || ev.Kind == ClaudeEventKind.AssistantText)
            {
                lock (_pendingText) _pendingText.Append(ev.Text);
                OnUi(() => _flushTimer.Start());
                return;
            }

            if (ev.Kind == ClaudeEventKind.Error || (ev.Kind == ClaudeEventKind.TurnCompleted && ev.IsError))
                ClaudeOutputPane.WriteLine("Error: " + ev.Text);

            OnUi(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _meterModel?.Observe(ev);
                switch (ev.Kind)
                {
                    case ClaudeEventKind.SessionStarted:
                        ClaudeOutputPane.WriteLine($"Session {ev.SessionId} started, model {ev.Model}");
                        break;
                    case ClaudeEventKind.AssistantUsage:
                        if (!ev.IsSubagent && !string.IsNullOrEmpty(ev.Model)) _turnModel = ev.Model;
                        break;
                    case ClaudeEventKind.ToolUse:
                        FlushText();
                        _view.AppendTool(ev.ToolUseId, ev.Text, ev.ToolInput?.ToString());
                        break;
                    case ClaudeEventKind.ToolResult:
                        _view.CompleteTool(ev.ToolUseId, ev.ResultChars, ev.IsError);
                        break;
                    case ClaudeEventKind.PermissionRequest:
                        FlushText();
                        _pendingPermission = ev;
                        _permissionText.Text = "Allow " + ev.Text + "?";
                        _permission.Visibility = Visibility.Visible;
                        break;
                    case ClaudeEventKind.Compacted:
                        Append($"\n  ✂ Compacted ({TokenMeter.Format(ev.PreCompactTokens)} tokens before)\n");
                        break;
                    case ClaudeEventKind.TurnCompleted:
                        FlushText();
                        if (ev.IsError && !string.IsNullOrEmpty(ev.Text)) AppendError("[error] ", ev.Text);
                        string used = _turnModel != null ? " · " + _turnModel : "";
                        RecordUsage(ev, _turnModel);
                        _turnModel = null;
                        Append($"\n  ✓ {ev.DurationMs / 1000.0:0.0}s · {TokenMeter.Format(ev.Usage.Total)} tok · ${_meterModel?.LastTurnCostUsd:0.000}{used}\n");
                        break;
                    case ClaudeEventKind.Error:
                        AppendError("[claude] ", ev.Text);
                        break;
                }
            });
        }

        /// <summary>Completes the record started in SendText and appends it to the local usage log (off the UI thread).</summary>
        private void RecordUsage(ClaudeEvent ev, string modelUsed)
        {
            var rec = _pendingUsage;
            _pendingUsage = null;
            if (rec == null || Options?.KeepUsageLog == false) return;
            rec.Timestamp = DateTime.UtcNow;
            rec.ModelUsed = modelUsed;
            rec.InputTokens = ev.Usage.Input;
            rec.OutputTokens = ev.Usage.Output;
            rec.CacheReadTokens = ev.Usage.CacheRead;
            rec.CacheWriteTokens = ev.Usage.CacheCreation;
            rec.CostUsd = _meterModel?.LastTurnCostUsd ?? 0;
            rec.DurationMs = ev.DurationMs;
            rec.IsError = ev.IsError;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { Usage.Append(rec); }
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

        private void OnMeterChanged(object sender, EventArgs e)
        {
            var m = _meterModel;
            if (m == null) return;
            _ctxBar.Value = Math.Min(100, m.ContextPercent);
            long window = m.ContextWindow > 0 ? m.ContextWindow : 200_000;
            _meter.Text = $"Context {m.ContextPercent:0}% ({TokenMeter.Format(m.ContextTokens)}/{TokenMeter.Format(window)})  ·  "
                        + $"Session {TokenMeter.Format(m.SessionTotal.Total)} tok, ${m.SessionCostUsd:0.00}  ·  Cache {m.CacheHitPercent:0}%  ·  Model {(_session?.CurrentModel.ToCli() ?? "-")}";

            var advice = m.GetAdvice().OrderByDescending(a => a.Level).FirstOrDefault();
            _advice.Text = advice == null ? "" : (advice.Level == AdviceLevel.Critical ? "⚠ " : "💡 ") + advice.Message;
            if (advice?.Level == AdviceLevel.Critical) _advice.Foreground = Brushes.OrangeRed;
            else _advice.ClearValue(TextBlock.ForegroundProperty);
            _adviceCommand = advice?.SuggestedCommand;
            _adviceAction.Content = _adviceCommand;
            _adviceAction.Visibility = _adviceCommand == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void AnswerPermission(bool allow)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var req = _pendingPermission;
            _pendingPermission = null;
            _permission.Visibility = Visibility.Collapsed;
            if (req == null || _session == null) return;
            Append(allow ? "  ✔ allowed\n" : "  ✖ denied\n");
            Fire(() => _session.RespondToPermissionAsync(req.RequestId, req.ToolInput ?? new JObject(), allow));
        }

        // ---------------- Helpers ----------------

        private void ResetMeter()
        {
            if (_meterModel != null) _meterModel.Changed -= OnMeterChanged;
            _meterModel = new TokenMeter(Options?.ToMeterSettings());
            _meterModel.Changed += OnMeterChanged;
            OnMeterChanged(this, EventArgs.Empty);
        }

        private void FlushText()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string chunk;
            lock (_pendingText)
            {
                chunk = _pendingText.ToString();
                _pendingText.Clear();
            }
            if (chunk.Length > 0) _view.AppendAnswer(chunk);
            _flushTimer.Stop();
        }

        private void AppendError(string prefix, string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Append("\n" + prefix + text + "\n");
            string hint = ClaudeErrorHints.Explain(text);
            if (hint != null && hint != _lastHint) Append("  → " + hint + "\n");
            _lastHint = hint;
        }

        private static void OnRawLine(object sender, RawLineEventArgs e)
        {
            string arrow = e.Direction == RawLineDirection.Stdin ? ">>" : e.Direction == RawLineDirection.Stdout ? "<<" : "!!";
            ClaudeOutputPane.WriteLine(arrow + " " + e.Line);
        }

        private void Append(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _view.AppendPlain(text);
        }

        private void OnUi(Action action)
        {
            Jtf.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                action();
            }).FileAndForget("ClaudeVS/ChatControl/OnUi");
        }

        private void Fire(Func<System.Threading.Tasks.Task> action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Jtf.RunAsync(async () =>
            {
                try
                {
                    var t = action();
                    if (t != null) await t;
                }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ClaudeOutputPane.WriteLine("Exception: " + ex);
                    AppendError("[error] ", ex.Message);
                }
            }).FileAndForget("ClaudeVS/ChatControl/Fire");
        }

        private void DisposeSession()
        {
            if (_session == null) return;
            _session.EventReceived -= OnEvent;
            _session.RawLine -= OnRawLine;
            _session.Dispose();
            _session = null;
            _sessionDir = null;
            _permission.Visibility = Visibility.Collapsed;
        }

        public void Dispose()
        {
            _flushTimer.Stop();
            _routeTimer.Stop();
            DisposeSession();
        }
    }
}
