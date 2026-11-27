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
            Text = PlaceholderText, // or the suggested next prompt (ClaudeChatControl.NextPrompt.cs)
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
        // Each session's last modelUsage, so a resumed conversation's first turn counts its subagents exactly
        private static readonly ModelUsageReadings ModelReadings = new ModelUsageReadings(ModelUsageReadings.DefaultPath);
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
            _prompt.GotKeyboardFocus += (s, e) => { SetInputFocused(true); CheckInstructionFiles(); };
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
                if (_prompt.Text.Length > 0) ClearSuggestion(); // typing clears it; it doesn't come back when the box is emptied
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
            _view.BaseDirectory = () => { ThreadHelper.ThrowIfNotOnUIThread(); return _sessionDir ?? VsContext.GetSolutionDirectory(); }; // file links in answers (Phase 90)
            var transcript = new Grid();
            transcript.Children.Add(_view.Box);
            transcript.Children.Add(_emptyState); // shown over the transcript until the first message
            // The find bar over the conversation (Phase 101)
            InitFind();
            var conversation = new DockPanel();
            DockPanel.SetDock(_findBar, Dock.Top);
            conversation.Children.Add(_findBar);
            conversation.Children.Add(transcript);
            var middle = new DockPanel();
            DockPanel.SetDock(_bookmarks.View, Dock.Right);
            middle.Children.Add(_bookmarks.View);
            middle.Children.Add(conversation);
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
            if (busy) ClearSuggestion(); // a prompt went out
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
                        Core.SafeFile.Replace(target, ConversationExport.ToMarkdown(transcript, title, DateTime.Now));
                    });
                }
                catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { error = Core.SafeFile.Explain(ex, target); }
                await Jtf.SwitchToMainThreadAsync();
                if (error != null) { AppendError("[export] ", error); return; }
                Append($"Exported {items} items to {target}");
                try { VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, target); }
                catch (Exception ex) { ClaudeOutputPane.WriteLine("Could not open the export: " + ex.Message); }
            }).FileAndForget("ClaudeVS/ChatControl/Export");
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

        // The modelUsage readings only sharpen a count: a failure goes to the Output pane, not the chat
        private static Dictionary<string, TokenUsage> ReadModelUsage(string sessionId)
        {
            try { return ModelReadings.Get(sessionId); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { ClaudeOutputPane.WriteLine("model-usage.json: " + ex.Message); return null; }
        }

        private static void SaveModelUsage(string sessionId, Dictionary<string, ModelUsageEntry> modelUsage)
        {
            try { ModelReadings.Save(sessionId, modelUsage); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { ClaudeOutputPane.WriteLine("model-usage.json: " + Core.SafeFile.Explain(ex, ModelReadings.Path)); }
        }

        private void SaveSessionMeta(Action save)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { save(); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { AppendError("[sessions] ", Core.SafeFile.Explain(ex, SessionMeta.Path)); }
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
            UpdateRouteHint(); // plan mode changes which model answers (Haiku → Sonnet)
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
            MeasureReference(path);
        }

        /// <summary>The turn's reads and quiet commands (main agent and subagents), reset after each turn's footer.</summary>
        private readonly TurnFileActivity _turnFiles = new TurnFileActivity();
        /// <summary>This conversation's reads and runs, turn by turn, for Copy session summary; reset with the conversation.</summary>
        private readonly ConversationReads _conversationReads = new ConversationReads();
        /// <summary>"References: Claude read 2 of 12 files, …" for the last turn's footer tooltip; null when none attached.</summary>
        private string _lastTurnReferences;

        private static string JoinLines(params string[] parts) =>
            parts.Any(p => !string.IsNullOrEmpty(p)) ? string.Join("\n", parts.Where(p => !string.IsNullOrEmpty(p))) : null;

        /// <summary>What each reference would cost if read whole, by path; measured once, off the UI thread.</summary>
        private readonly System.Collections.Generic.Dictionary<string, ReferenceSize> _referenceSizes =
            new System.Collections.Generic.Dictionary<string, ReferenceSize>(StringComparer.OrdinalIgnoreCase);

        private void MeasureReference(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_referenceSizes.ContainsKey(path)) return;
            _referenceSizes[path] = null; // being measured
            Jtf.RunAsync(async () =>
            {
                var size = await System.Threading.Tasks.Task.Run(() => ReferenceSize.Measure(path));
                await Jtf.SwitchToMainThreadAsync();
                if (size == null) { _referenceSizes.Remove(path); return; }
                _referenceSizes[path] = size;
                if (_attachedFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) UpdateAttachments(focus: false);
            }).FileAndForget("ClaudeVS/ChatControl/ReferenceSize");
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
            _referenceSizes.Clear(); // files change: measure again when attached again
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
            var large = new System.Collections.Generic.HashSet<int>(); // indexes of parts shown in amber
            string dir = VsContext.GetSolutionDirectory() == null ? null : WorkFolder; // the folder Claude Code runs in
            if (_selection != null)
            {
                int lines = _selection.EndLine - _selection.StartLine + 1;
                bool inline = lines <= (Options?.InlineMaxLines ?? 60);
                parts.Add($"{PromptContextBuilder.Relative(dir, _selection.FilePath)}:L{_selection.StartLine}-{_selection.EndLine} ({lines} lines, {(inline ? "inlined" : "sent as reference")})");
            }
            foreach (var f in _attachedFiles)
            {
                // "src/Big.cs (reference, ~3.2k tok if read)": a ceiling, since only the path is sent.
                _referenceSizes.TryGetValue(f, out var size);
                string label = size?.Label;
                if (size != null && size.IsLarge) large.Add(parts.Count);
                parts.Add(PromptContextBuilder.Relative(dir, f) + (string.IsNullOrEmpty(label) ? " (reference)" : $" (reference, {label} if read)"));
            }
            if (_attachedErrors != null)
            {
                int max = Options?.MaxAttachedErrors ?? 20;
                parts.Add(_attachedErrors.Count > max ? $"{max} of {_attachedErrors.Count} errors" : $"{_attachedErrors.Count} error(s)");
            }
            if (_images.Count > 0) parts.Add(ImageSummary(_images));
            if (_browseWeb) parts.Add("🌐 web search allowed");

            if (parts.Count == 0) { _attachment.Visibility = Visibility.Collapsed; UpdateRouteHint(); return; }
            _attachment.Inlines.Clear();
            _attachment.Inlines.Add(new System.Windows.Documents.Run("📎 "));
            var amber = ChatTheme.Accents(this).Running;
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) _attachment.Inlines.Add(new System.Windows.Documents.Run(" · "));
                var run = new System.Windows.Documents.Run(parts[i]);
                if (large.Contains(i)) run.Foreground = amber;
                _attachment.Inlines.Add(run);
            }
            _attachment.Inlines.Add(new System.Windows.Documents.Run(" — Esc to remove"));
            _attachment.ToolTip = large.Count > 0
                ? $"A reference sends only its path, and Claude reads what it needs. The amber ones would cost {ReferenceSize.LargeTokens / 1000}k+ tokens if read whole: say which part you mean."
                : null;
            _attachment.Visibility = Visibility.Visible;
            if (focus && ChatWindowActive()) _prompt.Focus();
            UpdateRouteHint();
        }

        /// <summary>This chat's window frame, set when Visual Studio creates the window.</summary>
        internal Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame WindowFrame { get; set; }

        /// <summary>
        /// Whether this chat's window is still Visual Studio's active window, so focusing the input box won't take the
        /// keyboard from an editor you went back to (as VS Code's 2.1.330 fix for a slow-starting Claude tab). An
        /// editor command (attach a selection or a file) opens the chat and focuses its box once the window is there;
        /// by then you may be typing in the editor again. True when it can't tell.
        /// </summary>
        private bool ChatWindowActive()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (IsKeyboardFocusWithin || WindowFrame == null) return true;
            if (!(Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsShellMonitorSelection)) is Microsoft.VisualStudio.Shell.Interop.IVsMonitorSelection selection)) return true;
            if (selection.GetCurrentElementValue((uint)Microsoft.VisualStudio.VSConstants.VSSELELEMID.SEID_WindowFrame, out object active) != Microsoft.VisualStudio.VSConstants.S_OK || active == null) return true;
            if (ReferenceEquals(active, WindowFrame)) return true;
            // The same tool window seen through another wrapper: its kind and instance number
            if (!(active is Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame other)) return false;
            int slot = (int)Microsoft.VisualStudio.Shell.Interop.__VSFPROPID.VSFPROPID_GuidPersistenceSlot;
            int num = (int)Microsoft.VisualStudio.Shell.Interop.__VSFPROPID.VSFPROPID_MultiInstanceToolNum;
            return other.GetGuidProperty(slot, out Guid otherSlot) == Microsoft.VisualStudio.VSConstants.S_OK
                && WindowFrame.GetGuidProperty(slot, out Guid ourSlot) == Microsoft.VisualStudio.VSConstants.S_OK
                && otherSlot == ourSlot
                && other.GetProperty(num, out object otherNum) == Microsoft.VisualStudio.VSConstants.S_OK
                && WindowFrame.GetProperty(num, out object ourNum) == Microsoft.VisualStudio.VSConstants.S_OK
                && Equals(otherNum, ourNum);
        }

        // ---------------- Sending ----------------

        /// <summary>Tools → Options → Escape stops Claude (on by default).</summary>
        private bool EscapeStops => Options?.EscapeStopsClaude ?? true;

        /// <summary>How to stop Claude, for messages: "Esc", or the send button (Stop during a turn) when Esc doesn't.</summary>
        private string StopHow => EscapeStops ? "Esc" : "the Stop button";

        private void OnPromptKeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // A held Enter sends (or picks) once: its repeat would run the command just picked from the / list, or send
            // what was typed by then (as VS Code's fix in 2.1.330). Shift+Enter still repeats its new lines.
            if (e.Key == Key.Enter && e.IsRepeat && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; return; }
            if (_filePopup.IsOpen && HandleFilePickerKey(e.Key)) { e.Handled = true; return; }
            if (_commandPopup.IsOpen && HandleCommandPickerKey(e.Key)) { e.Handled = true; return; }
            if (TryPasteImage(e)) { e.Handled = true; return; }
            if (TakeSuggestion(e)) { e.Handled = true; return; } // Tab or → in the empty box: the suggested prompt
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
                string text = e.Key == Key.Up ? _promptHistory.Previous(_prompt.Text) : _promptHistory.Next(_prompt.Text);
                if (text != null) { _prompt.Text = text; _prompt.CaretIndex = text.Length; }
            }
            else if (e.Key == Key.Escape && HasAttachments)
            {
                e.Handled = true;
                ClearAttachments();
            }
            else if (e.Key == Key.Escape && _busy)
            {
                // Esc stops the answer, like the CLI, unless the option turns that off (as VS Code's
                // claudeCode.escapeStopsClaude, 2.1.330); then it does nothing during a turn, not Esc Esc's rewind either.
                if (!EscapeStops) return;
                e.Handled = true;
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

        private void SendFromBox()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string text = _prompt.Text.Trim();
            if (text.Length == 0 && _images.Count == 0) return; // an image alone is a valid prompt
            if (!SlashCommands.IsCommand(text) && (!PastDailyLimitConfirmed() || !PastConversationLimitConfirmed())) return; // the prompt stays in the box
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
            // The references and what reading them whole would cost (those measured by now), before they are cleared
            var refPaths = routed ? _attachedFiles.ToList() : new List<string>();
            var refSizes = refPaths.Select(p => _referenceSizes.TryGetValue(p, out var sz) ? sz : null).Where(sz => sz != null).ToList();
            int refFiles = refSizes.Sum(sz => sz.Files);
            long refCeiling = refSizes.Sum(sz => (long)sz.Tokens);
            // Still being measured (a large folder): their sizes land in the cache later and count when the turn is logged
            var refPending = refPaths.Where(p => !_referenceSizes.TryGetValue(p, out var sz) || sz == null).ToList();
            ClearAttachments();
            foreach (var p in refPending) _referenceSizes[p] = null; // keep the slot, so the background measurement is kept
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
                    if (effort == EffortChoice.Auto) effort = ClaudeSettings.Cap(decision.Effort, _effortCap); // Claude Code wouldn't go above maxEffortLevel
                    routeNote = RouteText.Note(_modelChoice == ModelChoice.Auto, _effortChoice == EffortChoice.Auto, model, effort);
                    routeTip = RouteText.Tip(fromHaiku != null, decision.Reason);
                    ClaudeOutputPane.WriteLine($"Route: {decision} | prompt {userText.Length} chars, {attachedLines} attached lines, {attachedFiles} files, context {_meterModel?.ContextTokens ?? 0} tok → using {model.ToCli()}/{effort.ToCli()}");
                }

                var offered = TakeOffered(); // the suggested prompt shown before this one, if any
                _pendingUsages.Enqueue(new UsageRecord
                {
                    Solution = dir,
                    Suggestion = offered.Source,
                    SuggestionTaken = offered.Taken,
                    Routed = decision != null,
                    RouteModel = decision != null ? model.ToCli() : null,
                    RouteEffort = decision != null ? effort.ToCli() : null,
                    Effort = effort.ToCli(),
                    RouteScore = decision?.Score,
                    RouteReason = decision?.Reason,
                    PromptChars = userText.Length,
                    AttachedLines = attachedLines,
                    AttachedFiles = attachedFiles,
                    RefPaths = refPaths.Count > 0 ? refPaths : null,
                    RefFiles = refFiles,
                    RefCeilingTokens = refCeiling,
                    RefPending = refPending.Count > 0 ? refPending : null,
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
                var sentRecord = _pendingUsages.LastOrDefault();
                await _session.SetModelAsync(model);
                await _session.SetEffortAsync(effort);
                ConfirmEffort(sentRecord); // the effort Claude Code says it applies, when the process already runs
                // Its real token count teaches the size estimate: text-only prompts that start a turn now (a queued one
                // would be measured against another request)
                if (!queued && routed && images.Count == 0) _meterModel?.Learner.PromptSent(built.Text.Length);
                await _session.SendPromptAsync(built.Text, messageId, images.Select(i => i.Image).ToList());
            });
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
            string text = SessionSummary.Format(_meterModel, _fullTitle ?? _sessionName, _session?.SessionId ?? _resumeId, _conversationReads, _session?.Models);
            try { Clipboard.SetText(text); Append("Copied the session summary."); }
            catch (Exception) { Append("The clipboard is busy; try again."); }
        }

        private void CopyLastAnswer()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(_lastAnswer)) { Append("No response to copy yet."); return; }
            try { TranscriptView.CopyMarkdown(_lastAnswer.Trim()); Append("Copied the last response."); }
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
            LogUnbilledSuggestion(_sessionDir); // no later turn of this process will bill it
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
            _saidEffortDiffers = false;
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
