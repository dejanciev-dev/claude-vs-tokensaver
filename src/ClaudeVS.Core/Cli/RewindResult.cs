using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    /// <summary>
    /// Answer to <c>rewind_files</c> (2.1.317). A dry run lists the files that would change and the line counts; the
    /// real rewind only reports whether it worked. <see cref="Error"/> is set when nothing can be rewound, for
    /// example "File rewinding is not enabled." or "No file checkpoint found for this message."
    /// </summary>
    public sealed class RewindResult
    {
        public bool CanRewind { get; set; }
        public string Error { get; set; }
        public List<string> FilesChanged { get; } = new List<string>();
        public int Insertions { get; set; }
        public int Deletions { get; set; }

        public bool Ok => CanRewind && string.IsNullOrEmpty(Error);

        public static RewindResult Parse(JObject r)
        {
            var result = new RewindResult
            {
                CanRewind = r?["canRewind"]?.Type == JTokenType.Boolean && (bool)r["canRewind"],
                Error = (string)r?["error"],
                Insertions = r?["insertions"]?.Type == JTokenType.Integer ? (int)r["insertions"] : 0,
                Deletions = r?["deletions"]?.Type == JTokenType.Integer ? (int)r["deletions"] : 0,
            };
            if (r?["filesChanged"] is JArray files) result.FilesChanged.AddRange(files.Select(f => (string)f).Where(f => !string.IsNullOrEmpty(f)));
            return result;
        }

        /// <summary>"2 files, +5 −3" for a dry run.</summary>
        public string Summary() =>
            $"{FilesChanged.Count} file{(FilesChanged.Count == 1 ? "" : "s")}, +{Insertions} −{Deletions}";
    }
}
