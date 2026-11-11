using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using ClaudeVS.Commands;
using ClaudeVS.Core.Markdown;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// Chat transcript: status lines as plain monospace text, Claude's answers rendered as Markdown
    /// (FlowDocument, no extra dependencies). The answer being streamed is re-rendered as it grows.
    /// Code blocks get Copy and Insert-at-cursor buttons. UI thread only.
    /// </summary>
    internal sealed class TranscriptView
    {
        private static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");
        // Translucent overlays work on light and dark themes alike.
        private static readonly Brush CodeBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)));
        private static readonly Brush QuoteBar = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80)));

        private readonly FlowDocument _doc;
        private readonly StringBuilder _answer = new StringBuilder();
        private readonly List<Block> _answerBlocks = new List<Block>();
        private Paragraph _plain;
        private readonly Dictionary<string, ToolRow> _tools = new Dictionary<string, ToolRow>();
        private ScrollViewer _scroll;

        /// <summary>
        /// A TextBox's own ScrollViewer swallows the mouse wheel even when it can't scroll vertically, which stalls
        /// scrolling the transcript over code blocks. Pass the wheel on to the transcript instead.
        /// </summary>
        private void ForwardMouseWheel(UIElement element)
        {
            element.PreviewMouseWheel += (s, e) =>
            {
                if (e.Handled) return;
                e.Handled = true;
                Box.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = s });
            };
        }

        public TranscriptView()
        {
            _doc = new FlowDocument { PagePadding = new Thickness(0, 4, 0, 12), FontSize = 13, LineHeight = 19 };
            _doc.SetResourceReference(FlowDocument.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _doc.SetResourceReference(FlowDocument.FontFamilyProperty, VsFonts.EnvironmentFontFamilyKey);
            Box = new RichTextBox
            {
                Document = _doc,
                IsReadOnly = true,
                IsDocumentEnabled = true, // lets the embedded buttons and links be clicked
                BorderThickness = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            Box.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            Box.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            Box.Loaded += (s, e) =>
            {
                // Loaded fires again when the tool window is re-docked, and the template may be new: look it up again.
                if (_scroll != null) _scroll.ScrollChanged -= OnScrollChanged;
                _scroll = FindScrollViewer(Box);
                if (_scroll != null) _scroll.ScrollChanged += OnScrollChanged;
            };
        }

        public RichTextBox Box { get; }

        /// <summary>Whether to show message times (read on each message, so the option applies right away).</summary>
        public Func<bool> ShowTimestamps { get; set; } = () => true;
        private DateTime? _lastDay;

        /// <summary>A date line when the day changed since the last time shown.</summary>
        private void AddDayLine(DateTime local)
        {
            if (_lastDay == local.Date) return;
            bool first = _lastDay == null;
            _lastDay = local.Date;
            if (first && local.Date == DateTime.Today) return; // today's first message needs no date
            var p = new Paragraph(new Run(local.ToString("D"))) { FontSize = 11, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
            p.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
            _doc.Blocks.Add(p);
        }

        /// <summary>
        /// Quiet status text (footers, notes, hints) in a small muted font; <paramref name="error"/> shows it in the
        /// error color. Ends any answer being streamed.
        /// </summary>
        public void AppendPlain(string text, bool error = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EndAnswer();
            if (string.IsNullOrEmpty(text)) return;
            // Leading/trailing blank lines were used for spacing in the old layout; margins do that now.
            text = text.Replace("\r\n", "\n").Trim('\n');
            if (text.Length == 0) return;
            if (_plain == null)
            {
                _plain = new Paragraph { FontSize = 11.5, LineHeight = 17, Margin = new Thickness(0, 4, 0, 4) };
                _plain.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
                _doc.Blocks.Add(_plain);
            }
            if (_plain.Inlines.Count > 0) _plain.Inlines.Add(new LineBreak());
            var parts = text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) _plain.Inlines.Add(new LineBreak());
                var run = new Run(parts[i].Trim());
                if (error) run.Foreground = ChatTheme.Error;
                _plain.Inlines.Add(run);
            }
            ScrollToEndIfNear();
        }

        /// <summary>
        /// Your prompt as a rounded bubble, with an optional muted line (route, attachments) under it.
        /// <paramref name="onRewind"/> adds a Rewind button, shown while the mouse is over the bubble.
        /// </summary>
        public void AppendUserMessage(string text, string meta, Action onRewind = null, IReadOnlyList<ImageSource> images = null, DateTime? timeUtc = null, string metaTip = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EndAnswer();
            _plain = null;
            bool stamped = false;
            if (ShowTimestamps())
            {
                var local = (timeUtc ?? DateTime.UtcNow).ToLocalTime();
                AddDayLine(local);
                var time = new Paragraph(new Run(local.ToString("t"))) { FontSize = 10.5, Margin = new Thickness(2, 12, 0, 0) };
                time.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
                _doc.Blocks.Add(time);
                stamped = true;
            }

            var body = new TextBox
            {
                Text = text ?? "",
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                FontSize = 13,
            };
            ChatTheme.UseEnvironmentFont(body);
            body.FontSize = 13;
            ChatTheme.Text(body);
            ForwardMouseWheel(body);

            var stack = new StackPanel();
            if (images != null && images.Count > 0)
            {
                var strip = new WrapPanel { Margin = new Thickness(0, 0, 0, string.IsNullOrEmpty(text) ? 0 : 6) };
                foreach (var src in images)
                    strip.Children.Add(new Border
                    {
                        Child = new Image { Source = src, MaxHeight = 72, MaxWidth = 128, Stretch = Stretch.Uniform },
                        CornerRadius = new CornerRadius(4),
                        Margin = new Thickness(0, 0, 6, 0),
                        ClipToBounds = true,
                    });
                stack.Children.Add(strip);
            }
            if (!string.IsNullOrEmpty(text)) stack.Children.Add(body);
            if (!string.IsNullOrEmpty(meta))
            {
                var m = new TextBlock { FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, ToolTip = metaTip };
                ChatTheme.Muted(m);
                AddRouteInlines(m, meta);
                stack.Children.Add(m);
            }
            UIElement content = stack;
            Button rewind = null;
            if (onRewind != null)
            {
                rewind = ChatTheme.IconButton(ChatTheme.IconRewind, "Rewind: put the files Claude changed since this prompt back the way they were");
                rewind.FontSize = 11;
                rewind.Width = 22;
                rewind.Height = 20;
                rewind.Margin = new Thickness(6, -3, -6, 0);
                rewind.VerticalAlignment = VerticalAlignment.Top;
                rewind.Visibility = Visibility.Hidden;
                ChatTheme.Muted(rewind);
                rewind.Click += (s, e) => { e.Handled = true; onRewind(); };
                var row = new DockPanel();
                DockPanel.SetDock(rewind, Dock.Right);
                row.Children.Add(rewind);
                row.Children.Add(stack);
                content = row;
            }
            var bubble = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 7), Background = ChatTheme.Subtle, Child = content };
            if (rewind != null)
            {
                bubble.MouseEnter += (s, e) => rewind.Visibility = Visibility.Visible;
                bubble.MouseLeave += (s, e) => rewind.Visibility = Visibility.Hidden;
            }
            bubble.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            bubble.BorderThickness = new Thickness(1);
            LastUserMessage = new BlockUIContainer(bubble) { Margin = new Thickness(0, stamped ? 2 : 14, 0, 6) };
            _doc.Blocks.Add(LastUserMessage);
            Box.ScrollToEnd(); // you just sent it: always show it
        }

        /// <summary>The bubble of the prompt sent last (where a bookmark of its answer scrolls to).</summary>
        public Block LastUserMessage { get; private set; }

        /// <summary>
        /// The muted "✓ 3.2s · 12k tok · $0.004" line after an answer, with a star that bookmarks the answer.
        /// <paramref name="toggleBookmark"/> returns whether the answer is bookmarked now; null hides the star.
        /// </summary>
        public void AppendTurnFooter(string text, Func<bool> toggleBookmark, string answerToCopy = null, DateTime? timeUtc = null, string tooltip = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EndAnswer();
            _plain = null;
            text = (text ?? "").Trim();
            if (ShowTimestamps())
            {
                var local = (timeUtc ?? DateTime.UtcNow).ToLocalTime();
                AddDayLine(local);
                text = text.Length == 0 ? local.ToString("t") : text + " · " + local.ToString("t");
            }
            var label = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            ChatTheme.Muted(label);
            if (text.StartsWith("✓", StringComparison.Ordinal))
            {
                label.Inlines.Add(new Run("✓") { Foreground = ChatTheme.Accents(Box).Success, FontWeight = FontWeights.SemiBold });
                label.Inlines.Add(new Run(text.Substring(1)));
            }
            else label.Text = text;
            if (!string.IsNullOrEmpty(tooltip)) label.ToolTip = tooltip;
            var row = new DockPanel();
            if (toggleBookmark != null)
            {
                var star = ChatTheme.IconButton(ChatTheme.IconStar, "Bookmark this answer");
                star.FontSize = 12;
                star.Width = 24;
                star.Height = 22;
                star.Margin = new Thickness(0, 0, 4, 0);
                ChatTheme.Muted(star);
                star.Click += (s, e) =>
                {
                    bool on = toggleBookmark();
                    star.Content = on ? ChatTheme.IconStarFill : ChatTheme.IconStar;
                    star.ToolTip = on ? "Remove bookmark" : "Bookmark this answer";
                    System.Windows.Automation.AutomationProperties.SetName(star, (string)star.ToolTip);
                };
                DockPanel.SetDock(star, Dock.Left);
                row.Children.Add(star);
            }
            if (!string.IsNullOrWhiteSpace(answerToCopy))
            {
                var copy = ChatTheme.IconButton(ChatTheme.IconCopy, "Copy response (Markdown)");
                copy.FontSize = 12;
                copy.Width = 24;
                copy.Height = 22;
                copy.Margin = new Thickness(0, 0, 4, 0);
                ChatTheme.Muted(copy);
                string answer = answerToCopy.Trim();
                copy.Click += (s, e) =>
                {
                    try { Clipboard.SetText(answer); copy.Content = ChatTheme.IconCheck; copy.ToolTip = "Copied"; }
                    catch (Exception) { copy.ToolTip = "Clipboard busy, try again"; }
                };
                DockPanel.SetDock(copy, Dock.Left);
                row.Children.Add(copy);
            }
            row.Children.Add(label);
            _doc.Blocks.Add(new BlockUIContainer(row) { Margin = new Thickness(0, 2, 0, 4) });
            ScrollToEndIfNear();
        }

        /// <summary>
        /// "✂ Compacted · 698k tokens before" with a Show summary / Hide summary link that expands the summary Claude
        /// continues from, as Markdown. Returns a setter for when the summary is only read later (a live session).
        /// </summary>
        public Action<string> AppendCompaction(long preTokens, string trigger, string summary)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EndAnswer();
            _plain = null;
            var head = new Paragraph { FontSize = 12, Margin = new Thickness(0, 10, 0, 4) };
            head.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
            head.Inlines.Add(new Run("✂ Compacted" + (preTokens > 0 ? $" · {Core.Tokens.TokenMeter.Format(preTokens)} tokens before" : "") + (trigger == "auto" ? " · automatic" : "")));
            _doc.Blocks.Add(head);

            Section body = null;
            string text = null;
            Hyperlink link = null;
            void SetSummary(string s)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (string.IsNullOrWhiteSpace(s) || link != null || !_doc.Blocks.Contains(head)) return;
                text = s;
                link = new Hyperlink(new Run("Show summary")) { ToolTip = "What Claude kept from the conversation above", Foreground = ChatTheme.Accents(Box).Link };
                link.Click += (o, e) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (body != null && _doc.Blocks.Contains(body)) { _doc.Blocks.Remove(body); ((Run)link.Inlines.FirstInline).Text = "Show summary"; return; }
                    body = new Section { BorderBrush = QuoteBar, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0), Margin = new Thickness(0, 0, 0, 8), FontSize = 12.5 };
                    foreach (var md in MarkdownParser.Parse(text)) body.Blocks.Add(Render(md));
                    _doc.Blocks.InsertAfter(head, body);
                    ((Run)link.Inlines.FirstInline).Text = "Hide summary";
                };
                head.Inlines.Add(new Run("  ·  "));
                head.Inlines.Add(link);
            }
            SetSummary(summary);
            ScrollToEndIfNear();
            return SetSummary;
        }

        /// <summary>A saved answer from another conversation: title, the prompt, then the answer as Markdown.</summary>
        public Block AppendBookmark(Core.Chat.Bookmark b)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EndAnswer();
            _plain = null;
            var head = new Paragraph { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 2) };
            head.Inlines.Add(new Run(ChatTheme.IconStarFill + "  ") { FontFamily = ChatTheme.IconFont, FontWeight = FontWeights.Normal });
            head.Inlines.Add(new Run(b.Title));
            var when = new Run("  ·  bookmarked " + b.CreatedUtc.ToLocalTime().ToString("g")) { FontWeight = FontWeights.Normal, FontSize = 11 };
            when.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
            head.Inlines.Add(when);
            _doc.Blocks.Add(head);

            var section = new Section { BorderBrush = QuoteBar, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0), Margin = new Thickness(0, 0, 0, 8) };
            if (!string.IsNullOrWhiteSpace(b.Prompt))
            {
                var prompt = new Paragraph(new Run(b.Prompt.Trim())) { FontStyle = FontStyles.Italic, Margin = new Thickness(0, 2, 0, 6) };
                prompt.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
                section.Blocks.Add(prompt);
            }
            foreach (var md in MarkdownParser.Parse(b.Answer ?? "")) section.Blocks.Add(Render(md));
            _doc.Blocks.Add(section);
            return head;
        }

        /// <summary>Scrolls so <paramref name="block"/> is at the top of the transcript, if it is still shown.</summary>
        public bool ScrollTo(Block block)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (block == null || !_doc.Blocks.Contains(block)) return false;
            // The rect is relative to the visible area, so add it to the current offset.
            var rect = block.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (!rect.IsEmpty) Box.ScrollToVerticalOffset(Math.Max(0, Box.VerticalOffset + rect.Top - 8));
            else block.BringIntoView();
            return true;
        }

        /// <summary>The empty-chat welcome with a few tips.</summary>
        public void AppendWelcome()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EndAnswer();
            _plain = null;
            var title = new Paragraph(new Run("Claude Code")) { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) };
            var sub = new Paragraph(new Run("Token saver edition · each prompt goes to the cheapest model that can handle it")) { FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
            sub.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
            var tips = new List(new ListItem(new Paragraph(new Run("Select code and press Ctrl+Alt+K, S to ask about it; only the selection is sent."))))
            {
                MarkerStyle = TextMarkerStyle.Disc,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(18, 0, 0, 0),
            };
            tips.ListItems.Add(new ListItem(new Paragraph(new Run("Type @ to attach a file as a reference, / for commands, Ctrl+↑ for earlier prompts."))));
            tips.ListItems.Add(new ListItem(new Paragraph(new Run("The meter above shows context, cost and cache use; New starts a fresh, cheap context."))));
            tips.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
            _doc.Blocks.Add(title);
            _doc.Blocks.Add(sub);
            _doc.Blocks.Add(tips);
        }

        /// <summary>Streamed answer text; the current answer is re-rendered as Markdown.</summary>
        public void AppendAnswer(string delta)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(delta)) return;
            _plain = null;
            _answer.Append(delta);
            foreach (var b in _answerBlocks) _doc.Blocks.Remove(b);
            _answerBlocks.Clear();
            foreach (var md in MarkdownParser.Parse(_answer.ToString()))
            {
                var block = Render(md);
                _doc.Blocks.Add(block);
                _answerBlocks.Add(block);
            }
            if (_answerBlocks.Count > 0) _lastAnswerStart = _answerBlocks[0];
            // Like the terminal: a ● in front of each piece of text Claude writes between tool calls.
            if (_answerBlocks.Count > 0 && _answerBlocks[0] is Paragraph first && first.Inlines.FirstInline != null)
            {
                var bullet = new Run("● ");
                bullet.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                first.Inlines.InsertBefore(first.Inlines.FirstInline, bullet);
            }
            ScrollToEndIfNear();
        }

        // First block of the text Claude wrote last (for Focus last message).
        private Block _lastAnswerStart;

        /// <summary>
        /// Puts the keyboard caret at the start of Claude's last answer, so a screen reader reads it from there
        /// (arrow keys move through it). False when there is no answer yet.
        /// </summary>
        public bool FocusLastAnswer()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var b = _lastAnswerStart;
            if (b == null || b.Parent == null) return false; // not in the document any more
            Box.IsReadOnlyCaretVisible = true; // a visible caret while reading with the keyboard
            Box.Focus();
            System.Windows.Input.Keyboard.Focus(Box);
            Box.Selection.Select(b.ContentStart, b.ContentStart);
            b.BringIntoView();
            return true;
        }

        private sealed class ToolRow
        {
            public TextBlock Header;
            public Run Dot;
            public string ToolName;
            public Core.Context.InlineDiff Diff;
            public UIElement DiffElement;
            public StackPanel Result;   // the ⎿ lines, always visible
            public StackPanel Body;     // input and full output, shown when the row is opened
            public Panel Expander;      // the row's stack: header, ⎿ lines, diff
            public Action OnDiff;
            public string OpenPath;
            public TextBlock AgentUsage; // under an Agent row: what the agent used, once it finished
        }

        // Agent rows by tool_use_id, kept after their result (a background agent finishes long after it)
        private readonly Dictionary<string, ToolRow> _agentRows = new Dictionary<string, ToolRow>();

        /// <summary>
        /// Under the Agent row that started a subagent: "Done · 24.6k tok · $0.042 · 3 tools" (see
        /// <see cref="Core.Cli.ToolCallView.AgentUsageLine"/>). Replaces an earlier line for the same agent.
        /// </summary>
        public void ShowAgentUsage(string toolUseId, string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(toolUseId) || string.IsNullOrEmpty(text) || !_agentRows.TryGetValue(toolUseId, out var row)) return;
            if (row.AgentUsage == null)
            {
                row.AgentUsage = new TextBlock { FontSize = 11.5, Margin = new Thickness(18, 0, 0, 0) };
                ChatTheme.Muted(row.AgentUsage);
                int at = row.Expander.Children.IndexOf(row.Result);
                if (at >= 0) row.Expander.Children.Insert(at + 1, row.AgentUsage);
                else row.Expander.Children.Add(row.AgentUsage);
            }
            row.AgentUsage.Text = "    " + text; // under the ⎿ line, lined up with its text
        }

        /// <summary>
        /// A tool call the way the Claude Code terminal shows it: "● Update(src/a.cs)", then (from
        /// <see cref="CompleteTool"/>) a "⎿ Added 3 lines" summary or the first lines of the output, and for a file
        /// change the numbered red / green lines. Clicking the row shows the input and the whole output.
        /// Without <paramref name="toolName"/> the older "Bash: command" header is used.
        /// </summary>
        public void AppendTool(string toolUseId, string description, string inputJson, Action onDiff = null, Core.Context.InlineDiff fileDiff = null,
            string toolName = null, Newtonsoft.Json.Linq.JObject input = null, string baseDir = null, string openPath = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EndAnswer();
            _plain = null;

            var accents = ChatTheme.Accents(Box);
            Run dot;
            var header = toolName != null
                ? CliToolHeader(toolName, input, baseDir, description, accents, out dot)
                : ToolHeader(description, accents, out dot);
            object headerContent = header;
            bool inlineDiff = fileDiff != null && fileDiff.Lines.Count > 0;
            if (onDiff != null && !inlineDiff) // with the lines shown, the links above them do this
            {
                var diff = SmallButton("Diff", "Compare the file before and after this change");
                diff.Click += (s, e) => { onDiff(); e.Handled = true; };
                var row = new DockPanel();
                DockPanel.SetDock(diff, Dock.Right);
                row.Children.Add(diff);
                row.Children.Add(header);
                headerContent = row;
            }
            var body = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(inputJson) ? "(no input)" : inputJson,
                IsReadOnly = true,
                FontFamily = Mono,
                FontSize = 11,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, // the whole input; the chat scrolls, not this box
                Margin = new Thickness(16, 2, 0, 2),
            };
            body.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            ForwardMouseWheel(body);
            var bodyPanel = new StackPanel();
            bodyPanel.Children.Add(body);
            var expander = Collapsible(headerContent, bodyPanel, new Thickness(0, 1, 0, 1));
            var result = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
            expander.Children.Add(result);
            string runNote = toolName != null ? Core.Cli.ToolCallView.RunNote(toolName, input) : null;
            if (runNote != null)
            {
                // "⎿ (timeout 10m)": how Claude set the command to run, so a long one doesn't look stuck.
                var note = new TextBlock { Text = "⎿  " + runNote, FontSize = 11.5, Margin = new Thickness(18, 0, 0, 0) };
                ChatTheme.Muted(note);
                expander.Children.Add(note);
            }
            UIElement diffElement = null;
            if (inlineDiff)
            {
                var block = new StackPanel();
                block.Children.Add(FileLinks(openPath ?? fileDiff.FilePath, onDiff, accents, new Thickness(36, 2, 0, 1)));
                block.Children.Add(DiffView(fileDiff, accents));
                expander.Children.Add(diffElement = block);
            }
            var toolRow = new ToolRow { Header = header, Dot = dot, ToolName = toolName, Diff = fileDiff, DiffElement = diffElement, Result = result, Body = bodyPanel,
                Expander = expander, OnDiff = onDiff, OpenPath = openPath };
            if ((toolName == "Agent" || toolName == "Task") && !string.IsNullOrEmpty(toolUseId)) _agentRows[toolUseId] = toolRow;

            if (FocusView())
            {
                var group = CurrentToolGroup();
                group.Rows.Children.Add(expander);
                group.Add(toolUseId, description);
                if (!string.IsNullOrEmpty(toolUseId)) _toolGroupOf[toolUseId] = group;
            }
            else if (IsLookCall(toolName, input) && !IsSubagentCall(description))
            {
                var look = CurrentLookGroup();
                look.Add(toolUseId, toolName, expander);
                if (!string.IsNullOrEmpty(toolUseId)) _lookGroupOf[toolUseId] = look;
            }
            else _doc.Blocks.Add(new BlockUIContainer(expander) { Margin = new Thickness(0) });
            if (!string.IsNullOrEmpty(toolUseId)) _tools[toolUseId] = toolRow;
            ScrollToEndIfNear();
        }

        /// <summary>
        /// The line under a prompt. "Auto → Sonnet, medium effort" gets the model in its meter color (Opus orange,
        /// Sonnet blue, Haiku green) so the pick stands out; the rest stays gray.
        /// </summary>
        private void AddRouteInlines(TextBlock line, string meta)
        {
            const string auto = "Auto → ";
            if (!meta.StartsWith(auto, StringComparison.Ordinal)) { line.Text = meta; return; }
            var a = ChatTheme.Accents(Box);
            string rest = meta.Substring(auto.Length);
            int end = 0;
            while (end < rest.Length && char.IsLetter(rest[end])) end++;
            string word = rest.Substring(0, end);
            Brush color = word == "Opus" ? a.Brand : word == "Sonnet" ? a.Link : word == "Haiku" ? a.Success : null;
            line.Inlines.Add(new Run(auto));
            if (color == null) { line.Inlines.Add(new Run(rest)); return; }
            line.Inlines.Add(new Run(word) { Foreground = color, FontWeight = FontWeights.SemiBold });
            line.Inlines.Add(new Run(rest.Substring(end)));
        }

        /// <summary>"Open diff · Open file" above a change's lines.</summary>
        private static TextBlock FileLinks(string path, Action onDiff, ChatTheme.AccentSet a, Thickness margin)
        {
            var links = new TextBlock { FontSize = 11.5, Margin = margin };
            if (onDiff != null)
            {
                links.Inlines.Add(Link("Open diff", "Visual Studio's diff: the file before this change and now", onDiff, a));
                var sep = new Run("  ·  ");
                sep.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
                links.Inlines.Add(sep);
            }
            links.Inlines.Add(Link("Open file", path, () => { ThreadHelper.ThrowIfNotOnUIThread(); OpenPath(path); }, a));
            return links;
        }

        private static Hyperlink Link(string text, string tip, Action onClick, ChatTheme.AccentSet a)
        {
            var link = new Hyperlink(new Run(text)) { Foreground = a.Link, ToolTip = tip, Cursor = System.Windows.Input.Cursors.Hand, TextDecorations = null };
            link.MouseEnter += (s, e) => link.TextDecorations = TextDecorations.Underline;
            link.MouseLeave += (s, e) => link.TextDecorations = null;
            link.Click += (s, e) => { onClick(); e.Handled = true; };
            return link;
        }

        private static void OpenPath(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { if (System.IO.File.Exists(path)) VsContext.OpenFile(path); }
            catch (Exception ex) { Debug.WriteLine("Open file failed: " + ex.Message); }
        }

        /// <summary>
        /// "Files changed" after an answer: one line per file the turn added, changed or deleted, with +added −removed,
        /// the path (opens the file) and a Diff link (the file before the turn against now).
        /// </summary>
        public void AppendChangedFiles(IReadOnlyList<Core.Context.ChangedFile> files, string baseDir, Action<Core.Context.ChangedFile> onDiff, Action<string> onOpen)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EndAnswer();
            _plain = null;
            var a = ChatTheme.Accents(Box);

            var panel = new StackPanel();
            var title = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) };
            title.Inlines.Add(new Run(files.Count == 1 ? "1 file changed" : $"{files.Count} files changed"));
            int added = files.Sum(f => f.Added ?? 0), removed = files.Sum(f => f.Removed ?? 0);
            if (added > 0) title.Inlines.Add(new Run("  +" + added) { Foreground = a.Success });
            if (removed > 0) title.Inlines.Add(new Run("  −" + removed) { Foreground = a.Failure });
            ChatTheme.Text(title);
            panel.Children.Add(title);

            foreach (var f in files)
            {
                var line = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 1), ToolTip = f.Path };
                var mark = new Run(f.IsNew ? "+ " : f.IsDeleted ? "− " : "~ ") { FontFamily = Mono, Foreground = f.IsNew ? a.Success : f.IsDeleted ? a.Failure : a.Running };
                line.Inlines.Add(mark);
                var file = f;
                string shown = Core.Cli.ToolCallView.Relative(f.Path, baseDir)?.Replace('\\', '/');
                if (f.IsDeleted) { var r = new Run(shown) { TextDecorations = TextDecorations.Strikethrough }; r.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey); line.Inlines.Add(r); }
                else { var open = Link(shown, "Open " + f.Path, () => onOpen(file.Path), a); open.Foreground = a.Path; line.Inlines.Add(open); }
                string note = f.IsNew ? "  new" : f.IsDeleted ? "  deleted" : "";
                if (note.Length > 0) { var r = new Run(note); r.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey); line.Inlines.Add(r); }
                if (f.Added > 0) line.Inlines.Add(new Run("  +" + f.Added) { Foreground = a.Success });
                if (f.Removed > 0) line.Inlines.Add(new Run("  −" + f.Removed) { Foreground = a.Failure });
                if (f.Before != null)
                {
                    line.Inlines.Add(new Run("   "));
                    line.Inlines.Add(Link("Diff", "Visual Studio's diff: before this answer and now", () => onDiff(file), a));
                }
                panel.Children.Add(line);
            }

            var card = new Border { Child = panel, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = ChatTheme.Hover, Background = ChatTheme.Subtle, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 2, 0, 8) };
            System.Windows.Automation.AutomationProperties.SetName(card, title.Text);
            _doc.Blocks.Add(new BlockUIContainer(card) { Margin = new Thickness(0) });
            ScrollToEndIfNear();
        }

        private static readonly HashSet<string> PathTitleTools = new HashSet<string>(StringComparer.Ordinal) { "Update", "Write", "Read" };

        /// <summary>"● Update(src/a.cs)": status dot, the CLI's name for the tool in bold, its argument in parentheses.</summary>
        private static TextBlock CliToolHeader(string toolName, Newtonsoft.Json.Linq.JObject input, string baseDir, string description, ChatTheme.AccentSet a, out Run dot)
        {
            var tb = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = description };
            ChatTheme.Muted(tb);
            dot = new Run("● ");
            dot.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
            tb.Inlines.Add(dot);
            if (description != null && description.StartsWith(Core.Cli.ClaudeStreamParser.SubagentToolPrefix, StringComparison.Ordinal))
                tb.Inlines.Add(new Run(Core.Cli.ClaudeStreamParser.SubagentToolPrefix));
            var (name, arg) = Core.Cli.ToolCallView.Title(toolName, input, baseDir);
            var n = new Run(name) { FontWeight = FontWeights.SemiBold };
            n.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            tb.Inlines.Add(n);
            if (!string.IsNullOrEmpty(arg))
            {
                tb.Inlines.Add(new Run("("));
                var r = new Run(arg);
                if (ShellTools.Contains(toolName)) { r.FontFamily = Mono; r.FontSize = 12; r.Foreground = a.Code; }
                else if (PathTitleTools.Contains(name)) r.Foreground = a.Path;
                else r.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                tb.Inlines.Add(r);
                tb.Inlines.Add(new Run(")"));
            }
            return tb;
        }

        /// <summary>The ⎿ lines under a finished tool call, and its whole output in the row's body.</summary>
        private void ShowResult(ToolRow row, string resultText, bool isError, ChatTheme.AccentSet a, Newtonsoft.Json.Linq.JObject resultData = null)
        {
            var summary = Core.Cli.ToolCallView.Summarize(row.ToolName, resultText, isError, row.Diff, resultData);
            row.Result.Children.Clear();
            for (int i = 0; i < summary.Lines.Count; i++)
            {
                var line = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
                var lead = new Run(i == 0 ? "⎿  " : "    ");
                lead.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
                line.Inlines.Add(lead);
                var text = new Run(summary.Lines[i]);
                bool output = summary.Lines.Count > 1 || summary.MoreLines > 0 || ShellTools.Contains(row.ToolName);
                if (output) { text.FontFamily = Mono; text.FontSize = 11.5; }
                if (summary.IsError) text.Foreground = a.Failure;
                else text.SetResourceReference(TextElement.ForegroundProperty, output ? EnvironmentColors.SystemGrayTextBrushKey : EnvironmentColors.ToolWindowTextBrushKey);
                line.Inlines.Add(text);
                row.Result.Children.Add(line);
            }
            if (summary.MoreLines > 0)
            {
                var more = new TextBlock { Text = $"    … +{summary.MoreLines} lines (click the row to see all)", FontSize = 11.5 };
                ChatTheme.Muted(more);
                row.Result.Children.Add(more);
            }
            // A failed change didn't happen: hide its diff.
            if (isError && row.DiffElement != null) row.DiffElement.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrWhiteSpace(resultText))
            {
                var label = new TextBlock { Text = "Output", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 4, 0, 0) };
                ChatTheme.Muted(label);
                var output = new TextBox
                {
                    Text = resultText.TrimEnd(),
                    IsReadOnly = true,
                    FontFamily = Mono,
                    FontSize = 11,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, // the whole output; the chat scrolls, not this box
                    Margin = new Thickness(16, 2, 0, 4),
                };
                output.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                ForwardMouseWheel(output);
                row.Body.Children.Add(label);
                row.Body.Children.Add(output);
            }
        }

        /// <summary>
        /// A row that opens and closes when its header is clicked (or Space / Enter on it), without the expander's
        /// round chevron: the header gets a light hover background instead. Returns the panel (header, content).
        /// </summary>
        private static StackPanel Collapsible(object header, UIElement content, Thickness margin)
        {
            var toggle = new ToggleButton
            {
                Content = header,
                Template = RowToggleTemplate,
                Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(3, 1, 3, 1),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            toggle.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            if (header is TextBlock tb) System.Windows.Automation.AutomationProperties.SetName(toggle, tb.Text);
            content.Visibility = Visibility.Collapsed;
            toggle.Checked += (s, e) => content.Visibility = Visibility.Visible;
            toggle.Unchecked += (s, e) => content.Visibility = Visibility.Collapsed;
            var panel = new StackPanel { Margin = margin };
            panel.Children.Add(toggle);
            panel.Children.Add(content);
            return panel;
        }

        private static readonly ControlTemplate RowToggleTemplate = CreateRowToggleTemplate();

        private static ControlTemplate CreateRowToggleTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "Bd");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            var t = new ControlTemplate(typeof(ToggleButton)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, ChatTheme.Hover, "Bd"));
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BackgroundProperty, ChatTheme.Hover, "Bd"));
            t.Triggers.Add(hover);
            t.Triggers.Add(focus);
            return t;
        }

        private const int MaxDiffLines = 300;
        private const int DiffPreviewLines = 12;

        /// <summary>
        /// The changed lines the way the Claude Code terminal shows them: a line-number gutter, then − / + and the
        /// code on a full-width dark red / green row, with syntax colors from the file's extension. No scroll box of
        /// its own: a long diff shows its first lines and "… +N lines" expands it in place (at most 300 lines; the
        /// Diff button shows everything).
        /// </summary>
        private UIElement DiffView(Core.Context.InlineDiff d, ChatTheme.AccentSet a)
        {
            Color Of(Brush b) => (b as SolidColorBrush)?.Color ?? Colors.Gray;
            Brush Tint(Brush b, byte alpha) { var c = Of(b); var br = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)); br.Freeze(); return br; }
            var addBack = Tint(a.Success, 0x38);
            var delBack = Tint(a.Failure, 0x38);
            var palette = CurrentPalette();
            string language = System.IO.Path.GetExtension(d.FilePath ?? "").TrimStart('.');
            int widest = d.Lines.Take(MaxDiffLines).Select(l => l.Number ?? 0).DefaultIfEmpty(0).Max().ToString().Length;
            bool numbered = d.Lines.Any(l => l.Number != null);

            UIElement Line(Core.Context.DiffLine l)
            {
                if (l.Kind == Core.Context.DiffLineKind.Gap)
                {
                    var gap = new TextBlock { Text = new string(' ', numbered ? widest + 1 : 0) + "  ⋮", FontFamily = Mono, FontSize = 11.5 };
                    ChatTheme.Muted(gap);
                    return gap;
                }
                var text = new TextBlock { FontFamily = Mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0, 0, 8, 0) };
                if (numbered)
                {
                    var num = new Run((l.Number?.ToString() ?? "").PadLeft(widest) + " ");
                    num.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
                    text.Inlines.Add(num);
                }
                switch (l.Kind)
                {
                    case Core.Context.DiffLineKind.Added:
                        text.Background = addBack;
                        text.Inlines.Add(new Run("+ ") { Foreground = a.Success });
                        break;
                    case Core.Context.DiffLineKind.Removed:
                        text.Background = delBack;
                        text.Inlines.Add(new Run("- ") { Foreground = a.Failure });
                        break;
                    default:
                        text.Inlines.Add(new Run("  "));
                        break;
                }
                string code = l.Text.Replace("\t", "    ");
                int pos = 0;
                foreach (var s in SyntaxHighlighter.Tokenize(code, language))
                {
                    if (s.Start > pos) text.Inlines.Add(CodeRun(code.Substring(pos, s.Start - pos), null, l.Kind));
                    text.Inlines.Add(CodeRun(code.Substring(s.Start, s.Length), palette[s.Kind], l.Kind));
                    pos = s.Start + s.Length;
                }
                if (pos < code.Length) text.Inlines.Add(CodeRun(code.Substring(pos), null, l.Kind));
                return text;
            }

            var shown = d.Lines.Take(MaxDiffLines).ToList();
            // Only fold when it saves a real amount: never hide just a line or two behind a link.
            int preview = shown.Count > DiffPreviewLines + 3 ? DiffPreviewLines : shown.Count;
            var view = new StackPanel { Margin = new Thickness(36, 2, 0, 6) };
            foreach (var l in shown.Take(preview)) view.Children.Add(Line(l));

            if (preview < shown.Count)
            {
                StackPanel rest = null; // built on the first expand
                var toggle = new TextBlock { FontSize = 11.5, Padding = new Thickness(0, 2, 8, 0) };
                void ShowCollapsed()
                {
                    toggle.Inlines.Clear();
                    toggle.Inlines.Add(Link($"… +{d.Lines.Count - preview} lines (click to see all)", "Show the whole change here", Expand, a));
                }
                void Expand()
                {
                    if (rest == null)
                    {
                        rest = new StackPanel();
                        foreach (var l in shown.Skip(preview)) rest.Children.Add(Line(l));
                        if (d.Lines.Count > MaxDiffLines)
                        {
                            var more = new TextBlock { Text = $"… {d.Lines.Count - MaxDiffLines} more lines (Open diff shows everything)", FontSize = 11, Padding = new Thickness(4, 2, 8, 2) };
                            ChatTheme.Muted(more);
                            rest.Children.Add(more);
                        }
                        view.Children.Insert(view.Children.IndexOf(toggle), rest);
                    }
                    rest.Visibility = Visibility.Visible;
                    toggle.Inlines.Clear();
                    toggle.Inlines.Add(Link("Show less", "Fold the change back to its first lines", () => { rest.Visibility = Visibility.Collapsed; ShowCollapsed(); }, a));
                }
                ShowCollapsed();
                view.Children.Add(toggle);
            }

            System.Windows.Automation.AutomationProperties.SetName(view, $"{System.IO.Path.GetFileName(d.FilePath)}: {d.Added} lines added, {d.Removed} removed");
            return view;
        }

        /// <summary>A piece of a diff line: syntax color when it has one, else the text color (gray for unchanged lines).</summary>
        private static Run CodeRun(string text, Brush color, Core.Context.DiffLineKind kind)
        {
            var r = new Run(text);
            if (color != null) r.Foreground = color;
            else r.SetResourceReference(TextElement.ForegroundProperty, kind == Core.Context.DiffLineKind.Context ? EnvironmentColors.SystemGrayTextBrushKey : EnvironmentColors.ToolWindowTextBrushKey);
            return r;
        }

        private static readonly HashSet<string> ShellTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Bash", "PowerShell", "BashOutput", "Monitor" };
        private static readonly HashSet<string> FileTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Read", "Write", "Edit", "MultiEdit", "NotebookEdit", "Glob", "Grep", "LS" };

        /// <summary>
        /// "● Bash  cd src &amp;&amp; dotnet test": a status dot (gray until the result arrives, then green or red), the
        /// tool name in bold, then its argument: colored monospace for shell commands, path-colored for file tools.
        /// </summary>
        private static TextBlock ToolHeader(string description, ChatTheme.AccentSet a, out Run dot)
        {
            var tb = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = description };
            ChatTheme.Muted(tb);
            dot = new Run("●  ") { FontSize = 11 };
            dot.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
            tb.Inlines.Add(dot);
            string d = description ?? "";
            int colon = d.IndexOf(": ", StringComparison.Ordinal);
            string name = colon > 0 && colon < 40 ? d.Substring(0, colon) : null;
            if (name == null) { tb.Inlines.Add(new Run(d)); return tb; }
            var n = new Run(name) { FontWeight = FontWeights.SemiBold };
            n.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            tb.Inlines.Add(n);
            tb.Inlines.Add(new Run("  "));
            var arg = new Run(d.Substring(colon + 2));
            if (ShellTools.Contains(name)) { arg.FontFamily = Mono; arg.FontSize = 11.5; arg.Foreground = a.Code; }
            else if (FileTools.Contains(name)) arg.Foreground = a.Path;
            tb.Inlines.Add(arg);
            return tb;
        }

        /// <summary>
        /// A card for Claude's AskUserQuestion: one group per question (radio buttons, or check boxes when several
        /// answers are allowed), each option's description, an "Other" text field, and an Answer button.
        /// <paramref name="onSubmit"/> gets question text → chosen labels; the card then locks.
        /// </summary>
        public void AppendQuestionCard(IReadOnlyList<Core.Cli.Question> questions, Action<Dictionary<string, IReadOnlyList<string>>> onSubmit)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EndAnswer();
            _plain = null;

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "❓ Claude has a question", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), Foreground = ChatTheme.Accents(Box).Brand });
            var readers = new List<Func<KeyValuePair<string, IReadOnlyList<string>>>>();
            string group = "q" + Guid.NewGuid().ToString("N");

            for (int qi = 0; qi < questions.Count; qi++)
            {
                var q = questions[qi];
                panel.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(q.Header) ? q.Text : q.Header + " — " + q.Text,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, qi == 0 ? 0 : 8, 0, 2),
                });
                var choices = new List<(ToggleButton Box, string Label)>();
                // Options can carry a preview (mockup or snippet): show the hovered one, else the checked one.
                var preview = new TextBox
                {
                    IsReadOnly = true, FontFamily = Mono, FontSize = 11.5, TextWrapping = TextWrapping.NoWrap, MaxHeight = 220,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Background = CodeBackground, BorderThickness = new Thickness(0), Padding = new Thickness(6, 4, 6, 4),
                    Margin = new Thickness(8, 4, 0, 2), Visibility = Visibility.Collapsed,
                };
                preview.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                void ShowPreview(string text)
                {
                    preview.Text = text ?? "";
                    preview.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
                }
                string CheckedPreview() => q.Options.Zip(choices, (o, c) => (o, c)).Where(p => p.c.Box.IsChecked == true).Select(p => p.o.Preview).FirstOrDefault(p => !string.IsNullOrEmpty(p));
                foreach (var o in q.Options)
                {
                    var content = new TextBlock { TextWrapping = TextWrapping.Wrap };
                    content.Inlines.Add(new Run(o.Label));
                    if (!string.IsNullOrEmpty(o.Description)) content.Inlines.Add(new Run(" — " + o.Description) { FontStyle = FontStyles.Italic });
                    var box = MakeChoice(q.MultiSelect, group + qi, content);
                    choices.Add((box, o.Label));
                    panel.Children.Add(box);
                    if (!string.IsNullOrEmpty(o.Preview))
                    {
                        string p = o.Preview;
                        box.MouseEnter += (s, e) => ShowPreview(p);
                        box.GotKeyboardFocus += (s, e) => ShowPreview(p);
                    }
                    box.MouseLeave += (s, e) => ShowPreview(CheckedPreview());
                    box.Checked += (s, e) => ShowPreview(CheckedPreview());
                    box.Unchecked += (s, e) => ShowPreview(CheckedPreview());
                }
                if (q.Options.Any(o => !string.IsNullOrEmpty(o.Preview))) ForwardMouseWheel(preview);
                var otherText = new TextBox { MinWidth = 160, Margin = new Thickness(4, 0, 0, 0) };
                var other = MakeChoice(q.MultiSelect, group + qi, new TextBlock { Text = "Other:", VerticalAlignment = VerticalAlignment.Center });
                otherText.GotKeyboardFocus += (s, e) => other.IsChecked = true;
                var otherRow = new StackPanel { Orientation = Orientation.Horizontal };
                otherRow.Children.Add(other);
                otherRow.Children.Add(otherText);
                panel.Children.Add(otherRow);
                panel.Children.Add(preview);

                string text = q.Text;
                readers.Add(() =>
                {
                    var picked = choices.Where(c => c.Box.IsChecked == true).Select(c => c.Label).ToList();
                    if (other.IsChecked == true && !string.IsNullOrWhiteSpace(otherText.Text)) picked.Add(otherText.Text.Trim());
                    return new KeyValuePair<string, IReadOnlyList<string>>(text, picked);
                });
            }

            var submit = SmallButton("Answer", "Send your answer to Claude");
            submit.HorizontalAlignment = HorizontalAlignment.Left;
            submit.Margin = new Thickness(0, 8, 0, 0);
            submit.Click += (s, e) =>
            {
                var answers = readers.Select(r => r()).ToDictionary(kv => kv.Key, kv => kv.Value);
                panel.IsEnabled = false;
                submit.Content = "Answered";
                onSubmit(answers);
            };
            panel.Children.Add(submit);
            AddCard(panel);
        }

        /// <summary>
        /// A card for ExitPlanMode: the plan rendered as Markdown, then Approve, or Request changes with feedback.
        /// </summary>
        public void AppendPlanCard(string planMarkdown, Action onApprove, Action<string> onRevise, Action onOpenDocument = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EndAnswer();
            _plain = null;

            _doc.Blocks.Add(new Paragraph(new Run("📋 Plan for approval")) { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2), Foreground = ChatTheme.Accents(Box).Brand });
            var section = new Section { BorderBrush = QuoteBar, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0) };
            foreach (var md in MarkdownParser.Parse(planMarkdown)) section.Blocks.Add(Render(md));
            _doc.Blocks.Add(section);

            var panel = new StackPanel();
            var feedback = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 40, ToolTip = "What should change? (only for Request changes)" };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var approve = SmallButton("Approve", "Let Claude leave plan mode and start the work");
            var revise = SmallButton("Request changes", "Send the feedback above and ask for a new plan");
            approve.Margin = new Thickness(0);
            buttons.Children.Add(approve);
            buttons.Children.Add(revise);
            if (onOpenDocument != null)
            {
                var open = SmallButton("Open as document", "Open the plan in the editor. Type comments anywhere or delete lines; Request changes sends them to Claude.");
                open.Click += (s, e) => onOpenDocument();
                buttons.Children.Add(open);
            }
            panel.Children.Add(new TextBlock { Text = onOpenDocument != null ? "Feedback (optional; comments in the plan document are sent too):" : "Feedback (optional):", Opacity = 0.75, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(feedback);
            panel.Children.Add(buttons);
            approve.Click += (s, e) => { panel.IsEnabled = false; approve.Content = "Approved"; onApprove(); };
            revise.Click += (s, e) => { panel.IsEnabled = false; revise.Content = "Changes requested"; onRevise(feedback.Text); };
            AddCard(panel);
        }

        private const int MaxReviewLines = 12;

        /// <summary>
        /// A proposed Edit or Write split into its changes, each with a check box and its removed (−) and added (+)
        /// lines. Apply selected sends the checked ones (all checked = allow, none = reject); Reject all rejects.
        /// </summary>
        public void AppendEditReviewCard(string fileName, Core.Context.EditReview review, Action onApply, Action onRejectAll, Action onOpenDiff)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EndAnswer();
            _plain = null;
            var accents = ChatTheme.Accents(Box);

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = $"✎ Claude wants to {(review.ToolName == "Write" ? "write" : "edit")} {fileName}: {review.Hunks.Count} changes",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2),
                Foreground = accents.Brand,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock { Text = "Uncheck the changes you don't want. Claude is told which ones you rejected.", Opacity = 0.75, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

            var apply = SmallButton("Apply all", "Apply the checked changes");
            foreach (var hunk in review.Hunks)
            {
                var h = hunk;
                var box = new CheckBox { IsChecked = h.Accepted, Content = $"Change {h.Index + 1} · {h.Label}", Margin = new Thickness(0, 6, 0, 2) };
                box.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                box.Checked += (s, e) => { h.Accepted = true; UpdateApply(); };
                box.Unchecked += (s, e) => { h.Accepted = false; UpdateApply(); };
                panel.Children.Add(box);

                var lines = new TextBlock { FontFamily = ChatTheme.Mono, FontSize = 11.5, Margin = new Thickness(20, 0, 0, 0), TextWrapping = TextWrapping.NoWrap };
                void Add(IReadOnlyList<string> src, string sign, Brush color)
                {
                    foreach (var l in src.Take(MaxReviewLines))
                    {
                        if (lines.Inlines.Count > 0) lines.Inlines.Add(new LineBreak());
                        lines.Inlines.Add(new Run(sign + " " + l) { Foreground = color });
                    }
                    if (src.Count > MaxReviewLines)
                    {
                        lines.Inlines.Add(new LineBreak());
                        lines.Inlines.Add(new Run($"  … {src.Count - MaxReviewLines} more lines") { Foreground = Brushes.Gray });
                    }
                }
                Add(h.OldLines, "−", accents.Failure);
                Add(h.NewLines, "+", accents.Success);
                panel.Children.Add(new ScrollViewer { Content = lines, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var reject = SmallButton("Reject all", "Apply none of these changes");
            apply.Margin = new Thickness(0);
            buttons.Children.Add(apply);
            buttons.Children.Add(reject);
            if (onOpenDiff != null)
            {
                var diff = SmallButton("Open diff", "Compare the whole file before and after, in Visual Studio's diff window");
                diff.Click += (s, e) => onOpenDiff();
                buttons.Children.Add(diff);
            }
            panel.Children.Add(buttons);

            void UpdateApply()
            {
                apply.Content = review.AllAccepted ? "Apply all" : review.NoneAccepted ? "Apply none" : $"Apply {review.Hunks.Count(x => x.Accepted)} of {review.Hunks.Count}";
                apply.IsEnabled = !review.NoneAccepted;
            }
            apply.Click += (s, e) => { panel.IsEnabled = false; apply.Content = "Applied"; onApply(); };
            reject.Click += (s, e) => { panel.IsEnabled = false; reject.Content = "Rejected"; onRejectAll(); };
            AddCard(panel);
        }

        private ToggleButton MakeChoice(bool multi, string group, object content)
        {
            ToggleButton box = multi ? (ToggleButton)new CheckBox() : new RadioButton { GroupName = group };
            box.Content = content;
            box.Margin = new Thickness(8, 2, 0, 2);
            box.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return box;
        }

        private void AddCard(UIElement content)
        {
            var border = new Border { Background = CodeBackground, CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 6, 8, 8), Child = content };
            _doc.Blocks.Add(new BlockUIContainer(border) { Margin = new Thickness(0, 4, 0, 4) });
            Box.ScrollToEnd(); // the user has to act on it, so always bring it into view
        }

        // ---------------- Focus view ----------------

        /// <summary>Focus view: consecutive tool calls collapse into one summary row (read on each tool call).</summary>
        public Func<bool> FocusView { get; set; } = () => false;
        private ToolGroup _toolGroup;
        private readonly Dictionary<string, ToolGroup> _toolGroupOf = new Dictionary<string, ToolGroup>();

        /// <summary>The group the next tool call joins: the last one, while nothing else was added after it.</summary>
        private ToolGroup CurrentToolGroup()
        {
            if (_toolGroup != null && _doc.Blocks.LastBlock == _toolGroup.Block) return _toolGroup;
            _toolGroup = new ToolGroup(ChatTheme.Accents(Box));
            _doc.Blocks.Add(_toolGroup.Block);
            return _toolGroup;
        }

        /// <summary>
        /// "⚙ 4 tool calls · Read ×3, Edit · ~6.2k tok", with "running Edit: Foo.cs" while one is pending;
        /// expands to the usual tool rows.
        /// </summary>
        private sealed class ToolGroup
        {
            private readonly TextBlock _header = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            private readonly ChatTheme.AccentSet _accents;
            private readonly List<string> _names = new List<string>();
            private readonly Dictionary<string, string> _running = new Dictionary<string, string>();
            private long _tokens;
            private int _failed;

            public ToolGroup(ChatTheme.AccentSet accents)
            {
                _accents = accents;
                ChatTheme.Muted(_header);
                Rows = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
                var expander = Collapsible(_header, Rows, new Thickness(0, 1, 0, 1));
                Block = new BlockUIContainer(expander) { Margin = new Thickness(0) };
            }

            public BlockUIContainer Block { get; }
            public StackPanel Rows { get; }

            public void Add(string id, string description)
            {
                string d = description ?? "";
                int colon = d.IndexOf(':');
                _names.Add(colon > 0 ? d.Substring(0, colon) : d);
                if (!string.IsNullOrEmpty(id)) _running[id] = d;
                Update();
            }

            public void Complete(string id, int resultChars, bool isError)
            {
                _running.Remove(id);
                _tokens += Core.Tokens.TokenEstimate.FromChars(resultChars);
                if (isError) _failed++;
                Update();
            }

            private void Update()
            {
                var counts = _names.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
                string text = $"{_names.Count} tool call{(_names.Count == 1 ? "" : "s")} · {string.Join(", ", counts)}";
                if (_tokens > 0) text += $" · ~{Core.Tokens.TokenMeter.Format(_tokens)} tok";
                _header.Inlines.Clear();
                _header.Inlines.Add(new Run("●  ") { FontSize = 11, Foreground = _running.Count > 0 ? _accents.Running : _failed > 0 ? _accents.Failure : _accents.Success });
                _header.Inlines.Add(new Run(text));
                if (_failed > 0) _header.Inlines.Add(new Run($" · {_failed} failed") { Foreground = _accents.Failure });
                if (_running.Count > 0) _header.Inlines.Add(new Run(" · running " + _running.Values.Last()) { Foreground = _accents.Running });
                _header.ToolTip = text + (_failed > 0 ? $" · {_failed} failed" : "");
                _header.FontWeight = _tokens >= 5000 ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        // ---------------- Read / search groups ----------------

        private LookGroup _lookGroup;
        private readonly Dictionary<string, LookGroup> _lookGroupOf = new Dictionary<string, LookGroup>();

        /// <summary>Read / Grep / Glob, or a shell command that only reads (cat, head, sed -n, git log…).</summary>
        private static bool IsLookCall(string toolName, Newtonsoft.Json.Linq.JObject input) =>
            Core.Cli.ToolCallView.IsLookTool(toolName) ||
            (toolName != null && ShellTools.Contains(toolName) && toolName != "BashOutput" && toolName != "Monitor"
             && input?["command"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
             && input["run_in_background"]?.Type != Newtonsoft.Json.Linq.JTokenType.Boolean
             && Core.Cli.ToolCallView.IsReadOnlyCommand((string)input["command"]));

        private static bool IsSubagentCall(string description) =>
            description != null && description.StartsWith(Core.Cli.ClaudeStreamParser.SubagentToolPrefix, StringComparison.Ordinal);

        /// <summary>The group the next Read / Grep / Glob joins: the last one, while nothing else was added after it.</summary>
        private LookGroup CurrentLookGroup()
        {
            if (_lookGroup != null && _doc.Blocks.LastBlock == _lookGroup.Block) return _lookGroup;
            _lookGroup = new LookGroup(ChatTheme.Accents(Box));
            _doc.Blocks.Add(_lookGroup.Block);
            return _lookGroup;
        }

        /// <summary>
        /// Back-to-back reads and searches, like the terminal: "● Read 3 files, searched for 2 patterns", opening on
        /// click to the usual rows. A single call shows its own row ("Read(src/a.cs) ⎿ Read 120 lines" says more);
        /// the folded line takes over from the second one.
        /// </summary>
        private sealed class LookGroup
        {
            private readonly TextBlock _header = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            private readonly Run _dot = new Run("● ");
            private readonly Run _text = new Run();
            private readonly Run _failedText = new Run();
            private readonly ToggleButton _toggle;
            private readonly ChatTheme.AccentSet _accents;
            private readonly HashSet<string> _running = new HashSet<string>();
            private int _reads, _searches, _commands, _failed;

            public LookGroup(ChatTheme.AccentSet accents)
            {
                _accents = accents;
                ChatTheme.Muted(_header);
                _failedText.Foreground = accents.Failure;
                _header.Inlines.Add(_dot);
                _header.Inlines.Add(_text);
                _header.Inlines.Add(_failedText);
                Rows = new StackPanel();
                var panel = Collapsible(_header, Rows, new Thickness(0, 1, 0, 1));
                _toggle = (ToggleButton)panel.Children[0];
                _toggle.ToolTip = "Show each read, search and command";
                _toggle.Checked += (s, e) => Update();
                _toggle.Unchecked += (s, e) => Update();
                Block = new BlockUIContainer(panel) { Margin = new Thickness(0) };
            }

            public BlockUIContainer Block { get; }
            public StackPanel Rows { get; }

            public void Add(string id, string toolName, UIElement row)
            {
                Rows.Children.Add(row);
                if (toolName == "Read") _reads++;
                else if (toolName == "Grep" || toolName == "Glob") _searches++;
                else _commands++;
                if (!string.IsNullOrEmpty(id)) _running.Add(id);
                Update();
            }

            public void Complete(string id, bool isError)
            {
                _running.Remove(id);
                if (isError) _failed++;
                Update();
            }

            private void Update()
            {
                if (Rows.Children.Count < 2)
                {
                    _toggle.Visibility = Visibility.Collapsed; // one call: just its row
                    Rows.Visibility = Visibility.Visible;
                    return;
                }
                _toggle.Visibility = Visibility.Visible;
                Rows.Visibility = _toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                Rows.Margin = new Thickness(12, 0, 0, 0);
                bool running = _running.Count > 0;
                _dot.Foreground = running ? _accents.Running : _failed > 0 ? _accents.Failure : _accents.Success;
                _text.Text = Core.Cli.ToolCallView.LookSummary(_reads, _searches, running, _commands);
                _failedText.Text = _failed > 0 ? $" · {_failed} failed" : "";
                System.Windows.Automation.AutomationProperties.SetName(_toggle, _text.Text + _failedText.Text + (_toggle.IsChecked == true ? ", expanded" : ", collapsed"));
            }
        }

        /// <summary>
        /// Adds the result size (≈ chars / TokenEstimate.CharsPerToken tokens) to a tool row and marks failed calls; with the result text,
        /// the ⎿ summary under the row and the whole output inside it.
        /// </summary>
        /// <param name="confirmedDiff">
        /// For a file change, the diff rebuilt from the CLI's own copy of the file before the change; it replaces the
        /// lines drawn when the call arrived (those came from our copy, which a fast tool can beat).
        /// </param>
        /// <param name="allowedBy">Why Claude Code's settings let the call run without asking (see <see cref="Core.Cli.ClaudeEvent.AllowedBy"/>).</param>
        /// <param name="deniedBy">Why Claude Code's settings blocked it (see <see cref="Core.Cli.ClaudeEvent.DeniedBy"/>).</param>
        public void CompleteTool(string toolUseId, int resultChars, bool isError, string resultText = null, Core.Context.InlineDiff confirmedDiff = null, string allowedBy = null,
            Newtonsoft.Json.Linq.JObject resultData = null, string deniedBy = null)
        {
            if (!isError && confirmedDiff != null && confirmedDiff.Lines.Count > 0 && !string.IsNullOrEmpty(toolUseId)
                && _tools.TryGetValue(toolUseId, out var changed) && changed.ToolName != null && changed.Expander != null)
                ReplaceDiff(changed, confirmedDiff);
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!string.IsNullOrEmpty(toolUseId) && _toolGroupOf.TryGetValue(toolUseId, out var group))
            {
                _toolGroupOf.Remove(toolUseId);
                group.Complete(toolUseId, resultChars, isError);
            }
            if (!string.IsNullOrEmpty(toolUseId) && _lookGroupOf.TryGetValue(toolUseId, out var look))
            {
                _lookGroupOf.Remove(toolUseId);
                look.Complete(toolUseId, isError);
            }
            if (string.IsNullOrEmpty(toolUseId) || !_tools.TryGetValue(toolUseId, out var row)) return;
            _tools.Remove(toolUseId);
            var a = ChatTheme.Accents(Box);
            var header = row.Header;
            row.Dot.Foreground = isError ? a.Failure : a.Success;
            if (resultChars > 0) header.Inlines.Add(new Run($"  · ~{Core.Tokens.TokenMeter.Format(Core.Tokens.TokenEstimate.FromChars(resultChars))} tok") { FontSize = 11 });
            if (isError && row.ToolName == null) header.Inlines.Add(new Run(" · failed") { Foreground = a.Failure });
            if (Core.Cli.ToolCallView.AllowedNote(row.ToolName, allowedBy) is var (allowedText, allowedTip))
            {
                // "· allowed by a rule": why nothing was asked.
                var note = new Run("  · " + allowedText) { FontSize = 11, ToolTip = allowedTip };
                note.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
                header.Inlines.Add(note);
            }
            if (Core.Cli.ToolCallView.DeniedNote(deniedBy) is var (deniedText, deniedTip))
            {
                // "· denied by a rule": your settings blocked it, Claude was not refused by you just now.
                header.Inlines.Add(new Run("  · " + deniedText) { FontSize = 11, ToolTip = deniedTip, Foreground = a.Failure });
            }
            // Large results are what fill the context window; make them stand out.
            if (Core.Tokens.TokenEstimate.FromChars(resultChars) >= 5000) header.FontWeight = FontWeights.SemiBold;
            if (row.ToolName != null) ShowResult(row, resultText, isError, a, resultData);
        }

        private void ReplaceDiff(ToolRow row, Core.Context.InlineDiff diff)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var accents = ChatTheme.Accents(Box);
            var block = new StackPanel();
            block.Children.Add(FileLinks(row.OpenPath ?? diff.FilePath, row.OnDiff, accents, new Thickness(36, 2, 0, 1)));
            block.Children.Add(DiffView(diff, accents));
            int at = row.DiffElement != null ? row.Expander.Children.IndexOf(row.DiffElement) : -1;
            if (at >= 0) { row.Expander.Children.RemoveAt(at); row.Expander.Children.Insert(at, block); }
            else row.Expander.Children.Add(block);
            row.DiffElement = block;
            row.Diff = diff; // the ⎿ line counts come from it
        }

        public void EndAnswer()
        {
            _answer.Clear();
            _answerBlocks.Clear();
        }

        public void Clear()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EndAnswer();
            _plain = null;
            _doc.Blocks.Clear();
            _tools.Clear();
            _agentRows.Clear();
            _toolGroupOf.Clear();
            _toolGroup = null;
            _lookGroupOf.Clear();
            _lookGroup = null;
            LastUserMessage = null;
            _lastAnswerStart = null;
            _lastDay = null;
        }

        /// <summary>
        /// True while the view is at (or within 40px of) the bottom. Kept by <see cref="OnScrollChanged"/>, so it
        /// stays true while content grows or the chat gets shorter, and turns false only when the user scrolls up.
        /// </summary>
        private bool _followEnd = true;

        // Only follow the output when the user hasn't scrolled up to read something.
        private void ScrollToEndIfNear()
        {
            var sv = _scroll ?? (_scroll = FindScrollViewer(Box)); // template is applied once; cache the lookup
            if (sv == null || _followEnd || sv.VerticalOffset >= sv.ScrollableHeight - 40) Box.ScrollToEnd();
        }

        /// <summary>
        /// Keeps the newest output in view when the content grows after it was added (a tool row filling in its
        /// result, text laid out later) or when the chat gets shorter (the permission card or the tasks panel
        /// opening below it). A scroll with no size change is the user's own, so it decides whether to follow.
        /// </summary>
        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            var sv = (ScrollViewer)sender;
            if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
                _followEnd = sv.VerticalOffset >= sv.ScrollableHeight - 40;
            else if (_followEnd)
                sv.ScrollToEnd();
        }

        private static ScrollViewer FindScrollViewer(DependencyObject d)
        {
            if (d is ScrollViewer sv) return sv;
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var found = FindScrollViewer(VisualTreeHelper.GetChild(d, i));
                if (found != null) return found;
            }
            return null;
        }

        // ---------------- Rendering ----------------

        private Block Render(MdBlock b)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            switch (b.Kind)
            {
                case MdBlockKind.Heading:
                {
                    var p = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = b.Level == 1 ? 17 : b.Level == 2 ? 15 : 13.5, Margin = new Thickness(0, 8, 0, 4) };
                    if (b.Level <= 2) p.Foreground = ChatTheme.Accents(Box).Brand;
                    AddInlines(p.Inlines, b.Inlines);
                    return p;
                }
                case MdBlockKind.ListItem:
                {
                    var p = new Paragraph { Margin = new Thickness(12 + 16 * b.Indent, 1, 0, 1), TextIndent = -12 };
                    p.Inlines.Add(new Run(b.Number.HasValue ? b.Number + ". " : "• ") { Foreground = ChatTheme.Accents(Box).Brand });
                    AddInlines(p.Inlines, b.Inlines);
                    return p;
                }
                case MdBlockKind.Quote:
                {
                    var p = new Paragraph { Margin = new Thickness(4, 2, 0, 2), Padding = new Thickness(8, 0, 0, 0), BorderBrush = QuoteBar, BorderThickness = new Thickness(3, 0, 0, 0), FontStyle = FontStyles.Italic };
                    AddInlines(p.Inlines, b.Inlines);
                    return p;
                }
                case MdBlockKind.Rule:
                    return new BlockUIContainer(new Separator { Margin = new Thickness(0, 6, 0, 6) });
                case MdBlockKind.Code:
                    return RenderCode(b);
                default:
                {
                    var p = new Paragraph { Margin = new Thickness(0, 3, 0, 3) };
                    AddInlines(p.Inlines, b.Inlines);
                    return p;
                }
            }
        }

        private void AddInlines(InlineCollection target, IEnumerable<MdInline> inlines)
        {
            var a = ChatTheme.Accents(Box);
            foreach (var i in inlines)
            {
                switch (i.Kind)
                {
                    case MdInlineKind.Code:
                        target.Add(new Run(i.Text) { FontFamily = Mono, Foreground = a.Code, Background = a.CodeBack });
                        break;
                    case MdInlineKind.Bold:
                        target.Add(new Bold(new Run(i.Text)));
                        break;
                    case MdInlineKind.Italic:
                        target.Add(new Italic(new Run(i.Text)));
                        break;
                    case MdInlineKind.Link:
                        target.Add(MakeLink(i, a.Link));
                        break;
                    default:
                        AddTextWithBreaks(target, i.Text);
                        break;
                }
            }
        }

        private static void AddTextWithBreaks(InlineCollection target, string text)
        {
            var parts = (text ?? "").Split('\n');
            for (int k = 0; k < parts.Length; k++)
            {
                if (k > 0) target.Add(new LineBreak());
                if (parts[k].Length > 0) target.Add(new Run(parts[k]));
            }
        }

        private static Inline MakeLink(MdInline i, Brush color)
        {
            if (!Uri.TryCreate(i.Url, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return new Run(i.Text); // only open web links; anything else is shown as text
            var link = new Hyperlink(new Run(i.Text)) { NavigateUri = uri, ToolTip = uri.ToString(), Foreground = color };
            link.RequestNavigate += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                e.Handled = true;
            };
            return link;
        }

        private Block RenderCode(MdBlock b)
        {
            string code = b.Code ?? "";
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            if (b.Closed)
            {
                var insert = SmallButton("Insert", "Insert at the cursor in the active editor");
                insert.Click += (s, e) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    insert.Content = VsContext.InsertAtCursor(code) ? "Inserted" : "No editor open";
                };
                var copy = SmallButton("Copy", "Copy to clipboard");
                copy.Click += (s, e) => { try { Clipboard.SetText(code); copy.Content = "Copied"; } catch (Exception) { copy.Content = "Clipboard busy"; } };
                DockPanel.SetDock(insert, Dock.Right);
                DockPanel.SetDock(copy, Dock.Right);
                header.Children.Add(insert);
                header.Children.Add(copy);
            }
            header.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(b.Language) ? "code" : b.Language, Opacity = 0.7, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });

            var body = CodeView(code, b.Language);
            ForwardMouseWheel(body);

            var panel = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            panel.Children.Add(header);
            panel.Children.Add(body);
            var border = new Border { Background = CodeBackground, CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 4, 6, 6), Child = panel };
            return new BlockUIContainer(border) { Margin = new Thickness(0, 4, 0, 4) };
        }

        // ---------------- Syntax coloring ----------------

        private static readonly Dictionary<SyntaxKind, Brush> DarkPalette = Palette("#569CD6", "#CE9178", "#6A9955", "#B5CEA8", "#4EC9B0", "#9CDCFE");
        private static readonly Dictionary<SyntaxKind, Brush> LightPalette = Palette("#0000FF", "#A31515", "#008000", "#098658", "#800000", "#E50000");

        private static Dictionary<SyntaxKind, Brush> Palette(string keyword, string str, string comment, string number, string tag, string attribute)
        {
            Brush B(string hex) => Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));
            return new Dictionary<SyntaxKind, Brush>
            {
                [SyntaxKind.Keyword] = B(keyword), [SyntaxKind.String] = B(str), [SyntaxKind.Comment] = B(comment),
                [SyntaxKind.Number] = B(number), [SyntaxKind.Tag] = B(tag), [SyntaxKind.Attribute] = B(attribute),
            };
        }

        /// <summary>Dark or light colors, from the brightness of the tool window background (VS Light, Blue, Dark…).</summary>
        private Dictionary<SyntaxKind, Brush> CurrentPalette()
        {
            var bg = (Box.TryFindResource(EnvironmentColors.ToolWindowBackgroundBrushKey) as SolidColorBrush)?.Color ?? Colors.White;
            return 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B < 128 ? DarkPalette : LightPalette;
        }

        /// <summary>
        /// A read-only, selectable code view with colored tokens and no wrapping. Its page is as wide as the longest
        /// line, so long lines scroll sideways like the plain text box did.
        /// </summary>
        private RichTextBox CodeView(string code, string language)
        {
            var palette = CurrentPalette();
            var para = new Paragraph { Margin = new Thickness(0) };
            int pos = 0;
            foreach (var s in SyntaxHighlighter.Tokenize(code, language))
            {
                if (s.Start > pos) AddCode(para.Inlines, code.Substring(pos, s.Start - pos), null);
                AddCode(para.Inlines, code.Substring(s.Start, s.Length), palette[s.Kind]);
                pos = s.Start + s.Length;
            }
            if (pos < code.Length) AddCode(para.Inlines, code.Substring(pos), null);

            double dip = VisualTreeHelper.GetDpi(Box).PixelsPerDip;
            var typeface = new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            double widest = code.Split('\n').Select(l => new FormattedText(l.TrimEnd('\r'), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, Brushes.Black, dip).WidthIncludingTrailingWhitespace).DefaultIfEmpty(0).Max();

            var doc = new FlowDocument(para) { FontFamily = Mono, FontSize = 12, PagePadding = new Thickness(0, 0, 4, 0), PageWidth = Math.Max(40, widest + 12) };
            var view = new RichTextBox
            {
                Document = doc,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            view.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return view;
        }

        private static void AddCode(InlineCollection target, string text, Brush color)
        {
            var parts = text.Replace("\r\n", "\n").Split('\n');
            for (int k = 0; k < parts.Length; k++)
            {
                if (k > 0) target.Add(new LineBreak());
                if (parts[k].Length == 0) continue;
                var run = new Run(parts[k]);
                if (color != null) run.Foreground = color;
                target.Add(run);
            }
        }

        private static Button SmallButton(string text, string tip)
        {
            var b = ChatTheme.TextButton(text, tip);
            b.FontSize = 11;
            b.Padding = new Thickness(7, 1, 7, 1);
            b.Margin = new Thickness(4, 0, 0, 0);
            return b;
        }

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    }
}
