using System.ComponentModel;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Context;
using ClaudeVS.Core.Tokens;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVS.Options
{
    /// <summary>Tools > Options > Claude Code > General.</summary>
    public sealed class ClaudeOptionsPage : DialogPage
    {
        public const string BrevityPrompt = "Be concise. Show only changed code; do not restate unchanged code or repeat the question.";

        [Category("CLI"), DisplayName("Claude executable path"), Description("Leave empty to find claude.exe / claude.cmd automatically.")]
        public string ClaudePath { get; set; } = "";

        [Category("CLI"), DisplayName("Permission mode"), Description("AcceptEdits: file edits are applied, shell commands ask. Plan: read-only planning. Note: in Plan mode the CLI answered with Sonnet even when Haiku was requested (Claude Code 2.1.316 to 2.1.319), so auto-routing to Haiku saves nothing there.")]
        public PermissionModeChoice PermissionMode { get; set; } = PermissionModeChoice.AcceptEdits;

        [Category("Chat"), DisplayName("Archive inactive sessions after (days)"), Description("Sessions not used for this many days move to the Archived group of the sessions list (0 = never). Unarchiving one keeps it visible until it is inactive that long again.")]
        public int AutoArchiveDays { get; set; } = 14;

        [Category("Chat"), DisplayName("Focus view"), Description("Collapse consecutive tool calls into one summary row (expand it to see each call). Also toggled with / → Focus view.")]
        public bool FocusView { get; set; }

        [Category("Chat"), DisplayName("Screen reader announcements"), Description("Screen readers (Narrator, NVDA, JAWS) announce when Claude finishes (with the start of the answer), asks for permission, has a question or a plan, wants an edit reviewed, stops responding, or hits a usage limit. Nothing is sent when no screen reader is running.")]
        public bool ScreenReaderAnnouncements { get; set; } = true;

        [Category("Chat"), DisplayName("Show message timestamps"), Description("A small time above each prompt and at the end of each answer's line, with a date line where the day changes.")]
        public bool ShowTimestamps { get; set; } = true;

        [Category("CLI"), DisplayName("File checkpoints (rewind)"), Description("Lets Claude Code keep a copy of each file before Claude changes it, so you can rewind a prompt's file changes (hover your message → Rewind). Applies to new sessions.")]
        public bool FileCheckpoints { get; set; } = true;

        [Category("CLI"), DisplayName("Extra arguments"), Description("Appended to the claude command line, e.g. --add-dir ..\\shared")]
        public string ExtraArguments { get; set; } = "";

        [Category("Token saving"), DisplayName("Concise answers"), Description("Adds a one-line system prompt asking for short answers. Output tokens cost ~5x input tokens.")]
        public bool ConciseAnswers { get; set; } = true;

        [Category("Token saving"), DisplayName("Default model"), Description("Auto = route each prompt to the cheapest model likely to handle it.")]
        public ModelChoice DefaultModel { get; set; } = ModelChoice.Auto;

        [Category("Token saving"), DisplayName("Route with Haiku"), Description("With the model on Auto, a small Haiku call picks model and effort for each prompt instead of the local keyword rules (about $0.0001 and 1–2 s per prompt). It runs in a separate, trimmed Claude Code process (no tools, no project files, ~1.3k tokens) started when the chat opens. If it doesn't answer within 4 s, the local rules decide. The savings report compares both.")]
        public bool HaikuRouting { get; set; }

        [Category("Token saving"), DisplayName("Inline selection up to (lines)"), Description("Larger selections are sent as file:line references instead of pasted code.")]
        public int InlineMaxLines { get; set; } = 60;

        [Category("Token saving"), DisplayName("Keep model above context (tokens)"), Description("Auto routing will not switch model once the conversation is this big, to keep the prompt cache.")]
        public int StickyContextTokens { get; set; } = 30000;

        [Category("Token saving"), DisplayName("Suggest /compact at (%)")]
        public int CompactSuggestPercent { get; set; } = 60;

        [Category("Token saving"), DisplayName("Urge /compact at (%)")]
        public int CompactUrgentPercent { get; set; } = 85;

        [Category("Token saving"), DisplayName("Token tips"), Description("One-time tips in the chat when something costs more tokens than it needs to: a large file read whole or twice, a flood of command output, heavy MCP tools or memory files, a costly output style, a cold prompt cache. Billing, MCP server and permission mode notes are always shown.")]
        public bool TokenTips { get; set; } = true;

        [Category("Token saving"), DisplayName("Warn at conversation cost (USD)"), Description("The chat says so once each time a conversation passes another multiple of this amount (at API prices, as Claude Code reports them). 0 turns it off.")]
        public double CostWarningUsd { get; set; } = 5;

        [Category("Token saving"), DisplayName("Auto-approve quiet rewrites"), Description("When installing hooks: run rewritten dotnet build/test commands without asking.")]
        public bool AutoApproveHookRewrites { get; set; } = false;

        [Category("Token saving"), DisplayName("Claude can read the Error List"), Description("Gives Claude a read-only Error List tool (an MCP server inside the extension), so it can check errors and warnings after editing instead of running a full build. It costs almost nothing until Claude uses it. Applies to new sessions.")]
        public bool ErrorListTool { get; set; } = true;

        [Category("Token saving"), DisplayName("Max attached errors"), Description("Attach Error List sends at most this many errors, one line each.")]
        public int MaxAttachedErrors { get; set; } = 20;

        [Category("Diagnostics"), DisplayName("Keep local usage log"), Description("Append one line per turn (model, route, tokens, cost) to %LocalAppData%\\claude-vs-tokensaver\\usage.jsonl. Stays on this machine; used for router tuning and the savings report.")]
        public bool KeepUsageLog { get; set; } = true;

        [Category("Diagnostics"), DisplayName("Log prompt preview (for router tuning)"), Description("Also store the first 200 characters of each prompt in the usage log, so the savings report can replay real prompts through the router. Off by default because prompts may contain code or secrets.")]
        public bool LogPromptPreview { get; set; } = false;

        [Category("Pricing"), DisplayName("Opus price"), Description("USD per million tokens: input, output, cache write, cache read. Used only for the savings estimate.")]
        public string OpusPrice { get; set; } = PriceTable.DefaultOpus;

        [Category("Pricing"), DisplayName("Sonnet price"), Description("USD per million tokens: input, output, cache write, cache read.")]
        public string SonnetPrice { get; set; } = PriceTable.DefaultSonnet;

        [Category("Pricing"), DisplayName("Haiku price"), Description("USD per million tokens: input, output, cache write, cache read.")]
        public string HaikuPrice { get; set; } = PriceTable.DefaultHaiku;

        public PriceTable ToPriceTable() => PriceTable.FromStrings(OpusPrice, SonnetPrice, HaikuPrice);

        [Category("Diagnostics"), DisplayName("Log raw CLI traffic"), Description("Write every stream-json line sent to and received from claude to Output > Claude Code. Large; turn on only when debugging.")]
        public bool LogRawCliTraffic { get; set; } = false;

        public ContextSettings ToContextSettings() => new ContextSettings { InlineMaxLines = InlineMaxLines, MaxErrors = MaxAttachedErrors };

        public TokenMeterSettings ToMeterSettings() => new TokenMeterSettings
        {
            CompactSuggestPercent = CompactSuggestPercent,
            CompactUrgentPercent = CompactUrgentPercent,
        };
    }
}
