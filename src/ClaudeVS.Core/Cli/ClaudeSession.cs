using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClaudeVS.Core.Tokens;
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
        /// <summary>The claude process id while it runs (to tell our own sessions apart in the CLI's registry).</summary>
        public int? ProcessId { get { lock (_gate) { try { return _process != null && !_process.HasExited ? _process.Id : (int?)null; } catch (InvalidOperationException) { return null; } } } }
        // Written by the stdout reader and process-exit threads, read from the UI thread.
        private volatile bool _isBusy;
        public bool IsBusy { get => _isBusy; private set => _isBusy = value; }
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
                // File checkpoints for rewind: off by default in headless mode, turned on by this variable (2.1.317).
                if (Options.EnableFileCheckpointing) psi.EnvironmentVariables[ClaudeLaunchOptions.FileCheckpointingVariable] = "1";

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
                p.Exited += (s, e) => { IsBusy = false; FailPending(); Exited?.Invoke(this, SafeExitCode(p)); };
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
        public async Task SendPromptAsync(string text, CancellationToken ct = default) => await SendPromptAsync(text, null, ct).ConfigureAwait(false);

        /// <summary>
        /// Sends a prompt with our own message id. The CLI keeps it as the message's uuid (verified with 2.1.317), so
        /// it can later be passed to <see cref="RewindFilesAsync"/>.
        /// </summary>
        public async Task SendPromptAsync(string text, string messageId, CancellationToken ct = default) =>
            await SendPromptAsync(text, messageId, null, ct).ConfigureAwait(false);

        /// <summary>Sends a prompt with images (base64 content blocks before the text, as the API recommends).</summary>
        public async Task SendPromptAsync(string text, string messageId, IReadOnlyList<Context.PromptImage> images, CancellationToken ct = default)
        {
            bool started = EnsureRunning();
            if (started && !ThinkingEnabled) await SendThinkingAsync().ConfigureAwait(false); // there is no launch flag for it
            IsBusy = true;
            await WriteLineAsync(BuildUserMessage(text, messageId, images)).ConfigureAwait(false);
        }

        public static string BuildUserMessage(string text, string messageId, IReadOnlyList<Context.PromptImage> images = null)
        {
            var o = new JObject { ["type"] = "user" };
            if (!string.IsNullOrEmpty(messageId)) o["uuid"] = messageId;
            var content = new JArray();
            foreach (var img in images ?? Array.Empty<Context.PromptImage>())
            {
                if (string.IsNullOrEmpty(img?.Base64) || string.IsNullOrEmpty(img.MediaType)) continue;
                content.Add(new JObject
                {
                    ["type"] = "image",
                    ["source"] = new JObject { ["type"] = "base64", ["media_type"] = img.MediaType, ["data"] = img.Base64 },
                });
            }
            if (!string.IsNullOrWhiteSpace(text) || content.Count == 0) // an image alone needs no (empty) text block
                content.Add(new JObject { ["type"] = "text", ["text"] = text ?? "" });
            o["message"] = new JObject { ["role"] = "user", ["content"] = content };
            return o.ToString(Formatting.None);
        }

        /// <summary>
        /// Puts files back the way they were before the prompt with id <paramref name="messageId"/> (control request
        /// <c>rewind_files</c>; needs <see cref="ClaudeLaunchOptions.EnableFileCheckpointing"/>). A dry run reports
        /// which files would change without touching them. The conversation itself is not rewound.
        /// </summary>
        public async Task<RewindResult> RewindFilesAsync(string messageId, bool dryRun)
        {
            try
            {
                EnsureRunning(); // e.g. a resumed conversation before its first prompt; starting makes no model call
                var r = await RequestAsync(new JObject { ["subtype"] = "rewind_files", ["user_message_id"] = messageId, ["dry_run"] = dryRun }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                return r == null ? new RewindResult { Error = "Claude did not answer the rewind request." } : RewindResult.Parse(r);
            }
            catch (InvalidOperationException ex) { return new RewindResult { Error = ex.Message }; }
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

        /// <summary>
        /// Switch permission mode on the running process (control request <c>set_permission_mode</c>, verified with
        /// Claude Code 2.1.316); before the process starts, the mode is used for the launch.
        /// </summary>
        public async Task SetPermissionModeAsync(PermissionModeChoice mode)
        {
            Options.PermissionMode = mode;
            if (!IsRunning) return;
            await SendControlAsync(new JObject { ["subtype"] = "set_permission_mode", ["mode"] = mode.ToControl() }).ConfigureAwait(false);
        }

        /// <summary>Extended thinking on or off (control request <c>set_max_thinking_tokens</c>; 0 = off, null = default).</summary>
        public bool ThinkingEnabled { get; private set; } = true;

        public async Task SetThinkingAsync(bool enabled)
        {
            ThinkingEnabled = enabled;
            if (!IsRunning) return; // applied right after the process starts
            await SendThinkingAsync().ConfigureAwait(false);
        }

        private Task SendThinkingAsync() =>
            SendControlAsync(new JObject { ["subtype"] = "set_max_thinking_tokens", ["max_thinking_tokens"] = ThinkingEnabled ? JValue.CreateNull() : new JValue(0) });

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

        // Control requests waiting for their control_response, by request id.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JObject>> _pending =
            new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JObject>>();

        /// <summary>
        /// Sends a control request and waits for the CLI's answer. Null when the process is not running, exits, or
        /// does not answer in time; throws <see cref="InvalidOperationException"/> when the CLI answers with an error.
        /// </summary>
        public async Task<JObject> RequestAsync(JObject request, TimeSpan timeout)
        {
            if (!IsRunning) return null;
            string id = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            try
            {
                await WriteLineAsync(new JObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request }.ToString(Formatting.None)).ConfigureAwait(false);
                var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
                return done == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
            }
            finally { _pending.TryRemove(id, out _); }
        }

        /// <summary>
        /// What fills the context window, by category (control request <c>get_context_usage</c>; no model call,
        /// verified with 2.1.317). Null when the process is not running.
        /// </summary>
        public async Task<ContextUsage> GetContextUsageAsync()
        {
            var r = await RequestAsync(new JObject { ["subtype"] = "get_context_usage" }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return r == null ? null : ContextUsage.Parse(r);
        }

        /// <summary>The /skills rows (no model call; starts the process if needed). Null when the CLI does not answer.</summary>
        public async Task<List<SkillRow>> GetSkillsAsync()
        {
            EnsureRunning();
            var r = await RequestAsync(new JObject { ["subtype"] = "get_skills_dialog" }, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            return r == null ? null : SkillRow.ParseDialog(r);
        }

        /// <summary>Re-reads skills after their settings changed, so this session uses the new states.</summary>
        public Task<JObject> ReloadSkillsAsync() => RequestAsync(new JObject { ["subtype"] = "reload_skills" }, TimeSpan.FromSeconds(20));

        /// <summary>MCP servers and their status (no model call; starts the process if needed).</summary>
        public async Task<List<McpServerRow>> GetMcpServersAsync()
        {
            EnsureRunning();
            var r = await RequestAsync(new JObject { ["subtype"] = "mcp_status" }, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            return r == null ? null : McpServerRow.ParseStatus(r);
        }

        public Task<JObject> SetMcpServerEnabledAsync(string serverName, bool enabled) =>
            RequestAsync(new JObject { ["subtype"] = "mcp_toggle", ["serverName"] = serverName, ["enabled"] = enabled }, TimeSpan.FromSeconds(30));

        public Task<JObject> ReconnectMcpServerAsync(string serverName) =>
            RequestAsync(new JObject { ["subtype"] = "mcp_reconnect", ["serverName"] = serverName }, TimeSpan.FromSeconds(60));

        private void CompletePending(ClaudeEvent ev)
        {
            if (ev.RequestId == null || !_pending.TryRemove(ev.RequestId, out var tcs)) return;
            if (ev.IsError) tcs.TrySetException(new InvalidOperationException(ev.Text ?? "The CLI rejected the request."));
            else tcs.TrySetResult(ev.Payload ?? new JObject());
        }

        private void FailPending()
        {
            foreach (var id in _pending.Keys.ToList())
                if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(null);
        }

        /// <summary>Starts the process if needed; true when it was (re)started just now.</summary>
        private bool EnsureRunning()
        {
            if (IsRunning) return false;
            // Relaunch into the same conversation so context (and cache) survive a crash or an interrupt.
            if (_parser?.SessionId != null)
            {
                Options.ResumeSessionId = _parser.SessionId;
                Options.ForkSession = false; // the fork already happened; relaunch into the forked session itself
            }
            Options.Model = CurrentModel == ModelChoice.Auto ? Options.Model : CurrentModel;
            Options.Effort = CurrentEffort == EffortChoice.Auto ? Options.Effort : CurrentEffort;
            Start();
            return true;
        }

        private async Task WriteLineAsync(string line)
        {
            StreamWriter stdin;
            lock (_gate) stdin = _process != null && !_process.HasExited ? _stdin : null;
            if (stdin == null) throw new InvalidOperationException("Claude process is not running.");
            RaiseRaw(RawLineDirection.Stdin, line);
            // Prompts, permission answers and MCP replies come from different threads; StreamWriter allows one write at a time.
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try { await stdin.WriteLineAsync(line).ConfigureAwait(false); }
            finally { _writeLock.Release(); }
        }

        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

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
                        // Off the reader thread: a tool may wait for the UI thread, which may be waiting on us.
                        if (ev.Kind == ClaudeEventKind.McpMessage) { Task.Run(() => AnswerMcpAsync(ev)); continue; }
                        if (ev.Kind == ClaudeEventKind.TurnCompleted) IsBusy = false;
                        if (ev.Kind == ClaudeEventKind.ControlResponse) CompletePending(ev);
                        Raise(ev);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
        }

        /// <summary>Hands an mcp_message to our server of that name and writes its JSON-RPC answer back.</summary>
        private async Task AnswerMcpAsync(ClaudeEvent ev)
        {
            JObject response;
            try
            {
                var server = Options.SdkMcpServers.FirstOrDefault(s => s.Name == ev.ServerName);
                response = server == null
                    ? new JObject { ["subtype"] = "error", ["request_id"] = ev.RequestId, ["error"] = "No MCP server named " + ev.ServerName }
                    : new JObject { ["subtype"] = "success", ["request_id"] = ev.RequestId, ["response"] = new JObject { ["mcp_response"] = await server.HandleAsync(ev.Payload).ConfigureAwait(false) } };
            }
            catch (Exception ex)
            {
                response = new JObject { ["subtype"] = "error", ["request_id"] = ev.RequestId, ["error"] = ex.Message };
            }
            try { await WriteLineAsync(new JObject { ["type"] = "control_response", ["response"] = response }.ToString(Formatting.None)).ConfigureAwait(false); }
            catch (InvalidOperationException) { } // the process exited meanwhile
            catch (IOException) { }
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
