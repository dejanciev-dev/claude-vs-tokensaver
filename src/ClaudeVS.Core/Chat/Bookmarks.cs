using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ClaudeVS.Core.Chat
{
    /// <summary>One saved answer: the prompt that led to it and Claude's reply as Markdown.</summary>
    public sealed class Bookmark
    {
        public string Id { get; set; }
        public DateTime CreatedUtc { get; set; }
        /// <summary>Solution folder the conversation ran in; bookmarks are listed per solution.</summary>
        public string Solution { get; set; }
        public string SessionId { get; set; }
        public string Title { get; set; }
        public string Prompt { get; set; }
        public string Answer { get; set; }

        /// <summary>Longest answer kept; longer ones are cut with a note (a bookmark is for finding things again, not an archive).</summary>
        public const int MaxAnswerChars = 100_000;

        public static Bookmark Create(string solution, string sessionId, string prompt, string answer, DateTime nowUtc)
        {
            answer = (answer ?? "").Trim();
            if (answer.Length > MaxAnswerChars) answer = answer.Substring(0, MaxAnswerChars) + "\n\n… (cut)";
            return new Bookmark
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedUtc = nowUtc,
                Solution = solution,
                SessionId = sessionId,
                Prompt = prompt ?? "",
                Answer = answer,
                Title = MakeTitle(prompt, answer),
            };
        }

        /// <summary>The prompt's first line, or else the answer's first non-empty line without Markdown markers; at most 80 chars.</summary>
        public static string MakeTitle(string prompt, string answer)
        {
            string line = FirstLine(prompt) ?? FirstLine((answer ?? "").Replace("```", "")) ?? "Bookmark";
            line = line.TrimStart('#', '>', '-', '*', ' ').Trim();
            if (line.Length == 0) line = "Bookmark";
            return line.Length <= 80 ? line : line.Substring(0, 79) + "…";
        }

        private static string FirstLine(string s) =>
            (s ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

        /// <summary>Case-insensitive match on title, prompt and answer; an empty query matches everything.</summary>
        public bool Matches(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            return query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).All(word =>
                Contains(Title, word) || Contains(Prompt, word) || Contains(Answer, word));
        }

        private static bool Contains(string s, string word) => s != null && s.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Bookmarked answers in one local JSON file (never sent anywhere). Every call re-reads the file, so several
    /// chat windows see each other's changes. Thread-safe within one process.
    /// </summary>
    public sealed class BookmarkStore
    {
        private static readonly object Gate = new object();

        public BookmarkStore(string path) => Path = path ?? throw new ArgumentNullException(nameof(path));

        public string Path { get; }

        public static string DefaultPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-vs-tokensaver", "bookmarks.json");

        /// <summary>Bookmarks for a solution folder (all of them when <paramref name="solution"/> is null), newest first.</summary>
        public List<Bookmark> List(string solution = null)
        {
            lock (Gate)
            {
                return Read()
                    .Where(b => solution == null || string.Equals(Normalize(b.Solution), Normalize(solution), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(b => b.CreatedUtc)
                    .ToList();
            }
        }

        public void Add(Bookmark bookmark)
        {
            if (bookmark == null) return;
            lock (Gate)
            {
                var all = Read();
                all.RemoveAll(b => b.Id == bookmark.Id);
                all.Add(bookmark);
                Write(all);
            }
        }

        /// <summary>Returns false when no bookmark has that id.</summary>
        public bool Remove(string id)
        {
            lock (Gate)
            {
                var all = Read();
                if (all.RemoveAll(b => b.Id == id) == 0) return false;
                Write(all);
                return true;
            }
        }

        private List<Bookmark> Read()
        {
            try
            {
                if (!File.Exists(Path)) return new List<Bookmark>();
                return JsonConvert.DeserializeObject<List<Bookmark>>(File.ReadAllText(Path))?.Where(b => b?.Id != null).ToList() ?? new List<Bookmark>();
            }
            catch (JsonException) { return new List<Bookmark>(); } // a damaged file is not worth an error; the next save replaces it
        }

        // Write to a temp file first so a crash mid-write can't leave half a file.
        private void Write(List<Bookmark> all)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(all, Formatting.Indented));
            if (File.Exists(Path)) File.Replace(tmp, Path, null);
            else File.Move(tmp, Path);
        }

        private static string Normalize(string dir) => (dir ?? "").TrimEnd('\\', '/');
    }
}
