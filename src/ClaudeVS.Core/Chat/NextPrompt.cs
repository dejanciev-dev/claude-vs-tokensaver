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
        };

        private static readonly Regex SentenceEnd = new Regex(@"(?<=[.!?])\s+(?=[""“(*_`]?[A-Z])");
        private static readonly Regex Markup = new Regex(@"\*\*|__|(?<!\w)[*_](?!\s)|(?<!\s)[*_](?!\w)|`");

        /// <summary>The reply that takes up the offer the answer ends with, or null.</summary>
        public static string FromAnswer(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer)) return null;
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
                    if (action != null) return "Yes, " + action;
                }
            }
            return null;
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
