using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudeVS.Core.Cli;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// What one turn read and ran, for the usage log: the tokens each file's reads brought into the context (the main
    /// agent's and its subagents', since every read is paid), and the shell commands the token-saving hook made quiet.
    /// </summary>
    public sealed class TurnFileActivity
    {
        /// <summary>The reason the token-saving hook gives when it asks (its <c>permissionDecisionReason</c>).</summary>
        public const string QuietReasonPrefix = "Quiet output to save tokens:";

        private readonly Dictionary<string, string> _pendingReads = new Dictionary<string, string>(StringComparer.Ordinal);    // tool_use_id → path
        private readonly Dictionary<string, string> _shellCommands = new Dictionary<string, string>(StringComparer.Ordinal);   // tool_use_id → Claude's command
        private readonly HashSet<string> _quietAsked = new HashSet<string>(StringComparer.Ordinal);                            // asked with the hook's reason

        /// <summary>Tokens brought in by reads, by full path (as Claude named it).</summary>
        public Dictionary<string, long> ReadTokensByFile { get; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Commands the hook made quiet, by kind ("dotnet build", "git log"…).</summary>
        public Dictionary<string, int> QuietRuns { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>Tokens those quiet commands returned.</summary>
        public long QuietTokens { get; private set; }

        /// <summary>A build, test or git log that ran loud and brought this many tokens or more is counted (<see cref="LoudRuns"/>).</summary>
        public const int LoudTokensFrom = 2000;
        /// <summary>Builds, tests and git logs that ran in full (a form the hook leaves alone, or no hook) with 2k+ tokens, by kind.</summary>
        public Dictionary<string, int> LoudRuns { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>Tokens those loud commands brought in.</summary>
        public long LoudTokens { get; private set; }
        /// <summary>The last loud command seen, for the tip: its call, Claude's command, its kind and tokens.</summary>
        public (string ToolUseId, string Command, string Kind, long Tokens)? LastLoud { get; private set; }

        /// <summary>Files the main agent read in this conversation (since its last compaction); kept across turns.</summary>
        private readonly HashSet<string> _mainRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// This turn's tokens of subagents reading files the main agent had already read in the conversation: a subagent
        /// starts without that context, so it reads them again.
        /// </summary>
        public long SubagentRereadTokens { get; private set; }

        /// <summary>
        /// The last subagent re-read: the Read call's id (to match the event that caused it), the subagent's Agent call,
        /// the file and its tokens. Null until one happens in the turn.
        /// </summary>
        public (string ReadToolUseId, string AgentToolUseId, string Path, long Tokens)? LastReread { get; private set; }

        /// <summary>A new conversation: forgets what the main agent read, and the turn.</summary>
        public void ResetConversation() { _mainRead.Clear(); Reset(); }

        public void Reset()
        {
            SubagentRereadTokens = 0;
            LastReread = null;
            _pendingReads.Clear(); _shellCommands.Clear(); _quietAsked.Clear();
            ReadTokensByFile.Clear(); QuietRuns.Clear(); QuietTokens = 0;
            LoudRuns.Clear(); LoudTokens = 0; LastLoud = null;
        }

        public void Observe(ClaudeEvent ev)
        {
            switch (ev.Kind)
            {
                case ClaudeEventKind.ToolUse when ev.ToolUseId != null:
                    if (ev.ToolName == "Read" && ev.ToolInput?["file_path"]?.Type == Newtonsoft.Json.Linq.JTokenType.String)
                        _pendingReads[ev.ToolUseId] = (string)ev.ToolInput["file_path"];
                    else if ((ev.ToolName == "Bash" || ev.ToolName == "PowerShell") && ev.ToolInput?["command"]?.Type == Newtonsoft.Json.Linq.JTokenType.String)
                        _shellCommands[ev.ToolUseId] = (string)ev.ToolInput["command"];
                    break;
                case ClaudeEventKind.Compacted:
                    _mainRead.Clear(); // the summary replaced what the main agent read
                    break;
                case ClaudeEventKind.PermissionRequest when ev.ToolUseId != null:
                    // The hook rewrote it and asks (its default): the request says so in its reason (verified with 2.1.327).
                    string reason = ev.Payload?["decision_reason"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)ev.Payload["decision_reason"] : null;
                    if (reason != null && reason.StartsWith(QuietReasonPrefix, StringComparison.Ordinal)) _quietAsked.Add(ev.ToolUseId);
                    break;
                case ClaudeEventKind.ToolResult when ev.ToolUseId != null:
                    if (_pendingReads.TryGetValue(ev.ToolUseId, out var path))
                    {
                        _pendingReads.Remove(ev.ToolUseId);
                        if (!ev.IsError && ev.ResultChars > 0)
                        {
                            int tokens = TokenEstimate.FromChars(ev.ResultChars);
                            ReadTokensByFile.TryGetValue(path, out var t);
                            ReadTokensByFile[path] = t + tokens;
                            if (!ev.IsSubagent) _mainRead.Add(path);
                            else if (_mainRead.Contains(path))
                            {
                                SubagentRereadTokens += tokens;
                                LastReread = (ev.ToolUseId, ev.ParentToolUseId, path, tokens);
                            }
                        }
                    }
                    else if (_shellCommands.TryGetValue(ev.ToolUseId, out var command))
                    {
                        _shellCommands.Remove(ev.ToolUseId);
                        bool asked = _quietAsked.Remove(ev.ToolUseId);
                        string kind = QuietKind(command);
                        // Rewritten when the hook asked with its reason, or allowed it (auto-approve) and the command is one it rewrites
                        bool quiet = kind != null && (asked || ev.AllowedBy == ClaudeStreamParser.HookDecision);
                        bool refused = ev.IsError && (ev.ResultText ?? "").IndexOf("doesn't want to proceed", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (quiet && !refused && ev.DeniedBy == null)
                        {
                            QuietRuns.TryGetValue(kind, out var n);
                            QuietRuns[kind] = n + 1;
                            QuietTokens += TokenEstimate.FromChars(ev.ResultChars);
                        }
                        else if (!quiet && !refused && ev.DeniedBy == null && BaseKind(command) is string loud)
                        {
                            long tokens = TokenEstimate.FromChars(ev.ResultChars);
                            if (tokens >= LoudTokensFrom)
                            {
                                LoudRuns.TryGetValue(loud, out var n);
                                LoudRuns[loud] = n + 1;
                                LoudTokens += tokens;
                                LastLoud = (ev.ToolUseId, command, loud, tokens);
                            }
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// The kind of command the token-saving hook rewrites ("dotnet build", "dotnet test", "msbuild", "git log",
        /// "npm test"), by the same rules as its script; null for any other, and for ones it leaves alone (pipes,
        /// chains, a verbosity already given, git log with a count).
        /// </summary>
        public static string QuietKind(string command)
        {
            string c = command ?? "";
            if (Regex.IsMatch(c, "[|;&]") || Regex.IsMatch(c, @"(\s-v(erbosity)?[\s:=])|(--verbosity)|(-clp:)", RegexOptions.IgnoreCase)) return null;
            var m = Regex.Match(c, @"^\s*dotnet\s+(build|publish|pack|restore|clean)\b", RegexOptions.IgnoreCase);
            if (m.Success) return "dotnet " + m.Groups[1].Value.ToLowerInvariant();
            if (Regex.IsMatch(c, @"^\s*dotnet\s+test\b", RegexOptions.IgnoreCase)) return "dotnet test";
            if (Regex.IsMatch(c, @"^\s*msbuild(\.exe)?\b", RegexOptions.IgnoreCase)) return "msbuild";
            if (Regex.IsMatch(c, @"^\s*git\s+log\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(c, @"(\s-n\s*\d)|(--max-count)|(\s-\d+)", RegexOptions.IgnoreCase)) return "git log";
            if (Regex.IsMatch(c, @"^\s*(npm|npx)\s+(run\s+)?test\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(c, "watch", RegexOptions.IgnoreCase)) return "npm test";
            return null;
        }

        /// <summary>Where <see cref="Capped"/> adds up the folders past the largest ones.</summary>
        public const string OtherFolders = "(other)";

        /// <summary>
        /// The <paramref name="max"/> largest folders, and the rest added up as <see cref="OtherFolders"/>, so a turn that
        /// reads all over a large repository writes a short usage line. Null for null.
        /// </summary>
        public static Dictionary<string, long> Capped(IReadOnlyDictionary<string, long> folders, int max = 10)
        {
            if (folders == null) return null;
            var sorted = folders.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in sorted.Take(max)) result[kv.Key] = kv.Value;
            long rest = sorted.Skip(max).Sum(kv => kv.Value);
            if (rest > 0) { result.TryGetValue(OtherFolders, out var t); result[OtherFolders] = t + rest; }
            return result;
        }

        /// <summary>Where <see cref="ByFolder"/> puts reads outside the working folder (their paths are not logged).</summary>
        public const string OutsideFolder = "(outside the working folder)";

        /// <summary>
        /// The kind of build, test or git log a command starts with, whatever its flags, pipes or chains ("dotnet build
        /// -v d | more" → "dotnet build"); null for other commands.
        /// </summary>
        public static string BaseKind(string command)
        {
            string first = Regex.Split(command ?? "", "[|;&]")[0];
            var m = Regex.Match(first, @"^\s*dotnet\s+(build|publish|pack|restore|clean)\b", RegexOptions.IgnoreCase);
            if (m.Success) return "dotnet " + m.Groups[1].Value.ToLowerInvariant();
            if (Regex.IsMatch(first, @"^\s*dotnet\s+test\b", RegexOptions.IgnoreCase)) return "dotnet test";
            if (Regex.IsMatch(first, @"^\s*msbuild(\.exe)?\b", RegexOptions.IgnoreCase)) return "msbuild";
            if (Regex.IsMatch(first, @"^\s*git\s+log\b", RegexOptions.IgnoreCase)) return "git log";
            if (Regex.IsMatch(first, @"^\s*(npm|npx)\s+(run\s+)?test\b", RegexOptions.IgnoreCase)) return "npm test";
            return null;
        }

        /// <summary>
        /// The tip after a loud build, test or git log: what it brought, why the hook left it alone, and the form that
        /// runs quiet. <paramref name="hookInstalled"/> false: suggests installing the hook instead.
        /// </summary>
        public static string LoudTip(string command, string kind, long tokens, bool hookInstalled)
        {
            string size = $"`{Shorten(command)}` put ~{TokenMeter.Format(tokens)} tokens of output into the context.";
            if (!hookInstalled)
                return size + $" Tools → Claude Code → Install Token-Saving Settings and Hooks runs {kind} and the like quietly.";
            string why = Regex.IsMatch(command ?? "", "[|;&]") ? "it was piped or chained"
                : kind == "git log" && Regex.IsMatch(command ?? "", @"(\s-n\s*\d)|(--max-count)|(\s-\d+)") ? "it named a count"
                : Regex.IsMatch(command ?? "", @"(\s-v(erbosity)?[\s:=])|(--verbosity)|(-clp:)", RegexOptions.IgnoreCase) ? "it named a verbosity"
                : kind == "npm test" ? "it runs in watch mode"
                : null;
            return size + (why == null
                ? $" It ran as written, not through the quiet-build hook; ask Claude for the plain `{kind}` and it runs quiet."
                : $" The quiet-build hook left it alone because {why}; ask Claude for the plain `{kind}` and it runs quiet.");
        }

        /// <summary>What the loud-command tip's link puts in the input box: asking for the plain command.</summary>
        public static string PlainCommandRequest(string kind) =>
            $"Run the plain `{kind}` instead, with no verbosity, pipe or count, so the quiet-build hook can make it quiet.";

        private static string Shorten(string s)
        {
            s = (s ?? "").Replace('\n', ' ').Trim();
            return s.Length <= 60 ? s : s.Substring(0, 57) + "…";
        }

        /// <summary>
        /// Read tokens by folder: the first <paramref name="depth"/> folders of each file below <paramref name="root"/>
        /// ("src/ClaudeVS.Core"), "." for files directly in it, <see cref="OutsideFolder"/> for files outside it.
        /// </summary>
        public static Dictionary<string, long> ByFolder(IReadOnlyDictionary<string, long> byFile, string root, int depth = 2)
        {
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            string full = string.IsNullOrEmpty(root) ? null : root.Replace('\\', '/').TrimEnd('/') + "/";
            foreach (var kv in byFile ?? new Dictionary<string, long>())
            {
                string path = (kv.Key ?? "").Replace('\\', '/');
                string folder;
                if (full != null && path.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                    folder = Group(path.Substring(full.Length).Split('/'), depth);
                else if (full == null && !Path.IsPathRooted(path))
                    folder = Group(path.Split('/'), depth);
                else folder = OutsideFolder;
                result.TryGetValue(folder, out var t);
                result[folder] = t + kv.Value;
            }
            return result;
        }

        /// <summary>
        /// A file's folder for <see cref="ByFolder"/> (<paramref name="parts"/>: its path's segments, the name last): the
        /// first <paramref name="depth"/> folders, or down to a build-output or vendored folder at any depth
        /// ("src/App/obj"), so it is told apart from the code around it.
        /// </summary>
        private static string Group(string[] parts, int depth)
        {
            if (parts.Length <= 1) return ".";
            int dirs = parts.Length - 1;
            for (int k = 1; k <= dirs; k++)
            {
                string prefix = string.Join("/", parts.Take(k));
                // the noise folder ends here: "obj", or the second part of "wwwroot/lib"
                if (SavingsReport.NoiseRule(parts[k - 1]) != null || (k >= 2 && SavingsReport.NoiseRule(parts[k - 2] + "/" + parts[k - 1]) != null && SavingsReport.NoiseRule(parts[k - 2]) == null))
                    return prefix;
            }
            return string.Join("/", parts.Take(Math.Min(depth, dirs)));
        }

        /// <summary>
        /// Of the files read this turn, those inside the attached references (a file, or anything under a folder):
        /// how many and their tokens.
        /// </summary>
        public (int Files, long Tokens) ReadUnder(IEnumerable<string> references)
        {
            var refs = (references ?? Enumerable.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Replace('\\', '/').TrimEnd('/')).ToList();
            int files = 0; long tokens = 0;
            foreach (var kv in ReadTokensByFile)
            {
                string p = kv.Key.Replace('\\', '/');
                if (refs.Any(r => p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase)))
                { files++; tokens += kv.Value; }
            }
            return (files, tokens);
        }
    }
}
