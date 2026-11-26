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
    /// <summary>Which model a prompt goes to (the router, Haiku routing when it is on), the route hint and its price.</summary>
    public sealed partial class ClaudeChatControl
    {
        private RouteDecision Route(string prompt, int attachedLines, int attachedFiles) => _router.Route(RouteRequestFor(prompt, attachedLines, attachedFiles));

        private RouteRequest RouteRequestFor(string prompt, int attachedLines, int attachedFiles)
        {
            _router.StickyContextTokens = Options?.StickyContextTokens ?? 30000;
            return new RouteRequest
            {
                Prompt = prompt,
                AttachedCodeLines = attachedLines,
                AttachedFiles = attachedFiles,
                CurrentModel = _session?.IsRunning == true ? _session.CurrentModel : ModelChoice.Auto,
                ContextTokens = _meterModel?.ContextTokens ?? 0,
                PlanMode = _mode == PermissionModeChoice.Plan,
            };
        }

        // ---- "Ask Haiku to route" (option Route with Haiku) ----

        private HaikuRouter _haikuRouter;
        private static readonly TimeSpan HaikuRouteTimeout = TimeSpan.FromSeconds(4);

        /// <summary>The Haiku router while the option is on (started and warmed ahead of time: ~14 s to start, then a slow first request); null when off.</summary>
        private HaikuRouter HaikuRouterIfOn()
        {
            if (Options?.HaikuRouting != true)
            {
                if (_haikuRouter != null) { _haikuRouter.Dispose(); _haikuRouter = null; }
                return null;
            }
            if (_haikuRouter == null)
            {
                _haikuRouter = new HaikuRouter(Options?.ClaudePath ?? "");
                var r = _haikuRouter;
                _ = System.Threading.Tasks.Task.Run(() => r.WarmUp()); // starts it and sends one short warm-up request (~$0.0001)
            }
            return _haikuRouter;
        }

        /// <summary>
        /// Asks the Haiku router (at most <see cref="HaikuRouteTimeout"/>); its answer goes through the same cache rule as
        /// the local one. Null when it is off or does not answer in time; the local decision is used then.
        /// </summary>
        private async System.Threading.Tasks.Task<(RouteDecision Decision, double Cost, long Ms)> AskHaikuRouterAsync(RouteRequest request)
        {
            var router = HaikuRouterIfOn();
            if (router == null) return (null, 0, 0);
            double before = router.TotalCostUsd;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var d = await router.RouteAsync(request, HaikuRouteTimeout);
            sw.Stop();
            if (d != null) d = _router.ApplyCacheStickiness(d, request, clearlyHard: d.Model == ModelChoice.Opus && d.Effort == EffortChoice.High);
            ClaudeOutputPane.WriteLine(d != null ? $"Haiku router ({sw.ElapsedMilliseconds} ms): {d}" : $"Haiku router gave no answer in {sw.ElapsedMilliseconds} ms; using the local rules");
            return (d, Math.Max(0, router.TotalCostUsd - before), sw.ElapsedMilliseconds);
        }

        private void UpdateRouteHint()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (SlashCommands.IsCommand(_prompt.Text))
            {
                // The list above the box shows the commands; this line says how to use it, or what the chosen one does.
                RefreshCommands();
                var chosen = SlashCommands.TypedName(_prompt.Text) == null ? SlashCommands.Match(_commands, _prompt.Text).FirstOrDefault() : null;
                _route.Text = chosen != null
                    ? "/" + chosen.Name + (string.IsNullOrWhiteSpace(chosen.Description) ? "" : " — " + chosen.Description)
                    : _commandPopup.IsOpen && _commandMatches.Count > 0 ? "↑ ↓ to choose · Tab or Enter to insert · Esc to close" : "";
                return;
            }
            string size = PromptSize.Note(_prompt.Text); // a long prompt goes with every later request
            if (_modelChoice != ModelChoice.Auto || string.IsNullOrWhiteSpace(_prompt.Text) || SlashCommands.IsCommand(_prompt.Text))
            {
                string send = _modelChoice != ModelChoice.Auto ? _modelChoice.ToCli() : null;
                string dearer = send != null && !string.IsNullOrWhiteSpace(_prompt.Text) ? (Options?.ToPriceTable() ?? new PriceTable()).VersusOpus(send) : null;
                if (dearer != null) dearer = _modelChoice + ": " + dearer; // "Fable: 2× the Opus price"
                _route.Text = string.Join("   ·   ", new[] { dearer, size, PriceNote(send) }.Where(s => s != null));
                return;
            }
            int lines = _selection == null ? 0 : _selection.EndLine - _selection.StartLine + 1;
            var d = Route(_prompt.Text, lines, _attachedFiles.Count);
            _route.Text = $"Auto: {d.Model.ToCli()} / {d.Effort.ToCli()} — {d.Reason}" + (size != null ? "   ·   " + size : "") + (PriceNote(d.Model.ToCli()) is string price ? "   ·   " + price : "");
        }

        /// <summary>
        /// "~$0.02 to send (context cached)" while a prompt is typed into a conversation with context: the least the
        /// prompt costs (its first request's input). Null for an empty prompt or a new conversation.
        /// </summary>
        private string PriceNote(string sendModel)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var m = _meterModel;
            if (m == null || string.IsNullOrWhiteSpace(_prompt.Text) || m.Cache.ContextTokens < PromptSize.ShowFrom) return null;
            // The model the alias resolves to (ANTHROPIC_DEFAULT_HAIKU_MODEL can point "haiku" at Haiku 4.5): its price and cache
            if (sendModel != null) sendModel = _session?.Models?.ResolvedId(sendModel) ?? sendModel;
            return NextPromptPrice.Estimate(m.Cache, m.LastModel, sendModel, PromptSize.Estimate(_prompt.Text), Options?.ToPriceTable(), DateTime.UtcNow).Note();
        }
    }
}
