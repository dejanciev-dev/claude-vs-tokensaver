using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Tokens;
using ClaudeVS.Diagnostics;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// "Your Claude sign-in ends in 4 days (Nov 21). Sign in" under the meter, from 10 days before a stored claude.ai
    /// sign-in ends (<c>credential_expires_at</c> in each result, 2.1.318), and in red once it has expired or the CLI
    /// says so. Sign in opens a console running <c>claude auth login</c>.
    /// </summary>
    public sealed partial class ClaudeChatControl
    {
        private DateTime? _signInExpiresUtc;
        // The CLI refused with an expired-login error; cleared by the next answer that completes.
        private bool _signInExpired;
        private string _signInText;
        private readonly TextBlock _signInBanner = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };

        /// <summary>From each completed turn; an answer that completed means the sign-in works.</summary>
        private void OnSignInExpiry(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (ev.CredentialExpiresUtc != null) _signInExpiresUtc = ev.CredentialExpiresUtc;
            if (!ev.IsError) _signInExpired = false;
            UpdateSignInBanner();
        }

        /// <summary>From an error line: the CLI's message when the stored login has reached its time limit.</summary>
        private void CheckSignInError(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!SignInExpiry.IsExpiredError(text)) return;
            _signInExpired = true;
            UpdateSignInBanner();
        }

        private void UpdateSignInBanner()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var (level, text) = _signInExpired
                ? (LimitLevel.Reached, "Your Claude sign-in has expired.")
                : SignInExpiry.Notice(_signInExpiresUtc, DateTime.UtcNow);
            if (level == LimitLevel.None) { _signInBanner.Visibility = Visibility.Collapsed; _signInText = null; return; }
            if (text == _signInText) return; // the timer calls this; keep the link under the mouse
            _signInText = text;

            bool reached = level == LimitLevel.Reached;
            _signInBanner.Inlines.Clear();
            _signInBanner.Inlines.Add(new Run((reached ? "⛔ " : "⚠ ") + text + " "));
            _signInBanner.Inlines.Add(PermLink("Sign in", "Opens a console running `" + SignInExpiry.LoginCommand + "` (your browser opens to sign in)", RunSignIn));
            _signInBanner.Foreground = reached ? ChatTheme.Error : Brushes.DarkOrange;
            _signInBanner.ToolTip = _signInExpiresUtc != null
                ? $"Claude Code's stored claude.ai sign-in ends {_signInExpiresUtc.Value.ToLocalTime():f}.\nSign in again to renew it; on a build server, `claude setup-token` makes a long-lived token."
                : "Sign in again with `" + SignInExpiry.LoginCommand + "`.";
            _signInBanner.Visibility = Visibility.Visible;
            AnnounceLimit(text, reached);
        }

        private void RunSignIn()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string exe = ClaudeCliLocator.Resolve(Options?.ClaudePath);
            if (exe == null) { AppendError("[sign in] ", "Claude Code CLI not found. Set its path under Tools > Options > Claude Code."); return; }
            try
            {
                // A console of its own, left open so the result stays readable.
                var psi = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    "/d /s /k \"" + ClaudeArgsBuilder.Quote(exe) + " auth login\"")
                { UseShellExecute = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
                System.Diagnostics.Process.Start(psi);
                ClaudeOutputPane.WriteLine("Started " + SignInExpiry.LoginCommand);
                Append("  → Sign in in the console that opened, then send your message again.\n");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                AppendError("[sign in] ", ex.Message);
            }
        }
    }
}
