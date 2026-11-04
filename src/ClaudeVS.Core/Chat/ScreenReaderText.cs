using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeVS.Core.Chat
{
    /// <summary>
    /// Short plain-text announcements for screen readers (UIA live region): Markdown is turned into what a person
    /// would read aloud, and long text is cut at a word with a hint how to read the rest.
    /// </summary>
    public static class ScreenReaderText
    {
        public const int AnswerChars = 300;
        public const string ReadAllHint = "Press Control Alt K, then M, to read the whole answer.";

        private static readonly Regex Fence = new Regex(@"^\s*(```|~~~)[^\n]*\n.*?(^\s*\1\s*$|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        private static readonly Regex Link = new Regex(@"!?\[([^\]]*)\]\([^)]*\)");
        private static readonly Regex Heading = new Regex(@"^\s{0,3}#{1,6}\s+", RegexOptions.Multiline);
        private static readonly Regex ListMarker = new Regex(@"^\s*([-*+]|\d+[.)])\s+", RegexOptions.Multiline);
        private static readonly Regex Quote = new Regex(@"^\s*>\s?", RegexOptions.Multiline);
        private static readonly Regex Emphasis = new Regex(@"(\*\*|__|\*|_|~~|`)");
        private static readonly Regex TableRule = new Regex(@"^\s*\|?\s*:?-{3,}.*$", RegexOptions.Multiline);
        private static readonly Regex Spaces = new Regex(@"\s+");

        /// <summary>Markdown as plain text: code blocks become "(code block)", links keep their text, marks are dropped.</summary>
        public static string Plain(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return "";
            string s = markdown.Replace("\r\n", "\n");
            s = Fence.Replace(s, " (code block) ");
            s = Link.Replace(s, "$1");
            s = TableRule.Replace(s, "");
            s = Heading.Replace(s, "");
            s = ListMarker.Replace(s, "");
            s = Quote.Replace(s, "");
            s = Emphasis.Replace(s, "");
            s = s.Replace("|", " ");
            return Spaces.Replace(s, " ").Trim();
        }

        /// <summary>At most <paramref name="max"/> characters, cut after a sentence or word, with "…" when cut.</summary>
        public static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
            string cut = text.Substring(0, max);
            int sentence = cut.LastIndexOfAny(new[] { '.', '!', '?' });
            if (sentence >= max / 2) return cut.Substring(0, sentence + 1);
            int space = cut.LastIndexOf(' ');
            return (space >= max / 2 ? cut.Substring(0, space) : cut).TrimEnd(',', ';', ':') + "…";
        }

        /// <summary>"Claude finished. &lt;start of the answer&gt;", with how to read the rest when it was cut.</summary>
        public static string AnswerFinished(string answerMarkdown)
        {
            string plain = Plain(answerMarkdown);
            if (plain.Length == 0) return "Claude finished.";
            string start = Shorten(plain, AnswerChars);
            var sb = new StringBuilder("Claude finished. ").Append(start);
            if (start.Length < plain.Length) sb.Append(' ').Append(ReadAllHint);
            return sb.ToString();
        }
    }
}
