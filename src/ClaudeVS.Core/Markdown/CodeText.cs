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

        /// <summary>
        /// A diff block's new side (Phase 103), for <c>Insert</c>: context and added lines without their marker; removed
        /// lines, file lines and <c>@@</c> headers left out. Null when <paramref name="language"/> isn't a diff or the
        /// diff adds and removes nothing.
        /// </summary>
        public static string DiffNewSide(string code, string language)
        {
            string lang = (language ?? "").Trim().ToLowerInvariant();
            if (lang != "diff" && lang != "patch" && lang != "udiff") return null;
            var kept = new List<string>();
            bool changes = false;
            foreach (string line in SplitLines(code))
            {
                if (line.StartsWith("@@") || line.StartsWith("+++ ") || line.StartsWith("--- ") || line.StartsWith("diff ")
                    || line.StartsWith("index ") || line.StartsWith("\\ No newline")) continue;
                if (line.StartsWith("+")) { kept.Add(line.Substring(1)); changes = true; }
                else if (line.StartsWith("-")) changes = true;
                else if (line.StartsWith(" ")) kept.Add(line.Substring(1));
                else kept.Add(line);
            }
            return changes ? string.Join("\n", kept) : null;
        }

        // "$ dotnet test", "PS> Get-Item", "PS C:\repo> Get-Item"; in a console block also "> " and "# " (root)
        private static readonly System.Text.RegularExpressions.Regex ShellPrompt =
            new System.Text.RegularExpressions.Regex(@"^\s*(?:\$|PS(?: [^>]*)?>)\s");
        private static readonly System.Text.RegularExpressions.Regex ConsolePrompt =
            new System.Text.RegularExpressions.Regex(@"^\s*(?:\$|#|>|PS(?: [^>]*)?>)\s");
        private static readonly HashSet<string> ConsoleLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "console", "shell-session", "shellsession", "terminal", "ps-session", "pwsh-session", "doscon" };

        /// <summary>
        /// The commands of a block that shows a terminal session (Phase 103): the lines after a prompt, without it, and
        /// the lines that continue them (after a <c>\</c> or <c>`</c> at a line's end); output left out. Null when the
        /// block has no prompts: then it is code, and copied as it is. <c>#</c> and <c>&gt;</c> count as prompts only
        /// in a <c>console</c>-like block, where they aren't comments or redirections.
        /// </summary>
        public static string ConsoleCommands(string code, string language)
        {
            var prompt = ConsoleLanguages.Contains((language ?? "").Trim()) ? ConsolePrompt : ShellPrompt;
            var commands = new List<string>();
            bool continued = false;
            foreach (string line in SplitLines(code))
            {
                string command;
                var m = prompt.Match(line);
                if (m.Success) command = line.Substring(m.Length);
                else if (continued) command = line;
                else continue;
                commands.Add(command.TrimEnd());
                string end = command.TrimEnd();
                continued = end.EndsWith("\\", StringComparison.Ordinal) || end.EndsWith("`", StringComparison.Ordinal);
            }
            return commands.Count == 0 ? null : string.Join("\r\n", commands);
        }
    }
}
