using System.Windows.Automation;
using System.Windows.Input;
using ClaudeVS.Core.Chat;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// A suggested next prompt as grey text in the empty input box once a turn ends: from the answer's closing offer
    /// (<see cref="NextPrompt.FromAnswer"/>, no model call), replaced by Claude Code's own suggestion when that option
    /// is on and one comes. Tab or → puts it into the box; it is never sent on its own.
    /// </summary>
    public sealed partial class ClaudeChatControl
    {
        private const string PlaceholderText = "Ask Claude to edit…";
        private string _suggestion;

        /// <summary>After a turn ends: the local rule's suggestion, if any.</summary>
        private void SuggestFromAnswer(string answer, bool isError)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (isError) { ClearSuggestion(); return; }
            ShowSuggestion(NextPrompt.FromAnswer(answer));
        }

        /// <summary>Claude Code's suggestion (it comes just after the turn's result): it knows the conversation, so it wins.</summary>
        private void SuggestFromCli(string suggestion)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ShowSuggestion(suggestion);
        }

        private void ShowSuggestion(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Not while Claude works or waits on a card, not over what you typed, not when turned off.
            if (string.IsNullOrWhiteSpace(text) || !(Options?.SuggestNextPrompt ?? true) || _busy || _waitingForUser || _prompt.Text.Length > 0)
                return;
            _suggestion = text.Trim();
            _placeholder.Text = _suggestion + "   Tab ⇥";
            AutomationProperties.SetHelpText(_prompt, "Suggested prompt: " + _suggestion + ". Press Tab to use it.");
        }

        private void ClearSuggestion()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_suggestion == null) return;
            _suggestion = null;
            _placeholder.Text = PlaceholderText;
            AutomationProperties.SetHelpText(_prompt, "");
        }

        /// <summary>Tab or → in the empty box takes the suggestion, to edit or send. True when the key was used.</summary>
        private bool TakeSuggestion(KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_suggestion == null || _prompt.Text.Length > 0 || Keyboard.Modifiers != ModifierKeys.None) return false;
            if (e.Key != Key.Tab && e.Key != Key.Right) return false;
            string text = _suggestion;
            ClearSuggestion();
            _prompt.Text = text;
            _prompt.CaretIndex = text.Length;
            return true;
        }
    }
}
