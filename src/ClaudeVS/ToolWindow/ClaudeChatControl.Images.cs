using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeVS.Commands;
using System.Collections.Generic;
using ClaudeVS.Core.Chat;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Context;
using ClaudeVS.Core.Tokens;
using ClaudeVS.Diagnostics;
using ClaudeVS.Options;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.ToolWindow
{
    /// <summary>Images pasted or uploaded into the prompt, and the strip that shows them.</summary>
    public sealed partial class ClaudeChatControl
    {
        private const int MaxImages = 10;
        private readonly List<ImageAttachment> _images = new List<ImageAttachment>();
        private readonly WrapPanel _imageStrip = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };

        /// <summary>Ctrl+V / Shift+Insert with an image (or image files) on the clipboard attaches it instead of pasting text.</summary>
        private bool TryPasteImage(KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            bool paste = (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0) || (e.Key == Key.Insert && (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            if (!paste || !ImageAttachments.ClipboardHasImage()) return false;
            AddImages(ImageAttachments.FromClipboard);
            return true;
        }

        /// <summary>Encodes images (scaled to the API's 1568 px limit) and shows them above the input box.</summary>
        private void AddImages(Func<List<ImageAttachment>> load)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            List<ImageAttachment> added;
            try { added = load(); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException || ex is System.IO.FileFormatException)
            {
                AppendError("[image] ", ex.Message);
                return;
            }
            foreach (var img in added)
            {
                if (_images.Count >= MaxImages) { Append($"At most {MaxImages} images per prompt; the rest were not attached."); break; }
                _images.Add(img);
            }
            RefreshImageStrip();
            UpdateAttachments(focus: true);
        }

        private void RefreshImageStrip()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _imageStrip.Children.Clear();
            foreach (var img in _images)
            {
                var picture = new Image { Source = img.Preview, MaxHeight = 56, MaxWidth = 96, Stretch = Stretch.Uniform };
                var remove = ChatTheme.IconButton(ChatTheme.IconClose, "Remove image");
                remove.FontSize = 8;
                remove.Width = 16;
                remove.Height = 16;
                remove.Background = ChatTheme.Pressed;
                remove.HorizontalAlignment = HorizontalAlignment.Right;
                remove.VerticalAlignment = VerticalAlignment.Top;
                var item = img;
                remove.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); _images.Remove(item); RefreshImageStrip(); UpdateAttachments(focus: true); };
                var cell = new Grid { Margin = new Thickness(0, 0, 6, 0) };
                cell.Children.Add(new Border { Child = picture, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = ChatTheme.Hover });
                cell.Children.Add(remove);
                cell.ToolTip = $"{img.Image.Name} · {img.Image.Width}×{img.Image.Height} · ~{TokenMeter.Format(img.Image.EstimatedTokens)} tokens";
                _imageStrip.Children.Add(cell);
            }
            _imageStrip.Visibility = _images.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string ImageSummary(IReadOnlyCollection<ImageAttachment> images) =>
            $"🖼 {images.Count} image{(images.Count == 1 ? "" : "s")} (~{TokenMeter.Format(images.Sum(i => i.Image.EstimatedTokens))} tok)";
    }
}
