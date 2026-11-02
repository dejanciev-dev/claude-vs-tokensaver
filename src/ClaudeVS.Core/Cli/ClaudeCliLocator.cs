using System;
using System.IO;
using System.Linq;

namespace ClaudeVS.Core.Cli
{
    public static class ClaudeCliLocator
    {
        /// <summary>Returns the claude executable to start, or null when it cannot be found.</summary>
        public static string Resolve(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
                return File.Exists(configuredPath) ? configuredPath : null;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            // Native installer first (~/.local/bin/claude.exe), then npm global shim.
            var candidates = new[]
            {
                Path.Combine(home, ".local", "bin", "claude.exe"),
                Path.Combine(home, ".claude", "local", "claude.exe"),
                Path.Combine(appData, "npm", "claude.cmd"),
            };
            var hit = candidates.FirstOrDefault(File.Exists);
            if (hit != null) return hit;

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (var name in new[] { "claude.exe", "claude.cmd", "claude" })
                {
                    try
                    {
                        string full = Path.Combine(dir.Trim().Trim('"'), name);
                        if (File.Exists(full)) return full;
                    }
                    catch (ArgumentException) { }
                }
            }
            return null;
        }
    }
}
