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
            if (string.IsNullOrWhiteSpace(prompt)) return;
            if (_items.Count > 0 && _items[_items.Count - 1] == prompt) return; // no consecutive duplicates
            _items.Add(prompt);
            if (_items.Count > _max) _items.RemoveAt(0);
        }

        /// <summary>Older entry, or null when there is nothing older. <paramref name="current"/> is saved as the draft on the first step.</summary>
        public string Previous(string current)
        {
            if (_items.Count == 0) return null;
            if (_pos == -1) { _draft = current ?? ""; _pos = _items.Count; }
            if (_pos == 0) return null;
            _pos--;
            return _items[_pos];
        }

        /// <summary>Newer entry; past the newest returns the saved draft. Null when not browsing.</summary>
        public string Next()
        {
            if (_pos == -1) return null;
            _pos++;
            if (_pos >= _items.Count) { _pos = -1; return _draft; }
            return _items[_pos];
        }
    }
}
