using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ClaudeVS.Core.Chat;
using Microsoft.VisualStudio.PlatformUI;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// Side panel listing the bookmarked answers for this solution: a search box, then one row per bookmark
    /// (title, age, a remove button). Clicking a row raises <see cref="OpenRequested"/>. UI thread only.
    /// </summary>
    internal sealed class BookmarksPanel
    {
        private readonly TextBox _search = new TextBox { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(2, 3, 2, 3), FontSize = 12 };
        private readonly TextBlock _searchPlaceholder = new TextBlock { Text = "Search bookmarks…", IsHitTestVisible = false, Margin = new Thickness(4, 3, 0, 0), FontSize = 12 };
        private readonly StackPanel _list = new StackPanel();
        private readonly TextBlock _empty = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(8, 6, 8, 6) };
        private List<Bookmark> _items = new List<Bookmark>();

        public BookmarksPanel()
        {
            var title = new TextBlock { Text = "Bookmarks", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            ChatTheme.Text(title);
            var close = ChatTheme.IconButton(ChatTheme.IconClose, "Close bookmarks");
            close.FontSize = 11;
            close.Click += (s, e) => Close?.Invoke(this, EventArgs.Empty);
            var header = new DockPanel { Margin = new Thickness(0, 6, 4, 4) };
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
            header.Children.Add(title);

            ChatTheme.Text(_search);
            _search.SetResourceReference(TextBoxBase.CaretBrushProperty, EnvironmentColors.ToolWindowTextBrushKey);
            ChatTheme.Muted(_searchPlaceholder);
            _search.TextChanged += (s, e) => { _searchPlaceholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Rebuild(); };
            _search.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { Close?.Invoke(this, EventArgs.Empty); e.Handled = true; } };
            var searchGrid = new Grid();
            searchGrid.Children.Add(_search);
            searchGrid.Children.Add(_searchPlaceholder);
            var searchBox = new Border { Child = searchGrid, Background = ChatTheme.Subtle, CornerRadius = new CornerRadius(4), Margin = new Thickness(6, 0, 6, 6), Padding = new Thickness(4, 0, 4, 0) };
            ChatTheme.Muted(_empty);

            var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(searchBox, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(searchBox);
            root.Children.Add(scroll);

            View = new Border { Child = root, Width = 250, BorderThickness = new Thickness(1, 0, 0, 0), Visibility = Visibility.Collapsed };
            View.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        }

        public Border View { get; }

        public bool IsOpen => View.Visibility == Visibility.Visible;

        public event EventHandler<Bookmark> OpenRequested;
        public event EventHandler<Bookmark> RemoveRequested;
        public event EventHandler Close;

        public void Show(IEnumerable<Bookmark> items)
        {
            View.Visibility = Visibility.Visible;
            SetItems(items);
            _search.Focus();
        }

        public void Hide() => View.Visibility = Visibility.Collapsed;

        /// <summary>Replaces the list (keeps the search text).</summary>
        public void SetItems(IEnumerable<Bookmark> items)
        {
            _items = items?.ToList() ?? new List<Bookmark>();
            Rebuild();
        }

        private void Rebuild()
        {
            _list.Children.Clear();
            var now = DateTime.UtcNow;
            var hits = _items.Where(b => b.Matches(_search.Text)).ToList();
            foreach (var b in hits) _list.Children.Add(Row(b, now));
            if (hits.Count == 0)
            {
                _empty.Text = _items.Count == 0
                    ? "No bookmarks yet. Click the star under an answer to keep it here."
                    : "No bookmark matches.";
                _list.Children.Add(_empty);
            }
        }

        private UIElement Row(Bookmark b, DateTime now)
        {
            var title = new TextBlock { Text = b.Title, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 };
            ChatTheme.Text(title);
            string preview = Preview(b.Answer);
            var sub = new TextBlock { Text = SessionHistory.TimeAgo(b.CreatedUtc, now) + (preview.Length > 0 ? " · " + preview : ""), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, Margin = new Thickness(0, 1, 0, 0) };
            ChatTheme.Muted(sub);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(title);
            text.Children.Add(sub);

            var remove = ChatTheme.IconButton(ChatTheme.IconDelete, "Remove bookmark");
            remove.FontSize = 11;
            remove.Width = 22;
            remove.Height = 22;
            remove.Visibility = Visibility.Hidden; // shown on hover, like VS Code's list actions
            remove.Click += (s, e) => { e.Handled = true; RemoveRequested?.Invoke(this, b); };

            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(text);

            var item = new Border { Child = row, Padding = new Thickness(8, 4, 4, 4), Margin = new Thickness(4, 0, 4, 1), CornerRadius = new CornerRadius(4), Background = System.Windows.Media.Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = string.IsNullOrEmpty(b.Prompt) ? null : b.Prompt };
            item.MouseEnter += (s, e) => { item.Background = ChatTheme.Hover; remove.Visibility = Visibility.Visible; };
            item.MouseLeave += (s, e) => { item.Background = System.Windows.Media.Brushes.Transparent; remove.Visibility = Visibility.Hidden; };
            item.MouseLeftButtonUp += (s, e) => { if (!e.Handled) OpenRequested?.Invoke(this, b); };
            return item;
        }

        private static string Preview(string answer)
        {
            string s = string.Join(" ", (answer ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Replace("**", "").Replace("`", "").Trim();
            return s.Length <= 120 ? s : s.Substring(0, 119) + "…";
        }
    }
}
