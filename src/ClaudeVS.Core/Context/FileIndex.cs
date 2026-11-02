using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeVS.Core.Setup;

namespace ClaudeVS.Core.Context
{
    /// <summary>An "@query" being typed at the caret.</summary>
    public sealed class AtMention
    {
        /// <summary>Index of the '@' in the prompt.</summary>
        public int Start { get; set; }
        /// <summary>Length of "@query" (including the '@').</summary>
        public int Length { get; set; }
        public string Query { get; set; }

        /// <summary>
        /// The mention that ends at <paramref name="caret"/>, or null. The '@' must start the text or follow
        /// whitespace, so e-mail addresses and decorators like "obj.@x" are ignored.
        /// </summary>
        public static AtMention Find(string text, int caret)
        {
            if (string.IsNullOrEmpty(text) || caret <= 0 || caret > text.Length) return null;
            int i = caret - 1;
            while (i >= 0 && !char.IsWhiteSpace(text[i]) && text[i] != '@') i--;
            if (i < 0 || text[i] != '@') return null;
            if (i > 0 && !char.IsWhiteSpace(text[i - 1])) return null;
            return new AtMention { Start = i, Length = caret - i, Query = text.Substring(i + 1, caret - i - 1) };
        }
    }

    /// <summary>
    /// Relative paths of the files in a solution folder, for the "@" file picker. Skips the same noise folders
    /// the CLAUDE.md scanner does (bin, obj, node_modules, .git, ...) and caps the count for huge repos.
    /// </summary>
    public sealed class FileIndex
    {
        public const int DefaultMaxFiles = 30_000;

        private readonly List<string> _files;

        private FileIndex(string root, List<string> files, bool truncated) { Root = root; _files = files; Truncated = truncated; }

        public string Root { get; }
        public int Count => _files.Count;
        /// <summary>True when the folder had more files than the cap.</summary>
        public bool Truncated { get; }

        public static FileIndex Build(string root, int maxFiles = DefaultMaxFiles)
        {
            var files = new List<string>();
            bool truncated = false;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return new FileIndex(root, files, false);

            string full = Path.GetFullPath(root).TrimEnd('\\', '/');
            var noise = new HashSet<string>(SolutionScanner.NoiseFolders.Select(n => n.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(full);
            while (stack.Count > 0 && !truncated)
            {
                string dir = stack.Pop();
                string[] subdirs, names;
                try { subdirs = Directory.GetDirectories(dir); names = Directory.GetFiles(dir); }
                catch (Exception) { continue; } // access denied, long path, deleted meanwhile
                foreach (var f in names)
                {
                    if (files.Count >= maxFiles) { truncated = true; break; }
                    files.Add(f.Substring(full.Length + 1).Replace('\\', '/'));
                }
                foreach (var d in subdirs)
                {
                    string rel = d.Substring(full.Length + 1).Replace('\\', '/');
                    string name = Path.GetFileName(d);
                    if (noise.Contains(name) || noise.Contains(rel)) continue;
                    stack.Push(d);
                }
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return new FileIndex(full, files, truncated);
        }

        /// <summary>
        /// Best matches for <paramref name="query"/>: file name starts with it, then file name contains it,
        /// then path contains it, then its characters appear in order. Shorter paths win ties.
        /// "/" in the query matches path segments ("svc/price" → src/Services/PriceService.cs).
        /// </summary>
        public List<string> Search(string query, int max = 8)
        {
            string q = (query ?? "").Replace('\\', '/').ToLowerInvariant();
            if (q.Length == 0) return _files.OrderBy(f => f.Count(c => c == '/')).ThenBy(f => f.Length).Take(max).ToList();

            return _files
                .Select(f => new { File = f, Score = Score(f, q) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.File.Length)
                .Take(max)
                .Select(x => x.File)
                .ToList();
        }

        public string FullPath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        private static int Score(string path, string q)
        {
            string p = path.ToLowerInvariant();
            string name = p.Substring(p.LastIndexOf('/') + 1);
            if (name.StartsWith(q)) return 400;
            if (name.Contains(q)) return 300;
            if (p.Contains(q)) return 200;
            return IsSubsequence(q, p) ? 100 : 0;
        }

        private static bool IsSubsequence(string q, string p)
        {
            int j = 0;
            for (int i = 0; i < p.Length && j < q.Length; i++) if (p[i] == q[j]) j++;
            return j == q.Length;
        }
    }
}
