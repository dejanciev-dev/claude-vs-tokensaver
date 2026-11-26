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
        private static readonly Regex Quote = new Regex(@"^\s*(>\s?)+", RegexOptions.Multiline);
        private static readonly Regex Emphasis = new Regex(@"(\*\*|__|\*|_|~~|`)");
        private static readonly Regex TableRule = new Regex(@"^\s*\|?\s*:?-{3,}.*$", RegexOptions.Multiline);
        private static readonly Regex Spaces = new Regex(@"\s+");
        // Phase 99: what Phases 96-98 draw, said as words
        private static readonly Regex Image = new Regex(@"!\[([^\]]*)\]\([^)]*\)");
        private static readonly Regex Task = new Regex(@"^[ \t]*\[([ xX])\][ \t]+", RegexOptions.Multiline);
        private static readonly Regex Alert = new Regex(@"^[ \t]*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\][ \t]*", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        private static readonly Regex Break = new Regex(@"<br\s*/?>", RegexOptions.IgnoreCase);

        /// <summary>
        /// Markdown as plain text: code blocks become "(code block)", links keep their text, marks are dropped; a task
        /// reads "done:" or "to do:", an alert its title and a colon, an image "image:" and its alt text, entities their
        /// characters.
        /// </summary>
        public static string Plain(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return "";
            string s = markdown.Replace("\r\n", "\n");
            s = Fence.Replace(s, " (code block) ");
            s = Image.Replace(s, m => m.Groups[1].Value.Trim().Length > 0 ? "image: " + m.Groups[1].Value : "image");
            s = Link.Replace(s, "$1");
            s = TableRule.Replace(s, "");
            s = Heading.Replace(s, "");
            s = ListMarker.Replace(s, "");
            s = Task.Replace(s, m => m.Groups[1].Value == " " ? "to do: " : "done: ");
            s = Quote.Replace(s, "");
            s = Alert.Replace(s, m => char.ToUpperInvariant(m.Groups[1].Value[0]) + m.Groups[1].Value.Substring(1).ToLowerInvariant() + ": ");
            s = Break.Replace(s, " ");
            s = Emphasis.Replace(s, "");
            s = s.Replace("|", " ");
            s = System.Net.WebUtility.HtmlDecode(s);
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
