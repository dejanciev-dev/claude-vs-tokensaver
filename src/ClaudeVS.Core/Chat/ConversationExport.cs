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
        /// <summary>What the conversation cost (<see cref="SessionCost.Read"/>), for the line under the title; null leaves it out (Phase 112).</summary>
        public SessionCostInfo Cost { get; set; }
        /// <summary>"Haiku 5.5" for a model ID, as Claude Code names it; null falls back to <see cref="ConversationExport.ModelName"/>.</summary>
        public Func<string, string> ModelName { get; set; }
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
            if (Summary(transcript, options, whole: start == 0) is string summary) sb.Append('\n').Append(summary).Append('\n');
            if (start > 0) sb.Append("\n*From a chosen prompt on; ").Append(skipped).Append(" earlier items are not included.*\n");
            else if (skipped > 0) sb.Append("\n*").Append(skipped).Append(" earlier items are not included.*\n");

            int header = sb.Length; // where the contents go (Phase 112)
            var prompts = new System.Collections.Generic.List<string>();
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
                        prompts.Add(item.Text);
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
            if (Contents(sb.ToString(), prompts) is string contents) sb.Insert(header, contents);
            return sb.ToString();
        }

        /// <summary>The line the contents start with, which the page also looks for (Phase 112).</summary>
        private const string ContentsTitle = "**Prompts**";

        /// <summary>
        /// A numbered list of the prompts (Phase 112), each its first line linking to its "## You" section, for three
        /// prompts or more. The anchors come from parsing the document as it is, so they are the ones the page and
        /// GitHub give those headings ("you", "you-1", …). Null when there are fewer prompts, or when a prompt holds a
        /// "You" heading of its own and the sections can't be told apart.
        /// </summary>
        private static string Contents(string document, System.Collections.Generic.List<string> prompts)
        {
            if (prompts.Count < 3) return null;
            var anchors = Markdown.MarkdownParser.Parse(document)
                .Where(b => b.Kind == Markdown.MdBlockKind.Heading && b.Level == 2 && Markdown.AnswerText.Inline(b.Inlines) == "You")
                .Select(b => b.Anchor).ToList();
            if (anchors.Count != prompts.Count) return null;
            var sb = new StringBuilder("\n").Append(ContentsTitle).Append("\n\n");
            for (int i = 0; i < prompts.Count; i++)
            {
                string first = OneLine((prompts[i] ?? "").Trim().Split('\n')[0]);
                // Marks that would end the link or change its text go; a long line is cut
                first = new string(first.Where(c => "[]`*_<>|\\".IndexOf(c) < 0).ToArray()).Trim();
                if (first.Length > 80) first = first.Substring(0, 79).TrimEnd() + "…";
                if (first.Length == 0) first = "(an image or an empty prompt)";
                sb.Append(i + 1).Append(". [").Append(first).Append("](#").Append(anchors[i]).Append(")\n");
            }
            return sb.ToString();
        }

        /// <summary>
        /// The line under the title (Phase 112): "*12 prompts · $0.42 ($0.10 agents) · 1.2M tokens · Sonnet 5.5, Haiku 5.5 ·
        /// 2026-11-27 09:00–11:30*", from the session file's cost and the prompts' times; the parts it has. When the
        /// export starts at a chosen prompt, the cost is the whole conversation's and says so. Null with nothing to say.
        /// </summary>
        private static string Summary(SessionTranscript transcript, ExportOptions options, bool whole)
        {
            var parts = new System.Collections.Generic.List<string>();
            int prompts = transcript.Items.Count(i => i.Kind == TranscriptItemKind.User);
            if (prompts > 0) parts.Add(prompts == 1 ? "1 prompt" : prompts + " prompts");
            var cost = options.Cost;
            if (cost != null && cost.Requests > 0)
            {
                string usd = Usd(cost.CostUsd) + (cost.AgentCostUsd >= 0.005 ? " (" + Usd(cost.AgentCostUsd) + " agents)" : "");
                parts.Add(whole ? usd : "the whole conversation " + usd);
                parts.Add(TokenMeter.Format(cost.Usage.Total) + " tokens");
                var names = cost.Models.Select(m => options.ModelName?.Invoke(m) ?? ModelName(m)).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
                if (names.Count > 0) parts.Add(string.Join(", ", names));
            }
            var times = transcript.Items.Where(i => i.TimestampUtc.HasValue).Select(i => i.TimestampUtc.Value.ToLocalTime()).ToList();
            if (times.Count > 0)
            {
                DateTime first = times.Min(), last = times.Max();
                string span = first.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                if (last - first >= TimeSpan.FromMinutes(1))
                    span += "–" + last.ToString(last.Date == first.Date ? "HH:mm" : "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                parts.Add(span);
            }
            return parts.Count == 0 ? null : "*" + string.Join(" · ", parts) + "*";
        }

        // Under a cent with four decimals: a short conversation on Haiku costs $0.0032, not "$0.00" (Phase 114)
        private static string Usd(double v) => "$" + v.ToString(v >= 100 ? "0" : v >= 0.01 || v == 0 ? "0.00" : "0.0000", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// A model ID as a short name when the catalog doesn't know it (Phase 112): "claude-haiku-5-5" → "Haiku 5.5",
        /// "claude-sonnet-4-5-20250929" → "Sonnet 4.5"; anything else as it is.
        /// </summary>
        public static string ModelName(string model)
        {
            if (string.IsNullOrEmpty(model)) return model;
            var m = System.Text.RegularExpressions.Regex.Match(model, @"^claude-(?<f>[a-z]+)-(?<v>\d{1,2}(?:-\d{1,2})?)(?:-\d{8})?(?<rest>\[.*\])?$");
            if (!m.Success) return model;
            string family = m.Groups["f"].Value;
            return char.ToUpperInvariant(family[0]) + family.Substring(1) + " " + m.Groups["v"].Value.Replace('-', '.');
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
            // Claude Code's own last line after a command (2.1.332), not the command's output (Phase 114)
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\n?Shell cwd was reset to [^\n]*$", "").Trim('\n');
            if (text.Length == 0) return null;
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
            "details.tool{margin:-.6em 0 .6em 2.5em;font-size:.85em}details.tool summary{color:#6e7781;cursor:pointer}details.tool pre{max-height:30em;overflow:auto}" +
            "nav.contents{background:#f6f8fa;border-radius:6px;padding:.4em 1em;margin:1em 0}nav.contents ol{margin:.3em 0}" +
            // Dark when the system is (Phase 112). Code blocks keep their light background (an inline style, so a paste
            // keeps it), so their plain text keeps a dark color
            "@media (prefers-color-scheme:dark){body{background:#0d1117;color:#e6edf3}a{color:#4493f8}h2{border-color:#30363d}" +
            "code{background:#262c36}pre{color:#1f2328}pre code{background:none}blockquote{border-color:#30363d;color:#9198a1}" +
            "th,td{border-color:#30363d}th{background:#161b22}li.tool,details.tool summary{color:#9198a1}nav.contents{background:#161b22}kbd{border-color:#30363d}}" +
            // Printed (Phase 112): light, no contents or tool output, long code lines wrapped, code and headings kept together
            "@media print{body{background:#fff;color:#1f2328;max-width:none;margin:0}nav.contents,details.tool{display:none}" +
            "pre{white-space:pre-wrap!important;break-inside:avoid;color:#1f2328}h2{break-after:avoid}a{color:inherit;text-decoration:none}}" +
            "kbd{border:1px solid #d0d7de;border-bottom-width:2px;border-radius:3px;padding:0 .3em;font-size:.9em}";

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
            // The contents in a nav of their own (Phase 112), which a print leaves out
            const string contentsStart = "<p><strong>Prompts</strong></p>\n<ol>";
            int c = body.IndexOf(contentsStart, StringComparison.Ordinal);
            int end = c < 0 ? -1 : body.IndexOf("</ol>", c, StringComparison.Ordinal);
            if (end > 0) body = body.Substring(0, c) + "<nav class=\"contents\">" + body.Substring(c, end + 5 - c) + "</nav>" + body.Substring(end + 5);
            return Page(string.IsNullOrWhiteSpace(title) ? "Claude Code conversation" : title, body);
        }

        /// <summary>An HTML body in the export's page: its title, the stylesheet, nothing loaded from elsewhere.</summary>
        private static string Page(string title, string body) =>
            "<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<meta name=\"color-scheme\" content=\"light dark\">\n" +
            "<title>" + Markdown.MarkdownHtml.E(OneLine(title)) + "</title>\n<style>" + PageStyle + "</style>\n</head>\n<body>\n" + body + "</body>\n</html>\n";

        /// <summary>
        /// A name for one answer (Phase 112): its first heading, else its first line of text, without Markdown's marks,
        /// at most 60 characters; "Claude's answer" when it has neither.
        /// </summary>
        public static string AnswerTitle(string markdown)
        {
            var blocks = Markdown.MarkdownParser.Parse(markdown ?? "");
            var pick = blocks.FirstOrDefault(b => b.Kind == Markdown.MdBlockKind.Heading)
                    ?? blocks.FirstOrDefault(b => (b.Kind == Markdown.MdBlockKind.Paragraph || b.Kind == Markdown.MdBlockKind.ListItem) && b.Inlines.Count > 0);
            string text = pick == null ? "" : OneLine(Markdown.AnswerText.Inline(pick.Inlines)).TrimEnd(':', '.', ' ');
            if (text.Length > 60) text = text.Substring(0, 59).TrimEnd() + "…";
            return text.Length == 0 ? "Claude's answer" : text;
        }

        /// <summary>
        /// One answer as a page of its own (Phase 112, *Save as…* on its copy button): the answer as the chat draws it,
        /// under a line saying when it was saved, in the export's style.
        /// </summary>
        public static string AnswerPage(string markdown, DateTime savedLocal) =>
            Page(AnswerTitle(markdown),
                 "<p><em>Claude's answer, saved from Claude Code in Visual Studio on " + savedLocal.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "</em></p>\n" +
                 Markdown.MarkdownHtml.Render(markdown ?? ""));

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
