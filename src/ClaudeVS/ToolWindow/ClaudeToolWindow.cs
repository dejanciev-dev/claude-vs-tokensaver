using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeVS.ToolWindow
{
    [Guid(PackageIds.ToolWindowGuidString)]
    public sealed class ClaudeToolWindow : ToolWindowPane
    {
        public ClaudeToolWindow() : base(null)
        {
            Caption = "Claude Code";
            Content = new ClaudeChatControl();
        }

        /// <summary>
        /// Windows that VS restores at startup are created without going through ShowChatInstanceAsync, so read
        /// the instance number from the frame to get the right caption and id.
        /// </summary>
        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Frame is IVsWindowFrame frame &&
                frame.GetProperty((int)__VSFPROPID.VSFPROPID_MultiInstanceToolNum, out object value) == VSConstants.S_OK &&
                value is int id && Content is ClaudeChatControl chat)
            {
                chat.InstanceId = id;
                if (id > 0) Caption = $"Claude Code ({id + 1})";
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) (Content as ClaudeChatControl)?.Dispose();
            base.Dispose(disposing);
        }
    }
}
