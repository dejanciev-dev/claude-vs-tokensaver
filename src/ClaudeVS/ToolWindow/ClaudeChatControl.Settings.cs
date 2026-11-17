using System;
using ClaudeVS.Core.Cli;
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
            }).FileAndForget("ClaudeVS/ChatControl/Settings");
        }

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
