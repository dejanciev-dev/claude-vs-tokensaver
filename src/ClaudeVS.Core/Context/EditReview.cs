using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Context
{
    /// <summary>One block of changed lines in a proposed edit, which the user can accept or reject.</summary>
    public sealed class ReviewHunk
    {
        public int Index { get; set; }
        /// <summary>1-based file line where the hunk starts (best effort for Edit: where old_string is in the file).</summary>
        public int FileLine { get; set; }
        public List<string> OldLines { get; } = new List<string>();
        public List<string> NewLines { get; } = new List<string>();
        public bool Accepted { get; set; } = true;

        // Diff position inside the compared texts.
        internal int OldStart, OldEnd, NewStart, NewEnd;

        public string Label
        {
            get
            {
                int count = Math.Max(1, OldLines.Count);
                string where = OldLines.Count == 0 ? $"after line {Math.Max(0, FileLine - 1)}"
                    : count == 1 ? $"line {FileLine}" : $"lines {FileLine}–{FileLine + count - 1}";
                string what = OldLines.Count == 0 ? $"+{NewLines.Count}" : NewLines.Count == 0 ? $"−{OldLines.Count}" : $"−{OldLines.Count} +{NewLines.Count}";
                return $"{where} ({what})";
            }
        }
    }

    /// <summary>
    /// A proposed Edit or Write split into separate changes, so the user can apply some and reject others. The
    /// accepted ones go back to the CLI as the tool's <c>updatedInput</c> (verified with 2.1.318: the CLI writes
    /// exactly that), and <see cref="NoteForClaude"/> is attached to the tool result by a PostToolUse hook, because
    /// the CLI itself only reports "updated successfully".
    /// </summary>
    public sealed class EditReview
    {
        /// <summary>Above this many lines × lines the diff is not computed line by line (one change instead).</summary>
        private const long MaxDiffCells = 4_000_000;
        private const int MaxNoteLines = 8;

        private string[] _old;
        private string[] _new;

        public string ToolName { get; private set; }
        public string FilePath { get; private set; }
        public JObject OriginalInput { get; private set; }
        public List<ReviewHunk> Hunks { get; } = new List<ReviewHunk>();

        public bool AllAccepted => Hunks.All(h => h.Accepted);
        public bool NoneAccepted => Hunks.All(h => !h.Accepted);

        public static bool Supports(string toolName) => toolName == "Edit" || toolName == "Write";

        /// <summary>
        /// Splits an Edit (old_string → new_string) or a Write (current file → content) into hunks. Null for other
        /// tools or when nothing changes. <paramref name="currentFile"/> is the file on disk, or null when it does not exist.
        /// </summary>
        public static EditReview Create(string toolName, JObject input, string currentFile)
        {
            if (input == null || !Supports(toolName)) return null;
            var r = new EditReview { ToolName = toolName, FilePath = (string)input["file_path"], OriginalInput = input };
            string before, after;
            int lineOffset = 0;
            if (toolName == "Edit")
            {
                before = (string)input["old_string"] ?? "";
                after = (string)input["new_string"] ?? "";
                int at = currentFile?.IndexOf(before, StringComparison.Ordinal) ?? -1;
                if (at >= 0 && before.Length > 0) lineOffset = CountLines(currentFile, at);
            }
            else
            {
                before = currentFile ?? "";
                after = (string)input["content"] ?? "";
            }
            r._old = SplitLines(before);
            r._new = SplitLines(after);
            foreach (var h in Diff(r._old, r._new))
            {
                h.Index = r.Hunks.Count;
                h.FileLine = lineOffset + h.OldStart + 1;
                for (int i = h.OldStart; i < h.OldEnd; i++) h.OldLines.Add(r._old[i].TrimEnd('\r'));
                for (int i = h.NewStart; i < h.NewEnd; i++) h.NewLines.Add(r._new[i].TrimEnd('\r'));
                r.Hunks.Add(h);
            }
            return r.Hunks.Count == 0 ? null : r;
        }

        /// <summary>The tool input with only the accepted hunks applied (the rejected ones keep the old lines).</summary>
        public JObject BuildInput()
        {
            var input = (JObject)OriginalInput.DeepClone();
            string merged = Merge();
            if (ToolName == "Edit") input["new_string"] = merged;
            else input["content"] = merged;
            return input;
        }

        /// <summary>
        /// Unchanged lines come from the proposal, so accepting everything gives exactly Claude's text; kept old lines
        /// take the proposal's line endings.
        /// </summary>
        private string Merge()
        {
            bool crlf = _new.Length > 1 && _new.Take(_new.Length - 1).Any(l => l.EndsWith("\r"));
            var lines = new List<string>();
            var fromOld = new List<int>(); // indexes in lines that came from the old text
            int ni = 0;
            foreach (var h in Hunks)
            {
                for (; ni < h.NewStart; ni++) lines.Add(_new[ni]);
                if (h.Accepted) for (int i = h.NewStart; i < h.NewEnd; i++) lines.Add(_new[i]);
                else for (int i = h.OldStart; i < h.OldEnd; i++) { fromOld.Add(lines.Count); lines.Add(_old[i]); }
                ni = h.NewEnd;
            }
            for (; ni < _new.Length; ni++) lines.Add(_new[ni]);
            foreach (int i in fromOld)
            {
                string bare = lines[i].TrimEnd('\r');
                lines[i] = crlf && i < lines.Count - 1 ? bare + "\r" : bare;
            }
            return string.Join("\n", lines);
        }

        /// <summary>The whole file as the proposal would leave it (for the diff window); null when it cannot be built.</summary>
        public static string ProposedFile(string toolName, JObject input, string currentFile)
        {
            if (toolName == "Write") return (string)input?["content"] ?? "";
            if (toolName != "Edit" || currentFile == null) return null;
            string oldS = (string)input["old_string"] ?? "", newS = (string)input["new_string"] ?? "";
            int at = oldS.Length == 0 ? -1 : currentFile.IndexOf(oldS, StringComparison.Ordinal);
            if (at < 0) return null;
            if (input.Value<bool?>("replace_all") == true) return currentFile.Replace(oldS, newS);
            return currentFile.Substring(0, at) + newS + currentFile.Substring(at + oldS.Length);
        }

        /// <summary>
        /// What Claude is told when only some changes were applied: which were rejected (old and proposed lines), so
        /// it neither assumes they are in the file nor applies them again. Null when everything was accepted.
        /// </summary>
        public string NoteForClaude()
        {
            var rejected = Hunks.Where(h => !h.Accepted).ToList();
            if (rejected.Count == 0) return null;
            var sb = new StringBuilder();
            sb.Append("The user reviewed this ").Append(ToolName == "Write" ? "write" : "edit").Append(" and applied only ")
              .Append(Hunks.Count - rejected.Count).Append(" of ").Append(Hunks.Count).Append(" changes. These were rejected and are NOT in the file:\n");
            foreach (var h in rejected)
            {
                sb.Append("- ").Append(h.Label).Append(": kept\n");
                AppendLines(sb, h.OldLines, "(nothing)");
                sb.Append("  instead of\n");
                AppendLines(sb, h.NewLines, "(nothing)");
            }
            sb.Append("Do not apply the rejected changes again unless the user asks.");
            return sb.ToString();
        }

        private static void AppendLines(StringBuilder sb, List<string> lines, string empty)
        {
            if (lines.Count == 0) { sb.Append("    ").Append(empty).Append('\n'); return; }
            foreach (var l in lines.Take(MaxNoteLines)) sb.Append("    ").Append(l).Append('\n');
            if (lines.Count > MaxNoteLines) sb.Append("    … ").Append(lines.Count - MaxNoteLines).Append(" more lines\n");
        }

        /// <summary>Lines split on \n, keeping any \r, so joining with \n gives back the exact text.</summary>
        private static string[] SplitLines(string text) => text.Length == 0 ? new string[0] : text.Split('\n');

        private static int CountLines(string text, int upTo)
        {
            int n = 0;
            for (int i = 0; i < upTo; i++) if (text[i] == '\n') n++;
            return n;
        }

        /// <summary>
        /// Line diff (longest common subsequence) grouped into hunks: each run of changed lines between unchanged ones.
        /// Lines compare without a trailing \r, so a CRLF file and LF proposal only differ where the text does.
        /// </summary>
        internal static List<ReviewHunk> Diff(string[] a, string[] b)
        {
            var hunks = new List<ReviewHunk>();
            int pre = 0;
            while (pre < a.Length && pre < b.Length && Same(a[pre], b[pre])) pre++;
            int suf = 0;
            while (suf < a.Length - pre && suf < b.Length - pre && Same(a[a.Length - 1 - suf], b[b.Length - 1 - suf])) suf++;
            int n = a.Length - pre - suf, m = b.Length - pre - suf;
            if (n == 0 && m == 0) return hunks;
            if ((long)n * m > MaxDiffCells || n == 0 || m == 0)
            {
                hunks.Add(new ReviewHunk { OldStart = pre, OldEnd = pre + n, NewStart = pre, NewEnd = pre + m });
                return hunks;
            }

            // lcs[i, j] = LCS length of a[pre+i..] and b[pre+j..]
            var lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    lcs[i, j] = Same(a[pre + i], b[pre + j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

            int x = 0, y = 0;
            ReviewHunk open = null;
            void Close() { if (open != null) { open.OldEnd = pre + x; open.NewEnd = pre + y; hunks.Add(open); open = null; } }
            while (x < n || y < m)
            {
                if (x < n && y < m && Same(a[pre + x], b[pre + y])) { Close(); x++; y++; continue; }
                if (open == null) open = new ReviewHunk { OldStart = pre + x, NewStart = pre + y };
                if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y])) y++;
                else x++;
            }
            Close();
            return hunks;
        }

        private static bool Same(string x, string y) => string.Equals(x.TrimEnd('\r'), y.TrimEnd('\r'), StringComparison.Ordinal);
    }
}
