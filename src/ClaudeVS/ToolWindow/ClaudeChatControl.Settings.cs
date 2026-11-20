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
        /// <summary>The Claude Code settings that make turns dearer, as read at the process's start (for the context breakdown).</summary>
        private System.Collections.Generic.List<CostlySetting> _costlySettings;
        /// <summary>Claude Code talks to a gateway: a low output limit may be all its model takes (no "raise it").</summary>
        private bool _behindGateway;

        /// <summary>
        /// / → Settings that cost: reads Claude Code's settings now (starting it if needed, no model call) and says in the
        /// chat which make turns dearer, where each is set and how to change it.
        /// </summary>
        private void ShowSettingsCheck()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsureSession(WorkFolder);
            var s = _session;
            if (s == null) return;
            Jtf.RunAsync(async () =>
            {
                ClaudeSettings settings = null;
                try { settings = await s.GetSettingsAsync(start: true); }
                catch (InvalidOperationException ex) { ClaudeOutputPane.WriteLine("Claude Code's settings not available: " + ex.Message); }
                await Jtf.SwitchToMainThreadAsync();
                if (s != _session) return;
                if (settings == null) { Append("\nClaude Code didn't answer with its settings (an older Claude Code may not offer them). Try again once it runs.\n"); return; }
                _costlySettings = settings.Costly;
                _behindGateway = settings.BehindGateway;
                Append("\n" + SettingsCheck.Text(settings.Costly) + "\n");
            }).FileAndForget("ClaudeVS/ChatControl/SettingsCheck");
        }

        /// <summary>What isn't in effect of the token-saving setup, as read at the process's start; null when not installed.</summary>
        private System.Collections.Generic.List<CostlySetting> _setupProblems;

        /// <summary>
        /// / → Check token-saving setup: reads Claude Code's settings now (starting it if needed, no model call) and says
        /// whether the hook, the read-deny rules and the output cap that Install Token-Saving Settings wrote still apply.
        /// </summary>
        private void ShowSetupCheck()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsureSession(WorkFolder);
            var s = _session;
            if (s == null) return;
            string root = WorkFolder;
            Jtf.RunAsync(async () =>
            {
                ClaudeSettings settings = null;
                try { settings = await s.GetSettingsAsync(start: true); }
                catch (InvalidOperationException ex) { ClaudeOutputPane.WriteLine("Claude Code's settings not available: " + ex.Message); }
                var problems = settings == null ? null : await System.Threading.Tasks.Task.Run(() => SetupCheck.Find(settings.Response, root));
                await Jtf.SwitchToMainThreadAsync();
                if (s != _session) return;
                if (settings == null) { Append("\nClaude Code didn't answer with its settings (an older Claude Code may not offer them). Try again once it runs.\n"); return; }
                _setupProblems = problems;
                Append("\n" + SetupCheck.Text(problems) + "\n");
                if (problems != null && problems.Count > 0) _view.AppendActions(SetupFixes(problems, root));
            }).FileAndForget("ClaudeVS/ChatControl/SetupCheck");
        }

        /// <summary>
        /// Links under the setup check's problems: Reinstall (when that fixes one: the project's files) and Open for
        /// each settings file that overrides the setup.
        /// </summary>
        private System.Collections.Generic.List<(string Text, string Tip, Action OnClick)> SetupFixes(System.Collections.Generic.List<CostlySetting> problems, string root)
        {
            var actions = new System.Collections.Generic.List<(string, string, Action)>();
            if (SetupCheck.ReinstallHelps(problems))
                actions.Add(("Reinstall", "Install Token-Saving Settings and Hooks again for " + root + " (merges into .claude/settings.json; yours stay)", (Action)(() => ReinstallSetup(root))));
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var file in SetupCheck.FilesToOpen(problems, root, home))
            {
                string shown = file.StartsWith(root ?? "\0", StringComparison.OrdinalIgnoreCase) ? file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/') : file;
                actions.Add(("Open " + shown, file, (Action)(() => { ThreadHelper.ThrowIfNotOnUIThread(); if (System.IO.File.Exists(file)) Commands.VsContext.OpenFile(file); })));
            }
            return actions;
        }

        private void ReinstallSetup(string root)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            bool autoApprove = Options?.AutoApproveHookRewrites ?? false;
            Jtf.RunAsync(async () =>
            {
                string said;
                try
                {
                    var result = await System.Threading.Tasks.Task.Run(() => Core.Setup.TokenSaverSetup.Apply(root, autoApprove));
                    said = result.Written.Count == 0 ? "Token-saving setup: nothing to change in the project's files." : "Token-saving setup reinstalled: " + string.Join(", ", result.Written) + ".";
                }
                catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException)
                {
                    said = "Could not reinstall the token-saving setup: " + ex.Message;
                }
                await Jtf.SwitchToMainThreadAsync();
                Append("\n" + said + " Start a New chat for Claude Code to load it, then check again.\n");
            }).FileAndForget("ClaudeVS/ChatControl/ReinstallSetup");
        }

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
                _costlySettings = settings.Costly;
                _behindGateway = settings.BehindGateway;
                _setupProblems = SetupCheck.Find(settings.Response, WorkFolder);
                if (_setupProblems != null && _setupProblems.Count > 0)
                    ClaudeOutputPane.WriteLine(SetupCheck.Text(_setupProblems));
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
