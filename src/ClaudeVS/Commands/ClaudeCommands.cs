using System;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using ClaudeVS.Core.Setup;
using ClaudeVS.Core.Tokens;
using ClaudeVS.Diagnostics;
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

            var attachFile = Add(mcs, PackageIds.AttachCurrentFile, (s, e) => Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                string path = VsContext.GetActiveDocumentPath() ?? throw new InvalidOperationException("Open a file in the editor first.");
                var chat = await _package.ShowChatAsync();
                chat.AttachFile(path);
            }));
            attachFile.BeforeQueryStatus += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                ((OleMenuCommand)s).Enabled = VsContext.GetActiveDocumentPath() != null;
            };

            Add(mcs, PackageIds.AttachErrorList, (s, e) => Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var errors = VsContext.GetErrors();
                if (errors.Count == 0)
                {
                    Message("Claude Code", "The Error List has no errors to attach.", OLEMSGICON.OLEMSGICON_INFO);
                    return;
                }
                var chat = await _package.ShowChatAsync();
                chat.AttachErrors(errors);
            }));

            Add(mcs, PackageIds.ShowSavingsReport, (s, e) => Run(ShowSavingsReportAsync));
            Add(mcs, PackageIds.ExportUsageCsv, (s, e) => Run(ExportUsageCsvAsync));
            Add(mcs, PackageIds.SaveSavingsReport, (s, e) => Run(SaveSavingsReportAsync));
            Add(mcs, PackageIds.NewChatWindow, (s, e) => Run(async () => await _package.NewChatWindowAsync()));
            Add(mcs, PackageIds.FocusLastMessage, (s, e) => Run(async () =>
            {
                var chat = await _package.ShowChatAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                chat.FocusLastMessage();
            }));

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

        /// <summary>Writes the savings report (and a router replay, if prompt previews were logged) to Output > Claude Code.</summary>
        private static async Task ShowSavingsReportAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string solution = VsContext.GetSolutionDirectory();
            var prices = _package.Options.ToPriceTable();
            var log = ToolWindow.ClaudeChatControl.Usage;

            string text = await Task.Run(() => SavingsReportText.Full(log.ReadAll(), prices, solution, DateTime.UtcNow, log.Folder));

            ClaudeOutputPane.WriteBlock("\n" + new string('=', 60) + "\n" + text);
            ClaudeOutputPane.Activate();
        }

        /// <summary>Saves the same report as a Markdown file, to attach to a ticket or share.</summary>
        private static async Task SaveSavingsReportAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var log = ToolWindow.ClaudeChatControl.Usage;
            if (!log.HasAny)
            {
                Message("Claude Code", "No usage logged yet. Turn on Options > Diagnostics > Keep local usage log.", OLEMSGICON.OLEMSGICON_INFO);
                return;
            }
            string solution = VsContext.GetSolutionDirectory();
            var prices = _package.Options.ToPriceTable();
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save savings report as Markdown",
                FileName = "claude-savings-" + DateTime.Now.ToString("yyyyMMdd") + ".md",
                DefaultExt = ".md",
                Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog() != true) return;
            string target = dlg.FileName;
            try
            {
                await Task.Run(() =>
                {
                    var now = DateTime.UtcNow;
                    // The log's folder is left out: the file is for sharing
                    File.WriteAllText(target, SavingsReportText.Markdown(SavingsReportText.Full(log.ReadAll(), prices, solution, now), now), new System.Text.UTF8Encoding(false));
                });
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ClaudeOutputPane.WriteLine("Saved the savings report to " + target);
                OpenFile(target);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Message("Claude Code", "Could not save the report: " + ex.Message, OLEMSGICON.OLEMSGICON_WARNING);
            }
        }

        /// <summary>Saves the usage log (all solutions) as CSV, one row per turn, for a spreadsheet.</summary>
        private static async Task ExportUsageCsvAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var log = ToolWindow.ClaudeChatControl.Usage;
            if (!log.HasAny)
            {
                Message("Claude Code", "No usage logged yet. Turn on Options > Diagnostics > Keep local usage log.", OLEMSGICON.OLEMSGICON_INFO);
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export usage as CSV",
                FileName = "claude-usage-" + DateTime.Now.ToString("yyyyMMdd") + ".csv",
                DefaultExt = ".csv",
                Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog() != true) return;
            string target = dlg.FileName;
            try
            {
                // UTF-8 with a BOM, so Excel reads non-ASCII prompts and paths correctly.
                int rows = await Task.Run(() =>
                {
                    using (var w = new System.IO.StreamWriter(target, false, new System.Text.UTF8Encoding(true)))
                        return UsageCsv.Write(log.ReadAll(), w);
                });
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ClaudeOutputPane.WriteLine($"Exported {rows} turns to {target}");
                Message("Claude Code", $"Exported {rows} turns to\n{target}", OLEMSGICON.OLEMSGICON_INFO);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Message("Claude Code", "Could not export usage: " + ex.Message, OLEMSGICON.OLEMSGICON_WARNING);
            }
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
