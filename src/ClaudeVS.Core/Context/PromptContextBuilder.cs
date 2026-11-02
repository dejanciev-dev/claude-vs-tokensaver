using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeVS.Core.Context
{
    public sealed class EditorSelection
    {
        public string FilePath { get; set; }
        public int StartLine { get; set; }   // 1-based
        public int EndLine { get; set; }     // inclusive
        public string Text { get; set; }
        public string Language { get; set; }
    }

    public sealed class ContextSettings
    {
        /// <summary>Selections up to this many lines are inlined; larger ones are sent as a reference only.</summary>
        public int InlineMaxLines { get; set; } = 60;
        /// <summary>Hard cap on inlined characters (protects against minified one-liners).</summary>
        public int InlineMaxChars { get; set; } = 6_000;
        /// <summary>Strip leading indentation common to all lines (saves tokens on deeply nested code).</summary>
        public bool Dedent { get; set; } = true;
    }

    public sealed class BuiltPrompt
    {
        public string Text { get; set; }
        public int InlinedLines { get; set; }
        public int ReferencedFiles { get; set; }
        /// <summary>Rough estimate of characters NOT sent because we used references instead of whole files.</summary>
        public long CharsAvoided { get; set; }
    }

    /// <summary>
    /// Builds the smallest prompt that still gives Claude what it needs:
    /// small selections are inlined (dedented), large ones become "path:Lstart-Lend" references
    /// that Claude can Read with a line range on demand. Whole files are never pasted.
    /// </summary>
    public sealed class PromptContextBuilder
    {
        private readonly ContextSettings _s;
        public PromptContextBuilder(ContextSettings settings = null) { _s = settings ?? new ContextSettings(); }

        public BuiltPrompt Build(string userPrompt, string solutionDir, EditorSelection selection = null, IEnumerable<string> extraFiles = null)
        {
            var sb = new StringBuilder();
            var result = new BuiltPrompt();

            if (selection != null && !string.IsNullOrEmpty(selection.FilePath))
            {
                string rel = Relative(solutionDir, selection.FilePath);
                string lines = selection.EndLine > selection.StartLine ? $"L{selection.StartLine}-{selection.EndLine}" : $"L{selection.StartLine}";
                string text = selection.Text ?? "";
                int lineCount = text.Length == 0 ? 0 : text.Split('\n').Length;

                if (lineCount > 0 && lineCount <= _s.InlineMaxLines && text.Length <= _s.InlineMaxChars)
                {
                    sb.Append("`").Append(rel).Append(':').Append(lines).Append("`\n");
                    sb.Append("```").Append(selection.Language ?? "").Append('\n');
                    sb.Append(_s.Dedent ? Dedent(text) : text.TrimEnd());
                    sb.Append("\n```\n\n");
                    result.InlinedLines = lineCount;
                    result.CharsAvoided += Math.Max(0, SafeLength(selection.FilePath) - text.Length);
                }
                else
                {
                    sb.Append("Selected code: `").Append(rel).Append(':').Append(lines)
                      .Append("` (").Append(lineCount).Append(" lines, not inlined — read only that range if needed)\n\n");
                    result.CharsAvoided += Math.Max(text.Length, SafeLength(selection.FilePath));
                }
                result.ReferencedFiles++;
            }

            var files = (extraFiles ?? Enumerable.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count > 0)
            {
                sb.Append("Relevant files: ");
                sb.Append(string.Join(", ", files.Select(f => "`" + Relative(solutionDir, f) + "`")));
                sb.Append("\n\n");
                result.ReferencedFiles += files.Count;
                result.CharsAvoided += files.Sum(SafeLength);
            }

            sb.Append(userPrompt?.Trim() ?? "");
            result.Text = sb.ToString();
            if (result.CharsAvoided < 0) result.CharsAvoided = 0;
            return result;
        }

        public static string Relative(string baseDir, string path)
        {
            if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(path)) return path?.Replace('\\', '/');
            try
            {
                string b = Path.GetFullPath(baseDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                string f = Path.GetFullPath(path);
                if (f.StartsWith(b, StringComparison.OrdinalIgnoreCase)) f = f.Substring(b.Length);
                return f.Replace('\\', '/');
            }
            catch (Exception) { return path.Replace('\\', '/'); }
        }

        public static string Dedent(string text)
        {
            var lines = text.Replace("\r\n", "\n").TrimEnd('\n', ' ', '\t').Split('\n');
            int indent = lines.Where(l => l.Trim().Length > 0)
                              .Select(l => l.Length - l.TrimStart(' ', '\t').Length)
                              .DefaultIfEmpty(0).Min();
            if (indent == 0) return string.Join("\n", lines.Select(l => l.TrimEnd()));
            return string.Join("\n", lines.Select(l => (l.Length >= indent ? l.Substring(indent) : l.TrimStart()).TrimEnd()));
        }

        private static long SafeLength(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
        }
    }
}
