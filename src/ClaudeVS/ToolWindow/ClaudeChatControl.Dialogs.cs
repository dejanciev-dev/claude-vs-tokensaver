using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ClaudeVS.Commands;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Setup;
using ClaudeVS.Core.Tokens;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// The CLI's own dialogs as lists in the chat: Status, Permission rules, Hooks, Memory and instructions, Output
    /// style, Account and usage. Each is one control request, with no model call.
    /// </summary>
    public sealed partial class ClaudeChatControl
    {
        private IEnumerable<ChatPopup.Entry> DialogEntries() => new[]
        {
            new ChatPopup.Entry { Group = "Claude Code", Label = "Status", Description = "Version, account, model, setting sources", Run = ShowStatus },
            new ChatPopup.Entry { Group = "Claude Code", Label = "Account & usage", Description = "Plan, usage limits and when they reset, this session's cost", Run = ShowAccountUsage },
            new ChatPopup.Entry { Group = "Claude Code", Label = "Memory & instructions…", Description = "CLAUDE.md files and saved memories, with what they cost per request", Run = ShowMemory },
            new ChatPopup.Entry { Group = "Claude Code", Label = "Output style…", Description = "How Claude answers: default, concise, explanatory…", Run = ShowOutputStyles },
            new ChatPopup.Entry { Group = "Claude Code", Label = "Permission rules", Description = "What Claude may do without asking, must ask for, or may not do", Run = ShowPermissionRules },
            new ChatPopup.Entry { Group = "Claude Code", Label = "Hooks", Description = "Commands Claude Code runs on events, such as before a tool call", Run = ShowHooks },
        };

        /// <summary>Shows "Loading…", reads the data from the CLI, then shows the list (or an error line).</summary>
        private void ShowCliDialog<T>(string what, Func<ClaudeSession, Task<T>> load, Func<T, List<ChatPopup.Entry>> build, Func<T, string> footer) where T : class
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsureSession(ManageDir); // reading makes no model call
            var session = _session;
            _manage.Footer = null;
            _manage.Show(new[] { new ChatPopup.Entry { Label = $"Loading {what}…", Enabled = false } }, _inputBox);
            Jtf.RunAsync(async () =>
            {
                T data = null;
                string error = null;
                try { data = await load(session); }
                catch (InvalidOperationException ex) { error = ex.Message; }
                await Jtf.SwitchToMainThreadAsync();
                if (session != _session) return;
                if (data == null) { _manage.Close(); AppendError("[claude code] ", error ?? $"Claude Code did not return the {what}."); return; }
                _manage.Footer = footer(data);
                _manage.Show(build(data), _inputBox, keepFilter: true);
            }).FileAndForget("ClaudeVS/ChatControl/Dialog");
        }

        private void ShowStatus() => ShowCliDialog("status", s => s.GetStatusAsync(),
            rows => rows.Select(r => new ChatPopup.Entry
            {
                Group = r.Section,
                Label = r.Label,
                Description = r.Value,
                Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); CopyText(r.Value, r.Label); },
            }).ToList(),
            _ => "From Claude Code's /status · click a row to copy its value");

        private void ShowAccountUsage() => ShowCliDialog("usage", s => s.GetUsageAsync(),
            u =>
            {
                var now = DateTime.UtcNow;
                var list = new List<ChatPopup.Entry>
                {
                    new ChatPopup.Entry { Group = "Plan", Label = u.PlanName, Description = u.ExtraUsageEnabled ? "Extra usage is on" : null, Enabled = false },
                };
                list.AddRange(u.Limits.Select(l => new ChatPopup.Entry
                {
                    Group = "Usage limits",
                    Label = l.Name,
                    Detail = l.Percent + "%",
                    Description = (l.ResetsAt.HasValue ? "Resets " + RateLimitInfo.When(l.ResetsAt.Value, now) : "")
                        + (l.IsActive ? (l.ResetsAt.HasValue ? " · " : "") + "the limit that applies now" : ""),
                    Enabled = false,
                }));
                if (u.Limits.Count == 0) list.Add(new ChatPopup.Entry { Group = "Usage limits", Label = "No plan limits reported", Description = "API keys are billed per token instead", Enabled = false });
                list.Add(new ChatPopup.Entry { Group = "This session", Label = "Cost", Detail = "$" + u.SessionCostUsd.ToString("0.000"), Enabled = false });
                list.Add(new ChatPopup.Entry { Group = "This session", Label = "Lines changed", Detail = $"+{u.LinesAdded} −{u.LinesRemoved}", Enabled = false });
                list.Add(new ChatPopup.Entry { Group = "This session", Label = "Savings report", Description = "Cost per model, cache hits and tokens saved", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); _manage.Close(); VsContext.ExecuteCommand("Tools.ShowSavingsReport"); } });
                return list;
            },
            _ => "From Claude Code (get_usage); no model call");

        private void ShowMemory() => ShowCliDialog("memory files", s => s.GetMemoryAsync(),
            d =>
            {
                var list = new List<ChatPopup.Entry>();
                foreach (var e in d.Entries)
                {
                    var entry = e;
                    bool everyRequest = e.Kind == "user" || e.Kind == "project" || e.Label == "MEMORY.md";
                    int tokens = !e.IsFolder && e.Exists ? EstimateFileTokens(e.Path) : 0;
                    list.Add(new ChatPopup.Entry
                    {
                        Group = e.IsFolder ? "Folders" : e.Kind == "memory" ? "Saved memories" : "Instructions",
                        Label = e.Label,
                        Description = e.Description,
                        Detail = e.IsFolder ? null : !e.Exists ? "create" : tokens > 0 ? $"~{TokenMeter.Format(tokens)} tok{(everyRequest ? "" : " when read")}" : null,
                        Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); _manage.Close(); OpenMemoryEntry(entry); },
                    });
                }
                if (list.Count == 0) list.Add(new ChatPopup.Entry { Label = "No memory files", Enabled = false });
                return list;
            },
            d => "Instruction files and the memory index are sent with every request"
                 + (d.AutoMemoryStatus != null ? $" · auto memory {d.AutoMemoryStatus}" : "") + " · click to open");

        private static int EstimateFileTokens(string path)
        {
            try { return File.Exists(path) && new FileInfo(path).Length < 2_000_000 ? ClaudeMdGenerator.EstimateTokens(File.ReadAllText(path)) : 0; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return 0; }
        }

        private void OpenMemoryEntry(MemoryEntry e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (e.IsFolder)
                {
                    Directory.CreateDirectory(e.Path);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + e.Path.TrimEnd('\\') + "\"") { UseShellExecute = true });
                    return;
                }
                if (!File.Exists(e.Path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(e.Path));
                    File.WriteAllText(e.Path, "");
                    Append($"Created {e.Path}. Keep it short: it is sent with every request.");
                }
                VsContext.OpenFile(e.Path);
            }
            catch (Exception ex) { AppendError("[memory] ", ex.Message); }
        }

        private void ShowOutputStyles() => ShowCliDialog("output styles", s => s.GetOutputStylesAsync(),
            styles => styles.Available.Select(name => new ChatPopup.Entry
            {
                Label = name,
                Description = OutputStyles.Describe(name),
                Checked = string.Equals(name, styles.Current, StringComparison.OrdinalIgnoreCase),
                Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); if (!string.Equals(name, styles.Current, StringComparison.OrdinalIgnoreCase)) SetOutputStyle(name); },
            }).ToList(),
            _ => "Saved for this project in .claude/settings.local.json · a change writes the prompt cache again once");

        private void SetOutputStyle(string style)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var session = _session;
            if (session == null) return;
            Jtf.RunAsync(async () =>
            {
                string error = null;
                try { if (!await session.SetOutputStyleAsync(style)) error = "Claude Code did not answer."; }
                catch (InvalidOperationException ex) { error = ex.Message; }
                await Jtf.SwitchToMainThreadAsync();
                if (error != null) { AppendError("[output style] ", error); return; }
                Append($"Output style set to {style} for this project.");
                ShowOutputStyles();
            }).FileAndForget("ClaudeVS/ChatControl/OutputStyle");
        }

        private void ShowPermissionRules()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dir = ManageDir;
            ShowCliDialog("permission rules", s => s.GetPermissionRulesAsync(),
                rules =>
                {
                    var list = rules.OrderBy(r => PermissionRuleRow.Order(r.Behavior)).Select(r => new ChatPopup.Entry
                    {
                        Group = r.Behavior == "deny" ? "Deny" : r.Behavior == "ask" ? "Ask first" : "Allow",
                        Label = r.Rule,
                        Description = string.Join(" · ", new[] { r.Description, SettingsSources.Label(r.Source) }.Where(x => !string.IsNullOrEmpty(x))),
                        Run = SettingsOpener(r.Source, dir),
                        Enabled = SettingsSources.PathFor(r.Source, dir) != null,
                    }).ToList();
                    if (list.Count == 0) list.Add(new ChatPopup.Entry { Label = "No permission rules", Description = "Add them under \"permissions\" in .claude/settings.json", Enabled = false });
                    list.AddRange(SettingsFileEntries(dir));
                    return list;
                },
                _ => "From Claude Code's settings files · click a rule to open its file");
        }

        private void ShowHooks()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dir = ManageDir;
            ShowCliDialog("hooks", s => s.GetHooksAsync(),
                hooks =>
                {
                    var list = hooks.Select(h => new ChatPopup.Entry
                    {
                        Group = h.Event,
                        Label = h.Text ?? h.Type,
                        Description = string.Join(" · ", new[] { string.IsNullOrEmpty(h.Matcher) ? null : "for " + h.Matcher, h.SourceLabel ?? SettingsSources.Label(h.Source) }.Where(x => !string.IsNullOrEmpty(x))),
                        Run = SettingsOpener(h.Source, dir),
                        Enabled = SettingsSources.PathFor(h.Source, dir) != null,
                    }).ToList();
                    if (list.Count == 0) list.Add(new ChatPopup.Entry { Label = "No hooks", Description = "Tools → Claude Code → Install Token-Saving Settings and Hooks adds a quiet-build hook", Enabled = false });
                    list.AddRange(SettingsFileEntries(dir));
                    return list;
                },
                _ => "Hooks run commands on Claude Code's events · click one to open its settings file");
        }

        private List<ChatPopup.Entry> SettingsFileEntries(string dir)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var list = new List<ChatPopup.Entry>();
            foreach (string source in new[] { "projectSettings", "localSettings", "userSettings" })
            {
                list.Add(new ChatPopup.Entry
                {
                    Group = "Settings files",
                    Label = SettingsSources.Label(source),
                    Description = SettingsSources.PathFor(source, dir),
                    Detail = File.Exists(SettingsSources.PathFor(source, dir)) ? null : "create",
                    Run = SettingsOpener(source, dir),
                });
            }
            return list;
        }

        private Action SettingsOpener(string source, string dir) => () =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string path = SettingsSources.PathFor(source, dir);
            if (path == null) return;
            _manage.Close();
            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, "{\n}\n");
                }
                VsContext.OpenFile(path);
            }
            catch (Exception ex) { AppendError("[settings] ", ex.Message); }
        };

        private void CopyText(string text, string what)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { Clipboard.SetText(text ?? ""); Append($"Copied {what}."); }
            catch (Exception) { Append("The clipboard is busy; try again."); }
        }
    }
}
