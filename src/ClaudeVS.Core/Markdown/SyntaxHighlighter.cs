using System;
using System.Collections.Generic;

namespace ClaudeVS.Core.Markdown
{
    public enum SyntaxKind { Keyword, String, Comment, Number, Tag, Attribute }

    public struct SyntaxSpan
    {
        public int Start;
        public int Length;
        public SyntaxKind Kind;
        public override string ToString() => $"{Kind}@{Start}+{Length}";
    }

    /// <summary>
    /// A small, fast tokenizer for coloring code blocks in answers (it runs on every streamed update, so it does no
    /// parsing). It knows comments, strings, numbers and keywords for the usual languages, and tags/attributes for
    /// XML-like markup. Unknown languages still get comments, strings and numbers when they look C-like.
    /// Not a real lexer: odd cases (nested templates, heredocs) may color slightly wrong, never break the text.
    /// </summary>
    public static class SyntaxHighlighter
    {
        private sealed class Lang
        {
            public HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal);
            public bool IgnoreCase;
            public string LineComment = "//";
            public bool BlockComments = true;     // /* */
            public bool HashComments;             // #
            public bool SingleQuoteStrings = true;
            public bool Backticks;                // JS template strings
            public bool VerbatimStrings;          // C# @"..."
            public bool TripleQuotes;             // Python """..."""
            public bool DashDashComments;         // SQL, Lua
            public bool Markup;                   // XML/HTML
        }

