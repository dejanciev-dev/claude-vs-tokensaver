using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClaudeVS.Core.Cli;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// "Ask Haiku to route": a separate, trimmed Claude Code process on Haiku that picks model and effort for each
    /// prompt. Trimmed means our own short system prompt, no tools, no MCP servers, no skills or settings, no saved
    /// session, an empty working folder: about 1.3k tokens instead of the usual ~25k. Measured with 2.1.318: the
    /// process takes ~14 s to start (so it is started ahead of time), then ~1–2 s and ~$0.0001 per route.
    /// One request at a time; restarted every <see cref="RestartAfter"/> routes so its history stays small.
    /// Since Claude Code 2.1.323 the first request of a process waits 5–7 s for its first byte (later ones ~0.5 s),
    /// past the chat's 4 s limit, so each new process gets one short warm-up request (<see cref="WarmUpRequest"/>)
    /// while idle. Its cost is added to the next route's, so the usage log still has every cent.
    /// </summary>
    public sealed class HaikuRouter : IDisposable
    {
        public const int RestartAfter = 25;
        public const int MaxPromptChars = 3000;

        public const string SystemPrompt =
            "You route coding requests to the cheapest Claude model that will do them well, with an effort level. " +
            "haiku: lookups, small or mechanical edits, renames, explaining short code, commit messages. " +
            "sonnet: normal feature work, debugging, tests, refactors within a few files. " +
            "opus: architecture, design, hard multi-file reasoning, subtle concurrency or performance problems. " +
            "effort low for routine work, medium for normal work, high only for hard problems. " +
            "Judge each request on its own, ignoring earlier ones. Give a reason of at most 8 words. Answer only with the JSON.";

        public static readonly JObject Schema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""model"": { ""type"": ""string"", ""enum"": [""haiku"", ""sonnet"", ""opus""] },
    ""effort"": { ""type"": ""string"", ""enum"": [""low"", ""medium"", ""high""] },
    ""reason"": { ""type"": ""string"" }
  },
  ""required"": [""model"", ""effort""]
}");

        private readonly string _claudePath;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly object _lock = new object();
        private ClaudeSession _session;
        private string _dir;
        private int _routesThisProcess;
        private double _lastProcessCost;
        private TaskCompletionSource<ClaudeEvent> _pending;
        private bool _disposed;
        private bool _warming;          // the request in flight is the warm-up, not a route
        private ClaudeSession _warmed;  // the process that has had its warm-up
        private double _warmUpCost;     // not yet added to TotalCostUsd (it goes with the next route)

        /// <summary>The warm-up request sent to a new process: a real request, so the slow first one is over.</summary>
        public const string WarmUpRequest = "Request:\nfix a typo in a comment";

        public HaikuRouter(string claudePath) { _claudePath = claudePath ?? ""; }

        public int Routes { get; private set; }
        public double TotalCostUsd { get; private set; }
        public double LastRouteCostUsd { get; private set; }

        /// <summary>The command-line additions that trim the process down to a classifier.</summary>
        public static string TrimmedArguments() => string.Join(" ",
            "--system-prompt", ClaudeArgsBuilder.Quote(SystemPrompt),
            "--tools", "\"\"",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--setting-sources", "\"\"",
            "--no-session-persistence",
            "--json-schema", ClaudeArgsBuilder.Quote(Schema.ToString(Newtonsoft.Json.Formatting.None)));

        /// <summary>What the router is asked: the prompt (cut to <see cref="MaxPromptChars"/>) and what is attached.</summary>
        public static string BuildRequest(RouteRequest r)
        {
            string p = (r?.Prompt ?? "").Trim();
            if (p.Length > MaxPromptChars) p = p.Substring(0, MaxPromptChars) + " …(cut)";
            string attached = r == null ? "" : (r.AttachedFiles > 0 ? $" {r.AttachedFiles} file(s) attached." : "") + (r.AttachedCodeLines > 0 ? $" {r.AttachedCodeLines} lines of code attached." : "");
            return "Request:" + attached + "\n" + p;
        }

        /// <summary>The router's answer (the result text is the JSON) as a decision; null when it is not usable.</summary>
        public static RouteDecision Parse(string resultJson)
        {
            if (string.IsNullOrWhiteSpace(resultJson)) return null;
            JObject o;
            try { o = JObject.Parse(resultJson.Trim()); }
            catch (Newtonsoft.Json.JsonException) { return null; }
            ModelChoice model;
            switch (((string)o["model"] ?? "").ToLowerInvariant())
            {
                case "haiku": model = ModelChoice.Haiku; break;
                case "sonnet": model = ModelChoice.Sonnet; break;
                case "opus": model = ModelChoice.Opus; break;
                default: return null;
            }
            EffortChoice effort;
            switch (((string)o["effort"] ?? "").ToLowerInvariant())
            {
                case "low": effort = EffortChoice.Low; break;
                case "high": effort = EffortChoice.High; break;
                default: effort = EffortChoice.Medium; break;
            }
            string reason = ((string)o["reason"])?.Trim();
            return new RouteDecision { Model = model, Effort = effort, Score = double.NaN, Reason = "Haiku router" + (string.IsNullOrEmpty(reason) ? "" : ": " + reason) };
        }

        /// <summary>
        /// Starts the process now and sends it the warm-up request, so the ~14 s start-up and the slow first request
        /// (2.1.323) are over before the first prompt. About $0.0001.
        /// </summary>
        public void WarmUp()
        {
            ClaudeSession session;
            try { session = EnsureSession(); }
            catch (Exception ex) when (ex is FileNotFoundException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { return; }
            if (!_gate.Wait(0)) return; // a route is running: it warms the process itself
            TaskCompletionSource<ClaudeEvent> tcs;
            lock (_lock)
            {
                if (_disposed || session != _session || _warmed == session) { _gate.Release(); return; }
                _warmed = session;
                _warming = true;
                tcs = _pending = new TaskCompletionSource<ClaudeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _ = tcs.Task.ContinueWith(_ => _gate.Release(), TaskScheduler.Default);
            try { session.SendPromptAsync(WarmUpRequest).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { lock (_lock) { _warming = false; if (_pending == tcs) _pending = null; } tcs.TrySetResult(null); }
        }

        /// <summary>
        /// Asks for a decision. Null when the router is busy, fails, or does not answer within <paramref name="timeout"/>;
        /// the caller then uses the local rules. A late answer is dropped.
        /// </summary>
        public async Task<RouteDecision> RouteAsync(RouteRequest request, TimeSpan timeout)
        {
            if (_disposed) return null;
            var started = DateTime.UtcNow;
            if (!await _gate.WaitAsync(timeout).ConfigureAwait(false)) return null; // the previous route is still running
            bool releaseLater = false;
            try
            {
                ClaudeSession session;
                try { session = EnsureSession(); }
                catch (Exception ex) when (ex is FileNotFoundException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { return null; }

                var tcs = new TaskCompletionSource<ClaudeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_lock) _pending = tcs;
                try { await session.SendPromptAsync(BuildRequest(request)).ConfigureAwait(false); }
                catch (InvalidOperationException) { return null; }

                var left = timeout - (DateTime.UtcNow - started);
                if (left < TimeSpan.FromMilliseconds(100)) left = TimeSpan.FromMilliseconds(100);
                var done = await Task.WhenAny(tcs.Task, Task.Delay(left)).ConfigureAwait(false);
                if (done != tcs.Task)
                {
                    // Let it finish in the background before the next route starts.
                    releaseLater = true;
                    _ = tcs.Task.ContinueWith(_ => _gate.Release(), TaskScheduler.Default);
                    return null;
                }
                var ev = await tcs.Task.ConfigureAwait(false);
                return ev == null || ev.IsError ? null : Parse(ev.Text);
            }
            finally
            {
                if (!releaseLater) _gate.Release();
                // The process is restarted after this many routes: start and warm the next one now, while idle.
                bool restartDue;
                lock (_lock) restartDue = !_disposed && _routesThisProcess >= RestartAfter;
                if (restartDue) _ = Task.Run(() => WarmUp());
            }
        }

        private ClaudeSession EnsureSession()
        {
            lock (_lock)
            {
                if (_disposed) throw new InvalidOperationException("The router was closed.");
                if (_session != null && _routesThisProcess >= RestartAfter) { CloseSession(); }
                if (_session != null && _session.IsRunning) return _session;
                CloseSession();
                _dir = Path.Combine(Path.GetTempPath(), "claude-vs-tokensaver", "router-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_dir); // empty: no CLAUDE.md or project settings to pick up
                var s = new ClaudeSession(new ClaudeLaunchOptions
                {
                    ClaudePath = _claudePath,
                    WorkingDirectory = _dir,
                    Model = ModelChoice.Haiku,
                    Effort = EffortChoice.Low,
                    PermissionMode = PermissionModeChoice.Default,
                    ExtraArguments = TrimmedArguments(),
                });
                s.EventReceived += OnEvent;
                s.Exited += (o, code) => { lock (_lock) { _pending?.TrySetResult(null); _pending = null; _warming = false; } };
                s.Start();
                _session = s;
                _routesThisProcess = 0;
                _lastProcessCost = 0;
                return s;
            }
        }

        private void OnEvent(object sender, ClaudeEvent ev)
        {
            if (ev.Kind == ClaudeEventKind.PermissionRequest)
            {
                // It has no tools; refuse anything anyway.
                _ = (sender as ClaudeSession)?.RespondToPermissionAsync(ev.RequestId, ev.ToolInput, false, "The router has no tools.");
                return;
            }
            if (ev.Kind != ClaudeEventKind.TurnCompleted) return;
            TaskCompletionSource<ClaudeEvent> tcs;
            lock (_lock)
            {
                if (sender != _session) return;
                tcs = _pending;
                _pending = null;
                double total = ev.TotalCostUsd ?? _lastProcessCost; // cumulative for this process
                double cost = Math.Max(0, total - _lastProcessCost);
                _lastProcessCost = total;
                if (_warming)
                {
                    _warming = false;
                    _warmUpCost += cost; // billed with the next route
                }
                else
                {
                    _routesThisProcess++;
                    Routes++;
                    LastRouteCostUsd = cost + _warmUpCost;
                    _warmUpCost = 0;
                    TotalCostUsd += LastRouteCostUsd;
                }
            }
            tcs?.TrySetResult(ev);
        }

        private void CloseSession()
        {
            var old = _session;
            string dir = _dir;
            _session = null;
            if (old == null) return;
            old.EventReceived -= OnEvent;
            _ = Task.Run(() =>
            {
                old.Dispose();
                try { if (dir != null) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            });
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _pending?.TrySetResult(null);
                CloseSession();
            }
        }
    }
}
