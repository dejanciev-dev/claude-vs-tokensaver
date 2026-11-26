using System;
using System.Linq;

namespace ClaudeVS.Core.Markdown
{
    /// <summary>
    /// Long code blocks in answers are folded (Phase 97): a 300-line file Claude prints would fill screens and push
    /// the rest of the answer away. A finished block of more than <see cref="FoldOver"/> lines shows its first
    /// <see cref="Shown"/> and a button for the rest; Copy and Insert still take the whole code. A block still
    /// streaming isn't folded: its end is what is new.
    /// </summary>
    public static class CodeFold
    {
        public const int FoldOver = 40;
        public const int Shown = 20;

        /// <summary>The number of lines in <paramref name="code"/> (a last empty line not counted).</summary>
        public static int Lines(string code)
        {
            if (string.IsNullOrEmpty(code)) return 0;
            int n = 1;
            foreach (char c in code) if (c == '\n') n++;
            return code.EndsWith("\n", StringComparison.Ordinal) ? n - 1 : n;
        }

        /// <summary>Whether a block is drawn folded: finished, and longer than <see cref="FoldOver"/> lines.</summary>
        public static bool Folds(string code, bool closed) => closed && Lines(code) > FoldOver;

        /// <summary>The first <see cref="Shown"/> lines, without the line break after the last.</summary>
        public static string Head(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            int at = -1;
            for (int k = 0; k < Shown; k++)
            {
                at = code.IndexOf('\n', at + 1);
                if (at < 0) return code;
            }
            return code.Substring(0, at).TrimEnd('\r');
        }

        /// <summary>The button that unfolds it: "Show all 312 lines".</summary>
        public static string ShowAll(string code) => "Show all " + Lines(code).ToString(System.Globalization.CultureInfo.InvariantCulture) + " lines";
    }

    /// <summary>
    /// Long tables in answers are folded (Phase 102), as long code is: more than <see cref="FoldOver"/> rows under the
    /// header show the header, the first <see cref="Shown"/> and a link for the rest. A table still arriving folds
    /// once it is that long, and the link's count grows with it.
    /// </summary>
    public static class TableFold
    {
        public const int FoldOver = 25;
        public const int Shown = 10;

        /// <summary>The rows under the header.</summary>
        public static int Rows(MdBlock table) => table == null || table.Kind != MdBlockKind.Table ? 0 : Math.Max(0, table.Rows.Count - 1);

        public static bool Folds(MdBlock table) => Rows(table) > FoldOver;

        /// <summary>"Show all 60 rows".</summary>
        public static string ShowAll(MdBlock table) => "Show all " + Rows(table).ToString(System.Globalization.CultureInfo.InvariantCulture) + " rows";
    }

    /// <summary>
    /// A tool's long output is folded (Phase 105): more than <see cref="FoldOver"/> lines show the first
    /// <see cref="HeadLines"/> and the last <see cref="TailLines"/>, a build's or a test run's end being what matters.
    /// Lines are split at <c>\n</c> (a <c>\r</c> before it stays with its line).
    /// </summary>
    public static class OutputFold
    {
        public const int FoldOver = 200;
        public const int HeadLines = 20;
        public const int TailLines = 80;

        private static string[] Split(string text) => (text ?? "").TrimEnd().Split('\n');

        public static int Lines(string text) => string.IsNullOrWhiteSpace(text) ? 0 : Split(text).Length;

        public static bool Folds(string text) => Lines(text) > FoldOver;

        public static string Head(string text) => string.Join("\n", Split(text).Take(HeadLines));

        public static string Tail(string text) { var l = Split(text); return string.Join("\n", l.Skip(Math.Max(0, l.Length - TailLines))); }

        /// <summary>The lines between the head and the tail.</summary>
        public static int Hidden(string text) => Math.Max(0, Lines(text) - HeadLines - TailLines);

        /// <summary>"Show all 1200 lines".</summary>
        public static string ShowAll(string text) => "Show all " + Lines(text).ToString(System.Globalization.CultureInfo.InvariantCulture) + " lines";
    }
}
