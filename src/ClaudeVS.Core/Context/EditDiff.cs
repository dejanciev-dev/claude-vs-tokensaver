using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Context
{
    public sealed class TextEdit
    {
        public string OldText { get; set; }
        public string NewText { get; set; }
        public bool ReplaceAll { get; set; }
    }

    /// <summary>A file change made by one of Claude Code's file tools (Edit, MultiEdit, Write).</summary>
    public sealed class FileChange
    {
        public string FilePath { get; set; }
        public string ToolName { get; set; }
        /// <summary>Replacements in the order they were applied (Edit, MultiEdit).</summary>
        public List<TextEdit> Edits { get; } = new List<TextEdit>();
        /// <summary>True for Write: the whole file was replaced, so the old content cannot be rebuilt.</summary>
        public bool IsWrite { get; set; }

        /// <summary>Reads the tool input; null for tools that do not change a file.</summary>
        public static FileChange FromToolUse(string toolName, JObject input)
        {
            string path = (string)input?["file_path"];
            if (string.IsNullOrEmpty(path)) return null;
            var change = new FileChange { FilePath = path, ToolName = toolName };
            switch (toolName)
            {
                case "Edit":
                    change.Edits.Add(ReadEdit(input));
                    break;
                case "MultiEdit":
                    if (!(input["edits"] is JArray edits)) return null;
                    change.Edits.AddRange(edits.OfType<JObject>().Select(ReadEdit));
                    break;
                case "Write":
                    change.IsWrite = true;
                    break;
                default:
                    return null;
            }
            return change.IsWrite || change.Edits.Count > 0 ? change : null;
        }

        private static TextEdit ReadEdit(JObject e) => new TextEdit
        {
            OldText = (string)e["old_string"] ?? "",
            NewText = (string)e["new_string"] ?? "",
            ReplaceAll = e.Value<bool?>("replace_all") ?? false,
        };

        /// <summary>
        /// The file content before this change, rebuilt from the content after it by undoing the replacements
        /// in reverse order. Returns null when that is not possible: a Write, an edit whose new text is empty or
        /// no longer found exactly once (for single replacements), or a file changed again since.
        /// </summary>
        public string ReconstructBefore(string after)
        {
            if (IsWrite || after == null) return null;
            // Edit text usually uses "\n"; a Windows file uses "\r\n". Match the file's line endings.
            bool crlf = after.Contains("\r\n");
            string Fit(string s) => crlf ? s.Replace("\r\n", "\n").Replace("\n", "\r\n") : s;

            string text = after;
            for (int i = Edits.Count - 1; i >= 0; i--)
            {
                var e = new TextEdit { OldText = Fit(Edits[i].OldText), NewText = Fit(Edits[i].NewText), ReplaceAll = Edits[i].ReplaceAll };
                if (e.NewText.Length == 0) return null; // a deletion can't be located in the result
                if (e.ReplaceAll)
                {
                    if (text.IndexOf(e.NewText, StringComparison.Ordinal) < 0) return null;
                    text = text.Replace(e.NewText, e.OldText);
                    continue;
                }
                int at = text.IndexOf(e.NewText, StringComparison.Ordinal);
                if (at < 0 || text.IndexOf(e.NewText, at + 1, StringComparison.Ordinal) >= 0) return null; // missing or ambiguous
                text = text.Substring(0, at) + e.OldText + text.Substring(at + e.NewText.Length);
            }
            return text;
        }
    }
}
