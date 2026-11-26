using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClaudeVS.Core.Markdown
{
    /// <summary>A code block's code as it goes into the editor or onto the clipboard (Phase 103).</summary>
    public static class CodeText
    {
        private static string[] SplitLines(string code) => (code ?? "").Replace("\r\n", "\n").Split('\n');

        /// <summary>
        /// The code as <c>Insert</c> puts it at the caret: the block's common indent taken off, each line after the
        /// first starting at the indent of the caret's line (<paramref name="beforeCaret"/> is that line's text up to
        /// the caret), line breaks as <paramref name="newline"/>. Blank lines stay blank.
        /// </summary>
        public static string FitToCaret(string code, string beforeCaret, string newline = "\r\n")
        {
            var lines = SplitLines(code);
            int common = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart(' ', '\t').Length).DefaultIfEmpty(0).Min();
            string line = beforeCaret ?? "";
            string indent = line.Substring(0, line.Length - line.TrimStart(' ', '\t').Length);
            var sb = new StringBuilder();
            for (int k = 0; k < lines.Length; k++)
            {
                string l = lines[k];
                string body = l.Trim().Length == 0 ? "" : l.Substring(Math.Min(common, l.Length));
                if (k > 0) sb.Append(newline);
                if (k > 0 && body.Length > 0) sb.Append(indent);
                sb.Append(body);
            }
            return sb.ToString();
        }
    }
}
