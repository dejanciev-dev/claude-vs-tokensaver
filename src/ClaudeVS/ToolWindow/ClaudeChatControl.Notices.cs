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

        /// <summary>A new conversation: notes may be said again.</summary>
        private void ResetNotices()
        {
            _saidNotices.Clear();
            _gaveLargeReadTip = false;
        }
    }
}
