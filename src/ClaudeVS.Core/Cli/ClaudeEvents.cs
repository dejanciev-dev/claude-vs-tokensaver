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
        /// <summary>
        /// A JSON-RPC message for one of our in-process MCP servers (control_request <c>mcp_message</c>). The
        /// session answers it itself; <see cref="ClaudeEvent.ServerName"/>, RequestId, Payload = the message.
        /// </summary>
        McpMessage,
        /// <summary>One of our hooks ran (control_request <c>hook_callback</c>); the session answers it itself. Payload = the request.</summary>
        HookCallback,
        /// <summary>A subagent or background command started, progressed or ended; the system message is in Payload (see <see cref="AgentTaskList"/>).</summary>
        Task,
        /// <summary>
        /// What the main agent is doing right now, from the stream's content blocks: <see cref="ClaudeEvent.Activity"/>,
        /// with ToolName for a tool call being written and <see cref="ClaudeEvent.EstimatedTokens"/> while thinking.
        /// Subagents' blocks are not reported.
        /// </summary>
        Activity,
        /// <summary>
        /// How long the main agent thought for one thinking block, in <see cref="ClaudeEvent.DurationMs"/>: the CLI's
        /// <c>thinking_duration_ms</c> on the block's assistant message (2.1.319; older versions do not send it).
        /// </summary>
        ThinkingDuration,
        Error,
    }

    /// <summary>The phase of the main agent's current response (<see cref="ClaudeEventKind.Activity"/>).</summary>
    public enum ClaudeActivity
    {
        /// <summary>A thinking block. The CLI streams no thinking text, only an estimate of its size.</summary>
        Thinking,
        /// <summary>A text block: the answer is being written.</summary>
        Writing,
        /// <summary>A tool_use block: Claude is writing the input of a tool call (ToolName).</summary>
        Tool,
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
        /// <summary>ToolResult: the result's text, cut at <see cref="ClaudeStreamParser.MaxResultText"/> characters.</summary>
        public string ResultText { get; set; }
        /// <summary>
        /// ToolResult of an Edit / Write: the file before the change, as the CLI read it (<c>tool_use_result.originalFile</c>,
        /// 2.1.319); "" when the Write created the file; null when the CLI did not say.
        /// </summary>
        public string OriginalFile { get; set; }
        /// <summary>
        /// ToolResult: the tool's structured output (<c>tool_use_result</c>, 2.1.319), e.g. Read's
        /// <c>file.numLines/startLine/totalLines</c>, Glob's and Grep's <c>numFiles</c>; null when not an object.
        /// </summary>
        public JObject ResultData { get; set; }
        /// <summary>
        /// ToolResult: why the call ran without asking, when Claude Code's settings allowed it
        /// (<c>tool_result_meta[].permission_decision.reason_type</c>, 2.1.319): "mode", "rule", "subcommandResults"…
        /// Null when you were asked, or the CLI did not say.
        /// </summary>
        public string AllowedBy { get; set; }
        public bool IsError { get; set; }
        public string RequestId { get; set; }
        /// <summary>ControlResponse: the CLI's answer to our control request (e.g. the get_context_usage data).</summary>
        public JObject Payload { get; set; }
        /// <summary>McpMessage: which of our MCP servers the message is for.</summary>
        public string ServerName { get; set; }

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
        /// <summary>When the stored claude.ai sign-in ends (<c>credential_expires_at</c>, 2.1.318); null for API keys.</summary>
        public System.DateTime? CredentialExpiresUtc { get; set; }
        /// <summary>Why the turn ended (<c>terminal_reason</c>, 2.1.319): "completed", "api_error"…; null when not sent.</summary>
        public string TerminalReason { get; set; }

        // SessionStarted
        /// <summary>The CLI's <c>claude_code_version</c> from <c>init</c>, e.g. "2.1.318".</summary>
        public string CliVersion { get; set; }
        /// <summary>The session's skills (<c>skills</c> in <c>init</c>); null when not sent.</summary>
        public System.Collections.Generic.IReadOnlyList<string> Skills { get; set; }
        /// <summary>The commands you can run (<c>slash_commands</c> in <c>init</c>); null when not sent.</summary>
        public System.Collections.Generic.IReadOnlyList<string> SlashCommands { get; set; }

        // Compacted
        public long PreCompactTokens { get; set; }

        // Activity
        public ClaudeActivity Activity { get; set; }
        /// <summary>Activity (Thinking): the CLI's estimate of the thinking tokens so far, when it sends one.</summary>
        public int? EstimatedTokens { get; set; }

        public override string ToString() => $"{Kind}: {Text ?? ToolName}";
    }
}
