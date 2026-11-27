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
                        if (item.ResultChars > 0) sb.Append(" · ~").Append(TokenMeter.Format(TokenEstimate.FromChars(item.ResultChars))).Append(" tok");
                        if (item.IsError) sb.Append(" · failed");
                        sb.Append('\n');
                        break;
                    case TranscriptItemKind.Compacted:
                        sb.Append("\n---\n\n*Conversation compacted here; Claude continued from a summary of the part above.*\n");
                        if (!string.IsNullOrWhiteSpace(item.Text))
                            sb.Append("\n<details>\n<summary>Summary Claude continued from</summary>\n\n").Append(item.Text.Trim()).Append("\n\n</details>\n");
                        claudeOpen = false;
                        break;
                }
            }
            return sb.ToString();
        }

        private const string PageStyle =
            "body{font-family:'Segoe UI',system-ui,sans-serif;max-width:52em;margin:2em auto;padding:0 1em;line-height:1.5;color:#1f2328}" +
            "h1{font-size:1.6em}h2{font-size:1.2em;border-bottom:1px solid #d0d7de;padding-bottom:.2em;margin-top:1.6em}" +
            "code{font-family:Consolas,'Cascadia Mono',monospace;background:#f6f8fa;padding:0 .2em;border-radius:3px}" +
            "pre code{padding:0;background:none}blockquote{margin:0;padding:0 1em;border-left:3px solid #d0d7de;color:#57606a}" +
            "table{border-collapse:collapse}th,td{border:1px solid #d0d7de;padding:.2em .6em}th{background:#f6f8fa}" +
            "li.tool{color:#6e7781;font-size:.9em;list-style:none}kbd{border:1px solid #d0d7de;border-bottom-width:2px;border-radius:3px;padding:0 .3em;font-size:.9em}";

        /// <summary>
        /// The conversation as a web page (Phase 109): the Markdown of <see cref="ToMarkdown"/> as HTML, styled for
        /// reading, the tool lines muted. One file, nothing loaded from elsewhere.
        /// </summary>
        public static string ToHtml(SessionTranscript transcript, string title, DateTime exportedLocal, bool includeTools = true)
        {
            string body = Markdown.MarkdownHtml.Render(ToMarkdown(transcript, title, exportedLocal, includeTools))
                .Replace("<li>🔧 ", "<li class=\"tool\">🔧 ");
            string pageTitle = Markdown.MarkdownHtml.E(OneLine(string.IsNullOrWhiteSpace(title) ? "Claude Code conversation" : title));
            return "<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n" +
                   "<title>" + pageTitle + "</title>\n<style>" + PageStyle + "</style>\n</head>\n<body>\n" + body + "</body>\n</html>\n";
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
