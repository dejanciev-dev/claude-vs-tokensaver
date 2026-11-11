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
    [InstalledProductRegistration("Claude Code for Visual Studio — Token Saver", "Token-efficient Claude Code chat for Visual Studio. Community project, not affiliated with Anthropic.", "0.9.0")]
    [Guid(PackageIds.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Docked next to Solution Explorer by default.
    [ProvideToolWindow(typeof(ClaudeToolWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057", MultiInstances = true)]
    [ProvideOptionPage(typeof(ClaudeOptionsPage), "Claude Code", "General", 0, 0, true)]
    public sealed class ClaudeVSPackage : AsyncPackage
    {
        public static ClaudeVSPackage Instance { get; private set; }

        public ClaudeOptionsPage Options => (ClaudeOptionsPage)GetDialogPage(typeof(ClaudeOptionsPage));

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            Instance = this;
            // What earlier runs learned about characters per token (a small file next to the usage log)
            Core.Tokens.TokenEstimate.UseStore(Core.Tokens.TokenEstimate.DefaultStorePath);
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await Diagnostics.ClaudeOutputPane.InitializeAsync(this, cancellationToken);
            await ClaudeCommands.InitializeAsync(this);
        }

        /// <summary>
        /// Shows the chat that commands (Send Selection, Attach...) should go to: the chat window used most
        /// recently, or the first one.
        /// </summary>
        public async System.Threading.Tasks.Task<ClaudeChatControl> ShowChatAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            int id = ClaudeChatControl.LastActive?.InstanceId ?? 0;
            return await ShowChatInstanceAsync(id);
        }

        /// <summary>Opens another, independent chat window (its own claude process and conversation).</summary>
        public async System.Threading.Tasks.Task<ClaudeChatControl> NewChatWindowAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            for (int id = 1; id < 100; id++)
            {
                if (FindToolWindow(typeof(ClaudeToolWindow), id, false) == null)
                    return await ShowChatInstanceAsync(id);
            }
            throw new InvalidOperationException("Too many Claude Code windows are open.");
        }

        private async System.Threading.Tasks.Task<ClaudeChatControl> ShowChatInstanceAsync(int id)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            ToolWindowPane window = await ShowToolWindowAsync(typeof(ClaudeToolWindow), id, true, DisposalToken);
            if (window?.Frame == null) throw new NotSupportedException("Cannot create the Claude Code window.");
            var chat = (ClaudeChatControl)window.Content;
            chat.InstanceId = id;
            if (id > 0) window.Caption = $"Claude Code ({id + 1})";
            return chat;
        }
    }
}
