using System;
using System.Collections.Generic;
using ClaudeVS.Core.Cli;
using ClaudeVS.Diagnostics;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// Notes about the session from the CLI's init (billing, …): each said once per conversation in the chat, with a
    /// line in the Output pane. See <see cref="SessionNotices"/>.
    /// </summary>
    public sealed partial class ClaudeChatControl
    {
        // Notes already said in this conversation; init comes at the start of every turn.
        private readonly HashSet<string> _saidNotices = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>From each init.</summary>
        private void OnSessionFacts(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(ev.Payload is JObject init)) return;
            Notice(SessionNotices.ApiKey(init), important: true);
            Notice(SessionNotices.McpServers(init));
            Tip(SessionNotices.OutputStyle(init));
            if (ev.IsFirstInit) { SyncPermissionMode(init); CheckAuth(); }
        }

        /// <summary>
        /// Once per Claude Code process: what /status says signs it in (an auth token, both a token and an API key, a
        /// Console organization), which init does not show. Reading /status makes no model call.
        /// </summary>
        private void CheckAuth()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var session = _session;
            if (session == null) return;
            Jtf.RunAsync(async () =>
            {
                System.Collections.Generic.List<StatusRow> rows = null;
                try { rows = await session.GetStatusAsync(); }
                catch (InvalidOperationException) { return; } // an older CLI without /status: nothing to add
                await Jtf.SwitchToMainThreadAsync();
                if (session != _session) return;
                Notice(SessionNotices.Auth(rows), important: true);
            }).FileAndForget("ClaudeVS/ChatControl/Auth");
        }

        /// <summary>
        /// On a process's first init: when the CLI started in another mode than the chat asked for, say so (unless
        /// Manual was asked, where your settings' default mode applies) and make the mode picker show the real one,
        /// without sending a switch back.
        /// </summary>
        private void SyncPermissionMode(JObject init)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var actual = ChoiceNames.FromCli((string)init["permissionMode"]);
            if (actual == null || actual == _mode) return;
            Notice(SessionNotices.PermissionMode(init, _mode), important: true);
            ClaudeOutputPane.WriteLine($"Mode: the CLI runs in {actual.Value.ToControl()}, the chat asked for {_mode.ToControl()}");
            _mode = actual.Value;
            UpdateChips();
        }

        /// <summary>Token tips are on unless turned off in Options → Token saving → Token tips.</summary>
        private static bool TipsOn => Options?.TokenTips ?? true;

        /// <summary>A token tip: in the chat like a note, or only in the Output pane when token tips are off.</summary>
        private void Tip(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(text)) return;
            if (TipsOn) Notice(text);
            else if (_saidNotices.Add(text)) ClaudeOutputPane.WriteLine("Tip (token tips are off): " + text);
        }

        /// <summary>Says a note once per conversation; important ones are also read out by screen readers.</summary>
        private void Notice(string text, bool important = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(text) || !_saidNotices.Add(text)) return;
            ClaudeOutputPane.WriteLine(text);
            FlushText();
            Append("\n  ℹ " + text + "\n");
            if (important) Announce(text);
        }

        // The large-read tip is given once per conversation, whichever file it was.
        private bool _gaveLargeReadTip;

        /// <summary>After a Read: the token tip when Claude read all of a large file.</summary>
        private void LargeReadTip(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_gaveLargeReadTip || !(ev.ResultData is JObject data)) return;
            string tip = SessionNotices.WholeFileRead(data, ev.ResultChars);
            if (tip == null) return;
            _gaveLargeReadTip = true;
            Tip(tip);
        }

        // ---------------- Repeated reads ----------------

        private readonly Core.Tokens.ReadTracker _reads = new Core.Tokens.ReadTracker();
        private bool _gaveRepeatReadTip;

        // The main agent's shell calls waiting for their result: tool_use_id → tool name.
        private readonly Dictionary<string, string> _shellCalls = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool _gaveLargeOutputTip;

        /// <summary>Every event: the token tips that watch tool calls (repeated reads, large command output).</summary>
        private void TrackTips(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            TrackReads(ev);
            if (ev.Kind == ClaudeEventKind.ToolUse && !ev.IsSubagent && ev.ToolUseId != null && (ev.ToolName == "Bash" || ev.ToolName == "PowerShell"))
                _shellCalls[ev.ToolUseId] = ev.ToolName;
            else if (ev.Kind == ClaudeEventKind.ToolResult && ev.ToolUseId != null && _shellCalls.TryGetValue(ev.ToolUseId, out var tool))
            {
                _shellCalls.Remove(ev.ToolUseId);
                if (ev.IsError || _gaveLargeOutputTip) return;
                string tip = SessionNotices.LargeCommandOutput(tool, ev.ResultChars, Core.Setup.TokenSaverSetup.IsInstalled(_sessionDir));
                if (tip == null) return;
                _gaveLargeOutputTip = true;
                Tip(tip);
            }
        }

        /// <summary>The tip, once per conversation, when Claude reads an unchanged file it already has.</summary>
        private void TrackReads(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(_reads.Observe(ev) is var (path, tokens))) return;
            ClaudeOutputPane.WriteLine($"Repeated read: {path} (~{Core.Tokens.TokenMeter.Format(tokens)} tokens)");
            if (_gaveRepeatReadTip || Core.Tokens.ReadTracker.Tip(path, tokens) is not string tip) return;
            _gaveRepeatReadTip = true;
            Tip(tip);
        }

        // ---------------- Base context ----------------

        private bool _gaveBaseContextTip;

        /// <summary>After each context breakdown: once per conversation, the biggest fixed cost worth cutting.</summary>
        private void BaseContextTip(Core.Tokens.ContextUsage u)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_gaveBaseContextTip || SessionNotices.BaseContext(u) is not string tip) return;
            _gaveBaseContextTip = true;
            Tip(tip);
        }

        // ---------------- Conversation cost ----------------

        private readonly Core.Tokens.CostWatch _costWatch = new Core.Tokens.CostWatch();

        /// <summary>After each turn: once per step of Options → Warn at conversation cost, say what the conversation cost.</summary>
        private void CheckCost()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_meterModel == null) return;
            _costWatch.StepUsd = Options?.CostWarningUsd ?? 5;
            CostWarning(_costWatch.Check(_meterModel.SessionCostUsd));
            DailyCost.StepUsd = Options?.DailyCostWarningUsd ?? 20;
            MonthlyCost.StepUsd = Options?.MonthlyCostWarningUsd ?? 200;
            LoadSpend();
            // The change in the conversation's total: a result without a cost adds nothing.
            double total = _meterModel.SessionCostUsd, added = Math.Max(0, total - _dailyCounted);
            _dailyCounted = total;
            var now = DateTime.UtcNow;
            CostWarning(DailyCost.Add(added, now));
            CostWarning(MonthlyCost.Add(added, now));
        }

        private void CostWarning(string warning)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (warning == null) return;
            ClaudeOutputPane.WriteLine(warning);
            FlushText();
            Append("\n  💲 " + warning + "\n");
            Announce(warning);
        }

        /// <summary>Today's and this month's spend in all chat windows (Options → Warn at daily / monthly cost).</summary>
        private static readonly Core.Tokens.SpendWatch DailyCost = new Core.Tokens.SpendWatch(Core.Tokens.SpendPeriod.Day, 20);
        private static readonly Core.Tokens.SpendWatch MonthlyCost = new Core.Tokens.SpendWatch(Core.Tokens.SpendPeriod.Month, 200);
        private static bool _spendLoaded;
        private double _dailyCounted; // of this meter's SessionCostUsd, already added to DailyCost and MonthlyCost

        /// <summary>Once per Visual Studio session: this month's earlier turns from the usage log (in the background).</summary>
        private static void LoadSpend()
        {
            if (_spendLoaded || Options?.KeepUsageLog == false) return;
            _spendLoaded = true;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { var all = Usage.ReadAll(); DailyCost.Load(all); MonthlyCost.Load(all); }
                catch (Exception ex) { ClaudeOutputPane.WriteLine("Could not read usage log: " + ex.Message); }
            });
        }

        // ---------------- Pricey context ----------------

        private bool _gavePriceyContextTip;

        /// <summary>After a turn: once per conversation, when reading the conversation from the cache cost most of it.</summary>
        private void PriceyContextTip(string model)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_gavePriceyContextTip || _meterModel == null) return;
            string tip = SessionNotices.PriceyContext(model ?? _meterModel.LastModel, _meterModel.LastTurn, _meterModel.ContextTokens, _meterModel.Prices);
            if (tip == null) return;
            _gavePriceyContextTip = true;
            Tip(tip);
        }

        // ---------------- Cache breaks ----------------

        private bool _gaveCacheBreakTip;

        /// <summary>After each turn: a warm prompt cache written again, in the chat once per conversation, later ones in the Output pane.</summary>
        private void CacheBreakTip()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(_meterModel?.TakeCacheBreak() is Core.Tokens.CacheBreak b)) return;
            string note = b.Note();
            if (_gaveCacheBreakTip) { ClaudeOutputPane.WriteLine(note); return; }
            _gaveCacheBreakTip = true;
            Tip(note);
        }

        // ---------------- Prompt cache ----------------

        private readonly System.Windows.Controls.TextBlock _cacheNote = new System.Windows.Controls.TextBlock
        {
            FontSize = 11.5, TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new System.Windows.Thickness(0, 4, 0, 0), Visibility = System.Windows.Visibility.Collapsed,
        };

        /// <summary>
        /// While Claude is idle: "⏱ Prompt cache expires in 4 min: …" in the last minutes of a 1-hour cache, then
        /// "Prompt cache expired …" with what the next prompt costs. Refreshed by the 5-second timer.
        /// </summary>
        private void UpdateCacheNote()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cache = _meterModel?.Cache;
            var now = DateTime.UtcNow;
            string text = _busy || cache == null || !TipsOn ? null : cache.ExpiredNote(now) ?? cache.SoonNote(now);
            if (text == null) { _cacheNote.Visibility = System.Windows.Visibility.Collapsed; return; }
            if (_cacheNote.Text != "⏱ " + text) _cacheNote.Text = "⏱ " + text;
            _cacheNote.ToolTip = cache.Status(now) + "\nEach request keeps the conversation cached for this long again; cached input costs a tenth of the full price.";
            ChatTheme.Muted(_cacheNote);
            _cacheNote.Visibility = System.Windows.Visibility.Visible;
        }

        /// <summary>A new conversation: notes may be said again.</summary>
        private void ResetNotices()
        {
            _saidNotices.Clear();
            _gaveLargeReadTip = false;
            _gaveRepeatReadTip = false;
            _gaveLargeOutputTip = false;
            _shellCalls.Clear();
            _costWatch.Reset();
            _gaveBaseContextTip = false;
            _gaveCacheBreakTip = false;
            _gavePriceyContextTip = false;
            _reads.Reset();
        }
    }
}
