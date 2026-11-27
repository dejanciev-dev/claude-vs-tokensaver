using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ClaudeVS.Core.Chat
{
    /// <summary>
    /// The prompts you sent, kept per solution between Visual Studio sessions (Phase 113), so a new chat window's
    /// <c>Ctrl+Up</c> starts with them. One JSON file beside the bookmarks; the 100 newest prompts of the 50 solutions
    /// used last. A prompt longer than <see cref="MaxPromptChars"/> (a pasted log) isn't kept.
    /// </summary>
    public sealed class PromptHistoryStore
    {
        public const int MaxPrompts = 100;
        public const int MaxSolutions = 50;
        public const int MaxPromptChars = 20_000;

        private sealed class Entry
        {
            public DateTime UsedUtc { get; set; }
            public List<string> Prompts { get; set; } = new List<string>();
        }

        private static readonly object Gate = new object();

        public PromptHistoryStore(string path) => Path = path ?? throw new ArgumentNullException(nameof(path));

        public string Path { get; }

        public static string DefaultPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-vs-tokensaver", "prompt-history.json");

        /// <summary>The solution's saved prompts, oldest first; empty when there are none or the file can't be read.</summary>
        public List<string> Get(string solution)
        {
            lock (Gate)
                return Read().TryGetValue(Key(solution), out var e) ? e.Prompts.ToList() : new List<string>();
        }

        /// <summary>
        /// Adds a prompt you sent to the solution's (the newest <see cref="MaxPrompts"/> kept; an overlong one isn't).
        /// Each send adds its own, read from the file again, so two chat windows of a solution don't overwrite each
        /// other's. Other solutions' are kept, the ones used longest ago dropped past <see cref="MaxSolutions"/>. Throws
        /// on an I/O error, which the caller may ignore.
        /// </summary>
        public void Add(string solution, string prompt, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > MaxPromptChars) return;
            lock (Gate)
            {
                var all = Read();
                if (!all.TryGetValue(Key(solution), out var entry)) all[Key(solution)] = entry = new Entry();
                entry.UsedUtc = nowUtc;
                entry.Prompts.Remove(prompt); // sent again: the newest place, once
                entry.Prompts.Add(prompt);
                if (entry.Prompts.Count > MaxPrompts) entry.Prompts.RemoveRange(0, entry.Prompts.Count - MaxPrompts);
                var keep = all.OrderByDescending(kv => kv.Value.UsedUtc).Take(MaxSolutions).ToDictionary(kv => kv.Key, kv => kv.Value);
                SafeFile.Replace(Path, JsonConvert.SerializeObject(keep, Formatting.Indented));
            }
        }

        private Dictionary<string, Entry> Read()
        {
            try
            {
                if (!File.Exists(Path)) return new Dictionary<string, Entry>();
                var all = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(Path));
                return all?.Where(kv => kv.Value?.Prompts != null).ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, Entry>();
            }
            catch (JsonException) { return new Dictionary<string, Entry>(); } // a damaged file: start again, the next save replaces it
            catch (IOException) { return new Dictionary<string, Entry>(); }
            catch (UnauthorizedAccessException) { return new Dictionary<string, Entry>(); }
        }

        /// <summary>A solution folder as a key: without a trailing slash, any case ("" for no solution).</summary>
        private static string Key(string solution) => (solution ?? "").TrimEnd('\\', '/').ToLowerInvariant();
    }
}
