using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudeVS.Core.Context
{
    /// <summary>
    /// A link in Claude's answer to a file or folder on disk: <c>[Login.cs](src/Login.cs:42)</c>,
    /// <c>[a](C:/repo/a.cs#L5-10)</c>, <c>file:///C:/repo/My%20App/</c>. As VS Code's 2.1.332 fix for "links to files
    /// without an extension, to folders, and with a line and column doing nothing": the extension made only web links
    /// clickable before (Phase 90).
    /// </summary>
    public sealed class FileLink
    {
        public string Path { get; private set; }
        /// <summary>1-based; null for none (the file opens with its caret where it was).</summary>
        public int? Line { get; private set; }
        public int? EndLine { get; private set; }
        /// <summary>1-based; only with a line.</summary>
        public int? Column { get; private set; }
        public bool IsFolder { get; private set; }

        /// <summary>What the link's tooltip says: "src\Login.cs, line 42" (relative to the solution when inside it).</summary>
        public string Describe(string baseDir)
        {
            string shown = Path;
            if (!string.IsNullOrEmpty(baseDir))
            {
                string root = System.IO.Path.GetFullPath(baseDir).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
                if (shown.StartsWith(root, StringComparison.OrdinalIgnoreCase)) shown = shown.Substring(root.Length);
            }
            if (IsFolder) return "Open folder " + shown;
            if (Line is int l)
                return shown + (EndLine is int e && e > l ? $", lines {l}–{e}" : $", line {l}") + (Column is int c ? $", column {c}" : "");
            return shown;
        }

        // ":12", ":12:5", ":12-20" after the path
        private static readonly Regex ColonLine = new Regex(@"^(?<path>.+?):(?<line>\d+)(?::(?<col>\d+))?(?:-(?<end>\d+))?$");
        // "L12", "L12-20", "L12-L20", "12", "12-20", "L12C5"
        private static readonly Regex Fragment = new Regex(@"^L?(?<line>\d+)(?:C(?<col>\d+))?(?:-L?(?<end>\d+))?$", RegexOptions.IgnoreCase);
        private static readonly Regex Scheme = new Regex(@"^(?<s>[a-zA-Z][a-zA-Z0-9+.\-]+):");

        /// <summary>
        /// The file or folder a link target names, resolved against <paramref name="baseDir"/> (the solution's folder),
        /// or null: a web or other link, a target that isn't a path, or nothing on disk. <paramref name="fileExists"/>
        /// and <paramref name="dirExists"/> default to the disk (tests pass their own).
        /// </summary>
        public static FileLink Parse(string target, string baseDir, Func<string, bool> fileExists = null, Func<string, bool> dirExists = null)
        {
            fileExists = fileExists ?? File.Exists;
            dirExists = dirExists ?? Directory.Exists;
            string t = (target ?? "").Trim();
            if (t.StartsWith("<", StringComparison.Ordinal) && t.EndsWith(">", StringComparison.Ordinal)) t = t.Substring(1, t.Length - 2).Trim();
            if (t.Length == 0 || t.Length > 1024 || t.IndexOfAny(new[] { '\n', '\r', '"', '|', '*', '?' }) >= 0) return null;

            int? line = null, end = null, col = null;
            var scheme = Scheme.Match(t);
            // "C:" is a drive (one letter), and "Makefile:3" a file and its line, not a scheme
            if (scheme.Success && !(t.Length > scheme.Length && char.IsDigit(t[scheme.Length])))
            {
                if (!string.Equals(scheme.Groups["s"].Value, "file", StringComparison.OrdinalIgnoreCase)) return null;
                int hash = t.IndexOf('#');
                if (hash >= 0) { ReadFragment(t.Substring(hash + 1), ref line, ref end, ref col); t = t.Substring(0, hash); }
                if (!Uri.TryCreate(t, UriKind.Absolute, out Uri uri) || !uri.IsFile) return null;
                t = uri.LocalPath;
            }
            else
            {
                int hash = t.IndexOf('#');
                if (hash > 0 && ReadFragment(t.Substring(hash + 1), ref line, ref end, ref col)) t = t.Substring(0, hash);
            }
            if (line == null)
            {
                var m = ColonLine.Match(t);
                if (m.Success && !(m.Groups["path"].Length == 1 && char.IsLetter(m.Groups["path"].Value[0]))) // "C:12" is no line
                {
                    t = m.Groups["path"].Value;
                    line = Int(m.Groups["line"]); col = Int(m.Groups["col"]); end = Int(m.Groups["end"]);
                }
            }
            if (line is int l0 && l0 < 1) { line = null; col = null; end = null; }

            foreach (string candidate in Candidates(t, baseDir))
            {
                if (fileExists(candidate)) return new FileLink { Path = candidate, Line = line, EndLine = line != null ? end : null, Column = line != null ? col : null };
                if (dirExists(candidate)) return new FileLink { Path = candidate.TrimEnd('\\', '/'), IsFolder = true };
            }
            return null;
        }

        /// <summary>
        /// Inline code that is a file reference: Claude Code is told to write code references as
        /// <c>file_path:line_number</c>, usually in backticks. Files only (a folder name in backticks is more often a
        /// word than a place to go), and only text shaped like a path: no brackets, quotes or operators, and blanks only in a path with slashes, so
        /// <c>int x</c>, <c>Foo(bar)</c> or <c>a = b</c> stay code without a look at the disk. Null otherwise.
        /// </summary>
        public static FileLink FromCode(string code, string baseDir, Func<string, bool> fileExists = null)
        {
            string c = (code ?? "").Trim();
            if (c.Length < 2 || c.Length > 400 || c.IndexOfAny(NotInCodePath) >= 0) return null;
            bool slashes = c.IndexOf('/') >= 0 || c.IndexOf('\\') >= 0;
            if (c.IndexOf('.') < 0 && !slashes) return null; // `Makefile` alone: a word
            if (c.IndexOf(' ') >= 0 && !slashes) return null; // a blank only inside a path (C:\Users\Jane Doe\…)
            if (Scheme.Match(c) is var s && s.Success && s.Groups["s"].Length > 1 && !(c.Length > s.Length && char.IsDigit(c[s.Length]))) return null;
            var link = Parse(c, baseDir, fileExists, _ => false);
            return link != null && !link.IsFolder ? link : null;
        }

        /// <summary>A file reference found in plain text: where it is and where it goes.</summary>
        public struct TextMatch
        {
            public int Start;
            public int Length;
            public FileLink Link;
        }

        // A word shaped like a file path (Phase 99): an optional drive, path characters, a file extension, a line
        private static readonly Regex PathWord = new Regex(@"^(?:[A-Za-z]:)?[\w.\-/\\]*[\w\-]\.[A-Za-z][A-Za-z0-9]{0,9}(?<line>:\d+(?::\d+)?(?:-\d+)?)?$", RegexOptions.Compiled);
        private static readonly Regex Word = new Regex(@"\S+", RegexOptions.Compiled);

        /// <summary>
        /// File references in plain text, <c>see src/Login.cs:42</c> (Phase 99): words shaped like a path, with a
        /// slash or a backslash, or a file name with a line (<c>Login.cs:42</c>), that name a file. Brackets, quotes and
        /// the punctuation that ends a sentence around the word aren't part of it. Other words never go to the disk:
        /// <c>Program.cs</c> alone, <c>ASP.NET</c>, <c>e.g.</c>, web addresses.
        /// </summary>
        public static System.Collections.Generic.List<TextMatch> FindInText(string text, string baseDir, Func<string, bool> fileExists = null)
        {
            var found = new System.Collections.Generic.List<TextMatch>();
            if (string.IsNullOrEmpty(text) || (text.IndexOf('.') < 0)) return found;
            foreach (Match w in Word.Matches(text))
            {
                int start = w.Index, end = w.Index + w.Length;
                while (start < end && "([{\"'*_".IndexOf(text[start]) >= 0) start++;
                while (end > start && ".,;:!?)]}\"'*_".IndexOf(text[end - 1]) >= 0) end--;
                if (end - start < 4) continue;
                string word = text.Substring(start, end - start);
                if (word.Contains("://")) continue;
                var m = PathWord.Match(word);
                if (!m.Success) continue;
                bool slashes = word.IndexOf('/') >= 0 || word.IndexOf('\\') >= 0;
                if (!slashes && !m.Groups["line"].Success) continue;
                if (FromCode(word, baseDir, fileExists) is FileLink link)
                    found.Add(new TextMatch { Start = start, Length = end - start, Link = link });
            }
            return found;
        }

        // Phase 105: how tools name a file and line in their output
        private static readonly Regex[] OutputForms =
        {
            // MSBuild and the C# compiler at a line's start, where a path may hold blanks (C:\Users\Jane Doe\…)
            new Regex(@"^[ \t]*(?<path>(?:[A-Za-z]:)?[^\r\n:*?""<>|()]*[\w\-]\.[A-Za-z][A-Za-z0-9]{0,9})\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)", RegexOptions.Compiled | RegexOptions.Multiline),
            // …and anywhere else: src\Login.cs(42,7): error CS0103 (also (42) and (42,7,42,12))
            new Regex(@"(?<path>(?:[A-Za-z]:)?[\w.\-/\\]*[\w\-]\.[A-Za-z][A-Za-z0-9]{0,9})\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)", RegexOptions.Compiled),
            // Python: File "app/main.py", line 12
            new Regex(@"File ""(?<path>[^""]+)"", line (?<line>\d+)", RegexOptions.Compiled),
            // .NET stack traces: at Demo.Login.Validate() in C:\repo\Login.cs:line 42
            new Regex(@" in (?<path>(?:[A-Za-z]:)?[^:*?""<>|\r\n]+?):line (?<line>\d+)", RegexOptions.Compiled),
            // Grep's lines (Phase 106): C:\repo\src\Login.cs:42:    var x (":" a match, "-" around it), at a line's start
            new Regex(@"^(?<path>(?:[A-Za-z]:)?[^\r\n:*?""<>|]*[\w\-]\.[A-Za-z][A-Za-z0-9]{0,9})[:\-](?<line>\d+)(?::(?<col>\d+))?(?=[:\-\s]|$)", RegexOptions.Compiled | RegexOptions.Multiline),
            // Grep's and Glob's file lists: a line that is a path, blanks and all
            new Regex(@"^[ \t]*(?<path>(?:[A-Za-z]:)?[^\r\n:*?""<>|]*[\w\-]\.[A-Za-z][A-Za-z0-9]{0,9})[ \t]*\r?$", RegexOptions.Compiled | RegexOptions.Multiline),
        };

        /// <summary>
        /// File references in a tool's output (Phase 105): the compiler's <c>path(line,col)</c>, Python's
        /// <c>File "path", line n</c>, .NET's <c>in path:line n</c>, and what <see cref="FindInText"/> finds
        /// (<c>path:line:col</c>, most tools). Each names a file that exists; the match covers the path and the line.
        /// </summary>
        public static System.Collections.Generic.List<TextMatch> FindInOutput(string text, string baseDir, Func<string, bool> fileExists = null)
        {
            var found = new System.Collections.Generic.List<TextMatch>();
            if (string.IsNullOrEmpty(text)) return found;
            foreach (var form in OutputForms)
            {
                foreach (Match m in form.Matches(text))
                {
                    if (found.Any(f => m.Index < f.Start + f.Length && f.Start < m.Index + m.Length)) continue;
                    var line = m.Groups["line"];
                    string target = m.Groups["path"].Value + (line.Success ? ":" + line.Value : "") + (m.Groups["col"].Success ? ":" + m.Groups["col"].Value : "");
                    var link = Parse(target, baseDir, fileExists, _ => false);
                    if (link == null || link.IsFolder) continue;
                    // The link covers the path and the line, not the words around them (File ", " in ")
                    var path = m.Groups["path"];
                    int start = path.Index;
                    int end = !line.Success ? path.Index + path.Length
                        : form == OutputForms[0] || form == OutputForms[1] ? m.Index + m.Length
                        : m.Groups["col"].Success ? m.Groups["col"].Index + m.Groups["col"].Length
                        : line.Index + line.Length;
                    found.Add(new TextMatch { Start = start, Length = end - start, Link = link });
                }
            }
            foreach (var t in FindInText(text, baseDir, fileExists))
                if (!found.Any(f => t.Start < f.Start + f.Length && f.Start < t.Start + t.Length)) found.Add(t);
            found.Sort((a, b) => a.Start.CompareTo(b.Start));
            return found;
        }

        private static readonly char[] NotInCodePath ={ '\t', '(', ')', '[', ']', '{', '}', '<', '>', '=', ',', ';', '\'', '"', '`', '$', '&', '|', '*', '?', '!', '@', '+' };

        private static bool ReadFragment(string fragment, ref int? line, ref int? end, ref int? col)
        {
            var m = Fragment.Match(fragment);
            if (!m.Success) return false;
            line = Int(m.Groups["line"]); col = Int(m.Groups["col"]); end = Int(m.Groups["end"]);
            return true;
        }

        private static int? Int(Group g) =>
            g.Success && int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : (int?)null;

        // The path as written, then percent-decoded (a link's spaces are often %20); relative ones under the solution.
        // "/src/a.cs" is the solution's src first, as Claude Code writes paths from the project root.
        private static System.Collections.Generic.IEnumerable<string> Candidates(string t, string baseDir)
        {
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string decoded = t.IndexOf('%') >= 0 ? SafeUnescape(t) : t;
            foreach (string p in new[] { t, decoded })
            {
                if (p == null) continue;
                string slashed = p.Replace('/', System.IO.Path.DirectorySeparatorChar);
                bool driveless = (slashed.StartsWith("\\", StringComparison.Ordinal) && !slashed.StartsWith("\\\\", StringComparison.Ordinal));
                if (!string.IsNullOrEmpty(baseDir) && (driveless || !System.IO.Path.IsPathRooted(slashed)))
                    if (Full(System.IO.Path.Combine(baseDir, slashed.TrimStart('\\'))) is string under && seen.Add(under)) yield return under;
                if (System.IO.Path.IsPathRooted(slashed) && !driveless && Full(slashed) is string rooted && seen.Add(rooted)) yield return rooted;
            }
        }

        private static string Full(string path)
        {
            try { return System.IO.Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }

        private static string SafeUnescape(string s)
        {
            try { return Uri.UnescapeDataString(s); } catch (UriFormatException) { return null; }
        }
    }
}
