using System;
using System.Collections.Generic;

namespace ClaudeVS.Core.Chat
{
    /// <summary>
    /// Find in the chat (Phase 101): where a query is in the conversation's text, which match comes next, and what the
    /// find bar says about it.
    /// </summary>
    public static class TextSearch
    {
        /// <summary>
        /// The start of each match, left to right, not overlapping; none for an empty query. With
        /// <paramref name="wholeWord"/> (Phase 108) only a match with no word character (letter, digit, <c>_</c>) right
        /// before or after it: <c>id</c> doesn't find <c>width</c>.
        /// </summary>
        public static List<int> FindAll(string text, string query, bool matchCase, bool wholeWord = false)
        {
            var found = new List<int>();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return found;
            var how = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            for (int at = text.IndexOf(query, 0, how); at >= 0; )
            {
                if (!wholeWord || IsWholeWord(text, at, query.Length)) { found.Add(at); at = text.IndexOf(query, at + query.Length, how); }
                else at = text.IndexOf(query, at + 1, how);
            }
            return found;
        }

        /// <summary>
        /// Every match with its length (Phase 108). With <paramref name="regex"/> the query is a .NET regular expression
        /// (an empty match is passed over); one that doesn't parse, or takes too long, gives no matches and says why in
        /// <paramref name="error"/>. Otherwise as <see cref="FindAll"/>.
        /// </summary>
        public static List<(int Start, int Length)> Matches(string text, string query, bool matchCase, bool wholeWord, bool regex, out string error)
        {
            error = null;
            var found = new List<(int, int)>();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return found;
            if (!regex)
            {
                foreach (int at in FindAll(text, query, matchCase, wholeWord)) found.Add((at, query.Length));
                return found;
            }
            try
            {
                string pattern = wholeWord ? @"(?<![\w])(?:" + query + @")(?![\w])" : query;
                var options = System.Text.RegularExpressions.RegexOptions.CultureInvariant | (matchCase ? 0 : System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var re = new System.Text.RegularExpressions.Regex(pattern, options, TimeSpan.FromSeconds(1));
                foreach (System.Text.RegularExpressions.Match m in re.Matches(text))
                    if (m.Length > 0) found.Add((m.Index, m.Length));
            }
            catch (ArgumentException ex)
            {
                found.Clear();
                error = "Not a valid pattern: " + ShortReason(ex.Message);
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                found.Clear();
                error = "The pattern takes too long";
            }
            return found;
        }

        // .NET's message is "Invalid pattern '(' at offset 1. Not enough )'s.": the reason after the offset
        private static string ShortReason(string message)
        {
            int dot = message.IndexOf(". ", StringComparison.Ordinal);
            return (dot > 0 && dot + 2 < message.Length ? message.Substring(dot + 2) : message).TrimEnd('.');
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>Whether the match at <paramref name="at"/> stands as a word: no word character joins it on either side.</summary>
        public static bool IsWholeWord(string text, int at, int length) =>
            (at == 0 || !IsWordChar(text[at - 1])) && (at + length >= text.Length || !IsWordChar(text[at + length]));

        /// <summary>
        /// The match to go to from the one at <paramref name="from"/> (an offset in the text; -1 when none was shown):
        /// the first after it, or with <paramref name="back"/> the last before it, wrapping around. -1 when there are no
        /// matches. The text may have grown since (an answer streaming), so it goes by offset, not by index.
        /// </summary>
        public static int Next(IReadOnlyList<int> starts, int from, bool back)
        {
            if (starts == null || starts.Count == 0) return -1;
            if (back)
            {
                for (int k = starts.Count - 1; k >= 0; k--)
                    if (from < 0 || starts[k] < from) return k;
                return starts.Count - 1;
            }
            for (int k = 0; k < starts.Count; k++)
                if (starts[k] > from) return k;
            return 0;
        }

        /// <summary>
        /// What a screen reader says when the find bar's result changes (Phase 108): "3 of 12 for Validate", "No results
        /// for Validate", or the bar's own words (a pattern that doesn't parse). Nothing for an empty query.
        /// </summary>
        public static string Announcement(string status, string query)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(status)) return "";
            bool count = status == "No results" || status.EndsWith(" match", StringComparison.Ordinal) || status.EndsWith(" matches", StringComparison.Ordinal)
                || System.Text.RegularExpressions.Regex.IsMatch(status, @"^\d+ of \d+$");
            return count ? status + " for " + query : status;
        }

        /// <summary>What the find bar says: "3 of 12", "No results", or nothing for an empty query.</summary>
        public static string Status(int index, int count, string query)
        {
            if (string.IsNullOrEmpty(query)) return "";
            if (count == 0) return "No results";
            return index >= 0 ? $"{index + 1} of {count}" : (count == 1 ? "1 match" : $"{count} matches");
        }
    }
}
