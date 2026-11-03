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
        public bool IsError { get; set; }
        /// <summary>User: the message id in the CLI session (what rewind_files takes).</summary>
        public string Uuid { get; set; }
    }

    public sealed class SessionTranscript
    {
        public List<TranscriptItem> Items { get; } = new List<TranscriptItem>();
        /// <summary>Items left out at the start because the conversation is longer than the limit.</summary>
        public int Skipped { get; set; }

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
                            case "assistant": ReadAssistant(o, t.Items, tools); break;
                            case "system":
                                if ((string)o["subtype"] == "compact_boundary") t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.Compacted });
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
            if (o.Value<bool?>("isMeta") == true || o.Value<bool?>("isCompactSummary") == true) return;
            var content = o.SelectToken("message.content");
            if (content?.Type == JTokenType.String)
            {
                AddPrompt(items, (string)content, (string)o["uuid"]);
                return;
            }
            if (!(content is JArray blocks)) return;
            foreach (var b in blocks.OfType<JObject>())
            {
                switch ((string)b["type"])
                {
                    case "text":
                        AddPrompt(items, (string)b["text"], (string)o["uuid"]);
                        break;
                    case "tool_result":
                        if ((string)b["tool_use_id"] is string id && tools.TryGetValue(id, out var tool))
                        {
                            var c = b["content"];
                            tool.ResultChars = c == null ? 0 : c.Type == JTokenType.String ? ((string)c).Length : c.ToString(Formatting.None).Length;
                            tool.IsError = b.Value<bool?>("is_error") ?? false;
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
                        if (last?.Kind == TranscriptItemKind.Answer) last.Text += "\n\n" + text;
                        else items.Add(new TranscriptItem { Kind = TranscriptItemKind.Answer, Text = text });
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

        private static void AddPrompt(List<TranscriptItem> items, string text, string uuid)
        {
            text = text?.Trim();
            // CLI-generated wrappers and notes, not something you typed.
            if (string.IsNullOrEmpty(text) || text.StartsWith("<") || text.StartsWith("[Request interrupted")) return;
            items.Add(new TranscriptItem { Kind = TranscriptItemKind.User, Text = text, Uuid = uuid });
        }
    }
}
