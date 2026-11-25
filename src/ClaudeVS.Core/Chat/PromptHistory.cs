using System;
using System.Collections.Generic;

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
            if (_pos == -1) { _draft = current ?? ""; _pos = _items.Count; }
            if (_pos == 0) return null;
            Keep(current);
            _pos--;
            return Shown(_pos);
        }

        /// <summary>
        /// Newer entry; past the newest returns the saved draft. Null when not browsing. <paramref name="current"/>
        /// (the box's text) is kept as the edit of the entry being left, as VS Code keeps it since 2.1.331.
        /// </summary>
        public string Next(string current = null)
        {
            if (_pos == -1) return null;
            Keep(current);
            _pos++;
            if (_pos >= _items.Count) { _pos = -1; _edits.Clear(); return _draft; }
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
