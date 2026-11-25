using System;
using System.Globalization;
using System.IO;
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
