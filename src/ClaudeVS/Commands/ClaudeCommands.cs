using System;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using ClaudeVS.Core.Setup;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeVS.Commands
{
    internal static class ClaudeCommands
    {
        private static ClaudeVSPackage _package;

        public static async Task InitializeAsync(ClaudeVSPackage package)
        {
            _package = package;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var mcs = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (mcs == null) return;

            Add(mcs, PackageIds.OpenWindow, (s, e) => Run(async () => await _package.ShowChatAsync()));

            var send = Add(mcs, PackageIds.SendSelection, (s, e) => Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var sel = VsContext.GetSelection();
                var chat = await _package.ShowChatAsync();
                chat.AttachSelection(sel);
            }));
            send.BeforeQueryStatus += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                ((OleMenuCommand)s).Visible = VsContext.GetSelection() != null;
            };

            Add(mcs, PackageIds.GenerateClaudeMd, (s, e) => Run(GenerateClaudeMdAsync));
            Add(mcs, PackageIds.InstallTokenSaver, (s, e) => Run(InstallTokenSaverAsync));
        }

        private static OleMenuCommand Add(OleMenuCommandService mcs, int id, EventHandler handler)
        {
            var cmd = new OleMenuCommand(handler, new CommandID(PackageIds.CmdSet, id));
            mcs.AddCommand(cmd);
            return cmd;
        }

        private static void Run(Func<Task> action)
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await action(); }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    Message("Claude Code", ex.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
                }
            }).FileAndForget("ClaudeVS/Commands/Run");
        }

        private static async Task GenerateClaudeMdAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string root = VsContext.GetSolutionDirectory() ?? throw new InvalidOperationException("Open a solution or folder first.");
            string path = Path.Combine(root, ClaudeMdGenerator.FileName);

            string content = await Task.Run(() => ClaudeMdGenerator.Generate(SolutionScanner.Scan(root)));
            int tokens = ClaudeMdGenerator.EstimateTokens(content);

            if (File.Exists(path))
            {
                int existing = ClaudeMdGenerator.EstimateTokens(File.ReadAllText(path));
                string draft = Path.Combine(root, "CLAUDE.generated.md");
                File.WriteAllText(draft, content);
                Message("Claude Code", $"CLAUDE.md already exists (~{existing} tokens per request). A lean draft (~{tokens} tokens) was saved as CLAUDE.generated.md for you to compare and merge.", OLEMSGICON.OLEMSGICON_INFO);
                OpenFile(draft);
                return;
            }

            File.WriteAllText(path, content);
            Message("Claude Code", $"Created CLAUDE.md (~{tokens} tokens, loaded on every request). Add only a few convention lines.", OLEMSGICON.OLEMSGICON_INFO);
            OpenFile(path);
        }

        private static async Task InstallTokenSaverAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string root = VsContext.GetSolutionDirectory() ?? throw new InvalidOperationException("Open a solution or folder first.");
            bool autoApprove = _package.Options.AutoApproveHookRewrites;
            var result = await Task.Run(() => TokenSaverSetup.Apply(root, autoApprove));

            string written = result.Written.Count == 0 ? "Nothing to change — already installed." : "Updated: " + string.Join(", ", result.Written);
            Message("Claude Code",
                written + "\n\n• Claude won't read bin/obj/node_modules/packages/dist\n• Shell output capped at "
                + TokenSaverSetup.DefaultBashMaxOutput + " chars\n• dotnet build/test, msbuild and git log run in quiet mode\n\nRestart the chat (New) for the settings to apply. Commit .claude/ to share with your team.",
                OLEMSGICON.OLEMSGICON_INFO);
        }

        private static void OpenFile(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.OpenDocument(_package, path);
        }

        private static void Message(string title, string text, OLEMSGICON icon)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.ShowMessageBox(_package, text, title, icon, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}
