using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClaudeVS.Core.Cli
{
    /// <summary>A piece of tool output in one style (Phase 104).</summary>
    public sealed class TerminalSpan
    {
        public string Text;
        /// <summary>One of the 16 terminal colors (0 black … 7 white, 8–15 their bright forms); null for the default.</summary>
        public int? Color;
        public bool Bold;
        public override string ToString() => (Color.HasValue ? "c" + Color : "") + (Bold ? "b" : "") + ":" + Text;
    }

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

        /// <summary>
        /// A tool's output as <c>Copy</c> puts it on the clipboard (Phase 105): the whole of it, also when it is folded,
        /// a progress bar's last state, no escape codes, CRLF line breaks, no blank end.
        /// </summary>
        public static string ForCopy(string text) =>
            (Strip(Collapse(text)) ?? "").TrimEnd().Replace("\r\n", "\n").Replace("\n", "\r\n");

        /// <summary>
        /// Lines a progress bar redrew with <c>\r</c> (<c>Downloading 10%\r… 100%</c>) as their last state (Phase 104):
        /// what follows the last <c>\r</c> of a line that has text after it. <c>\r\n</c> is a line break as before.
        /// A redraw shorter than what it covers isn't merged with the rest of the older text; progress bars redraw whole.
        /// </summary>
        public static string Collapse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('\r') < 0) return text;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int k = 0; k < lines.Length; k++)
            {
                string line = lines[k];
                if (line.IndexOf('\r') < 0) continue;
                var states = line.Split('\r');
                string last = states.LastOrDefault(s => Strip(s).Trim().Length > 0);
                lines[k] = last ?? "";
            }
            return string.Join("\n", lines);
        }

        /// <summary>
        /// The text in pieces of one style, as the color codes (<c>ESC[…m</c>) say (Phase 104): the 16 colors (30–37,
        /// 90–97), 256-color and RGB ones (38;5;n, 38;2;r;g;b) as the nearest of the 16, bold (1, 22), resets (0, 39).
        /// Background colors and every other code are left out. Neighbouring pieces of the same style are joined.
        /// </summary>
        public static List<TerminalSpan> Spans(string text)
        {
            var spans = new List<TerminalSpan>();
            if (string.IsNullOrEmpty(text)) return spans;
            int? color = null;
            bool bold = false;
            var sb = new StringBuilder();
            void Flush()
            {
                if (sb.Length == 0) return;
                var last = spans.Count > 0 ? spans[spans.Count - 1] : null;
                if (last != null && last.Color == color && last.Bold == bold) last.Text += sb.ToString();
                else spans.Add(new TerminalSpan { Text = sb.ToString(), Color = color, Bold = bold });
                sb.Clear();
            }
            for (int i = 0; i < text.Length; i++)
            {
                int end = CodeEnd(text, i);
                if (end == i) { sb.Append(text[i]); continue; }
                string code = text.Substring(i, end - i);
                if (code.EndsWith("m", StringComparison.Ordinal) && (code.StartsWith("\u001b[", StringComparison.Ordinal) || code[0] == Csi8))
                {
                    Flush();
                    ApplySgr(code.Substring(code[0] == Csi8 ? 1 : 2, code.Length - (code[0] == Csi8 ? 2 : 3)), ref color, ref bold);
                }
                i = end - 1;
            }
            Flush();
            return spans;
        }

        private static void ApplySgr(string parameters, ref int? color, ref bool bold)
        {
            var p = parameters.Split(';', ':').Select(x => int.TryParse(x, out int v) ? v : 0).ToList();
            if (p.Count == 0) p.Add(0);
            for (int k = 0; k < p.Count; k++)
            {
                int n = p[k];
                if (n == 0) { color = null; bold = false; }
                else if (n == 1) bold = true;
                else if (n == 22) bold = false;
                else if (n >= 30 && n <= 37) color = n - 30;
                else if (n >= 90 && n <= 97) color = n - 90 + 8;
                else if (n == 39) color = null;
                else if ((n == 38 || n == 48) && k + 1 < p.Count)
                {
                    int? c = null;
                    if (p[k + 1] == 5 && k + 2 < p.Count) { c = From256(p[k + 2]); k += 2; }
                    else if (p[k + 1] == 2 && k + 4 < p.Count) { c = FromRgb(p[k + 2], p[k + 3], p[k + 4]); k += 4; }
                    else k++;
                    if (n == 38) color = c; // a background isn't drawn
                }
            }
        }

        /// <summary>A 256-color index as the nearest of the 16.</summary>
        internal static int From256(int n)
        {
            if (n < 16) return Math.Max(0, n);
            if (n >= 232) return n < 244 ? 8 : 7; // the grays: dark gray, light gray
            n -= 16;
            int r = n / 36, g = n / 6 % 6, b = n % 6;
            return FromRgb(r * 51, g * 51, b * 51);
        }

        /// <summary>An RGB color as the nearest of the 16: which channels are lit, bright when the strongest is.</summary>
        internal static int FromRgb(int r, int g, int b)
        {
            int max = Math.Max(r, Math.Max(g, b));
            if (max < 64) return 0;
            int bits = (r >= max / 2 + 32 ? 1 : 0) | (g >= max / 2 + 32 ? 2 : 0) | (b >= max / 2 + 32 ? 4 : 0);
            return bits + (max >= 200 ? 8 : 0);
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
