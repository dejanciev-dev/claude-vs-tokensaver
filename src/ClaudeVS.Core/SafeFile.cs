using System;
using System.IO;
using System.Text;

namespace ClaudeVS.Core
{
    /// <summary>
    /// The extension's own file writes, safe on a full disk: the new text goes to a temp file next to the target first,
    /// so a failed write leaves the old file whole (and the half-written temp file is removed, giving the space back).
    /// </summary>
    public static class SafeFile
    {
        private const int ErrorHandleDiskFull = 39; // ERROR_HANDLE_DISK_FULL
        private const int ErrorDiskFull = 112;      // ERROR_DISK_FULL

        /// <summary>
        /// Writes <paramref name="contents"/> to <paramref name="path"/> through <c>path.tmp</c>, creating the folder.
        /// Throws what the write threw; the file at <paramref name="path"/> is then as it was.
        /// </summary>
        public static void Replace(string path, string contents, Encoding encoding = null)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, contents ?? "", encoding ?? new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                throw;
            }
        }

        /// <summary>The write failed because the disk is full (Windows' error codes, or the words in a message).</summary>
        public static bool IsDiskFull(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                int code = e.HResult & 0xFFFF;
                if (e is IOException && (e.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) && (code == ErrorDiskFull || code == ErrorHandleDiskFull)) return true;
                if (IsDiskFullText(e.Message)) return true;
            }
            return false;
        }

        /// <summary>A message that says the disk is full: Node's <c>ENOSPC</c> and "no space left on device", Windows' "not enough space on the disk".</summary>
        public static bool IsDiskFullText(string message)
        {
            string m = (message ?? "").ToLowerInvariant();
            return m.Contains("enospc") || m.Contains("no space left on device") || m.Contains("not enough space on the disk");
        }

        /// <summary>
        /// What to tell the user when writing <paramref name="path"/> failed: "The disk is full: free some space on C:,
        /// then try again." for a full disk, else the exception's own message.
        /// </summary>
        public static string Explain(Exception ex, string path)
        {
            if (ex == null) return null;
            if (!IsDiskFull(ex)) return ex.Message;
            string drive = null;
            try { drive = Path.GetPathRoot(Path.GetFullPath(path ?? ""))?.TrimEnd('\\', '/'); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException || e is System.Security.SecurityException) { }
            return string.IsNullOrEmpty(drive) ? "The disk is full: free some space, then try again." : $"The disk is full: free some space on {drive}, then try again.";
        }
    }
}
