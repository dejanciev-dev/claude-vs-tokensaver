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
    /// The activity strip above the input box while Claude works: three pulsing dots and a sweeping line in the
    /// color of the current phase (thinking, writing, a tool, waiting for you), the time the turn has taken and,
    /// while thinking, the CLI's estimate of the thinking tokens. The meter line is colored here too.
    /// </summary>
    public partial class ClaudeChatControl
    {
        private enum Phase { Starting, Thinking, Writing, Tool, Waiting }

        private readonly Border _activity = new Border
        {
            Visibility = Visibility.Collapsed,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 6),
        };
        private readonly Ellipse[] _activityDots = new Ellipse[3];
        private readonly TextBlock _activityLabel = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _activityDetail = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _activityHint = new TextBlock { Text = "Esc to stop", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        private readonly Rectangle _activitySweep = new Rectangle { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        private DispatcherTimer _activityTimer;
        private bool _activityAnimating;

        private Phase _phase;
        private string _phaseText;           // what the tool phase is about ("Bash dotnet test")
        private string _phaseTool;           // tool name for the label
        private int? _thinkingTokens;
        private DateTime _turnStartedUtc = DateTime.UtcNow;
        private Brush _activityColor;

        private void InitActivity()
        {
            var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            for (int i = 0; i < _activityDots.Length; i++)
            {
                _activityDots[i] = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0) };
                dots.Children.Add(_activityDots[i]);
            }
            ChatTheme.Muted(_activityDetail);
            ChatTheme.Muted(_activityHint);

            var row = new DockPanel { Margin = new Thickness(8, 5, 8, 6), LastChildFill = true };
            DockPanel.SetDock(dots, Dock.Left);
            DockPanel.SetDock(_activityHint, Dock.Right);
            row.Children.Add(dots);
            row.Children.Add(_activityHint);
            var text = new StackPanel { Orientation = Orientation.Horizontal };
            text.Children.Add(_activityLabel);
            text.Children.Add(_activityDetail);
            row.Children.Add(text);

            var grid = new Grid { ClipToBounds = true };
            grid.Children.Add(row);
            grid.Children.Add(_activitySweep);
            _activity.Child = grid;
            System.Windows.Automation.AutomationProperties.SetName(_activity, "Claude is working");

            _activityTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (s, e) => RenderActivity(), Dispatcher);
            _activityTimer.Stop();
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
            _thinkingTokens = null;
        }

        /// <summary>Events that change what the strip says. Called for every event on the UI thread.</summary>
        private void OnActivityEvent(ClaudeEvent ev)
        {
            switch (ev.Kind)
            {
                case ClaudeEventKind.Activity:
                    if (ev.Activity == ClaudeActivity.Thinking)
                    {
                        if (_phase != Phase.Thinking) _thinkingTokens = null;
                        _phase = Phase.Thinking;
                        if (ev.EstimatedTokens is int t) _thinkingTokens = (_thinkingTokens ?? 0) + t;
                    }
                    else if (ev.Activity == ClaudeActivity.Writing) _phase = Phase.Writing;
                    else { _phase = Phase.Tool; _phaseTool = ev.ToolName; _phaseText = null; }
                    break;
                case ClaudeEventKind.ToolUse when !ev.IsSubagent:
                    _phase = Phase.Tool;
                    _phaseTool = ev.ToolName;
                    _phaseText = ev.Text;
                    break;
                case ClaudeEventKind.ToolResult when !ev.IsSubagent:
                    _phase = Phase.Thinking; // Claude reads the result and decides what's next
                    _thinkingTokens = null;
                    break;
                case ClaudeEventKind.TurnCompleted:
                    StartActivityTurn(); // a queued prompt may follow right away
                    break;
                case ClaudeEventKind.PermissionRequest:
                    break; // _waitingForUser is set: "Waiting for you"
                default:
                    return;
            }
            RenderActivity();
        }

        /// <summary>Draws the strip from the current state; also every second for the elapsed time.</summary>
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
            string label, detail = null;
            Brush color;
            switch (phase)
            {
                case Phase.Thinking:
                    label = "Thinking";
                    color = a.Brand;
                    if (_thinkingTokens is int t && t > 0) detail = "~" + TokenMeter.Format(t) + " tok";
                    break;
                case Phase.Writing:
                    label = "Writing";
                    color = a.Link;
                    break;
                case Phase.Tool:
                    label = ToolVerb(_phaseTool);
                    color = a.Running;
                    detail = ShortToolText(_phaseTool, _phaseText);
                    break;
                case Phase.Waiting:
                    label = "Waiting for you";
                    color = a.Success;
                    detail = "answer the card or the Allow/Deny bar";
                    break;
                default:
                    label = _session?.IsRunning == true ? "Working" : "Starting Claude Code";
                    color = a.Brand;
                    break;
            }

            int secs = (int)(DateTime.UtcNow - _turnStartedUtc).TotalSeconds;
            string elapsed = secs < 60 ? secs + "s" : $"{secs / 60}m {secs % 60:00}s";
            detail = detail == null ? elapsed : elapsed + " · " + detail;

            _activityLabel.Text = label;
            _activityDetail.Text = detail;
            _activityHint.Visibility = phase == Phase.Waiting ? Visibility.Collapsed : Visibility.Visible;
            System.Windows.Automation.AutomationProperties.SetName(_activity, label + ", " + detail);

            if (!ReferenceEquals(color, _activityColor))
            {
                _activityColor = color;
                _activityLabel.Foreground = color;
                _activity.BorderBrush = color;
                var c = (color as SolidColorBrush)?.Color ?? Colors.Gray;
                _activity.Background = new SolidColorBrush(Color.FromArgb(0x1C, c.R, c.G, c.B));
                foreach (var dot in _activityDots) dot.Fill = color;
                _activitySweep.Fill = SweepBrush(c);
                _activityAnimating = false; // the sweep brush is new: start its animation again
            }
            SetActivityAnimation(phase != Phase.Waiting);
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

        /// <summary>Pulsing dots and the sweep; off when Windows' "Show animations" is off, or while waiting for you.</summary>
        private void SetActivityAnimation(bool on)
        {
            on &= SystemParameters.ClientAreaAnimation;
            if (on == _activityAnimating) return;
            _activityAnimating = on;
            for (int i = 0; i < _activityDots.Length; i++)
            {
                if (!on) { _activityDots[i].BeginAnimation(UIElement.OpacityProperty, null); _activityDots[i].Opacity = 1; continue; }
                _activityDots[i].BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(450))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(150 * i),
                    EasingFunction = new SineEase(),
                });
            }
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
