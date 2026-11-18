using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClaudeVS.Core.Context
{
    /// <summary>
    /// What an attached reference (a file, a folder, a project's folder) would cost if Claude read it whole: a ceiling,
    /// not a charge, since a reference sends only its path. A folder counts its text files the way the "@" picker
    /// lists them (<see cref="FileIndex"/>: bin, obj, node_modules… left out), without binaries, up to
    /// <see cref="MaxFiles"/> files.
    /// </summary>
    public sealed class ReferenceSize
    {
        /// <summary>Folders with more files than this are counted up to it, and the label says "+".</summary>
        public const int MaxFiles = 5000;

        /// <summary>From this many tokens a reference is called large (shown in amber).</summary>
        public const int LargeTokens = 20_000;

        /// <summary>Files Claude doesn't read as text: their bytes are not tokens.</summary>
        public static readonly HashSet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dll", ".exe", ".pdb", ".obj", ".lib", ".so", ".dylib", ".nupkg", ".snk", ".pfx", ".zip", ".7z", ".gz", ".tar", ".rar",
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff", ".psd", ".mp3", ".mp4", ".wav", ".avi", ".mov",
            ".pdf", ".docx", ".xlsx", ".pptx", ".ttf", ".otf", ".woff", ".woff2", ".eot", ".db", ".sqlite", ".mdf", ".ldf", ".bin", ".dat",
        };

        public bool IsFolder { get; private set; }
        /// <summary>Text files counted (1 for a file).</summary>
        public int Files { get; private set; }
        public long Bytes { get; private set; }
        /// <summary>True when the folder had more files than <see cref="MaxFiles"/>.</summary>
        public bool Truncated { get; private set; }

        public int Tokens => Core.Tokens.TokenEstimate.FromChars(Bytes);

        public bool IsLarge => Tokens >= LargeTokens;

        /// <summary>"~3.2k tok" for a file; "12 files, ~40.0k tok" for a folder ("5000+ files" when cut short); "" when empty.</summary>
        public string Label
        {
            get
            {
                string tok = "~" + Core.Tokens.TokenMeter.Format(Tokens) + " tok";
                if (!IsFolder) return Bytes > 0 ? tok : "";
                if (Files == 0) return "no text files";
                return $"{Files}{(Truncated ? "+" : "")} file{(Files == 1 && !Truncated ? "" : "s")}, {tok}";
            }
        }

        /// <summary>Measures a file or folder; null when it is neither (deleted, no access).</summary>
        public static ReferenceSize Measure(string path, int maxFiles = MaxFiles)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (File.Exists(path))
                    return new ReferenceSize { Files = 1, Bytes = BinaryExtensions.Contains(Path.GetExtension(path)) ? 0 : new FileInfo(path).Length };
                if (!Directory.Exists(path)) return null;

                // One more than the cap, to know whether it was cut short.
                var index = FileIndex.Build(path, maxFiles + 1);
                var text = index.Files.Where(f => !BinaryExtensions.Contains(Path.GetExtension(f))).ToList();
                bool truncated = index.Truncated || text.Count > maxFiles;
                long bytes = 0;
                foreach (var rel in text.Take(maxFiles))
                {
                    try { bytes += new FileInfo(index.FullPath(rel)).Length; } catch (Exception) { }
                }
                return new ReferenceSize { IsFolder = true, Files = Math.Min(text.Count, maxFiles), Bytes = bytes, Truncated = truncated };
            }
            catch (Exception) { return null; }
        }
    }
}
