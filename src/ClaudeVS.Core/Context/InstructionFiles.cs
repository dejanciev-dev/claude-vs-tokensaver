using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClaudeVS.Core.Context
{
    /// <summary>
    /// The project files Claude Code reads only when it starts: <c>CLAUDE.md</c>, <c>.claude/CLAUDE.md</c>,
    /// <c>CLAUDE.local.md</c> and <c>.mcp.json</c>. Probed with 2.1.329 (`--live-restart`): a <c>CLAUDE.md</c> written
    /// mid-conversation reached Claude only after a restart. <c>.claude/settings*.json</c> are not here: their rules and
    /// hooks apply at once (Phase 76), and the extension restarts in place for what doesn't.
    /// </summary>
    public static class InstructionFiles
    {
        /// <summary>Relative to the folder Claude Code runs in.</summary>
        public static readonly string[] Names = { "CLAUDE.md", Path.Combine(".claude", "CLAUDE.md"), "CLAUDE.local.md", ".mcp.json" };

        /// <summary>
        /// Each file's last write time (UTC), or null when it doesn't exist. Never throws. With <paramref name="home"/>,
        /// your own <c>~/.claude/CLAUDE.md</c> too: Claude Code reads it at the start as well (Phase 79).
        /// </summary>
        public static Dictionary<string, DateTime?> Snapshot(string root, string home = null)
        {
            var d = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(root)) return d;
            var paths = Names.Select(n => Path.Combine(root, n)).ToList();
            if (!string.IsNullOrEmpty(home)) paths.Add(Path.Combine(home, ".claude", "CLAUDE.md"));
            // The files the CLAUDE.md files import, which Claude Code reads with them (Phase 81)
            foreach (var md in paths.Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).ToList())
                foreach (var imported in Imports(md, home))
                    if (!paths.Contains(imported, StringComparer.OrdinalIgnoreCase)) paths.Add(imported);
            foreach (var path in paths)
            {
                try { d[path] = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) { d[path] = null; }
            }
            return d;
        }

        /// <summary>
        /// The same files' write times again, without reading them for imports: what the chat checks on focus. A new
        /// import changes its CLAUDE.md, which is noticed anyway.
        /// </summary>
        public static Dictionary<string, DateTime?> Refresh(IEnumerable<string> paths)
        {
            var d = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths ?? Enumerable.Empty<string>())
            {
                try { d[path] = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) { d[path] = null; }
            }
            return d;
        }

        /// <summary>Instructions (a CLAUDE.md or a file it imports) go with every request; .mcp.json doesn't.</summary>
        private static bool IsInstructions(string path) => !path.EndsWith(".mcp.json", StringComparison.OrdinalIgnoreCase);

        /// <summary>How deep imports of imports go, as Claude Code documents it.</summary>
        public const int MaxImportDepth = 5;

        private static readonly System.Text.RegularExpressions.Regex Fences = new System.Text.RegularExpressions.Regex(@"^\s*(```|~~~).*?^\s*\1[^\n]*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Singleline);
        private static readonly System.Text.RegularExpressions.Regex InlineCode = new System.Text.RegularExpressions.Regex(@"`[^`\n]*`");
        private static readonly System.Text.RegularExpressions.Regex Import = new System.Text.RegularExpressions.Regex(@"(?<=^|\s)@((?:~[\\/])?[\w.\-][^\s`)\]]*)", System.Text.RegularExpressions.RegexOptions.Multiline);

        /// <summary>
        /// The files <paramref name="mdPath"/> imports (<c>@docs/rules.md</c>, <c>@~/.claude/mine.md</c>, <c>@README</c>),
        /// and theirs, up to <see cref="MaxImportDepth"/> deep: <c>@path</c> starting a word, outside code blocks and
        /// inline code; relative to the importing file's folder, <c>~/</c> to <paramref name="home"/>. Only files that
        /// exist; no file twice. Never throws.
        /// </summary>
        public static List<string> Imports(string mdPath, string home = null)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mdPath ?? "" };
            void Walk(string file, int depth)
            {
                if (depth > MaxImportDepth) return;
                string text;
                try { if (!File.Exists(file) || new FileInfo(file).Length > 1_000_000) return; text = File.ReadAllText(file); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) { return; }
                text = InlineCode.Replace(Fences.Replace(text, ""), "");
                foreach (System.Text.RegularExpressions.Match m in Import.Matches(text))
                {
                    string target = m.Groups[1].Value.TrimEnd('.', ',', ';', ':');
                    string full;
                    try
                    {
                        full = target.StartsWith("~/", StringComparison.Ordinal) || target.StartsWith("~\\", StringComparison.Ordinal)
                            ? (string.IsNullOrEmpty(home) ? null : Path.GetFullPath(Path.Combine(home, target.Substring(2))))
                            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file) ?? "", target));
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { continue; }
                    if (full == null || !seen.Add(full) || !File.Exists(full)) continue;
                    found.Add(full);
                    Walk(full, depth + 1);
                }
            }
            if (!string.IsNullOrEmpty(mdPath)) Walk(mdPath, 1);
            return found;
        }

        /// <summary>
        /// The files created, changed or deleted between two snapshots, as full paths, leaving out
        /// <paramref name="writtenByClaude"/> (Claude knows what it wrote).
        /// </summary>
        public static List<string> Changed(IReadOnlyDictionary<string, DateTime?> before, IReadOnlyDictionary<string, DateTime?> now, ICollection<string> writtenByClaude = null)
        {
            var changed = new List<string>();
            if (before == null || now == null) return changed;
            foreach (var kv in now)
            {
                before.TryGetValue(kv.Key, out var was);
                if (was == kv.Value) continue;
                if (writtenByClaude != null && writtenByClaude.Any(w => string.Equals(w, kv.Key, StringComparison.OrdinalIgnoreCase))) continue;
                changed.Add(kv.Key);
            }
            return changed;
        }

        /// <summary>The instruction files' sizes (bytes; 0 when missing), the same paths as <see cref="Snapshot"/>. Never throws.</summary>
        public static Dictionary<string, long> Sizes(string root, string home = null)
        {
            var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Snapshot(root, home).Keys.Where(IsInstructions))
            {
                try { d[path] = File.Exists(path) ? new FileInfo(path).Length : 0; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) { d[path] = 0; }
            }
            return d;
        }

        /// <summary>
        /// How many tokens the changed instruction files add to every request (negative: take away), from their sizes
        /// then and now. Null when under 50 either way, or no instruction file changed.
        /// </summary>
        public static long? TokensPerRequest(IEnumerable<string> changedPaths, IReadOnlyDictionary<string, long> before, IReadOnlyDictionary<string, long> now)
        {
            if (changedPaths == null || before == null || now == null) return null;
            long delta = 0;
            foreach (var p in changedPaths.Where(IsInstructions))
                delta += (now.TryGetValue(p, out var n) ? n : 0) - (before.TryGetValue(p, out var b) ? b : 0);
            long tokens = Math.Sign(delta) * (long)Tokens.TokenEstimate.FromChars(Math.Abs(delta));
            return Math.Abs(tokens) < 50 ? (long?)null : tokens;
        }

        /// <summary>
        /// The notice for changed files: what Claude Code does with them, and that a restart reads them. With
        /// <paramref name="tokensPerRequest"/> (<see cref="TokensPerRequest"/>), what the change will cost on every request.
        /// </summary>
        public static string Notice(IReadOnlyList<string> changedPaths, string root, string home = null, long? tokensPerRequest = null)
        {
            if (changedPaths == null || changedPaths.Count == 0) return null;
            bool Under(string p, string dir) => !string.IsNullOrEmpty(dir) && p.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
            string After(string p, string dir) => p.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/');
            string Rel(string p) => Under(p, root) ? After(p, root) : Under(p, home) ? "~/" + After(p, home) : Path.GetFileName(p);
            var names = changedPaths.Select(Rel).ToList();
            string list = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names.Last();
            bool mcp = names.Any(n => n.EndsWith(".mcp.json", StringComparison.OrdinalIgnoreCase));
            bool md = changedPaths.Any(IsInstructions);
            string what = md && mcp ? "instructions and MCP servers" : mcp ? "MCP servers" : "instructions";
            string cost = tokensPerRequest is long t
                ? (t > 0 ? $" Once read, it adds ~{Tokens.TokenMeter.Format(t)} tokens to every request; a shorter one costs less on every turn."
                         : $" Once read, it takes ~{Tokens.TokenMeter.Format(-t)} tokens off every request.")
                : "";
            return $"{list} changed since Claude Code started. It reads its {what} only when it starts, so this conversation still has the old ones. Restart Claude Code to use them: the conversation carries on, and its prompt cache too.{cost}";
        }
    }
}
