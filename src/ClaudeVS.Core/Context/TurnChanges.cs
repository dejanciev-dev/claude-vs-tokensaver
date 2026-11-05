using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeVS.Core.Context
{
    /// <summary>One file Claude added or changed during a turn.</summary>
    public sealed class ChangedFile
    {
        public string Path { get; set; }
        /// <summary>The content when the turn first touched it ("" for a new file); null when it could not be read.</summary>
        public string Before { get; set; }
        public bool IsNew { get; set; }
        public bool IsDeleted { get; set; }
        /// <summary>Line counts against <see cref="Before"/>; null when Before is unknown.</summary>
        public int? Added { get; set; }
        public int? Removed { get; set; }
    }

    /// <summary>
    /// The files a turn changes, for the "Files changed" list under the answer: the content from just before the
    /// first change to each file (taken when the tool call arrives), compared with the file when the turn ends.
    /// </summary>
    public sealed class TurnChanges
    {
        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, string> _before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int Count => _order.Count;

        /// <summary>
        /// Remembers a file before its first change in this turn; later changes keep the first copy.
        /// <paramref name="before"/>: the content, "" when the file did not exist, null when unreadable.
        /// </summary>
        public void Record(string fullPath, string before)
        {
            if (string.IsNullOrEmpty(fullPath) || _before.ContainsKey(fullPath)) return;
            _order.Add(fullPath);
            _before[fullPath] = before;
        }

        /// <summary>
        /// The changed files, in the order they were first touched, with line counts; files that ended up as they
        /// were are left out. <paramref name="readNow"/> returns a file's content now, "" when it doesn't exist and
        /// null when it can't be read.
        /// </summary>
        public IReadOnlyList<ChangedFile> Collect(Func<string, string> readNow, Func<string, bool> exists)
        {
            var result = new List<ChangedFile>();
            foreach (var path in _order)
            {
                string before = _before[path];
                bool isNow = exists(path);
                string after = isNow ? readNow(path) : "";
                if (before != null && after != null && Same(before, after)) continue;
                var f = new ChangedFile { Path = path, Before = before, IsNew = before == "" && isNow, IsDeleted = !isNow && before != "" };
                if (before != null && after != null)
                {
                    var lines = InlineDiff.Diff(Split(before), Split(after));
                    f.Added = lines.Count(l => l.Kind == DiffLineKind.Added);
                    f.Removed = lines.Count(l => l.Kind == DiffLineKind.Removed);
                }
                result.Add(f);
            }
            return result;
        }

        public void Clear()
        {
            _order.Clear();
            _before.Clear();
        }

        private static bool Same(string a, string b) => a.Replace("\r\n", "\n") == b.Replace("\r\n", "\n");

        private static List<string> Split(string s)
        {
            if (string.IsNullOrEmpty(s)) return new List<string>();
            var lines = s.Replace("\r\n", "\n").Split('\n').ToList();
            if (lines.Count > 1 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        /// <summary>A full path with the OS separators; relative paths are taken from <paramref name="baseDir"/>.</summary>
        public static string FullPath(string path, string baseDir)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string p = path.Trim().Replace('/', System.IO.Path.DirectorySeparatorChar);
                if (!System.IO.Path.IsPathRooted(p) && !string.IsNullOrEmpty(baseDir)) p = System.IO.Path.Combine(baseDir, p);
                return System.IO.Path.GetFullPath(p);
            }
            catch (Exception) { return null; } // a path the OS can't take (wildcards, ~)
        }
    }
}
