using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeVS.Core.Cli;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Chat
{
    public enum TranscriptItemKind { User, Answer, Tool, Compacted }

    /// <summary>One thing to redraw from an earlier conversation.</summary>
    public sealed class TranscriptItem
    {
        public TranscriptItemKind Kind { get; set; }
        /// <summary>Prompt or answer text (Markdown), or the one-line tool description.</summary>
        public string Text { get; set; }
        public string ToolUseId { get; set; }
        public string ToolName { get; set; }
        public JObject ToolInput { get; set; }
        /// <summary>Tool result size; -1 when no result was recorded (the call was interrupted).</summary>
        public int ResultChars { get; set; } = -1;
        /// <summary>Tool: the result's text (cut like the live one), for the ⎿ summary line.</summary>
        public string ResultText { get; set; }
        public bool IsError { get; set; }
        /// <summary>User: the message id in the CLI session (what rewind_files takes).</summary>
        public string Uuid { get; set; }
        /// <summary>Compacted: context size before compaction, and "auto" or "manual".</summary>
        public long PreTokens { get; set; }
        public string Trigger { get; set; }
        /// <summary>When the prompt was sent, or when the answer's last part arrived (from the session file).</summary>
        public DateTime? TimestampUtc { get; set; }
    }

    public sealed class SessionTranscript
    {
        public List<TranscriptItem> Items { get; } = new List<TranscriptItem>();
        /// <summary>Items left out at the start because the conversation is longer than the limit.</summary>
        public int Skipped { get; set; }
        /// <summary>
        /// The context the conversation had at its last answer (input + cache of that request's usage), which the next
        /// prompt sends again; 0 when unknown or right after a compaction.
        /// </summary>
        public long LastContextTokens { get; set; }

        /// <summary>
        /// Reads a CLI session file (~/.claude/projects/&lt;folder&gt;/&lt;id&gt;.jsonl) into prompts, answers and tool calls,
        /// keeping the last <paramref name="maxItems"/>. Skips side chains (subagents), meta and CLI-generated
        /// messages (command wrappers, caveats, notifications), thinking, and the compaction summary itself.
        /// Read-only; malformed lines are ignored.
        /// </summary>
        public static SessionTranscript Read(string file, int maxItems = 300)
        {
            var t = new SessionTranscript();
            var tools = new Dictionary<string, TranscriptItem>();
            var seen = new HashSet<string>();
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var o = SessionHistory.ParseLine(line);
                        if (o == null || o.Value<bool?>("isSidechain") == true) continue;
                        string uuid = (string)o["uuid"];
                        if (uuid != null && !seen.Add(uuid)) continue;
                        switch ((string)o["type"])
                        {
                            case "user": ReadUser(o, t.Items, tools); break;
                            case "assistant":
                                ReadAssistant(o, t.Items, tools);
                                var usage = Cli.TokenUsage.From(o.SelectToken("message.usage"));
                                if (usage.ContextTokens > 0) t.LastContextTokens = usage.ContextTokens;
                                break;
                            case "system":
                                if ((string)o["subtype"] == "compact_boundary") t.LastContextTokens = 0; // the next answer says the new size
                                if ((string)o["subtype"] == "compact_boundary")
                                    t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.Compacted, PreTokens = o.SelectToken("compactMetadata.preTokens")?.Value<long?>() ?? 0, Trigger = (string)o.SelectToken("compactMetadata.trigger") });
                                break;
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (t.Items.Count > maxItems)
            {
                t.Skipped = t.Items.Count - maxItems;
                t.Items.RemoveRange(0, t.Skipped);
            }
            return t;
        }

        private static void ReadUser(JObject o, List<TranscriptItem> items, Dictionary<string, TranscriptItem> tools)
        {
            if (o.Value<bool?>("isCompactSummary") == true)
            {
                // The summary Claude continues from: attach it to the compaction just before it.
                var last = items.Count > 0 ? items[items.Count - 1] : null;
                if (last?.Kind == TranscriptItemKind.Compacted && last.Text == null) last.Text = CleanCompactSummary(MessageText(o.SelectToken("message.content")));
                return;
            }
            if (o.Value<bool?>("isMeta") == true) return;
            var content = o.SelectToken("message.content");
            if (content?.Type == JTokenType.String)
            {
                AddPrompt(items, (string)content, (string)o["uuid"], Time(o));
                return;
            }
            if (!(content is JArray blocks)) return;
            foreach (var b in blocks.OfType<JObject>())
            {
                switch ((string)b["type"])
                {
                    case "text":
                        AddPrompt(items, (string)b["text"], (string)o["uuid"], Time(o));
                        break;
                    case "tool_result":
                        if ((string)b["tool_use_id"] is string id && tools.TryGetValue(id, out var tool))
                        {
                            var c = b["content"];
                            tool.ResultChars = c == null ? 0 : c.Type == JTokenType.String ? ((string)c).Length : c.ToString(Formatting.None).Length;
                            tool.IsError = b.Value<bool?>("is_error") ?? false;
                            tool.ResultText = Cli.ClaudeStreamParser.ResultText(c);
                        }
                        break;
                }
            }
        }

        private static void ReadAssistant(JObject o, List<TranscriptItem> items, Dictionary<string, TranscriptItem> tools)
        {
            if (!(o.SelectToken("message.content") is JArray blocks)) return;
            foreach (var b in blocks.OfType<JObject>())
            {
                switch ((string)b["type"])
                {
                    case "text":
                        string text = (string)b["text"];
                        if (string.IsNullOrWhiteSpace(text)) break;
                        // The CLI writes one line per content block; consecutive text belongs to one answer.
                        var last = items.Count > 0 ? items[items.Count - 1] : null;
                        if (last?.Kind == TranscriptItemKind.Answer) { last.Text += "\n\n" + text; last.TimestampUtc = Time(o) ?? last.TimestampUtc; }
                        else items.Add(new TranscriptItem { Kind = TranscriptItemKind.Answer, Text = text, TimestampUtc = Time(o) });
                        break;
                    case "tool_use":
                        var input = b["input"] as JObject;
                        var item = new TranscriptItem
                        {
                            Kind = TranscriptItemKind.Tool,
                            ToolUseId = (string)b["id"],
                            ToolName = (string)b["name"],
                            ToolInput = input,
                            Text = ClaudeStreamParser.DescribeTool((string)b["name"], input),
                        };
                        items.Add(item);
                        if (item.ToolUseId != null) tools[item.ToolUseId] = item;
                        break;
                }
            }
        }

        /// <summary>The line's "timestamp" (ISO 8601, UTC), or null.</summary>
        private static DateTime? Time(JObject o)
        {
            var t = o["timestamp"];
            if (t == null) return null;
            if (t.Type == JTokenType.Date) return ((DateTime)t).ToUniversalTime();
            return DateTime.TryParse((string)t, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTime?)null;
        }

        private static string MessageText(JToken content) =>
            content?.Type == JTokenType.String
                ? (string)content
                : string.Join("\n\n", (content as JArray)?.OfType<JObject>().Where(b => (string)b["type"] == "text").Select(b => (string)b["text"]) ?? Enumerable.Empty<string>());

        /// <summary>
        /// The compaction summary without the CLI's wrapping: the "This session is being continued…" preamble and
        /// &lt;system-reminder&gt; blocks are removed. Null when nothing is left.
        /// </summary>
        public static string CleanCompactSummary(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<system-reminder>[\s\S]*?</system-reminder>", "");
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            // The preamble is the first paragraph when it says the session is being continued.
            int first = lines.FindIndex(l => l.Trim().Length > 0);
            if (first >= 0 && lines[first].IndexOf("session is being continued", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int end = lines.FindIndex(first, l => l.Trim().Length == 0);
                lines.RemoveRange(first, (end < 0 ? lines.Count : end) - first);
            }
            string result = string.Join("\n", lines).Trim();
            return result.Length == 0 ? null : result;
        }

        /// <summary>
        /// The newest compaction summary in a session file (read from its last MB), for a compaction that just
        /// happened in a running session. Null when there is none yet.
        /// </summary>
        public static string ReadLastCompactSummary(string file)
        {
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    const int tail = 1024 * 1024;
                    if (stream.Length > tail) stream.Position = stream.Length - tail;
                    var reader = new StreamReader(stream, Encoding.UTF8);
                    if (stream.Position > 0) reader.ReadLine(); // partial line
                    string line, found = null;
                    while ((line = reader.ReadLine()) != null)
                    {
                        // Only a summary written after the newest boundary counts; an older one belongs to an earlier compaction.
                        if (line.IndexOf("\"compact_boundary\"", StringComparison.Ordinal) >= 0) found = null;
                        else if (line.IndexOf("\"isCompactSummary\":true", StringComparison.Ordinal) >= 0) found = line;
                    }
                    var o = SessionHistory.ParseLine(found);
                    return o == null ? null : CleanCompactSummary(MessageText(o.SelectToken("message.content")));
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static void AddPrompt(List<TranscriptItem> items, string text, string uuid, DateTime? time)
        {
            text = text?.Trim();
            // CLI-generated wrappers and notes, not something you typed.
            if (string.IsNullOrEmpty(text) || text.StartsWith("<") || text.StartsWith("[Request interrupted")) return;
            items.Add(new TranscriptItem { Kind = TranscriptItemKind.User, Text = text, Uuid = uuid, TimestampUtc = time });
        }
    }
}
