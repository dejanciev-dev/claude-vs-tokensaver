using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeVS.Core.Markdown
{
    public enum MdBlockKind { Paragraph, Heading, Code, ListItem, Quote, Rule, Table }

    public enum MdAlign { None, Left, Center, Right }

    public enum MdInlineKind { Text, Code, Bold, Italic, Link, Strike }

    public sealed class MdInline
    {
        public MdInlineKind Kind { get; set; }
        public string Text { get; set; }
        /// <summary>Link target (Kind == Link).</summary>
        public string Url { get; set; }
        public override string ToString() => $"{Kind}:{Text}";

        public bool SameAs(MdInline other) =>
            other != null && Kind == other.Kind && Text == other.Text && Url == other.Url;
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
        /// <summary>Table rows (Kind == Table), the header first; each row has a cell per column.</summary>
        public List<List<List<MdInline>>> Rows { get; } = new List<List<List<MdInline>>>();
        /// <summary>Each column's alignment, from the separator row (Kind == Table).</summary>
        public List<MdAlign> Align { get; } = new List<MdAlign>();
        public override string ToString() =>
            Kind == MdBlockKind.Code ? $"Code({Language}):{Code}"
            : Kind == MdBlockKind.Table ? "Table:" + string.Join("/", Rows.Select(r => string.Join("|", r.Select(c => string.Join("", c)))))
            : $"{Kind}:{string.Join("", Inlines)}";

        /// <summary>Same kind and content: what was drawn for <paramref name="other"/> can stay as it is.</summary>
        public bool SameAs(MdBlock other)
        {
            if (other == null || Kind != other.Kind || Level != other.Level || Indent != other.Indent || Number != other.Number
                || Language != other.Language || Code != other.Code || Closed != other.Closed)
                return false;
            if (Rows.Count != other.Rows.Count || !Align.SequenceEqual(other.Align)) return false;
            for (int r = 0; r < Rows.Count; r++)
            {
                if (Rows[r].Count != other.Rows[r].Count) return false;
                for (int c = 0; c < Rows[r].Count; c++)
                    if (!SameInlines(Rows[r][c], other.Rows[r][c])) return false;
            }
            return SameInlines(Inlines, other.Inlines);
        }

        internal static bool SameInlines(List<MdInline> a, List<MdInline> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!a[i].SameAs(b[i])) return false;
            return true;
        }
    }

    /// <summary>
    /// Small Markdown subset parser for chat answers: headings, fenced code, bullet/numbered lists,
    /// block quotes, rules, tables, paragraphs; inline code, bold, italic and links. Tolerates partial input
    /// (streaming): an unclosed fence becomes an open code block.
    /// </summary>
    public static class MarkdownParser
    {
        private static readonly Regex Fence = new Regex(@"^ {0,3}(```+|~~~+)\s*([\w#+.\-]*)\s*$", RegexOptions.Compiled);
        private static readonly Regex HeadingRx = new Regex(@"^ {0,3}(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
        private static readonly Regex Bullet = new Regex(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex Ordered = new Regex(@"^(\s*)(\d{1,9})[.)]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex RuleRx = new Regex(@"^ {0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
        private static readonly Regex QuoteRx = new Regex(@"^ {0,3}>\s?(.*)$", RegexOptions.Compiled);

        /// <summary>
        /// Parses <paramref name="markdown"/> into blocks. <paramref name="streaming"/>: the text is an answer still
        /// arriving (Phase 95), so its last line may be cut off: an unclosed <c>**bold</c> or <c>`code</c> there is
        /// bold or code to its end, and a table whose header is the last line (or whose separator row is still being
        /// written) is a table already. Parse the finished answer again without it.
        /// </summary>
        public static List<MdBlock> Parse(string markdown, bool streaming = false)
        {
            var blocks = new List<MdBlock>();
            var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
            var para = new StringBuilder();
            // The last line, while it may still be cut off
            bool Open(int line) => streaming && line == lines.Length - 1;

            void FlushPara(bool openEnd = false)
            {
                if (para.Length == 0) return;
                var b = new MdBlock { Kind = MdBlockKind.Paragraph };
                b.Inlines.AddRange(ParseInlines(para.ToString(), openEnd));
                blocks.Add(b);
                para.Clear();
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                Match m;

                if (TableStarts(lines, i, streaming, out var header, out var align))
                {
                    FlushPara();
                    var t = new MdBlock { Kind = MdBlockKind.Table };
                    t.Align.AddRange(align);
                    t.Rows.Add(header.Select(c => ParseInlines(c)).ToList());
                    i++; // the separator row (or what has arrived of it)
                    while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && lines[i + 1].Contains("|") && !Fence.IsMatch(lines[i + 1]))
                    {
                        i++;
                        var cells = SplitRow(lines[i]);
                        var row = new List<List<MdInline>>();
                        for (int c = 0; c < header.Count; c++)
                            row.Add(c < cells.Count ? ParseInlines(cells[c], Open(i) && c == cells.Count - 1) : new List<MdInline>());
                        t.Rows.Add(row);
                    }
                    blocks.Add(t);
                    continue;
                }

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
                    b.Inlines.AddRange(ParseInlines(m.Groups[2].Value, Open(i)));
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
                    b.Inlines.AddRange(ParseInlines(m.Groups[ordered ? 3 : 2].Value, Open(i)));
                    blocks.Add(b);
                    continue;
                }

                if ((m = QuoteRx.Match(line)).Success)
                {
                    FlushPara();
                    var b = new MdBlock { Kind = MdBlockKind.Quote };
                    b.Inlines.AddRange(ParseInlines(m.Groups[1].Value, Open(i)));
                    blocks.Add(b);
                    continue;
                }

                // Continuation of a list item (indented, no marker) joins the previous item.
                if (para.Length == 0 && blocks.Count > 0 && blocks[blocks.Count - 1].Kind == MdBlockKind.ListItem && line.StartsWith("  "))
                {
                    var prev = blocks[blocks.Count - 1];
                    prev.Inlines.Add(new MdInline { Kind = MdInlineKind.Text, Text = " " });
                    prev.Inlines.AddRange(ParseInlines(line.Trim(), Open(i)));
                    continue;
                }

                // Paragraph: keep line breaks (chat answers rely on them).
                if (para.Length > 0) para.Append('\n');
                para.Append(line.TrimEnd());
            }
            FlushPara(streaming && lines[lines.Length - 1].Trim().Length > 0);
            return blocks;
        }

        private static readonly Regex SeparatorCell = new Regex(@"^:?-+:?$", RegexOptions.Compiled);

        /// <summary>
        /// A table starts at <paramref name="i"/>: a row, then a separator row with a cell per header cell
        /// (<c>|---|:--:|</c>). While streaming, also a header starting with <c>|</c> that is the last line, or whose
        /// separator row, the last line, is still being written.
        /// </summary>
        private static bool TableStarts(string[] lines, int i, bool streaming, out List<string> header, out List<MdAlign> align)
        {
            header = null;
            align = null;
            string line = lines[i];
            if (!line.Contains("|") || Fence.IsMatch(line)) return false;
            if (i + 1 < lines.Length)
            {
                string next = lines[i + 1].Trim();
                var cells = SplitRow(lines[i]);
                var seps = SplitRow(next);
                if (next.Contains("|") || next.Contains("-"))
                {
                    if (seps.Count == cells.Count && seps.All(s => SeparatorCell.IsMatch(s)))
                    {
                        header = cells;
                        align = seps.Select(AlignOf).ToList();
                        return true;
                    }
                }
                // The separator row arriving: "|--", "| --- | :-"
                bool arriving = streaming && i + 1 == lines.Length - 1 && line.TrimStart().StartsWith("|") && next.StartsWith("|")
                    && next.All(ch => ch == '|' || ch == '-' || ch == ':' || ch == ' ');
                if (!arriving) return false;
            }
            else if (!(streaming && line.TrimStart().StartsWith("|") && SplitRow(line).Count > 0)) return false;
            header = SplitRow(line);
            align = header.Select(_ => MdAlign.None).ToList();
            return true;
        }

        private static MdAlign AlignOf(string sep) =>
            sep.StartsWith(":") && sep.EndsWith(":") ? MdAlign.Center : sep.EndsWith(":") ? MdAlign.Right : sep.StartsWith(":") ? MdAlign.Left : MdAlign.None;

        /// <summary>A table row's cells, trimmed, without the outer pipes; <c>\|</c> is a pipe inside a cell.</summary>
        internal static List<string> SplitRow(string line)
        {
            string s = (line ?? "").Trim();
            if (s.StartsWith("|")) s = s.Substring(1);
            if (s.EndsWith("|") && !s.EndsWith("\\|")) s = s.Substring(0, s.Length - 1);
            var cells = new List<string>();
            var cell = new StringBuilder();
            for (int k = 0; k < s.Length; k++)
            {
                if (s[k] == '\\' && k + 1 < s.Length && s[k + 1] == '|') { cell.Append('|'); k++; continue; }
                if (s[k] == '|') { cells.Add(cell.ToString().Trim()); cell.Clear(); continue; }
                cell.Append(s[k]);
            }
            cells.Add(cell.ToString().Trim());
            return cells;
        }

        /// <summary>
        /// The first block of <paramref name="now"/> that isn't the same as in <paramref name="before"/>: a streamed
        /// answer is parsed again for every piece, and the blocks before this one were drawn already and can stay.
        /// Usually the last block, the one still being written. When one list is a start of the other, the shorter
        /// one's length: the blocks from there on are drawn (or removed).
        /// </summary>
        public static int FirstChanged(IReadOnlyList<MdBlock> before, IReadOnlyList<MdBlock> now)
        {
            int n = Math.Min(before?.Count ?? 0, now?.Count ?? 0);
            for (int i = 0; i < n; i++)
                if (!now[i].SameAs(before[i])) return i;
            return n;
        }

        private static int IndentLevel(string ws)
        {
            int cols = 0;
            foreach (char c in ws) cols += c == '\t' ? 4 : 1;
            return cols / 2;
        }

        /// <summary>
        /// Inline code, **bold** / __bold__, *italic* / _italic_, ~~struck~~, [text](url). Unmatched markers stay
        /// literal, except with <paramref name="openEnd"/> (text still arriving): an unclosed <c>`code</c> is code to the
        /// end, and an unclosed <c>**bold</c> or <c>~~struck</c> that starts a word is bold or struck to the end; just
        /// the marker alone shows nothing yet.
        /// </summary>
        public static List<MdInline> ParseInlines(string text, bool openEnd = false)
        {
            var result = new List<MdInline>();
            var plain = new StringBuilder();
            string s = text ?? "";
            // Only a marker on the last line may still be waiting for its end
            int openFrom = openEnd ? s.LastIndexOf('\n') + 1 : int.MaxValue;

            void Emit(MdInlineKind kind, string value, string url = null)
            {
                if (plain.Length > 0) { result.Add(new MdInline { Kind = MdInlineKind.Text, Text = plain.ToString() }); plain.Clear(); }
                result.Add(new MdInline { Kind = kind, Text = value, Url = url });
            }

            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];

                if (c == '\\' && i + 1 < s.Length && "\\`*_[]()#+-.!~".IndexOf(s[i + 1]) >= 0) { plain.Append(s[i + 1]); i += 2; continue; }

                if (c == '`')
                {
                    int ticks = 1;
                    while (i + ticks < s.Length && s[i + ticks] == '`') ticks++;
                    string fence = new string('`', ticks);
                    int end = s.IndexOf(fence, i + ticks, StringComparison.Ordinal);
                    if (end > 0) { Emit(MdInlineKind.Code, s.Substring(i + ticks, end - i - ticks).Trim()); i = end + ticks; continue; }
                    if (i >= openFrom)
                    {
                        string rest = s.Substring(i + ticks).Trim();
                        if (rest.Length > 0) Emit(MdInlineKind.Code, rest);
                        break;
                    }
                }

                // **bold**, __bold__, and ~~struck~~ (Phase 96) the same way
                if ((c == '*' || c == '_' || c == '~') && i + 1 < s.Length && s[i + 1] == c)
                {
                    var kind = c == '~' ? MdInlineKind.Strike : MdInlineKind.Bold;
                    string marker = new string(c, 2);
                    int end = s.IndexOf(marker, i + 2, StringComparison.Ordinal);
                    if (end > i + 2) { Emit(kind, s.Substring(i + 2, end - i - 2)); i = end + 2; continue; }
                    // Still arriving: "**bol" (not "2 ** 3", whose marker doesn't start a word)
                    if (i >= openFrom && end < 0 && (i + 2 == s.Length || !char.IsWhiteSpace(s[i + 2])) && (i == 0 || !char.IsLetterOrDigit(s[i - 1])))
                    {
                        if (i + 2 < s.Length) Emit(kind, s.Substring(i + 2));
                        break;
                    }
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
