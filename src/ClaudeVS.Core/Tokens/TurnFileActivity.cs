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

        public void Reset()
        {
            _pendingReads.Clear(); _shellCommands.Clear(); _quietAsked.Clear();
            ReadTokensByFile.Clear(); QuietRuns.Clear(); QuietTokens = 0;
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
                            ReadTokensByFile.TryGetValue(path, out var t);
                            ReadTokensByFile[path] = t + TokenEstimate.FromChars(ev.ResultChars);
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

        /// <summary>Where <see cref="ByFolder"/> puts reads outside the working folder (their paths are not logged).</summary>
        public const string OutsideFolder = "(outside the working folder)";

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
                {
                    var parts = path.Substring(full.Length).Split('/');
                    folder = parts.Length <= 1 ? "." : string.Join("/", parts.Take(Math.Min(depth, parts.Length - 1)));
                }
                else if (full == null && !Path.IsPathRooted(path))
                {
                    var parts = path.Split('/');
                    folder = parts.Length <= 1 ? "." : string.Join("/", parts.Take(Math.Min(depth, parts.Length - 1)));
                }
                else folder = OutsideFolder;
                result.TryGetValue(folder, out var t);
                result[folder] = t + kv.Value;
            }
            return result;
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
