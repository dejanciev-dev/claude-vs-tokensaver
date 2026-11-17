using System.Windows.Automation;
using System.Windows.Input;
using ClaudeVS.Core.Chat;
using ClaudeVS.Core.Tokens;
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

        /// <summary>
        /// The suggestion shown since the last prompt went out, for that prompt's usage record: its source
        /// (<see cref="UsageRecord.SuggestionLocal"/> or <see cref="UsageRecord.SuggestionCli"/>) and whether Tab or →
        /// took it. Kept after typing clears the grey text; reset once a prompt takes it along, and on a new conversation.
        /// </summary>
        private string _offeredSource;
        private bool _offeredTaken;

        /// <summary>What Claude Code's own requests (suggestions, /compact) have cost in this conversation, as billed; else null.</summary>
        private string SuggestionsCostLine() =>
            _meterModel == null ? null : SuggestionCost.Line(_meterModel.SessionSuggestionsUsd, _meterModel.SessionCompactUsd);

        /// <summary>After a turn ends: the local rule's suggestion, if any.</summary>
        private void SuggestFromAnswer(string answer, bool isError)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (isError) { ClearSuggestion(); return; }
            ShowSuggestion(NextPrompt.FromAnswer(answer), UsageRecord.SuggestionLocal);
        }

        /// <summary>Claude Code's suggestion (it comes just after the turn's result): it knows the conversation, so it wins.</summary>
        /// <summary>
        /// Claude Code's suggestion. Its cost is billed with the next turn (the meter measures it there,
        /// <see cref="TokenMeter.LastTurnSide"/>).
        /// </summary>
        private void SuggestFromCli(string suggestion)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ShowSuggestion(suggestion, UsageRecord.SuggestionCli);
        }

        /// <summary>For the next prompt's usage record: which suggestion was shown before it, and whether it was taken; then forgets it.</summary>
        private (string Source, bool Taken) TakeOffered()
        {
            var offered = (_offeredSource, _offeredSource != null && _offeredTaken);
            ForgetOffered();
            return offered;
        }

        private void ForgetOffered() { _offeredSource = null; _offeredTaken = false; }

        private void ShowSuggestion(string text, string source)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Not while Claude works or waits on a card, not over what you typed, not when turned off.
            if (string.IsNullOrWhiteSpace(text) || !(Options?.SuggestNextPrompt ?? true) || _busy || _waitingForUser || _prompt.Text.Length > 0)
                return;
            _offeredSource = source;
            _offeredTaken = false;
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
            _offeredTaken = true; // sent as is or edited
            _prompt.Text = text;
            _prompt.CaretIndex = text.Length;
            return true;
        }
    }
}
