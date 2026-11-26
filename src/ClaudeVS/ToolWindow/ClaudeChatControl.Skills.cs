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
    /// <summary>Skills and MCP servers: lists to turn them on and off, and reconnecting a server.</summary>
    public sealed partial class ClaudeChatControl
    {
        /// <summary>Breakdown rows that open a list where their contents can be turned off.</summary>
        private Action CategoryAction(ContextCategory c)
        {
            string id = (c.Id ?? c.Name ?? "").ToLowerInvariant();
            if (id.Contains("skill")) return ShowSkills;
            if (id.Contains("mcp")) return ShowMcpServers;
            return null;
        }

        private string ManageDir
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return _sessionDir ?? WorkFolder;
            }
        }

        /// <summary>
        /// The /skills menu: each skill with its source and what its listing costs on every request; clicking one turns
        /// it on or off (claude edit-skill-overrides, then reload_skills). Locked skills are shown, not changeable.
        /// </summary>
        private void ShowSkills()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dir = ManageDir;
            EnsureSession(dir); // reading the list makes no model call
            var session = _session;
            _manage.Footer = null;
            _manage.Show(new[] { new ChatPopup.Entry { Label = "Loading skills…", Enabled = false } }, _inputBox);
            Jtf.RunAsync(async () =>
            {
                var skills = await session.GetSkillsAsync();
                await Jtf.SwitchToMainThreadAsync();
                if (session != _session) return;
                if (skills == null) { _manage.Close(); AppendError("[skills] ", "Claude Code did not return the skills list."); return; }
                var entries = skills.OrderByDescending(s => s.Tokens).Select(s => new ChatPopup.Entry
                {
                    Group = s.IsOn ? "On" : "Off",
                    Label = s.DisplayName,
                    Detail = $"~{TokenMeter.Format(s.Tokens)} tok",
                    Description = s.Source + (s.State != "on" && s.State != "off" ? " · " + s.State : "") + (s.LockedBy != null ? " · locked: " + SkillRow.LockReason(s.LockedBy) : ""),
                    Checked = s.IsOn,
                    Enabled = s.LockedBy == null,
                    Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SetSkill(s, s.IsOn ? "off" : "on"); },
                }).OrderBy(e => e.Group == "On" ? 0 : 1).ToList();
                if (entries.Count == 0) entries.Add(new ChatPopup.Entry { Label = "No skills that can be turned on or off here", Enabled = false });
                int onTokens = skills.Where(s => s.IsOn).Sum(s => s.Tokens);
                _manage.Footer = $"Skills that are on cost ~{TokenMeter.Format(onTokens)} tokens on every request · click to turn one on or off";
                _manage.Show(entries, _inputBox, keepFilter: true);
            }).FileAndForget("ClaudeVS/ChatControl/Skills");
        }

        private void SetSkill(SkillRow skill, string state)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dir = ManageDir;
            var session = _session;
            string claudePath = Options?.ClaudePath ?? "";
            Jtf.RunAsync(async () =>
            {
                string error = await ClaudeSubcommands.SetSkillStateAsync(claudePath, dir, skill.Name, state);
                if (error == null && session != null) await session.ReloadSkillsAsync(); // the running session picks it up now
                await Jtf.SwitchToMainThreadAsync();
                if (error != null) { AppendError("[skills] ", error); return; }
                Append($"Skill {skill.DisplayName} turned {state} ({(state == "off" ? "saves" : "costs")} ~{TokenMeter.Format(skill.Tokens)} tokens per request).");
                ShowSkills();
                RefreshContextUsage(show: false);
            }).FileAndForget("ClaudeVS/ChatControl/SetSkill");
        }

        /// <summary>MCP servers with their status: click to turn one on or off; failed ones can be reconnected.</summary>
        private void ShowMcpServers()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsureSession(ManageDir);
            var session = _session;
            _manage.Footer = null;
            _manage.Show(new[] { new ChatPopup.Entry { Label = "Loading MCP servers…", Enabled = false } }, _inputBox);
            Jtf.RunAsync(async () =>
            {
                var servers = await session.GetMcpServersAsync();
                await Jtf.SwitchToMainThreadAsync();
                if (session != _session) return;
                if (servers == null) { _manage.Close(); AppendError("[mcp] ", "Claude Code did not return the MCP server list."); return; }
                var entries = servers.Select(s => new ChatPopup.Entry
                {
                    Label = s.Name,
                    Detail = McpServerRow.StatusText(s.Status),
                    Description = string.Join(" · ", new[]
                    {
                        s.Scope, s.ToolCount >= 0 ? $"{s.ToolCount} tools" : null,
                        s.Status == "needs-auth" ? "sign in with /mcp in a terminal" : null, s.Error,
                    }.Where(x => !string.IsNullOrEmpty(x))),
                    Checked = s.IsEnabled,
                    Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); SetMcpServer(s.Name, !s.IsEnabled); },
                    Actions = s.Status == "failed" || s.Status == "pending"
                        ? new List<ChatPopup.EntryAction> { new ChatPopup.EntryAction { Glyph = ChatTheme.IconRefresh, Tooltip = "Reconnect", Run = () => { ThreadHelper.ThrowIfNotOnUIThread(); ReconnectMcp(s.Name); } } }
                        : null,
                }).ToList();
                if (entries.Count == 0) entries.Add(new ChatPopup.Entry { Label = "No MCP servers for this folder", Description = "Add them with claude mcp add, or in .mcp.json", Enabled = false });
                var u = _contextUsage;
                string inWindow = u != null && u.McpToolTokens > 0 ? $"MCP tools take ~{TokenMeter.Format(u.McpToolTokens)} tokens of context · " : "";
                string onDemand = u != null && u.McpDeferredToolCount > 0 ? $"{u.McpDeferredToolCount} load only when used · " : "";
                _manage.Footer = inWindow + onDemand + "click to turn a server on or off";
                _manage.Show(entries, _inputBox, keepFilter: true);
            }).FileAndForget("ClaudeVS/ChatControl/Mcp");
        }

        private void SetMcpServer(string name, bool enabled)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var session = _session;
            if (session == null) return;
            Jtf.RunAsync(async () =>
            {
                string error = null;
                try { if (await session.SetMcpServerEnabledAsync(name, enabled) == null) error = "Claude Code did not answer."; }
                catch (InvalidOperationException ex) { error = ex.Message; }
                await Jtf.SwitchToMainThreadAsync();
                if (error != null) { AppendError("[mcp] ", error); return; }
                Append($"MCP server {name} turned {(enabled ? "on" : "off")}.");
                ShowMcpServers();
                RefreshContextUsage(show: false);
            }).FileAndForget("ClaudeVS/ChatControl/McpToggle");
        }

        private void ReconnectMcp(string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var session = _session;
            if (session == null) return;
            Jtf.RunAsync(async () =>
            {
                string error = null;
                try { if (await session.ReconnectMcpServerAsync(name) == null) error = "Claude Code did not answer in time."; }
                catch (InvalidOperationException ex) { error = ex.Message; }
                await Jtf.SwitchToMainThreadAsync();
                if (error != null) AppendError("[mcp] ", error); else Append($"Reconnected {name}.");
                ShowMcpServers();
            }).FileAndForget("ClaudeVS/ChatControl/McpReconnect");
        }
    }
}
