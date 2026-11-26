using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeVS.Commands;
using System.Collections.Generic;
using ClaudeVS.Core.Chat;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Context;
using ClaudeVS.Core.Tokens;
using ClaudeVS.Diagnostics;
using ClaudeVS.Options;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.ToolWindow
{
    /// <summary>The context usage breakdown from <c>get_context_usage</c>, in the meter's tooltip and its popup.</summary>
    public sealed partial class ClaudeChatControl
    {
        /// <summary>
        /// Asks the running CLI what fills the context (get_context_usage: no model call, nothing billed) and keeps
        /// the answer for the breakdown popup; <paramref name="show"/> opens the popup when it arrives.
        /// </summary>
        // The turn that just ended failed with "Prompt is too long": the next breakdown says what takes the room
        private bool _tooLongAdvice;

        private void RefreshContextUsage(bool show)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var s = _session;
            if (s == null || !s.IsRunning) { if (show) ShowContextBreakdownNow(); return; }
            Jtf.RunAsync(async () =>
            {
                ContextUsage u = null;
                string error = null;
                try { u = await s.GetContextUsageAsync(); }
                catch (InvalidOperationException ex) { error = ex.Message; } // an older CLI without get_context_usage
                await Jtf.SwitchToMainThreadAsync();
                if (s != _session) return;
                if (error != null) ClaudeOutputPane.WriteLine("Context usage not available: " + error);
                if (u != null)
                {
                    _contextUsage = u;
                    UpdateMeterTooltip();
                    _meterModel?.ApplyContextUsage(u.TotalTokens, u.MaxTokens); // the CLI's own number, also right after /compact
                    BaseContextTip(u);
                    if (_tooLongAdvice) { _tooLongAdvice = false; Notice(SessionNotices.TooLong(u), important: true); }
                }
                if (show) ShowContextBreakdownNow();
            }).FileAndForget("ClaudeVS/ChatControl/ContextUsage");
        }

        private void ShowContextBreakdown()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_busy || _session?.IsRunning != true) ShowContextBreakdownNow(); // while answering, show the last one instead of waiting
            else RefreshContextUsage(show: true);
        }

        /// <summary>The breakdown popup: used categories, what is in "Messages", reserved space, and tips.</summary>
        private void ShowContextBreakdownNow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var u = _contextUsage;
            var entries = new List<ChatPopup.Entry>();
            if (u == null)
            {
                entries.Add(new ChatPopup.Entry { Label = "No breakdown yet", Description = "It comes from the running Claude process, so it appears after the first answer.", Enabled = false });
                _contextPopup.Footer = null;
                _contextPopup.Show(entries, _meterArea);
                return;
            }

            string Size(long tokens) => $"{TokenMeter.Format(tokens)} · {u.PercentOfWindow(tokens):0.#}%";
            string window = $"In use: {TokenMeter.Format(u.TotalTokens)} of {TokenMeter.Format(u.MaxTokens)} ({u.Percentage:0}%)";
            foreach (var c in u.Categories.Where(c => c.IsUsed && c.Tokens > 0).OrderByDescending(c => c.Tokens))
                entries.Add(new ChatPopup.Entry { Group = window, Label = c.Name, Detail = Size(c.Tokens), Description = CategoryAction(c) != null ? "Click to see them and turn off what you don't need" : null, Run = CategoryAction(c) });
            foreach (var p in u.MessageParts())
                entries.Add(new ChatPopup.Entry { Group = "Inside Messages", Label = p.Key, Detail = Size(p.Value) });
            foreach (var c in u.Categories.Where(c => !c.IsUsed && c.Tokens > 0))
                entries.Add(new ChatPopup.Entry
                {
                    Group = "Not in use",
                    Label = c.Name,
                    Description = c.Kind == "deferred" ? "Loaded only when Claude needs it" : c.Kind == "buffer" ? "Kept free so autocompact has room" : null,
                    Detail = Size(c.Tokens),
                });
            var limits = _rateLimits;
            if (limits != null)
                foreach (var w in limits.Windows)
                    entries.Add(new ChatPopup.Entry
                    {
                        Group = "Plan usage",
                        Label = RateLimitInfo.DisplayName(w.Name),
                        Detail = $"{Math.Round(w.Utilization * 100):0}%",
                        Description = w.ResetsAtUtc == null ? null : "Resets at " + RateLimitInfo.When(w.ResetsAtUtc.Value, DateTime.UtcNow),
                    });
            foreach (var tip in u.Tips())
            {
                int dot = tip.IndexOf(". ", StringComparison.Ordinal); // first sentence as the label ("1.2k" has no space after its dot)
                entries.Add(new ChatPopup.Entry { Group = "Tips", Glyph = ChatTheme.IconLightbulb, Label = dot < 0 ? tip : tip.Substring(0, dot), Description = dot < 0 ? null : tip.Substring(dot + 2) });
            }
            if (_costlySettings != null && _costlySettings.Count > 0)
                entries.Add(new ChatPopup.Entry
                {
                    Group = "Tips", Glyph = ChatTheme.IconLightbulb,
                    Label = $"{_costlySettings.Count} Claude Code {(_costlySettings.Count == 1 ? "setting makes" : "settings make")} turns dearer",
                    Description = string.Join("; ", _costlySettings.Select(c => c.Setting)) + ". Click for where and why",
                    Run = ShowSettingsCheck,
                });
            if (_setupProblems != null && _setupProblems.Count > 0)
                entries.Add(new ChatPopup.Entry
                {
                    Group = "Tips", Glyph = ChatTheme.IconLightbulb,
                    Label = $"Token-saving setup: {_setupProblems.Count} {(_setupProblems.Count == 1 ? "part isn't" : "parts aren't")} in effect",
                    Description = string.Join("; ", _setupProblems.Select(c => c.Setting)) + ". Click for where and how to fix",
                    Run = ShowSetupCheck,
                });
            if (u.Percentage >= 30)
                entries.Add(new ChatPopup.Entry { Group = "Tips", Glyph = ChatTheme.IconCompact, Label = "Compact now", Description = "Summarize the conversation to free space", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SendText("/compact", routed: false); } });

            _contextPopup.Footer = (u.IsAutoCompactEnabled && u.AutoCompactThreshold > 0 ? $"Autocompact at {TokenMeter.Format(u.AutoCompactThreshold)} · " : "")
                                 + (_busy ? "as of the last answer" : "from Claude Code, no model call");
            _contextPopup.Show(entries, _meterArea);
        }

        private static string ContextTooltip(ContextUsage u)
        {
            var lines = u.Categories.Where(c => c.IsUsed && c.Tokens > 0).OrderByDescending(c => c.Tokens).Take(5)
                .Select(c => $"{c.Name}: {TokenMeter.Format(c.Tokens)}");
            return $"Context {TokenMeter.Format(u.TotalTokens)} of {TokenMeter.Format(u.MaxTokens)}\n" + string.Join("\n", lines) + "\nClick for the full breakdown";
        }
    }
}
