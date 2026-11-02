using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    /// <summary>
    /// One long-running <c>claude</c> process in stream-json mode. Keeping the process alive between
    /// prompts keeps the prompt cache warm (cache reads cost ~10% of normal input tokens).
    /// Events are raised on a background thread — marshal to the UI thread yourself.
    /// </summary>
    public sealed class ClaudeSession : IDisposable
    {
        private readonly object _gate = new object();
        private Process _process;
        private StreamWriter _stdin;
        private ClaudeStreamParser _parser;
        private bool _disposed;

        public ClaudeSession(ClaudeLaunchOptions options)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public ClaudeLaunchOptions Options { get; }
        public string SessionId => _parser?.SessionId ?? Options.ResumeSessionId;
        public bool IsRunning { get { lock (_gate) return _process != null && !_process.HasExited; } }
        public bool IsBusy { get; private set; }
        /// <summary>Model alias the running process uses right now (after any set_model).</summary>
        public ModelChoice CurrentModel { get; private set; }
        public EffortChoice CurrentEffort { get; private set; }

        public event EventHandler<ClaudeEvent> EventReceived;
        public event EventHandler<int> Exited;
        /// <summary>Every raw protocol line, for diagnostics. Raised on background threads.</summary>
        public event EventHandler<RawLineEventArgs> RawLine;

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ClaudeSession));
                if (_process != null && !_process.HasExited) return;

                string exe = ClaudeCliLocator.Resolve(Options.ClaudePath);
                if (exe == null)
                    throw new FileNotFoundException("Claude Code CLI not found. Install it (https://claude.com/product/claude-code) or set its path under Tools > Options > Claude Code.");

                string args = ClaudeArgsBuilder.Build(Options);
                var psi = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = string.IsNullOrEmpty(Options.WorkingDirectory) ? Environment.CurrentDirectory : Options.WorkingDirectory,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                };

                // npm installs a .cmd shim on Windows, which cannot be started directly without a shell.
                if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                {
                    psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                    psi.Arguments = "/d /s /c \"" + ClaudeArgsBuilder.Quote(exe) + " " + args + "\"";
                }
                else
                {
                    psi.FileName = exe;
                    psi.Arguments = args;
                }

                _parser = new ClaudeStreamParser();
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.Exited += (s, e) => { IsBusy = false; Exited?.Invoke(this, SafeExitCode(p)); };
                p.Start();
                // stdin must be UTF-8 without BOM. .NET Framework would otherwise use the console code page
                // (netstandard2.0 has no StandardInputEncoding), so wrap the raw stream ourselves.
                _stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                _process = p;
                CurrentModel = Options.Model;
                CurrentEffort = Options.Effort;

                var stdout = p.StandardOutput;
                var stderr = p.StandardError;
                Task.Run(() => ReadLoop(stdout, isError: false));
                Task.Run(() => ReadLoop(stderr, isError: true));
            }
        }

        /// <summary>Send a user prompt. Starts (or restarts with --resume) the process if needed.</summary>
        public Task SendPromptAsync(string text, CancellationToken ct = default)
        {
            EnsureRunning();
            IsBusy = true;
            var payload = new { type = "user", message = new { role = "user", content = new[] { new { type = "text", text } } } };
            return WriteLineAsync(JsonConvert.SerializeObject(payload));
        }

        public Task InterruptAsync() => SendControlAsync(new JObject { ["subtype"] = "interrupt" });

        /// <summary>Switch model on the running process (no restart). NOTE: a model switch starts a new prompt cache.</summary>
        public async Task SetModelAsync(ModelChoice model)
        {
            if (model == ModelChoice.Auto || model == CurrentModel) return;
            if (!IsRunning) { Options.Model = model; CurrentModel = model; return; }
            await SendControlAsync(new JObject { ["subtype"] = "set_model", ["model"] = model.ToCli() }).ConfigureAwait(false);
            CurrentModel = model;
        }

        public async Task SetEffortAsync(EffortChoice effort)
        {
            if (effort == EffortChoice.Auto || effort == CurrentEffort) return;
            if (!IsRunning) { Options.Effort = effort; CurrentEffort = effort; return; }
            await SendControlAsync(new JObject { ["subtype"] = "apply_flag_settings", ["settings"] = new JObject { ["effort"] = effort.ToCli() } }).ConfigureAwait(false);
            CurrentEffort = effort;
        }

        /// <summary>Answer a <see cref="ClaudeEventKind.PermissionRequest"/>.</summary>
        public Task RespondToPermissionAsync(string requestId, JObject originalInput, bool allow, string denyMessage = null)
        {
            JObject response = allow
                ? new JObject { ["behavior"] = "allow", ["updatedInput"] = originalInput ?? new JObject() }
                : new JObject { ["behavior"] = "deny", ["message"] = denyMessage ?? "The user denied this action." };
            var payload = new JObject
            {
                ["type"] = "control_response",
                ["response"] = new JObject { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = response },
            };
            return WriteLineAsync(payload.ToString(Formatting.None));
        }

        private Task SendControlAsync(JObject request)
        {
            if (!IsRunning) return Task.CompletedTask;
            var payload = new JObject { ["type"] = "control_request", ["request_id"] = Guid.NewGuid().ToString("N"), ["request"] = request };
            return WriteLineAsync(payload.ToString(Formatting.None));
        }

        private void EnsureRunning()
        {
            if (IsRunning) return;
            // Relaunch into the same conversation so context (and cache) survive a crash or an interrupt.
            if (_parser?.SessionId != null) Options.ResumeSessionId = _parser.SessionId;
            Options.Model = CurrentModel == ModelChoice.Auto ? Options.Model : CurrentModel;
            Options.Effort = CurrentEffort == EffortChoice.Auto ? Options.Effort : CurrentEffort;
            Start();
        }

        private async Task WriteLineAsync(string line)
        {
            StreamWriter stdin;
            lock (_gate) stdin = _process != null && !_process.HasExited ? _stdin : null;
            if (stdin == null) throw new InvalidOperationException("Claude process is not running.");
            RaiseRaw(RawLineDirection.Stdin, line);
            await stdin.WriteLineAsync(line).ConfigureAwait(false);
        }

        private void ReadLoop(StreamReader reader, bool isError)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    RaiseRaw(isError ? RawLineDirection.Stderr : RawLineDirection.Stdout, line);
                    if (isError)
                    {
                        if (!string.IsNullOrWhiteSpace(line)) Raise(new ClaudeEvent { Kind = ClaudeEventKind.Error, Text = line });
                        continue;
                    }
                    IReadOnlyList<ClaudeEvent> events;
                    try { events = _parser.Parse(line); }
                    catch (Exception ex) { events = new[] { new ClaudeEvent { Kind = ClaudeEventKind.Error, Text = "Parse error: " + ex.Message } }; }
                    foreach (var ev in events)
                    {
                        if (ev.Kind == ClaudeEventKind.TurnCompleted) IsBusy = false;
                        Raise(ev);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
        }

        private void Raise(ClaudeEvent ev)
        {
            try { EventReceived?.Invoke(this, ev); }
            catch (Exception ex) { Debug.WriteLine("ClaudeSession handler failed: " + ex); }
        }

        private void RaiseRaw(RawLineDirection direction, string line)
        {
            var handler = RawLine;
            if (handler == null) return;
            try { handler(this, new RawLineEventArgs(direction, line)); }
            catch (Exception ex) { Debug.WriteLine("ClaudeSession raw handler failed: " + ex); }
        }

        private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        _stdin?.Close(); // CLI exits cleanly on EOF
                        if (!_process.WaitForExit(2000)) _process.Kill();
                    }
                }
                catch { }
                _process?.Dispose();
                _process = null;
            }
        }
    }

    public enum RawLineDirection { Stdin, Stdout, Stderr }

    public sealed class RawLineEventArgs : EventArgs
    {
        public RawLineEventArgs(RawLineDirection direction, string line) { Direction = direction; Line = line; }
        public RawLineDirection Direction { get; }
        public string Line { get; }
    }
}
