using System;
using System.Linq;
using System.Text;
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
    /// <summary>
    /// The chat panel. Built in code (no XAML) to keep the project simple; colors come from the
    /// VS theme so it works in Light, Dark and Blue in both VS 2022 and VS 2026.
    /// </summary>
    public sealed class ClaudeChatControl : UserControl, IDisposable
    {
        // Header: session title, history, new session.
        private readonly TextBlock _title = new TextBlock { Text = DefaultTitle, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _new = ChatTheme.IconButton(ChatTheme.IconMessage, "New session (a fresh context is cheapest)");
        private readonly Button _historyBtn = ChatTheme.IconButton(ChatTheme.IconHistory, "Sessions: resume an earlier conversation");
        // Input footer: + menu, / actions, model chip, mode chip, send/stop.
        private readonly Button _plusBtn = ChatTheme.IconButton(ChatTheme.IconAdd, "Add: upload files, context, web");
        private readonly Button _paletteBtn = ChatTheme.IconButton("/", "Actions (Ctrl+/)");
        private readonly Button _modelChip = ChatTheme.Chip("Model, effort and thinking");
        private readonly Button _modeChip = ChatTheme.Chip("Mode (Shift+Tab to switch)");
        private const string DefaultTitle = "New session";
        private ModelChoice _modelChoice;
        private EffortChoice _effortChoice = EffortChoice.Auto;
        private PermissionModeChoice _mode;
        private bool _thinking = true;
        private bool _browseWeb;
        private bool _busy;
        private ChatPopup _menu, _palette, _sessions, _contextPopup;
        private ContextUsage _contextUsage;
        private readonly Border _meterArea = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Click for what fills the context window" };
        private readonly StackPanel _emptyState = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24) };
        private readonly ProgressBar _ctxBar = new ProgressBar { Height = 2, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 5, 0, 0), BorderThickness = new Thickness(0), Background = ChatTheme.Subtle };
        private readonly TextBlock _meter = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _advice = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _adviceAction = Btn("", "");
        private readonly TranscriptView _view = new TranscriptView();
        private readonly StackPanel _permission = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
        private readonly TextBlock _permissionText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 400 };
        private readonly TextBlock _route = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 4) };
        private readonly TextBlock _attachment = new TextBlock { FontSize = 11, Visibility = Visibility.Collapsed, Margin = new Thickness(2, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
        private readonly TextBox _prompt = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 38, MaxHeight = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Padding = new Thickness(0), FontSize = 13,
        };
        private readonly TextBlock _placeholder = new TextBlock
        {
            Text = "Ask Claude to edit…",
            IsHitTestVisible = false, FontSize = 13, Margin = new Thickness(2, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        private readonly Border _inputBox = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 6, 6) };
        private readonly Button _send = ChatTheme.SendButton();

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
        private readonly ListBox _fileList = new ListBox { MaxHeight = 220, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, BorderThickness = new Thickness(0) };
        private readonly Popup _filePopup;
        private FileIndex _fileIndex;
        private string _fileIndexDir;
        private string _indexingDir;
        private AtMention _mention;
        private ClaudeEvent _pendingPermission;
        private string _adviceCommand;
        private string _turnModel;
        // Model families requested per sent prompt (null = Auto/default), matched to turns in order.
        private readonly Queue<string> _requestedFamilies = new Queue<string>();
        private bool _warnedModelMismatch;
        private string _lastHint;
        private readonly System.Collections.Generic.List<string> _attachedFiles = new System.Collections.Generic.List<string>();
        private System.Collections.Generic.IReadOnlyList<BuildError> _attachedErrors;
        private readonly Queue<UsageRecord> _pendingUsages = new Queue<UsageRecord>();
        internal static readonly UsageLog Usage = new UsageLog(UsageLog.DefaultPath);
        internal static readonly BookmarkStore Bookmarks = new BookmarkStore(BookmarkStore.DefaultPath);
        internal static readonly SessionMetaStore SessionMeta = new SessionMetaStore(SessionMetaStore.DefaultPath);
        // Name given to this conversation (title click or the sessions list); passed to the CLI as --name.
        private string _sessionName;
        private bool _showArchived;
        private readonly TextBox _titleEdit = new TextBox { Visibility = Visibility.Collapsed, FontWeight = FontWeights.SemiBold, FontSize = 13, Padding = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _bookmarksBtn = ChatTheme.IconButton(ChatTheme.IconBookmarks, "Bookmarks");
        private readonly BookmarksPanel _bookmarks = new BookmarksPanel();
        // Answer text of the turn in progress, and the prompts still waiting for their answer (sent while busy).
        private readonly StringBuilder _turnAnswer = new StringBuilder();
        private readonly Queue<(string Prompt, System.Windows.Documents.Block Anchor)> _turnPrompts = new Queue<(string, System.Windows.Documents.Block)>();
        // Bookmark id → its prompt bubble in this transcript, so the panel can scroll there.
        private readonly Dictionary<string, System.Windows.Documents.Block> _bookmarkAnchors = new Dictionary<string, System.Windows.Documents.Block>();

        public ClaudeChatControl()
        {
            _modelChoice = Options?.DefaultModel ?? ModelChoice.Auto;
            _mode = Options?.PermissionMode ?? PermissionModeChoice.AcceptEdits;
            Content = BuildLayout();
            this.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            this.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            ChatTheme.UseEnvironmentFont(this);
            ChatTheme.Text(_prompt);
            _prompt.SetResourceReference(TextBoxBase.CaretBrushProperty, EnvironmentColors.ToolWindowTextBrushKey); // default caret is black
            ChatTheme.Muted(_placeholder);
            ChatTheme.Muted(_meter);
            ChatTheme.Muted(_route);
            ChatTheme.Muted(_attachment);
            _ctxBar.SetResourceReference(ForegroundProperty, EnvironmentColors.SystemHighlightBrushKey);
            SetInputFocused(false);
            _prompt.GotKeyboardFocus += (s, e) => SetInputFocused(true);
            _prompt.LostKeyboardFocus += (s, e) => SetInputFocused(false);

            _send.Click += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_busy) Stop(); else SendFromBox();
            };
            _new.Click += (s, e) => NewChat();
            _historyBtn.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); ShowHistoryMenu(); };
            _bookmarksBtn.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); ToggleBookmarksPanel(); };
            _bookmarks.Close += (s, e) => { _bookmarks.Hide(); _prompt.Focus(); };
            _bookmarks.OpenRequested += (s, b) => { ThreadHelper.ThrowIfNotOnUIThread(); OpenBookmark(b); };
            _bookmarks.RemoveRequested += (s, b) => { ThreadHelper.ThrowIfNotOnUIThread(); RemoveBookmark(b.Id); };
            _plusBtn.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); ShowPlusMenu(); };
            _paletteBtn.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); ShowPalette(); };
            _modelChip.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); ShowModelMenu(); };
            _modeChip.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); ShowModeMenu(); };
            _paletteBtn.FontFamily = ChatTheme.Mono;
            _paletteBtn.FontSize = 13;
            _menu = new ChatPopup(_inputBox, withFilter: false, width: 320);
            _palette = new ChatPopup(_inputBox, withFilter: true, filterPlaceholder: "Filter actions…", width: 380);
            _sessions = new ChatPopup(_historyBtn, withFilter: true, filterPlaceholder: "Search sessions…", width: 340, placement: PlacementMode.Bottom);
            _contextPopup = new ChatPopup(_meterArea, withFilter: false, width: 360, placement: PlacementMode.Bottom);
            BuildEmptyState();
            UpdateChips();
            _adviceAction.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); if (_adviceCommand != null) SendText(_adviceCommand, routed: false); };
            _prompt.PreviewKeyDown += OnPromptKeyDown;
            _prompt.TextChanged += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _placeholder.Visibility = _prompt.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                _routeTimer.Stop();
                _routeTimer.Start();
                UpdateFilePicker();
            };
            _filePopup = BuildFilePicker();
            GotKeyboardFocus += (s, e) => LastActive = this;
            LastActive = LastActive ?? this;

            _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (s, e) => FlushText(), Dispatcher);
            _routeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (s, e) => { _routeTimer.Stop(); UpdateRouteHint(); }, Dispatcher);
            _flushTimer.Stop();
            _routeTimer.Stop();

            ResetMeter();
            SetEmptyState(true);
        }

        private static ClaudeOptionsPage Options => ClaudeVSPackage.Instance?.Options;

        /// <summary>Tool window instance id (0 = the first "Claude Code" window).</summary>
        public int InstanceId { get; set; }

        /// <summary>The chat the user interacted with last; editor commands send to it.</summary>
        public static ClaudeChatControl LastActive { get; private set; }

        private static Microsoft.VisualStudio.Threading.JoinableTaskFactory Jtf => ClaudeVSPackage.Instance?.JoinableTaskFactory ?? ThreadHelper.JoinableTaskFactory;

        // ---------------- Layout ----------------

        /// <summary>
        /// Layout modelled on the Claude Code panel in VS Code:
        ///   header   — meter line on the left, icon actions on the right, a 2px context bar, /compact advice
        ///   middle   — transcript
        ///   bottom   — permission bar, attachments, route hint, then one rounded input box whose footer holds
        ///              the model / effort pickers and the round send button.
        /// </summary>
        private UIElement BuildLayout()
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(_bookmarksBtn);
            actions.Children.Add(_historyBtn);
            actions.Children.Add(_new);

            var header = new DockPanel();
            DockPanel.SetDock(actions, Dock.Right);
            header.Children.Add(actions);
            var titleArea = new Grid();
            titleArea.Children.Add(_title);
            titleArea.Children.Add(_titleEdit);
            header.Children.Add(titleArea);
            ChatTheme.Text(_title);
            _title.Cursor = Cursors.IBeam;
            _title.MouseLeftButtonUp += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); StartTitleEdit(); };
            ChatTheme.Text(_titleEdit);
            _titleEdit.Background = ChatTheme.Subtle;
            _titleEdit.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.SystemHighlightBrushKey);
            _titleEdit.SetResourceReference(TextBoxBase.CaretBrushProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _titleEdit.KeyDown += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (e.Key == Key.Enter) { e.Handled = true; EndTitleEdit(save: true); }
                else if (e.Key == Key.Escape) { e.Handled = true; EndTitleEdit(save: false); }
            };
            _titleEdit.LostKeyboardFocus += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); EndTitleEdit(save: true); };
            _meter.Margin = new Thickness(0, 4, 0, 0);

            var adviceRow = new DockPanel();
            _adviceAction.Visibility = Visibility.Collapsed;
            _adviceAction.Margin = new Thickness(6, 4, 0, 0);
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

            var top = new StackPanel { Margin = new Thickness(10, 6, 6, 4) };
            top.Children.Add(header);
            var meterStack = new StackPanel();
            meterStack.Children.Add(_meter);
            meterStack.Children.Add(_ctxBar);
            _meterArea.Child = meterStack; // meter line and bar together: click for the breakdown
            _meterArea.MouseLeftButtonUp += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); ShowContextBreakdown(); };
            top.Children.Add(_meterArea);
            top.Children.Add(adviceRow);

            // Input box: text (with placeholder) on top; footer: + / model … mode send.
            var textArea = new Grid();
            textArea.Children.Add(_prompt);
            textArea.Children.Add(_placeholder);
            var footer = new DockPanel { Margin = new Thickness(-4, 6, 0, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(_modeChip);
            right.Children.Add(_send);
            _send.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(right, Dock.Right);
            footer.Children.Add(right);
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(_plusBtn);
            left.Children.Add(_paletteBtn);
            left.Children.Add(_modelChip);
            footer.Children.Add(left);
            var inputStack = new DockPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            inputStack.Children.Add(footer);
            inputStack.Children.Add(textArea);
            _inputBox.Child = inputStack;
            _inputBox.Background = ChatTheme.Subtle;
            _inputBox.MouseLeftButtonDown += (s, e) => _prompt.Focus(); // clicking the box's padding focuses the text

            var bottom = new StackPanel { Margin = new Thickness(10, 4, 10, 10) };
            bottom.Children.Add(_permission);
            bottom.Children.Add(_attachment);
            bottom.Children.Add(_route);
            bottom.Children.Add(_inputBox);

            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(top);
            root.Children.Add(bottom);
            _view.Box.Margin = new Thickness(10, 0, 6, 0);
            var transcript = new Grid();
            transcript.Children.Add(_view.Box);
            transcript.Children.Add(_emptyState); // shown over the transcript until the first message
            var middle = new DockPanel();
            DockPanel.SetDock(_bookmarks.View, Dock.Right);
            middle.Children.Add(_bookmarks.View);
            middle.Children.Add(transcript);
            root.Children.Add(middle);
            return root;
        }

        /// <summary>The empty-session message, like VS Code's (with our own icon, not Anthropic's logo or mascot).</summary>
        private void BuildEmptyState()
        {
            var icon = new TextBlock { Text = ChatTheme.IconMessage, FontFamily = ChatTheme.IconFont, FontSize = 34, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
            icon.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemHighlightBrushKey);
            var text = new TextBlock { Text = "What to do first? Ask about this codebase or we can start writing code.", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 280, FontSize = 13 };
            ChatTheme.Text(text);
            var tips = new TextBlock { Text = "@ attach a file  ·  / actions  ·  Shift+Tab mode  ·  Ctrl+↑ history", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 300, FontSize = 11, Margin = new Thickness(0, 10, 0, 0) };
            ChatTheme.Muted(tips);
            _emptyState.Children.Add(icon);
            _emptyState.Children.Add(text);
            _emptyState.Children.Add(tips);
            _emptyState.IsHitTestVisible = false;
        }

        private void SetEmptyState(bool empty) => _emptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Model chip ("Auto", "Sonnet · High", "… · no thinking") and mode chip.</summary>
        private void UpdateChips()
        {
            string model = _modelChoice == ModelChoice.Auto ? "Auto" : _modelChoice.ToString();
            string effort = _effortChoice == EffortChoice.Auto ? null : _effortChoice.ToString();
            string label = model + (effort != null ? " · " + effort : "") + (_thinking ? "" : " · no thinking");
            ChatTheme.SetChip(_modelChip, null, label);
            var (modeLabel, _) = _mode.Describe();
            ChatTheme.SetChip(_modeChip, ChatTheme.ModeGlyph(_mode), modeLabel);
            _modeChip.ToolTip = _mode.Describe().Description + " (Shift+Tab to switch)";
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _send.Content = busy ? ChatTheme.IconStop : ChatTheme.IconSend;
            _send.ToolTip = busy ? "Stop" : "Send (Enter)";
            System.Windows.Automation.AutomationProperties.SetName(_send, busy ? "Stop" : "Send");
        }

        private void Stop()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Fire(() => _session?.InterruptAsync());
        }

        private void SetTitle(string text)
        {
            string t = (text ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
            _fullTitle = t.Length == 0 ? null : t;
            _title.Text = t.Length == 0 ? DefaultTitle : t.Length > 70 ? t.Substring(0, 69) + "…" : t;
            _title.ToolTip = (t.Length > 70 ? t + "\n" : "") + "Click to rename";
        }

        private string _fullTitle;

        /// <summary>
        /// Saves the whole conversation as Markdown. Read from the CLI's session file, so it includes messages from
        /// before a resume and from before a compaction, not just what this window shows.
        /// </summary>
        private void ExportConversation()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string id = _session?.SessionId ?? _resumeId;
            if (id == null) { Append("Nothing to export yet: send a prompt first."); return; }
            string dir = _sessionDir ?? VsContext.GetSolutionDirectory() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string source = SessionHistory.SessionFile(null, dir, id);
            if (!System.IO.File.Exists(source)) { Append("The conversation is not saved by Claude Code yet (no session file). Try again after the first answer."); return; }

            string title = _fullTitle;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export conversation as Markdown",
                FileName = ConversationExport.FileName(title),
                DefaultExt = ".md",
                Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
                InitialDirectory = dir,
            };
            if (dlg.ShowDialog() != true) return;
            string target = dlg.FileName;

            Jtf.RunAsync(async () =>
            {
                string error = null;
                int items = 0;
                try
                {
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        var transcript = SessionTranscript.Read(source, maxItems: int.MaxValue);
                        items = transcript.Items.Count;
                        System.IO.File.WriteAllText(target, ConversationExport.ToMarkdown(transcript, title, DateTime.Now), new UTF8Encoding(false));
                    });
                }
                catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { error = ex.Message; }
                await Jtf.SwitchToMainThreadAsync();
                if (error != null) { AppendError("[export] ", error); return; }
                Append($"Exported {items} items to {target}");
                try { VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, target); }
                catch (Exception ex) { ClaudeOutputPane.WriteLine("Could not open the export: " + ex.Message); }
            }).FileAndForget("ClaudeVS/ChatControl/Export");
        }

        // ---------------- Rewind ----------------

        /// <summary>The Rewind button for a prompt bubble; null (no button) without a message id or with checkpoints off.</summary>
        private Action RewindAction(string messageId, string prompt)
        {
            if (string.IsNullOrEmpty(messageId) || Options?.FileCheckpoints == false) return null;
            return () => { ThreadHelper.ThrowIfNotOnUIThread(); RewindTo(messageId, prompt); };
        }

        /// <summary>
        /// Puts the files Claude changed since <paramref name="prompt"/> back the way they were: a dry run lists the
        /// files, you confirm, then the CLI restores them from its checkpoints. The conversation is left as it is.
        /// </summary>
        private void RewindTo(string messageId, string prompt)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_busy) { Append("Stop Claude first (Esc), then rewind."); return; }
            string dir = _sessionDir ?? VsContext.GetSolutionDirectory() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            EnsureSession(dir); // a resumed conversation has no process yet; starting one makes no model call
            var session = _session;
            string shortPrompt = SessionMetaStore.Clean(prompt) ?? "";
            if (shortPrompt.Length > 60) shortPrompt = shortPrompt.Substring(0, 59) + "…";

            Jtf.RunAsync(async () =>
            {
                var dry = await session.RewindFilesAsync(messageId, dryRun: true);
                await Jtf.SwitchToMainThreadAsync();
                if (session != _session) return;
                if (!dry.Ok) { AppendError("[rewind] ", RewindError(dry.Error)); return; }
                if (dry.FilesChanged.Count == 0) { Append($"Nothing to rewind: Claude has not changed any files since \"{shortPrompt}\"."); return; }

                string files = string.Join("\n", dry.FilesChanged.Take(15).Select(f => "  " + PromptContextBuilder.Relative(dir, f)))
                             + (dry.FilesChanged.Count > 15 ? $"\n  … and {dry.FilesChanged.Count - 15} more" : "");
                int answer = VsShellUtilities.ShowMessageBox(ClaudeVSPackage.Instance ?? (IServiceProvider)ServiceProvider.GlobalProvider,
                    $"Put these files back the way they were before \"{shortPrompt}\"?\n\n{files}\n\n({dry.Summary()}). Later changes to them, also your own, are undone too. "
                    + "The conversation is not changed, so tell Claude what you rewound.",
                    "Rewind file changes",
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_QUERY,
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
                if (answer != 1 /* IDOK */) return;

                var done = await session.RewindFilesAsync(messageId, dryRun: false);
                await Jtf.SwitchToMainThreadAsync();
                if (!done.Ok) { AppendError("[rewind] ", RewindError(done.Error)); return; }
                Append($"↶ Rewound {dry.Summary()} to before \"{shortPrompt}\". Claude still remembers its edits; tell it what you rewound.");
                ClaudeOutputPane.WriteLine("Rewound: " + string.Join(", ", dry.FilesChanged));
            }).FileAndForget("ClaudeVS/ChatControl/Rewind");
        }

        private static string RewindError(string error)
        {
            if (error != null && error.IndexOf("not enabled", StringComparison.OrdinalIgnoreCase) >= 0)
                return error + " Turn on Tools → Options → Claude Code → File checkpoints, then start a new session.";
            if (error != null && error.IndexOf("No file checkpoint", StringComparison.OrdinalIgnoreCase) >= 0)
                return error + " Checkpoints exist only for prompts sent while file checkpoints were on.";
            return error ?? "Rewind failed.";
        }

        // ---------------- Context usage breakdown ----------------

        /// <summary>
        /// Asks the running CLI what fills the context (get_context_usage: no model call, nothing billed) and keeps
        /// the answer for the breakdown popup; <paramref name="show"/> opens the popup when it arrives.
        /// </summary>
        private void RefreshContextUsage(bool show)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var s = _session;
            if (s == null || !s.IsRunning) { if (show) ShowContextBreakdownNow(); return; }
            Jtf.RunAsync(async () =>
            {
                ContextUsage u = null;
                string error = null;
                try { u = await s.GetContextUsageAsync(); }
                catch (InvalidOperationException ex) { error = ex.Message; } // an older CLI without get_context_usage
                await Jtf.SwitchToMainThreadAsync();
                if (s != _session) return;
                if (error != null) ClaudeOutputPane.WriteLine("Context usage not available: " + error);
                if (u != null)
                {
                    _contextUsage = u;
                    _meterArea.ToolTip = ContextTooltip(u);
                }
                if (show) ShowContextBreakdownNow();
            }).FileAndForget("ClaudeVS/ChatControl/ContextUsage");
        }

        private void ShowContextBreakdown()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_busy || _session?.IsRunning != true) ShowContextBreakdownNow(); // while answering, show the last one instead of waiting
            else RefreshContextUsage(show: true);
        }

        /// <summary>The breakdown popup: used categories, what is in "Messages", reserved space, and tips.</summary>
        private void ShowContextBreakdownNow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var u = _contextUsage;
            var entries = new List<ChatPopup.Entry>();
            if (u == null)
            {
                entries.Add(new ChatPopup.Entry { Label = "No breakdown yet", Description = "It comes from the running Claude process, so it appears after the first answer.", Enabled = false });
                _contextPopup.Footer = null;
                _contextPopup.Show(entries, _meterArea);
                return;
            }

            string Size(long tokens) => $"{TokenMeter.Format(tokens)} · {u.PercentOfWindow(tokens):0.#}%";
            string window = $"In use: {TokenMeter.Format(u.TotalTokens)} of {TokenMeter.Format(u.MaxTokens)} ({u.Percentage:0}%)";
            foreach (var c in u.Categories.Where(c => c.IsUsed && c.Tokens > 0).OrderByDescending(c => c.Tokens))
                entries.Add(new ChatPopup.Entry { Group = window, Label = c.Name, Detail = Size(c.Tokens) });
            foreach (var p in u.MessageParts())
                entries.Add(new ChatPopup.Entry { Group = "Inside Messages", Label = p.Key, Detail = Size(p.Value) });
            foreach (var c in u.Categories.Where(c => !c.IsUsed && c.Tokens > 0))
                entries.Add(new ChatPopup.Entry
                {
                    Group = "Not in use",
                    Label = c.Name,
                    Description = c.Kind == "deferred" ? "Loaded only when Claude needs it" : c.Kind == "buffer" ? "Kept free so autocompact has room" : null,
                    Detail = Size(c.Tokens),
                });
            foreach (var tip in u.Tips())
            {
                int dot = tip.IndexOf(". ", StringComparison.Ordinal); // first sentence as the label ("1.2k" has no space after its dot)
                entries.Add(new ChatPopup.Entry { Group = "Tips", Glyph = ChatTheme.IconLightbulb, Label = dot < 0 ? tip : tip.Substring(0, dot), Description = dot < 0 ? null : tip.Substring(dot + 2) });
            }
            if (u.Percentage >= 30)
                entries.Add(new ChatPopup.Entry { Group = "Tips", Glyph = ChatTheme.IconCompact, Label = "Compact now", Description = "Summarize the conversation to free space", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SendText("/compact", routed: false); } });

            _contextPopup.Footer = (u.IsAutoCompactEnabled && u.AutoCompactThreshold > 0 ? $"Autocompact at {TokenMeter.Format(u.AutoCompactThreshold)} · " : "")
                                 + (_busy ? "as of the last answer" : "from Claude Code, no model call");
            _contextPopup.Show(entries, _meterArea);
        }

        private static string ContextTooltip(ContextUsage u)
        {
            var lines = u.Categories.Where(c => c.IsUsed && c.Tokens > 0).OrderByDescending(c => c.Tokens).Take(5)
                .Select(c => $"{c.Name}: {TokenMeter.Format(c.Tokens)}");
            return $"Context {TokenMeter.Format(u.TotalTokens)} of {TokenMeter.Format(u.MaxTokens)}\n" + string.Join("\n", lines) + "\nClick for the full breakdown";
        }

        private void StartTitleEdit()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _titleEdit.Text = _sessionName ?? (_title.Text == DefaultTitle ? "" : _title.Text);
            _title.Visibility = Visibility.Hidden;
            _titleEdit.Visibility = Visibility.Visible;
            _titleEdit.Focus();
            _titleEdit.SelectAll();
        }

        private void EndTitleEdit(bool save)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_titleEdit.Visibility != Visibility.Visible) return;
            _titleEdit.Visibility = Visibility.Collapsed;
            _title.Visibility = Visibility.Visible;
            if (save) RenameCurrentSession(_titleEdit.Text);
        }

        /// <summary>
        /// Names this conversation. Saved locally right away when the CLI session id is known (otherwise when the
        /// session starts) and passed to the CLI with --name the next time the process starts.
        /// </summary>
        private void RenameCurrentSession(string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            name = SessionMetaStore.Clean(name);
            if (name == _sessionName) return;
            _sessionName = name;
            SetTitle(name ?? _title.Text);
            string id = _session?.SessionId ?? _resumeId;
            if (id != null) SaveSessionMeta(() => SessionMeta.SetName(id, name));
        }

        private void SaveSessionMeta(Action save)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { save(); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { AppendError("[sessions] ", ex.Message); }
        }

        // ---------------- Menus: + , / , model, mode, sessions ----------------

        private void ShowPlusMenu()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _menu.Footer = null;
            _menu.Show(new[]
            {
                new ChatPopup.Entry { Glyph = ChatTheme.IconUpload, Label = "Upload from computer", Description = "Attach files or images as references", Run = UploadFiles },
                new ChatPopup.Entry { Glyph = ChatTheme.IconDocument, Label = "Add context", Description = "Mention a file from this solution (@)", Run = StartMention },
                new ChatPopup.Entry { Glyph = ChatTheme.IconEdit, Label = "Attach current file", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); AttachFile(VsContext.GetActiveDocumentPath()); } },
                new ChatPopup.Entry { Glyph = ChatTheme.IconWarning, Label = "Attach Error List", Run = AttachErrorListFromIde },
                new ChatPopup.Entry { Glyph = ChatTheme.IconGlobe, Label = "Browse the web", Description = "Let Claude search the web for the next prompts", Checked = _browseWeb, Run = () => { _browseWeb = !_browseWeb; UpdateAttachments(focus: true); } },
            }, _plusBtn);
        }

        private void ShowModelMenu()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _menu.Footer = "Auto picks the cheapest model likely to handle each prompt";
            _menu.Show(ModelEntries(), _modelChip);
        }

        private IEnumerable<ChatPopup.Entry> ModelEntries()
        {
            foreach (ModelChoice m in Enum.GetValues(typeof(ModelChoice)))
                yield return new ChatPopup.Entry { Group = "Model", Label = m == ModelChoice.Auto ? "Auto (route each prompt)" : m.ToString(), Checked = _modelChoice == m, Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SetModel(m); } };
            foreach (EffortChoice ef in Enum.GetValues(typeof(EffortChoice)))
                yield return new ChatPopup.Entry { Group = "Effort", Label = ef == EffortChoice.Auto ? "Auto (follow the model choice)" : ef.ToString(), Checked = _effortChoice == ef, Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SetEffort(ef); } };
            yield return new ChatPopup.Entry { Group = "Thinking", Label = "Extended thinking", Description = "Off answers faster and cheaper on simple tasks", Checked = _thinking, Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SetThinking(!_thinking); } };
        }

        private void ShowModeMenu()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _menu.Footer = "Shift+Tab to switch";
            _menu.Show(ModeEntries(null), _modeChip);
        }

        private IEnumerable<ChatPopup.Entry> ModeEntries(string group) =>
            ChoiceNames.CycleOrder.Select(m => new ChatPopup.Entry
            {
                Group = group,
                Glyph = ChatTheme.ModeGlyph(m),
                Label = m.Describe().Label,
                Description = m.Describe().Description,
                Checked = _mode == m,
                Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SetMode(m); },
            });

        /// <summary>The / actions palette: context, model, mode, session and custom commands, filterable.</summary>
        private void ShowPalette()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RefreshCommands();
            var entries = new List<ChatPopup.Entry>
            {
                new ChatPopup.Entry { Group = "Context", Label = "Attach file…", Run = UploadFiles },
                new ChatPopup.Entry { Group = "Context", Label = "Mention file from this project…", Detail = "@", Run = StartMention },
                new ChatPopup.Entry { Group = "Context", Label = "Attach current file", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); AttachFile(VsContext.GetActiveDocumentPath()); } },
                new ChatPopup.Entry { Group = "Context", Label = "Attach selection", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); AttachSelection(VsContext.GetSelection()); } },
                new ChatPopup.Entry { Group = "Context", Label = "Attach Error List", Run = AttachErrorListFromIde },
                new ChatPopup.Entry { Group = "Context", Label = "Clear conversation", Description = "Start a new session", Run = NewChat },
                new ChatPopup.Entry { Group = "Context", Label = "Context usage", Description = "What fills the context window", Run = ShowContextBreakdown },
                new ChatPopup.Entry { Group = "Context", Label = "Compact conversation", Description = "Summarize to shrink the context", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SendText("/compact", routed: false); } },
                new ChatPopup.Entry { Group = "Context", Label = "Resume a session…", Run = ShowHistoryMenu },
                new ChatPopup.Entry { Group = "Context", Label = "Rename session", Run = StartTitleEdit },
                new ChatPopup.Entry { Group = "Context", Label = "Export conversation…", Description = "Save the whole conversation as Markdown", Run = ExportConversation },
                new ChatPopup.Entry { Group = "Context", Label = "Bookmarks", Description = "Answers you starred", Run = ToggleBookmarksPanel },
            };
            entries.AddRange(ModelEntries().Select(e => { e.Group = "Model · " + e.Group; return e; }));
            entries.AddRange(ModeEntries("Mode"));
            entries.AddRange(_commands.Where(c => !string.Equals(c.Name, "compact", StringComparison.OrdinalIgnoreCase)).Select(c => new ChatPopup.Entry
            {
                Group = "Commands",
                Label = "/" + c.Name,
                Description = c.Description,
                Detail = c.Source,
                Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SendText("/" + c.Name, routed: false); },
            }));
            entries.Add(new ChatPopup.Entry { Group = "Other", Label = "Show savings report", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); VsContext.ExecuteCommand("Tools.ShowSavingsReport"); } });
            entries.Add(new ChatPopup.Entry { Group = "Other", Label = "Open Output → Claude Code", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); ClaudeOutputPane.Activate(); } });
            entries.Add(new ChatPopup.Entry { Group = "Other", Glyph = null, Label = "Settings", Detail = "Tools → Options", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); ClaudeVSPackage.Instance?.ShowOptionPage(typeof(ClaudeOptionsPage)); } });
            _palette.Show(entries, _inputBox);
        }

        private void UploadFiles()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Attach files for Claude (sent as references)", InitialDirectory = VsContext.GetSolutionDirectory() ?? "" };
            if (dlg.ShowDialog() != true) return;
            foreach (var f in dlg.FileNames) AttachFile(f);
        }

        /// <summary>Puts "@" at the caret, which opens the file picker.</summary>
        private void StartMention()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int at = _prompt.CaretIndex;
            string before = at > 0 && !char.IsWhiteSpace(_prompt.Text[at - 1]) ? " @" : "@";
            _prompt.Text = _prompt.Text.Insert(at, before);
            _prompt.CaretIndex = at + before.Length;
            _prompt.Focus();
            UpdateFilePicker();
        }

        private void AttachErrorListFromIde()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var errors = VsContext.GetErrors();
            if (errors.Count == 0) { Append("The Error List has no errors to attach."); return; }
            AttachErrors(errors);
        }

        private void SetModel(ModelChoice m)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _modelChoice = m;
            UpdateChips();
            UpdateRouteHint();
        }

        private void SetEffort(EffortChoice e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _effortChoice = e;
            UpdateChips();
        }

        private void SetThinking(bool on)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _thinking = on;
            UpdateChips();
            var s = _session;
            if (s != null) Fire(() => s.SetThinkingAsync(on));
        }

        /// <summary>Changes the permission mode now; a running session switches without a restart.</summary>
        private void SetMode(PermissionModeChoice m)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (m == _mode) return;
            _mode = m;
            UpdateChips();
            var s = _session;
            if (s != null) Fire(() => s.SetPermissionModeAsync(m));
            ClaudeOutputPane.WriteLine("Mode: " + m.ToControl());
        }

        /// <summary>Input box border: accent color while typing, the normal border otherwise.</summary>
        private void SetInputFocused(bool focused) =>
            _inputBox.SetResourceReference(Border.BorderBrushProperty, focused ? EnvironmentColors.SystemHighlightBrushKey : EnvironmentColors.ToolWindowBorderBrushKey);

        private static Button Btn(string text, string tip) => ChatTheme.TextButton(text, tip);

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
            if (_browseWeb) parts.Add("🌐 web search allowed");

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
            if (_filePopup.IsOpen && HandleFilePickerKey(e.Key)) { e.Handled = true; return; }
            if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                e.Handled = true; // Shift+Tab cycles the mode, like VS Code
                SetMode(_mode.Next());
                return;
            }
            if (e.Key == Key.Oem2 && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                e.Handled = true; // Ctrl+/ opens the actions palette
                ShowPalette();
                return;
            }
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
            else if (e.Key == Key.Escape && _busy)
            {
                e.Handled = true; // Esc stops the answer, like the CLI
                Stop();
            }
        }

        // ---------------- @ file picker ----------------

        private Popup BuildFilePicker()
        {
            _fileList.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _fileList.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _fileList.MouseDoubleClick += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); PickFile(); };
            var border = new Border { BorderThickness = new Thickness(1), Child = _fileList };
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            return new Popup { PlacementTarget = _prompt, Placement = PlacementMode.Top, StaysOpen = false, Child = border };
        }

        /// <summary>Shows matching solution files while an "@query" is being typed at the caret.</summary>
        private void UpdateFilePicker()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _mention = AtMention.Find(_prompt.Text, _prompt.CaretIndex);
            if (_mention == null) { _filePopup.IsOpen = false; return; } // common case: no DTE call per keystroke
            string dir = VsContext.GetSolutionDirectory();
            if (dir == null) { _filePopup.IsOpen = false; return; }

            if (_fileIndex == null || !string.Equals(_fileIndexDir, dir, StringComparison.OrdinalIgnoreCase))
            {
                StartFileIndex(dir);
                _fileList.ItemsSource = new[] { "Indexing solution files…" };
                _fileList.IsEnabled = false;
                _filePopup.IsOpen = true;
                return;
            }

            var hits = _fileIndex.Search(_mention.Query, 10);
            _fileList.IsEnabled = hits.Count > 0;
            _fileList.ItemsSource = hits.Count > 0 ? (System.Collections.IEnumerable)hits : new[] { "No matching file" };
            _fileList.SelectedIndex = hits.Count > 0 ? 0 : -1;
            _filePopup.Width = Math.Max(260, _prompt.ActualWidth);
            _filePopup.IsOpen = true;
        }

        private void StartFileIndex(string dir)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_indexingDir != null && string.Equals(_indexingDir, dir, StringComparison.OrdinalIgnoreCase)) return;
            _indexingDir = dir;
            Jtf.RunAsync(async () =>
            {
                var index = await System.Threading.Tasks.Task.Run(() => FileIndex.Build(dir));
                await Jtf.SwitchToMainThreadAsync();
                _indexingDir = null;
                _fileIndex = index;
                _fileIndexDir = dir;
                ClaudeOutputPane.WriteLine($"Indexed {index.Count} files for @ picker{(index.Truncated ? " (capped)" : "")}");
                UpdateFilePicker();
            }).FileAndForget("ClaudeVS/ChatControl/FileIndex");
        }

        private bool HandleFilePickerKey(Key key)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int count = _fileList.IsEnabled ? _fileList.Items.Count : 0;
            switch (key)
            {
                case Key.Down when count > 0:
                    _fileList.SelectedIndex = (_fileList.SelectedIndex + 1) % count;
                    _fileList.ScrollIntoView(_fileList.SelectedItem);
                    return true;
                case Key.Up when count > 0:
                    _fileList.SelectedIndex = (_fileList.SelectedIndex - 1 + count) % count;
                    _fileList.ScrollIntoView(_fileList.SelectedItem);
                    return true;
                case Key.Enter when count > 0:
                case Key.Tab when count > 0:
                    PickFile();
                    return true;
                case Key.Escape:
                    _filePopup.IsOpen = false;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Removes the "@query" text and attaches the chosen file as a path reference.</summary>
        private void PickFile()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(_fileList.SelectedItem is string rel) || _mention == null || _fileIndex == null || !_fileList.IsEnabled) return;
            var m = _mention;
            _filePopup.IsOpen = false;
            _prompt.Text = _prompt.Text.Remove(m.Start, m.Length);
            _prompt.CaretIndex = m.Start;
            AttachFile(_fileIndex.FullPath(rel));
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
            if (routed && _browseWeb) built.Text = "You may search the web if it helps with this request.\n\n" + built.Text;
            ClearAttachments();
            SetEmptyState(false);
            if (_title.Text == DefaultTitle && routed) SetTitle(userText);
            SetBusy(true);

            Fire(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                EnsureSession(dir);

                ModelChoice model = _modelChoice;
                EffortChoice effort = _effortChoice;
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

                _pendingUsages.Enqueue(new UsageRecord
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
                });

                // Show the message right away; switching model/effort talks to the CLI first.
                var meta = new List<string>();
                if (routeNote != null) meta.Add(routeNote);
                if (built.ReferencedFiles > 0) meta.Add($"{built.ReferencedFiles} file reference(s)");
                if (built.InlinedLines > 0) meta.Add($"{built.InlinedLines} lines inlined");
                if (built.AttachedErrors > 0) meta.Add($"{built.AttachedErrors} error(s)");
                if (built.CharsAvoided > 0) meta.Add($"~{built.CharsAvoided / 4:N0} tokens of file content not sent");
                // Our own id for the message, so its file changes can be rewound later (slash commands edit nothing).
                string messageId = userText.StartsWith("/") ? null : Guid.NewGuid().ToString();
                _view.AppendUserMessage(userText, string.Join("  ·  ", meta), RewindAction(messageId, userText));
                _turnPrompts.Enqueue((userText, _view.LastUserMessage));

                _requestedFamilies.Enqueue(model == ModelChoice.Auto ? null : model.ToCli());
                await _session.SetModelAsync(model);
                await _session.SetEffortAsync(effort);
                await _session.SendPromptAsync(built.Text, messageId);
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
            if (_modelChoice != ModelChoice.Auto || string.IsNullOrWhiteSpace(_prompt.Text) || _prompt.Text.TrimStart().StartsWith("/"))
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
                PermissionMode = _mode,
                AppendSystemPrompt = (o?.ConciseAnswers ?? true) ? ClaudeOptionsPage.BrevityPrompt : null,
                ExtraArguments = o?.ExtraArguments ?? "",
                ResumeSessionId = _resumeId,
                EnableFileCheckpointing = o?.FileCheckpoints ?? true,
                SessionName = _sessionName,
            });
            _resumeId = null;
            _sessionDir = dir;
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
                });
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

        private void ShowHistoryMenu(bool refresh)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dir = VsContext.GetSolutionDirectory() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var now = DateTime.UtcNow;
            List<SessionInfo> all;
            try { all = SessionMeta.Apply(SessionHistory.List(SessionHistory.DefaultProjectsRoot, dir, max: 80)); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { AppendError("[sessions] ", ex.Message); return; }
            string currentId = _session?.SessionId ?? _resumeId;

            var entries = all.Where(s => _showArchived || !s.Archived).Select(s => new ChatPopup.Entry
            {
                Group = s.Archived ? "Archived" : null,
                Label = s.Title,
                Detail = SessionHistory.TimeAgo(s.LastWriteUtc, now),
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
            }).OrderBy(e => e.Group == null ? 0 : 1).ToList(); // archived ones last, under their own header

            if (entries.Count == 0) entries.Add(new ChatPopup.Entry { Label = all.Count == 0 ? "No earlier sessions for this folder" : "All sessions are archived", Enabled = false });
            int archived = all.Count(s => s.Archived);
            if (archived > 0)
                entries.Add(new ChatPopup.Entry
                {
                    Glyph = ChatTheme.IconArchive,
                    Label = _showArchived ? "Hide archived sessions" : $"Show archived sessions ({archived})",
                    Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); _showArchived = !_showArchived; ShowHistoryMenu(refresh: true); },
                });
            _sessions.Footer = "Hover a session to rename or archive it";
            _sessions.Show(entries, _historyBtn, keepFilter: refresh);
        }

        /// <summary>
        /// The next prompt continues an earlier conversation (claude --resume). Its earlier messages are read from the
        /// CLI's session file and redrawn (prompts, answers, tool rows), off the UI thread.
        /// </summary>
        private void ResumeSession(SessionInfo s)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DisposeSession();
            ResetMeter();
            ClearTranscript();
            _resumeId = s.Id;
            _sessionName = SessionMeta.Get(s.Id)?.Name;
            SetBusy(false);
            SetEmptyState(false);
            SetTitle(s.Title);
            ClaudeOutputPane.WriteLine($"Next prompt resumes session {s.Id}");
            _prompt.Focus();

            int generation = ++_resumeGeneration;
            Jtf.RunAsync(async () =>
            {
                var transcript = await System.Threading.Tasks.Task.Run(() => SessionTranscript.Read(s.FilePath));
                await Jtf.SwitchToMainThreadAsync();
                if (generation != _resumeGeneration || _session != null) return; // another session was opened, or a prompt already sent
                RenderTranscript(transcript);
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
                        _view.AppendUserMessage(item.Text, null, RewindAction(item.Uuid, item.Text));
                        prompt = item.Text;
                        anchor = _view.LastUserMessage;
                        break;
                    case TranscriptItemKind.Answer:
                        _view.AppendAnswer(item.Text);
                        _view.EndAnswer();
                        // Earlier answers can be bookmarked too.
                        _view.AppendTurnFooter("", BookmarkToggle(prompt, item.Text, anchor));
                        break;
                    case TranscriptItemKind.Tool:
                        _view.AppendTool(item.ToolUseId, item.Text, item.ToolInput?.ToString());
                        if (item.ResultChars >= 0) _view.CompleteTool(item.ToolUseId, item.ResultChars, item.IsError);
                        break;
                    case TranscriptItemKind.Compacted:
                        Append("✂ Conversation compacted here; Claude sees a summary of what came before.");
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
            DisposeSession();
            ResetMeter();
            ClearTranscript();
            _resumeId = null;
            _sessionName = null;
            _resumeGeneration++; // drop a resumed transcript that is still being read
            RefreshCommands(force: true);
            _fileIndex = null; // re-index on the next @ (files may have been added)
            SetBusy(false);
            SetTitle(null);
            SetEmptyState(true);
        }

        // ---------------- Events from the CLI (background thread) ----------------

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
            string writeSnapshot = change?.IsWrite == true ? ReadSnapshot(change.FilePath) : null;

            _events.Enqueue(new QueuedEvent { Session = sender as ClaudeSession, Event = ev, Change = change, WriteSnapshot = writeSnapshot });
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
                switch (ev.Kind)
                {
                    case ClaudeEventKind.SessionStarted:
                        ClaudeOutputPane.WriteLine($"Session {ev.SessionId} started, model {ev.Model}");
                        string named = _sessionName;
                        if (named != null && ev.SessionId != null && SessionMeta.Get(ev.SessionId)?.Name != named)
                            SaveSessionMeta(() => SessionMeta.SetName(ev.SessionId, named)); // named before the session had an id
                        break;
                    case ClaudeEventKind.AssistantUsage:
                        if (!ev.IsSubagent && !string.IsNullOrEmpty(ev.Model)) _turnModel = ev.Model;
                        break;
                    case ClaudeEventKind.ToolUse:
                        FlushText();
                        if (_turnAnswer.Length > 0) _turnAnswer.Append("\n\n"); // text before and after a tool call are separate paragraphs
                        Action diff = null;
                        if (change != null) diff = () => { ThreadHelper.ThrowIfNotOnUIThread(); ShowDiff(change, writeSnapshot); };
                        _view.AppendTool(ev.ToolUseId, ev.Text, ev.ToolInput?.ToString(), diff);
                        break;
                    case ClaudeEventKind.ToolResult:
                        _view.CompleteTool(ev.ToolUseId, ev.ResultChars, ev.IsError);
                        break;
                    case ClaudeEventKind.PermissionRequest:
                        FlushText();
                        if (ShowInteractiveCard(ev)) break;
                        _pendingPermission = ev;
                        _permissionText.Text = "Allow " + ev.Text + "?";
                        _permission.Visibility = Visibility.Visible;
                        break;
                    case ClaudeEventKind.Compacted:
                        Append($"\n  ✂ Compacted ({TokenMeter.Format(ev.PreCompactTokens)} tokens before)\n");
                        break;
                    case ClaudeEventKind.TurnCompleted:
                        FlushText();
                        SetBusy(_pendingUsages.Count > 1); // still busy if more prompts were queued
                        if (ev.IsError && !string.IsNullOrEmpty(ev.Text)) AppendError("[error] ", ev.Text);
                        string used = _turnModel != null ? " · " + _turnModel : "";
                        CheckModelMismatch(_turnModel);
                        RecordUsage(ev, _turnModel);
                        RefreshContextUsage(show: false); // control request, no model call
                        _turnModel = null;
                        var turn = _turnPrompts.Count > 0 ? _turnPrompts.Dequeue() : default;
                        string answer = _turnAnswer.ToString();
                        _turnAnswer.Clear();
                        _view.AppendTurnFooter($"✓ {ev.DurationMs / 1000.0:0.0}s · {TokenMeter.Format(ev.Usage.Total)} tok · ${_meterModel?.LastTurnCostUsd:0.000}{used}",
                            string.IsNullOrWhiteSpace(answer) ? null : BookmarkToggle(turn.Prompt, answer, turn.Anchor));
                        break;
                    case ClaudeEventKind.Error:
                        AppendError("[claude] ", ev.Text);
                        break;
                }
            }
        }

        /// <summary>
        /// The CLI can accept a model (its init event reports it) and still answer with another one, for example
        /// in plan mode. Routing to a cheaper model then silently costs more, so say so once per conversation.
        /// </summary>
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
            // Prompts sent while Claude is still answering are queued by the CLI and finish in order.
            if (_pendingUsages.Count == 0) return;
            var rec = _pendingUsages.Dequeue();
            if (Options?.KeepUsageLog == false) return;
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
                Append($"  {note}\n");
                Fire(() => session.RespondToPermissionAsync(ev.RequestId, input ?? ev.ToolInput ?? new JObject(), allow, denyMessage));
            }

            if (ev.ToolName == AskUserQuestionTool.Name)
            {
                var questions = AskUserQuestionTool.Parse(ev.ToolInput);
                if (questions.Count == 0) return false;
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
                _view.AppendPlanCard(ExitPlanModeTool.Plan(ev.ToolInput),
                    () => { ThreadHelper.ThrowIfNotOnUIThread(); Respond(true, null, null, "✔ plan approved — Claude starts the work"); },
                    feedback => { ThreadHelper.ThrowIfNotOnUIThread(); Respond(false, null, ExitPlanModeTool.RevisionMessage(feedback), "✎ changes requested"); });
                return true;
            }
            return false;
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
            if (chunk.Length > 0)
            {
                _view.AppendAnswer(chunk);
                _turnAnswer.Append(chunk);
            }
            _flushTimer.Stop();
        }

        private void ClearTranscript()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _view.Clear();
            _bookmarkAnchors.Clear();
        }

        private void ClearTurns()
        {
            _turnPrompts.Clear();
            _turnAnswer.Clear();
        }

        // ---------------- Bookmarks ----------------

        private static string BookmarkSolution
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return VsContext.GetSolutionDirectory() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
        }

        /// <summary>The star under an answer: the first click saves a bookmark, the next one removes it.</summary>
        private Func<bool> BookmarkToggle(string prompt, string answer, System.Windows.Documents.Block anchor)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string id = null;
            string dir = _sessionDir ?? BookmarkSolution;          // no process yet when redrawing a resumed conversation
            string sessionId = _session?.SessionId ?? _resumeId;
            return () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    if (id != null)
                    {
                        RemoveBookmark(id);
                        id = null;
                        return false;
                    }
                    var b = Bookmark.Create(dir, sessionId, prompt, answer, DateTime.UtcNow);
                    Bookmarks.Add(b);
                    id = b.Id;
                    if (anchor != null) _bookmarkAnchors[id] = anchor;
                    RefreshBookmarksPanel();
                    return true;
                }
                catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
                {
                    AppendError("[bookmarks] ", ex.Message);
                    return id != null;
                }
            };
        }

        private void ToggleBookmarksPanel()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_bookmarks.IsOpen) { _bookmarks.Hide(); return; }
            _bookmarks.Show(Bookmarks.List(BookmarkSolution));
        }

        private void RefreshBookmarksPanel()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_bookmarks.IsOpen) _bookmarks.SetItems(Bookmarks.List(BookmarkSolution));
        }

        /// <summary>Scrolls to the answer when it is in this transcript; otherwise shows the saved copy at the end.</summary>
        private void OpenBookmark(Bookmark b)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_bookmarkAnchors.TryGetValue(b.Id, out var anchor) && _view.ScrollTo(anchor)) return;
            SetEmptyState(false);
            var head = _view.AppendBookmark(b);
            _bookmarkAnchors[b.Id] = head;
            // Scroll once the new blocks are laid out.
            Jtf.RunAsync(async () =>
            {
                await Jtf.SwitchToMainThreadAsync(alwaysYield: true);
                _view.Box.UpdateLayout();
                _view.ScrollTo(head);
            }).FileAndForget("ClaudeVS/ChatControl/OpenBookmark");
        }

        private void RemoveBookmark(string id)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { Bookmarks.Remove(id); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { AppendError("[bookmarks] ", ex.Message); return; }
            _bookmarkAnchors.Remove(id);
            RefreshBookmarksPanel();
        }

        private void AppendError(string prefix, string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _view.AppendPlain(prefix + text, error: true);
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
                    SetBusy(false);
                    AppendError("[error] ", ex.Message);
                }
            }).FileAndForget("ClaudeVS/ChatControl/Fire");
        }

        private void DisposeSession()
        {
            if (_session == null) return;
            var old = _session;
            old.EventReceived -= OnEvent;
            old.RawLine -= OnRawLine;
            _session = null;
            // Stopping the process can wait up to 2 s for a clean exit; don't block the UI thread for it.
            // Anything the old session still reports is ignored (DrainEvents and Exited check the session).
            _ = System.Threading.Tasks.Task.Run(() => old.Dispose());
            _sessionDir = null;
            _pendingUsages.Clear();
            _requestedFamilies.Clear();
            ClearTurns();
            _warnedModelMismatch = false;
            _contextUsage = null;
            _meterArea.ToolTip = "Click for what fills the context window";
            lock (_pendingText) _pendingText.Clear();
            _permission.Visibility = Visibility.Collapsed;
        }

        public void Dispose()
        {
            if (LastActive == this) LastActive = null;
            _flushTimer.Stop();
            _routeTimer.Stop();
            DisposeSession();
        }
    }
}
