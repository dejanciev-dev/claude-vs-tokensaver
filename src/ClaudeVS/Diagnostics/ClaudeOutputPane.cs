using System;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeVS.Diagnostics
{
    /// <summary>"Claude Code" pane in the Output window: route decisions, errors and (optionally) raw CLI traffic.</summary>
    internal static class ClaudeOutputPane
    {
        private static readonly Guid PaneGuid = new Guid("6f3b5a2e-8d41-4c1e-9b7a-2f5d0c3e9a14");
        private static IVsOutputWindowPane _pane;

        public static async Task InitializeAsync(AsyncPackage package, CancellationToken ct)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            if (!(await package.GetServiceAsync(typeof(SVsOutputWindow)) is IVsOutputWindow output)) return;
            Guid guid = PaneGuid;
            output.CreatePane(ref guid, "Claude Code", fInitVisible: 1, fClearWithSolution: 0);
            output.GetPane(ref guid, out _pane);
        }

        /// <summary>Thread-safe; does nothing before initialization.</summary>
        public static void WriteLine(string message)
        {
            var pane = _pane;
            if (pane == null) return;
            // OutputStringThreadSafe is documented as callable from any thread; the analyzer can't know that.
#pragma warning disable VSTHRD010
            pane.OutputStringThreadSafe($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
#pragma warning restore VSTHRD010
        }
    }
}
