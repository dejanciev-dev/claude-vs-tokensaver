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
    /// <summary>Bookmarked answers and the bookmarks panel.</summary>
    public sealed partial class ClaudeChatControl
    {
        private static string BookmarkSolution
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return VsContext.GetSolutionDirectory() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
        }

        /// <summary>The star under an answer: the first click saves a bookmark, the next one removes it.</summary>
        private Func<bool> BookmarkToggle(string prompt, string answer, System.Windows.Documents.Block anchor)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string id = null;
            string dir = _sessionDir ?? BookmarkSolution;          // no process yet when redrawing a resumed conversation
            string sessionId = _session?.SessionId ?? _resumeId;
            return () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    if (id != null)
                    {
                        RemoveBookmark(id);
                        id = null;
                        return false;
                    }
                    var b = Bookmark.Create(dir, sessionId, prompt, answer, DateTime.UtcNow);
                    Bookmarks.Add(b);
                    id = b.Id;
                    if (anchor != null) _bookmarkAnchors[id] = anchor;
                    RefreshBookmarksPanel();
                    return true;
                }
                catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
                {
                    AppendError("[bookmarks] ", Core.SafeFile.Explain(ex, Bookmarks.Path));
                    return id != null;
                }
            };
        }

        private void ToggleBookmarksPanel()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_bookmarks.IsOpen) { _bookmarks.Hide(); return; }
            _bookmarks.Show(Bookmarks.List(BookmarkSolution));
        }

        private void RefreshBookmarksPanel()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_bookmarks.IsOpen) _bookmarks.SetItems(Bookmarks.List(BookmarkSolution));
        }

        /// <summary>Scrolls to the answer when it is in this transcript; otherwise shows the saved copy at the end.</summary>
        private void OpenBookmark(Bookmark b)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_bookmarkAnchors.TryGetValue(b.Id, out var anchor) && _view.ScrollTo(anchor)) return;
            SetEmptyState(false);
            var head = _view.AppendBookmark(b);
            _bookmarkAnchors[b.Id] = head;
            // Scroll once the new blocks are laid out.
            Jtf.RunAsync(async () =>
            {
                await Jtf.SwitchToMainThreadAsync(alwaysYield: true);
                _view.Box.UpdateLayout();
                _view.ScrollTo(head);
            }).FileAndForget("ClaudeVS/ChatControl/OpenBookmark");
        }

        private void RemoveBookmark(string id)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { Bookmarks.Remove(id); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { AppendError("[bookmarks] ", Core.SafeFile.Explain(ex, Bookmarks.Path)); return; }
            _bookmarkAnchors.Remove(id);
            RefreshBookmarksPanel();
        }
    }
}
