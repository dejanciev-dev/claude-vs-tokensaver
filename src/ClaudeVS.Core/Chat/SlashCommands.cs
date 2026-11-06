using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClaudeVS.Core.Chat
{
    public sealed class SlashCommand
    {
        public string Name { get; set; }        // without the leading '/'
        public string Description { get; set; }
        /// <summary>"built-in", "project" or "user".</summary>
        public string Source { get; set; }
        public override string ToString() => "/" + Name;
    }

    /// <summary>
    /// Slash commands to suggest while typing. Built-ins are limited to ones that work in the CLI's
    /// stream-json mode; custom commands come from .claude/commands (project and user), including
    /// sub-folders, which the CLI exposes as "folder:name".
    /// </summary>
    public static class SlashCommands
    {
        public static readonly IReadOnlyList<SlashCommand> BuiltIn = new[]
        {
            new SlashCommand { Name = "compact", Description = "Summarize the conversation to shrink context", Source = "built-in" },
        };

        public static List<SlashCommand> Discover(string solutionDir, string userHome)
        {
            var list = new List<SlashCommand>(BuiltIn);
            if (!string.IsNullOrEmpty(solutionDir)) list.AddRange(FromFolder(Path.Combine(solutionDir, ".claude", "commands"), "project"));
            if (!string.IsNullOrEmpty(userHome)) list.AddRange(FromFolder(Path.Combine(userHome, ".claude", "commands"), "user"));
            // Project commands win over user commands with the same name.
            return list.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        }

        /// <summary>
        /// Adds the skills Claude Code reports in <c>init</c> (2.1.319) that you can run yourself: names in both
        /// <c>skills</c> and <c>slash_commands</c>, so Claude-only skills and the built-in commands (many need the
        /// terminal) stay out. Commands already listed (built-in, project, user) win; source "skill".
        /// </summary>
        public static List<SlashCommand> WithSkills(IEnumerable<SlashCommand> commands, IEnumerable<string> skills, IEnumerable<string> slashCommands)
        {
            var list = new List<SlashCommand>(commands ?? Enumerable.Empty<SlashCommand>());
            var runnable = new HashSet<string>(slashCommands ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var have = new HashSet<string>(list.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var s in skills ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(s) || !runnable.Contains(s) || !have.Add(s)) continue;
                int colon = s.IndexOf(':');
                list.Add(new SlashCommand { Name = s, Source = "skill", Description = colon > 0 ? "from plugin " + s.Substring(0, colon) : "" });
            }
            return list;
        }

        /// <summary>Commands whose name starts with what follows the '/' in <paramref name="typed"/>.</summary>
        public static List<SlashCommand> Match(IEnumerable<SlashCommand> commands, string typed)
        {
            string t = (typed ?? "").TrimStart();
            if (!t.StartsWith("/")) return new List<SlashCommand>();
            string prefix = t.Substring(1).Split(' ')[0];
            return (commands ?? Enumerable.Empty<SlashCommand>())
                .Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<SlashCommand> FromFolder(string root, string source)
        {
            if (!Directory.Exists(root)) yield break;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).ToList(); }
            catch (Exception) { yield break; }
            foreach (var f in files)
            {
                string rel = f.Substring(root.Length).TrimStart('\\', '/');
                string name = Path.ChangeExtension(rel, null).Replace('\\', ':').Replace('/', ':');
                yield return new SlashCommand { Name = name, Description = ReadDescription(f), Source = source };
            }
        }

        /// <summary>"description:" from YAML front matter, else the first non-empty line, trimmed to 80 chars.</summary>
        private static string ReadDescription(string file)
        {
            try
            {
                var lines = File.ReadLines(file).Take(30).ToList();
                if (lines.Count > 0 && lines[0].Trim() == "---")
                {
                    for (int i = 1; i < lines.Count && lines[i].Trim() != "---"; i++)
                        if (lines[i].TrimStart().StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                            return Trim(lines[i].Substring(lines[i].IndexOf(':') + 1).Trim().Trim('"', '\''));
                    lines = lines.SkipWhile((l, i) => i == 0 || l.Trim() != "---").Skip(1).ToList();
                }
                return Trim(lines.Select(l => l.Trim().TrimStart('#').Trim()).FirstOrDefault(l => l.Length > 0) ?? "");
            }
            catch (Exception) { return ""; }
        }

        private static string Trim(string s) => s.Length <= 80 ? s : s.Substring(0, 79) + "…";
    }
}
