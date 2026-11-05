using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ClaudeVS.Commands;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// The working folder: where Claude Code runs, and what every relative path in the chat is relative to. It is
    /// the solution folder unless you pick another one (a subfolder, or a different repository) from the folder
    /// line under the title. Claude Code keeps conversations per folder, so a new folder starts a new conversation.
    /// </summary>
    public partial class ClaudeChatControl
    {
        private readonly Button _folderChip = ChatTheme.Chip("Working folder: where Claude Code runs. Click to change it.");
        private ChatPopup _folderMenu;
        private string _folderOverride;          // the folder you picked
        private string _folderOverrideSolution;  // … for this solution ("" = no solution)

        private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>The folder a new conversation runs in: your pick for this solution, else the solution folder.</summary>
        private string WorkFolder
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                string solution = VsContext.GetSolutionDirectory();
                if (_folderOverride != null && string.Equals(_folderOverrideSolution, solution ?? "", StringComparison.OrdinalIgnoreCase)
                    && System.IO.Directory.Exists(_folderOverride))
                    return _folderOverride;
                return solution ?? UserProfile;
            }
        }

        private void InitFolderChip()
        {
            _folderChip.FontSize = 11;
            _folderChip.Margin = new Thickness(-4, 1, 0, 0);
            _folderChip.HorizontalAlignment = HorizontalAlignment.Left;
            _folderChip.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); ShowFolderMenu(); };
            _folderMenu = new ChatPopup(_folderChip, withFilter: false, width: 380, placement: PlacementMode.Bottom);
            IsVisibleChanged += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (IsVisible) UpdateFolderChip(); // the solution may have changed while the window was hidden
            };
        }

        /// <summary>The folder line: the running conversation's folder, or where the next one will run.</summary>
        private void UpdateFolderChip()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dir = _sessionDir ?? WorkFolder;
            ChatTheme.SetChip(_folderChip, "", FolderLabel(dir) + "  ▾");
            _folderChip.ToolTip = $"Working folder: {dir}\nClaude Code runs here; paths in the chat are relative to it. Click to change it.";
        }

        /// <summary>"linq-samples", "linq-samples/src/App" inside the solution, "~/repos/other" elsewhere.</summary>
        private static string FolderLabel(string dir)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string solution = VsContext.GetSolutionDirectory();
            string full = dir.TrimEnd('\\', '/');
            if (solution != null)
            {
                string sol = solution.TrimEnd('\\', '/');
                string name = System.IO.Path.GetFileName(sol);
                if (string.Equals(full, sol, StringComparison.OrdinalIgnoreCase)) return name;
                if (full.StartsWith(sol + "\\", StringComparison.OrdinalIgnoreCase)) return name + "/" + full.Substring(sol.Length + 1).Replace('\\', '/');
            }
            string home = UserProfile.TrimEnd('\\');
            if (full.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase)) return "~/" + full.Substring(home.Length + 1).Replace('\\', '/');
            return full.Replace('\\', '/');
        }

        private void ShowFolderMenu()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            UpdateFolderChip();
            string dir = _sessionDir ?? WorkFolder;
            string solution = VsContext.GetSolutionDirectory();
            bool custom = solution != null && !string.Equals(dir.TrimEnd('\\'), solution.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            var entries = new List<ChatPopup.Entry>
            {
                new ChatPopup.Entry { Group = "Working folder", Glyph = "", Label = FolderLabel(dir), Description = dir, Detail = custom ? "chosen" : "solution",
                    Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); OpenInExplorer(dir); } },
                new ChatPopup.Entry { Group = "Change", Glyph = "", Label = "Choose another folder…", Description = "A subfolder or another repository; starts a new conversation",
                    Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); BrowseForWorkFolder(dir); } },
            };
            if (custom)
                entries.Add(new ChatPopup.Entry { Group = "Change", Glyph = "", Label = "Use the solution folder", Description = solution,
                    Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SetWorkFolder(null); } });
            entries.Add(new ChatPopup.Entry { Group = "Folder", Glyph = "", Label = "Open in File Explorer", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); OpenInExplorer(dir); } });
            entries.Add(new ChatPopup.Entry { Group = "Folder", Glyph = "", Label = "Copy path", Description = dir, Run = () =>
            {
                try { Clipboard.SetText(dir); } catch (Exception) { Append("The clipboard is busy; try again."); }
            } });
            _folderMenu.Show(entries, _folderChip);
        }

        private void BrowseForWorkFolder(string start)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string picked = VsContext.BrowseForFolder("Working folder for Claude Code", start);
            if (picked != null) SetWorkFolder(picked);
        }

        /// <summary>Use <paramref name="folder"/> (null: the solution folder) from the next prompt on.</summary>
        private void SetWorkFolder(string folder)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_busy) { Append("Stop Claude first (Esc), then change the folder."); return; }
            string solution = VsContext.GetSolutionDirectory();
            if (folder != null && solution != null && string.Equals(folder.TrimEnd('\\'), solution.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) folder = null;
            string before = _sessionDir ?? WorkFolder;
            _folderOverride = folder;
            _folderOverrideSolution = solution ?? "";
            string now = WorkFolder;
            if (!string.Equals(before.TrimEnd('\\'), now.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                bool hadConversation = _session != null || _resumeId != null;
                if (hadConversation) NewChat(); // Claude Code keeps conversations per folder
                Append($"Working folder: {now}" + (hadConversation ? "\nClaude Code keeps conversations per folder, so this is a new conversation." : ""));
            }
            UpdateFolderChip();
        }

        private void OpenInExplorer(string dir)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\""); }
            catch (Exception ex) { AppendError("[folder] ", ex.Message); }
        }
    }
}
