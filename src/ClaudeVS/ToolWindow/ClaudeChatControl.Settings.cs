using System;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Tokens;
using ClaudeVS.Diagnostics;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// The few of Claude Code's own settings the chat follows (<see cref="ClaudeSettings"/>), read once each Claude
    /// Code process starts: the effort cap (<c>maxEffortLevel</c>) and <c>promptSuggestionEnabled</c>.
    /// </summary>
    public sealed partial class ClaudeChatControl
    {
        /// <summary>Claude Code's <c>maxEffortLevel</c>, once read; null when not set or not known yet.</summary>
        private EffortChoice? _effortCap;

        /// <summary>On a process's first init: reads Claude Code's settings (no model call) and applies them.</summary>
        private void ReadClaudeSettings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var s = _session;
            if (s == null) return;
            Jtf.RunAsync(async () =>
            {
                ClaudeSettings settings = null;
                try { settings = await s.GetSettingsAsync(); }
                catch (InvalidOperationException ex) { ClaudeOutputPane.WriteLine("Claude Code's settings not available: " + ex.Message); }
                await Jtf.SwitchToMainThreadAsync();
                if (s != _session || settings == null) return;
                ApplyEffortCap(settings.MaxEffort);
                bool off = settings.PromptSuggestionEnabled == false;
                if (off && !_claudeSuggestionsOff) ClaudeOutputPane.WriteLine("Suggested next prompts are off: promptSuggestionEnabled is false in Claude Code's settings");
                _claudeSuggestionsOff = off;
                if (off) ClearSuggestion();
            }).FileAndForget("ClaudeVS/ChatControl/Settings");
        }

        /// <summary>
        /// The effort a turn ran at: what Claude Code said it applies when the prompt went out (<see cref="ConfirmEffort"/>),
        /// else what it applies now (the first prompt of a process goes out before that is read), else the one asked for,
        /// no higher than the cap.
        /// </summary>
        private string EffortUsed(UsageRecord rec)
        {
            if (rec == null) return null;
            if (rec.EffortFromClaudeCode) return rec.Effort;
            return _session?.AppliedEffort ?? ClaudeSettings.Cap(rec.Effort, _effortCap);
        }

        private bool _saidEffortDiffers; // once per conversation

        /// <summary>
        /// After a prompt's effort switch: Claude Code's applied effort goes into its usage record, and when it is not the
        /// one asked for (a cap set for the model by your organization, or the settings), the chat says so once.
        /// </summary>
        private void ConfirmEffort(UsageRecord rec)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string applied = _session?.AppliedEffort;
            if (rec == null || applied == null) return;
            string asked = rec.Effort;
            rec.Effort = applied;
            rec.EffortFromClaudeCode = true;
            if (asked == null || string.Equals(asked, applied, StringComparison.OrdinalIgnoreCase) || _saidEffortDiffers) return;
            _saidEffortDiffers = true;
            ClaudeOutputPane.WriteLine($"Effort: asked for {asked}, Claude Code applies {applied}");
            Notice($"Claude Code runs at {applied} effort, not {asked}: a cap for this model in your organization's or your Claude Code settings. "
                + "The footer and the usage log show the effort it runs at.");
        }

        /// <summary>
        /// <c>promptSuggestionEnabled: false</c> in Claude Code's settings: no suggested next prompt here either. Claude
        /// Code then makes none itself, even with <c>--prompt-suggestions true</c> (2.1.326: no request in its debug log).
        /// </summary>
        private bool _claudeSuggestionsOff;

        /// <summary>
        /// Claude Code answers at no higher effort than <c>maxEffortLevel</c> (and says so only in its debug log), so the
        /// effort picker and the router stay under it, and the footer and the usage log say the effort really used.
        /// </summary>
        private void ApplyEffortCap(EffortChoice? cap)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            bool changed = cap != _effortCap;
            _effortCap = cap;
            if (cap != null && changed) ClaudeOutputPane.WriteLine($"Effort cap from Claude Code's settings (maxEffortLevel): {cap.Value.ToCli()}");
            if (ClaudeSettings.Above(_effortChoice, cap))
            {
                Notice($"Your Claude Code settings cap the effort at {cap.Value.ToCli()} (maxEffortLevel), so {_effortChoice.ToCli()} is not used: "
                    + $"the effort picker is set to {cap.Value.ToCli()}.");
                _effortChoice = cap.Value;
                UpdateChips();
            }
        }
    }
}
