using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeVS.Core.Cli;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// The permission card above the input box: a plain question ("Run this command?"), Claude's description, the
    /// command or file in a code box, then Allow, Always allow (the CLI's suggested rule), Deny, and a box to tell
    /// Claude what to do instead.
    /// </summary>
    public partial class ClaudeChatControl
    {
        private readonly Border _permission = new Border
        {
            Visibility = Visibility.Collapsed,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(3, 1, 1, 1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
        };
        private readonly TextBlock _permTitle = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _permDescription = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        private readonly Border _permDetailBox = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBox _permDetail = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        /// <summary>"… +N lines" under a long command: no scroll box, so the card keeps its size and Allow / Deny stay in view.</summary>
        private readonly TextBlock _permDetailToggle = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0) };
        private string _permFullDetail;
        private const int PermPreviewLines = 6;  // shown before "… +N lines"
        private const int PermMaxInPlace = 25;   // longer than this opens in the editor instead of growing the card
        private readonly Button _permAllow = ChatTheme.PrimaryButton("Allow", "Allow this once");
        private readonly Button _permAlways = ChatTheme.TextButton("Always allow");
        private readonly Button _permDeny = ChatTheme.TextButton("Deny", "Deny; Claude is told you said no");
        private readonly TextBox _permFeedback = new TextBox { FontSize = 12, Padding = new Thickness(6, 3, 6, 3), BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock _permFeedbackHint = new TextBlock { Text = "Or tell Claude what to do instead, then Enter", FontSize = 12, IsHitTestVisible = false, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private PermissionPrompt _permPrompt;

        private void BuildPermissionCard()
        {
            ChatTheme.Text(_permDescription);
            ChatTheme.Text(_permDetail);
            _permDetail.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            _permDetail.FontSize = 12;
            _permDetail.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey);
            _permDetailBox.Background = ChatTheme.Subtle;
            var detail = new StackPanel();
            detail.Children.Add(_permDetail);
            detail.Children.Add(_permDetailToggle);
            _permDetailBox.Child = detail;

            _permAllow.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); AnswerPermission(true); };
            _permAlways.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); AnswerPermission(true, always: true); };
            _permDeny.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); AnswerPermission(false, feedback: _permFeedback.Text); };
            var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(_permAllow);
            buttons.Children.Add(_permAlways);
            buttons.Children.Add(_permDeny);

            ChatTheme.Text(_permFeedback);
            ChatTheme.Muted(_permFeedbackHint);
            _permFeedback.Background = Brushes.Transparent;
            _permFeedback.BorderBrush = ChatTheme.Hover;
            _permFeedback.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey);
            _permFeedback.TextChanged += (s, e) => _permFeedbackHint.Visibility = _permFeedback.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _permFeedback.KeyDown += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (e.Key == Key.Enter && _permFeedback.Text.Trim().Length > 0) { e.Handled = true; AnswerPermission(false, feedback: _permFeedback.Text); }
                else if (e.Key == Key.Escape) { e.Handled = true; _permFeedback.Clear(); _prompt.Focus(); }
            };
            System.Windows.Automation.AutomationProperties.SetName(_permFeedback, "Tell Claude what to do instead");
            var feedback = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            feedback.Children.Add(_permFeedback);
            feedback.Children.Add(_permFeedbackHint);

            var stack = new StackPanel();
            stack.Children.Add(_permTitle);
            stack.Children.Add(_permDescription);
            stack.Children.Add(_permDetailBox);
            stack.Children.Add(buttons);
            stack.Children.Add(feedback);
            _permission.Child = stack;
        }

        private void ShowPermission(ClaudeEvent ev)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _pendingPermission = ev;
            var p = _permPrompt = PermissionPrompt.From(ev.ToolName, ev.ToolInput, ev.Payload);
            var a = ChatTheme.Accents(this);
            var c = (a.Running as SolidColorBrush)?.Color ?? Colors.Goldenrod;
            _permission.BorderBrush = a.Running;
            _permission.Background = new SolidColorBrush(Color.FromArgb(0x16, c.R, c.G, c.B));

            _permTitle.Inlines.Clear();
            _permTitle.Inlines.Add(new System.Windows.Documents.Run("⚠ ") { Foreground = a.Running });
            var title = new System.Windows.Documents.Run(p.Title);
            title.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey);
            _permTitle.Inlines.Add(title);

            _permDescription.Text = p.Description ?? "";
            _permDescription.Visibility = p.Description == null ? Visibility.Collapsed : Visibility.Visible;
            SetPermissionDetail(p.Detail ?? "", expanded: false);
            if (p.DetailIsCode) _permDetail.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            else _permDetail.SetResourceReference(Control.FontFamilyProperty, VsFonts.EnvironmentFontFamilyKey);
            if (!p.DetailIsCode) _permDetail.Foreground = a.Path;
            else ChatTheme.Text(_permDetail);
            _permDetailBox.Visibility = p.Detail == null ? Visibility.Collapsed : Visibility.Visible;

            if (p.AlwaysAllow != null)
            {
                string rules = string.Join(", ", p.AlwaysAllowRules);
                _permAlways.Content = new TextBlock { Text = "Always allow " + rules, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 };
                _permAlways.ToolTip = $"Allow now, and don't ask again for {rules} {p.AlwaysAllowScope}";
                System.Windows.Automation.AutomationProperties.SetName(_permAlways, "Always allow " + rules);
                _permAlways.Visibility = Visibility.Visible;
            }
            else _permAlways.Visibility = Visibility.Collapsed;

            _permFeedback.Clear();
            _permission.Visibility = Visibility.Visible;
            Announce($"Claude asks: {p.Title} {p.Description ?? p.Detail}. The Allow and Deny buttons are above the prompt box.", assertive: true);
        }

        /// <summary>
        /// The command or file in the card. A long one shows its first lines and "… +N lines": up to 25 lines open in
        /// the card ("Show less" folds them back); a longer one (a heredoc writing a whole file) opens in the editor,
        /// so the card never grows past the window and the buttons stay in view.
        /// </summary>
        private void SetPermissionDetail(string full, bool expanded)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _permFullDetail = full;
            var lines = full.TrimEnd().Split('\n');
            _permDetailToggle.Inlines.Clear();
            if (lines.Length <= PermPreviewLines + 2)
            {
                _permDetail.Text = full;
                _permDetailToggle.Visibility = Visibility.Collapsed;
                return;
            }
            _permDetailToggle.Visibility = Visibility.Visible;
            int hidden = lines.Length - PermPreviewLines;
            if (expanded)
            {
                _permDetail.Text = full;
                _permDetailToggle.Inlines.Add(PermLink("Show less", "Fold the command back to its first lines", () => SetPermissionDetail(_permFullDetail, expanded: false)));
            }
            else
            {
                _permDetail.Text = string.Join("\n", lines, 0, PermPreviewLines).TrimEnd('\r');
                if (lines.Length <= PermMaxInPlace)
                    _permDetailToggle.Inlines.Add(PermLink($"… +{hidden} lines (click to see all)", "Show the whole command here", () => SetPermissionDetail(_permFullDetail, expanded: true)));
                else
                    _permDetailToggle.Inlines.Add(PermLink($"… +{hidden} lines (open the whole command in the editor)", "Too long for the card: opens it in a read-only editor tab", OpenPermissionDetailInEditor));
            }
        }

        private System.Windows.Documents.Hyperlink PermLink(string text, string tip, Action onClick)
        {
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text))
            {
                Foreground = ChatTheme.Accents(this).Link, ToolTip = tip, Cursor = Cursors.Hand, TextDecorations = null,
            };
            link.MouseEnter += (s, e) => link.TextDecorations = TextDecorations.Underline;
            link.MouseLeave += (s, e) => link.TextDecorations = null;
            link.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); onClick(); e.Handled = true; };
            return link;
        }

        /// <summary>Writes the command to a temp file (.sh / .ps1 for syntax colors) and opens it in Visual Studio.</summary>
        private void OpenPermissionDetailInEditor()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                string tool = _pendingPermission?.ToolName ?? "";
                string ext = tool.Equals("Bash", StringComparison.OrdinalIgnoreCase) ? ".sh"
                           : tool.Equals("PowerShell", StringComparison.OrdinalIgnoreCase) ? ".ps1" : ".txt";
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClaudeVS", "commands");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "command-" + DateTime.Now.ToString("HHmmss") + ext);
                System.IO.File.WriteAllText(path, _permFullDetail ?? "");
                System.IO.File.SetAttributes(path, System.IO.FileAttributes.ReadOnly);
                Commands.VsContext.OpenFile(path);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Open command failed: " + ex.Message); }
        }

        /// <summary>Allow (once or always) or deny; a denial can carry what to do instead.</summary>
        private void AnswerPermission(bool allow, bool always = false, string feedback = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var req = _pendingPermission;
            var prompt = _permPrompt;
            _pendingPermission = null;
            _permPrompt = null;
            _permission.Visibility = Visibility.Collapsed;
            _waitingForUser = false;
            RenderActivity();
            System.Threading.Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
            if (req == null || _session == null) return;

            feedback = string.IsNullOrWhiteSpace(feedback) ? null : feedback.Trim();
            JArray rules = always ? prompt?.AlwaysAllow : null;
            if (allow) Append(rules != null ? $"  ✔ Allowed, and from now on: {string.Join(", ", prompt.AlwaysAllowRules)}\n" : "  ✔ Allowed\n");
            else Append(feedback != null ? $"  ✖ Denied: {feedback}\n" : "  ✖ Denied\n");
            string denyMessage = feedback != null ? "The user denied this and said what to do instead: " + feedback : null;
            var session = _session;
            Fire(() => session.RespondToPermissionAsync(req.RequestId, req.ToolInput ?? new JObject(), allow, denyMessage, rules));
            _prompt.Focus();
        }
    }
}
