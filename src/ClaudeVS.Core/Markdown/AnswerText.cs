using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace ClaudeVS.Core.Markdown
{
    /// <summary>
    /// Parts of an answer as text to take elsewhere (Phase 100): a table tab-separated or as HTML, for Excel, Word or
    /// Outlook.
    /// </summary>
    public static class AnswerText
    {
        /// <summary>Inlines as plain text: no marks, a link or image as its text, line breaks kept.</summary>
        public static string Inline(IEnumerable<MdInline> inlines)
        {
            var sb = new StringBuilder();
            foreach (var i in inlines ?? Enumerable.Empty<MdInline>())
            {
                if (i.Children != null) sb.Append(Inline(i.Children));
                else if (i.Kind == MdInlineKind.Image) sb.Append(string.IsNullOrWhiteSpace(i.Text) ? i.ImageLabel.Substring(3) : i.Text);
                else sb.Append(i.Text);
            }
            return sb.ToString();
        }

        /// <summary>A table's cell as one line: a line break or a tab is a blank.</summary>
        private static string Cell(List<MdInline> cell) =>
            Inline(cell).Replace("\r", "").Replace('\n', ' ').Replace('\t', ' ').Trim();

        /// <summary>A table tab-separated, a row per line (CRLF): pasted into Excel it fills cells.</summary>
        public static string TableTsv(MdBlock table)
        {
            if (table == null || table.Kind != MdBlockKind.Table) return "";
            return string.Join("\r\n", table.Rows.Select(r => string.Join("\t", r.Select(Cell))));
        }

        /// <summary>A table as an HTML fragment: the header row in <c>th</c>, each column aligned as written.</summary>
        public static string TableHtml(MdBlock table)
        {
            if (table == null || table.Kind != MdBlockKind.Table) return "";
            var sb = new StringBuilder("<table>");
            for (int r = 0; r < table.Rows.Count; r++)
            {
                sb.Append("<tr>");
                for (int c = 0; c < table.Rows[r].Count; c++)
                {
                    string tag = r == 0 ? "th" : "td";
                    var align = c < table.Align.Count ? table.Align[c] : MdAlign.None;
                    string style = align == MdAlign.None ? "" : $" style=\"text-align:{align.ToString().ToLowerInvariant()}\"";
                    sb.Append('<').Append(tag).Append(style).Append('>').Append(WebUtility.HtmlEncode(Cell(table.Rows[r][c]))).Append("</").Append(tag).Append('>');
                }
                sb.Append("</tr>");
            }
            return sb.Append("</table>").ToString();
        }

        /// <summary>
        /// An HTML fragment in the clipboard's HTML format (CF_HTML): a header with the byte offsets of the document
        /// and the fragment, which Word and Outlook need to paste it.
        /// </summary>
        public static string ClipboardHtml(string fragment)
        {
            const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            string pre = "<html><body><!--StartFragment-->";
            string post = "<!--EndFragment--></body></html>";
            int headerLength = string.Format(System.Globalization.CultureInfo.InvariantCulture, header, 0, 0, 0, 0).Length;
            var utf8 = Encoding.UTF8;
            int startHtml = headerLength;
            int startFragment = startHtml + utf8.GetByteCount(pre);
            int endFragment = startFragment + utf8.GetByteCount(fragment ?? "");
            int endHtml = endFragment + utf8.GetByteCount(post);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, header, startHtml, endHtml, startFragment, endFragment) + pre + fragment + post;
        }
    }
}
