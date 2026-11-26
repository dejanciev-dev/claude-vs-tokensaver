using System.Text;

namespace ClaudeVS.Core.Cli
{
    /// <summary>
    /// Tool output as a terminal would show it (Phase 104). Claude Code passes a command's escape codes through
    /// (2.1.332: <c>\u001b[32mgreen\u001b[0m plain</c> in a Bash result), and test runners, git, npm and dotnet print
    /// them; the chat shows text, not codes.
    /// </summary>
    public static class TerminalText
    {
        private const char Esc = '\u001b';
        private const char Csi8 = '\u009b'; // the one-character form of ESC [

        /// <summary>Whether <paramref name="text"/> holds anything to clean (most output doesn't).</summary>
        public static bool HasCodes(string text) => text != null && (text.IndexOf(Esc) >= 0 || text.IndexOf(Csi8) >= 0);

        /// <summary>
        /// The text without escape codes: colors and styles (<c>ESC[…m</c>), cursor moves and erases (any
        /// <c>ESC[…</c>), window titles and links (<c>ESC]…BEL</c> or <c>ESC]…ESC\</c>), and two-character ones
        /// (<c>ESC(B</c>, <c>ESC=</c>).
        /// </summary>
        public static string Strip(string text)
        {
            if (!HasCodes(text)) return text;
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                int end = CodeEnd(text, i);
                if (end > i) { i = end - 1; continue; }
                sb.Append(text[i]);
            }
            return sb.ToString();
        }

        /// <summary>Where an escape code that starts at <paramref name="i"/> ends (exclusive), or <paramref name="i"/> for none.</summary>
        internal static int CodeEnd(string s, int i)
        {
            char c = s[i];
            if (c == Csi8) return CsiEnd(s, i + 1);
            if (c != Esc) return i;
            if (i + 1 >= s.Length) return i + 1; // a lone ESC at the end
            char n = s[i + 1];
            if (n == '[') return CsiEnd(s, i + 2);
            if (n == ']')
            {
                // Until BEL or ESC \ (string terminator); unfinished: to the end
                for (int k = i + 2; k < s.Length; k++)
                {
                    if (s[k] == '\a') return k + 1;
                    if (s[k] == Esc && k + 1 < s.Length && s[k + 1] == '\\') return k + 2;
                }
                return s.Length;
            }
            // ESC, intermediates (space to /), a final character
            int j = i + 1;
            while (j < s.Length && s[j] >= 0x20 && s[j] <= 0x2F) j++;
            return j < s.Length ? j + 1 : s.Length;
        }

        // Parameters (0-9 ; : < = > ?), intermediates (space to /), a final character (@ to ~)
        private static int CsiEnd(string s, int j)
        {
            while (j < s.Length && s[j] >= 0x30 && s[j] <= 0x3F) j++;
            while (j < s.Length && s[j] >= 0x20 && s[j] <= 0x2F) j++;
            return j < s.Length ? j + 1 : s.Length;
        }
    }
}
