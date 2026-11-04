using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeVS.Core.Chat;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// Screen reader support: announcements through a UIA live region (answer finished, errors, permission requests,
    /// questions, plans, edits to review, stalls, limits) and Focus last message (Ctrl+Alt+K, M).
    /// </summary>
    public sealed partial class ClaudeChatControl
    {
        // Invisible but in the automation tree: a collapsed element would have no automation peer.
        private readonly TextBlock _announcer = new TextBlock { Height = 1, Opacity = 0, IsHitTestVisible = false, Focusable = false };
        private string _lastLimitAnnounced;

        private void InitAccessibility(Panel host)
        {
            AutomationProperties.SetLiveSetting(_announcer, AutomationLiveSetting.Polite);
            AutomationProperties.SetName(_announcer, "");
            host.Children.Add(_announcer);
            AutomationProperties.SetName(_prompt, "Message to Claude");
            AutomationProperties.SetHelpText(_prompt, "Enter sends. Slash lists commands, at sign picks a file. Control Alt K, then M, reads Claude's last answer.");
            AutomationProperties.SetName(_view.Box, "Conversation");
            AutomationProperties.SetName(_meterArea, "Context and token usage. Click for what fills the context window.");
            // Esc in the conversation goes back to the prompt box.
            _view.Box.PreviewKeyDown += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None) return;
                e.Handled = true;
                _view.Box.IsReadOnlyCaretVisible = false;
                _prompt.Focus();
            };
        }

        /// <summary>
        /// Says <paramref name="text"/> through the screen reader, if one is listening and the option is on.
        /// Assertive interrupts what is being read (for things that wait for you).
        /// </summary>
        private void Announce(string text, bool assertive = false)
        {
            if (string.IsNullOrWhiteSpace(text) || Options?.ScreenReaderAnnouncements == false) return;
            if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return; // no screen reader: nothing to do
            _announcer.Text = text;
            AutomationProperties.SetName(_announcer, text);
            AutomationProperties.SetLiveSetting(_announcer, assertive ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
            var peer = UIElementAutomationPeer.FromElement(_announcer) ?? UIElementAutomationPeer.CreatePeerForElement(_announcer);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        /// <summary>Moves the keyboard to the start of Claude's last answer (Tools → Claude Code → Focus Last Message).</summary>
        public void FocusLastMessage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_view.FocusLastAnswer()) Announce("Claude's last answer. Use the arrow keys to read it, Escape to return to the prompt.");
            else Announce("There is no answer from Claude yet.");
        }

        private void AnnounceLimit(string text, bool reached)
        {
            if (text == _lastLimitAnnounced) return;
            _lastLimitAnnounced = text;
            if (reached) Announce(text, assertive: true);
        }

        private static string Short(string text, int max = 200) => ScreenReaderText.Shorten(ScreenReaderText.Plain(text), max);
    }
}
