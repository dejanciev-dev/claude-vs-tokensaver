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
        /// <summary>Hidden from the sessions list unless archived sessions are shown (see <see cref="SessionMetaStore"/>).</summary>
        public bool Archived { get; set; }
        /// <summary>Archived because it was inactive too long, not by you.</summary>
        public bool AutoArchived { get; set; }
        /// <summary>Your group for it (from <see cref="SessionMetaStore"/>); null when ungrouped.</summary>
        public string Group { get; set; }
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

        /// <summary>Where the CLI keeps a session's conversation: &lt;projectsRoot&gt;/&lt;encoded folder&gt;/&lt;id&gt;.jsonl.</summary>
        public static string SessionFile(string projectsRoot, string solutionDir, string sessionId) =>
            Path.Combine(projectsRoot ?? DefaultProjectsRoot, EncodeProjectPath(solutionDir), sessionId + ".jsonl");

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

        /// <summary>How much of the end of a session file is searched for its name and AI title.</summary>
        private const int TailBytes = 256 * 1024;

        /// <summary>
        /// The session's title, best first: the name given with <c>--name</c> ("custom-title" entries), the CLI's own
        /// generated title ("ai-title"), a "summary" entry, then the first real user prompt. The CLI appends the
        /// title entries again after each turn, so the latest one is near the end of the file; only the first 400
        /// lines and the last 256 KB are read. Null when the file has neither a title nor a prompt.
        /// </summary>
        public static string ReadTitle(string file)
        {
            string firstPrompt = null, summary = null, custom = null, ai = null;
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var reader = new StreamReader(stream, Encoding.UTF8);
                    string line;
                    for (int n = 0; n < 400 && (line = reader.ReadLine()) != null; n++)
                    {
                        var o = ParseLine(line);
                        if (o == null) continue;
                        string type = (string)o["type"];
                        if (type == "summary" && summary == null && !string.IsNullOrWhiteSpace((string)o["summary"])) summary = (string)o["summary"];
                        else if (firstPrompt == null && type == "user" && o.Value<bool?>("isMeta") != true && o.Value<bool?>("isSidechain") != true)
                            firstPrompt = PromptText(o.SelectToken("message.content"));
                        else ReadTitleEntry(o, ref custom, ref ai);
                    }

                    // The rest of the file: all of it when it is small, else only the tail (the reader buffers ahead,
                    // so seek from the end rather than from the reader's position).
                    if (stream.Length > TailBytes * 2 && !reader.EndOfStream)
                    {
                        stream.Position = stream.Length - TailBytes;
                        reader = new StreamReader(stream, Encoding.UTF8);
                        reader.ReadLine(); // probably a partial line
                    }
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.IndexOf("-title\"", StringComparison.Ordinal) < 0) continue; // cheap filter before parsing
                        var o = ParseLine(line);
                        if (o != null) ReadTitleEntry(o, ref custom, ref ai);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            string title = new[] { custom, ai, summary, firstPrompt }.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            return title == null ? null : Clean(title);
        }

        private static void ReadTitleEntry(JObject o, ref string custom, ref string ai)
        {
            string type = (string)o["type"];
            if (type == "custom-title") custom = (string)o["customTitle"] ?? custom;
            else if (type == "ai-title") ai = (string)o["aiTitle"] ?? ai;
        }

        internal static JObject ParseLine(string line)
        {
            if (string.IsNullOrEmpty(line) || line[0] != '{') return null;
            try { return JObject.Parse(line); } catch (JsonException) { return null; }
        }

        private static string PromptText(JToken content)
        {
            string text = content?.Type == JTokenType.String
                ? (string)content
                : (content as JArray)?.OfType<JObject>().Where(b => (string)b["type"] == "text" && !Cli.ClaudeSession.IsEditorNote((string)b["text"])).Select(b => (string)b["text"]).FirstOrDefault();
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
