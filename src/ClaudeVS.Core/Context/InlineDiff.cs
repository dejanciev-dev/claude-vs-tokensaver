using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Context
{
    public enum DiffLineKind { Context, Removed, Added, Gap }

    public sealed class DiffLine
    {
        public DiffLineKind Kind { get; set; }
        public string Text { get; set; }
        /// <summary>Line number in the file: the new number for added and unchanged lines, the old one for removed; null when unknown.</summary>
        public int? Number { get; set; }
        public override string ToString() => (Kind == DiffLineKind.Added ? "+" : Kind == DiffLineKind.Removed ? "-" : Kind == DiffLineKind.Gap ? "…" : " ") + Text;
    }

    /// <summary>
    /// The red / green lines shown under a file-changing tool call in the chat: Edit and MultiEdit (old → new
    /// text), Write (the file before, when it was captured, → the new content), and shell commands that write a
    /// file with a heredoc (<c>cat &gt; path &lt;&lt;'EOF'</c>), which Claude uses a lot on Windows.
    /// </summary>
    public sealed class InlineDiff
    {
        /// <summary>Unchanged lines kept around each change.</summary>
        public const int ContextLines = 2;
        private const int MaxLcsCells = 400_000;

        public string FilePath { get; private set; }
        public IReadOnlyList<DiffLine> Lines { get; private set; } = Array.Empty<DiffLine>();
        public int Added { get; private set; }
        public int Removed { get; private set; }
        /// <summary>A new file (or a write whose earlier content is unknown): every line is added.</summary>
        public bool IsNewFile { get; private set; }
        /// <summary>The shell command appends (<c>&gt;&gt;</c>) instead of replacing the file.</summary>
        public bool IsAppend { get; private set; }

        /// <summary>
        /// The diff for a tool call, or null when it does not change a file. <paramref name="before"/> is the file
        /// as it was before the call: for a Write the old content (null: a new file), for an Edit it gives the line
        /// numbers (null: no numbers).
        /// </summary>
        public static InlineDiff FromToolUse(string toolName, JObject input, string before = null)
        {
            if (input == null) return null;
            switch (toolName)
            {
                case "Edit":
                case "MultiEdit":
                {
                    var change = FileChange.FromToolUse(toolName, input);
                    if (change == null) return null;
                    var lines = new List<DiffLine>();
                    foreach (var e in change.Edits)
                    {
                        if (lines.Count > 0) lines.Add(new DiffLine { Kind = DiffLineKind.Gap, Text = "" });
                        lines.AddRange(Trim(Diff(SplitLines(e.OldText), SplitLines(e.NewText), LineOf(before, e.OldText))));
                    }
                    return Make(change.FilePath, lines, isNew: false);
                }
                case "Write":
                {
                    string path = (string)input["file_path"];
                    string content = (string)input["content"];
                    if (string.IsNullOrEmpty(path) || content == null) return null;
                    bool isNew = before == null;
                    var lines = isNew ? Numbered(SplitLines(content))
                                      : Trim(Diff(SplitLines(before), SplitLines(content), 1));
                    return Make(path, lines, isNew);
                }
                case "Bash":
                case "PowerShell":
                    return FromHeredoc((string)input["command"]);
                default:
                    return null;
            }
        }

        // cat > file <<'EOF' … EOF   /   cat <<EOF > file   /   cat >> file <<-"EOF"   (optionally after cd "dir" &&)
        private static readonly Regex HeredocRedirectFirst = new Regex(@"\bcat\s+(?<op>>>?)\s*(?<path>""[^""]+""|'[^']+'|[^\s<>|;&]+)\s*<<-?\s*(?<q>['""]?)(?<tag>\w+)\k<q>[^\n]*\n", RegexOptions.Compiled);
        private static readonly Regex HeredocTagFirst = new Regex(@"\bcat\s+<<-?\s*(?<q>['""]?)(?<tag>\w+)\k<q>\s*(?<op>>>?)\s*(?<path>""[^""]+""|'[^']+'|[^\s<>|;&]+)[^\n]*\n", RegexOptions.Compiled);
        private static readonly Regex CdPrefix = new Regex(@"^\s*cd\s+(?<dir>""[^""]+""|'[^']+'|[^\s;&]+)\s*(&&|;)", RegexOptions.Compiled);

        /// <summary>The first file a shell command writes with a heredoc; null when there is none.</summary>
        public static InlineDiff FromHeredoc(string command)
        {
            if (string.IsNullOrEmpty(command) || command.IndexOf("<<", StringComparison.Ordinal) < 0) return null;
            command = command.Replace("\r\n", "\n");
            var m = HeredocRedirectFirst.Match(command);
            if (!m.Success) m = HeredocTagFirst.Match(command);
            if (!m.Success) return null;

            string tag = m.Groups["tag"].Value;
            int start = m.Index + m.Length;
            var end = new Regex(@"^[ \t]*" + Regex.Escape(tag) + @"[ \t]*$", RegexOptions.Multiline).Match(command, start);
            string body = end.Success ? command.Substring(start, Math.Max(0, end.Index - start)) : command.Substring(start);
            if (body.EndsWith("\n", StringComparison.Ordinal)) body = body.Substring(0, body.Length - 1);

            string path = Unquote(m.Groups["path"].Value);
            var cd = CdPrefix.Match(command);
            if (cd.Success && cd.Index < m.Index && !IsRooted(path))
                path = Unquote(cd.Groups["dir"].Value).TrimEnd('/', '\\') + "/" + path;

            bool append = m.Groups["op"].Value == ">>";
            var lines = append ? SplitLines(body).Select(l => new DiffLine { Kind = DiffLineKind.Added, Text = l }).ToList() : Numbered(SplitLines(body));
            var diff = Make(path, lines, isNew: true);
            diff.IsAppend = append;
            return diff;
        }

        private static InlineDiff Make(string path, List<DiffLine> lines, bool isNew) => new InlineDiff
        {
            FilePath = path,
            Lines = lines,
            Added = lines.Count(l => l.Kind == DiffLineKind.Added),
            Removed = lines.Count(l => l.Kind == DiffLineKind.Removed),
            IsNewFile = isNew,
        };

        private static string Unquote(string s) => s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0] ? s.Substring(1, s.Length - 2) : s;
        private static bool IsRooted(string p) => p.StartsWith("/", StringComparison.Ordinal) || p.StartsWith("~", StringComparison.Ordinal) || (p.Length > 1 && p[1] == ':');

        private static List<string> SplitLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return new List<string>();
            var lines = s.Replace("\r\n", "\n").Split('\n').ToList();
            if (lines.Count > 1 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        private static List<DiffLine> Numbered(List<string> lines) =>
            lines.Select((l, i) => new DiffLine { Kind = DiffLineKind.Added, Text = l, Number = i + 1 }).ToList();

        /// <summary>1-based line where <paramref name="text"/> starts in <paramref name="file"/>; 0 when unknown.</summary>
        private static int LineOf(string file, string text)
        {
            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(text)) return 0;
            string f = file.Replace("\r\n", "\n");
            int at = f.IndexOf(text.Replace("\r\n", "\n"), StringComparison.Ordinal);
            if (at < 0) return 0;
            int line = 1;
            for (int i = 0; i < at; i++) if (f[i] == '\n') line++;
            return line;
        }

        /// <summary>
        /// Line diff: common start and end kept as context, the middle by LCS (or removed-then-added when large).
        /// <paramref name="firstLine"/> &gt; 0 numbers the lines from there (the old and the new file both start at it).
        /// </summary>
        public static List<DiffLine> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b, int firstLine = 0)
        {
            var lines = DiffCore(a, b);
            if (firstLine > 0)
            {
                int oldNo = firstLine, newNo = firstLine;
                foreach (var l in lines)
                {
                    if (l.Kind == DiffLineKind.Removed) l.Number = oldNo++;
                    else if (l.Kind == DiffLineKind.Added) l.Number = newNo++;
                    else { l.Number = newNo++; oldNo++; }
                }
            }
            return lines;
        }

        private static List<DiffLine> DiffCore(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            int pre = 0;
            while (pre < a.Count && pre < b.Count && a[pre] == b[pre]) pre++;
            int suf = 0;
            while (suf < a.Count - pre && suf < b.Count - pre && a[a.Count - 1 - suf] == b[b.Count - 1 - suf]) suf++;

            var result = new List<DiffLine>();
            for (int i = 0; i < pre; i++) result.Add(new DiffLine { Kind = DiffLineKind.Context, Text = a[i] });

            int n = a.Count - pre - suf, m = b.Count - pre - suf;
            if ((long)n * m <= MaxLcsCells)
            {
                var lcs = new int[n + 1, m + 1];
                for (int i = n - 1; i >= 0; i--)
                    for (int j = m - 1; j >= 0; j--)
                        lcs[i, j] = a[pre + i] == b[pre + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                int x = 0, y = 0;
                while (x < n || y < m)
                {
                    if (x < n && y < m && a[pre + x] == b[pre + y]) { result.Add(new DiffLine { Kind = DiffLineKind.Context, Text = a[pre + x] }); x++; y++; }
                    else if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y])) { result.Add(new DiffLine { Kind = DiffLineKind.Added, Text = b[pre + y] }); y++; }
                    else { result.Add(new DiffLine { Kind = DiffLineKind.Removed, Text = a[pre + x] }); x++; }
                }
                // Within each changed block show removed lines first, then added (easier to read).
                result = Regroup(result);
            }
            else
            {
                for (int i = 0; i < n; i++) result.Add(new DiffLine { Kind = DiffLineKind.Removed, Text = a[pre + i] });
                for (int j = 0; j < m; j++) result.Add(new DiffLine { Kind = DiffLineKind.Added, Text = b[pre + j] });
            }

            for (int i = a.Count - suf; i < a.Count; i++) result.Add(new DiffLine { Kind = DiffLineKind.Context, Text = a[i] });
            return result;
        }

        private static List<DiffLine> Regroup(List<DiffLine> lines)
        {
            var output = new List<DiffLine>();
            var removed = new List<DiffLine>();
            var added = new List<DiffLine>();
            void Flush() { output.AddRange(removed); output.AddRange(added); removed.Clear(); added.Clear(); }
            foreach (var l in lines)
            {
                if (l.Kind == DiffLineKind.Removed) removed.Add(l);
                else if (l.Kind == DiffLineKind.Added) added.Add(l);
                else { Flush(); output.Add(l); }
            }
            Flush();
            return output;
        }

        /// <summary>Keeps <see cref="ContextLines"/> unchanged lines around each change; longer unchanged runs become a gap.</summary>
        public static List<DiffLine> Trim(List<DiffLine> lines)
        {
            var keep = new bool[lines.Count];
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Kind == DiffLineKind.Context) continue;
                for (int k = Math.Max(0, i - ContextLines); k <= Math.Min(lines.Count - 1, i + ContextLines); k++) keep[k] = true;
            }
            var result = new List<DiffLine>();
            bool gap = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (keep[i]) { result.Add(lines[i]); gap = false; }
                else if (!gap && result.Count > 0) { result.Add(new DiffLine { Kind = DiffLineKind.Gap, Text = "" }); gap = true; }
            }
            if (result.Count > 0 && result[result.Count - 1].Kind == DiffLineKind.Gap) result.RemoveAt(result.Count - 1);
            return result;
        }
    }
}
