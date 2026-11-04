using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeVS.Core.Context;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

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

        /// <summary>Opens a file in the editor (Markdown files get VS's Markdown editor and preview).</summary>
        public static void OpenFile(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path);
        }

        /// <summary>Saves the file if it is open with unsaved changes, so its content on disk is current.</summary>
        public static void SaveIfOpen(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                foreach (Document d in Dte.Documents)
                    if (string.Equals(d.FullName, path, StringComparison.OrdinalIgnoreCase) && !d.Saved) { d.Save(); return; }
            }
            catch (Exception) { } // best effort: the file on disk is used as it is
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

        /// <summary>Runs a Visual Studio command by its canonical name (e.g. "Tools.ShowSavingsReport").</summary>
        public static void ExecuteCommand(string name, string args = "")
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { Dte?.ExecuteCommand(name, args); }
            catch (Exception ex) { Diagnostics.ClaudeOutputPane.WriteLine($"Command {name} failed: {ex.Message}"); }
        }

        /// <summary>Full path of the active document, or null.</summary>
        public static string GetActiveDocumentPath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                string path = Dte?.ActiveDocument?.FullName;
                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Inserts text at the caret of the active text document, replacing any selection, as one undo step.
        /// Returns false when no text editor is active.
        /// </summary>
        public static bool InsertAtCursor(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!(Dte?.ActiveDocument?.Selection is TextSelection sel)) return false;
                var undo = Dte.UndoContext;
                bool ownUndo = !undo.IsOpen;
                if (ownUndo) undo.Open("Insert from Claude Code");
                try { sel.Insert(text, (int)vsInsertFlags.vsInsertFlagsContainNewText); }
                finally { if (ownUndo) undo.Close(); }
                Dte.ActiveDocument.Activate();
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Opens Visual Studio's diff window. <paramref name="leftText"/> is written to a temporary file; the right
        /// side is <paramref name="rightFile"/> as it is on disk (or <paramref name="rightText"/> in a temp file).
        /// </summary>
        public static void OpenDiff(string leftText, string leftLabel, string rightFile, string rightText, string rightLabel, string caption)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(SVsDifferenceService)) is IVsDifferenceService diff))
                throw new InvalidOperationException("The Visual Studio diff service is not available.");

            string dir = Path.Combine(Path.GetTempPath(), "claude-vs-tokensaver", "diff");
            Directory.CreateDirectory(dir);
            string name = Path.GetFileNameWithoutExtension(rightFile ?? "snippet");
            string ext = Path.GetExtension(rightFile ?? ".txt");
            string stamp = DateTime.Now.ToString("HHmmssfff");
            string left = Path.Combine(dir, $"{name}.before-{stamp}{ext}");
            File.WriteAllText(left, leftText ?? "");

            uint options = (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary;
            string right = rightFile;
            if (rightText != null)
            {
                right = Path.Combine(dir, $"{name}.after-{stamp}{ext}");
                File.WriteAllText(right, rightText);
                options |= (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary;
            }
            diff.OpenComparisonWindow2(left, right, caption, caption, leftLabel, rightLabel, null, null, options);
        }

        /// <summary>Errors (not warnings or messages) currently in the Error List.</summary>
        public static List<BuildError> GetErrors()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return GetErrorListRows(includeWarnings: false);
        }

        /// <summary>
        /// Rows of the Error List: errors only, or errors, warnings and messages. Capped at <paramref name="max"/>
        /// rows, because each row is several COM calls on the UI thread.
        /// </summary>
        public static List<BuildError> GetErrorListRows(bool includeWarnings, int max = 2000)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var list = new List<BuildError>();
            try
            {
                ErrorItems items = Dte?.ToolWindows?.ErrorList?.ErrorItems;
                if (items == null) return list;
                for (int i = 1; i <= items.Count && list.Count < max; i++) // COM collection, 1-based
                {
                    ErrorItem item = items.Item(i);
                    var severity = item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh ? ErrorSeverity.Error
                        : item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium ? ErrorSeverity.Warning : ErrorSeverity.Message;
                    if (!includeWarnings && severity != ErrorSeverity.Error) continue;
                    var (code, message) = SplitCode(item.Description);
                    var row = new BuildError { FilePath = item.FileName, Line = item.Line, Code = code, Message = message, Severity = severity };
                    try { row.Column = item.Column; row.Project = item.Project; } catch (Exception) { } // not every provider fills these
                    list.Add(row);
                }
            }
            catch (Exception) { }
            return list;
        }

        /// <summary>DTE has no error-code property; descriptions from some tools start with "CS0103: ...".</summary>
        private static (string code, string message) SplitCode(string description)
        {
            string d = description ?? "";
            int colon = d.IndexOf(':');
            if (colon > 1 && colon <= 10)
            {
                string head = d.Substring(0, colon);
                if (head.All(char.IsLetterOrDigit) && head.Any(char.IsDigit) && head.Any(char.IsLetter))
                    return (head, d.Substring(colon + 1).Trim());
            }
            return (null, d);
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
