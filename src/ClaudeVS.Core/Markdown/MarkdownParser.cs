using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeVS.Core.Markdown
{
    public enum MdBlockKind { Paragraph, Heading, Code, ListItem, Quote, Rule, Table, Details }

    public enum MdAlign { None, Left, Center, Right }

    /// <summary>Kbd, Sup, Sub and Underline come from HTML tags in answers (Phase 107).</summary>
    public enum MdInlineKind { Text, Code, Bold, Italic, Link, Strike, Image, Kbd, Sup, Sub, Underline }

    public sealed class MdInline
    {
        public MdInlineKind Kind { get; set; }
        public string Text { get; set; }
        /// <summary>Link target (Kind == Link).</summary>
        public string Url { get; set; }
        /// <summary>
        /// What bold, italic or struck text holds (Phase 96): code, links, the other emphasis. <see cref="Text"/> is
        /// what was written between the markers. Null for the other kinds.
        /// </summary>
        public List<MdInline> Children { get; set; }
        public override string ToString() => $"{Kind}:{Text}";

        /// <summary>
        /// What an image (Phase 98) is drawn as, since the picture itself isn't: "🖼 " and its alt text, or the file
        /// name when it has none.
        /// </summary>
        public string ImageLabel
        {
            get
            {
                string name = Text;
                if (string.IsNullOrWhiteSpace(name))
                {
                    string path = (Url ?? "").Split('?', '#')[0].TrimEnd('/', '\\');
                    int slash = path.LastIndexOfAny(new[] { '/', '\\' });
                    name = slash >= 0 ? path.Substring(slash + 1) : path;
                }
                return "\U0001F5BC " + (string.IsNullOrWhiteSpace(name) ? "image" : name.Trim()); // 🖼
            }
        }

        public bool SameAs(MdInline other) =>
            other != null && Kind == other.Kind && Text == other.Text && Url == other.Url
            && (Children == null ? other.Children == null : other.Children != null && MdBlock.SameInlines(Children, other.Children));
    }

    public sealed class MdBlock
    {
        public MdBlockKind Kind { get; set; }
        /// <summary>Heading level 1-6; a quote's depth, 1 for <c>&gt;</c>, 2 for <c>&gt;&gt;</c> (Phase 98).</summary>
        public int Level { get; set; }
        /// <summary>
        /// A quote that is part of a GitHub alert (Phase 98): "Note", "Tip", "Important", "Warning" or "Caution".
        /// Null for an ordinary quote.
        /// </summary>
        public string Alert { get; set; }
        /// <summary>The alert's first line, <c>&gt; [!NOTE]</c>: drawn as its title.</summary>
        public bool AlertTitle { get; set; }
        /// <summary>A heading's anchor in its answer (Phase 102): "setup", "setup-1" for the second.</summary>
        public string Anchor { get; set; }
        /// <summary>
        /// List nesting depth, 0-based. For a code block (Phase 97): 0 at the answer's margin, else the depth of the
        /// list item it is under plus one, so it is drawn indented with that item's text.
        /// </summary>
        public int Indent { get; set; }
        /// <summary>Ordered list number, or null for a bullet.</summary>
        public int? Number { get; set; }
        /// <summary>A task list item (<c>- [ ]</c>, <c>- [x]</c>): whether it is done; null for an ordinary item.</summary>
        public bool? Task { get; set; }
        /// <summary>
        /// What a list item is drawn after: "1. ", "• ", or a task's box in place of the bullet ("☐ ", "☑ ";
        /// "2. ☑ " in a numbered list).
        /// </summary>
        public string Marker =>
            (Number.HasValue ? Number + ". " : Task.HasValue ? "" : "• ") + (Task == true ? "☑ " : Task == false ? "☐ " : "");
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
        /// <summary>A collapsed section's own blocks (Kind == Details, Phase 107); its summary is <see cref="Inlines"/>.</summary>
        public List<MdBlock> Blocks { get; } = new List<MdBlock>();
        public override string ToString() =>
            Kind == MdBlockKind.Code ? $"Code({Language}):{Code}"
            : Kind == MdBlockKind.Table ? "Table:" + string.Join("/", Rows.Select(r => string.Join("|", r.Select(c => string.Join("", c)))))
            : $"{Kind}:{string.Join("", Inlines)}";

        /// <summary>Same kind and content: what was drawn for <paramref name="other"/> can stay as it is.</summary>
        public bool SameAs(MdBlock other)
        {
            if (other == null || Kind != other.Kind || Level != other.Level || Indent != other.Indent || Number != other.Number
                || Task != other.Task || Alert != other.Alert || AlertTitle != other.AlertTitle || Anchor != other.Anchor || Language != other.Language || Code != other.Code || Closed != other.Closed)
                return false;
            if (Rows.Count != other.Rows.Count || !Align.SequenceEqual(other.Align)) return false;
            for (int r = 0; r < Rows.Count; r++)
            {
                if (Rows[r].Count != other.Rows[r].Count) return false;
                for (int c = 0; c < Rows[r].Count; c++)
                    if (!SameInlines(Rows[r][c], other.Rows[r][c])) return false;
            }
            if (Blocks.Count != other.Blocks.Count) return false;
            for (int k = 0; k < Blocks.Count; k++)
                if (!Blocks[k].SameAs(other.Blocks[k])) return false;
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
    /// task lists, block quotes, rules, tables, paragraphs; inline code, bold, italic and struck text (which hold
    /// inlines of their own), links and web addresses. Tolerates partial input
    /// (streaming): an unclosed fence becomes an open code block.
    /// </summary>
    public static class MarkdownParser
    {
        // Any indent: more than three spaces is a fence only under a list item (Phase 97)
        private static readonly Regex Fence = new Regex(@"^( *)(```+|~~~+)\s*([\w#+.\-]*)\s*$", RegexOptions.Compiled);
        private static readonly Regex HeadingRx = new Regex(@"^ {0,3}(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
        private static readonly Regex Bullet = new Regex(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex Ordered = new Regex(@"^(\s*)(\d{1,9})[.)]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex RuleRx = new Regex(@"^ {0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
        private static readonly Regex QuoteRx = new Regex(@"^ {0,3}>\s?(.*)$", RegexOptions.Compiled);
        // Three or more, so a "-" that starts a list item while it streams isn't one
        private static readonly Regex Underline = new Regex(@"^ {0,3}(={3,}|-{3,})\s*$", RegexOptions.Compiled);
        private static readonly Regex AlertRx = new Regex(@"^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TaskBox = new Regex(@"^\[([ xX])\](?:\s+|$)", RegexOptions.Compiled);

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

                // <details><summary>…</summary> … </details> (Phase 107): a collapsed section of Markdown
                if (DetailsOpen.IsMatch(line))
                {
                    FlushPara();
                    blocks.Add(ReadDetails(lines, ref i, streaming));
                    continue;
                }

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

                if ((m = Fence.Match(line)).Success && (m.Groups[1].Length <= 3 || (para.Length == 0 && ListItemAbove(blocks) != null)))
                {
                    FlushPara();
                    int fenceIndent = m.Groups[1].Length;
                    string marker = m.Groups[2].Value;
                    var code = new StringBuilder();
                    bool closed = false;
                    for (i++; i < lines.Length; i++)
                    {
                        // Closing fence: only the fence character, at least as many as the opening fence.
                        string t = lines[i].Trim();
                        if (t.Length >= marker.Length && t.Trim(marker[0]).Length == 0) { closed = true; break; }
                        if (code.Length > 0) code.Append('\n');
                        // The fence's indent comes off its lines, as much as each has (Phase 97)
                        string l = lines[i];
                        int strip = 0;
                        while (strip < fenceIndent && strip < l.Length && l[strip] == ' ') strip++;
                        code.Append(l, strip, l.Length - strip);
                    }
                    // Under a list item (indented, after one): drawn with it
                    var item = fenceIndent >= 2 ? ListItemAbove(blocks) : null;
                    blocks.Add(new MdBlock
                    {
                        Kind = MdBlockKind.Code,
                        Language = m.Groups[3].Value,
                        Code = code.ToString(),
                        Closed = closed,
                        Indent = item == null ? 0 : (item.Kind == MdBlockKind.Code ? item.Indent : item.Indent + 1),
                    });
                    continue;
                }

                if (line.Trim().Length == 0) { FlushPara(); continue; }

                // "Summary" over "=======" or "-------" (Phase 98): a heading, when it is the paragraph's only line
                if (para.Length > 0 && para.ToString().IndexOf('\n') < 0 && (m = Underline.Match(line)).Success)
                {
                    var h = new MdBlock { Kind = MdBlockKind.Heading, Level = m.Groups[1].Value[0] == '=' ? 1 : 2 };
                    h.Inlines.AddRange(ParseInlines(para.ToString()));
                    para.Clear();
                    blocks.Add(h);
                    continue;
                }

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
                    string content = m.Groups[ordered ? 3 : 2].Value;
                    // A task (Phase 96): "- [ ] add tests", "- [x] build"
                    var box = TaskBox.Match(content);
                    if (box.Success)
                    {
                        b.Task = box.Groups[1].Value != " ";
                        content = content.Substring(box.Length);
                    }
                    b.Inlines.AddRange(ParseInlines(content, Open(i)));
                    blocks.Add(b);
                    continue;
                }

                if ((m = QuoteRx.Match(line)).Success)
                {
                    FlushPara();
                    var b = new MdBlock { Kind = MdBlockKind.Quote, Level = 1 };
                    string inner = m.Groups[1].Value;
                    // ">> deeper" and "> > deeper" (Phase 98)
                    Match deeper;
                    while ((deeper = QuoteRx.Match(inner)).Success) { b.Level++; inner = deeper.Groups[1].Value; }
                    // GitHub's alerts: "> [!NOTE]" titles the quote lines after it at its depth
                    var alert = AlertRx.Match(inner);
                    var prev = blocks.Count > 0 ? blocks[blocks.Count - 1] : null;
                    if (alert.Success)
                    {
                        b.Alert = char.ToUpperInvariant(alert.Groups[1].Value[0]) + alert.Groups[1].Value.Substring(1).ToLowerInvariant();
                        b.AlertTitle = true;
                        inner = alert.Groups[2].Value;
                    }
                    else if (prev != null && prev.Kind == MdBlockKind.Quote && prev.Alert != null && prev.Level == b.Level && i > 0 && QuoteRx.IsMatch(lines[i - 1]))
                        b.Alert = prev.Alert;
                    // "> - item", "> 1. step": the item's marker drawn, not its dash
                    Match item;
                    if ((item = Bullet.Match(inner)).Success && !RuleRx.IsMatch(inner))
                    {
                        b.Inlines.Add(new MdInline { Kind = MdInlineKind.Text, Text = "• " });
                        inner = item.Groups[2].Value;
                    }
                    else if ((item = Ordered.Match(inner)).Success)
                    {
                        b.Inlines.Add(new MdInline { Kind = MdInlineKind.Text, Text = item.Groups[2].Value + ". " });
                        inner = item.Groups[3].Value;
                    }
                    b.Inlines.AddRange(ParseInlines(inner, Open(i)));
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
            Renumber(blocks);
            AssignAnchors(blocks);
            return blocks;
        }

        /// <summary>
        /// Each heading's anchor (Phase 102), as GitHub makes them: <c>[see Setup](#setup)</c> goes there. A heading
        /// that comes again gets <c>-1</c>, <c>-2</c>.
        /// </summary>
        private static void AssignAnchors(List<MdBlock> blocks)
        {
            var seen = new Dictionary<string, int>();
            foreach (var b in blocks)
            {
                if (b.Kind != MdBlockKind.Heading) continue;
                string slug = Slug(AnswerText.Inline(b.Inlines));
                if (seen.TryGetValue(slug, out int n)) { seen[slug] = n + 1; b.Anchor = slug + "-" + n; }
                else { seen[slug] = 1; b.Anchor = slug; }
            }
        }

        /// <summary>GitHub's anchor for a heading: lower case, blanks as <c>-</c>, punctuation dropped.</summary>
        public static string Slug(string heading)
        {
            var sb = new StringBuilder();
            foreach (char c in (heading ?? "").Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else if (c == ' ') sb.Append('-');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Numbered lists count from their first item (Phase 102), as Markdown does: Claude's "1. 1. 1." is 1, 2, 3. A
        /// nested list counts on its own and the outer one goes on after it, also after an item's code block; a bullet
        /// at the same depth, or anything outside a list (a paragraph, a heading), ends the lists.
        /// </summary>
        private static void Renumber(List<MdBlock> blocks)
        {
            var next = new Dictionary<int, int>(); // depth → the number the list there gives its next item
            foreach (var b in blocks)
            {
                if (b.Kind == MdBlockKind.Code && b.Indent > 0) continue; // inside an item
                if (b.Kind != MdBlockKind.ListItem) { next.Clear(); continue; }
                foreach (int deeper in next.Keys.Where(k => k > b.Indent || (k == b.Indent && !b.Number.HasValue)).ToList()) next.Remove(deeper);
                if (!b.Number.HasValue) continue;
                if (next.TryGetValue(b.Indent, out int n)) b.Number = n;
                next[b.Indent] = b.Number.Value + 1;
            }
        }

        private static readonly Regex DetailsOpen = new Regex(@"^\s*<details(?:\s[^>]*)?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DetailsClose = new Regex(@"</details\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SummaryTag = new Regex(@"^\s*<summary>(?<s>.*?)</summary>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        /// <summary>
        /// A collapsed section from the <c>&lt;details&gt;</c> line at <paramref name="i"/> to its <c>&lt;/details&gt;</c>
        /// (sections inside it counted), leaving <paramref name="i"/> on that line. Its summary is the block's inlines
        /// ("Details" without one), what is between the tags its own blocks; unclosed while an answer streams.
        /// </summary>
        private static MdBlock ReadDetails(string[] lines, ref int i, bool streaming)
        {
            var body = new StringBuilder();
            int depth = 0;
            bool closed = false;
            for (; i < lines.Length; i++)
            {
                string line = lines[i];
                if (body.Length == 0 && depth == 0) line = DetailsOpen.Replace(line, "", 1);
                depth += DetailsOpen.Matches(line).Count;
                var close = DetailsClose.Match(line);
                if (close.Success && depth == 0)
                {
                    body.Append(line.Substring(0, close.Index));
                    closed = true;
                    break;
                }
                if (close.Success) depth--;
                body.Append(line).Append('\n');
                if (i == lines.Length - 1) break;
            }
            string text = body.ToString();
            var block = new MdBlock { Kind = MdBlockKind.Details, Closed = closed };
            var summary = SummaryTag.Match(text);
            if (summary.Success)
            {
                block.Inlines.AddRange(ParseInlines(summary.Groups["s"].Value.Trim()));
                text = text.Substring(summary.Index + summary.Length);
            }
            else block.Inlines.Add(new MdInline { Kind = MdInlineKind.Text, Text = "Details" });
            block.Blocks.AddRange(Parse(text.Trim('\n'), streaming && !closed));
            return block;
        }

        /// <summary>
        /// The list item a fence just after it belongs to: the last block, when it is a list item, or a code block
        /// already under one (a second fence in the same item). Null otherwise.
        /// </summary>
        private static MdBlock ListItemAbove(List<MdBlock> blocks)
        {
            var last = blocks.Count > 0 ? blocks[blocks.Count - 1] : null;
            return last == null ? null
                : last.Kind == MdBlockKind.ListItem || (last.Kind == MdBlockKind.Code && last.Indent > 0) ? last
                : null;
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

            void Emit(MdInlineKind kind, string value, string url = null, bool open = false)
            {
                if (plain.Length > 0) { result.Add(new MdInline { Kind = MdInlineKind.Text, Text = plain.ToString() }); plain.Clear(); }
                var inline = new MdInline { Kind = kind, Text = value, Url = url };
                // Bold, italic and struck text hold inlines of their own (Phase 96): **`Validate`**, *see [docs](u)*
                if (kind == MdInlineKind.Bold || kind == MdInlineKind.Italic || kind == MdInlineKind.Strike
                    || kind == MdInlineKind.Sup || kind == MdInlineKind.Sub || kind == MdInlineKind.Underline)
                    inline.Children = ParseInlines(value, open);
                result.Add(inline);
            }

            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];

                if (c == '\\' && i + 1 < s.Length && "\\`*_[]()#+-.!~".IndexOf(s[i + 1]) >= 0) { plain.Append(s[i + 1]); i += 2; continue; }
                // A backslash that ends a line is Markdown's hard break (Phase 102): the break stays, the backslash goes
                if (c == '\\' && i + 1 < s.Length && s[i + 1] == '\n') { i++; continue; }

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
                        if (i + 2 < s.Length) Emit(kind, s.Substring(i + 2), open: true);
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

                // ![alt](path): an image (Phase 98), a link drawn with a picture sign
                if (c == '!' && i + 1 < s.Length && s[i + 1] == '[')
                {
                    int close = s.IndexOf("](", i + 2, StringComparison.Ordinal);
                    int paren = close > 0 ? s.IndexOf(')', close + 2) : -1;
                    if (close > i && paren > close) { Emit(MdInlineKind.Image, s.Substring(i + 2, close - i - 2), s.Substring(close + 2, paren - close - 2).Trim()); i = paren + 1; continue; }
                }

                if (c == '[')
                {
                    int close = s.IndexOf("](", i + 1, StringComparison.Ordinal);
                    int paren = close > 0 ? s.IndexOf(')', close + 2) : -1;
                    if (close > i && paren > close) { Emit(MdInlineKind.Link, s.Substring(i + 1, close - i - 1), s.Substring(close + 2, paren - close - 2)); i = paren + 1; continue; }
                }

                // <kbd>Ctrl</kbd>, <sup>2</sup>, <b>…</b> and the like (Phase 107); other tags stay as written
                if (c == '<' && InlineTag.Match(s, i) is Match tag && tag.Success)
                {
                    string name = tag.Groups["t"].Value.ToLowerInvariant(), inner = tag.Groups["in"].Value;
                    var tagKind = name == "kbd" ? MdInlineKind.Kbd : name == "sup" ? MdInlineKind.Sup : name == "sub" ? MdInlineKind.Sub
                        : name == "u" ? MdInlineKind.Underline : name == "code" ? MdInlineKind.Code
                        : name == "b" || name == "strong" ? MdInlineKind.Bold : MdInlineKind.Italic;
                    Emit(tagKind, tagKind == MdInlineKind.Code ? System.Net.WebUtility.HtmlDecode(inner) : inner);
                    i += tag.Length;
                    continue;
                }
                // <br> is a line break, and &lt; &amp; &nbsp; &#8594; their characters (Phase 98); code keeps them
                if (c == '<' && BreakTag.Match(s, i) is Match br && br.Success && br.Index == i) { plain.Append('\n'); i += br.Length; continue; }
                if (c == '&' && Entity.Match(s, i) is Match ent && ent.Success && ent.Index == i)
                {
                    string decoded = System.Net.WebUtility.HtmlDecode(ent.Value);
                    if (decoded != ent.Value) { plain.Append(decoded); i += ent.Length; continue; }
                }

                // <https://…> and a bare https://… (Phase 96) are links to themselves
                if (c == '<' && IsWebAddressAt(s, i + 1))
                {
                    int end = s.IndexOf('>', i + 1);
                    string url = end > 0 ? s.Substring(i + 1, end - i - 1) : null;
                    if (url != null && url.IndexOfAny(new[] { ' ', '\t', '\n', '<' }) < 0) { Emit(MdInlineKind.Link, url, url); i = end + 1; continue; }
                }
                if ((c == 'h' || c == 'H') && (i == 0 || !char.IsLetterOrDigit(s[i - 1])) && IsWebAddressAt(s, i))
                {
                    int end = WebAddressEnd(s, i);
                    if (end > i) { string url = s.Substring(i, end - i); Emit(MdInlineKind.Link, url, url); i = end; continue; }
                }

                plain.Append(c);
                i++;
            }
            if (plain.Length > 0) result.Add(new MdInline { Kind = MdInlineKind.Text, Text = plain.ToString() });
            return result;
        }

        private static readonly Regex InlineTag = new Regex(@"\G<(?<t>kbd|sup|sub|b|strong|i|em|code|u)>(?<in>.*?)</\k<t>>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BreakTag = new Regex(@"\G<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Entity = new Regex(@"\G&(#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|[a-zA-Z][a-zA-Z0-9]{1,31});", RegexOptions.Compiled);

        /// <summary>
        /// The web addresses in plain text (Phase 105, for tool output), by the rule answers use (Phase 96): http and
        /// https, at a word's start, without the punctuation that ends a sentence around them.
        /// </summary>
        public static List<(int Start, int Length)> WebAddresses(string text)
        {
            var found = new List<(int, int)>();
            if (string.IsNullOrEmpty(text)) return found;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if ((c != 'h' && c != 'H') || (i > 0 && char.IsLetterOrDigit(text[i - 1])) || !IsWebAddressAt(text, i)) continue;
                int end = WebAddressEnd(text, i);
                if (end <= i) continue;
                found.Add((i, end - i));
                i = end - 1;
            }
            return found;
        }

        private static bool IsWebAddressAt(string s, int i)
        {
            foreach (string scheme in new[] { "https://", "http://" })
                if (string.Compare(s, i, scheme, 0, scheme.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    return s.Length > i + scheme.Length && !char.IsWhiteSpace(s[i + scheme.Length]);
            return false;
        }

        /// <summary>
        /// Where a bare web address that starts at <paramref name="i"/> ends: at a blank, a quote or <c>&lt;</c>, and
        /// without what ends the sentence around it (<c>.</c>, <c>,</c>, a <c>)</c> that has no <c>(</c> in the address).
        /// </summary>
        private static int WebAddressEnd(string s, int i)
        {
            int end = i;
            while (end < s.Length && !char.IsWhiteSpace(s[end]) && s[end] != '<' && s[end] != '>' && s[end] != '"' && s[end] != '`') end++;
            while (end > i)
            {
                char last = s[end - 1];
                if (".,;:!?'*_~".IndexOf(last) >= 0) { end--; continue; }
                if (last == ')')
                {
                    string url = s.Substring(i, end - i);
                    if (url.Count(ch => ch == '(') < url.Count(ch => ch == ')')) { end--; continue; }
                }
                break;
            }
            return end;
        }
    }
}
