using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace ClaudeVS.Core.Markdown
{
    /// <summary>
    /// An answer as HTML (Phase 109), for pasting into Word, Outlook or Teams and for an exported page: what the chat
    /// draws, from the same parsed blocks, everything escaped. A fragment, no page around it.
    /// </summary>
    public static class MarkdownHtml
    {
        public static string Render(string markdown) => Blocks(MarkdownParser.Parse(markdown));

        /// <summary>
        /// Escaped for HTML text and attributes: <c>&amp; &lt; &gt; "</c> only. Other characters stay as they are (the
        /// page and the clipboard's HTML are UTF-8), so an emoji or a box stays one, not a numeric entity.
        /// </summary>
        public static string E(string s) =>
            (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        /// <summary>A link target that is safe in an href: web addresses, anchors and relative paths; nothing with another scheme.</summary>
        private static string Href(string url)
        {
            string u = (url ?? "").Trim();
            if (u.StartsWith("#", StringComparison.Ordinal)) return u;
            int colon = u.IndexOf(':');
            int slash = u.IndexOfAny(new[] { '/', '\\' });
            bool hasScheme = colon > 1 && (slash < 0 || colon < slash);
            if (hasScheme && !u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
            return u;
        }

        public static string Inlines(IEnumerable<MdInline> inlines)
        {
            var sb = new StringBuilder();
            foreach (var i in inlines ?? Enumerable.Empty<MdInline>())
            {
                string inner = i.Children != null ? Inlines(i.Children) : E(i.Text);
                switch (i.Kind)
                {
                    case MdInlineKind.Code: sb.Append("<code>").Append(E(i.Text)).Append("</code>"); break;
                    case MdInlineKind.Bold: sb.Append("<strong>").Append(inner).Append("</strong>"); break;
                    case MdInlineKind.Italic: sb.Append("<em>").Append(inner).Append("</em>"); break;
                    case MdInlineKind.Strike: sb.Append("<del>").Append(inner).Append("</del>"); break;
                    case MdInlineKind.Underline: sb.Append("<u>").Append(inner).Append("</u>"); break;
                    case MdInlineKind.Sup: sb.Append("<sup>").Append(inner).Append("</sup>"); break;
                    case MdInlineKind.Sub: sb.Append("<sub>").Append(inner).Append("</sub>"); break;
                    case MdInlineKind.Kbd: sb.Append("<kbd>").Append(E(i.Text)).Append("</kbd>"); break;
                    case MdInlineKind.FootnoteRef: sb.Append("<sup><a href=\"#fn-").Append(E(i.Url)).Append("\">").Append(E(i.Text)).Append("</a></sup>"); break;
                    case MdInlineKind.Link:
                    case MdInlineKind.Image:
                    {
                        string text = i.Kind == MdInlineKind.Image ? E(i.ImageLabel) : E(i.Text);
                        string href = Href(i.Url);
                        if (href == null) sb.Append(text);
                        else sb.Append("<a href=\"").Append(E(href)).Append("\">").Append(text).Append("</a>");
                        break;
                    }
                    default: sb.Append(E(i.Text).Replace("\r\n", "\n").Replace("\n", "<br>")); break;
                }
            }
            return sb.ToString();
        }

        public static string Blocks(IReadOnlyList<MdBlock> blocks)
        {
            var sb = new StringBuilder();
            for (int k = 0; k < blocks.Count; k++)
            {
                var b = blocks[k];
                switch (b.Kind)
                {
                    case MdBlockKind.Heading:
                        sb.Append("<h").Append(b.Level).Append(b.Anchor != null ? " id=\"" + E(b.Anchor) + "\"" : "").Append('>')
                          .Append(Inlines(b.Inlines)).Append("</h").Append(b.Level).Append(">\n");
                        break;
                    case MdBlockKind.ListItem:
                        k = List(blocks, k, sb) - 1;
                        sb.Append('\n');
                        break;
                    case MdBlockKind.Quote:
                    {
                        sb.Append("<blockquote>");
                        for (; k < blocks.Count && blocks[k].Kind == MdBlockKind.Quote; k++)
                        {
                            var q = blocks[k];
                            sb.Append("<p").Append(q.Level > 1 ? $" style=\"margin-left:{q.Level - 1}em\"" : "").Append('>');
                            if (q.AlertTitle) sb.Append("<strong>").Append(E(q.Alert)).Append("</strong>").Append(q.Inlines.Count > 0 ? " " : "");
                            sb.Append(Inlines(q.Inlines)).Append("</p>");
                        }
                        k--;
                        sb.Append("</blockquote>\n");
                        break;
                    }
                    case MdBlockKind.Rule: sb.Append("<hr>\n"); break;
                    case MdBlockKind.Code: sb.Append(Code(b)).Append('\n'); break;
                    case MdBlockKind.Table: sb.Append(Table(b)).Append('\n'); break;
                    case MdBlockKind.Details:
                        sb.Append("<details><summary>").Append(Inlines(b.Inlines)).Append("</summary>\n").Append(Blocks(b.Blocks)).Append("</details>\n");
                        break;
                    case MdBlockKind.Footnote:
                        sb.Append("<p id=\"fn-").Append(E(b.Anchor)).Append("\"><small>").Append(b.Number).Append(". ").Append(Inlines(b.Inlines)).Append("</small></p>\n");
                        break;
                    default: sb.Append("<p>").Append(Inlines(b.Inlines)).Append("</p>\n"); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>A run of list items from <paramref name="start"/> as nested lists; returns the index after it.</summary>
        private static int List(IReadOnlyList<MdBlock> blocks, int start, StringBuilder sb)
        {
            int depth = blocks[start].Indent;
            bool ordered = blocks[start].Number.HasValue;
            sb.Append(ordered ? (blocks[start].Number != 1 ? $"<ol start=\"{blocks[start].Number}\">" : "<ol>") : "<ul>");
            int k = start;
            while (k < blocks.Count && blocks[k].Kind == MdBlockKind.ListItem && blocks[k].Indent >= depth)
            {
                var item = blocks[k];
                if (item.Indent > depth) { k = List(blocks, k, sb); continue; }
                sb.Append("<li>");
                if (item.Task.HasValue) sb.Append(item.Task.Value ? "☑ " : "☐ ");
                sb.Append(Inlines(item.Inlines));
                k++;
                // A nested list or code block belongs inside this item
                while (k < blocks.Count && ((blocks[k].Kind == MdBlockKind.ListItem && blocks[k].Indent > depth) || (blocks[k].Kind == MdBlockKind.Code && blocks[k].Indent > depth)))
                {
                    if (blocks[k].Kind == MdBlockKind.Code) { sb.Append(Code(blocks[k])); k++; }
                    else k = List(blocks, k, sb);
                }
                sb.Append("</li>");
            }
            sb.Append(ordered ? "</ol>" : "</ul>");
            return k;
        }

        // The chat's light palette (Phase 109): inline styles survive a paste into Word or Outlook, a stylesheet doesn't
        private static readonly Dictionary<SyntaxKind, string> Colors = new Dictionary<SyntaxKind, string>
        {
            [SyntaxKind.Keyword] = "#0000FF", [SyntaxKind.String] = "#A31515", [SyntaxKind.Comment] = "#008000",
            [SyntaxKind.Number] = "#098658", [SyntaxKind.Tag] = "#800000", [SyntaxKind.Attribute] = "#E50000",
            [SyntaxKind.Inserted] = "#22863A", [SyntaxKind.Deleted] = "#B31D28", [SyntaxKind.Hunk] = "#6F42C1",
        };

        private const string PreStyle = "background:#f6f8fa;padding:8px 10px;border-radius:4px;font-family:Consolas,'Cascadia Mono',monospace;font-size:10pt;white-space:pre";

        /// <summary>A code block as a monospace block, in its syntax colors (Phase 109).</summary>
        public static string Code(MdBlock b)
        {
            string code = b.Code ?? "";
            string lang = string.IsNullOrEmpty(b.Language) ? "" : " class=\"language-" + E(b.Language) + "\"";
            var sb = new StringBuilder("<pre style=\"" + PreStyle + "\"><code" + lang + ">");
            int pos = 0;
            foreach (var s in SyntaxHighlighter.Tokenize(code, b.Language))
            {
                if (s.Start > pos) sb.Append(E(code.Substring(pos, s.Start - pos)));
                sb.Append("<span style=\"color:").Append(Colors[s.Kind]).Append("\">").Append(E(code.Substring(s.Start, s.Length))).Append("</span>");
                pos = s.Start + s.Length;
            }
            if (pos < code.Length) sb.Append(E(code.Substring(pos)));
            return sb.Append("</code></pre>").ToString();
        }

        private static string Table(MdBlock t)
        {
            var sb = new StringBuilder("<table>");
            for (int r = 0; r < t.Rows.Count; r++)
            {
                sb.Append("<tr>");
                for (int c = 0; c < t.Rows[r].Count; c++)
                {
                    string tag = r == 0 ? "th" : "td";
                    var align = c < t.Align.Count ? t.Align[c] : MdAlign.None;
                    string style = align == MdAlign.None ? "" : $" style=\"text-align:{align.ToString().ToLowerInvariant()}\"";
                    sb.Append('<').Append(tag).Append(style).Append('>').Append(Inlines(t.Rows[r][c])).Append("</").Append(tag).Append('>');
                }
                sb.Append("</tr>");
            }
            return sb.Append("</table>").ToString();
        }
    }
}
