using System;
using System.Collections.Generic;
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
        /// <summary>Of <see cref="CacheCreation"/>: written with the 1-hour lifetime (<c>cache_creation.ephemeral_1h_input_tokens</c>).</summary>
        public long CacheCreation1h;
        /// <summary>Of <see cref="CacheCreation"/>: written with the 5-minute lifetime (<c>cache_creation.ephemeral_5m_input_tokens</c>).</summary>
        public long CacheCreation5m;
        /// <summary>Of <see cref="Output"/>: thinking (<c>output_tokens_details.thinking_tokens</c>, 2.1.319).</summary>
        public long Thinking;
        /// <summary>
        /// Of <see cref="Total"/>: tokens of requests answered in fast mode (<c>usage.speed == "fast"</c>), billed at
        /// <see cref="Tokens.ModelPrice.FastMultiplier"/>× every price. Counted apart because a sum can mix speeds: since
        /// Claude Code 2.1.321 a fast turn falls back to standard speed on the same model when fast serving fails.
        /// </summary>
        public long FastTokens;
        /// <summary>
        /// Of <see cref="Total"/>: tokens of requests with more than <see cref="LargeContextFrom"/> input tokens, which a
        /// model with a long-context tier bills higher (Haiku 5.5: 5×). The price depends on each request, not on a sum,
        /// so a turn's total says it only through this (set per request: <see cref="AsRequest"/>, summed by +).
        /// </summary>
        public long LargeContextTokens;

        /// <summary>Haiku 5.5's long-context tier starts above this many input tokens per request.</summary>
        public const long LargeContextFrom = 100_000;

        /// <summary>This usage as one request's: <see cref="LargeContextTokens"/> = all of it when its input passes <see cref="LargeContextFrom"/>.</summary>
        public TokenUsage AsRequest()
        {
            var t = this;
            t.LargeContextTokens = ContextTokens > LargeContextFrom ? Total : 0;
            return t;
        }

        /// <summary>Tokens the model had to read for this request = how full the context window is.</summary>
        public long ContextTokens => Input + CacheRead + CacheCreation;
        public long Total => Input + Output + CacheRead + CacheCreation;
        public bool IsEmpty => Total == 0;
        /// <summary>Some of it ran in fast mode.</summary>
        public bool AnyFast => FastTokens > 0;

        public static TokenUsage From(JToken u)
        {
            if (u == null || u.Type != JTokenType.Object) return default;
            var t = Parse(u);
            if ((string)u["speed"] == "fast") t.FastTokens = t.Total;
            return t;
        }

        private static TokenUsage Parse(JToken u)
        {
            return new TokenUsage
            {
                Input = u.Value<long?>("input_tokens") ?? 0,
                Output = u.Value<long?>("output_tokens") ?? 0,
                CacheRead = u.Value<long?>("cache_read_input_tokens") ?? 0,
                CacheCreation = u.Value<long?>("cache_creation_input_tokens") ?? 0,
                CacheCreation1h = u.SelectToken("cache_creation.ephemeral_1h_input_tokens")?.Type == JTokenType.Integer ? (long)u.SelectToken("cache_creation.ephemeral_1h_input_tokens") : 0,
                CacheCreation5m = u.SelectToken("cache_creation.ephemeral_5m_input_tokens")?.Type == JTokenType.Integer ? (long)u.SelectToken("cache_creation.ephemeral_5m_input_tokens") : 0,
                Thinking = u.SelectToken("output_tokens_details.thinking_tokens")?.Type == JTokenType.Integer ? (long)u.SelectToken("output_tokens_details.thinking_tokens") : 0,
            };
        }

        public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new TokenUsage
        {
            Input = a.Input + b.Input,
            Output = a.Output + b.Output,
            CacheRead = a.CacheRead + b.CacheRead,
            CacheCreation = a.CacheCreation + b.CacheCreation,
            CacheCreation1h = a.CacheCreation1h + b.CacheCreation1h,
            CacheCreation5m = a.CacheCreation5m + b.CacheCreation5m,
            Thinking = a.Thinking + b.Thinking,
            FastTokens = a.FastTokens + b.FastTokens,
            LargeContextTokens = a.LargeContextTokens + b.LargeContextTokens,
        };

        /// <summary>a − b per field, never below 0 (a cumulative count's growth).</summary>
        public static TokenUsage Minus(TokenUsage a, TokenUsage b) => new TokenUsage
        {
            Input = Math.Max(0, a.Input - b.Input),
            Output = Math.Max(0, a.Output - b.Output),
            CacheRead = Math.Max(0, a.CacheRead - b.CacheRead),
            CacheCreation = Math.Max(0, a.CacheCreation - b.CacheCreation),
            CacheCreation1h = Math.Max(0, a.CacheCreation1h - b.CacheCreation1h),
            CacheCreation5m = Math.Max(0, a.CacheCreation5m - b.CacheCreation5m),
            Thinking = Math.Max(0, a.Thinking - b.Thinking),
        };
    }

    /// <summary>
    /// One model's line of the result's <c>modelUsage</c>: everything the process has used on that model so far,
    /// subagents included (the result's <c>usage</c> is the main agent's turn only; seen in
    /// Fixtures/cli-2.1.319-fork.jsonl), with the CLI's own cost.
    /// </summary>
    public sealed class ModelUsageEntry
    {
        public TokenUsage Usage;
        public double CostUsd;

        /// <summary>Model id → its cumulative usage; empty when the result has no modelUsage.</summary>
        public static Dictionary<string, ModelUsageEntry> Parse(JToken modelUsage)
        {
            var d = new Dictionary<string, ModelUsageEntry>(StringComparer.Ordinal);
            if (!(modelUsage is JObject o)) return d;
            foreach (var p in o.Properties())
            {
                if (!(p.Value is JObject m)) continue;
                long L(string k) => m[k]?.Type == JTokenType.Integer || m[k]?.Type == JTokenType.Float ? (long)(double)m[k] : 0;
                d[p.Name] = new ModelUsageEntry
                {
                    Usage = new TokenUsage
                    {
                        Input = L("inputTokens"), Output = L("outputTokens"), CacheRead = L("cacheReadInputTokens"),
                        CacheCreation = L("cacheCreationInputTokens"), CacheCreation1h = L("cacheCreationEphemeral1hInputTokens"),
                        CacheCreation5m = L("cacheCreationEphemeral5mInputTokens"), Thinking = L("thinkingTokens"),
                    },
                    CostUsd = m["costUSD"]?.Type == JTokenType.Float || m["costUSD"]?.Type == JTokenType.Integer ? (double)m["costUSD"] : 0,
                };
            }
            return d;
        }
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
        /// <summary>
        /// A hook is still running (2.1.323: a heartbeat every 30 s while hooks run long in <c>-p</c>, as a
        /// <c>tool_progress</c> line with <c>heartbeat: true</c>). <see cref="ClaudeEvent.ToolName"/> = the hook event
        /// (<c>PreToolUse</c>), ToolUseId = the tool call it runs for, <see cref="ClaudeEvent.DurationMs"/> = how long so far.
        /// </summary>
        HookRunning,
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
        /// <summary>AssistantUsage: the API message id (`message.id`); the CLI repeats a message per content block.</summary>
        public string MessageId { get; set; }

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
        /// <summary>ToolResult: why Claude Code's settings blocked the call (a rejected config decision's reason_type); null otherwise.</summary>
        public string DeniedBy { get; set; }
        public bool IsError { get; set; }
        public string RequestId { get; set; }
        /// <summary>ControlResponse: the CLI's answer to our control request (e.g. the get_context_usage data). SessionStarted: the whole init.</summary>
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
        /// <summary>TurnCompleted: the result's <c>subtype</c> ("success", "error_during_execution", "error_max_turns"…).</summary>
        public string ResultSubtype { get; set; }
        /// <summary>The turn's subagents (<c>subagent_stats</c>, 2.1.319); null when not sent.</summary>
        public SubagentStats Subagents { get; set; }
        /// <summary>TurnCompleted: the result's <c>modelUsage</c>, cumulative per model for the process (subagents included).</summary>
        public Dictionary<string, ModelUsageEntry> ModelUsage { get; set; }
        /// <summary>AssistantUsage of a subagent: the Agent call it runs under (<c>parent_tool_use_id</c>).</summary>
        public string ParentToolUseId { get; set; }

        // SessionStarted
        /// <summary>The CLI's <c>claude_code_version</c> from <c>init</c>, e.g. "2.1.318".</summary>
        public string CliVersion { get; set; }
        /// <summary>The first init of this CLI process (it comes again at the start of every turn).</summary>
        public bool IsFirstInit { get; set; }
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
