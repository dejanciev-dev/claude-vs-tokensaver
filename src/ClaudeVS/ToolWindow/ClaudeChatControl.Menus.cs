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
    /// <summary>The + menu, the model and mode chips' menus, and the / actions palette.</summary>
    public sealed partial class ClaudeChatControl
    {
        private void ShowPlusMenu()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _menu.Footer = null;
            _menu.Show(new[]
            {
                new ChatPopup.Entry { Glyph = ChatTheme.IconUpload, Label = "Upload from computer", Description = "Attach files or images as references", Run = UploadFiles },
                new ChatPopup.Entry { Glyph = ChatTheme.IconDocument, Label = "Add context", Description = "Mention a file from this solution (@)", Run = StartMention },
                new ChatPopup.Entry { Glyph = ChatTheme.IconEdit, Label = "Attach current file", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); AttachFile(VsContext.GetActiveDocumentPath()); } },
                new ChatPopup.Entry { Glyph = ChatTheme.IconWarning, Label = "Attach Error List", Run = AttachErrorListFromIde },
                new ChatPopup.Entry { Glyph = ChatTheme.IconGlobe, Label = "Browse the web", Description = "Let Claude search the web for the next prompts", Checked = _browseWeb, Run = () => { _browseWeb = !_browseWeb; UpdateAttachments(focus: true); } },
            }, _plusBtn);
        }

        private void ShowModelMenu()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _menu.Footer = "Auto picks the cheapest model likely to handle each prompt";
            _menu.Show(ModelEntries(), _modelChip);
        }

        private IEnumerable<ChatPopup.Entry> ModelEntries()
        {
            var prices = Options?.ToPriceTable() ?? new PriceTable();
            var models = _session?.Models;
            foreach (ModelChoice m in Enum.GetValues(typeof(ModelChoice)))
            {
                // Claude Code's name ("Haiku 5.5") and the price; a model dearer than Opus (Fable) says so: Auto never
                // picks it, so this is the only place you choose it.
                yield return new ChatPopup.Entry
                {
                    Group = "Model",
                    Label = ModelPickerText.Label(m, models),
                    Description = ModelPickerText.Description(m, models, prices),
                    Checked = _modelChoice == m,
                    Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SetModel(m); },
                };
            }
            string nextModel = ModelPickerText.NextModel(_modelChoice, models, _meterModel?.LastModel);
            foreach (EffortChoice ef in Enum.GetValues(typeof(EffortChoice)))
            {
                bool capped = ClaudeSettings.Above(ef, _effortCap); // above maxEffortLevel: Claude Code would answer lower
                yield return new ChatPopup.Entry
                {
                    Group = "Effort", Label = ef == EffortChoice.Auto ? "Auto (follow the model choice)" : ef.ToString(), Checked = _effortChoice == ef,
                    Enabled = !capped,
                    // a level the model doesn't take: Claude Code uses the next one down, or none
                    Description = capped ? $"Above your Claude Code settings' cap ({_effortCap.Value.ToCli()}, maxEffortLevel)" : ModelPickerText.EffortNote(ef, models, nextModel),
                    Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SetEffort(ef); },
                };
            }
            yield return new ChatPopup.Entry { Group = "Thinking", Label = "Extended thinking", Description = "Off answers faster and cheaper on simple tasks", Checked = _thinking, Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SetThinking(!_thinking); } };
        }

        private void ShowModeMenu()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _menu.Footer = "Shift+Tab to switch";
            _menu.Show(ModeEntries(null), _modeChip);
        }

        private IEnumerable<ChatPopup.Entry> ModeEntries(string group) =>
            ChoiceNames.CycleOrder.Select(m => new ChatPopup.Entry
            {
                Group = group,
                Glyph = ChatTheme.ModeGlyph(m),
                Label = m.Describe().Label,
                Description = m.Describe().Description,
                Checked = _mode == m,
                Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SetMode(m); },
            });

        /// <summary>The / actions palette: context, model, mode, session and custom commands, filterable.</summary>
        private void ShowPalette()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RefreshCommands();
            var entries = new List<ChatPopup.Entry>
            {
                new ChatPopup.Entry { Group = "Context", Label = "Attach file…", Run = UploadFiles },
                new ChatPopup.Entry { Group = "Context", Label = "Mention file from this project…", Detail = "@", Run = StartMention },
                new ChatPopup.Entry { Group = "Context", Label = "Attach current file", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); AttachFile(VsContext.GetActiveDocumentPath()); } },
                new ChatPopup.Entry { Group = "Context", Label = "Attach selection", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); AttachSelection(VsContext.GetSelection()); } },
                new ChatPopup.Entry { Group = "Context", Label = "Attach Error List", Run = AttachErrorListFromIde },
                new ChatPopup.Entry { Group = "Context", Label = "Clear conversation", Description = "Start a new session", Run = NewChat },
                new ChatPopup.Entry { Group = "Context", Label = "Context usage", Description = "What fills the context window", Run = ShowContextBreakdown },
                new ChatPopup.Entry { Group = "Context", Label = "Skills…", Description = "Turn skills on or off, with what each costs", Run = ShowSkills },
                new ChatPopup.Entry { Group = "Context", Label = "MCP servers…", Description = "Turn servers on or off, reconnect", Run = ShowMcpServers },
                new ChatPopup.Entry { Group = "Context", Label = "Settings that cost", Description = "Claude Code settings that make turns dearer, and where to change them", Run = ShowSettingsCheck },
                new ChatPopup.Entry { Group = "Context", Label = "Check token-saving setup", Description = "Is the quiet-build hook, the read-deny rules and the shell output cap still in effect?", Run = ShowSetupCheck },
                new ChatPopup.Entry { Group = "Context", Label = "Compact conversation", Description = "Summarize to shrink the context", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SendText("/compact", routed: false); } },
                new ChatPopup.Entry { Group = "Context", Label = "Resume a session…", Run = ShowHistoryMenu },
                new ChatPopup.Entry { Group = "Context", Label = "Rename session", Run = StartTitleEdit },
                new ChatPopup.Entry { Group = "Context", Label = "Fork this session", Description = "Continue in a new session with this history; the original stays", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); ForkSession(_session?.SessionId ?? _resumeId, _fullTitle, keepTranscript: true); } },
                new ChatPopup.Entry { Group = "Context", Label = "Restart Claude Code", Description = "Keep this conversation and its prompt cache; picks up settings, CLAUDE.md and MCP servers you changed by hand", Run = () => RestartInPlace() },
                new ChatPopup.Entry { Group = "Context", Label = "Rewind…", Detail = "Esc Esc", Description = "Put files back to before one of your prompts", Run = ShowRewindPicker },
                new ChatPopup.Entry { Group = "Context", Label = "Focus view", Description = "Collapse tool calls into one summary row", Checked = Options?.FocusView == true, Run = ToggleFocusView },
                new ChatPopup.Entry { Group = "Context", Label = "Copy last response", Description = "Claude's last answer as Markdown", Run = CopyLastAnswer },
                new ChatPopup.Entry { Group = "Context", Label = "Copy session summary", Description = "Turns, cost, tokens, cache and context as text", Run = CopySessionSummary },
                new ChatPopup.Entry { Group = "Context", Label = "Find in chat…", Detail = "Ctrl+Alt+K, Ctrl+F", Description = "Find text in this conversation", Run = ShowFind },
                new ChatPopup.Entry { Group = "Context", Label = "Focus last message", Detail = "Ctrl+Alt+K, M", Description = "Move the keyboard to Claude's last answer (for screen readers)", Run = FocusLastMessage },
                new ChatPopup.Entry { Group = "Context", Label = "Export conversation…", Description = "Save the whole conversation as Markdown", Run = ExportConversation },
                new ChatPopup.Entry { Group = "Context", Label = "Bookmarks", Description = "Answers you starred", Run = ToggleBookmarksPanel },
            };
            entries.AddRange(ModelEntries().Select(e => { e.Group = "Model · " + e.Group; return e; }));
            entries.AddRange(ModeEntries("Mode"));
            entries.AddRange(_commands.Where(c => !string.Equals(c.Name, "compact", StringComparison.OrdinalIgnoreCase)).Select(c => new ChatPopup.Entry
            {
                Group = c.Source == "skill" ? "Skills" : "Commands",
                Label = "/" + c.Name,
                Description = c.Description,
                Detail = c.Source,
                Run = () =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (c.Source != "skill") { SendText("/" + c.Name, routed: false); return; }
                    // Skills usually take arguments: put the command in the prompt box to finish and send; what you had
                    // typed stays, as its argument (Phase 94)
                    _prompt.Text = SlashCommands.WithCommand(_prompt.Text, c.Name);
                    _prompt.CaretIndex = _prompt.Text.Length;
                    _prompt.Focus();
                },
            }));
            entries.AddRange(DialogEntries());
            entries.Add(new ChatPopup.Entry { Group = "Other", Label = "Show savings report", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); VsContext.ExecuteCommand("Tools.ShowSavingsReport"); } });
            entries.Add(new ChatPopup.Entry { Group = "Other", Label = "Open Output → Claude Code", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); ClaudeOutputPane.Activate(); } });
            entries.Add(new ChatPopup.Entry { Group = "Other", Glyph = null, Label = "Settings", Detail = "Tools → Options", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); ClaudeVSPackage.Instance?.ShowOptionPage(typeof(ClaudeOptionsPage)); } });
            _palette.Show(entries, _inputBox);
        }

        private void UploadFiles()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Attach files for Claude (images are sent with the prompt, other files as references)", InitialDirectory = VsContext.GetSolutionDirectory() ?? "" };
            if (dlg.ShowDialog() != true) return;
            var images = dlg.FileNames.Where(f => ImageTokens.MediaTypeFor(f) != null).ToList();
            foreach (var f in dlg.FileNames.Except(images)) AttachFile(f);
            if (images.Count > 0) AddImages(() => images.Select(ImageAttachments.FromFile).ToList());
        }
    }
}
