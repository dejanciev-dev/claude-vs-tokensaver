using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ClaudeVS.Core.Chat;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// The list of slash commands above the input box while a "/name" is typed: one row per command (its plugin or
    /// folder muted, the name in bold, what it does underneath, where it comes from on the right). ↑ ↓ choose,
    /// Tab or Enter put it in the box, Esc closes the list; Enter on a command already typed in full sends it.
    /// </summary>
    public partial class ClaudeChatControl
    {
        private const int CommandRowsShown = 8;
        private readonly ListBox _commandList = new ListBox { MaxHeight = 300, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        private Popup _commandPopup;
        private List<SlashCommand> _commandMatches = new List<SlashCommand>();
        private string _commandDismissedFor; // the text the list was closed for with Esc

        private Popup BuildCommandPicker()
        {
            _commandList.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _commandList.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _commandList.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            ScrollViewer.SetHorizontalScrollBarVisibility(_commandList, ScrollBarVisibility.Disabled);
            _commandList.PreviewMouseLeftButtonUp += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (ItemsControl.ContainerFromElement(_commandList, e.OriginalSource as DependencyObject) is ListBoxItem) PickCommand();
            };
            var border = new Border { BorderThickness = new Thickness(1), Child = _commandList };
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            return new Popup { PlacementTarget = _prompt, Placement = PlacementMode.Top, StaysOpen = false, Child = border, AllowsTransparency = false };
        }

        /// <summary>Shows the commands matching the "/name" being typed; closes the list otherwise.</summary>
        private void UpdateCommandPicker()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string typed = SlashCommands.TypedName(_prompt.Text);
            if (typed == null || _prompt.Text == _commandDismissedFor) { _commandPopup.IsOpen = false; return; }
            _commandDismissedFor = null;
            RefreshCommands();
            _commandMatches = SlashCommands.Match(_commands, _prompt.Text);
            _commandList.Items.Clear();
            if (_commandMatches.Count == 0)
            {
                _commandList.Items.Add(new ListBoxItem { Content = NoMatchRow(), IsEnabled = false });
            }
            else
            {
                foreach (var c in _commandMatches) _commandList.Items.Add(new ListBoxItem { Content = CommandRow(c), Padding = new Thickness(8, 4, 8, 4) });
                _commandList.SelectedIndex = 0;
                _commandList.ScrollIntoView(_commandList.SelectedItem);
            }
            _commandPopup.Width = System.Math.Max(300, _prompt.ActualWidth);
            _commandPopup.IsOpen = true;
        }

        private static FrameworkElement CommandRow(SlashCommand c)
        {
            var name = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
            ChatTheme.Text(name);
            // The plugin or folder muted, so the part you would type stands out: /anthropic-skills:docs
            var slash = new System.Windows.Documents.Run("/" + c.Prefix);
            if (c.Prefix.Length > 0) slash.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
            name.Inlines.Add(slash);
            name.Inlines.Add(new System.Windows.Documents.Run(c.ShortName) { FontWeight = FontWeights.SemiBold });

            var source = new TextBlock { Text = c.SourceLabel, FontSize = 11, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            ChatTheme.Muted(source);
            var top = new DockPanel();
            DockPanel.SetDock(source, Dock.Right);
            top.Children.Add(source);
            top.Children.Add(name);

            var stack = new StackPanel();
            stack.Children.Add(top);
            if (!string.IsNullOrWhiteSpace(c.Description))
            {
                var desc = new TextBlock { Text = c.Description, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
                ChatTheme.Muted(desc);
                stack.Children.Add(desc);
            }
            stack.ToolTip = "/" + c.Name + (string.IsNullOrWhiteSpace(c.Description) ? "" : "\n" + c.Description);
            return stack;
        }

        private static FrameworkElement NoMatchRow()
        {
            var t = new TextBlock { Text = "No matching command. Custom commands live in .claude/commands/*.md", FontSize = 11.5, Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
            ChatTheme.Muted(t);
            return t;
        }

        /// <summary>Keys while the list is open; true when handled.</summary>
        private bool HandleCommandPickerKey(Key key)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int count = _commandMatches.Count;
            switch (key)
            {
                case Key.Down when count > 0:
                    _commandList.SelectedIndex = (_commandList.SelectedIndex + 1) % count;
                    _commandList.ScrollIntoView(_commandList.SelectedItem);
                    return true;
                case Key.Up when count > 0:
                    _commandList.SelectedIndex = (_commandList.SelectedIndex - 1 + count) % count;
                    _commandList.ScrollIntoView(_commandList.SelectedItem);
                    return true;
                case Key.PageDown when count > 0:
                    _commandList.SelectedIndex = System.Math.Min(count - 1, _commandList.SelectedIndex + CommandRowsShown);
                    _commandList.ScrollIntoView(_commandList.SelectedItem);
                    return true;
                case Key.PageUp when count > 0:
                    _commandList.SelectedIndex = System.Math.Max(0, _commandList.SelectedIndex - CommandRowsShown);
                    _commandList.ScrollIntoView(_commandList.SelectedItem);
                    return true;
                case Key.Tab when count > 0 && (Keyboard.Modifiers & ModifierKeys.Shift) == 0:
                    PickCommand();
                    return true;
                case Key.Enter when count > 0 && (Keyboard.Modifiers & ModifierKeys.Shift) == 0:
                    // A command typed in full: Enter sends it, as without the list.
                    if (SelectedCommand() is SlashCommand c && string.Equals(_prompt.Text.Trim(), "/" + c.Name, System.StringComparison.OrdinalIgnoreCase))
                    {
                        _commandPopup.IsOpen = false;
                        return false;
                    }
                    PickCommand();
                    return true;
                case Key.Escape:
                    _commandDismissedFor = _prompt.Text;
                    _commandPopup.IsOpen = false;
                    return true;
                default:
                    return false;
            }
        }

        private SlashCommand SelectedCommand()
        {
            int i = _commandList.SelectedIndex;
            return i >= 0 && i < _commandMatches.Count ? _commandMatches[i] : null;
        }

        /// <summary>Puts "/name " in the input box, ready for arguments (or Enter to run it).</summary>
        private void PickCommand()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(SelectedCommand() is SlashCommand c)) return;
            _commandPopup.IsOpen = false;
            _prompt.Text = "/" + c.Name + " ";
            _prompt.CaretIndex = _prompt.Text.Length;
            _prompt.Focus();
        }
    }
}
