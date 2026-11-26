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
    /// <summary>The usage limit banner, and the notice when Claude Code stops answering.</summary>
    public sealed partial class ClaudeChatControl
    {
        // Latest plan usage from the CLI (rate_limit_event); null for API-key users, who never get one.
        private RateLimitInfo _rateLimits;
        private readonly TextBlock _limitBanner = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };

        /// <summary>
        /// "5-hour limit 86% used. Resets at 3:10 PM." under the meter (orange), or "… reached" (red); hidden below
        /// 80% and once the reset time has passed. The tooltip lists every window.
        /// </summary>
        private void UpdateLimitBanner()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var now = DateTime.UtcNow;
            var r = _rateLimits;
            var (level, text) = r?.Banner(now) ?? (LimitLevel.None, null);
            if (level != LimitLevel.None && r.ResetsAtUtc != null && r.ResetsAtUtc < now && r.Windows.All(w => w.ResetsAtUtc == null || w.ResetsAtUtc < now))
                level = LimitLevel.None; // everything has reset since the CLI last told us
            if (level == LimitLevel.None) { _limitBanner.Visibility = Visibility.Collapsed; return; }
            _limitBanner.Text = (level == LimitLevel.Reached ? "⛔ " : "⚠ ") + text;
            _limitBanner.Foreground = level == LimitLevel.Reached ? ChatTheme.Error : Brushes.DarkOrange;
            _limitBanner.ToolTip = r.Details(now) + "\nFrom Claude Code; updated with each answer.";
            _limitBanner.Visibility = Visibility.Visible;
            AnnounceLimit(text, level == LimitLevel.Reached);
        }

        // ---------------- "Not responding" ----------------

        // Last time the CLI said anything (UTC ticks; written on the reader thread).
        private long _lastOutputTicks = DateTime.UtcNow.Ticks;
        // A permission bar or question/plan card is waiting for you: silence is expected then.
        private bool _waitingForUser;
        private DispatcherTimer _stallTimer;
        private readonly TextBlock _stall = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 4), Visibility = Visibility.Collapsed, Foreground = ChatTheme.Error };
        private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(60);

        private void CheckStalled()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var quiet = DateTime.UtcNow - new DateTime(System.Threading.Interlocked.Read(ref _lastOutputTicks), DateTimeKind.Utc);
            SetStalled(_busy && !_waitingForUser && quiet >= StallAfter, quiet);
            if (_limitBanner.Visibility == Visibility.Visible) UpdateLimitBanner(); // hide it once the limit has reset
            if (_signInBanner.Visibility == Visibility.Visible && !_signInExpired) UpdateSignInBanner(); // days left, then expired
            UpdateCacheNote();
        }

        private void SetStalled(bool stalled, TimeSpan quiet = default)
        {
            bool wasStalled = _stall.Visibility == Visibility.Visible;
            _stall.Visibility = stalled ? Visibility.Visible : Visibility.Collapsed;
            if (stalled) _stall.Text = $"⚠ Not responding: no output from Claude Code for {(int)quiet.TotalMinutes} min. A long command may still be running; " + (EscapeStops ? "Esc stops it." : "the Stop button stops it.");
            if (stalled && !wasStalled) Announce("Claude Code is not responding. A long command may still be running. Press Escape in the prompt box to stop it.");
            if (stalled != wasStalled) RenderActivity(); // the red notice replaces the activity strip
        }
    }
}
