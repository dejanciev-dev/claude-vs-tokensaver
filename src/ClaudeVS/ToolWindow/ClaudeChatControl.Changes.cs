using System;
using ClaudeVS.Commands;
using ClaudeVS.Core.Context;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// "Files changed" under each answer: every file the turn added or changed (Edit, MultiEdit, Write and shell
    /// heredocs), with +added −removed, a link that opens the file and one that opens Visual Studio's diff of the
    /// file before the turn against now.
    /// </summary>
    public partial class ClaudeChatControl
    {
        private readonly TurnChanges _turnChanges = new TurnChanges();

        private void ShowTurnChanges()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_turnChanges.Count == 0) return;
            var files = _turnChanges.Collect(p => ReadSnapshot(p), p => System.IO.File.Exists(p));
            _turnChanges.Clear();
            if (files.Count == 0) return;
            _view.AppendChangedFiles(files, _sessionDir, OpenTurnDiff, path => { ThreadHelper.ThrowIfNotOnUIThread(); OpenChangedFile(path); });
        }

        /// <summary>Visual Studio's diff window: the file before the turn (left) against the file now (right).</summary>
        private void OpenTurnDiff(ChangedFile f)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string name = System.IO.Path.GetFileName(f.Path);
            try
            {
                if (f.Before == null) { Append($"\n  (no diff for {name}: its earlier content could not be read)\n"); return; }
                if (f.IsDeleted) { VsContext.OpenDiff(f.Before, "Before (deleted since)", f.Path, "", "Now: deleted", $"{name}: deleted"); return; }
                VsContext.OpenDiff(f.Before, f.IsNew ? "New file" : "Before Claude's changes", f.Path, null, "Now", $"{name}: Claude's changes");
            }
            catch (Exception ex) { AppendError("[diff] ", ex.Message); }
        }

        private void OpenChangedFile(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (System.IO.File.Exists(path)) VsContext.OpenFile(path);
                else Append($"\n  ({System.IO.Path.GetFileName(path)} no longer exists)\n");
            }
            catch (Exception ex) { AppendError("[open] ", ex.Message); }
        }
    }
}
