using System;
using System.Runtime.InteropServices;
using System.Threading;
using ClaudeVS.Commands;
using ClaudeVS.Options;
using ClaudeVS.ToolWindow;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClaudeVS
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Claude Code for Visual Studio — Token Saver", "Token-efficient Claude Code chat for Visual Studio. Community project, not affiliated with Anthropic.", "0.1.0")]
    [Guid(PackageIds.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Docked next to Solution Explorer by default.
    [ProvideToolWindow(typeof(ClaudeToolWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")]
    [ProvideOptionPage(typeof(ClaudeOptionsPage), "Claude Code", "General", 0, 0, true)]
    public sealed class ClaudeVSPackage : AsyncPackage
    {
        public static ClaudeVSPackage Instance { get; private set; }

        public ClaudeOptionsPage Options => (ClaudeOptionsPage)GetDialogPage(typeof(ClaudeOptionsPage));

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            Instance = this;
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await ClaudeCommands.InitializeAsync(this);
        }

        public async System.Threading.Tasks.Task<ClaudeChatControl> ShowChatAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            ToolWindowPane window = await ShowToolWindowAsync(typeof(ClaudeToolWindow), 0, true, DisposalToken);
            if (window?.Frame == null) throw new NotSupportedException("Cannot create the Claude Code window.");
            return (ClaudeChatControl)window.Content;
        }
    }
}
