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

        /// <summary>
        /// Your order of the groups (Move up / Move down, as VS Code got in 2.1.330), next to the sessions file: a JSON
        /// list of names. Its own file, so the sessions file keeps its shape for older versions.
        /// </summary>
        public string GroupOrderPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? "", "session-groups.json");

        /// <summary>All group names in use, in your order: the ones you moved first, then the rest by name.</summary>
        public List<string> Groups()
        {
            lock (Gate) return Order(GroupsIn(Read()), ReadOrder());
        }

        /// <summary>
        /// <paramref name="groups"/> as listed: those in <paramref name="order"/> first, in that order, then the others by
        /// name. Names compare without case; a name in the order that no session uses is left out.
        /// </summary>
        public static List<string> Order(IEnumerable<string> groups, IReadOnlyList<string> order)
        {
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < (order?.Count ?? 0); i++) if (!string.IsNullOrEmpty(order[i]) && !rank.ContainsKey(order[i])) rank[order[i]] = i;
            return groups.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => rank.TryGetValue(g, out int r) ? r : int.MaxValue)
                .ThenBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Moves a group one place up (<paramref name="step"/> -1) or down (+1) among <see cref="Groups"/>, and keeps
        /// that order for all of them. False when it is already first or last, or not in use.
        /// </summary>
        public bool MoveGroup(string group, int step)
        {
            lock (Gate)
            {
                var list = Order(GroupsIn(Read()), ReadOrder());
                int i = list.FindIndex(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
                int j = i + step;
                if (i < 0 || j < 0 || j >= list.Count || step == 0) return false;
                (list[i], list[j]) = (list[j], list[i]);
                SafeFile.Replace(GroupOrderPath, JsonConvert.SerializeObject(list, Formatting.Indented));
                return true;
            }
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
                // A renamed group keeps its place; a deleted one leaves the order
                var order = ReadOrder();
                int at = order.FindIndex(g => string.Equals(g, oldName, StringComparison.OrdinalIgnoreCase));
                if (at < 0) return;
                if (newName == null || order.Any(g => string.Equals(g, newName, StringComparison.OrdinalIgnoreCase))) order.RemoveAt(at);
                else order[at] = newName;
                SafeFile.Replace(GroupOrderPath, JsonConvert.SerializeObject(order, Formatting.Indented));
            }
        }

        private static IEnumerable<string> GroupsIn(Dictionary<string, SessionMeta> all) =>
            all.Values.Select(m => m.Group).Where(g => !string.IsNullOrEmpty(g));

        private List<string> ReadOrder()
        {
            try
            {
                if (!File.Exists(GroupOrderPath)) return new List<string>();
                return JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(GroupOrderPath))?.Where(g => !string.IsNullOrEmpty(g)).ToList() ?? new List<string>();
            }
            catch (JsonException) { return new List<string>(); } // a damaged file: by name, until the next move
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
            SafeFile.Replace(Path, JsonConvert.SerializeObject(all, Formatting.Indented, new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore }));
        }
    }
}
