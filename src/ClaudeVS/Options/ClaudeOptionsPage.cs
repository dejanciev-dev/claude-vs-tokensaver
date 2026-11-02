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

        [Category("CLI"), DisplayName("Permission mode"), Description("AcceptEdits: file edits are applied, shell commands ask. Plan: read-only planning.")]
        public PermissionModeChoice PermissionMode { get; set; } = PermissionModeChoice.AcceptEdits;

        [Category("CLI"), DisplayName("Extra arguments"), Description("Appended to the claude command line, e.g. --add-dir ..\\shared")]
        public string ExtraArguments { get; set; } = "";

        [Category("Token saving"), DisplayName("Concise answers"), Description("Adds a one-line system prompt asking for short answers. Output tokens cost ~5x input tokens.")]
        public bool ConciseAnswers { get; set; } = true;

        [Category("Token saving"), DisplayName("Default model"), Description("Auto = route each prompt to the cheapest model likely to handle it.")]
        public ModelChoice DefaultModel { get; set; } = ModelChoice.Auto;

        [Category("Token saving"), DisplayName("Inline selection up to (lines)"), Description("Larger selections are sent as file:line references instead of pasted code.")]
        public int InlineMaxLines { get; set; } = 60;

        [Category("Token saving"), DisplayName("Keep model above context (tokens)"), Description("Auto routing will not switch model once the conversation is this big, to keep the prompt cache.")]
        public int StickyContextTokens { get; set; } = 30000;

        [Category("Token saving"), DisplayName("Suggest /compact at (%)")]
        public int CompactSuggestPercent { get; set; } = 60;

        [Category("Token saving"), DisplayName("Urge /compact at (%)")]
        public int CompactUrgentPercent { get; set; } = 85;

        [Category("Token saving"), DisplayName("Auto-approve quiet rewrites"), Description("When installing hooks: run rewritten dotnet build/test commands without asking.")]
        public bool AutoApproveHookRewrites { get; set; } = false;

        public ContextSettings ToContextSettings() => new ContextSettings { InlineMaxLines = InlineMaxLines };

        public TokenMeterSettings ToMeterSettings() => new TokenMeterSettings
        {
            CompactSuggestPercent = CompactSuggestPercent,
            CompactUrgentPercent = CompactUrgentPercent,
        };
    }
}
