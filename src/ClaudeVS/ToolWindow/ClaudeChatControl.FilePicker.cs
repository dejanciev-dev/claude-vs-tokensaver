using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeVS.Commands;
using System.Collections.Generic;
using ClaudeVS.Core.Chat;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Context;
using ClaudeVS.Core.Tokens;
using ClaudeVS.Diagnostics;
using ClaudeVS.Options;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.ToolWindow
{
    /// <summary>The @ file picker: the solution's files, filtered as you type.</summary>
    public sealed partial class ClaudeChatControl
    {
        private Popup BuildFilePicker()
        {
            _fileList.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _fileList.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _fileList.MouseDoubleClick += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); PickFile(); };
            var border = new Border { BorderThickness = new Thickness(1), Child = _fileList };
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            return new Popup { PlacementTarget = _prompt, Placement = PlacementMode.Top, StaysOpen = false, Child = border };
        }

        /// <summary>Shows matching solution files while an "@query" is being typed at the caret.</summary>
        private void UpdateFilePicker()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _mention = AtMention.Find(_prompt.Text, _prompt.CaretIndex);
            if (_mention == null) { _filePopup.IsOpen = false; return; } // common case: no DTE call per keystroke
            string dir = VsContext.GetSolutionDirectory() == null ? null : WorkFolder; // the folder Claude Code runs in
            if (dir == null) { _filePopup.IsOpen = false; return; }

            if (_fileIndex == null || !string.Equals(_fileIndexDir, dir, StringComparison.OrdinalIgnoreCase))
            {
                StartFileIndex(dir);
                _fileList.ItemsSource = new[] { "Indexing solution files…" };
                _fileList.IsEnabled = false;
                _filePopup.IsOpen = true;
                return;
            }

            var hits = _fileIndex.SearchWithSizes(_mention.Query, 10); // with "~3.2k tok": what reading it would cost
            _fileList.IsEnabled = hits.Count > 0;
            _fileList.ItemsSource = hits.Count > 0 ? (System.Collections.IEnumerable)hits : new[] { "No matching file" };
            _fileList.SelectedIndex = hits.Count > 0 ? 0 : -1;
            _filePopup.Width = Math.Max(260, _prompt.ActualWidth);
            _filePopup.IsOpen = true;
        }

        private void StartFileIndex(string dir)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_indexingDir != null && string.Equals(_indexingDir, dir, StringComparison.OrdinalIgnoreCase)) return;
            _indexingDir = dir;
            Jtf.RunAsync(async () =>
            {
                var index = await System.Threading.Tasks.Task.Run(() => FileIndex.Build(dir));
                await Jtf.SwitchToMainThreadAsync();
                _indexingDir = null;
                _fileIndex = index;
                _fileIndexDir = dir;
                ClaudeOutputPane.WriteLine($"Indexed {index.Count} files for @ picker{(index.Truncated ? " (capped)" : "")}");
                UpdateFilePicker();
            }).FileAndForget("ClaudeVS/ChatControl/FileIndex");
        }

        private bool HandleFilePickerKey(Key key)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int count = _fileList.IsEnabled ? _fileList.Items.Count : 0;
            switch (key)
            {
                case Key.Down when count > 0:
                    _fileList.SelectedIndex = (_fileList.SelectedIndex + 1) % count;
                    _fileList.ScrollIntoView(_fileList.SelectedItem);
                    return true;
                case Key.Up when count > 0:
                    _fileList.SelectedIndex = (_fileList.SelectedIndex - 1 + count) % count;
                    _fileList.ScrollIntoView(_fileList.SelectedItem);
                    return true;
                case Key.Enter when count > 0:
                case Key.Tab when count > 0:
                    PickFile();
                    return true;
                case Key.Escape:
                    _filePopup.IsOpen = false;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Removes the "@query" text and attaches the chosen file as a path reference.</summary>
        private void PickFile()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(_fileList.SelectedItem is FileHit hit) || _mention == null || _fileIndex == null || !_fileList.IsEnabled) return;
            string rel = hit.Path;
            var m = _mention;
            _filePopup.IsOpen = false;
            _prompt.Text = _prompt.Text.Remove(m.Start, m.Length);
            _prompt.CaretIndex = m.Start;
            AttachFile(_fileIndex.FullPath(rel));
        }
    }
}
