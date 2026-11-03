using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClaudeVS.Core.Context
{
    /// <summary>
    /// Cleans prompt text before it is sent, like the VS Code panel (2.1.280):
    /// <list type="bullet">
    /// <item>Removes invisible characters that can hide text from you but not from Claude: Unicode tag characters
    /// (U+E0000–U+E007F, which can spell out hidden instructions), bidirectional overrides and isolates, zero-width
    /// spaces, word joiners, invisible operators, the byte-order mark and soft hyphens. Zero-width (non-)joiners are
    /// kept, since emoji sequences and several scripts need them.</item>
    /// <item>Marks long pasted text (over 800 characters or more than 2 line breaks), so Claude can tell it from
    /// what you typed.</item>
    /// </list>
    /// </summary>
    public static class PromptSanitizer
    {
        public const int LongPasteChars = 800;
        public const int LongPasteLineBreaks = 2;
        public const string PasteStart = "[Pasted text]";
        public const string PasteEnd = "[End of pasted text]";

        /// <summary>The text without invisible formatting characters; <paramref name="removed"/> counts them (a surrogate pair counts once).</summary>
        public static string RemoveInvisible(string text, out int removed)
        {
            removed = 0;
            if (string.IsNullOrEmpty(text)) return text ?? "";
            StringBuilder sb = null;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                int width = 1;
                bool drop;
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    int cp = char.ConvertToUtf32(c, text[i + 1]);
                    width = 2;
                    drop = cp >= 0xE0000 && cp <= 0xE007F; // tag characters
                }
                else drop = IsInvisible(c);

                if (drop)
                {
                    if (sb == null) sb = new StringBuilder(text, 0, i, text.Length);
                    removed++;
                }
                else sb?.Append(text, i, width);
                i += width - 1;
            }
            return sb?.ToString() ?? text;
        }

        private static bool IsInvisible(char c) =>
            c == '­'                       // soft hyphen
            || c == '᠎'                    // Mongolian vowel separator
            || c == '​'                    // zero-width space (200C/200D joiners are kept)
            || c == '‎' || c == '‏'   // left-to-right / right-to-left marks
            || (c >= '‪' && c <= '‮') // bidi embeddings and overrides
            || (c >= '⁠' && c <= '⁤') // word joiner, invisible operators
            || (c >= '⁦' && c <= '⁩') // bidi isolates
            || c == '﻿';                   // byte-order mark / zero-width no-break space

        /// <summary>True for a paste that should be marked: over 800 characters or more than 2 line breaks.</summary>
        public static bool IsLongPaste(string pasted) =>
            !string.IsNullOrEmpty(pasted) && (pasted.Length > LongPasteChars || pasted.Count(ch => ch == '\n') > LongPasteLineBreaks);

        /// <summary>
        /// Wraps each long paste that is still in <paramref name="text"/> (first occurrence) in
        /// "[Pasted text]" … "[End of pasted text]" lines. Pastes that were edited away are skipped.
        /// </summary>
        public static string MarkPastes(string text, IEnumerable<string> pastes)
        {
            if (string.IsNullOrEmpty(text) || pastes == null) return text ?? "";
            int searchFrom = 0;
            foreach (var p in pastes.Where(IsLongPaste))
            {
                string paste = p.Trim('\r', '\n');
                if (paste.Length == 0) continue;
                int at = text.IndexOf(paste, searchFrom, System.StringComparison.Ordinal);
                if (at < 0) at = text.IndexOf(paste, System.StringComparison.Ordinal);
                if (at < 0) continue;
                string marked = PasteStart + "\n" + paste + "\n" + PasteEnd;
                string before = text.Substring(0, at), after = text.Substring(at + paste.Length);
                if (before.Length > 0 && !before.EndsWith("\n")) marked = "\n" + marked;
                if (after.Length > 0 && !after.StartsWith("\n")) marked += "\n";
                text = before + marked + after;
                searchFrom = before.Length + marked.Length;
            }
            return text;
        }
    }
}
