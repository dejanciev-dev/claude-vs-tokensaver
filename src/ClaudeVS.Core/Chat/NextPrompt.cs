using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudeVS.Core.Chat
{
    /// <summary>
    /// A suggested next prompt from the end of Claude's answer, with no model call: when the answer closes with an
    /// offer ("If you want separate PRs, tell me and I'll split them out", "Want me to run the tests?"), the reply
    /// that takes it up ("Yes, split them out"). Null when the end of the answer makes no offer. English only.
    /// </summary>
    public static class NextPrompt
    {
        /// <summary>Longer offers are left alone: cut short they would read wrongly.</summary>
        public const int MaxLength = 90;

        /// <summary>How many sentences from the end of the last paragraph are looked at.</summary>
        private const int SentencesLooked = 3;

        // Each finds the offered action in group "a". Tried in this order on each sentence, last sentence first.
        private static readonly Regex[] Offers =
        {
            // "…, tell me and I'll split them out." / "Say so and I will add it."
            new Regex(@"\b(?:tell me|let me know|say so|just say)(?: and| so)? I(?:'ll|’ll| will) (?<a>.+)$", RegexOptions.IgnoreCase),
            // "Want me to run the tests?" / "Should I commit?" / "Would you like me to open a PR?"
            new Regex(@"^(?:(?:do you |would you )?(?:want|like) me to|should I|shall I) (?<a>.+)\?$", RegexOptions.IgnoreCase),
            // "Let me know if you'd like me to add tests." / "Tell me if you want me to push."
            new Regex(@"\b(?:tell me|let me know)(?: if| whether) you(?:'d|’d| would)? (?:want|like) me to (?<a>.+)$", RegexOptions.IgnoreCase),
            // "If you want, I can add tests." / "If you'd like one, I can open it with …"
            new Regex(@"^if you(?:'d|’d| would)? (?:want|like|prefer)\b[^,]*, I (?:can|could)(?: also)? (?<a>.+)$", RegexOptions.IgnoreCase),
            // "If you want me to push, tell me." → push
            new Regex(@"^if you(?:'d|’d| would)? (?:want|like) me to (?<a>[^,]+),", RegexOptions.IgnoreCase),
            // "I can also add tests if you want."
            new Regex(@"^I (?:can|could)(?: also)? (?<a>.+?),? if you(?:'d|’d| would)? (?:want|like)(?: (?:it|that|them|me to))?$", RegexOptions.IgnoreCase),
            // "Happy to add tests if you'd like." / "I'm glad to split it up, if that helps."
            new Regex(@"^(?:I(?:'m|’m| am| would be|'d be|’d be) )?(?:happy|glad) to(?: also)? (?<a>.+?),? if (?:you(?:'d|’d| would)? (?:want|like)(?: (?:it|that|them|me to))?|that(?:'d|’d| would)? helps?)$", RegexOptions.IgnoreCase),
            // "Want to see the diff?" (me left out) → "show me the diff"; "Want to add a test for it?"
            new Regex(@"^(?:do you )?want to (?<see>see|look at|check) (?<a>.+)\?$", RegexOptions.IgnoreCase),
            new Regex(@"^(?:do you )?want to (?<a>.+)\?$", RegexOptions.IgnoreCase),
        };

        /// <summary>"Which do you prefer?", "Which one should I go with?": asks to pick from a list.</summary>
        private static readonly Regex WhichQuestion = new Regex(@"^which\b[^?]*\?$", RegexOptions.IgnoreCase);
        private static readonly Regex NumberedItem = new Regex(@"^\s*(?<n>\d{1,2})[.)]\s+(?<t>\S.*)$");

        private static readonly Regex SentenceEnd = new Regex(@"(?<=[.!?])\s+(?=[""“(*_`]?[A-Z])");
        private static readonly Regex Markup = new Regex(@"\*\*|__|(?<!\w)[*_](?!\s)|(?<!\s)[*_](?!\w)|`");

        /// <summary>The reply that takes up the offer the answer ends with, or null.</summary>
        public static string FromAnswer(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer)) return null;
            if (FirstOption(answer) is string option) return option;
            string paragraph = LastParagraph(answer);
            if (paragraph == null) return null;

            var sentences = SentenceEnd.Split(paragraph).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            foreach (string sentence in Enumerable.Reverse(sentences).Take(SentencesLooked))
            {
                string s = Markup.Replace(sentence, "").Trim();
                string question = s.EndsWith("?", StringComparison.Ordinal) ? s : null;
                s = s.TrimEnd('.', '!');
                foreach (var offer in Offers)
                {
                    var m = offer.Match(question ?? s);
                    if (!m.Success) continue;
                    string action = Action(m.Groups["a"].Value);
                    if (action == null) continue;
                    if (m.Groups["see"].Success) action = "show me " + action;
                    if (action.Length <= MaxLength) return "Yes, " + action;
                }
            }
            return null;
        }

        /// <summary>
        /// "Option 1: Split into two PRs" when the answer ends by asking which of a numbered list you want: the
        /// question after the list ("1. … 2. …\n\nWhich do you prefer?") or just before it. Null otherwise.
        /// </summary>
        private static string FirstOption(string answer)
        {
            var lines = answer.Replace("\r\n", "\n").Trim().Split('\n').Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count == 0) return null;
            // The list ends at the last line, or at the line before a closing "Which …?"
            int i = lines.Count - 1;
            bool asksAfter = IsWhich(lines[i]);
            if (asksAfter) i--;
            // Walk back over the items and the indented or bulleted lines under them
            int items = 0, first = -1;
            for (; i >= 0; i--)
            {
                string line = lines[i];
                if (NumberedItem.IsMatch(line)) { items++; first = i; }
                else if (!(char.IsWhiteSpace(line[0]) || line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))) break;
            }
            if (items < 2) return null;
            // The question comes after the list, or as the line just before it ("Which do you prefer?\n1. …\n2. …")
            if (!asksAfter && (i < 0 || i != first - 1 || !IsWhich(lines[i]))) return null;
            var item = NumberedItem.Match(lines[first]);
            if (item.Groups["n"].Value != "1") return null;
            string text = Markup.Replace(OptionTitle(item.Groups["t"].Value), "").Trim().TrimEnd('.', ':', ';', ',');
            if (text.Length < 2 || text.Length > MaxLength) return null;
            return "Option 1: " + text;
        }

        private static bool IsWhich(string line)
        {
            string s = Markup.Replace(line, "").Trim();
            // The question can follow other sentences: "Both work. Which do you prefer?"
            string lastSentence = SentenceEnd.Split(s).Last().Trim();
            return WhichQuestion.IsMatch(lastSentence);
        }

        /// <summary>A list item's title: its bold start ("**Split it** — two PRs" → "Split it"), or the part before a dash, colon or full stop.</summary>
        private static string OptionTitle(string item)
        {
            var bold = Regex.Match(item, @"^\*\*(?<t>[^*]+)\*\*");
            if (bold.Success) return bold.Groups["t"].Value;
            int cut = IndexOfAny(item, " — ", " – ", " - ", ": ", ". ", " (");
            return cut > 0 ? item.Substring(0, cut) : item;
        }

        /// <summary>The last paragraph of prose: code blocks, lists and tables are not offers.</summary>
        private static string LastParagraph(string answer)
        {
            var paragraphs = Regex.Split(answer.Replace("\r\n", "\n").Trim(), @"\n\s*\n");
            string last = paragraphs[paragraphs.Length - 1].Trim();
            if (last.StartsWith("```", StringComparison.Ordinal) || last.EndsWith("```", StringComparison.Ordinal)) return null;
            if (last.StartsWith("|", StringComparison.Ordinal) || last.StartsWith("#", StringComparison.Ordinal)) return null;
            return Regex.Replace(last, @"\s*\n\s*", " ");
        }

        /// <summary>The offered action, as you would ask for it: "show you the diff" → "show me the diff".</summary>
        private static string Action(string raw)
        {
            string a = raw.Trim().TrimEnd('?', '.', '!', ',', ':', ';').Trim();
            // Keep the action itself, not what follows it: "split them out, so each can be reviewed" → "split them out".
            int cut = IndexOfAny(a, ", so ", ", which ", " — ", " - ", "; ", " (");
            if (cut > 0) a = a.Substring(0, cut);
            // "Should I go ahead and commit?" → "commit"
            a = Regex.Replace(a, @"^(?:go ahead and|just|also|now) ", "", RegexOptions.IgnoreCase);
            a = Regex.Replace(a, @"\byou\b", "me");
            a = Regex.Replace(a, @"\byour\b", "my");
            a = Regex.Replace(a, @"\byours\b", "mine");
            a = Regex.Replace(a, @"\s+", " ").Trim();
            if (a.Length < 2 || a.Length > MaxLength) return null;
            if (a.IndexOf("http", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            return a;
        }

        private static int IndexOfAny(string s, params string[] parts)
        {
            int best = -1;
            foreach (string p in parts)
            {
                int i = s.IndexOf(p, StringComparison.Ordinal);
                if (i > 0 && (best < 0 || i < best)) best = i;
            }
            return best;
        }
    }
}
