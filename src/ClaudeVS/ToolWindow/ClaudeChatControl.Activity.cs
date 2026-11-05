using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Tokens;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// The activity strip above the input box while Claude works, in the style of the Claude Code terminal:
    ///   ✻ Imagining… (1m 05s · ↓ 9.5k tokens · thinking)                                   Esc to stop
    ///     ⎿ Tip: …
    /// The spinner, the verb and a sweeping line take the color of the phase (thinking, writing, a tool, waiting
    /// for you). The token count is an estimate: the CLI's thinking estimates plus streamed text and tool input
    /// at ~4 characters per token. The meter line is colored here too.
    /// </summary>
    public partial class ClaudeChatControl
    {
        private enum Phase { Starting, Thinking, Writing, Tool, Waiting }

        private static readonly string[] SpinnerFrames = { "·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢" };
        private static readonly string[] Verbs = { "Thinking", "Pondering", "Imagining", "Reasoning", "Considering", "Mulling", "Working", "Brewing", "Crafting", "Figuring" };
        private static readonly string[] Tips =
        {
            "@ attaches a file as a path only, so Claude reads just what it needs",
            "Shift+Tab switches mode: Manual, Edit automatically, Plan, Auto",
            "a new session is the cheapest context; /compact when the meter turns amber",
            "Ctrl+Alt+K, S asks about the selection; large selections go as line ranges",
            "click the meter to see what fills the context window",
            "type while Claude works: your message waits until Claude gets to it",
            "with the model on Auto, simple prompts go to Haiku",
            "Always allow on a permission card saves the rule, so Claude stops asking",
        };
        private static readonly Random Rng = new Random();

        private readonly Border _activity = new Border
        {
            Visibility = Visibility.Collapsed,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 6),
        };
        private readonly TextBlock _spinner = new TextBlock { FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 14, Width = 16, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        private readonly TextBlock _activityLabel = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _activityDetail = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _activityHint = new TextBlock { Text = "Esc to stop", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        private readonly TextBlock _activityTip = new TextBlock { FontSize = 11, Margin = new Thickness(24, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Rectangle _activitySweep = new Rectangle { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        private DispatcherTimer _activityTimer, _spinTimer;
        private int _spinFrame;
        private bool _activityAnimating;

        private Phase _phase;
        private string _phaseText;           // what the tool phase is about ("Bash dotnet test")
        private string _phaseTool;           // tool name for the label
        private string _turnVerb = "Thinking";
        private long _turnThinkingTokens;    // the CLI's estimates, summed over the turn
        private long _turnOutputChars;       // streamed answer text and tool input
        private DateTime _turnStartedUtc = DateTime.UtcNow;
        private TimeSpan _turnThought;       // thinking time this turn, for "thought for 4s"
        private DateTime? _thinkingSinceUtc; // set while Claude is thinking
        private Brush _activityColor;

        private void InitActivity()
        {
            ChatTheme.Muted(_activityDetail);
            ChatTheme.Muted(_activityHint);
            ChatTheme.Muted(_activityTip);

            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_spinner, Dock.Left);
            DockPanel.SetDock(_activityHint, Dock.Right);
            row.Children.Add(_spinner);
            row.Children.Add(_activityHint);
            var text = new StackPanel { Orientation = Orientation.Horizontal };
            text.Children.Add(_activityLabel);
            text.Children.Add(_activityDetail);
            row.Children.Add(text);

            var lines = new StackPanel { Margin = new Thickness(8, 5, 8, 6) };
            lines.Children.Add(row);
            lines.Children.Add(_activityTip);

            var grid = new Grid { ClipToBounds = true };
            grid.Children.Add(lines);
            grid.Children.Add(_activitySweep);
            _activity.Child = grid;
            System.Windows.Automation.AutomationProperties.SetName(_activity, "Claude is working");

            _activityTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (s, e) => RenderActivity(), Dispatcher);
            _activityTimer.Stop();
            _spinTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (s, e) => _spinner.Text = SpinnerFrames[_spinFrame = (_spinFrame + 1) % SpinnerFrames.Length], Dispatcher);
            _spinTimer.Stop();
        }

        /// <summary>Called from SetBusy: a turn starts (or the queue moves on) or Claude is done.</summary>
        private void OnBusyChangedActivity(bool busy, bool wasBusy)
        {
            if (busy && !wasBusy) StartActivityTurn();
            RenderActivity();
        }

        private void StartActivityTurn()
        {
            _turnStartedUtc = DateTime.UtcNow;
            _phase = Phase.Starting;
            _phaseTool = _phaseText = null;
            _turnThinkingTokens = 0;
            _turnOutputChars = 0;
            _turnThought = TimeSpan.Zero;
            _thinkingSinceUtc = null;
            _turnVerb = Verbs[Rng.Next(Verbs.Length)];
            _activityTip.Text = "⎿  Tip: " + Tips[Rng.Next(Tips.Length)];
        }

        /// <summary>Streamed answer text (counted for the ↓ tokens estimate; the strip shows it on the next tick).</summary>
        private void CountStreamedText(int chars) => _turnOutputChars += chars;

        /// <summary>Events that change what the strip says. Called for every event on the UI thread.</summary>
        private void OnActivityEvent(ClaudeEvent ev)
        {
            switch (ev.Kind)
            {
                case ClaudeEventKind.Activity:
                    if (ev.Activity == ClaudeActivity.Thinking)
                    {
                        _phase = Phase.Thinking;
                        if (ev.EstimatedTokens is int t && t > 0) _turnThinkingTokens += t;
                    }
                    else if (ev.Activity == ClaudeActivity.Writing) _phase = Phase.Writing;
                    else { _phase = Phase.Tool; _phaseTool = ev.ToolName; _phaseText = null; }
                    break;
                case ClaudeEventKind.ToolUse when !ev.IsSubagent:
                    _phase = Phase.Tool;
                    _phaseTool = ev.ToolName;
                    _phaseText = ev.Text;
                    _turnOutputChars += ev.ToolInput?.ToString(Newtonsoft.Json.Formatting.None).Length ?? 0;
                    break;
                case ClaudeEventKind.ToolResult when !ev.IsSubagent:
                    _phase = Phase.Starting; // Claude reads the result and decides what's next
                    break;
                case ClaudeEventKind.TurnCompleted:
                    StartActivityTurn(); // a queued prompt may follow right away
                    break;
                case ClaudeEventKind.PermissionRequest:
                    break; // _waitingForUser is set: "Waiting for you"
                default:
                    return;
            }
            // Time spent thinking: from the first thinking event to the first event of anything else.
            if (_phase == Phase.Thinking) { if (_thinkingSinceUtc == null) _thinkingSinceUtc = DateTime.UtcNow; }
            else if (_thinkingSinceUtc is DateTime since) { _turnThought += DateTime.UtcNow - since; _thinkingSinceUtc = null; }
            RenderActivity();
        }

        /// <summary>Draws the strip from the current state; also every second for the elapsed time and tokens.</summary>
        private void RenderActivity()
        {
            bool stalled = _stall.Visibility == Visibility.Visible;
            bool show = _busy && !stalled;
            _activity.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
            {
                _activityTimer.Stop();
                SetActivityAnimation(false);
                return;
            }
            _activityTimer.Start();

            var a = ChatTheme.Accents(this);
            var phase = _waitingForUser ? Phase.Waiting : _phase;
            string label, what = null;
            Brush color;
            switch (phase)
            {
                case Phase.Thinking:
                    label = _turnVerb + "…";
                    what = "thinking";
                    color = a.Brand;
                    break;
                case Phase.Writing:
                    label = "Writing…";
                    color = a.Link;
                    break;
                case Phase.Tool:
                    label = ToolVerb(_phaseTool) + "…";
                    what = ShortToolText(_phaseTool, _phaseText);
                    color = a.Running;
                    break;
                case Phase.Waiting:
                    label = "Waiting for you";
                    what = "answer the card above the prompt box";
                    color = a.Success;
                    break;
                default:
                    label = _session?.IsRunning == true ? _turnVerb + "…" : "Starting Claude Code…";
                    color = a.Brand;
                    break;
            }

            int secs = (int)(DateTime.UtcNow - _turnStartedUtc).TotalSeconds;
            string detail = secs < 60 ? secs + "s" : $"{secs / 60}m {secs % 60:00}s";
            long tokens = _turnThinkingTokens + _turnOutputChars / 4;
            if (tokens > 0) detail += " · ↓ " + TokenMeter.Format(tokens) + " tokens";
            if (what != null) detail += " · " + what;
            // Like the terminal: once Claude has stopped thinking, how long it thought (explains a long pause).
            if (phase != Phase.Thinking && _turnThought.TotalSeconds >= 1)
                detail += " · thought for " + Core.Cli.ToolCallView.Duration(_turnThought);

            _activityLabel.Text = label;
            _activityDetail.Text = "(" + detail + ")";
            _activityDetail.ToolTip = tokens > 0 ? "↓ tokens: estimated output so far (thinking, text and tool input)" : null;
            _activityHint.Visibility = phase == Phase.Waiting ? Visibility.Collapsed : Visibility.Visible;
            _activityTip.Visibility = phase == Phase.Waiting ? Visibility.Collapsed : Visibility.Visible;
            System.Windows.Automation.AutomationProperties.SetName(_activity, label + " " + detail);

            if (!ReferenceEquals(color, _activityColor))
            {
                _activityColor = color;
                _activityLabel.Foreground = color;
                _spinner.Foreground = color;
                _activity.BorderBrush = color;
                var c = (color as SolidColorBrush)?.Color ?? Colors.Gray;
                _activity.Background = new SolidColorBrush(Color.FromArgb(0x1C, c.R, c.G, c.B));
                _activitySweep.Fill = SweepBrush(c);
                _activityAnimating = false; // the sweep brush is new: start its animation again
            }
            SetActivityAnimation(phase != Phase.Waiting);
            if (!_activityAnimating) _spinner.Text = phase == Phase.Waiting ? "●" : "✻";
        }

        private static Brush SweepBrush(Color c)
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xE0, c.R, c.G, c.B), 0.5));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            brush.RelativeTransform = new TranslateTransform(-1, 0);
            return brush;
        }

        /// <summary>The spinner and the sweep; off when Windows' "Show animations" is off, or while waiting for you.</summary>
        private void SetActivityAnimation(bool on)
        {
            on &= SystemParameters.ClientAreaAnimation;
            if (on == _activityAnimating) return;
            _activityAnimating = on;
            if (on) _spinTimer.Start(); else _spinTimer.Stop();
            var move = (_activitySweep.Fill as Brush)?.RelativeTransform as TranslateTransform;
            if (move == null) return;
            if (on) move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(1600)) { RepeatBehavior = RepeatBehavior.Forever });
            else { move.BeginAnimation(TranslateTransform.XProperty, null); move.X = -1; }
            _activitySweep.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string ToolVerb(string tool)
        {
            switch (tool)
            {
                case "Read": return "Reading";
                case "Edit": case "MultiEdit": case "Write": case "NotebookEdit": return "Editing";
                case "Grep": case "Glob": return "Searching";
                case "Bash": case "PowerShell": return "Running";
                case "WebFetch": case "WebSearch": return "Browsing";
                case "Task": case "Agent": return "Starting an agent";
                case "TodoWrite": return "Planning";
                case null: case "": return "Using a tool";
                default:
                    return tool.StartsWith("mcp__", StringComparison.Ordinal) ? "Using " + tool.Substring(tool.LastIndexOf("__", StringComparison.Ordinal) + 2) : "Using " + tool;
            }
        }

        /// <summary>The tool row's description without the tool name, one line, at most 70 characters.</summary>
        private static string ShortToolText(string tool, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return tool;
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (tool != null && text.StartsWith(tool + " ", StringComparison.Ordinal)) text = text.Substring(tool.Length + 1).TrimStart();
            return text.Length > 70 ? text.Substring(0, 69) + "…" : text;
        }

        // ---------------- Colored meter ----------------

        /// <summary>
        /// The meter line with colored values: context fill green / amber / red at the /compact thresholds, cache
        /// hits green when most input comes from the cache, cost in Claude orange, the model by family.
        /// </summary>
        private void RenderMeter(TokenMeter m, long window)
        {
            var a = ChatTheme.Accents(this);
            var settings = Options?.ToMeterSettings() ?? new TokenMeterSettings();
            double pct = m.ContextPercent;
            Brush ctx = pct >= settings.CompactUrgentPercent ? a.Failure : pct >= settings.CompactSuggestPercent ? a.Running : a.Success;
            double cache = m.CacheHitPercent;
            Brush cacheBrush = m.SessionTotal.Total == 0 ? null : cache >= 70 ? a.Success : cache >= 30 ? a.Running : a.Failure;
            string model = _session?.CurrentModel.ToCli() ?? "-";
            if (model.Length > 1 && char.IsLower(model[0])) model = char.ToUpperInvariant(model[0]) + model.Substring(1); // "Sonnet"
            string family = PriceTable.Family(model);
            Brush modelBrush = family == "opus" ? a.Brand : family == "sonnet" ? a.Link : family == "haiku" ? a.Success : null;

            _ctxBar.Foreground = ctx;
            var lines = _meter.Inlines;
            lines.Clear();
            void Label(string s) => lines.Add(new System.Windows.Documents.Run(s));
            void Value(string s, Brush b)
            {
                var run = new System.Windows.Documents.Run(s) { FontWeight = FontWeights.SemiBold };
                if (b != null) run.Foreground = b;
                else run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey);
                lines.Add(run);
            }
            Label("Context ");
            Value($"{pct:0}%", ctx);
            Label($" ({TokenMeter.Format(m.ContextTokens)}/{TokenMeter.Format(window)})  ·  Session ");
            Value($"{TokenMeter.Format(m.SessionTotal.Total)} tok", null);
            Label(", ");
            Value($"${m.SessionCostUsd:0.00}", a.Brand);
            Label("  ·  Cache ");
            Value($"{cache:0}%", cacheBrush);
            Label("  ·  Model ");
            Value(model, modelBrush);
        }
    }
}
