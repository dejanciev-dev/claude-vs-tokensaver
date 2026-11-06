using System;
using System.Collections.Generic;
using ClaudeVS.Core.Cli;
using ClaudeVS.Diagnostics;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// Notes about the session from the CLI's init (billing, …): each said once per conversation in the chat, with a
    /// line in the Output pane. See <see cref="SessionNotices"/>.
    /// </summary>
    public sealed partial class ClaudeChatControl
    {
        // Notes already said in this conversation; init comes at the start of every turn.
        private readonly HashSet<string> _saidNotices = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>From each init.</summary>
        private void OnSessionFacts(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(ev.Payload is JObject init)) return;
            Notice(SessionNotices.ApiKey(init), important: true);
            Notice(SessionNotices.McpServers(init));
            Notice(SessionNotices.OutputStyle(init));
            if (ev.IsFirstInit) SyncPermissionMode(init);
        }

        /// <summary>
        /// On a process's first init: when the CLI started in another mode than the chat asked for, say so (unless
        /// Manual was asked, where your settings' default mode applies) and make the mode picker show the real one,
        /// without sending a switch back.
        /// </summary>
        private void SyncPermissionMode(JObject init)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var actual = ChoiceNames.FromCli((string)init["permissionMode"]);
            if (actual == null || actual == _mode) return;
            Notice(SessionNotices.PermissionMode(init, _mode), important: true);
            ClaudeOutputPane.WriteLine($"Mode: the CLI runs in {actual.Value.ToControl()}, the chat asked for {_mode.ToControl()}");
            _mode = actual.Value;
            UpdateChips();
        }

        /// <summary>Says a note once per conversation; important ones are also read out by screen readers.</summary>
        private void Notice(string text, bool important = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(text) || !_saidNotices.Add(text)) return;
            ClaudeOutputPane.WriteLine(text);
            FlushText();
            Append("\n  ℹ " + text + "\n");
            if (important) Announce(text);
        }

        // The large-read tip is given once per conversation, whichever file it was.
        private bool _gaveLargeReadTip;

        /// <summary>After a Read: the token tip when Claude read all of a large file.</summary>
        private void LargeReadTip(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_gaveLargeReadTip || !(ev.ResultData is JObject data)) return;
            string tip = SessionNotices.WholeFileRead(data, ev.ResultChars);
            if (tip == null) return;
            _gaveLargeReadTip = true;
            Notice(tip);
        }

        // ---------------- Repeated reads ----------------

        private readonly Core.Tokens.ReadTracker _reads = new Core.Tokens.ReadTracker();
        private bool _gaveRepeatReadTip;

        // The main agent's shell calls waiting for their result: tool_use_id → tool name.
        private readonly Dictionary<string, string> _shellCalls = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool _gaveLargeOutputTip;

        /// <summary>Every event: the token tips that watch tool calls (repeated reads, large command output).</summary>
        private void TrackTips(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            TrackReads(ev);
            if (ev.Kind == ClaudeEventKind.ToolUse && !ev.IsSubagent && ev.ToolUseId != null && (ev.ToolName == "Bash" || ev.ToolName == "PowerShell"))
                _shellCalls[ev.ToolUseId] = ev.ToolName;
            else if (ev.Kind == ClaudeEventKind.ToolResult && ev.ToolUseId != null && _shellCalls.TryGetValue(ev.ToolUseId, out var tool))
            {
                _shellCalls.Remove(ev.ToolUseId);
                if (ev.IsError || _gaveLargeOutputTip) return;
                string tip = SessionNotices.LargeCommandOutput(tool, ev.ResultChars, Core.Setup.TokenSaverSetup.IsInstalled(_sessionDir));
                if (tip == null) return;
                _gaveLargeOutputTip = true;
                Notice(tip);
            }
        }

        /// <summary>The tip, once per conversation, when Claude reads an unchanged file it already has.</summary>
        private void TrackReads(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(_reads.Observe(ev) is var (path, tokens))) return;
            ClaudeOutputPane.WriteLine($"Repeated read: {path} (~{Core.Tokens.TokenMeter.Format(tokens)} tokens)");
            if (_gaveRepeatReadTip || Core.Tokens.ReadTracker.Tip(path, tokens) is not string tip) return;
            _gaveRepeatReadTip = true;
            Notice(tip);
        }

        // ---------------- Prompt cache ----------------

        private readonly System.Windows.Controls.TextBlock _cacheNote = new System.Windows.Controls.TextBlock
        {
            FontSize = 11.5, TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new System.Windows.Thickness(0, 4, 0, 0), Visibility = System.Windows.Visibility.Collapsed,
        };

        /// <summary>
        /// While Claude is idle: "⏱ Prompt cache expires in 4 min: …" in the last minutes of a 1-hour cache, then
        /// "Prompt cache expired …" with what the next prompt costs. Refreshed by the 5-second timer.
        /// </summary>
        private void UpdateCacheNote()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cache = _meterModel?.Cache;
            var now = DateTime.UtcNow;
            string text = _busy || cache == null ? null : cache.ExpiredNote(now) ?? cache.SoonNote(now);
            if (text == null) { _cacheNote.Visibility = System.Windows.Visibility.Collapsed; return; }
            if (_cacheNote.Text != "⏱ " + text) _cacheNote.Text = "⏱ " + text;
            _cacheNote.ToolTip = cache.Status(now) + "\nEach request keeps the conversation cached for this long again; cached input costs a tenth of the full price.";
            ChatTheme.Muted(_cacheNote);
            _cacheNote.Visibility = System.Windows.Visibility.Visible;
        }

        /// <summary>A new conversation: notes may be said again.</summary>
        private void ResetNotices()
        {
            _saidNotices.Clear();
            _gaveLargeReadTip = false;
            _gaveRepeatReadTip = false;
            _gaveLargeOutputTip = false;
            _shellCalls.Clear();
            _reads.Reset();
        }
    }
}
