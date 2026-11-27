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
    /// <summary>Rewind: the picker over your earlier prompts, forking a session at one, and rewinding files to it.</summary>
    public sealed partial class ClaudeChatControl
    {
        /// <summary>The Rewind button for a prompt bubble; null (no button) without a message id or with checkpoints off.</summary>
        private Action RewindAction(string messageId, string prompt, DateTime? timeUtc = null)
        {
            if (string.IsNullOrEmpty(messageId) || Options?.FileCheckpoints == false) return null;
            _rewindPoints.Add((messageId, prompt, timeUtc ?? DateTime.UtcNow));
            return () => { ThreadHelper.ThrowIfNotOnUIThread(); RewindTo(messageId, prompt); };
        }

        // Prompts in this transcript that can be rewound, oldest first (for the Esc-Esc picker).
        private readonly List<(string Id, string Prompt, DateTime TimeUtc)> _rewindPoints = new List<(string, string, DateTime)>();
        private DateTime _lastEscUtc;

        /// <summary>Esc twice (or / → Rewind…): pick one of your prompts to put the files back to before it, newest first.</summary>
        private void ShowRewindPicker()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var now = DateTime.UtcNow;
            var entries = _rewindPoints.AsEnumerable().Reverse().Select(p => new ChatPopup.Entry
            {
                Glyph = ChatTheme.IconRewind,
                Label = SessionMetaStore.Clean(p.Prompt) ?? "(image)",
                Detail = SessionHistory.TimeAgo(p.TimeUtc, now),
                Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); RewindTo(p.Id, p.Prompt); },
            }).ToList();
            if (entries.Count == 0) entries.Add(new ChatPopup.Entry { Label = Options?.FileCheckpoints == false ? "File checkpoints are off (Tools → Options → Claude Code)" : "No prompts to rewind yet", Enabled = false });
            _manage.Footer = "Puts the files Claude changed since that prompt back; you confirm first";
            _manage.Show(entries, _inputBox);
        }

        /// <summary>
        /// *Go to prompt…* (Phase 113): this chat's prompts, newest first, with when each was sent; picking one scrolls
        /// the chat to it. Prompts no longer shown (a cleared chat) aren't listed.
        /// </summary>
        private void ShowGoToPrompt()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var now = DateTime.UtcNow;
            var prompts = _view.Prompts;
            var entries = prompts.Select((p, i) => (p, i)).Reverse().Select(x => new ChatPopup.Entry
            {
                Label = (x.i + 1) + ". " + (SessionMetaStore.Clean(x.p.Text) ?? "(image)"),
                Detail = SessionHistory.TimeAgo(x.p.TimeUtc, now),
                Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); _view.ScrollTo(x.p.Block); },
            }).ToList();
            if (entries.Count == 0) entries.Add(new ChatPopup.Entry { Label = "No prompts in this chat yet", Enabled = false });
            _manage.Footer = prompts.Count > 0 ? $"{prompts.Count} prompts · Ctrl+Up in the box steps through them" : null;
            _manage.Show(entries, _inputBox);
        }

        /// <summary>
        /// Continues this conversation in a new session (a copy, <c>--fork-session</c>), leaving the original as it
        /// was, so you can try another direction. Takes effect with the next prompt.
        /// </summary>
        private void ForkSession(string sessionId, string title, bool keepTranscript)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(sessionId)) { Append("Nothing to fork yet: send a prompt first."); return; }
            if (_busy) { Append($"Stop Claude first ({StopHow}), then fork."); return; }
            DisposeSession(); ClearWaitingNotice();
            ResetMeter();
            if (!keepTranscript) ClearTranscript();
            _resumeId = sessionId;
            _forkNext = true;
            _sessionName = SessionMetaStore.Clean((title ?? "Session") + " (fork)");
            SetTitle(_sessionName);
            SetEmptyState(false);
            Append("Forked: the next prompt continues in a new session with this history; the original stays as it was.");
            ClaudeOutputPane.WriteLine($"Next prompt forks session {sessionId}");
            _prompt.Focus();
        }

        private bool _forkNext;

        /// <summary>
        /// Puts the files Claude changed since <paramref name="prompt"/> back the way they were: a dry run lists the
        /// files, you confirm, then the CLI restores them from its checkpoints. The conversation is left as it is.
        /// </summary>
        private void RewindTo(string messageId, string prompt)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_busy) { Append($"Stop Claude first ({StopHow}), then rewind."); return; }
            string dir = _sessionDir ?? WorkFolder;
            EnsureSession(dir); // a resumed conversation has no process yet; starting one makes no model call
            var session = _session;
            string shortPrompt = SessionMetaStore.Clean(prompt) ?? "";
            if (shortPrompt.Length > 60) shortPrompt = shortPrompt.Substring(0, 59) + "…";

            Jtf.RunAsync(async () =>
            {
                var dry = await session.RewindFilesAsync(messageId, dryRun: true);
                await Jtf.SwitchToMainThreadAsync();
                if (session != _session) return;
                if (!dry.Ok) { AppendError("[rewind] ", RewindError(dry.Error)); return; }
                if (dry.FilesChanged.Count == 0) { Append($"Nothing to rewind: Claude has not changed any files since \"{shortPrompt}\"."); return; }

                string files = string.Join("\n", dry.FilesChanged.Take(15).Select(f => "  " + PromptContextBuilder.Relative(dir, f)))
                             + (dry.FilesChanged.Count > 15 ? $"\n  … and {dry.FilesChanged.Count - 15} more" : "");
                int answer = VsShellUtilities.ShowMessageBox(ClaudeVSPackage.Instance ?? (IServiceProvider)ServiceProvider.GlobalProvider,
                    $"Put these files back the way they were before \"{shortPrompt}\"?\n\n{files}\n\n({dry.Summary()}). Later changes to them, also your own, are undone too. "
                    + "The conversation is not changed, so tell Claude what you rewound.",
                    "Rewind file changes",
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_QUERY,
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
                if (answer != 1 /* IDOK */) return;

                var done = await session.RewindFilesAsync(messageId, dryRun: false);
                await Jtf.SwitchToMainThreadAsync();
                if (!done.Ok) { AppendError("[rewind] ", RewindError(done.Error)); return; }
                Append($"↶ Rewound {dry.Summary()} to before \"{shortPrompt}\". Claude still remembers its edits; tell it what you rewound.");
                ClaudeOutputPane.WriteLine("Rewound: " + string.Join(", ", dry.FilesChanged));
            }).FileAndForget("ClaudeVS/ChatControl/Rewind");
        }

        private static string RewindError(string error)
        {
            if (error != null && error.IndexOf("not enabled", StringComparison.OrdinalIgnoreCase) >= 0)
                return error + " Turn on Tools → Options → Claude Code → File checkpoints, then start a new session.";
            if (error != null && error.IndexOf("No file checkpoint", StringComparison.OrdinalIgnoreCase) >= 0)
                return error + " Checkpoints exist only for prompts sent while file checkpoints were on.";
            return error ?? "Rewind failed.";
        }
    }
}
