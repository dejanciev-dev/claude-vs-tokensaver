using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    /// <summary>
    /// Turns one stdout line of <c>claude --output-format stream-json --verbose --include-partial-messages</c>
    /// into zero or more <see cref="ClaudeEvent"/>s. Stateful (one instance per session).
    ///
    /// The stream reports text twice: token deltas in <c>stream_event</c> and the full message in
    /// <c>assistant</c>. We show deltas and only fall back to the full text when no delta arrived
    /// for that message.
    /// </summary>
    public sealed class ClaudeStreamParser
    {
        private static readonly IReadOnlyList<ClaudeEvent> None = Array.Empty<ClaudeEvent>();
        private readonly HashSet<string> _streamedMessageIds = new HashSet<string>();
        private string _currentStreamMessageId;
        private TokenUsage _lastMainUsage;
        private string _model;

        public string SessionId { get; private set; }

        public IReadOnlyList<ClaudeEvent> Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return None;
            JObject json;
            try { json = JObject.Parse(line); }
            catch (JsonException)
            {
                // Non-JSON output (warnings, npm noise). Surface it as text so nothing is lost silently.
                return new[] { new ClaudeEvent { Kind = ClaudeEventKind.Error, Text = line.Trim() } };
            }

            switch ((string)json["type"])
            {
                case "system": return ParseSystem(json);
                case "stream_event":
                    var parent = json["parent_tool_use_id"];
                    return ParseStreamEvent(json["event"] as JObject, isSubagent: parent != null && parent.Type != JTokenType.Null);
                case "assistant": return ParseAssistant(json);
                case "user": return ParseUser(json);
                case "result": return ParseResult(json);
                case "control_request": return ParseControlRequest(json);
                case "rate_limit_event":
                    return json["rate_limit_info"] is JObject limits ? new[] { new ClaudeEvent { Kind = ClaudeEventKind.RateLimit, Payload = limits } } : None;
                case "control_response":
                    return new[] { new ClaudeEvent { Kind = ClaudeEventKind.ControlResponse, RequestId = (string)json.SelectToken("response.request_id"), IsError = (string)json.SelectToken("response.subtype") == "error", Text = (string)json.SelectToken("response.error"), Payload = json.SelectToken("response.response") as JObject } };
                default: return None;
            }
        }

        private IReadOnlyList<ClaudeEvent> ParseSystem(JObject json)
        {
            if (AgentTaskList.IsTaskMessage(json)) return new[] { new ClaudeEvent { Kind = ClaudeEventKind.Task, Payload = json } };
            switch ((string)json["subtype"])
            {
                case "init":
                    SessionId = (string)json["session_id"] ?? SessionId;
                    _model = (string)json["model"] ?? _model;
                    _lastMainUsage = default;
                    return new[] { new ClaudeEvent { Kind = ClaudeEventKind.SessionStarted, SessionId = SessionId, Model = _model } };
                case "compact_boundary":
                    _lastMainUsage = default;
                    return new[] { new ClaudeEvent
                    {
                        Kind = ClaudeEventKind.Compacted,
                        PreCompactTokens = json.SelectToken("compact_metadata.pre_tokens")?.Value<long?>() ?? 0,
                        Text = (string)json.SelectToken("compact_metadata.trigger"),
                    } };
                default:
                    return None;
            }
        }

        private IReadOnlyList<ClaudeEvent> ParseStreamEvent(JObject ev, bool isSubagent)
        {
            if (ev == null) return None;
            string type = (string)ev["type"];
            if (type == "message_start")
            {
                _currentStreamMessageId = (string)ev.SelectToken("message.id");
                return None;
            }
            if (type == "content_block_start")
            {
                if (isSubagent) return None;
                switch ((string)ev.SelectToken("content_block.type"))
                {
                    case "thinking":
                    case "redacted_thinking":
                        return new[] { new ClaudeEvent { Kind = ClaudeEventKind.Activity, Activity = ClaudeActivity.Thinking } };
                    case "text":
                        return new[] { new ClaudeEvent { Kind = ClaudeEventKind.Activity, Activity = ClaudeActivity.Writing } };
                    case "tool_use":
                    case "server_tool_use":
                        return new[] { new ClaudeEvent { Kind = ClaudeEventKind.Activity, Activity = ClaudeActivity.Tool, ToolName = (string)ev.SelectToken("content_block.name") } };
                    default: return None;
                }
            }
            if (type != "content_block_delta") return None;
            string deltaType = (string)ev.SelectToken("delta.type");
            if (deltaType == "thinking_delta")
            {
                // 2.1.318 sends no thinking text ("thinking_display": "updates"), only an estimate, sometimes null.
                int? estimate = ev.SelectToken("delta.estimated_tokens")?.Type == JTokenType.Integer ? ev.SelectToken("delta.estimated_tokens").Value<int>() : (int?)null;
                if (isSubagent || estimate == null) return None;
                return new[] { new ClaudeEvent { Kind = ClaudeEventKind.Activity, Activity = ClaudeActivity.Thinking, EstimatedTokens = estimate } };
            }
            if (deltaType != "text_delta") return None;

            string text = (string)ev.SelectToken("delta.text");
            if (string.IsNullOrEmpty(text)) return None;
            if (_currentStreamMessageId != null) _streamedMessageIds.Add(_currentStreamMessageId);
            return new[] { new ClaudeEvent { Kind = ClaudeEventKind.TextDelta, Text = text } };
        }

        private IReadOnlyList<ClaudeEvent> ParseAssistant(JObject json)
        {
            var msg = json["message"] as JObject;
            if (msg == null) return None;
            var events = new List<ClaudeEvent>();
            string msgId = (string)msg["id"];
            bool isSub = json["parent_tool_use_id"] != null && json["parent_tool_use_id"].Type != JTokenType.Null;

            if (msg["content"] is JArray content)
            {
                foreach (var block in content.OfType<JObject>())
                {
                    switch ((string)block["type"])
                    {
                        case "text":
                            if (!isSub && (msgId == null || !_streamedMessageIds.Contains(msgId)))
                            {
                                string t = (string)block["text"];
                                if (!string.IsNullOrEmpty(t)) events.Add(new ClaudeEvent { Kind = ClaudeEventKind.AssistantText, Text = t });
                            }
                            break;
                        case "tool_use":
                            events.Add(new ClaudeEvent
                            {
                                Kind = ClaudeEventKind.ToolUse,
                                ToolName = (string)block["name"],
                                ToolUseId = (string)block["id"],
                                ToolInput = block["input"] as JObject,
                                IsSubagent = isSub,
                                // A subagent's own tool calls are nested under its Agent call.
                                Text = (isSub ? SubagentToolPrefix : "") + DescribeTool((string)block["name"], block["input"] as JObject),
                            });
                            break;
                    }
                }
            }

            var usage = TokenUsage.From(msg["usage"]);
            if (!usage.IsEmpty)
            {
                if (!isSub) _lastMainUsage = usage;
                events.Add(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Usage = usage, IsSubagent = isSub, Model = (string)msg["model"] });
            }
            return events;
        }

        private static IReadOnlyList<ClaudeEvent> ParseUser(JObject json)
        {
            if (!(json.SelectToken("message.content") is JArray content)) return None;
            var events = new List<ClaudeEvent>();
            foreach (var block in content.OfType<JObject>().Where(b => (string)b["type"] == "tool_result"))
            {
                var c = block["content"];
                int chars = c == null ? 0 : c.Type == JTokenType.String ? ((string)c).Length : c.ToString(Formatting.None).Length;
                events.Add(new ClaudeEvent
                {
                    Kind = ClaudeEventKind.ToolResult,
                    ToolUseId = (string)block["tool_use_id"],
                    ResultChars = chars,
                    IsError = block.Value<bool?>("is_error") ?? false,
                });
            }
            return events;
        }

        private IReadOnlyList<ClaudeEvent> ParseResult(JObject json)
        {
            SessionId = (string)json["session_id"] ?? SessionId;

            // Context fill = the LAST request of the turn. The top-level usage sums every request.
            TokenUsage last = default;
            if (json.SelectToken("usage.iterations") is JArray it && it.Count > 0) last = TokenUsage.From(it.Last);
            if (last.IsEmpty) last = _lastMainUsage;

            long? window = null;
            if (json["modelUsage"] is JObject mu && mu.Count > 0)
            {
                JToken entry = (_model != null ? mu[_model] : null) ?? mu.Properties().Select(p => p.Value).OrderByDescending(v => v.Value<long?>("contextWindow") ?? 0).First();
                window = entry?.Value<long?>("contextWindow");
            }

            bool isError = json.Value<bool?>("is_error") ?? ((string)json["subtype"] != "success");
            return new[] { new ClaudeEvent
            {
                Kind = ClaudeEventKind.TurnCompleted,
                SessionId = SessionId,
                Text = (string)json["result"],
                IsError = isError,
                Usage = TokenUsage.From(json["usage"]),
                LastRequestUsage = last,
                TotalCostUsd = json.Value<double?>("total_cost_usd"),
                DurationMs = json.Value<long?>("duration_ms") ?? 0,
                NumTurns = json.Value<int?>("num_turns") ?? 0,
                ContextWindow = window,
            } };
        }

        private static IReadOnlyList<ClaudeEvent> ParseControlRequest(JObject json)
        {
            var req = json["request"] as JObject;
            if (req != null && (string)req["subtype"] == "mcp_message")
                return new[] { new ClaudeEvent { Kind = ClaudeEventKind.McpMessage, RequestId = (string)json["request_id"], ServerName = (string)req["server_name"], Payload = req["message"] as JObject } };
            if (req != null && (string)req["subtype"] == "hook_callback")
                return new[] { new ClaudeEvent { Kind = ClaudeEventKind.HookCallback, RequestId = (string)json["request_id"], Payload = req } };
            if (req == null || (string)req["subtype"] != "can_use_tool") return None;
            return new[] { new ClaudeEvent
            {
                Kind = ClaudeEventKind.PermissionRequest,
                RequestId = (string)json["request_id"],
                ToolName = (string)req["tool_name"],
                ToolUseId = (string)req["tool_use_id"],
                ToolInput = req["input"] as JObject,
                Text = DescribeTool((string)req["tool_name"], req["input"] as JObject),
            } };
        }

        public const string SubagentToolPrefix = "↳ ";

        /// <summary>One short line per tool call for the transcript (never the whole payload).</summary>
        public static string DescribeTool(string name, JObject input)
        {
            string friendly = Context.IdeMcpServer.DisplayName(name);
            if (friendly != null)
            {
                string scope = (string)input?["file"];
                return string.IsNullOrWhiteSpace(scope) ? friendly : $"{friendly}: {scope.Trim()}";
            }
            if (input == null) return name;
            string Arg(string k) => (string)input[k];
            string detail = Arg("file_path") ?? Arg("command") ?? Arg("pattern") ?? Arg("url") ?? Arg("description") ?? Arg("query");
            if (detail == null) return name;
            detail = detail.Replace('\n', ' ');
            if (detail.Length > 120) detail = detail.Substring(0, 117) + "...";
            return $"{name}: {detail}";
        }
    }
}
