using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeVS.Core.Context;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    /// <summary>The ⎿ line(s) under a tool row: a summary or the first lines of the output.</summary>
    public sealed class ToolResultSummary
    {
        public IReadOnlyList<string> Lines { get; set; } = Array.Empty<string>();
        /// <summary>Output lines not shown ("… +12 lines").</summary>
        public int MoreLines { get; set; }
        public bool IsError { get; set; }
    }

    /// <summary>
    /// Tool calls the way the Claude Code terminal shows them:
    ///   ● Update(src/Samples/WindowSamples.cs)
    ///     ⎿  Added 13 lines, removed 2 lines
    ///   ● Bash(dotnet test)
    ///     ⎿  Passed!  - Failed: 0, Passed: 903
    ///        … +4 lines
    /// </summary>
    public static class ToolCallView
    {
        public const int PreviewLines = 3;
        private const int MaxLineChars = 160;

        /// <summary>Name and argument: ("Update", "src/a.cs"), ("Bash", "dotnet test"), ("Search", "pattern: \"Foo\"").</summary>
        public static (string Name, string Arg) Title(string toolName, JObject input, string baseDir = null)
        {
            input = input ?? new JObject();
            string Arg(string k) => input[k]?.Type == JTokenType.String ? ((string)input[k]).Trim() : null;
            string Path(string k) => Relative(Arg(k), baseDir);
            switch (toolName)
            {
                case "Edit":
                case "MultiEdit": return ("Update", Path("file_path"));
                case "Write": return ("Write", Path("file_path"));
                case "Read": return ("Read", Path("file_path"));
                case "NotebookEdit": return ("Update", Path("notebook_path"));
                case "Bash":
                case "PowerShell": return (toolName, OneLine(WithoutCdToBase(Arg("command"), baseDir)));
                case "Grep": return ("Search", $"pattern: \"{Arg("pattern")}\"" + (Arg("path") != null ? ", path: " + Path("path") : ""));
                case "Glob": return ("Search", $"pattern: \"{Arg("pattern")}\"" + (Arg("path") != null ? ", path: " + Path("path") : ""));
                case "WebFetch": return ("Fetch", Arg("url"));
                case "WebSearch": return ("Web Search", Arg("query"));
                case "Task":
                case "Agent": return (AgentLabel(Arg("subagent_type")), Arg("description"));
                case "TodoWrite": return ("Update Todos", null);
                default:
                    if (toolName != null && toolName.StartsWith("mcp__", StringComparison.Ordinal))
                    {
                        var parts = toolName.Substring(5).Split(new[] { "__" }, StringSplitOptions.RemoveEmptyEntries);
                        string friendly = IdeMcpServer.DisplayName(toolName);
                        if (friendly != null) return (friendly, Arg("file"));
                        return (parts.Length == 2 ? $"{parts[0]} - {parts[1]} (MCP)" : toolName, null);
                    }
                    string described = ClaudeStreamParser.DescribeTool(toolName, input);
                    int colon = described?.IndexOf(": ", StringComparison.Ordinal) ?? -1;
                    return (toolName, colon > 0 ? described.Substring(colon + 2) : null);
            }
        }

        /// <summary>Tools whose back-to-back calls fold into one "Read 3 files, searched for 2 patterns" row.</summary>
        public static bool IsLookTool(string toolName) => toolName == "Read" || toolName == "Grep" || toolName == "Glob";

        /// <summary>
        /// The folded row's text, like the terminal: "Read 3 files, searched for 2 patterns, ran 1 read-only
        /// command", or while one is still running "Reading 3 files, searching for 2 patterns…".
        /// </summary>
        public static string LookSummary(int reads, int searches, bool running, int commands = 0)
        {
            var parts = new List<string>();
            if (reads > 0) parts.Add((running ? "reading " : "read ") + Count(reads, "file"));
            if (searches > 0) parts.Add((running ? "searching for " : "searched for ") + Count(searches, "pattern"));
            if (commands > 0) parts.Add((running ? "running " : "ran ") + Count(commands, "read-only command"));
            if (parts.Count == 0) return "";
            string s = string.Join(", ", parts);
            return char.ToUpperInvariant(s[0]) + s.Substring(1) + (running ? "…" : "");
        }

        private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

        // Programs that only read. git and find are checked further below; sed only without -i; awk, xargs, env
        // and anything else that can run another program are left out on purpose.
        private static readonly HashSet<string> ReadOnlyPrograms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cat", "head", "tail", "ls", "dir", "grep", "egrep", "rg", "wc", "sed", "sort", "uniq", "cut", "tr", "nl",
            "pwd", "echo", "tree", "file", "stat", "du", "which", "basename", "dirname", "realpath", "cd", "find", "git",
            "type", "Get-Content", "gc", "Select-String", "sls", "Get-ChildItem", "gci", "Get-Item", "Get-Location",
            "Resolve-Path", "Test-Path", "Measure-Object", "Select-Object", "Sort-Object", "Format-Table",
        };
        private static readonly HashSet<string> ReadOnlyGit = new HashSet<string>(StringComparer.Ordinal)
        {
            "ls-files", "log", "status", "diff", "show", "grep", "rev-parse", "blame", "shortlog", "describe", "ls-tree", "cat-file",
        };
        private static readonly HashSet<string> ReadOnlyGitBranchFlags = new HashSet<string>(StringComparer.Ordinal)
        {
            "-a", "-r", "-v", "-vv", "--all", "--list", "--show-current", "--remotes", "--no-color",
        };

        /// <summary>
        /// True when every part of a shell command only reads: <c>cat</c>, <c>head</c>, <c>sed -n</c>, <c>grep</c>,
        /// <c>git log</c>… joined by <c>&amp;&amp;</c>, <c>;</c> or <c>|</c>. No redirect into a file (only
        /// <c>2&gt;&amp;1</c> and <c>/dev/null</c>), no <c>$( )</c> or backticks, no heredoc, no variable assignment,
        /// no <c>sed -i</c> or <c>find -exec</c>. When unsure it says no, so the command keeps its own row.
        /// </summary>
        public static bool IsReadOnlyCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command) || command.IndexOf('\n') >= 0 || command.Length > 2000) return false;
            var segments = new List<List<string>>();
            var words = new List<string>();
            var word = new System.Text.StringBuilder();
            bool inWord = false;
            char quote = '\0';
            void EndWord() { if (inWord) { words.Add(word.ToString()); word.Clear(); inWord = false; } }
            void EndSegment() { EndWord(); if (words.Count > 0) { segments.Add(words); words = new List<string>(); } }

            for (int i = 0; i < command.Length; i++)
            {
                char c = command[i];
                if (quote == '\'') { if (c == '\'') quote = '\0'; else word.Append(c); continue; }
                if (c == '`' || (c == '$' && i + 1 < command.Length && command[i + 1] == '(')) return false; // runs a command
                if (quote == '"') { if (c == '"') quote = '\0'; else word.Append(c); continue; }
                switch (c)
                {
                    case '\'': case '"': quote = c; inWord = true; break;
                    case ' ': case '\t': EndWord(); break;
                    case ';': EndSegment(); break;
                    case '|': EndSegment(); if (i + 1 < command.Length && command[i + 1] == '|') i++; break;
                    case '&':
                        if (i + 1 < command.Length && command[i + 1] == '&') { EndSegment(); i++; break; }
                        if (i + 1 < command.Length && command[i + 1] == '>') break; // &>/dev/null, checked at '>'
                        return false; // a background job
                    case '<': return false; // heredoc or input redirect
                    case '(': case ')': case '{': case '}': return false; // subshells and blocks
                    case '>':
                    {
                        // Only into nothing: 2>&1, >/dev/null, 2> $null.
                        if (inWord && word.ToString() is string fd && (fd == "1" || fd == "2")) { word.Clear(); inWord = false; }
                        int j = i + 1;
                        if (j < command.Length && command[j] == '>') j++;
                        if (j + 1 < command.Length && command[j] == '&' && char.IsDigit(command[j + 1])) { i = j + 1; break; }
                        while (j < command.Length && command[j] == ' ') j++;
                        int start = j;
                        while (j < command.Length && command[j] != ' ' && command[j] != ';' && command[j] != '|' && command[j] != '&') j++;
                        string target = command.Substring(start, j - start);
                        if (!target.Equals("/dev/null", StringComparison.Ordinal) && !target.Equals("$null", StringComparison.OrdinalIgnoreCase) && !target.Equals("nul", StringComparison.OrdinalIgnoreCase)) return false;
                        i = j - 1;
                        break;
                    }
                    default: word.Append(c); inWord = true; break;
                }
            }
            if (quote != '\0') return false;
            EndSegment();
            if (segments.Count == 0) return false;
            return segments.All(IsReadOnlySegment);
        }

        private static bool IsReadOnlySegment(List<string> w)
        {
            string program = w[0];
            if (program.IndexOf('=') >= 0 || !ReadOnlyPrograms.Contains(program)) return false;
            var args = w.Skip(1).ToList();
            switch (program.ToLowerInvariant())
            {
                case "sed":
                    return !args.Any(a => a.StartsWith("-i", StringComparison.Ordinal) || a.StartsWith("--in-place", StringComparison.Ordinal)
                                          || (a.StartsWith("-", StringComparison.Ordinal) && !a.StartsWith("--", StringComparison.Ordinal) && a.IndexOf('i') > 0));
                case "find":
                    return !args.Any(a => a == "-exec" || a == "-execdir" || a == "-ok" || a == "-okdir" || a == "-delete" || a.StartsWith("-fprint", StringComparison.Ordinal) || a == "-fls");
                case "git":
                {
                    // Skip global options like -C dir, --no-pager.
                    int k = 0;
                    while (k < args.Count && args[k].StartsWith("-", StringComparison.Ordinal)) k += args[k] == "-C" || args[k] == "-c" ? 2 : 1;
                    if (k >= args.Count) return false;
                    string sub = args[k];
                    var rest = args.Skip(k + 1).ToList();
                    if (sub == "branch") return rest.All(ReadOnlyGitBranchFlags.Contains);
                    if (!ReadOnlyGit.Contains(sub)) return false;
                    return !rest.Any(a => a.StartsWith("--output", StringComparison.Ordinal));
                }
                default:
                    return true;
            }
        }

        /// <summary>
        /// The muted line under a shell command when Claude set how it runs: "(timeout 10m)", "(running in the
        /// background)", or both. Null for the defaults.
        /// </summary>
        public static string RunNote(string toolName, JObject input)
        {
            if (input == null || (toolName != "Bash" && toolName != "PowerShell")) return null;
            var parts = new List<string>();
            var t = input["timeout"];
            if (t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) && (double)t > 0)
                parts.Add("timeout " + Duration(TimeSpan.FromMilliseconds((double)t)));
            if (input["run_in_background"]?.Type == JTokenType.Boolean && (bool)input["run_in_background"])
                parts.Add("running in the background");
            return parts.Count == 0 ? null : "(" + string.Join(", ", parts) + ")";
        }

        /// <summary>"10m", "1h 30m", "45s", "2m 30s": the largest units, no zero parts.</summary>
        public static string Duration(TimeSpan d)
        {
            if (d.TotalSeconds < 60) return Math.Max(1, (int)Math.Round(d.TotalSeconds)) + "s";
            int h = (int)d.TotalHours, m = d.Minutes, s = d.Seconds;
            if (h > 0) return m > 0 ? $"{h}h {m}m" : $"{h}h";
            return s > 0 ? $"{m}m {s}s" : $"{m}m";
        }

        /// <summary>
        /// The tool row's name for an Agent call: the subagent type as the CLI names it ("general-purpose",
        /// "Explore"), "Fork" for a fork of this conversation (on by default in <c>-p</c> since 2.1.302), "Agent" when none.
        /// </summary>
        public static string AgentLabel(string subagentType) =>
            string.IsNullOrEmpty(subagentType) ? "Agent" : subagentType == ForkType ? "Fork" : subagentType;

        /// <summary>The agents panel's gray tag: "fork of this chat" for a fork, otherwise the subagent type.</summary>
        public static string AgentKind(string subagentType) =>
            string.IsNullOrEmpty(subagentType) ? "agent" : subagentType == ForkType ? "fork of this chat" : subagentType;

        /// <summary>A fork subagent: starts with a copy of the conversation so far.</summary>
        public const string ForkType = "fork";

        /// <summary>What the ⎿ line says once the result is in.</summary>
        public static ToolResultSummary Summarize(string toolName, string resultText, bool isError, InlineDiff diff = null)
        {
            var output = SplitOutput(resultText);
            if (isError)
                return new ToolResultSummary { IsError = true, Lines = output.Take(PreviewLines).Select(Cut).DefaultIfEmpty("Error").ToList(), MoreLines = Math.Max(0, output.Count - PreviewLines) };

            switch (toolName)
            {
                case "Edit":
                case "MultiEdit":
                case "Write":
                case "NotebookEdit":
                    if (diff != null && toolName == "Write" && diff.IsNewFile) return One($"Wrote {Lines(diff.Added)} to {FileName(diff.FilePath)}");
                    if (diff != null) return One(Changes(diff.Added, diff.Removed));
                    return One(output.FirstOrDefault() ?? "Done");
                case "Read":
                    return One(output.Count == 0 ? "Read 0 lines" : $"Read {Lines(output.Count)}");
                case "Grep":
                case "Glob":
                    if (output.Count == 0 || (output.Count == 1 && output[0].StartsWith("No ", StringComparison.Ordinal))) return One(output.FirstOrDefault() ?? "No matches");
                    return One($"Found {output.Count} {(output.Count == 1 ? "line" : "lines")}");
                case "TodoWrite":
                    return One("Todos updated");
                case "Task":
                case "Agent":
                    // A background agent's result is internal metadata Claude is told never to quote (2.1.319).
                    if (output.Count > 0 && output[0].StartsWith("Async agent launched", StringComparison.Ordinal)) return One("Running in the background");
                    break;
                case "Bash":
                case "PowerShell":
                    if (diff != null && output.Count == 0) return One((diff.IsAppend ? "Appended " : "Wrote ") + Lines(diff.Added) + " to " + FileName(diff.FilePath));
                    if (output.Count == 0) return One("(No output)");
                    break;
            }
            if (output.Count == 0) return One("Done");
            return new ToolResultSummary { Lines = output.Take(PreviewLines).Select(Cut).ToList(), MoreLines = Math.Max(0, output.Count - PreviewLines) };
        }

        public static string Changes(int added, int removed)
        {
            if (added > 0 && removed > 0) return $"Added {Lines(added)}, removed {Lines(removed)}";
            if (added > 0) return $"Added {Lines(added)}";
            if (removed > 0) return $"Removed {Lines(removed)}";
            return "No changes";
        }

        private static ToolResultSummary One(string line) => new ToolResultSummary { Lines = new[] { line } };
        private static string Lines(int n) => n == 1 ? "1 line" : $"{n} lines";
        private static string FileName(string path) => string.IsNullOrEmpty(path) ? "the file" : System.IO.Path.GetFileName(path.TrimEnd('/', '\\'));
        private static string Cut(string s) => s.Length > MaxLineChars ? s.Substring(0, MaxLineChars - 1) + "…" : s;

        private static List<string> SplitOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
            // Claude Code wraps some tool output in <system-reminder> blocks; they are not the tool's output.
            int reminder = lines.FindIndex(l => l.TrimStart().StartsWith("<system-reminder>", StringComparison.Ordinal));
            if (reminder >= 0) lines = lines.Take(reminder).ToList();
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        private static string OneLine(string s)
        {
            if (s == null) return null;
            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            return nl < 0 ? s : s.Substring(0, nl).TrimEnd() + " …";
        }

        private static readonly System.Text.RegularExpressions.Regex CdPrefix = new System.Text.RegularExpressions.Regex(
            @"^\s*(?:cd|Set-Location|pushd)\s+(?:""(?<d>[^""]+)""|'(?<d>[^']+)'|(?<d>[^\s;&|]+))\s*(?:&&|;)\s*",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// A command without a leading <c>cd "&lt;working folder&gt;" &amp;&amp;</c> (or <c>;</c>): Claude starts most
        /// commands that way, and the folder is already shown under the title. A cd anywhere else stays, since
        /// it changes what the command does. Git Bash paths (<c>/c/Users/…</c>) match their Windows form.
        /// </summary>
        public static string WithoutCdToBase(string command, string baseDir)
        {
            if (string.IsNullOrEmpty(command) || string.IsNullOrEmpty(baseDir)) return command;
            var m = CdPrefix.Match(command);
            if (!m.Success || m.Length >= command.Length) return command;
            return SameDir(m.Groups["d"].Value, baseDir) ? command.Substring(m.Length) : command;
        }

        private static bool SameDir(string a, string b)
        {
            string Norm(string p)
            {
                p = p.Replace('\\', '/').TrimEnd('/');
                if (p.Length >= 3 && p[0] == '/' && char.IsLetter(p[1]) && p[2] == '/') p = p[1] + ":" + p.Substring(2); // /c/x → c:/x
                else if (p.Length == 2 && p[0] == '/' && char.IsLetter(p[1])) p = p[1] + ":";
                return p;
            }
            return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A path inside <paramref name="baseDir"/> without that prefix (either slash style); others unchanged.</summary>
        public static string Relative(string path, string baseDir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(baseDir)) return path;
            string p = path.Replace('\\', '/');
            string b = baseDir.Replace('\\', '/').TrimEnd('/') + "/";
            return p.StartsWith(b, StringComparison.OrdinalIgnoreCase) && p.Length > b.Length ? p.Substring(b.Length) : path;
        }
    }
}
