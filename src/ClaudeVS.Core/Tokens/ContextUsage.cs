using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Tokens
{
    public sealed class ContextCategory
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public long Tokens { get; set; }
        /// <summary>"used", "deferred" (loaded on demand, not in the window yet), "buffer" (kept free for autocompact), "free".</summary>
        public string Kind { get; set; }
        public bool IsUsed => Kind == "used";
    }

    /// <summary>
    /// What fills the context window, from the CLI's <c>get_context_usage</c> control request (format verified with
    /// Claude Code 2.1.317; unknown fields are ignored and missing ones read as 0, so a format change degrades quietly).
    /// </summary>
    public sealed class ContextUsage
    {
        public List<ContextCategory> Categories { get; } = new List<ContextCategory>();
        /// <summary>Tokens in the window now (the "used" categories).</summary>
        public long TotalTokens { get; set; }
        public long MaxTokens { get; set; }
        public double Percentage { get; set; }
        public string Model { get; set; }
        public long AutoCompactThreshold { get; set; }
        public bool IsAutoCompactEnabled { get; set; }

        // Inside "Messages":
        public long ToolCallTokens { get; set; }
        public long ToolResultTokens { get; set; }
        public long AttachmentTokens { get; set; }
        public long AssistantMessageTokens { get; set; }
        public long UserMessageTokens { get; set; }

        public long MemoryFileTokens { get; set; }
        public int MemoryFileCount { get; set; }
        /// <summary>MCP tools whose definitions are in the window, sent with every request.</summary>
        public long McpToolTokens { get; set; }
        public int McpToolCount { get; set; }
        /// <summary>
        /// MCP tools that load on demand (<c>isLoaded: false</c>, Claude Code 2.1.319+; the "MCP tools (deferred)"
        /// category): their full definitions are not in the window until Claude calls one, so they do not weigh on
        /// every request. <see cref="McpDeferredToolTokens"/> is what they would take once loaded.
        /// </summary>
        public long McpDeferredToolTokens { get; set; }
        public int McpDeferredToolCount { get; set; }

        public static ContextUsage Parse(JObject r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            var u = new ContextUsage
            {
                TotalTokens = Long(r["totalTokens"]),
                MaxTokens = Long(r["maxTokens"]),
                Percentage = r["percentage"]?.Type == JTokenType.Integer || r["percentage"]?.Type == JTokenType.Float ? (double)r["percentage"] : 0,
                Model = (string)r["model"],
                AutoCompactThreshold = Long(r["autoCompactThreshold"]),
                IsAutoCompactEnabled = r["isAutoCompactEnabled"]?.Type == JTokenType.Boolean && (bool)r["isAutoCompactEnabled"],
            };
            foreach (var c in (r["categories"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                u.Categories.Add(new ContextCategory { Id = (string)c["id"], Name = (string)c["name"] ?? (string)c["id"], Tokens = Long(c["tokens"]), Kind = (string)c["kind"] ?? "used" });
            if (u.TotalTokens == 0) u.TotalTokens = u.Categories.Where(c => c.IsUsed).Sum(c => c.Tokens);
            if (u.Percentage == 0 && u.MaxTokens > 0) u.Percentage = 100.0 * u.TotalTokens / u.MaxTokens;

            if (r["messageBreakdown"] is JObject m)
            {
                u.ToolCallTokens = Long(m["toolCallTokens"]);
                u.ToolResultTokens = Long(m["toolResultTokens"]);
                u.AttachmentTokens = Long(m["attachmentTokens"]);
                u.AssistantMessageTokens = Long(m["assistantMessageTokens"]);
                u.UserMessageTokens = Long(m["userMessageTokens"]);
            }
            var memory = (r["memoryFiles"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            u.MemoryFileCount = memory.Count;
            u.MemoryFileTokens = memory.Sum(f => Long(f["tokens"]));
            var mcp = (r["mcpTools"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            bool Deferred(JObject t) => t["isLoaded"]?.Type == JTokenType.Boolean && !(bool)t["isLoaded"]; // missing (2.1.317): loaded
            var loaded = mcp.Where(t => !Deferred(t)).ToList();
            var deferred = mcp.Where(Deferred).ToList();
            u.McpToolCount = loaded.Count;
            u.McpToolTokens = loaded.Sum(t => Long(t["tokens"]));
            u.McpDeferredToolCount = deferred.Count;
            u.McpDeferredToolTokens = deferred.Sum(t => Long(t["tokens"]));
            return u;
        }

        /// <summary>Share of the window, in percent (0 when the window size is unknown).</summary>
        public double PercentOfWindow(long tokens) => MaxTokens > 0 ? 100.0 * tokens / MaxTokens : 0;

        /// <summary>The "Messages" part split up, largest first, leaving out empty parts.</summary>
        public IEnumerable<KeyValuePair<string, long>> MessageParts() =>
            new[]
            {
                new KeyValuePair<string, long>("Tool results", ToolResultTokens),
                new KeyValuePair<string, long>("Tool calls", ToolCallTokens),
                new KeyValuePair<string, long>("Claude's messages", AssistantMessageTokens),
                new KeyValuePair<string, long>("Your messages", UserMessageTokens),
                new KeyValuePair<string, long>("Attachments", AttachmentTokens),
            }.Where(p => p.Value > 0).OrderByDescending(p => p.Value);

        /// <summary>
        /// Token-saving hints from the breakdown: large tool results (compact or start fresh), heavy memory files
        /// (paid on every request), many MCP tools. Thresholds are deliberately high so the hints stay rare.
        /// </summary>
        public IEnumerable<string> Tips()
        {
            long messages = ToolCallTokens + ToolResultTokens + AttachmentTokens + AssistantMessageTokens + UserMessageTokens;
            if (ToolResultTokens >= 20_000 && ToolResultTokens * 2 >= messages)
                yield return $"Tool results are {TokenMeter.Format(ToolResultTokens)} of the conversation. /compact or a new session drops them.";
            if (MemoryFileTokens >= 5_000)
                yield return $"CLAUDE.md and memory files take {TokenMeter.Format(MemoryFileTokens)} on every request. Keep them short.";
            if (McpToolTokens >= 10_000)
                yield return $"{McpToolCount} MCP tools take {TokenMeter.Format(McpToolTokens)}. Turn off servers you don't use here.";
            if (IsAutoCompactEnabled && AutoCompactThreshold > 0 && TotalTokens >= AutoCompactThreshold * 0.8)
                yield return $"Autocompact starts at {TokenMeter.Format(AutoCompactThreshold)}; /compact now keeps you in control of the summary.";
        }

        private static long Long(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? (long)(double)t : 0;
    }
}
