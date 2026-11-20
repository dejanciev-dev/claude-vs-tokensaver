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

        /// <summary>Each file's last write time (UTC), or null when it doesn't exist. Never throws.</summary>
        public static Dictionary<string, DateTime?> Snapshot(string root)
        {
            var d = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(root)) return d;
            foreach (var name in Names)
            {
                string path = Path.Combine(root, name);
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

        /// <summary>The notice for changed files: what Claude Code does with them, and that a restart reads them.</summary>
        public static string Notice(IReadOnlyList<string> changedPaths, string root)
        {
            if (changedPaths == null || changedPaths.Count == 0) return null;
            string Rel(string p) => !string.IsNullOrEmpty(root) && p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? p.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/') : Path.GetFileName(p);
            var names = changedPaths.Select(Rel).ToList();
            string list = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names.Last();
            bool mcp = names.Any(n => n.EndsWith(".mcp.json", StringComparison.OrdinalIgnoreCase));
            bool md = names.Any(n => n.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
            string what = md && mcp ? "instructions and MCP servers" : mcp ? "MCP servers" : "instructions";
            return $"{list} changed since Claude Code started. It reads its {what} only when it starts, so this conversation still has the old ones. Restart Claude Code to use them; the conversation carries on.";
        }
    }
}
