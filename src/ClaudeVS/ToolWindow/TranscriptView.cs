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
        private static readonly Brush InlineCodeBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)));
        private static readonly Brush QuoteBar = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80)));

        private readonly FlowDocument _doc;
        private readonly StringBuilder _answer = new StringBuilder();
        private readonly List<Block> _answerBlocks = new List<Block>();
        private Paragraph _plain;
        private readonly Dictionary<string, TextBlock> _tools = new Dictionary<string, TextBlock>();
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
        public void AppendUserMessage(string text, string meta, Action onRewind = null, IReadOnlyList<ImageSource> images = null, DateTime? timeUtc = null)
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
                var m = new TextBlock { Text = meta, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
                ChatTheme.Muted(m);
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
        public void AppendTurnFooter(string text, Func<bool> toggleBookmark, string answerToCopy = null, DateTime? timeUtc = null)
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
            var label = new TextBlock { Text = text, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            ChatTheme.Muted(label);
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
                link = new Hyperlink(new Run("Show summary")) { ToolTip = "What Claude kept from the conversation above" };
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
            ScrollToEndIfNear();
        }

        /// <summary>
        /// A collapsible row for a tool call: the header shows the call, the body its input.
        /// <see cref="CompleteTool"/> adds the result size when the result arrives.
        /// </summary>
        public void AppendTool(string toolUseId, string description, string inputJson, Action onDiff = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EndAnswer();
            _plain = null;

            var header = new TextBlock { Text = "⚙  " + description, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ChatTheme.Muted(header);
            object headerContent = header;
            if (onDiff != null)
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
                MaxHeight = 240,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(16, 2, 0, 2),
            };
            body.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var expander = new Expander { Header = headerContent, Content = body, IsExpanded = false, Margin = new Thickness(0, 1, 0, 1) };
            expander.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            _doc.Blocks.Add(new BlockUIContainer(expander) { Margin = new Thickness(0) });
            if (!string.IsNullOrEmpty(toolUseId)) _tools[toolUseId] = header;
            ScrollToEndIfNear();
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
            panel.Children.Add(new TextBlock { Text = "❓ Claude has a question", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
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
        public void AppendPlanCard(string planMarkdown, Action onApprove, Action<string> onRevise)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EndAnswer();
            _plain = null;

            _doc.Blocks.Add(new Paragraph(new Run("📋 Plan for approval")) { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
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
            panel.Children.Add(new TextBlock { Text = "Feedback (optional):", Opacity = 0.75, FontSize = 11 });
            panel.Children.Add(feedback);
            panel.Children.Add(buttons);
            approve.Click += (s, e) => { panel.IsEnabled = false; approve.Content = "Approved"; onApprove(); };
            revise.Click += (s, e) => { panel.IsEnabled = false; revise.Content = "Changes requested"; onRevise(feedback.Text); };
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

        /// <summary>Adds the result size (≈ chars / 4 tokens) to a tool row; marks failed calls.</summary>
        public void CompleteTool(string toolUseId, int resultChars, bool isError)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(toolUseId) || !_tools.TryGetValue(toolUseId, out TextBlock header)) return;
            _tools.Remove(toolUseId);
            string size = resultChars > 0 ? $" · ~{Core.Tokens.TokenMeter.Format(resultChars / 4)} tok" : "";
            header.Text += isError ? size + " · failed" : size;
            // Large results are what fill the context window; make them stand out.
            if (resultChars / 4 >= 5000) header.FontWeight = FontWeights.SemiBold;
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
            LastUserMessage = null;
            _lastDay = null;
        }

        // Only follow the output when the user hasn't scrolled up to read something.
        private void ScrollToEndIfNear()
        {
            var sv = _scroll ?? (_scroll = FindScrollViewer(Box)); // template is applied once; cache the lookup
            if (sv == null || sv.VerticalOffset >= sv.ScrollableHeight - 40) Box.ScrollToEnd();
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
                    AddInlines(p.Inlines, b.Inlines);
                    return p;
                }
                case MdBlockKind.ListItem:
                {
                    var p = new Paragraph { Margin = new Thickness(12 + 16 * b.Indent, 1, 0, 1), TextIndent = -12 };
                    p.Inlines.Add(new Run(b.Number.HasValue ? b.Number + ". " : "• "));
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

        private static void AddInlines(InlineCollection target, IEnumerable<MdInline> inlines)
        {
            foreach (var i in inlines)
            {
                switch (i.Kind)
                {
                    case MdInlineKind.Code:
                        target.Add(new Run(i.Text) { FontFamily = Mono, Background = InlineCodeBackground });
                        break;
                    case MdInlineKind.Bold:
                        target.Add(new Bold(new Run(i.Text)));
                        break;
                    case MdInlineKind.Italic:
                        target.Add(new Italic(new Run(i.Text)));
                        break;
                    case MdInlineKind.Link:
                        target.Add(MakeLink(i));
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

        private static Inline MakeLink(MdInline i)
        {
            if (!Uri.TryCreate(i.Url, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return new Run(i.Text); // only open web links; anything else is shown as text
            var link = new Hyperlink(new Run(i.Text)) { NavigateUri = uri, ToolTip = uri.ToString() };
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
