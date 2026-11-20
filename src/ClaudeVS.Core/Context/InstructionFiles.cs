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
            foreach (var path in paths)
            {
                try { d[path] = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) { d[path] = null; }
            }
            return d;
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
            foreach (var path in Snapshot(root, home).Keys.Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
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
            foreach (var p in changedPaths.Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
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
            bool md = names.Any(n => n.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
            string what = md && mcp ? "instructions and MCP servers" : mcp ? "MCP servers" : "instructions";
            string cost = tokensPerRequest is long t
                ? (t > 0 ? $" Once read, it adds ~{Tokens.TokenMeter.Format(t)} tokens to every request; a shorter one costs less on every turn."
                         : $" Once read, it takes ~{Tokens.TokenMeter.Format(-t)} tokens off every request.")
                : "";
            return $"{list} changed since Claude Code started. It reads its {what} only when it starts, so this conversation still has the old ones. Restart Claude Code to use them: the conversation carries on, and its prompt cache too.{cost}";
        }
    }
}
