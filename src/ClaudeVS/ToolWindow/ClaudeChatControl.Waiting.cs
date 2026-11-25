using System;
using ClaudeVS.Core.Cli;
using ClaudeVS.Diagnostics;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// A chat out of view that waits for you (Phase 93): as VS Code's 2.1.331 fix for "a missing notification when a
    /// conversation running behind another one … needs a permission or an answer" and 2.1.332's "a hidden panel's
    /// notice not naming the waiting session". An info bar in VS's main window names the chat and what it waits for;
    /// *Show* brings the chat up. It goes when the chat comes into view, or the request is answered or dropped.
    /// </summary>
    public sealed partial class ClaudeChatControl : IVsInfoBarUIEvents
    {
        private IVsInfoBarUIElement _waitingBar;
        private uint _waitingBarCookie;
        private bool _waitingWatch;

        /// <summary>After a request is shown: when this chat isn't in view, the info bar says it waits.</summary>
        private void NoticeIfOutOfView(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_waitingWatch)
            {
                _waitingWatch = true;
                IsVisibleChanged += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); if (IsVisible) ClearWaitingNotice(); };
            }
            if (IsVisible) return;
            ClearWaitingNotice();
            try
            {
                var shell = ServiceProvider.GlobalProvider.GetService(typeof(SVsShell)) as IVsShell;
                var factory = ServiceProvider.GlobalProvider.GetService(typeof(SVsInfoBarUIFactory)) as IVsInfoBarUIFactory;
                if (shell == null || factory == null) return;
                if (shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object hostObj) != Microsoft.VisualStudio.VSConstants.S_OK
                    || !(hostObj is IVsInfoBarHost host)) return;
                var model = new InfoBarModel(
                    new[] { new InfoBarTextSpan(SessionNotices.WaitingOutOfView(_fullTitle, ev.ToolName) + " ") },
                    new[] { new InfoBarHyperlink("Show") },
                    KnownMonikers.StatusInformation,
                    isCloseButtonVisible: true);
                var bar = factory.CreateInfoBar(model);
                if (bar == null) return;
                bar.Advise(this, out _waitingBarCookie);
                host.AddInfoBar(bar);
                _waitingBar = bar;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { ClaudeOutputPane.WriteLine("Waiting notice: " + ex.Message); }
        }

        /// <summary>The request was answered or dropped, or the chat came into view: the info bar goes.</summary>
        private void ClearWaitingNotice()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var bar = _waitingBar;
            if (bar == null) return;
            _waitingBar = null;
            try { bar.Unadvise(_waitingBarCookie); bar.Close(); }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { ClaudeOutputPane.WriteLine("Waiting notice: " + ex.Message); }
        }

        void IVsInfoBarUIEvents.OnClosed(IVsInfoBarUIElement infoBarUIElement)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (ReferenceEquals(infoBarUIElement, _waitingBar)) { _waitingBar = null; try { infoBarUIElement.Unadvise(_waitingBarCookie); } catch (Exception) { } }
        }

        void IVsInfoBarUIEvents.OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { WindowFrame?.Show(); }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { ClaudeOutputPane.WriteLine("Waiting notice: " + ex.Message); }
            ClearWaitingNotice();
        }
    }
}