        private static Lang L(string keywords, Action<Lang> setup = null, bool ignoreCase = false)
        {
            var l = new Lang { IgnoreCase = ignoreCase };
            if (ignoreCase) l.Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in keywords.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) l.Keywords.Add(k);
            setup?.Invoke(l);
            return l;
        }

        private static readonly Lang CSharp = L("abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach get goto if implicit in init int interface internal is lock long namespace new null object operator out override params private protected public readonly record ref return sbyte sealed set short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile when where while yield nameof dynamic value global partial required file",
            l => l.VerbatimStrings = true);
        private static readonly Lang Js = L("async await break case catch class const continue debugger default delete do else export extends false finally for from function get if implements import in instanceof interface let new null of private protected public readonly return set static super switch this throw true try type typeof undefined var void while with yield enum declare namespace abstract as any boolean number string never unknown keyof",
            l => l.Backticks = true);
        private static readonly Lang Java = L("abstract assert boolean break byte case catch char class const continue default do double else enum extends false final finally float for if implements import instanceof int interface long native new null package private protected public return short static super switch synchronized this throw throws transient true try var void volatile while record sealed permits yield fun val when object data override open lateinit");
        private static readonly Lang Cpp = L("auto bool break case catch char class const constexpr continue default delete do double else enum explicit extern false float for friend if inline int long mutable namespace new noexcept nullptr operator private protected public return short signed sizeof static struct switch template this throw true try typedef typename union unsigned using virtual void volatile while include define ifdef ifndef endif pragma");
        private static readonly Lang Go = L("break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var true false nil string int bool error",
            l => l.Backticks = true);
        private static readonly Lang Rust = L("as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while");
        private static readonly Lang Python = L("and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield self match case",
            l => { l.LineComment = null; l.BlockComments = false; l.HashComments = true; l.TripleQuotes = true; });
        private static readonly Lang PowerShell = L("begin break catch class continue data do dynamicparam else elseif end enum exit filter finally for foreach from function if in param process return switch throw trap try until using var while -eq -ne -gt -ge -lt -le -like -notlike -match -notmatch -and -or -not -contains -in $true $false $null",
            l => { l.LineComment = null; l.BlockComments = false; l.HashComments = true; }, ignoreCase: true);
        private static readonly Lang Shell = L("if then else elif fi for while until do done case esac in function return exit export local readonly echo set unset shift source true false",
            l => { l.LineComment = null; l.BlockComments = false; l.HashComments = true; });
        private static readonly Lang Sql = L("select from where and or not insert into values update set delete create table alter drop index view join inner left right outer full on group by order having as distinct top limit offset union all exists in is null like between case when then else end primary key foreign references default int varchar nvarchar bit datetime begin commit rollback transaction declare exec procedure with",
            l => { l.LineComment = null; l.DashDashComments = true; }, ignoreCase: true);
        private static readonly Lang Json = L("true false null", l => { l.LineComment = null; l.BlockComments = false; l.SingleQuoteStrings = false; });
        private static readonly Lang Yaml = L("true false null yes no on off", l => { l.LineComment = null; l.BlockComments = false; l.HashComments = true; });
        private static readonly Lang Css = L("important media import from to", l => { l.LineComment = null; });
        private static readonly Lang Markup = L("", l => { l.Markup = true; });
        private static readonly Lang Generic = L("");

        private static Lang For(string language)
        {
            switch ((language ?? "").Trim().ToLowerInvariant())
            {
                case "cs": case "c#": case "csharp": case "razor": case "cshtml": return CSharp;
                case "js": case "javascript": case "jsx": case "ts": case "typescript": case "tsx": case "mjs": return Js;
                case "java": case "kotlin": case "kt": case "scala": return Java;
                case "c": case "cpp": case "c++": case "h": case "hpp": case "cc": case "objc": return Cpp;
                case "go": case "golang": return Go;
                case "rust": case "rs": return Rust;
                case "py": case "python": return Python;
                case "ps": case "ps1": case "powershell": case "pwsh": return PowerShell;
                case "sh": case "bash": case "shell": case "zsh": case "console": return Shell;
                case "sql": case "tsql": case "mysql": case "postgres": case "postgresql": return Sql;
                case "json": case "jsonc": case "json5": return Json;
                case "yaml": case "yml": return Yaml;
                case "css": case "scss": case "less": return Css;
                case "xml": case "html": case "htm": case "xaml": case "csproj": case "props": case "targets": case "svg": case "vue": return Markup;
                case "": case "text": case "txt": case "plaintext": case "md": case "markdown": case "diff": case "output": return null;
                default: return Generic;
            }
        }

        public static List<SyntaxSpan> Tokenize(string code, string language)
        {
            var spans = new List<SyntaxSpan>();
            var lang = For(language);
            if (lang == null || string.IsNullOrEmpty(code)) return spans;
            if (lang.Markup) { TokenizeMarkup(code, spans); return spans; }

            int i = 0, n = code.Length;
            while (i < n)
            {
                char c = code[i];
                // Comments
                if (lang.LineComment != null && At(code, i, lang.LineComment)) { i = ToLineEnd(code, i, spans, SyntaxKind.Comment); continue; }
                if (lang.HashComments && c == '#' && !(lang == PowerShell && i + 1 < n && code[i + 1] == '{')) { i = ToLineEnd(code, i, spans, SyntaxKind.Comment); continue; }
                if (lang.DashDashComments && At(code, i, "--")) { i = ToLineEnd(code, i, spans, SyntaxKind.Comment); continue; }
                if (lang.BlockComments && At(code, i, "/*")) { i = Until(code, i, 2, "*/", spans, SyntaxKind.Comment); continue; }
                if (lang == PowerShell && At(code, i, "<#")) { i = Until(code, i, 2, "#>", spans, SyntaxKind.Comment); continue; }
                // Strings
                if (lang.TripleQuotes && (At(code, i, "\"\"\"") || At(code, i, "'''"))) { i = Until(code, i, 3, code.Substring(i, 3), spans, SyntaxKind.String); continue; }
                if (lang.VerbatimStrings && (At(code, i, "@\"") || At(code, i, "$@\"") || At(code, i, "@$\""))) { i = Verbatim(code, i, spans); continue; }
                if (c == '"' || (c == '\'' && lang.SingleQuoteStrings) || (c == '`' && lang.Backticks)) { i = QuotedString(code, i, c, spans); continue; }
                // Numbers
                if (char.IsDigit(c) && (i == 0 || !IsWordChar(code[i - 1])))
                {
                    int s = i;
                    while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] == '.' || code[i] == '_') && !(code[i] == '.' && (i + 1 >= n || !char.IsDigit(code[i + 1])))) i++;
                    spans.Add(new SyntaxSpan { Start = s, Length = i - s, Kind = SyntaxKind.Number });
                    continue;
                }
                // Words (keywords)
                if (IsWordStart(c) || (lang == PowerShell && (c == '-' || c == '$') && i + 1 < n && char.IsLetter(code[i + 1])))
                {
                    int s = i;
                    i++;
                    while (i < n && IsWordChar(code[i])) i++;
                    if (lang.Keywords.Contains(code.Substring(s, i - s))) spans.Add(new SyntaxSpan { Start = s, Length = i - s, Kind = SyntaxKind.Keyword });
                    continue;
                }
                i++;
            }
            return spans;
        }

        private static void TokenizeMarkup(string code, List<SyntaxSpan> spans)
        {
            int i = 0, n = code.Length;
            while (i < n)
            {
                if (At(code, i, "<!--")) { i = Until(code, i, 4, "-->", spans, SyntaxKind.Comment); continue; }
                if (code[i] == '<' && i + 1 < n && (char.IsLetter(code[i + 1]) || code[i + 1] == '/' || code[i + 1] == '?' || code[i + 1] == '!'))
                {
                    int s = i;
                    i++;
                    while (i < n && (IsWordChar(code[i]) || code[i] == '/' || code[i] == '?' || code[i] == '!' || code[i] == ':' || code[i] == '-' || code[i] == '.')) i++;
                    spans.Add(new SyntaxSpan { Start = s, Length = i - s, Kind = SyntaxKind.Tag });
                    // Attributes and values until the tag closes.
                    while (i < n && code[i] != '>')
                    {
                        if (code[i] == '"' || code[i] == '\'') { i = QuotedString(code, i, code[i], spans, escapes: false); continue; }
                        if (IsWordStart(code[i]))
                        {
                            int a = i;
                            while (i < n && (IsWordChar(code[i]) || code[i] == ':' || code[i] == '-' || code[i] == '.')) i++;
                            spans.Add(new SyntaxSpan { Start = a, Length = i - a, Kind = SyntaxKind.Attribute });
                            continue;
                        }
                        i++;
                    }
                    if (i < n) { spans.Add(new SyntaxSpan { Start = i - (code[i - 1] == '/' || code[i - 1] == '?' ? 1 : 0), Length = code[i - 1] == '/' || code[i - 1] == '?' ? 2 : 1, Kind = SyntaxKind.Tag }); i++; }
                    continue;
                }
                i++;
            }
        }

        private static bool At(string s, int i, string token) => string.CompareOrdinal(s, i, token, 0, token.Length) == 0;
        private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static int ToLineEnd(string code, int i, List<SyntaxSpan> spans, SyntaxKind kind)
        {
            int s = i;
            while (i < code.Length && code[i] != '\n' && code[i] != '\r') i++;
            spans.Add(new SyntaxSpan { Start = s, Length = i - s, Kind = kind });
            return i;
        }

        /// <summary>From <paramref name="i"/> past the opening (<paramref name="open"/> chars) up to and including <paramref name="close"/>, or the end.</summary>
        private static int Until(string code, int i, int open, string close, List<SyntaxSpan> spans, SyntaxKind kind)
        {
            int end = code.IndexOf(close, i + open, StringComparison.Ordinal);
            end = end < 0 ? code.Length : end + close.Length;
            spans.Add(new SyntaxSpan { Start = i, Length = end - i, Kind = kind });
            return end;
        }

        private static int QuotedString(string code, int i, char quote, List<SyntaxSpan> spans, bool escapes = true)
        {
            int s = i++;
            while (i < code.Length && code[i] != quote && (quote == '`' || code[i] != '\n'))
            {
                if (escapes && code[i] == '\\' && i + 1 < code.Length) i++;
                i++;
            }
            if (i < code.Length && code[i] == quote) i++;
            spans.Add(new SyntaxSpan { Start = s, Length = i - s, Kind = SyntaxKind.String });
            return i;
        }

        // C# @"..." (and $@ / @$): "" is an escaped quote, newlines allowed.
        private static int Verbatim(string code, int i, List<SyntaxSpan> spans)
        {
            int s = i;
            i = code.IndexOf('"', i) + 1;
            while (i < code.Length)
            {
                if (code[i] == '"') { if (i + 1 < code.Length && code[i + 1] == '"') { i += 2; continue; } i++; break; }
                i++;
            }
            spans.Add(new SyntaxSpan { Start = s, Length = i - s, Kind = SyntaxKind.String });
            return i;
        }
    }
}
