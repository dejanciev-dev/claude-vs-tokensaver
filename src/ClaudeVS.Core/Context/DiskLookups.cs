using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeVS.Core.Context
{
    /// <summary>
    /// Remembers whether paths are files or folders, for the links in one streamed answer (Phase 94): the answer is
    /// parsed and drawn again for every piece that arrives, so each link and path-shaped code span would look at the
    /// disk each time, on the UI thread. <see cref="Clear"/> when the answer ends, so a file Claude writes afterwards
    /// links in the next answer. Pass <see cref="FileExists"/> and <see cref="DirExists"/> to
    /// <see cref="FileLink.Parse"/> and <see cref="FileLink.FromCode"/>.
    /// </summary>
    public sealed class DiskLookups
    {
        private readonly Func<string, bool> _fileExists;
        private readonly Func<string, bool> _dirExists;
        private readonly Dictionary<string, bool> _files = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _dirs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The disk by default; tests pass their own.</summary>
        public DiskLookups(Func<string, bool> fileExists = null, Func<string, bool> dirExists = null)
        {
            _fileExists = fileExists ?? File.Exists;
            _dirExists = dirExists ?? Directory.Exists;
        }

        /// <summary>How many times the disk was asked since the last <see cref="Clear"/>.</summary>
        public int Lookups { get; private set; }

        public bool FileExists(string path) => Ask(_files, _fileExists, path);

        public bool DirExists(string path) => Ask(_dirs, _dirExists, path);

        public void Clear()
        {
            _files.Clear();
            _dirs.Clear();
            Lookups = 0;
        }

        private bool Ask(Dictionary<string, bool> known, Func<string, bool> disk, string path)
        {
            string key = path ?? "";
            if (known.TryGetValue(key, out bool found)) return found;
            Lookups++;
            found = disk(path);
            known[key] = found;
            return found;
        }
    }
}
