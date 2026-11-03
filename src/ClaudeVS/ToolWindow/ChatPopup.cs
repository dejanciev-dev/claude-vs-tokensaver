using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.VisualStudio.PlatformUI;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// A themed popup list used for every menu in the chat (the + menu, the / actions palette, model and mode
    /// pickers, history): optional filter box, group headers, rows with icon / label / description / detail /
    /// check mark, keyboard navigation (↑ ↓ Enter Esc). Built in code so it follows the VS theme instead of WPF's
    /// default (light) menu style.
    /// </summary>
    internal sealed class ChatPopup
    {
        public sealed class Entry
        {
            public string Group { get; set; }
            public string Label { get; set; }
            /// <summary>Muted line under the label.</summary>
            public string Description { get; set; }
            /// <summary>Muted text on the right (e.g. "5m", "Opus").</summary>
            public string Detail { get; set; }
            public string Glyph { get; set; }
            public bool Checked { get; set; }
            public bool Enabled { get; set; } = true;
            public Action Run { get; set; }
            /// <summary>Small icon buttons shown on the right when the row is hovered or selected (archive…).</summary>
            public List<EntryAction> Actions { get; set; }
            /// <summary>When set, a rename button turns the label into a text box; Enter passes the new text here.</summary>
            public Action<string> Rename { get; set; }
        }

        public sealed class EntryAction
        {
            public string Glyph { get; set; }
            public string Tooltip { get; set; }
            public Action Run { get; set; }
        }

        private readonly Popup _popup;
        private readonly TextBox _filter;
        private readonly TextBlock _filterPlaceholder;
        private readonly ListBox _list;
        private readonly TextBlock _footer;
        private List<Entry> _entries = new List<Entry>();

        public ChatPopup(UIElement target, bool withFilter, string filterPlaceholder = "Filter…", double width = 300, PlacementMode placement = PlacementMode.Top)
        {
            _list = new ListBox
            {
                MaxHeight = 380,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            ChatTheme.Text(_list);
            _list.PreviewKeyDown += (s, e) => HandleKey(e);

            _filter = new TextBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(2, 3, 2, 3), FontSize = 12.5 };
            ChatTheme.Text(_filter);
            _filter.SetResourceReference(TextBoxBase.CaretBrushProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _filter.TextChanged += (s, e) => { _filterPlaceholder.Visibility = _filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Rebuild(); };
            _filter.PreviewKeyDown += (s, e) => HandleKey(e);
            _filterPlaceholder = new TextBlock { Text = filterPlaceholder, IsHitTestVisible = false, Margin = new Thickness(4, 3, 0, 0), FontSize = 12.5 };
            ChatTheme.Muted(_filterPlaceholder);

            _footer = new TextBlock { FontSize = 11, Margin = new Thickness(10, 4, 10, 6), Visibility = Visibility.Collapsed };
            ChatTheme.Muted(_footer);

            var stack = new DockPanel();
            if (withFilter)
            {
                var filterBox = new Grid();
                filterBox.Children.Add(_filter);
                filterBox.Children.Add(_filterPlaceholder);
                var filterBorder = new Border { Child = filterBox, Background = ChatTheme.Subtle, CornerRadius = new CornerRadius(4), Margin = new Thickness(6, 6, 6, 4), Padding = new Thickness(4, 0, 4, 0) };
                DockPanel.SetDock(filterBorder, Dock.Top);
                stack.Children.Add(filterBorder);
            }
            DockPanel.SetDock(_footer, Dock.Bottom);
            stack.Children.Add(_footer);
            stack.Children.Add(_list);

            var border = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0, 2, 0, 2),
                Width = width,
                Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.35 },
                Margin = new Thickness(6), // room for the shadow
            };
            border.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            ChatTheme.UseEnvironmentFont(border);

            _popup = new Popup
            {
                Child = border,
                PlacementTarget = target,
                Placement = placement,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
            };
            _popup.Opened += (s, e) =>
            {
                if (withFilter) _filter.Focus();
                else (_list.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, _list.SelectedIndex)) as UIElement)?.Focus();
            };
        }

        public bool IsOpen => _popup.IsOpen;

        /// <summary>Optional muted note at the bottom (e.g. "Shift+Tab to switch").</summary>
        public string Footer
        {
            get => _footer.Text;
            set { _footer.Text = value; _footer.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible; }
        }

        /// <summary>
        /// Optional: an extra first row built from the typed filter text (e.g. Create group "…"); null for none.
        /// Cleared by every <see cref="Show"/> that does not pass one.
        /// </summary>
        private Func<string, Entry> _fromFilter;

        /// <summary>Opens the popup (or refreshes it if open). <paramref name="keepFilter"/> keeps the typed filter text.</summary>
        public void Show(IEnumerable<Entry> entries, UIElement target = null, bool keepFilter = false, Func<string, Entry> fromFilter = null)
        {
            _entries = entries.ToList();
            _fromFilter = fromFilter;
            if (target != null) _popup.PlacementTarget = target;
            if (!keepFilter) _filter.Text = "";
            Rebuild();
            _popup.IsOpen = true;
        }

        public void Close() => _popup.IsOpen = false;

        private void Rebuild()
        {
            _list.Items.Clear();
            string f = _filter.Text.Trim();
            string lastGroup = null;
            var typed = f.Length > 0 ? _fromFilter?.Invoke(f) : null;
            foreach (var e in (typed != null ? new[] { typed } : Enumerable.Empty<Entry>()).Concat(_entries.Where(e => Matches(e, f))))
            {
                if (!string.IsNullOrEmpty(e.Group) && e.Group != lastGroup)
                {
                    var header = new TextBlock { Text = e.Group, FontSize = 11, Margin = new Thickness(4, lastGroup == null ? 2 : 8, 0, 2) };
                    ChatTheme.Muted(header);
                    _list.Items.Add(new ListBoxItem { Content = header, IsEnabled = false, Focusable = false, Padding = new Thickness(4, 0, 4, 0) });
                    lastGroup = e.Group;
                }
                var item = new ListBoxItem { Content = Row(e), Tag = e, IsEnabled = e.Enabled, Padding = new Thickness(6, 4, 8, 4), Cursor = Cursors.Hand };
                item.PreviewMouseLeftButtonUp += (s, ev) =>
                {
                    if (IsInside<ButtonBase>(ev.OriginalSource) || IsInside<TextBox>(ev.OriginalSource)) return; // a row action or the rename box
                    ev.Handled = true;
                    RunEntry(e);
                };
                _list.Items.Add(item);
            }
            if (_list.Items.Count == 0)
            {
                var none = new TextBlock { Text = "No matches", Margin = new Thickness(4) };
                ChatTheme.Muted(none);
                _list.Items.Add(new ListBoxItem { Content = none, IsEnabled = false, Focusable = false });
            }
            SelectFirst(e => typed != null ? e == typed : e.Checked); // Enter on a typed name creates it
        }

        private static bool Matches(Entry e, string f) =>
            f.Length == 0
            || (e.Label ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
            || (e.Description ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
            || (e.Group ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsInside<T>(object source) where T : DependencyObject
        {
            for (var d = source as DependencyObject; d != null && !(d is ListBoxItem); d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is T) return true;
            return false;
        }

        private UIElement Row(Entry e)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var glyph = new TextBlock { Text = e.Glyph ?? "", FontFamily = ChatTheme.IconFont, FontSize = 13, Width = e.Glyph == null ? 0 : 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            ChatTheme.Text(glyph);
            var text = new StackPanel();
            var label = new TextBlock { Text = e.Label, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 };
            ChatTheme.Text(label);
            text.Children.Add(label);
            if (!string.IsNullOrEmpty(e.Description))
            {
                var d = new TextBlock { Text = e.Description, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 1, 0, 0) };
                ChatTheme.Muted(d);
                text.Children.Add(d);
            }
            var detail = new TextBlock { Text = e.Detail ?? "", FontSize = 11, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            ChatTheme.Muted(detail);
            var check = new TextBlock { Text = e.Checked ? "" : "", FontFamily = ChatTheme.IconFont, FontSize = 12, Width = 18, Margin = new Thickness(6, 2, 0, 0), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            ChatTheme.Text(check);

            Grid.SetColumn(glyph, 0); Grid.SetColumn(text, 1); Grid.SetColumn(detail, 2); Grid.SetColumn(check, 3);
            grid.Children.Add(glyph); grid.Children.Add(text); grid.Children.Add(detail); grid.Children.Add(check);

            // Row actions (rename, archive…): shown while the mouse is over the row, like VS Code's list actions.
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Hidden, Margin = new Thickness(4, -2, 0, -2), VerticalAlignment = VerticalAlignment.Top };
            if (e.Rename != null)
                actions.Children.Add(ActionButton(ChatTheme.IconEdit, "Rename", () => StartRename(e, text, label)));
            foreach (var a in e.Actions ?? Enumerable.Empty<EntryAction>())
                actions.Children.Add(ActionButton(a.Glyph, a.Tooltip, a.Run));
            if (actions.Children.Count > 0)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(actions, 4);
                grid.Children.Add(actions);
                grid.Background = Brushes.Transparent; // hit-testable everywhere, so hover works across the row
                grid.MouseEnter += (s, ev) => actions.Visibility = Visibility.Visible;
                grid.MouseLeave += (s, ev) => actions.Visibility = Visibility.Hidden;
            }
            return grid;
        }

        private static Button ActionButton(string glyph, string tooltip, Action run)
        {
            var b = ChatTheme.IconButton(glyph, tooltip);
            b.FontSize = 11;
            b.Width = 22;
            b.Height = 20;
            b.Click += (s, ev) => { ev.Handled = true; run?.Invoke(); };
            return b;
        }

        /// <summary>Swaps the row's label for a text box: Enter saves through <see cref="Entry.Rename"/>, Esc cancels.</summary>
        private void StartRename(Entry e, StackPanel text, TextBlock label)
        {
            var box = new TextBox { Text = e.Label, FontSize = 12.5, Padding = new Thickness(1, 0, 1, 0), BorderThickness = new Thickness(1) };
            ChatTheme.Text(box);
            box.Background = ChatTheme.Subtle;
            box.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.SystemHighlightBrushKey);
            box.SetResourceReference(TextBoxBase.CaretBrushProperty, EnvironmentColors.ToolWindowTextBrushKey);
            int at = text.Children.IndexOf(label);
            text.Children.RemoveAt(at);
            text.Children.Insert(at, box);
            bool done = false;
            void Finish(bool save)
            {
                if (done) return;
                done = true;
                if (save && box.Text.Trim() != (e.Label ?? "")) e.Rename(box.Text);
                else { text.Children.Remove(box); text.Children.Insert(at, label); _list.Focus(); }
            }
            box.KeyDown += (s, ev) =>
            {
                if (ev.Key == Key.Enter) { ev.Handled = true; Finish(true); }
                else if (ev.Key == Key.Escape) { ev.Handled = true; Finish(false); }
            };
            box.LostKeyboardFocus += (s, ev) => { if (_popup.IsOpen) Finish(true); };
            box.Focus();
            box.SelectAll();
        }

        private void SelectFirst(Func<Entry, bool> preferred)
        {
            var items = _list.Items.OfType<ListBoxItem>().Where(i => i.IsEnabled && i.Tag is Entry).ToList();
            var pick = items.FirstOrDefault(i => preferred((Entry)i.Tag)) ?? items.FirstOrDefault();
            _list.SelectedItem = pick;
            if (pick != null) _list.ScrollIntoView(pick);
        }

        private void Move(int delta)
        {
            var items = _list.Items.OfType<ListBoxItem>().Where(i => i.IsEnabled && i.Tag is Entry).ToList();
            if (items.Count == 0) return;
            int at = items.IndexOf(_list.SelectedItem as ListBoxItem);
            var next = items[(at + delta + items.Count) % items.Count];
            _list.SelectedItem = next;
            _list.ScrollIntoView(next);
        }

        private void HandleKey(KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox box && box != _filter) return; // typing in a rename box
            switch (e.Key)
            {
                case Key.Down: Move(1); e.Handled = true; break;
                case Key.Up: Move(-1); e.Handled = true; break;
                case Key.Enter:
                case Key.Tab:
                    if ((_list.SelectedItem as ListBoxItem)?.Tag is Entry entry) RunEntry(entry);
                    e.Handled = true;
                    break;
                case Key.Escape: Close(); e.Handled = true; break;
            }
        }

        private void RunEntry(Entry e)
        {
            if (!e.Enabled) return;
            Close();
            e.Run?.Invoke();
        }
    }
}
