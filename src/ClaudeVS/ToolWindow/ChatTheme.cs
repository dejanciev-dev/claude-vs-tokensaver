using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVS.ToolWindow
{
    /// <summary>
    /// Shared look for the chat, modelled on the Claude Code panel in VS Code: flat icon buttons, rounded
    /// message bubbles and input box, the environment UI font. Colors come from VS theme resources (or are
    /// translucent overlays), so Light, Dark and Blue all work.
    /// </summary>
    internal static class ChatTheme
    {
        public static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");

        // Segoe MDL2 / Fluent icon code points.
        public const string IconAdd = "";
        public const string IconHistory = "";
        public const string IconStop = "";
        public const string IconCompact = ""; // "back to window": shrinking arrows
        public const string IconSend = "";    // arrow up
        public const string IconUpload = "";
        public const string IconGlobe = "";
        public const string IconDocument = "";
        public const string IconEdit = "";
        public const string IconPerson = "";
        public const string IconBolt = "";
        public const string IconMessage = "";
        public const string IconSettings = "";
        public const string IconWarning = "";

        public const string IconClose = "";
        public const string IconDelete = "";
        public const string IconStar = "";      // outline: not bookmarked
        public const string IconStarFill = "";  // filled: bookmarked
        public const string IconBookmarks = "";
        public const string IconLightbulb = "";
        public const string IconCopy = "";
        public const string IconCheck = "";
        public const string IconRewind = "";     // undo arrow
        public const string IconRefresh = "";
        public const string IconFork = "";       // branch-like "switch" arrows
        public const string IconOpenRing = "";   // hollow ring: open somewhere else
        public const string IconFolder = "";
        public const string IconFilter = "";
        public const string IconArchive = "";    // box
        public const string IconUnarchive = "";  // undo

        public static string ModeGlyph(Core.Cli.PermissionModeChoice m)
        {
            switch (m)
            {
                case Core.Cli.PermissionModeChoice.AcceptEdits: return IconEdit;
                case Core.Cli.PermissionModeChoice.Plan: return IconDocument;
                case Core.Cli.PermissionModeChoice.Auto: return IconBolt;
                case Core.Cli.PermissionModeChoice.BypassPermissions: return IconWarning;
                default: return IconPerson;
            }
        }

        /// <summary>A small rounded "chip" button (model, mode) showing an optional icon and a label.</summary>
        public static Button Chip(string tooltip)
        {
            var b = new Button
            {
                ToolTip = tooltip,
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(2, 0, 2, 0),
                Template = FlatTemplate(new CornerRadius(4)),
                Background = Brushes.Transparent,
                FontSize = 11.5,
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Text(b);
            return b;
        }

        public static void SetChip(Button chip, string glyph, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(glyph))
                row.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 12, Margin = new Thickness(0, 1, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            chip.Content = row;
            System.Windows.Automation.AutomationProperties.SetName(chip, text);
        }

        // Translucent overlays read well on light and dark backgrounds.
        public static readonly Brush Subtle = Freeze(Color.FromArgb(0x1E, 0x80, 0x80, 0x80));
        public static readonly Brush Hover = Freeze(Color.FromArgb(0x33, 0x80, 0x80, 0x80));
        public static readonly Brush Pressed = Freeze(Color.FromArgb(0x4D, 0x80, 0x80, 0x80));
        public static readonly Brush Error = Freeze(Color.FromRgb(0xE0, 0x6C, 0x5C));

        /// <summary>Use the IDE's UI font (Segoe UI by default) and size.</summary>
        public static void UseEnvironmentFont(FrameworkElement e)
        {
            e.SetResourceReference(Control.FontFamilyProperty, VsFonts.EnvironmentFontFamilyKey);
            e.SetResourceReference(Control.FontSizeProperty, VsFonts.EnvironmentFontSizeKey);
        }

        public static void Text(FrameworkElement e) => e.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

        public static void Muted(FrameworkElement e) => e.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);

        /// <summary>A borderless icon button with a rounded hover background, like VS Code's title-bar actions.</summary>
        public static Button IconButton(string glyph, string tooltip)
        {
            var b = new Button
            {
                Content = glyph,
                ToolTip = tooltip,
                FontFamily = IconFont,
                FontSize = 14,
                Width = 28,
                Height = 26,
                Margin = new Thickness(1, 0, 1, 0),
                Template = FlatTemplate(new CornerRadius(4)),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            Text(b);
            System.Windows.Automation.AutomationProperties.SetName(b, tooltip);
            return b;
        }

        /// <summary>The round send button inside the input box.</summary>
        public static Button SendButton()
        {
            var b = new Button
            {
                Content = IconSend,
                ToolTip = "Send (Enter)",
                FontFamily = IconFont,
                FontSize = 13,
                Width = 26,
                Height = 26,
                Template = FlatTemplate(new CornerRadius(13), filled: true),
                Foreground = Brushes.White,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.SystemHighlightBrushKey);
            System.Windows.Automation.AutomationProperties.SetName(b, "Send");
            return b;
        }

        /// <summary>A flat text button (Allow, Deny, Copy, Insert…) with a subtle background.</summary>
        public static Button TextButton(string text, string tooltip = null)
        {
            var b = new Button
            {
                Content = text,
                ToolTip = string.IsNullOrEmpty(tooltip) ? null : tooltip,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 4, 0),
                Template = FlatTemplate(new CornerRadius(3)),
                Background = Subtle,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            Text(b);
            return b;
        }

        /// <summary>A compact combo box using VS's own themed style.</summary>
        public static ComboBox Combo(double width, string tooltip)
        {
            var c = new ComboBox { Width = width, Margin = new Thickness(0, 0, 4, 0), ToolTip = tooltip, FontSize = 11.5 };
            c.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ComboBoxStyleKey);
            return c;
        }

        /// <summary>
        /// Button template: a rounded border showing the button's Background, lighter on hover, darker when pressed,
        /// faded when disabled. <paramref name="filled"/> keeps the background (send button) and dims it on hover.
        /// </summary>
        private static ControlTemplate FlatTemplate(CornerRadius radius, bool filled = false)
        {
            var border = new FrameworkElementFactory(typeof(Border), "Bd");
            border.SetValue(Border.CornerRadiusProperty, radius);
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);

            var t = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
            if (filled)
            {
                hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85));
                pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.7));
            }
            else
            {
                hover.Setters.Add(new Setter(Border.BackgroundProperty, Hover, "Bd"));
                pressed.Setters.Add(new Setter(Border.BackgroundProperty, Pressed, "Bd"));
            }
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
            t.Triggers.Add(hover);
            t.Triggers.Add(pressed);
            t.Triggers.Add(disabled);
            return t;
        }

        private static Brush Freeze(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
