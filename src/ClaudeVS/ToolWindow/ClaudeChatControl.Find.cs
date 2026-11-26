using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TextSearch = ClaudeVS.Core.Chat.TextSearch;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// Find in the chat (Phase 101): a bar over the conversation. Enter or F3 goes to the next match, Shift+Enter or
    /// Shift+F3 to the one before, Aa matches case, Esc closes it. The matching is <see cref="TextSearch"/>.
    /// </summary>
    public sealed partial class ClaudeChatControl
    {
        private readonly DockPanel _findBar = new DockPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(10, 2, 6, 4) };
        private readonly TextBox _findBox = new TextBox { MinWidth = 120, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock _findStatus = new TextBlock { Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
        private readonly ToggleButton _findCase = new ToggleButton { Content = "Aa", ToolTip = "Match case", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(2, 0, 0, 0) };
        private int _findAt = -1; // the shown match's offset in the conversation's text

        private void InitFind()
        {
            AutomationProperties.SetName(_findBox, "Find in chat");
            ChatTheme.Muted(_findStatus);
            var previous = ChatTheme.IconButton(ChatTheme.IconUp, "Previous match (Shift+Enter)");
            var next = ChatTheme.IconButton(ChatTheme.IconDown, "Next match (Enter)");
            var close = ChatTheme.IconButton(ChatTheme.IconClose, "Close (Esc)");
            previous.Click += (s, e) => FindStep(back: true);
            next.Click += (s, e) => FindStep(back: false);
            close.Click += (s, e) => CloseFind();
            _findCase.Click += (s, e) => { _findAt = -1; FindStep(back: false); };
            _findBox.TextChanged += (s, e) => { _findAt = -1; FindStep(back: false); };
            _findBox.PreviewKeyDown += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                if (e.Key == Key.Enter || e.Key == Key.F3) { e.Handled = true; FindStep(back: shift); }
                else if (e.Key == Key.Escape) { e.Handled = true; CloseFind(); }
            };
            foreach (var b in new UIElement[] { close, next, previous, _findCase, _findStatus })
            {
                DockPanel.SetDock(b, Dock.Right);
                _findBar.Children.Add(b);
            }
            _findBar.Children.Add(_findBox);

            // Ctrl+F in the chat, when VS lets it through (its own Find is usually bound to it); F3 once the bar is open
            PreviewKeyDown += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; ShowFind(); }
                else if (e.Key == Key.F3 && _findBar.Visibility == Visibility.Visible && !_findBox.IsKeyboardFocusWithin)
                {
                    e.Handled = true;
                    FindStep(back: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                }
            };
        }

        /// <summary>Opens the find bar (Tools → Claude Code → Find in Chat, Ctrl+Alt+K Ctrl+F), with the chat's selection as the query.</summary>
        public void ShowFind()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string selected = _view.Box.Selection.IsEmpty ? null : _view.Box.Selection.Text;
            _findBar.Visibility = Visibility.Visible;
            if (!string.IsNullOrWhiteSpace(selected) && selected.Length <= 200 && selected.IndexOf('\n') < 0) _findBox.Text = selected.Trim();
            _findBox.Focus();
            _findBox.SelectAll();
            if (_findBox.Text.Length > 0) { _findAt = -1; FindStep(back: false); }
        }

        private void CloseFind()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _findBar.Visibility = Visibility.Collapsed;
            _findAt = -1;
            _findStatus.Text = "";
            _view.ClearMatch();
            _prompt.Focus();
        }

        /// <summary>Finds again (the chat may have grown) and goes to the match after, or before, the one shown.</summary>
        private void FindStep(bool back)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string query = _findBox.Text;
            var map = _view.MapText();
            var starts = TextSearch.FindAll(map.Text, query, _findCase.IsChecked == true);
            int index = TextSearch.Next(starts, _findAt, back);
            _findStatus.Text = TextSearch.Status(index, starts.Count, query);
            if (index < 0) { _findAt = -1; _view.ClearMatch(); return; }
            _findAt = starts[index];
            _view.ShowMatch(map, _findAt, query.Length);
        }
    }
}
