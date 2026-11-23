namespace ClaudeVS.Core.Cli
{
    public enum CardKey { Up, Down, Enter }

    /// <summary>What a key does on a question card: where focus goes, what gets picked, whether to go on.</summary>
    public struct CardStep
    {
        /// <summary>The stop to focus (an index into the question's stops), or null to leave focus where it is.</summary>
        public int? Focus;
        /// <summary>Pick the option that gets focus (a single choice follows the arrows).</summary>
        public bool PickFocused;
        /// <summary>Pick the option the key was pressed on.</summary>
        public bool PickHere;
        /// <summary>Go to the next question, or answer after the last (<see cref="QuestionCardKeys.NextQuestion"/>).</summary>
        public bool Next;
    }

    /// <summary>
    /// The keyboard on an AskUserQuestion card (Phase 85, as VS Code's 2.1.330 fix). A question's stops are its
    /// options, then the Other text box (the last stop; focusing it picks Other). ↑/↓ move within the question, never
    /// into another, and on a single choice pick as they go; Enter picks the focused option (never un-picks: Space
    /// toggles) and goes to the next question, or answers after the last. The Other check box itself isn't a stop:
    /// ↑ goes to the last option, ↓ and Enter to its text box.
    /// </summary>
    public static class QuestionCardKeys
    {
        /// <summary>A key on stop <paramref name="at"/> of <paramref name="stops"/> (options, then the Other box).</summary>
        public static CardStep OnStop(CardKey key, int at, int stops, bool multiSelect)
        {
            bool isOther = at == stops - 1;
            switch (key)
            {
                case CardKey.Up:
                case CardKey.Down:
                    int to = at + (key == CardKey.Up ? -1 : 1);
                    if (to < 0 || to >= stops) return new CardStep(); // the question's first or last: stay
                    return new CardStep { Focus = to, PickFocused = !multiSelect && to != stops - 1 };
                case CardKey.Enter:
                    return new CardStep { PickHere = !isOther, Next = true }; // in the Other box, focusing it picked Other
                default:
                    return new CardStep();
            }
        }

        /// <summary>A key on the Other check box (not a stop): ↑ to the last option, ↓ and Enter to the text box.</summary>
        public static CardStep OnOtherCheckBox(CardKey key, int stops, bool multiSelect) =>
            key == CardKey.Up && stops > 1
                ? new CardStep { Focus = stops - 2, PickFocused = !multiSelect }
                : new CardStep { Focus = stops - 1 };

        /// <summary>The question after <paramref name="question"/>, or null after the last: then the card answers.</summary>
        public static int? NextQuestion(int question, int questions) => question + 1 < questions ? question + 1 : (int?)null;
    }
}
