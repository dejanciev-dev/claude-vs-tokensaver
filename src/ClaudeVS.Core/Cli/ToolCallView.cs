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
                case "Agent": return (Arg("subagent_type") ?? "Agent", Arg("description"));
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
