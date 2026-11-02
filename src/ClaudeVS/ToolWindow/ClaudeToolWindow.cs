using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

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

        protected override void Dispose(bool disposing)
        {
            if (disposing) (Content as ClaudeChatControl)?.Dispose();
            base.Dispose(disposing);
        }
    }
}
