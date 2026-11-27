using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeVS.Core.Chat
{
    /// <summary>
    /// Shell-style prompt history for Ctrl+Up / Ctrl+Down. Keeps the unsent draft, so stepping back
    /// past the newest entry restores what the user was typing.
    /// </summary>
    public sealed class PromptHistory
    {
        private readonly List<string> _items = new List<string>();
        private readonly int _max;
        private int _pos = -1;      // -1 = not browsing
        private string _draft = "";

        public PromptHistory(int max = 100) { _max = Math.Max(1, max); }

        public int Count => _items.Count;

        /// <summary>The prompts, oldest first (Phase 113: what is saved for the solution).</summary>
        public IReadOnlyList<string> Items => _items.AsReadOnly();

        /// <summary>
        /// Puts saved prompts before the ones sent in this chat (Phase 113), oldest first, the newest
        /// <see cref="PromptHistory(int)"/> max of them kept. Ends browsing.
        /// </summary>
        public void Load(IEnumerable<string> saved)
        {
            _pos = -1;
            _edits.Clear();
            var merged = new List<string>();
            foreach (var p in saved ?? new string[0])
                if (!string.IsNullOrWhiteSpace(p) && (merged.Count == 0 || merged[merged.Count - 1] != p)) merged.Add(p);
            foreach (var p in _items)
                if (merged.Count == 0 || merged[merged.Count - 1] != p) merged.Add(p);
            _items.Clear();
            _items.AddRange(merged.Count > _max ? merged.GetRange(merged.Count - _max, _max) : merged);
        }

        public void Add(string prompt)
        {
            _pos = -1;
            _edits.Clear();
            if (string.IsNullOrWhiteSpace(prompt)) return;
            if (_items.Count > 0 && _items[_items.Count - 1] == prompt) return; // no consecutive duplicates
            _items.Add(prompt);
            if (_items.Count > _max) _items.RemoveAt(0);
        }

        /// <summary>
        /// Older entry, or null when there is nothing older. <paramref name="current"/> (the box's text) is saved as the
        /// draft on the first step, and after that as the edit of the entry being left, so it isn't lost.
        /// </summary>
        public string Previous(string current)
        {
            if (_items.Count == 0) return null;
            if (_pos == -1)
            {
                _draft = current ?? "";
                _pos = _items.Count;
                // Text in the box searches (Phase 113): only the prompts that contain it, as a shell's history search;
                // when none does, all of them
                string find = _draft.Trim();
                _filter = find.Length > 0 && _items.Any(i => Contains(i, find)) ? find : null;
            }
            int older = _pos - 1;
            while (older >= 0 && !Matches(older)) older--;
            if (older < 0) return null;
            Keep(current);
            _pos = older;
            return Shown(_pos);
        }

        // What the box held when browsing started, when it searches (Phase 113)
        private string _filter;

        /// <summary>True while <c>Ctrl+Up</c> goes only to prompts containing what the box held (Phase 113).</summary>
        public bool Searching => _pos != -1 && _filter != null;

        private bool Matches(int pos) => _filter == null || Contains(_items[pos], _filter);

        private static bool Contains(string item, string find) => item.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Newer entry; past the newest returns the saved draft. Null when not browsing. <paramref name="current"/>
        /// (the box's text) is kept as the edit of the entry being left, as VS Code keeps it since 2.1.331.
        /// </summary>
        public string Next(string current = null)
        {
            if (_pos == -1) return null;
            Keep(current);
            _pos++;
            while (_pos < _items.Count && !Matches(_pos)) _pos++;
            if (_pos >= _items.Count) { _pos = -1; _edits.Clear(); _filter = null; return _draft; }
            return Shown(_pos);
        }

        // Edits to recalled entries while browsing, by position; dropped when browsing ends or a prompt is sent.
        // The entries themselves stay as they were sent.
        private readonly Dictionary<int, string> _edits = new Dictionary<int, string>();

        private void Keep(string current)
        {
            if (current == null || _pos < 0 || _pos >= _items.Count) return;
            if (current == _items[_pos]) _edits.Remove(_pos);
            else _edits[_pos] = current;
        }

        private string Shown(int pos) => _edits.TryGetValue(pos, out var edited) ? edited : _items[pos];
    }
}
