using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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

        public TranscriptView()
        {
            _doc = new FlowDocument { PagePadding = new Thickness(0), FontSize = 12.5 };
            _doc.SetResourceReference(FlowDocument.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
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

        /// <summary>Status text (prompt echo, tool calls, footers). Ends any answer being streamed.</summary>
        public void AppendPlain(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EndAnswer();
            if (string.IsNullOrEmpty(text)) return;
            if (_plain == null)
            {
                _plain = new Paragraph { FontFamily = Mono, FontSize = 12, Margin = new Thickness(0) };
                _doc.Blocks.Add(_plain);
            }
            _plain.Inlines.Add(new Run(text.Replace("\r\n", "\n")));
            ScrollToEndIfNear();
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
        }

        // Only follow the output when the user hasn't scrolled up to read something.
        private void ScrollToEndIfNear()
        {
            var sv = FindScrollViewer(Box);
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

            var body = new TextBox
            {
                Text = code,
                IsReadOnly = true,
                FontFamily = Mono,
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                TextWrapping = TextWrapping.NoWrap,
            };
            body.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var panel = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            panel.Children.Add(header);
            panel.Children.Add(body);
            var border = new Border { Background = CodeBackground, CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 4, 6, 6), Child = panel };
            return new BlockUIContainer(border) { Margin = new Thickness(0, 4, 0, 4) };
        }

        private static Button SmallButton(string text, string tip) => new Button
        {
            Content = text,
            ToolTip = tip,
            FontSize = 11,
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(4, 0, 0, 0),
            MinWidth = 0,
        };

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    }
}
