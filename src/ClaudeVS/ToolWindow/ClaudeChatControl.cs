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
    /// <summary>
    /// The chat panel. Built in code (no XAML) to keep the project simple; colors come from the
    /// VS theme so it works in Light, Dark and Blue in both VS 2022 and VS 2026.
    /// </summary>
    public sealed partial class ClaudeChatControl : UserControl, IDisposable
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
        private ChatPopup _menu, _palette, _sessions, _contextPopup, _manage;
        private ContextUsage _contextUsage;
        private readonly Border _meterArea = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Click for what fills the context window" };
        private readonly StackPanel _emptyState = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24) };
        private readonly ProgressBar _ctxBar = new ProgressBar { Height = 2, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 5, 0, 0), BorderThickness = new Thickness(0), Background = ChatTheme.Subtle };
        private readonly TextBlock _meter = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _advice = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _adviceAction = Btn("", "");
        private readonly TranscriptView _view = new TranscriptView();
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
        private readonly Queue<TurnPrompt> _turnPrompts = new Queue<TurnPrompt>();
        // Prompts sent while Claude was answering: shown above the input box until Claude starts on them.
        private readonly Queue<TurnPrompt> _queuedBubbles = new Queue<TurnPrompt>();
        private readonly StackPanel _queuedPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };
        private sealed class TurnPrompt
        {
            public string Prompt;
            public System.Windows.Documents.Block Anchor;
            /// <summary>Draws the prompt bubble in the transcript (deferred for a queued prompt).</summary>
            public Action Render;
        }
        // Bookmark id → its prompt bubble in this transcript, so the panel can scroll there.
        private readonly Dictionary<string, System.Windows.Documents.Block> _bookmarkAnchors = new Dictionary<string, System.Windows.Documents.Block>();

        public ClaudeChatControl()
        {
            _modelChoice = Options?.DefaultModel ?? ModelChoice.Auto;
            _mode = Options?.PermissionMode ?? PermissionModeChoice.AcceptEdits;
            Content = BuildLayout();
            _view.ShowTimestamps = () => Options?.ShowTimestamps ?? true;
            _view.FocusView = () => Options?.FocusView ?? false;
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
            _manage = new ChatPopup(_inputBox, withFilter: true, filterPlaceholder: "Filter…", width: 400);
            InitFolderChip();
            BuildEmptyState();
            UpdateChips();
            _adviceAction.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); if (_adviceCommand != null) SendText(_adviceCommand, routed: false); };
            _prompt.PreviewKeyDown += OnPromptKeyDown;
            DataObject.AddPastingHandler(_prompt, OnPromptPasting);
            _prompt.TextChanged += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _placeholder.Visibility = _prompt.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (_prompt.Text.Length == 0) _pastes.Clear();
                _routeTimer.Stop();
                _routeTimer.Start();
                UpdateFilePicker();
                UpdateCommandPicker();
            };
            _filePopup = BuildFilePicker();
            _commandPopup = BuildCommandPicker();
            GotKeyboardFocus += (s, e) => LastActive = this;
            lock (Instances) Instances.Add(this);
            LastActive = LastActive ?? this;

            _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (s, e) => FlushText(), Dispatcher);
            _routeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (s, e) => { _routeTimer.Stop(); UpdateRouteHint(); }, Dispatcher);
            _flushTimer.Stop();
            _routeTimer.Stop();
            _stallTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (s, e) => CheckStalled(), Dispatcher);

            ResetMeter();
            SetEmptyState(true);
        }

        private static ClaudeOptionsPage Options => ClaudeVSPackage.Instance?.Options;

        /// <summary>Tool window instance id (0 = the first "Claude Code" window).</summary>
        public int InstanceId { get; set; }

        /// <summary>The chat the user interacted with last; editor commands send to it.</summary>
        public static ClaudeChatControl LastActive { get; private set; }

        /// <summary>All open chat windows (to recognize our own claude processes in the CLI's session registry).</summary>
        private static readonly List<ClaudeChatControl> Instances = new List<ClaudeChatControl>();

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

            BuildPermissionCard();

            var top = new StackPanel { Margin = new Thickness(10, 6, 6, 4) };
            top.Children.Add(header);
            top.Children.Add(_folderChip); // the working folder, under the title
            var meterStack = new StackPanel();
            meterStack.Children.Add(_meter);
            meterStack.Children.Add(_ctxBar);
            _meterArea.Child = meterStack; // meter line and bar together: click for the breakdown
            _meterArea.MouseLeftButtonUp += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); ShowContextBreakdown(); };
            top.Children.Add(_meterArea);
            top.Children.Add(adviceRow);
            top.Children.Add(_limitBanner);
            top.Children.Add(_signInBanner);
            top.Children.Add(_cacheNote);

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

            InitActivity();
            var bottom = new StackPanel { Margin = new Thickness(10, 4, 10, 10) };
            bottom.Children.Add(_activity);
            bottom.Children.Add(_agentsPanel);
            bottom.Children.Add(_stall);
            bottom.Children.Add(_queuedPanel);
            bottom.Children.Add(_permission);
            bottom.Children.Add(_imageStrip);
            bottom.Children.Add(_attachment);
            bottom.Children.Add(_route);
            bottom.Children.Add(_inputBox);
            InitAccessibility(bottom);

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
            ThreadHelper.ThrowIfNotOnUIThread();
            if (busy && !_busy) System.Threading.Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks); // the 60 s count starts now
            bool wasBusy = _busy;
            _busy = busy;
            UpdateCacheNote(); // only while idle
            if (!busy) SetStalled(false);
            OnBusyChangedActivity(busy, wasBusy);
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
            string dir = _sessionDir ?? WorkFolder;
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
        private Action RewindAction(string messageId, string prompt, DateTime? timeUtc = null)
        {
            if (string.IsNullOrEmpty(messageId) || Options?.FileCheckpoints == false) return null;
            _rewindPoints.Add((messageId, prompt, timeUtc ?? DateTime.UtcNow));
            return () => { ThreadHelper.ThrowIfNotOnUIThread(); RewindTo(messageId, prompt); };
        }

        // Prompts in this transcript that can be rewound, oldest first (for the Esc-Esc picker).
        private readonly List<(string Id, string Prompt, DateTime TimeUtc)> _rewindPoints = new List<(string, string, DateTime)>();
        private DateTime _lastEscUtc;

        /// <summary>Esc twice (or / → Rewind…): pick one of your prompts to put the files back to before it, newest first.</summary>
        private void ShowRewindPicker()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var now = DateTime.UtcNow;
            var entries = _rewindPoints.AsEnumerable().Reverse().Select(p => new ChatPopup.Entry
            {
                Glyph = ChatTheme.IconRewind,
                Label = SessionMetaStore.Clean(p.Prompt) ?? "(image)",
                Detail = SessionHistory.TimeAgo(p.TimeUtc, now),
                Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); RewindTo(p.Id, p.Prompt); },
            }).ToList();
            if (entries.Count == 0) entries.Add(new ChatPopup.Entry { Label = Options?.FileCheckpoints == false ? "File checkpoints are off (Tools → Options → Claude Code)" : "No prompts to rewind yet", Enabled = false });
            _manage.Footer = "Puts the files Claude changed since that prompt back; you confirm first";
            _manage.Show(entries, _inputBox);
        }

        /// <summary>
        /// Continues this conversation in a new session (a copy, <c>--fork-session</c>), leaving the original as it
        /// was, so you can try another direction. Takes effect with the next prompt.
        /// </summary>
        private void ForkSession(string sessionId, string title, bool keepTranscript)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(sessionId)) { Append("Nothing to fork yet: send a prompt first."); return; }
            if (_busy) { Append("Stop Claude first (Esc), then fork."); return; }
            DisposeSession();
            ResetMeter();
            if (!keepTranscript) ClearTranscript();
            _resumeId = sessionId;
            _forkNext = true;
            _sessionName = SessionMetaStore.Clean((title ?? "Session") + " (fork)");
            SetTitle(_sessionName);
            SetEmptyState(false);
            Append("Forked: the next prompt continues in a new session with this history; the original stays as it was.");
            ClaudeOutputPane.WriteLine($"Next prompt forks session {sessionId}");
            _prompt.Focus();
        }

        private bool _forkNext;

        /// <summary>
        /// Puts the files Claude changed since <paramref name="prompt"/> back the way they were: a dry run lists the
        /// files, you confirm, then the CLI restores them from its checkpoints. The conversation is left as it is.
        /// </summary>
        private void RewindTo(string messageId, string prompt)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_busy) { Append("Stop Claude first (Esc), then rewind."); return; }
            string dir = _sessionDir ?? WorkFolder;
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
                    UpdateMeterTooltip();
                    _meterModel?.ApplyContextUsage(u.TotalTokens, u.MaxTokens); // the CLI's own number, also right after /compact
                    BaseContextTip(u);
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
                entries.Add(new ChatPopup.Entry { Group = window, Label = c.Name, Detail = Size(c.Tokens), Description = CategoryAction(c) != null ? "Click to see them and turn off what you don't need" : null, Run = CategoryAction(c) });
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
            var limits = _rateLimits;
            if (limits != null)
                foreach (var w in limits.Windows)
                    entries.Add(new ChatPopup.Entry
                    {
                        Group = "Plan usage",
                        Label = RateLimitInfo.DisplayName(w.Name),
                        Detail = $"{Math.Round(w.Utilization * 100):0}%",
                        Description = w.ResetsAtUtc == null ? null : "Resets at " + RateLimitInfo.When(w.ResetsAtUtc.Value, DateTime.UtcNow),
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
            var prices = Options?.ToPriceTable() ?? new PriceTable();
            foreach (ModelChoice m in Enum.GetValues(typeof(ModelChoice)))
            {
                // A model dearer than Opus (Fable) says so: Auto never picks it, so this is the only place you choose it.
                string dearer = prices.VersusOpus(m.ToCli());
                yield return new ChatPopup.Entry
                {
                    Group = "Model",
                    Label = m == ModelChoice.Auto ? "Auto (route each prompt)" : m.ToString(),
                    Description = dearer != null ? $"{dearer}; Auto never picks it" : null,
                    Checked = _modelChoice == m,
                    Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SetModel(m); },
                };
            }
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
                new ChatPopup.Entry { Group = "Context", Label = "Skills…", Description = "Turn skills on or off, with what each costs", Run = ShowSkills },
                new ChatPopup.Entry { Group = "Context", Label = "MCP servers…", Description = "Turn servers on or off, reconnect", Run = ShowMcpServers },
                new ChatPopup.Entry { Group = "Context", Label = "Compact conversation", Description = "Summarize to shrink the context", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SendText("/compact", routed: false); } },
                new ChatPopup.Entry { Group = "Context", Label = "Resume a session…", Run = ShowHistoryMenu },
                new ChatPopup.Entry { Group = "Context", Label = "Rename session", Run = StartTitleEdit },
                new ChatPopup.Entry { Group = "Context", Label = "Fork this session", Description = "Continue in a new session with this history; the original stays", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); ForkSession(_session?.SessionId ?? _resumeId, _fullTitle, keepTranscript: true); } },
                new ChatPopup.Entry { Group = "Context", Label = "Rewind…", Detail = "Esc Esc", Description = "Put files back to before one of your prompts", Run = ShowRewindPicker },
                new ChatPopup.Entry { Group = "Context", Label = "Focus view", Description = "Collapse tool calls into one summary row", Checked = Options?.FocusView == true, Run = ToggleFocusView },
                new ChatPopup.Entry { Group = "Context", Label = "Copy last response", Description = "Claude's last answer as Markdown", Run = CopyLastAnswer },
                new ChatPopup.Entry { Group = "Context", Label = "Copy session summary", Description = "Turns, cost, tokens, cache and context as text", Run = CopySessionSummary },
                new ChatPopup.Entry { Group = "Context", Label = "Focus last message", Detail = "Ctrl+Alt+K, M", Description = "Move the keyboard to Claude's last answer (for screen readers)", Run = FocusLastMessage },
                new ChatPopup.Entry { Group = "Context", Label = "Export conversation…", Description = "Save the whole conversation as Markdown", Run = ExportConversation },
                new ChatPopup.Entry { Group = "Context", Label = "Bookmarks", Description = "Answers you starred", Run = ToggleBookmarksPanel },
            };
            entries.AddRange(ModelEntries().Select(e => { e.Group = "Model · " + e.Group; return e; }));
            entries.AddRange(ModeEntries("Mode"));
            entries.AddRange(_commands.Where(c => !string.Equals(c.Name, "compact", StringComparison.OrdinalIgnoreCase)).Select(c => new ChatPopup.Entry
            {
                Group = c.Source == "skill" ? "Skills" : "Commands",
                Label = "/" + c.Name,
                Description = c.Description,
                Detail = c.Source,
                Run = () =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (c.Source != "skill") { SendText("/" + c.Name, routed: false); return; }
                    // Skills usually take arguments: put the command in the prompt box to finish and send.
                    _prompt.Text = "/" + c.Name + " ";
                    _prompt.CaretIndex = _prompt.Text.Length;
                    _prompt.Focus();
                },
            }));
            entries.AddRange(DialogEntries());
            entries.Add(new ChatPopup.Entry { Group = "Other", Label = "Show savings report", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); VsContext.ExecuteCommand("Tools.ShowSavingsReport"); } });
            entries.Add(new ChatPopup.Entry { Group = "Other", Label = "Open Output → Claude Code", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); ClaudeOutputPane.Activate(); } });
            entries.Add(new ChatPopup.Entry { Group = "Other", Glyph = null, Label = "Settings", Detail = "Tools → Options", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); ClaudeVSPackage.Instance?.ShowOptionPage(typeof(ClaudeOptionsPage)); } });
            _palette.Show(entries, _inputBox);
        }

        private void UploadFiles()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Attach files for Claude (images are sent with the prompt, other files as references)", InitialDirectory = VsContext.GetSolutionDirectory() ?? "" };
            if (dlg.ShowDialog() != true) return;
            var images = dlg.FileNames.Where(f => ImageTokens.MediaTypeFor(f) != null).ToList();
            foreach (var f in dlg.FileNames.Except(images)) AttachFile(f);
            if (images.Count > 0) AddImages(() => images.Select(ImageAttachments.FromFile).ToList());
        }

        // ---------------- Images ----------------

        private const int MaxImages = 10;
        private readonly List<ImageAttachment> _images = new List<ImageAttachment>();
        private readonly WrapPanel _imageStrip = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };

        /// <summary>Ctrl+V / Shift+Insert with an image (or image files) on the clipboard attaches it instead of pasting text.</summary>
        private bool TryPasteImage(KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            bool paste = (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0) || (e.Key == Key.Insert && (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            if (!paste || !ImageAttachments.ClipboardHasImage()) return false;
            AddImages(ImageAttachments.FromClipboard);
            return true;
        }

        /// <summary>Encodes images (scaled to the API's 1568 px limit) and shows them above the input box.</summary>
        private void AddImages(Func<List<ImageAttachment>> load)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            List<ImageAttachment> added;
            try { added = load(); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException || ex is System.IO.FileFormatException)
            {
                AppendError("[image] ", ex.Message);
                return;
            }
            foreach (var img in added)
            {
                if (_images.Count >= MaxImages) { Append($"At most {MaxImages} images per prompt; the rest were not attached."); break; }
                _images.Add(img);
            }
            RefreshImageStrip();
            UpdateAttachments(focus: true);
        }

        private void RefreshImageStrip()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _imageStrip.Children.Clear();
            foreach (var img in _images)
            {
                var picture = new Image { Source = img.Preview, MaxHeight = 56, MaxWidth = 96, Stretch = Stretch.Uniform };
                var remove = ChatTheme.IconButton(ChatTheme.IconClose, "Remove image");
                remove.FontSize = 8;
                remove.Width = 16;
                remove.Height = 16;
                remove.Background = ChatTheme.Pressed;
                remove.HorizontalAlignment = HorizontalAlignment.Right;
                remove.VerticalAlignment = VerticalAlignment.Top;
                var item = img;
                remove.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); _images.Remove(item); RefreshImageStrip(); UpdateAttachments(focus: true); };
                var cell = new Grid { Margin = new Thickness(0, 0, 6, 0) };
                cell.Children.Add(new Border { Child = picture, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = ChatTheme.Hover });
                cell.Children.Add(remove);
                cell.ToolTip = $"{img.Image.Name} · {img.Image.Width}×{img.Image.Height} · ~{TokenMeter.Format(img.Image.EstimatedTokens)} tokens";
                _imageStrip.Children.Add(cell);
            }
            _imageStrip.Visibility = _images.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string ImageSummary(IReadOnlyCollection<ImageAttachment> images) =>
            $"🖼 {images.Count} image{(images.Count == 1 ? "" : "s")} (~{TokenMeter.Format(images.Sum(i => i.Image.EstimatedTokens))} tok)";

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

        /// <summary>For Claude's Error List tool: called on a background thread, reads the Error List on the UI thread.</summary>
        private static async Task<IReadOnlyList<BuildError>> ReadErrorListAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return VsContext.GetErrorListRows(includeWarnings: true);
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
            _images.Clear();
            RefreshImageStrip();
            UpdateAttachments(focus: false);
        }

        private bool HasAttachments => _selection != null || _attachedFiles.Count > 0 || _attachedErrors != null || _images.Count > 0;

        private void UpdateAttachments(bool focus)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var parts = new System.Collections.Generic.List<string>();
            string dir = VsContext.GetSolutionDirectory() == null ? null : WorkFolder; // the folder Claude Code runs in
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
            if (_images.Count > 0) parts.Add(ImageSummary(_images));
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
            if (_commandPopup.IsOpen && HandleCommandPickerKey(e.Key)) { e.Handled = true; return; }
            if (TryPasteImage(e)) { e.Handled = true; return; }
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
            else if (e.Key == Key.Escape)
            {
                // Esc twice opens the rewind picker, like VS Code and the CLI.
                var now = DateTime.UtcNow;
                if ((now - _lastEscUtc).TotalMilliseconds < 600) { e.Handled = true; _lastEscUtc = default; ShowRewindPicker(); }
                else _lastEscUtc = now;
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
            string dir = VsContext.GetSolutionDirectory() == null ? null : WorkFolder; // the folder Claude Code runs in
            if (dir == null) { _filePopup.IsOpen = false; return; }

            if (_fileIndex == null || !string.Equals(_fileIndexDir, dir, StringComparison.OrdinalIgnoreCase))
            {
                StartFileIndex(dir);
                _fileList.ItemsSource = new[] { "Indexing solution files…" };
                _fileList.IsEnabled = false;
                _filePopup.IsOpen = true;
                return;
            }

            var hits = _fileIndex.SearchWithSizes(_mention.Query, 10); // with "~3.2k tok": what reading it would cost
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
            if (!(_fileList.SelectedItem is FileHit hit) || _mention == null || _fileIndex == null || !_fileList.IsEnabled) return;
            string rel = hit.Path;
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
            if (text.Length == 0 && _images.Count == 0) return; // an image alone is a valid prompt
            _promptHistory.Add(text);
            _prompt.Clear();
            // Long pastes are marked only in what Claude gets; your message shows the text as you typed it.
            string forClaude = PromptSanitizer.MarkPastes(text, _pastes);
            _pastes.Clear();
            SendText(text, routed: !SlashCommands.IsCommand(text), forClaude);
        }

        // Long texts pasted into the prompt box since it was last empty (marked when sent).
        private readonly List<string> _pastes = new List<string>();

        /// <summary>Removes invisible characters from pasted text (with a notice) and remembers long pastes.</summary>
        private void OnPromptPasting(object sender, DataObjectPastingEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(e.SourceDataObject.GetData(DataFormats.UnicodeText, true) is string pasted)) return;
            string clean = PromptSanitizer.RemoveInvisible(pasted, out int removed);
            if (removed > 0)
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, clean);
                e.DataObject = data;
                Append($"Removed {removed} invisible character{(removed == 1 ? "" : "s")} (formatting or hidden tag characters) from the pasted text.");
            }
            if (PromptSanitizer.IsLongPaste(clean)) _pastes.Add(clean);
        }

        private void SendText(string userText, bool routed, string forClaude = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var opts = Options;
            string dir = WorkFolder;

            // Typed text can carry invisible characters too (e.g. from autocomplete or IME); never send them.
            forClaude = PromptSanitizer.RemoveInvisible(forClaude ?? userText, out int hidden);
            userText = PromptSanitizer.RemoveInvisible(userText, out _);
            if (hidden > 0) ClaudeOutputPane.WriteLine($"Removed {hidden} invisible characters from the prompt");
            // A command goes out from its '/': space left in front once invisible characters are gone is dropped
            if (!routed) { userText = userText.TrimStart(); forClaude = forClaude.TrimStart(); }

            var built = routed
                ? new PromptContextBuilder(opts?.ToContextSettings()).Build(forClaude, dir, _selection, _attachedFiles, _attachedErrors)
                : new BuiltPrompt { Text = forClaude };
            int attachedLines = _selection == null ? 0 : _selection.EndLine - _selection.StartLine + 1;
            int attachedFiles = routed ? _attachedFiles.Count : 0;
            if (routed && _browseWeb) built.Text = "You may search the web if it helps with this request.\n\n" + built.Text;
            var images = routed ? _images.ToList() : new List<ImageAttachment>(); // slash commands take no images
            ClearAttachments();
            SetEmptyState(false);
            if (_title.Text == DefaultTitle && routed) SetTitle(userText.Length > 0 ? userText : images.Count == 1 ? images[0].Image.Name : "Images");
            bool queued = _busy;
            SetBusy(true);
            ForgetFinishedTasks();

            Fire(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                EnsureSession(dir);

                ModelChoice model = _modelChoice;
                EffortChoice effort = _effortChoice;
                string routeNote = null, routeTip = null;
                RouteDecision decision = null;
                RouteDecision local = null;
                double routerCost = 0;
                long routerMs = 0;
                if (routed && (model == ModelChoice.Auto || effort == EffortChoice.Auto))
                {
                    var request = RouteRequestFor(userText, attachedLines, attachedFiles);
                    local = _router.Route(request);
                    var (fromHaiku, cost, ms) = await AskHaikuRouterAsync(request);
                    routerCost = cost;
                    routerMs = ms;
                    decision = fromHaiku ?? local;
                    if (model == ModelChoice.Auto) model = decision.Model;
                    if (effort == EffortChoice.Auto) effort = decision.Effort;
                    routeNote = RouteText.Note(_modelChoice == ModelChoice.Auto, _effortChoice == EffortChoice.Auto, model, effort);
                    routeTip = RouteText.Tip(fromHaiku != null, decision.Reason);
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
                    Router = decision != null && decision != local ? "haiku" : null,
                    LocalModel = decision != null && decision != local ? local?.Model.ToCli() : null,
                    LocalEffort = decision != null && decision != local ? local?.Effort.ToCli() : null,
                    RouterCostUsd = routerCost,
                    RouterMs = routerMs,
                });

                // Show the message right away; switching model/effort talks to the CLI first.
                var meta = new List<string>();
                if (routeNote != null) meta.Add(routeNote);
                if (built.ReferencedFiles > 0) meta.Add($"{built.ReferencedFiles} file reference(s)");
                if (built.InlinedLines > 0) meta.Add($"{built.InlinedLines} lines inlined");
                if (built.AttachedErrors > 0) meta.Add($"{built.AttachedErrors} error(s)");
                if (built.CharsAvoided > 0) meta.Add($"~{TokenEstimate.FromChars(built.CharsAvoided):N0} tokens of file content not sent");
                if (images.Count > 0) meta.Add(ImageSummary(images));
                // Our own id for the message, so its file changes can be rewound later (slash commands edit nothing).
                string messageId = userText.StartsWith("/") ? null : Guid.NewGuid().ToString();
                var previews = images.Select(i => (ImageSource)i.Preview).ToList();
                var turn = new TurnPrompt { Prompt = userText };
                turn.Render = () =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    _view.AppendUserMessage(userText, string.Join("  ·  ", meta), RewindAction(messageId, userText), previews, metaTip: routeTip);
                    turn.Anchor = _view.LastUserMessage;
                };
                _turnPrompts.Enqueue(turn);
                // Like VS Code: a prompt sent while Claude works waits at the bottom until Claude starts on it.
                if (queued && _turnPrompts.Count > 1) { _queuedBubbles.Enqueue(turn); RefreshQueuedPanel(); }
                else turn.Render();

                _requestedFamilies.Enqueue(model == ModelChoice.Auto ? null : model.ToCli());
                await _session.SetModelAsync(model);
                await _session.SetEffortAsync(effort);
                // Its real token count teaches the size estimate: text-only prompts that start a turn now (a queued one
                // would be measured against another request)
                if (!queued && routed && images.Count == 0) _meterModel?.Learner.PromptSent(built.Text.Length);
                await _session.SendPromptAsync(built.Text, messageId, images.Select(i => i.Image).ToList());
            });
        }

        private RouteDecision Route(string prompt, int attachedLines, int attachedFiles) => _router.Route(RouteRequestFor(prompt, attachedLines, attachedFiles));

        private RouteRequest RouteRequestFor(string prompt, int attachedLines, int attachedFiles)
        {
            _router.StickyContextTokens = Options?.StickyContextTokens ?? 30000;
            return new RouteRequest
            {
                Prompt = prompt,
                AttachedCodeLines = attachedLines,
                AttachedFiles = attachedFiles,
                CurrentModel = _session?.IsRunning == true ? _session.CurrentModel : ModelChoice.Auto,
                ContextTokens = _meterModel?.ContextTokens ?? 0,
            };
        }

        // ---- "Ask Haiku to route" (option Route with Haiku) ----

        private HaikuRouter _haikuRouter;
        private static readonly TimeSpan HaikuRouteTimeout = TimeSpan.FromSeconds(4);

        /// <summary>The Haiku router while the option is on (started ahead of time: it takes ~14 s to start); null when off.</summary>
        private HaikuRouter HaikuRouterIfOn()
        {
            if (Options?.HaikuRouting != true)
            {
                if (_haikuRouter != null) { _haikuRouter.Dispose(); _haikuRouter = null; }
                return null;
            }
            if (_haikuRouter == null)
            {
                _haikuRouter = new HaikuRouter(Options?.ClaudePath ?? "");
                var r = _haikuRouter;
                _ = System.Threading.Tasks.Task.Run(() => r.WarmUp()); // starting a process makes no model call
            }
            return _haikuRouter;
        }

        /// <summary>
        /// Asks the Haiku router (at most <see cref="HaikuRouteTimeout"/>); its answer goes through the same cache rule as
        /// the local one. Null when it is off or does not answer in time; the local decision is used then.
        /// </summary>
        private async System.Threading.Tasks.Task<(RouteDecision Decision, double Cost, long Ms)> AskHaikuRouterAsync(RouteRequest request)
        {
            var router = HaikuRouterIfOn();
            if (router == null) return (null, 0, 0);
            double before = router.TotalCostUsd;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var d = await router.RouteAsync(request, HaikuRouteTimeout);
            sw.Stop();
            if (d != null) d = _router.ApplyCacheStickiness(d, request, clearlyHard: d.Model == ModelChoice.Opus && d.Effort == EffortChoice.High);
            ClaudeOutputPane.WriteLine(d != null ? $"Haiku router ({sw.ElapsedMilliseconds} ms): {d}" : $"Haiku router gave no answer in {sw.ElapsedMilliseconds} ms; using the local rules");
            return (d, Math.Max(0, router.TotalCostUsd - before), sw.ElapsedMilliseconds);
        }

        private void UpdateRouteHint()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (SlashCommands.IsCommand(_prompt.Text))
            {
                // The list above the box shows the commands; this line says how to use it, or what the chosen one does.
                RefreshCommands();
                var chosen = SlashCommands.TypedName(_prompt.Text) == null ? SlashCommands.Match(_commands, _prompt.Text).FirstOrDefault() : null;
                _route.Text = chosen != null
                    ? "/" + chosen.Name + (string.IsNullOrWhiteSpace(chosen.Description) ? "" : " — " + chosen.Description)
                    : _commandPopup.IsOpen && _commandMatches.Count > 0 ? "↑ ↓ to choose · Tab or Enter to insert · Esc to close" : "";
                return;
            }
            string size = PromptSize.Note(_prompt.Text); // a long prompt goes with every later request
            if (_modelChoice != ModelChoice.Auto || string.IsNullOrWhiteSpace(_prompt.Text) || SlashCommands.IsCommand(_prompt.Text))
            {
                string send = _modelChoice != ModelChoice.Auto ? _modelChoice.ToCli() : null;
                string dearer = send != null && !string.IsNullOrWhiteSpace(_prompt.Text) ? (Options?.ToPriceTable() ?? new PriceTable()).VersusOpus(send) : null;
                if (dearer != null) dearer = _modelChoice + ": " + dearer; // "Fable: 2× the Opus price"
                _route.Text = string.Join("   ·   ", new[] { dearer, size, PriceNote(send) }.Where(s => s != null));
                return;
            }
            int lines = _selection == null ? 0 : _selection.EndLine - _selection.StartLine + 1;
            var d = Route(_prompt.Text, lines, _attachedFiles.Count);
            _route.Text = $"Auto: {d.Model.ToCli()} / {d.Effort.ToCli()} — {d.Reason}" + (size != null ? "   ·   " + size : "") + (PriceNote(d.Model.ToCli()) is string price ? "   ·   " + price : "");
        }

        /// <summary>
        /// "~$0.02 to send (context cached)" while a prompt is typed into a conversation with context: the least the
        /// prompt costs (its first request's input). Null for an empty prompt or a new conversation.
        /// </summary>
        private string PriceNote(string sendModel)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var m = _meterModel;
            if (m == null || string.IsNullOrWhiteSpace(_prompt.Text) || m.Cache.ContextTokens < PromptSize.ShowFrom) return null;
            return NextPromptPrice.Estimate(m.Cache, m.LastModel, sendModel, PromptSize.Estimate(_prompt.Text), Options?.ToPriceTable(), DateTime.UtcNow).Note();
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
                ForkSession = _forkNext && _resumeId != null,
                EnableFileCheckpointing = o?.FileCheckpoints ?? true,
                SessionName = _sessionName,
                EditNotesHook = true, // tells Claude which changes of an edit were rejected (review card)
            });
            if (o?.ErrorListTool ?? true)
                _session.Options.SdkMcpServers.Add(IdeMcpServer.Create(ReadErrorListAsync, () => dir));
            HaikuRouterIfOn(); // with Route with Haiku on: start its process now, so it is warm for the first prompt
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
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { AppendError("[sessions] ", ex.Message); return; }
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
            foreach (var g in SessionMeta.Groups())
            {
                string group = g;
                entries.Add(new ChatPopup.Entry
                {
                    Group = "Groups",
                    Glyph = ChatTheme.IconFolder,
                    Label = group,
                    Checked = string.Equals(group, s.Group, StringComparison.OrdinalIgnoreCase),
                    Run = () => Done(() => SessionMeta.SetGroup(s.Id, group)),
                    Rename = name => Done(() => SessionMeta.RenameGroup(group, name)),
                    Actions = new List<ChatPopup.EntryAction>
                    {
                        new ChatPopup.EntryAction { Glyph = ChatTheme.IconDelete, Tooltip = "Delete group (its sessions stay, ungrouped)", Run = () => Done(() => SessionMeta.RenameGroup(group, null)) },
                    },
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
            DisposeSession();
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
                Tip(CacheWindow.ResumeNote(s.LastWriteUtc, transcript.LastContextTokens, DateTime.UtcNow)); // a cold cache costs the first prompt
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
                        if (item.ResultChars >= 0) _view.CompleteTool(item.ToolUseId, item.ResultChars, item.IsError, item.ResultText);
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
            DisposeSession();
            ResetMeter();
            ClearTranscript();
            _resumeId = null;
            _forkNext = false;
            _sessionName = null;
            _resumeGeneration++; // drop a resumed transcript that is still being read
            RefreshCommands(force: true);
            _fileIndex = null; // re-index on the next @ (files may have been added)
            SetBusy(false);
            SetTitle(null);
            SetEmptyState(true);
            UpdateFolderChip();
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
                        break;
                    case ClaudeEventKind.PermissionRequest:
                        FlushText();
                        _waitingForUser = true; // Claude waits for you; not a stall
                        SetStalled(false);
                        if (ShowInteractiveCard(ev)) break;
                        ShowPermission(ev);
                        break;
                    case ClaudeEventKind.Task:
                        OnTaskEvent(ev);
                        break;
                    case ClaudeEventKind.RateLimit:
                        _rateLimits = RateLimitInfo.Parse(ev.Payload);
                        UpdateLimitBanner();
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
                        string used = _turnModel != null ? " · " + _turnModel : "";
                        if (ev.Subagents?.FooterNote() is string agents) used += " · " + agents;
                        if (ev.Subagents?.Warning() is string agentTrouble)
                        {
                            FlushText();
                            Append("\n  ⚠ " + agentTrouble + "\n");
                            ClaudeOutputPane.WriteLine(agentTrouble);
                        }
                        CheckModelMismatch(_turnModel);
                        RecordUsage(ev, _turnModel);
                        CheckCost();
                        CacheBreakTip();
                        RefreshContextUsage(show: false); // control request, no model call
                        _turnModel = null;
                        var turn = _turnPrompts.Count > 0 ? _turnPrompts.Dequeue() : new TurnPrompt();
                        string answer = _turnAnswer.ToString();
                        _turnAnswer.Clear();
                        _view.AppendTurnFooter($"✓ {ev.DurationMs / 1000.0:0.0}s · {TokenMeter.Format(ev.Usage.Total)} tok · ${_meterModel?.LastTurnCostUsd:0.000}{used}{TurnFooter.Extras(ev.Usage, _meterModel?.LastTurnCacheSavedUsd ?? 0)}",
                            string.IsNullOrWhiteSpace(answer) ? null : BookmarkToggle(turn.Prompt, answer, turn.Anchor), answer);
                        if (!string.IsNullOrWhiteSpace(answer)) _lastAnswer = answer;
                        ShowTurnChanges(); // "Files changed" with Diff links
                        Announce(ev.IsError ? "Claude stopped with an error. " + Short(ev.Text) : ScreenReaderText.AnswerFinished(answer));
                        if (_queuedBubbles.Count > 0) { _queuedBubbles.Dequeue().Render(); RefreshQueuedPanel(); } // Claude starts on the next one now
                        break;
                    case ClaudeEventKind.Error:
                        AppendError("[claude] ", ev.Text);
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
            // Prompts sent while Claude is still answering are queued by the CLI and finish in order.
            if (_pendingUsages.Count == 0) return;
            var rec = _pendingUsages.Dequeue();
            if (Options?.KeepUsageLog == false) return;
            rec.Timestamp = DateTime.UtcNow;
            rec.ModelUsed = modelUsed;
            rec.InputTokens = ev.Usage.Input;
            rec.OutputTokens = ev.Usage.Output;
            rec.ThinkingTokens = ev.Usage.Thinking;
            rec.CacheReadTokens = ev.Usage.CacheRead;
            rec.CacheWriteTokens = ev.Usage.CacheCreation;
            rec.CacheWrite1hTokens = ev.Usage.CacheCreation1h;
            rec.FastTokens = ev.Usage.FastTokens;
            rec.LargeContextTokens = _meterModel?.LastTurn.LargeContextTokens ?? 0; // per request: the meter counted it
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

        /// <summary>The meter's tooltip: what fills the context (once measured), then what the prompt cache saved.</summary>
        private void UpdateMeterTooltip()
        {
            string tip = _contextUsage != null ? ContextTooltip(_contextUsage) : "Click for what fills the context window";
            if (_meterModel?.CacheSavedLine() is string cache) tip += "\n\n" + cache;
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
                        catch (Exception ex) { AppendError("[plan] ", ex.Message); }
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

        // ---------------- Helpers ----------------

        private void ResetMeter()
        {
            if (_meterModel != null) _meterModel.Changed -= OnMeterChanged;
            _meterModel = new TokenMeter(Options?.ToMeterSettings()) { Prices = Options?.ToPriceTable() };
            _dailyCounted = 0;
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
            _rewindPoints.Clear();
            _lastAnswer = null;
        }

        private string _lastAnswer;

        /// <summary>/ → Focus view: flips the option (saved like any Tools → Options change); applies to new tool calls.</summary>
        private void ToggleFocusView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var o = Options;
            if (o == null) return;
            o.FocusView = !o.FocusView;
            o.SaveSettingsToStorage();
            Append(o.FocusView ? "Focus view on: tool calls from now on collapse into one row each run." : "Focus view off: each tool call gets its own row again.");
        }

        /// <summary>
        /// The CLI writes the compaction summary to its session file around the time it reports the compaction; read
        /// it from there (a few tries, off the UI thread) and add it to the card.
        /// </summary>
        private void LoadCompactSummary(Action<string> setSummary)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var session = _session;
            string id = session?.SessionId, dir = _sessionDir;
            if (id == null || dir == null) return;
            string file = SessionHistory.SessionFile(null, dir, id);
            Jtf.RunAsync(async () =>
            {
                string summary = null;
                for (int attempt = 0; attempt < 5 && summary == null; attempt++)
                {
                    if (attempt > 0) await System.Threading.Tasks.Task.Delay(1000);
                    summary = await System.Threading.Tasks.Task.Run(() => SessionTranscript.ReadLastCompactSummary(file));
                }
                await Jtf.SwitchToMainThreadAsync();
                if (summary != null && session == _session) setSummary(summary);
            }).FileAndForget("ClaudeVS/ChatControl/CompactSummary");
        }

        /// <summary>/ → Copy last response (like /copy in VS Code): Claude's last answer as Markdown.</summary>
        /// <summary>/ → Copy session summary: turns, cost, tokens, cache and context as plain text.</summary>
        private void CopySessionSummary()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_meterModel == null || _meterModel.Turns == 0) { Append("No turns in this conversation yet."); return; }
            string text = SessionSummary.Format(_meterModel, _fullTitle ?? _sessionName, _session?.SessionId ?? _resumeId);
            try { Clipboard.SetText(text); Append("Copied the session summary."); }
            catch (Exception) { Append("The clipboard is busy; try again."); }
        }

        private void CopyLastAnswer()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(_lastAnswer)) { Append("No response to copy yet."); return; }
            try { Clipboard.SetText(_lastAnswer.Trim()); Append("Copied the last response."); }
            catch (Exception) { Append("The clipboard is busy; try again."); }
        }

        /// <summary>An Edit / Write waiting for its result.</summary>
        private sealed class FileCall
        {
            public string ToolName;
            public Newtonsoft.Json.Linq.JObject Input;
            public string Path;
            public string Snapshot;   // the file before the change: our copy, then the CLI's
            public bool FirstInTurn;  // its copy is the "before" of Files changed
        }

        private readonly Dictionary<string, FileCall> _fileCalls = new Dictionary<string, FileCall>(StringComparer.Ordinal);

        /// <summary>
        /// When an Edit / Write result carries the CLI's copy of the file before the change (2.1.319), it replaces
        /// ours, which a fast tool can beat: for the Diff button, for Files changed, and for the lines under the row
        /// (returned, rebuilt from it). Null when there is nothing to correct.
        /// </summary>
        private InlineDiff ConfirmFileChange(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(ev.ToolUseId) || !_fileCalls.TryGetValue(ev.ToolUseId, out var call)) return null;
            _fileCalls.Remove(ev.ToolUseId);
            if (StoppedEdit.IsStopped(ev.IsError, ev.ResultText)) TellIfStoppedEditWritten(ev.ToolUseId, call);
            if (ev.IsError || ev.OriginalFile == null || ev.OriginalFile == call.Snapshot) return null; // ours was right
            ClaudeOutputPane.WriteLine($"{call.ToolName} {call.Path}: diff corrected from the CLI's copy of the file");
            call.Snapshot = ev.OriginalFile;
            if (call.FirstInTurn) _turnChanges.Confirm(call.Path, ev.OriginalFile);
            return InlineDiff.FromToolUse(call.ToolName, call.Input, ev.OriginalFile == "" ? null : ev.OriginalFile);
        }

        /// <summary>
        /// Stop ended an Edit / Write while it ran, and Claude was told it "may have" changed the file: read the file and,
        /// when it says whether the change is in, tell Claude with the next prompt (and the Output pane now).
        /// </summary>
        private void TellIfStoppedEditWritten(string toolUseId, FileCall call)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string now = ReadForReview(call.Path);
            if (now == ReviewUnreadable) return;
            var state = StoppedEdit.Check(call.ToolName, call.Input, call.Snapshot, now);
            string note = StoppedEdit.Note(call.ToolName, System.IO.Path.GetFileName(call.Path ?? ""), state, call.Input);
            ClaudeOutputPane.WriteLine($"{call.ToolName} {call.Path} was stopped while running: {state}" + (note != null ? "; Claude is told with the next prompt" : ""));
            _session?.AddNextPromptNote(toolUseId, note);
        }

        private void ClearTurns()
        {
            _turnChanges.Clear();
            _fileCalls.Clear();
            _turnPrompts.Clear();
            _turnAnswer.Clear();
            _queuedBubbles.Clear();
            RefreshQueuedPanel();
            _waitingForUser = false;
            RenderActivity();
            SetStalled(false);
            ClearTasks(); // the process is gone (exit, new session), and its tasks with it
        }

        private void RefreshQueuedPanel()
        {
            _queuedPanel.Children.Clear();
            foreach (var q in _queuedBubbles)
            {
                string text = SessionMetaStore.Clean(q.Prompt) ?? "(image)";
                var label = new TextBlock { Text = "Queued · " + text, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = q.Prompt };
                ChatTheme.Muted(label);
                var box = new Border { Child = label, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 0, 2), BorderThickness = new Thickness(1), BorderBrush = ChatTheme.Hover };
                _queuedPanel.Children.Add(box);
            }
            _queuedPanel.Visibility = _queuedBubbles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------- Skills and MCP servers ----------------

        /// <summary>Breakdown rows that open a list where their contents can be turned off.</summary>
        private Action CategoryAction(ContextCategory c)
        {
            string id = (c.Id ?? c.Name ?? "").ToLowerInvariant();
            if (id.Contains("skill")) return ShowSkills;
            if (id.Contains("mcp")) return ShowMcpServers;
            return null;
        }

        private string ManageDir
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return _sessionDir ?? WorkFolder;
            }
        }

        /// <summary>
        /// The /skills menu: each skill with its source and what its listing costs on every request; clicking one turns
        /// it on or off (claude edit-skill-overrides, then reload_skills). Locked skills are shown, not changeable.
        /// </summary>
        private void ShowSkills()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dir = ManageDir;
            EnsureSession(dir); // reading the list makes no model call
            var session = _session;
            _manage.Footer = null;
            _manage.Show(new[] { new ChatPopup.Entry { Label = "Loading skills…", Enabled = false } }, _inputBox);
            Jtf.RunAsync(async () =>
            {
                var skills = await session.GetSkillsAsync();
                await Jtf.SwitchToMainThreadAsync();
                if (session != _session) return;
                if (skills == null) { _manage.Close(); AppendError("[skills] ", "Claude Code did not return the skills list."); return; }
                var entries = skills.OrderByDescending(s => s.Tokens).Select(s => new ChatPopup.Entry
                {
                    Group = s.IsOn ? "On" : "Off",
                    Label = s.DisplayName,
                    Detail = $"~{TokenMeter.Format(s.Tokens)} tok",
                    Description = s.Source + (s.State != "on" && s.State != "off" ? " · " + s.State : "") + (s.LockedBy != null ? " · locked: " + SkillRow.LockReason(s.LockedBy) : ""),
                    Checked = s.IsOn,
                    Enabled = s.LockedBy == null,
                    Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SetSkill(s, s.IsOn ? "off" : "on"); },
                }).OrderBy(e => e.Group == "On" ? 0 : 1).ToList();
                if (entries.Count == 0) entries.Add(new ChatPopup.Entry { Label = "No skills that can be turned on or off here", Enabled = false });
                int onTokens = skills.Where(s => s.IsOn).Sum(s => s.Tokens);
                _manage.Footer = $"Skills that are on cost ~{TokenMeter.Format(onTokens)} tokens on every request · click to turn one on or off";
                _manage.Show(entries, _inputBox, keepFilter: true);
            }).FileAndForget("ClaudeVS/ChatControl/Skills");
        }

        private void SetSkill(SkillRow skill, string state)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dir = ManageDir;
            var session = _session;
            string claudePath = Options?.ClaudePath ?? "";
            Jtf.RunAsync(async () =>
            {
                string error = await ClaudeSubcommands.SetSkillStateAsync(claudePath, dir, skill.Name, state);
                if (error == null && session != null) await session.ReloadSkillsAsync(); // the running session picks it up now
                await Jtf.SwitchToMainThreadAsync();
                if (error != null) { AppendError("[skills] ", error); return; }
                Append($"Skill {skill.DisplayName} turned {state} ({(state == "off" ? "saves" : "costs")} ~{TokenMeter.Format(skill.Tokens)} tokens per request).");
                ShowSkills();
                RefreshContextUsage(show: false);
            }).FileAndForget("ClaudeVS/ChatControl/SetSkill");
        }

        /// <summary>MCP servers with their status: click to turn one on or off; failed ones can be reconnected.</summary>
        private void ShowMcpServers()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsureSession(ManageDir);
            var session = _session;
            _manage.Footer = null;
            _manage.Show(new[] { new ChatPopup.Entry { Label = "Loading MCP servers…", Enabled = false } }, _inputBox);
            Jtf.RunAsync(async () =>
            {
                var servers = await session.GetMcpServersAsync();
                await Jtf.SwitchToMainThreadAsync();
                if (session != _session) return;
                if (servers == null) { _manage.Close(); AppendError("[mcp] ", "Claude Code did not return the MCP server list."); return; }
                var entries = servers.Select(s => new ChatPopup.Entry
                {
                    Label = s.Name,
                    Detail = McpServerRow.StatusText(s.Status),
                    Description = string.Join(" · ", new[]
                    {
                        s.Scope, s.ToolCount >= 0 ? $"{s.ToolCount} tools" : null,
                        s.Status == "needs-auth" ? "sign in with /mcp in a terminal" : null, s.Error,
                    }.Where(x => !string.IsNullOrEmpty(x))),
                    Checked = s.IsEnabled,
                    Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SetMcpServer(s.Name, !s.IsEnabled); },
                    Actions = s.Status == "failed" || s.Status == "pending"
                        ? new List<ChatPopup.EntryAction> { new ChatPopup.EntryAction { Glyph = ChatTheme.IconRefresh, Tooltip = "Reconnect", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); ReconnectMcp(s.Name); } } }
                        : null,
                }).ToList();
                if (entries.Count == 0) entries.Add(new ChatPopup.Entry { Label = "No MCP servers for this folder", Description = "Add them with claude mcp add, or in .mcp.json", Enabled = false });
                var u = _contextUsage;
                string inWindow = u != null && u.McpToolTokens > 0 ? $"MCP tools take ~{TokenMeter.Format(u.McpToolTokens)} tokens of context · " : "";
                string onDemand = u != null && u.McpDeferredToolCount > 0 ? $"{u.McpDeferredToolCount} load only when used · " : "";
                _manage.Footer = inWindow + onDemand + "click to turn a server on or off";
                _manage.Show(entries, _inputBox, keepFilter: true);
            }).FileAndForget("ClaudeVS/ChatControl/Mcp");
        }

        private void SetMcpServer(string name, bool enabled)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var session = _session;
            if (session == null) return;
            Jtf.RunAsync(async () =>
            {
                string error = null;
                try { if (await session.SetMcpServerEnabledAsync(name, enabled) == null) error = "Claude Code did not answer."; }
                catch (InvalidOperationException ex) { error = ex.Message; }
                await Jtf.SwitchToMainThreadAsync();
                if (error != null) { AppendError("[mcp] ", error); return; }
                Append($"MCP server {name} turned {(enabled ? "on" : "off")}.");
                ShowMcpServers();
                RefreshContextUsage(show: false);
            }).FileAndForget("ClaudeVS/ChatControl/McpToggle");
        }

        private void ReconnectMcp(string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var session = _session;
            if (session == null) return;
            Jtf.RunAsync(async () =>
            {
                string error = null;
                try { if (await session.ReconnectMcpServerAsync(name) == null) error = "Claude Code did not answer in time."; }
                catch (InvalidOperationException ex) { error = ex.Message; }
                await Jtf.SwitchToMainThreadAsync();
                if (error != null) AppendError("[mcp] ", error); else Append($"Reconnected {name}.");
                ShowMcpServers();
            }).FileAndForget("ClaudeVS/ChatControl/McpReconnect");
        }

        // ---------------- Usage limits ----------------

        // Latest plan usage from the CLI (rate_limit_event); null for API-key users, who never get one.
        private RateLimitInfo _rateLimits;
        private readonly TextBlock _limitBanner = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };

        /// <summary>
        /// "5-hour limit 86% used. Resets at 3:10 PM." under the meter (orange), or "… reached" (red); hidden below
        /// 80% and once the reset time has passed. The tooltip lists every window.
        /// </summary>
        private void UpdateLimitBanner()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var now = DateTime.UtcNow;
            var r = _rateLimits;
            var (level, text) = r?.Banner(now) ?? (LimitLevel.None, null);
            if (level != LimitLevel.None && r.ResetsAtUtc != null && r.ResetsAtUtc < now && r.Windows.All(w => w.ResetsAtUtc == null || w.ResetsAtUtc < now))
                level = LimitLevel.None; // everything has reset since the CLI last told us
            if (level == LimitLevel.None) { _limitBanner.Visibility = Visibility.Collapsed; return; }
            _limitBanner.Text = (level == LimitLevel.Reached ? "⛔ " : "⚠ ") + text;
            _limitBanner.Foreground = level == LimitLevel.Reached ? ChatTheme.Error : Brushes.DarkOrange;
            _limitBanner.ToolTip = r.Details(now) + "\nFrom Claude Code; updated with each answer.";
            _limitBanner.Visibility = Visibility.Visible;
            AnnounceLimit(text, level == LimitLevel.Reached);
        }

        // ---------------- "Not responding" ----------------

        // Last time the CLI said anything (UTC ticks; written on the reader thread).
        private long _lastOutputTicks = DateTime.UtcNow.Ticks;
        // A permission bar or question/plan card is waiting for you: silence is expected then.
        private bool _waitingForUser;
        private DispatcherTimer _stallTimer;
        private readonly TextBlock _stall = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 4), Visibility = Visibility.Collapsed, Foreground = ChatTheme.Error };
        private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(60);

        private void CheckStalled()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var quiet = DateTime.UtcNow - new DateTime(System.Threading.Interlocked.Read(ref _lastOutputTicks), DateTimeKind.Utc);
            SetStalled(_busy && !_waitingForUser && quiet >= StallAfter, quiet);
            if (_limitBanner.Visibility == Visibility.Visible) UpdateLimitBanner(); // hide it once the limit has reset
            if (_signInBanner.Visibility == Visibility.Visible && !_signInExpired) UpdateSignInBanner(); // days left, then expired
            UpdateCacheNote();
        }

        private void SetStalled(bool stalled, TimeSpan quiet = default)
        {
            bool wasStalled = _stall.Visibility == Visibility.Visible;
            _stall.Visibility = stalled ? Visibility.Visible : Visibility.Collapsed;
            if (stalled) _stall.Text = $"⚠ Not responding: no output from Claude Code for {(int)quiet.TotalMinutes} min. A long command may still be running; Esc stops it.";
            if (stalled && !wasStalled) Announce("Claude Code is not responding. A long command may still be running. Press Escape in the prompt box to stop it.");
            if (stalled != wasStalled) RenderActivity(); // the red notice replaces the activity strip
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
            Announce("Error: " + Short(text));
            string hint = ClaudeErrorHints.Explain(text);
            if (hint != null && hint != _lastHint) Append("  → " + hint + "\n");
            _lastHint = hint;
            CheckSignInError(text);
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
            ResetNotices();
            _contextUsage = null;
            UpdateMeterTooltip();
            lock (_pendingText) _pendingText.Clear();
            _permission.Visibility = Visibility.Collapsed;
        }

        public void Dispose()
        {
            if (LastActive == this) LastActive = null;
            lock (Instances) Instances.Remove(this);
            _flushTimer.Stop();
            _routeTimer.Stop();
            _stallTimer.Stop();
            _agentsTimer?.Stop();
            _haikuRouter?.Dispose();
            _haikuRouter = null;
            DisposeSession();
        }
    }
}
