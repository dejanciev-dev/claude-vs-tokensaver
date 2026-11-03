using System;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeVS.Core.Tokens;

namespace ClaudeVS.Core.Chat
{
    /// <summary>Turns a conversation read from the CLI's session file into a Markdown document.</summary>
    public static class ConversationExport
    {
        /// <summary>
        /// "# title", then "## You" / "## Claude" sections. Tool calls become a short list under the answer
        /// ("`Read: path` · ~1.2k tok"), never their full input or output; <paramref name="includeTools"/> = false
        /// leaves them out.
        /// </summary>
        public static string ToMarkdown(SessionTranscript transcript, string title, DateTime exportedLocal, bool includeTools = true)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(OneLine(string.IsNullOrWhiteSpace(title) ? "Claude Code conversation" : title)).Append("\n\n");
            sb.Append("*Exported from Claude Code in Visual Studio on ").Append(exportedLocal.ToString("yyyy-MM-dd HH:mm")).Append("*\n");
            if (transcript.Skipped > 0) sb.Append("\n*").Append(transcript.Skipped).Append(" earlier items are not included.*\n");

            bool claudeOpen = false; // inside a "## Claude" section, so the next answer or tool row continues it
            bool inToolList = false;
            foreach (var item in transcript.Items)
            {
                if (item.Kind == TranscriptItemKind.Tool && !includeTools) continue;
                if (item.Kind != TranscriptItemKind.Tool) inToolList = false;
                switch (item.Kind)
                {
                    case TranscriptItemKind.User:
                        sb.Append("\n## You\n\n").Append(item.Text.Trim()).Append('\n');
                        claudeOpen = false;
                        break;
                    case TranscriptItemKind.Answer:
                        if (!claudeOpen) { sb.Append("\n## Claude\n"); claudeOpen = true; }
                        sb.Append('\n').Append(item.Text.Trim()).Append('\n');
                        break;
                    case TranscriptItemKind.Tool:
                        if (!claudeOpen) { sb.Append("\n## Claude\n"); claudeOpen = true; }
                        if (!inToolList) { sb.Append('\n'); inToolList = true; }
                        sb.Append("- 🔧 `").Append(OneLine(item.Text).Replace("`", "'")).Append('`');
                        if (item.ResultChars > 0) sb.Append(" · ~").Append(TokenMeter.Format(item.ResultChars / 4)).Append(" tok");
                        if (item.IsError) sb.Append(" · failed");
                        sb.Append('\n');
                        break;
                    case TranscriptItemKind.Compacted:
                        sb.Append("\n---\n\n*Conversation compacted here; Claude continued from a summary of the part above.*\n");
                        claudeOpen = false;
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>A safe file name from the title: invalid characters become '-', at most 60 characters, ".md".</summary>
        public static string FileName(string title)
        {
            var invalid = Path.GetInvalidFileNameChars();
            string s = new string(OneLine(title ?? "").Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim(' ', '.', '-');
            if (s.Length > 60) s = s.Substring(0, 60).TrimEnd(' ', '.', '-');
            return (s.Length == 0 ? "claude-conversation" : s) + ".md";
        }

        private static string OneLine(string s) =>
            string.Join(" ", (s ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}
