using System;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeVS.Core.Tokens;

namespace ClaudeVS.Core.Chat
{
    /// <summary>What an export does with the tool calls (Phase 110).</summary>
    public enum ExportTools
    {
        /// <summary>A short line per call ("`Read: path` · ~1.2k tok").</summary>
        Lines,
        /// <summary>The line and, folded under it, what the call returned.</summary>
        WithOutput,
        /// <summary>No tool calls, only the prompts and answers.</summary>
        None,
    }

    /// <summary>How a conversation is exported (Phase 110).</summary>
    public sealed class ExportOptions
    {
        public ExportTools Tools { get; set; } = ExportTools.Lines;
        /// <summary>The prompt to start at (its message id in the session file); null for the whole conversation.</summary>
        public string FromUuid { get; set; }
        /// <summary>The same prompt's text, used when its id isn't in the file (the last prompt with this text).</summary>
        public string FromText { get; set; }
    }

    /// <summary>Turns a conversation read from the CLI's session file into a Markdown document.</summary>
    public static class ConversationExport
    {
        /// <summary>
        /// The item a "from here" export starts at (Phase 110): the prompt with <paramref name="uuid"/>, else the last
        /// prompt whose text is <paramref name="text"/>; 0 (the start) when neither is given or found.
        /// </summary>
        public static int StartIndex(SessionTranscript transcript, string uuid, string text)
        {
            var items = transcript.Items;
            if (!string.IsNullOrEmpty(uuid))
                for (int i = 0; i < items.Count; i++)
                    if (items[i].Kind == TranscriptItemKind.User && items[i].Uuid == uuid) return i;
            if (!string.IsNullOrWhiteSpace(text))
                for (int i = items.Count - 1; i >= 0; i--)
                    if (items[i].Kind == TranscriptItemKind.User && (items[i].Text ?? "").Trim() == text.Trim()) return i;
            return 0;
        }

        /// <summary>
        /// "# title", then "## You" / "## Claude" sections. Tool calls become a short list under the answer
        /// ("`Read: path` · ~1.2k tok"), never their full input or output; <paramref name="includeTools"/> = false
        /// leaves them out.
        /// </summary>
        public static string ToMarkdown(SessionTranscript transcript, string title, DateTime exportedLocal, bool includeTools = true) =>
            ToMarkdown(transcript, title, exportedLocal, new ExportOptions { Tools = includeTools ? ExportTools.Lines : ExportTools.None });

        /// <summary>
        /// As above, with <paramref name="options"/> (Phase 110): each tool's output folded under its line
        /// (<c>&lt;details&gt;</c> around a code block, which GitHub and the web page both fold), or no tools; from a
        /// chosen prompt on, saying how many earlier items are left out.
        /// </summary>
        public static string ToMarkdown(SessionTranscript transcript, string title, DateTime exportedLocal, ExportOptions options)
        {
            options = options ?? new ExportOptions();
            var sb = new StringBuilder();
            sb.Append("# ").Append(OneLine(string.IsNullOrWhiteSpace(title) ? "Claude Code conversation" : title)).Append("\n\n");
            sb.Append("*Exported from Claude Code in Visual Studio on ").Append(exportedLocal.ToString("yyyy-MM-dd HH:mm")).Append("*\n");
            int start = options.FromUuid != null || options.FromText != null ? StartIndex(transcript, options.FromUuid, options.FromText) : 0;
            int skipped = transcript.Skipped + start;
            if (start > 0) sb.Append("\n*From a chosen prompt on; ").Append(skipped).Append(" earlier items are not included.*\n");
            else if (skipped > 0) sb.Append("\n*").Append(skipped).Append(" earlier items are not included.*\n");

            bool claudeOpen = false; // inside a "## Claude" section, so the next answer or tool row continues it
            bool inToolList = false;
            foreach (var item in transcript.Items.Skip(start))
            {
                if (item.Kind == TranscriptItemKind.Tool && options.Tools == ExportTools.None) continue;
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
                        if (options.Tools == ExportTools.WithOutput && Output(item.ResultText) is string output)
                        {
                            sb.Append(output);
                            inToolList = false; // the next tool line starts a list after the folded output
                        }
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

        private const string OutputSummary = "⎿ Output";

        /// <summary>
        /// A tool's output folded (Phase 110): a <c>&lt;details&gt;</c> with its line count, the text without terminal
        /// codes in a fence longer than any run of backticks in it. Null for no output. A <c>details</c> tag in the
        /// output gets a zero-width space, so it can't close the fold early.
        /// </summary>
        private static string Output(string resultText)
        {
            if (string.IsNullOrWhiteSpace(resultText)) return null;
            string text = Cli.TerminalText.ForCopy(resultText).Replace("\r\n", "\n").Trim('\n');
            text = System.Text.RegularExpressions.Regex.Replace(text, "<(/?)details", "<$1" + (char)0x200B + "details", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            int lines = text.Split('\n').Length;
            int longest = System.Text.RegularExpressions.Regex.Matches(text, "`+").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Length).DefaultIfEmpty(0).Max();
            string fence = new string('`', Math.Max(3, longest + 1));
            return "\n<details>\n<summary>" + OutputSummary + " · " + lines + (lines == 1 ? " line" : " lines") + "</summary>\n\n" +
                   fence + "\n" + text + "\n" + fence + "\n\n</details>\n";
        }

        private const string PageStyle =
            "body{font-family:'Segoe UI',system-ui,sans-serif;max-width:52em;margin:2em auto;padding:0 1em;line-height:1.5;color:#1f2328}" +
            "h1{font-size:1.6em}h2{font-size:1.2em;border-bottom:1px solid #d0d7de;padding-bottom:.2em;margin-top:1.6em}" +
            "code{font-family:Consolas,'Cascadia Mono',monospace;background:#f6f8fa;padding:0 .2em;border-radius:3px}" +
            "pre code{padding:0;background:none}blockquote{margin:0;padding:0 1em;border-left:3px solid #d0d7de;color:#57606a}" +
            "table{border-collapse:collapse}th,td{border:1px solid #d0d7de;padding:.2em .6em}th{background:#f6f8fa}" +
            "li.tool{color:#6e7781;font-size:.9em;list-style:none}" +
            "details.tool{margin:-.6em 0 .6em 2.5em;font-size:.85em}details.tool summary{color:#6e7781;cursor:pointer}details.tool pre{max-height:30em;overflow:auto}kbd{border:1px solid #d0d7de;border-bottom-width:2px;border-radius:3px;padding:0 .3em;font-size:.9em}";

        /// <summary>
        /// The conversation as a web page (Phase 109): the Markdown of <see cref="ToMarkdown"/> as HTML, styled for
        /// reading, the tool lines muted. One file, nothing loaded from elsewhere.
        /// </summary>
        public static string ToHtml(SessionTranscript transcript, string title, DateTime exportedLocal, bool includeTools = true) =>
            ToHtml(transcript, title, exportedLocal, new ExportOptions { Tools = includeTools ? ExportTools.Lines : ExportTools.None });

        /// <summary>As above, with <paramref name="options"/> (Phase 110); a tool's folded output is muted like its line.</summary>
        public static string ToHtml(SessionTranscript transcript, string title, DateTime exportedLocal, ExportOptions options)
        {
            string body = Markdown.MarkdownHtml.Render(ToMarkdown(transcript, title, exportedLocal, options))
                .Replace("<li>🔧 ", "<li class=\"tool\">🔧 ")
                .Replace("<details><summary>" + OutputSummary, "<details class=\"tool\"><summary>" + OutputSummary);
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
