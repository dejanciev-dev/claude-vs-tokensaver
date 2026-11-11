using System;
using ClaudeVS.Core.Cli;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// Characters → tokens for text the extension has not sent yet or only knows by length (the prompt being typed,
    /// tool results, files in the @ picker). The current models' tokenizer takes more tokens than the old
    /// ~4 characters each: measured with Claude Code 2.1.322 (`--live-prompt-tokens`, Haiku 5.5) prose ran at 2.97,
    /// code at 2.75 and numbered lines at 2.71 characters per token. So the start is 3.0, refined by
    /// <see cref="PromptTokenLearner"/> from real turns; one value for all chat windows (one tokenizer).
    /// </summary>
    public static class TokenEstimate
    {
        public const double DefaultCharsPerToken = 3.0;
        /// <summary>The start counts as this many tokens of evidence, so one odd turn moves it little.</summary>
        private const double PriorTokens = 6000;
        /// <summary>Evidence beyond this is halved, so the value keeps following recent turns.</summary>
        private const double MaxTokens = 60000;
        public const double MinCharsPerToken = 1.5, MaxCharsPerToken = 6.0;

        private static readonly object Gate = new object();
        private static double _chars = PriorTokens * DefaultCharsPerToken, _tokens = PriorTokens;

        public static double CharsPerToken { get { lock (Gate) return _chars / _tokens; } }

        /// <summary>Tokens for <paramref name="chars"/> characters of text (0 for none).</summary>
        public static int FromChars(long chars) => chars <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Ceiling(chars / CharsPerToken));

        public static int FromText(string text) => FromChars(text?.Length ?? 0);

        /// <summary>A measured sample: <paramref name="chars"/> characters took <paramref name="tokens"/> tokens. Implausible ratios are ignored; returns whether it was used.</summary>
        public static bool Learn(long chars, long tokens)
        {
            if (chars <= 0 || tokens <= 0) return false;
            double ratio = (double)chars / tokens;
            if (ratio < MinCharsPerToken || ratio > MaxCharsPerToken) return false;
            double c, t;
            lock (Gate)
            {
                _chars += chars;
                _tokens += tokens;
                if (_tokens > MaxTokens) { _chars /= 2; _tokens /= 2; }
                c = _chars; t = _tokens;
            }
            Save(c, t);
            return true;
        }

        // ---- Kept across Visual Studio restarts ----

        /// <summary>Next to the usage log: %LocalAppData%\claude-vs-tokensaver\token-estimate.json (two numbers, no text).</summary>
        public static string DefaultStorePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-vs-tokensaver", "token-estimate.json");

        private static string _store;

        /// <summary>
        /// Loads what earlier runs learned from <paramref name="path"/> (a missing, unreadable or implausible file
        /// leaves the start value) and saves there after each sample from now on.
        /// </summary>
        public static void UseStore(string path)
        {
            _store = path;
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
                var o = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(path));
                double c = (double?)o["chars"] ?? 0, t = (double?)o["tokens"] ?? 0;
                if (t <= 0 || c <= 0 || c / t < MinCharsPerToken || c / t > MaxCharsPerToken) return;
                if (t > MaxTokens) { c *= MaxTokens / t; t = MaxTokens; }
                lock (Gate) { _chars = c; _tokens = t; }
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException || ex is InvalidCastException || ex is FormatException) { }
        }

        private static void Save(double chars, double tokens)
        {
            string path = _store;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                var o = new Newtonsoft.Json.Linq.JObject
                {
                    ["chars"] = Math.Round(chars),
                    ["tokens"] = Math.Round(tokens),
                    ["charsPerToken"] = Math.Round(chars / tokens, 3), // for a person reading it; not read back
                };
                string tmp = path + ".tmp";
                System.IO.File.WriteAllText(tmp, o.ToString(Newtonsoft.Json.Formatting.None));
                if (System.IO.File.Exists(path)) System.IO.File.Replace(tmp, path, null);
                else System.IO.File.Move(tmp, path);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { }
        }

        /// <summary>Back to <see cref="DefaultCharsPerToken"/>, not saving anywhere (tests).</summary>
        internal static void Reset() { lock (Gate) { _chars = PriorTokens * DefaultCharsPerToken; _tokens = PriorTokens; } _store = null; }
    }

    /// <summary>
    /// Learns <see cref="TokenEstimate"/> from one conversation: a prompt's tokens are what the first request after it
    /// adds to the previous request's context, less that request's answer and ~30 tokens of framing. Measured with
    /// 2.1.322: a 14-character prompt added 33 tokens on every turn but the second of a process, which added ~2,000
    /// once; so the first two turns of each process are not used, nor turns after a compaction. Only prompts of
    /// <see cref="MinChars"/>+ characters count, where the framing is small next to the text.
    /// </summary>
    public sealed class PromptTokenLearner
    {
        public const int MinChars = 2000;
        public const int FramingTokens = 30;

        private int _pendingChars;
        private int _turnsInProcess;
        private long _lastContext, _lastOutput;
        private string _lastMessageId;
        private bool _compacted;

        /// <summary>Last measured characters per token in this conversation (0 before the first).</summary>
        public double LastMeasured { get; private set; }

        public void Reset()
        {
            _pendingChars = 0; _turnsInProcess = 0; _lastContext = 0; _lastOutput = 0; _lastMessageId = null; _compacted = false; LastMeasured = 0;
        }

        /// <summary>A prompt of this many characters went out (text only: prompts with images are not passed in).</summary>
        public void PromptSent(int chars) => _pendingChars = chars;

        public void Observe(ClaudeEvent ev)
        {
            switch (ev.Kind)
            {
                case ClaudeEventKind.SessionStarted when ev.IsFirstInit:
                    _turnsInProcess = 0; _lastContext = 0; _lastOutput = 0; _lastMessageId = null;
                    break;
                case ClaudeEventKind.AssistantUsage when !ev.IsSubagent && !ev.Usage.IsEmpty:
                    bool newMessage = ev.MessageId == null || ev.MessageId != _lastMessageId;
                    if (newMessage && _pendingChars > 0)
                    {
                        if (_pendingChars >= MinChars && _turnsInProcess >= 2 && !_compacted && _lastContext > 0)
                        {
                            long tokens = ev.Usage.ContextTokens - _lastContext - _lastOutput - FramingTokens;
                            if (TokenEstimate.Learn(_pendingChars, tokens)) LastMeasured = (double)_pendingChars / tokens;
                        }
                        _pendingChars = 0;
                        _compacted = false;
                    }
                    _lastMessageId = ev.MessageId;
                    _lastContext = ev.Usage.ContextTokens;
                    _lastOutput = ev.Usage.Output; // the last copy of a message's usage is its final one
                    break;
                case ClaudeEventKind.Compacted:
                    _compacted = true;
                    break;
                case ClaudeEventKind.TurnCompleted:
                    _turnsInProcess++;
                    _pendingChars = 0;
                    break;
            }
        }
    }
}
