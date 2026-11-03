using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ClaudeVS.Core.Chat
{
    /// <summary>What this extension remembers about a CLI session: your name for it, archive state and group.</summary>
    public sealed class SessionMeta
    {
        public string Name { get; set; }
        public bool Archived { get; set; }
        /// <summary>When you last unarchived it; auto-archiving counts inactivity from here at the earliest.</summary>
        public DateTime? UnarchivedUtc { get; set; }
        public string Group { get; set; }
    }

    /// <summary>
    /// Session names and archive flags, kept in our own local JSON file because the CLI's session files are only
    /// read, never changed. A name is also passed to the CLI with <c>--name</c> the next time that session starts.
    /// Every call re-reads the file, so several chat windows stay in step. Thread-safe within one process.
    /// </summary>
    public sealed class SessionMetaStore
    {
        private static readonly object Gate = new object();

        public SessionMetaStore(string path) => Path = path ?? throw new ArgumentNullException(nameof(path));

        public string Path { get; }

        public static string DefaultPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-vs-tokensaver", "sessions.json");

        public SessionMeta Get(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            lock (Gate) return Read().TryGetValue(sessionId, out var m) ? m : null;
        }

        /// <summary>Sets or (with an empty name) clears the name.</summary>
        public void SetName(string sessionId, string name)
        {
            name = Clean(name);
            Update(sessionId, m => m.Name = name);
        }

        public void SetArchived(string sessionId, bool archived) => Update(sessionId, m =>
        {
            m.Archived = archived;
            if (!archived) m.UnarchivedUtc = DateTime.UtcNow; // so auto-archive does not take it straight back
        });

        /// <summary>Puts the session in a group, or (empty) takes it out.</summary>
        public void SetGroup(string sessionId, string group)
        {
            group = Clean(group);
            Update(sessionId, m => m.Group = group);
        }

        /// <summary>All group names in use, sorted.</summary>
        public List<string> Groups()
        {
            lock (Gate) return Read().Values.Select(m => m.Group).Where(g => !string.IsNullOrEmpty(g)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Renames a group for every session in it; an empty new name ungroups them (deletes the group).</summary>
        public void RenameGroup(string oldName, string newName)
        {
            newName = Clean(newName);
            lock (Gate)
            {
                var all = Read();
                foreach (var kv in all.ToList())
                    if (string.Equals(kv.Value.Group, oldName, StringComparison.OrdinalIgnoreCase))
                    {
                        kv.Value.Group = newName;
                        if (IsEmpty(kv.Value)) all.Remove(kv.Key);
                    }
                Write(all);
            }
        }

        /// <summary>
        /// Applies names, groups and archive flags to a session list (in place) and returns it. With
        /// <paramref name="autoArchiveDays"/> &gt; 0, a session untouched for that many days counts as archived
        /// (<see cref="SessionInfo.AutoArchived"/>), unless you unarchived it since; like VS Code (default 14 days).
        /// </summary>
        public List<SessionInfo> Apply(List<SessionInfo> sessions, int autoArchiveDays = 0, DateTime? nowUtc = null)
        {
            Dictionary<string, SessionMeta> all;
            lock (Gate) all = Read();
            var now = nowUtc ?? DateTime.UtcNow;
            foreach (var s in sessions)
            {
                if (s?.Id == null) continue;
                all.TryGetValue(s.Id, out var m);
                if (!string.IsNullOrEmpty(m?.Name)) s.Title = m.Name;
                s.Group = m?.Group;
                s.Archived = m?.Archived == true;
                if (!s.Archived && autoArchiveDays > 0)
                {
                    var lastActive = m?.UnarchivedUtc > s.LastWriteUtc ? m.UnarchivedUtc.Value : s.LastWriteUtc;
                    if ((now - lastActive).TotalDays > autoArchiveDays) s.Archived = s.AutoArchived = true;
                }
            }
            return sessions;
        }

        /// <summary>One line, at most 100 characters; null when nothing is left.</summary>
        public static string Clean(string name)
        {
            string s = string.Join(" ", (name ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (s.Length > 100) s = s.Substring(0, 100).TrimEnd();
            return s.Length == 0 ? null : s;
        }

        private void Update(string sessionId, Action<SessionMeta> change)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            lock (Gate)
            {
                var all = Read();
                if (!all.TryGetValue(sessionId, out var m)) all[sessionId] = m = new SessionMeta();
                change(m);
                if (IsEmpty(m)) all.Remove(sessionId); // nothing left to remember
                Write(all);
            }
        }

        private static bool IsEmpty(SessionMeta m) => string.IsNullOrEmpty(m.Name) && !m.Archived && string.IsNullOrEmpty(m.Group) && m.UnarchivedUtc == null;

        private Dictionary<string, SessionMeta> Read()
        {
            try
            {
                if (!File.Exists(Path)) return new Dictionary<string, SessionMeta>();
                var d = JsonConvert.DeserializeObject<Dictionary<string, SessionMeta>>(File.ReadAllText(Path));
                return d?.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, SessionMeta>();
            }
            catch (JsonException) { return new Dictionary<string, SessionMeta>(); }
        }

        private void Write(Dictionary<string, SessionMeta> all)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(all, Formatting.Indented, new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore }));
            if (File.Exists(Path)) File.Replace(tmp, Path, null);
            else File.Move(tmp, Path);
        }
    }
}
