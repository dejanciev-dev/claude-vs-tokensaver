using System;
using System.IO;
using ClaudeVS.Core.Context;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVS.Commands
{
    /// <summary>Reads solution folder and editor selection from the IDE. Main thread only.</summary>
    internal static class VsContext
    {
        private static DTE2 Dte
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return Package.GetGlobalService(typeof(DTE)) as DTE2;
            }
        }

        /// <summary>Folder of the open .sln, or the open folder (Open Folder mode), or null.</summary>
        public static string GetSolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                string full = Dte?.Solution?.FullName;
                if (string.IsNullOrEmpty(full)) return null;
                return File.Exists(full) ? Path.GetDirectoryName(full) : (Directory.Exists(full) ? full : null);
            }
            catch (Exception) { return null; }
        }

        public static EditorSelection GetSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                Document doc = Dte?.ActiveDocument;
                if (doc == null || !(doc.Selection is TextSelection sel)) return null;
                if (sel.IsEmpty) return null;
                int start = Math.Min(sel.TopPoint.Line, sel.BottomPoint.Line);
                int end = Math.Max(sel.TopPoint.Line, sel.BottomPoint.Line);
                // A selection ending at column 1 of the next line does not really include that line.
                if (end > start && sel.BottomPoint.LineCharOffset == 1) end--;
                return new EditorSelection
                {
                    FilePath = doc.FullName,
                    StartLine = start,
                    EndLine = end,
                    Text = sel.Text,
                    Language = MapLanguage(doc.Language, doc.FullName),
                };
            }
            catch (Exception) { return null; }
        }

        private static string MapLanguage(string vsLanguage, string path)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".cs": return "csharp";
                case ".ts": return "ts";
                case ".js": return "js";
                case ".html": return "html";
                case ".scss": return "scss";
                case ".sql": return "sql";
                case ".json": return "json";
                case ".xml": case ".csproj": case ".xaml": return "xml";
                case ".razor": case ".cshtml": return "razor";
            }
            return (vsLanguage ?? "").ToLowerInvariant();
        }
    }
}
