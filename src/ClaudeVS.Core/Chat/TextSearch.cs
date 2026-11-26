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
        /// <summary>The start of each match, left to right, not overlapping; none for an empty query.</summary>
        public static List<int> FindAll(string text, string query, bool matchCase)
        {
            var found = new List<int>();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return found;
            var how = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            for (int at = text.IndexOf(query, 0, how); at >= 0; at = text.IndexOf(query, at + query.Length, how))
                found.Add(at);
            return found;
        }

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

        /// <summary>What the find bar says: "3 of 12", "No results", or nothing for an empty query.</summary>
        public static string Status(int index, int count, string query)
        {
            if (string.IsNullOrEmpty(query)) return "";
            if (count == 0) return "No results";
            return index >= 0 ? $"{index + 1} of {count}" : (count == 1 ? "1 match" : $"{count} matches");
        }
    }
}
