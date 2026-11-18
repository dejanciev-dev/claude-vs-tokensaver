using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// What one conversation read and ran, added up from its turns' usage records, for / → Copy session summary: reads by
    /// folder, the attached references read against their ceiling, repeated reads, and builds made quiet or run loud.
    /// </summary>
    public sealed class ConversationReads
    {
        private readonly Dictionary<string, long> _folders = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private long _refCeiling, _refRead, _repeat, _quietTokens, _loudTokens;
        private int _quiet, _loud;

        public void Reset()
        {
            _folders.Clear();
            _refCeiling = _refRead = _repeat = _quietTokens = _loudTokens = 0;
            _quiet = _loud = 0;
        }

        /// <summary>Adds one turn's record (its reads, references, repeated reads, quiet and loud commands).</summary>
        public void Add(UsageRecord r)
        {
            if (r == null) return;
            foreach (var kv in r.ReadFolders ?? new Dictionary<string, long>())
            {
                _folders.TryGetValue(kv.Key, out var t);
                _folders[kv.Key] = t + kv.Value;
            }
            if (r.RefCeilingTokens > 0) { _refCeiling += r.RefCeilingTokens; _refRead += r.RefReadTokens; }
            _repeat += r.RepeatReadTokens;
            _quiet += r.QuietRuns?.Values.Sum() ?? 0;
            _quietTokens += r.QuietTokens;
            _loud += r.LoudRuns?.Values.Sum() ?? 0;
            _loudTokens += r.LoudTokens;
        }

        /// <summary>The summary's lines; empty when the conversation read and ran nothing worth saying.</summary>
        public List<string> Lines()
        {
            var lines = new List<string>();
            var top = _folders.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
            if (top.Count > 0)
            {
                var parts = top.Take(5).Select(kv => $"{kv.Key} {TokenMeter.Format(kv.Value)}").ToList();
                long rest = top.Skip(5).Sum(kv => kv.Value);
                if (rest > 0) parts.Add($"other {TokenMeter.Format(rest)}");
                lines.Add("Reads by folder: " + string.Join(" · ", parts));
            }
            if (_refCeiling > 0)
                lines.Add($"References: Claude read ~{TokenMeter.Format(_refRead)} of the ~{TokenMeter.Format(_refCeiling)} tok attached ({Math.Round(100.0 * Math.Min(_refRead, _refCeiling) / _refCeiling):0}%)");
            if (_repeat > 0) lines.Add($"Read again, unchanged: ~{TokenMeter.Format(_repeat)} tok");
            var runs = new List<string>();
            if (_quiet > 0) runs.Add($"{_quiet} made quiet (~{TokenMeter.Format(_quietTokens)} tok back)");
            if (_loud > 0) runs.Add($"{_loud} loud (~{TokenMeter.Format(_loudTokens)} tok)");
            if (runs.Count > 0) lines.Add("Builds and logs: " + string.Join(" · ", runs));
            return lines;
        }
    }
}
