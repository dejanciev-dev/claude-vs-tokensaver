using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeVS.Core.Markdown
{
    public enum MdBlockKind { Paragraph, Heading, Code, ListItem, Quote, Rule }

    public enum MdInlineKind { Text, Code, Bold, Italic, Link }

    public sealed class MdInline
    {
        public MdInlineKind Kind { get; set; }
        public string Text { get; set; }
        /// <summary>Link target (Kind == Link).</summary>
        public string Url { get; set; }
        public override string ToString() => $"{Kind}:{Text}";
    }

    public sealed class MdBlock
    {
        public MdBlockKind Kind { get; set; }
        /// <summary>Heading level 1-6.</summary>
        public int Level { get; set; }
        /// <summary>List nesting depth, 0-based.</summary>
        public int Indent { get; set; }
        /// <summary>Ordered list number, or null for a bullet.</summary>
        public int? Number { get; set; }
        /// <summary>Code fence language tag (Kind == Code).</summary>
        public string Language { get; set; }
        /// <summary>Raw code (Kind == Code).</summary>
        public string Code { get; set; }
        /// <summary>False while a streamed code fence has not been closed yet.</summary>
        public bool Closed { get; set; } = true;
        public List<MdInline> Inlines { get; } = new List<MdInline>();
        public override string ToString() => Kind == MdBlockKind.Code ? $"Code({Language}):{Code}" : $"{Kind}:{string.Join("", Inlines)}";
    }

    /// <summary>
    /// Small Markdown subset parser for chat answers: headings, fenced code, bullet/numbered lists,
    /// block quotes, rules, paragraphs; inline code, bold, italic and links. Tolerates partial input
    /// (streaming): an unclosed fence becomes an open code block. Tables are kept as plain paragraph lines.
    /// </summary>
    public static class MarkdownParser
    {
        private static readonly Regex Fence = new Regex(@"^ {0,3}(```+|~~~+)\s*([\w#+.\-]*)\s*$", RegexOptions.Compiled);
        private static readonly Regex HeadingRx = new Regex(@"^ {0,3}(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
        private static readonly Regex Bullet = new Regex(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex Ordered = new Regex(@"^(\s*)(\d{1,9})[.)]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex RuleRx = new Regex(@"^ {0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
        private static readonly Regex QuoteRx = new Regex(@"^ {0,3}>\s?(.*)$", RegexOptions.Compiled);

        public static List<MdBlock> Parse(string markdown)
        {
            var blocks = new List<MdBlock>();
            var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
            var para = new StringBuilder();

            void FlushPara()
            {
                if (para.Length == 0) return;
                var b = new MdBlock { Kind = MdBlockKind.Paragraph };
                b.Inlines.AddRange(ParseInlines(para.ToString()));
                blocks.Add(b);
                para.Clear();
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                Match m;

                if ((m = Fence.Match(line)).Success)
                {
                    FlushPara();
                    string marker = m.Groups[1].Value;
                    var code = new StringBuilder();
                    bool closed = false;
                    for (i++; i < lines.Length; i++)
                    {
                        // Closing fence: only the fence character, at least as many as the opening fence.
                        string t = lines[i].Trim();
                        if (t.Length >= marker.Length && t.Trim(marker[0]).Length == 0) { closed = true; break; }
                        if (code.Length > 0) code.Append('\n');
                        code.Append(lines[i]);
                    }
                    blocks.Add(new MdBlock { Kind = MdBlockKind.Code, Language = m.Groups[2].Value, Code = code.ToString(), Closed = closed });
                    continue;
                }

                if (line.Trim().Length == 0) { FlushPara(); continue; }

                if ((m = HeadingRx.Match(line)).Success)
                {
                    FlushPara();
                    var b = new MdBlock { Kind = MdBlockKind.Heading, Level = m.Groups[1].Value.Length };
                    b.Inlines.AddRange(ParseInlines(m.Groups[2].Value));
                    blocks.Add(b);
                    continue;
                }

                if (RuleRx.IsMatch(line)) { FlushPara(); blocks.Add(new MdBlock { Kind = MdBlockKind.Rule }); continue; }

                if ((m = Bullet.Match(line)).Success || (m = Ordered.Match(line)).Success)
                {
                    FlushPara();
                    bool ordered = m.Groups.Count == 4;
                    var b = new MdBlock
                    {
                        Kind = MdBlockKind.ListItem,
                        Indent = IndentLevel(m.Groups[1].Value),
                        Number = ordered ? int.Parse(m.Groups[2].Value) : (int?)null,
                    };
                    b.Inlines.AddRange(ParseInlines(m.Groups[ordered ? 3 : 2].Value));
                    blocks.Add(b);
                    continue;
                }

                if ((m = QuoteRx.Match(line)).Success)
                {
                    FlushPara();
                    var b = new MdBlock { Kind = MdBlockKind.Quote };
                    b.Inlines.AddRange(ParseInlines(m.Groups[1].Value));
                    blocks.Add(b);
                    continue;
                }

                // Continuation of a list item (indented, no marker) joins the previous item.
                if (para.Length == 0 && blocks.Count > 0 && blocks[blocks.Count - 1].Kind == MdBlockKind.ListItem && line.StartsWith("  "))
                {
                    var prev = blocks[blocks.Count - 1];
                    prev.Inlines.Add(new MdInline { Kind = MdInlineKind.Text, Text = " " });
                    prev.Inlines.AddRange(ParseInlines(line.Trim()));
                    continue;
                }

                // Paragraph: keep line breaks (chat answers and tables rely on them).
                if (para.Length > 0) para.Append('\n');
                para.Append(line.TrimEnd());
            }
            FlushPara();
            return blocks;
        }

        private static int IndentLevel(string ws)
        {
            int cols = 0;
            foreach (char c in ws) cols += c == '\t' ? 4 : 1;
            return cols / 2;
        }

        /// <summary>Inline code, **bold** / __bold__, *italic* / _italic_, [text](url). Unmatched markers stay literal.</summary>
        public static List<MdInline> ParseInlines(string text)
        {
            var result = new List<MdInline>();
            var plain = new StringBuilder();
            string s = text ?? "";

            void Emit(MdInlineKind kind, string value, string url = null)
            {
                if (plain.Length > 0) { result.Add(new MdInline { Kind = MdInlineKind.Text, Text = plain.ToString() }); plain.Clear(); }
                result.Add(new MdInline { Kind = kind, Text = value, Url = url });
            }

            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];

                if (c == '\\' && i + 1 < s.Length && "\\`*_[]()#+-.!".IndexOf(s[i + 1]) >= 0) { plain.Append(s[i + 1]); i += 2; continue; }

                if (c == '`')
                {
                    int ticks = 1;
                    while (i + ticks < s.Length && s[i + ticks] == '`') ticks++;
                    string fence = new string('`', ticks);
                    int end = s.IndexOf(fence, i + ticks, StringComparison.Ordinal);
                    if (end > 0) { Emit(MdInlineKind.Code, s.Substring(i + ticks, end - i - ticks).Trim()); i = end + ticks; continue; }
                }

                if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] == c)
                {
                    string marker = new string(c, 2);
                    int end = s.IndexOf(marker, i + 2, StringComparison.Ordinal);
                    if (end > i + 2) { Emit(MdInlineKind.Bold, s.Substring(i + 2, end - i - 2)); i = end + 2; continue; }
                }

                // Single * or _ for italic; _ only at word boundaries so snake_case stays intact.
                if ((c == '*' || c == '_') && i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]) && (c == '*' || i == 0 || !char.IsLetterOrDigit(s[i - 1])))
                {
                    int end = s.IndexOf(c, i + 1);
                    bool boundaryOk = end > i + 1 && !char.IsWhiteSpace(s[end - 1]) && (c == '*' || end + 1 >= s.Length || !char.IsLetterOrDigit(s[end + 1]));
                    if (boundaryOk) { Emit(MdInlineKind.Italic, s.Substring(i + 1, end - i - 1)); i = end + 1; continue; }
                }

                if (c == '[')
                {
                    int close = s.IndexOf("](", i + 1, StringComparison.Ordinal);
                    int paren = close > 0 ? s.IndexOf(')', close + 2) : -1;
                    if (close > i && paren > close) { Emit(MdInlineKind.Link, s.Substring(i + 1, close - i - 1), s.Substring(close + 2, paren - close - 2)); i = paren + 1; continue; }
                }

                plain.Append(c);
                i++;
            }
            if (plain.Length > 0) result.Add(new MdInline { Kind = MdInlineKind.Text, Text = plain.ToString() });
            return result;
        }
    }
}
