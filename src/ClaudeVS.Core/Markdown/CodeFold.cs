using System;

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
}
