using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    /// <summary>Token counts as reported by the API (one request, or a sum of requests).</summary>
    public struct TokenUsage
    {
        public long Input;
        public long Output;
        public long CacheRead;
        public long CacheCreation;

        /// <summary>Tokens the model had to read for this request = how full the context window is.</summary>
        public long ContextTokens => Input + CacheRead + CacheCreation;
        public long Total => Input + Output + CacheRead + CacheCreation;
        public bool IsEmpty => Total == 0;

        public static TokenUsage From(JToken u)
        {
            if (u == null || u.Type != JTokenType.Object) return default;
            return new TokenUsage
            {
                Input = u.Value<long?>("input_tokens") ?? 0,
                Output = u.Value<long?>("output_tokens") ?? 0,
                CacheRead = u.Value<long?>("cache_read_input_tokens") ?? 0,
                CacheCreation = u.Value<long?>("cache_creation_input_tokens") ?? 0,
            };
        }

        public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new TokenUsage
        {
            Input = a.Input + b.Input,
            Output = a.Output + b.Output,
            CacheRead = a.CacheRead + b.CacheRead,
            CacheCreation = a.CacheCreation + b.CacheCreation,
        };
    }

    public enum ClaudeEventKind
    {
        SessionStarted,
        TextDelta,
        AssistantText,
        AssistantUsage,
        ToolUse,
        ToolResult,
        PermissionRequest,
        Compacted,
        TurnCompleted,
        ControlResponse,
        /// <summary>Plan usage limits (rate_limit_event); the data is in Payload.</summary>
        RateLimit,
        Error,
    }

    public class ClaudeEvent
    {
        public ClaudeEventKind Kind { get; set; }
        public string Text { get; set; }
        public string SessionId { get; set; }
        public string Model { get; set; }

        // ToolUse / ToolResult / PermissionRequest
        public string ToolName { get; set; }
        public string ToolUseId { get; set; }
        public JObject ToolInput { get; set; }
        public int ResultChars { get; set; }
        public bool IsError { get; set; }
        public string RequestId { get; set; }
        /// <summary>ControlResponse: the CLI's answer to our control request (e.g. the get_context_usage data).</summary>
        public JObject Payload { get; set; }

        // Usage
        public TokenUsage Usage { get; set; }
        /// <summary>True when the usage belongs to a subagent (parent_tool_use_id set).</summary>
        public bool IsSubagent { get; set; }

        // TurnCompleted
        /// <summary>Usage of the last request in the turn — describes current context fill.</summary>
        public TokenUsage LastRequestUsage { get; set; }
        public double? TotalCostUsd { get; set; }
        public long DurationMs { get; set; }
        public int NumTurns { get; set; }
        public long? ContextWindow { get; set; }

        // Compacted
        public long PreCompactTokens { get; set; }

        public override string ToString() => $"{Kind}: {Text ?? ToolName}";
    }
}
