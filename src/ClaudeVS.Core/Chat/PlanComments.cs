using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClaudeVS.Core.Chat
{
    /// <summary>
    /// Turns your edits to a plan opened as a document into feedback for Claude: every line you added becomes a
    /// comment anchored to the plan line above it, every plan line you deleted becomes "remove this". A line diff
    /// (longest common subsequence) on trimmed lines, so re-indenting is not a comment.
    /// </summary>
    public static class PlanComments
    {
        public sealed class Comment
        {
            /// <summary>The nearest unchanged plan line above (trimmed, shortened); null at the very top.</summary>
            public string After { get; set; }
            /// <summary>Added text (several added lines in a row are joined), or null for a removal.</summary>
            public string Text { get; set; }
            /// <summary>A plan line you deleted, or null.</summary>
            public string Removed { get; set; }
        }

        public static List<Comment> Extract(string original, string edited)
        {
            var a = Lines(original);
            var b = Lines(edited);
            var comments = new List<Comment>();
            if (a.SequenceEqual(b)) return comments;

            // LCS table on trimmed lines. Plans are a few hundred lines at most.
            int n = a.Count, m = b.Count;
            var lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

            string anchor = null;
            var added = new List<string>();
            void FlushAdded()
            {
                var text = string.Join("\n", added.Where(l => l.Length > 0)).Trim();
                if (text.Length > 0) comments.Add(new Comment { After = anchor, Text = text });
                added.Clear();
            }

            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[x] == b[y]) { FlushAdded(); if (a[x].Length > 0) anchor = Short(a[x]); x++; y++; }
                else if (y < m && (x >= n || lcs[x, y + 1] >= lcs[x + 1, y])) { added.Add(b[y]); y++; }
                else
                {
                    FlushAdded();
                    if (a[x].Length > 0) comments.Add(new Comment { After = anchor, Removed = Short(a[x]) });
                    x++;
                }
            }
            FlushAdded();
            return comments;
        }

        /// <summary>The feedback message for "Request changes", or null when the document was not changed.</summary>
        public static string ToFeedback(IReadOnlyList<Comment> comments)
        {
            if (comments == null || comments.Count == 0) return null;
            var sb = new StringBuilder("Comments on the plan (from the plan document):\n");
            foreach (var c in comments)
            {
                string where = c.After == null ? "At the top" : $"After \"{c.After}\"";
                if (c.Removed != null) sb.Append($"- Remove: \"{c.Removed}\"\n");
                else sb.Append($"- {where}: {c.Text.Replace("\n", " / ")}\n");
            }
            return sb.ToString().TrimEnd();
        }

        private static List<string> Lines(string s) =>
            (s ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();

        private static string Short(string line) => line.Length <= 80 ? line : line.Substring(0, 79) + "…";
    }
}
