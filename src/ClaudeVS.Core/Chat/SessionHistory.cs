using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Chat
{
    public sealed class SessionInfo
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public long SizeBytes { get; set; }
        public string FilePath { get; set; }
    }

    /// <summary>
    /// Lists past Claude Code conversations for a folder from ~/.claude/projects/&lt;encoded-path&gt;/*.jsonl,
    /// so one can be resumed with --resume. Read-only; never modifies the CLI's files.
    /// </summary>
    public static class SessionHistory
    {
        /// <summary>The CLI's folder name for a project: every character that is not a letter or digit becomes '-'.</summary>
        public static string EncodeProjectPath(string dir)
        {
            string full = (dir ?? "").TrimEnd('\\', '/');
            var sb = new StringBuilder(full.Length);
            foreach (char c in full) sb.Append(c < 128 && char.IsLetterOrDigit(c) ? c : '-');
            return sb.ToString();
        }

        public static string DefaultProjectsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

        public static List<SessionInfo> List(string projectsRoot, string solutionDir, int max = 15)
        {
            var result = new List<SessionInfo>();
            if (string.IsNullOrEmpty(solutionDir)) return result;
            string folder = Path.Combine(projectsRoot ?? DefaultProjectsRoot, EncodeProjectPath(solutionDir));
            if (!Directory.Exists(folder)) return result;

            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(folder).GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(max).ToList(); }
            catch (Exception) { return result; }

            foreach (var f in files)
            {
                string title = ReadTitle(f.FullName);
                if (title == null) continue; // no user prompt: nothing worth resuming
                result.Add(new SessionInfo
                {
                    Id = Path.GetFileNameWithoutExtension(f.Name),
                    Title = title,
                    LastWriteUtc = f.LastWriteTimeUtc,
                    SizeBytes = f.Length,
                    FilePath = f.FullName,
                });
            }
            return result;
        }

        /// <summary>A "summary" entry if present, else the first real user prompt. Reads at most the first 400 lines.</summary>
        public static string ReadTitle(string file)
        {
            string firstPrompt = null;
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    int n = 0;
                    while ((line = reader.ReadLine()) != null && n++ < 400)
                    {
                        if (line.Length == 0 || line[0] != '{') continue;
                        JObject o;
                        try { o = JObject.Parse(line); } catch (JsonException) { continue; }
                        string type = (string)o["type"];
                        if (type == "summary" && !string.IsNullOrWhiteSpace((string)o["summary"])) return Clean((string)o["summary"]);
                        if (firstPrompt == null && type == "user" && o.Value<bool?>("isMeta") != true)
                            firstPrompt = PromptText(o.SelectToken("message.content"));
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return firstPrompt == null ? null : Clean(firstPrompt);
        }

        private static string PromptText(JToken content)
        {
            string text = content?.Type == JTokenType.String
                ? (string)content
                : (content as JArray)?.OfType<JObject>().Where(b => (string)b["type"] == "text").Select(b => (string)b["text"]).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            // Skip CLI-generated wrappers (command output, caveats, hook messages) and tool results.
            return text.StartsWith("<") ? null : text;
        }

        /// <summary>Compact age like the VS Code session list: "now", "5m", "3h", "2d", "6w", "1y".</summary>
        public static string TimeAgo(DateTime utc, DateTime nowUtc)
        {
            var d = nowUtc - utc;
            if (d.TotalMinutes < 1) return "now";
            if (d.TotalHours < 1) return (int)d.TotalMinutes + "m";
            if (d.TotalDays < 1) return (int)d.TotalHours + "h";
            if (d.TotalDays < 14) return (int)d.TotalDays + "d";
            if (d.TotalDays < 365) return (int)(d.TotalDays / 7) + "w";
            return (int)(d.TotalDays / 365) + "y";
        }

        private static string Clean(string s)
        {
            s = string.Join(" ", s.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            return s.Length <= 80 ? s : s.Substring(0, 79) + "…";
        }
    }
}
