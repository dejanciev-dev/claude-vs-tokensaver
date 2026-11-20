using System;
using System.Windows.Automation;
using System.Windows.Input;
using ClaudeVS.Core.Chat;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Tokens;
using ClaudeVS.Diagnostics;
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

        /// <summary>After a turn ends: the local rule's suggestion, if any; after a failed one, what to do next.</summary>
        private void SuggestFromAnswer(string answer, ClaudeEvent result, string prompt)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (result.IsError)
            {
                var step = ClaudeErrorHints.NextStep(true, UsageRecord.EndOf(true, result.TerminalReason, result.ResultSubtype), result.ErrorKind, result.ApiErrorStatus, result.Text, prompt);
                if (ClaudeErrorHints.ModelOutputLimit(result.Text) is int most)
                {
                    // Managed settings rank above the project's files: a link would write a value that doesn't apply
                    if (ClaudeErrorHints.ManagedOutputLimit(most, _settingsResponse) is string managed) step = new NextStep { Hint = managed };
                    else OfferOutputLimit(most);
                }
                ShowNextStep(step);
                return;
            }
            ShowSuggestion(NextPrompt.FromAnswer(answer), UsageRecord.SuggestionLocal);
        }

        private const string OutputLimitVariable = "CLAUDE_CODE_MAX_OUTPUT_TOKENS";

        /// <summary>
        /// The model refused Claude Code's output limit (a gateway's model, 2.1.329 no longer retries lower): a link
        /// under the failed turn sets the limit the error names and restarts Claude Code into the same conversation.
        /// </summary>
        private void OfferOutputLimit(int most)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string root = WorkFolder, value = most.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(root)) return;
            _view.AppendActions(new System.Collections.Generic.List<(string, string, Action)>
            {
                ($"Set {OutputLimitVariable}={value}",
                    $"Writes it into env in {System.IO.Path.Combine(root, ".claude", "settings.local.json")} and restarts Claude Code; the conversation carries on",
                    (Action)(() => SetOutputLimit(root, value))),
            });
        }

        private void SetOutputLimit(string root, string value)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var s = _session;
            Jtf.RunAsync(async () =>
            {
                string said;
                try
                {
                    await System.Threading.Tasks.Task.Run(() => Core.Setup.TokenSaverSetup.SetLocalEnv(root, OutputLimitVariable, value));
                    said = $"Set {OutputLimitVariable}={value} in .claude/settings.local.json.";
                    if (s != null && !_busy)
                    {
                        // Claude Code reads settings when it starts: the next prompt starts it again with --resume.
                        await System.Threading.Tasks.Task.Run(() => s.StopProcess());
                        said += " Claude Code restarts with it on your next prompt, in this conversation.";
                    }
                    else said += " It applies once Claude Code starts again (New chat, or after this turn).";
                }
                catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException)
                {
                    said = $"Could not set {OutputLimitVariable}: " + ex.Message;
                }
                await Jtf.SwitchToMainThreadAsync();
                Append("\n" + said + "\n");
            }).FileAndForget("ClaudeVS/ChatControl/SetOutputLimit");
        }

        /// <summary>The hint after a failed turn is showing (with or without a prompt for Tab): Claude Code's suggestion doesn't replace it.</summary>
        private bool _stepShown;

        /// <summary>
        /// After a failed turn, the empty box says what to do (<see cref="ClaudeErrorHints.NextStep"/>), as Claude Code
        /// 2.1.326 does; Tab puts in the step's prompt, if it has one. Shown also with suggestions turned off: it says
        /// what went wrong, it doesn't guess what you want.
        /// </summary>
        private void ShowNextStep(NextStep step)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ClearSuggestion();
            if (step == null || _busy || _waitingForUser || _prompt.Text.Length > 0) return;
            _suggestion = step.Prompt; // null: the hint alone, nothing for Tab
            _stepShown = true;
            _placeholder.Text = step.Display();
            AutomationProperties.SetHelpText(_prompt, "The last request failed. " + step.Hint + (step.Prompt != null ? ". Press Tab to put in: " + step.Prompt : "."));
        }

        /// <summary>
        /// Claude Code's suggestion. Its cost is billed with the next turn (the meter measures it there,
        /// <see cref="TokenMeter.LastTurnSide"/>).
        /// </summary>
        private void SuggestFromCli(string suggestion)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _cliSuggestionsCame++;
            // Billed with the next result; until then, what it would cost if the chat ends first (no later turn bills it)
            _unbilledSuggestion = (_session?.SessionId, _meterModel?.LastModel,
                SuggestionCost.EstimateUsd(_meterModel?.ContextTokens ?? 0, _meterModel?.LastModel, _meterModel?.Prices ?? Options?.ToPriceTable()));
            ShowSuggestion(suggestion, UsageRecord.SuggestionCli);
        }

        /// <summary>Claude Code's suggestion after the last result, estimated; null once a result billed it.</summary>
        private (string SessionId, string Model, double Usd)? _unbilledSuggestion;

        /// <summary>The chat ends (New chat, another conversation, closing): logs the unbilled last suggestion as a record with no turn.</summary>
        private void LogUnbilledSuggestion(string solution)
        {
            var unbilled = _unbilledSuggestion;
            _unbilledSuggestion = null;
            if (unbilled == null || unbilled.Value.Usd <= 0) return;
            var (sessionId, model, usd) = unbilled.Value;
            _cliSuggestionsUsd += usd;
            ClaudeOutputPane.WriteLine($"Claude Code's last suggestion in the conversation, never billed with a turn: ~${usd:0.0000} estimated");
            if (Options?.KeepUsageLog == false) return;
            var rec = UsageRecord.LastSuggestion(solution, sessionId, model, usd, DateTime.UtcNow);
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { Usage.Append(rec); }
                catch (Exception ex) { ClaudeOutputPane.WriteLine("Could not write usage log: " + ex.Message); }
            });
        }

        // Claude Code's suggestions since Visual Studio started, all chat windows together: for the tip when they don't pay
        private static int _cliSuggestionsCame, _cliSuggestionsTaken;
        private static double _cliSuggestionsUsd;
        private static bool _suggestionsTipGiven;

        /// <summary>After a turn: adds what it billed for suggestions, and gives the tip once per VS session when they don't pay.</summary>
        private void CountSuggestionCost()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _unbilledSuggestion = null; // this result billed it
            if (_meterModel != null && !_meterModel.LastTurnSide.IsEmpty && !_meterModel.LastTurnSideIsCompact)
                _cliSuggestionsUsd += _meterModel.LastTurnSideCostUsd;
            if (_suggestionsTipGiven || !(Options?.ClaudeCodeSuggestions ?? false)) return;
            if (SuggestionCost.Tip(_cliSuggestionsCame, _cliSuggestionsTaken, _cliSuggestionsUsd) is string tip)
            {
                _suggestionsTipGiven = true;
                Tip(tip);
            }
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
            if (string.IsNullOrWhiteSpace(text) || !(Options?.SuggestNextPrompt ?? true) || _claudeSuggestionsOff || _busy || _waitingForUser || _prompt.Text.Length > 0)
                return;
            if (_stepShown) return; // what to do after a failed turn stays
            _offeredSource = source;
            _offeredTaken = false;
            _suggestion = text.Trim();
            _placeholder.Text = _suggestion + "   Tab ⇥";
            AutomationProperties.SetHelpText(_prompt, "Suggested prompt: " + _suggestion + ". Press Tab to use it.");
        }

        private void ClearSuggestion()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_suggestion == null && !_stepShown) return;
            _suggestion = null;
            _stepShown = false;
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
            if (_offeredSource == UsageRecord.SuggestionCli) _cliSuggestionsTaken++;
            _prompt.Text = text;
            _prompt.CaretIndex = text.Length;
            return true;
        }
    }
}
