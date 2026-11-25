using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace DatvTx.LinuxUi
{
    /// <summary>The OpenTuner M0CKE / Windows DATV TX colours, for Avalonia.</summary>
    internal static class LTheme
    {
        public static readonly Color Bg = Color.FromRgb(7, 17, 29);
        public static readonly Color Surface = Color.FromRgb(13, 28, 45);
        public static readonly Color Surface2 = Color.FromRgb(18, 38, 60);
        public static readonly Color Border = Color.FromRgb(37, 67, 94);
        public static readonly Color Accent = Color.FromRgb(28, 139, 253);
        public static readonly Color Green = Color.FromRgb(40, 222, 126);
        public static readonly Color GreenFill = Color.FromRgb(17, 91, 59);
        public static readonly Color GreenBorder = Color.FromRgb(38, 140, 86);
        public static readonly Color Grey = Color.FromRgb(75, 84, 95);
        public static readonly Color GreyBorder = Color.FromRgb(108, 119, 131);
        public static readonly Color Red = Color.FromRgb(214, 48, 49);
        public static readonly Color RedBorder = Color.FromRgb(255, 99, 99);
        public static readonly Color Amber = Color.FromRgb(240, 170, 40);
        public static readonly Color Text = Color.FromRgb(242, 247, 252);
        public static readonly Color Muted = Color.FromRgb(142, 165, 190);
        public static readonly Color LogBg = Color.FromRgb(9, 24, 39);

        public static IBrush B(Color c) { return new SolidColorBrush(c); }

        public static readonly FontFamily Mono = new FontFamily("DejaVu Sans Mono, Liberation Mono, Noto Sans Mono, monospace");

        /// <summary>Fluent dark palette tuned to the navy theme, so stock controls match.</summary>
        public static ColorPaletteResources Palette()
        {
            return new ColorPaletteResources
            {
                Accent = Accent,
                RegionColor = Bg,
                AltHigh = Bg,
                AltMediumHigh = Surface,
                AltMedium = Surface,
                AltMediumLow = Surface2,
                AltLow = Surface2,
                BaseHigh = Text,
                BaseMediumHigh = Color.FromRgb(214, 226, 238),
                BaseMedium = Muted,
                BaseMediumLow = Border,
                BaseLow = Color.FromRgb(28, 52, 78),
                ChromeAltLow = Text,
                ChromeBlackHigh = Color.FromRgb(0, 0, 0),
                ChromeBlackLow = Text,
                ChromeBlackMedium = Bg,
                ChromeBlackMediumLow = Surface,
                ChromeDisabledHigh = Border,
                ChromeDisabledLow = Muted,
                ChromeGray = Muted,
                ChromeHigh = Border,
                ChromeLow = Surface,
                ChromeMedium = Surface2,
                ChromeMediumLow = Surface,
                ChromeWhite = Text,
                ListLow = Surface2,
                ListMedium = Color.FromRgb(28, 52, 78),
                ErrorText = RedBorder,
            };
        }

        public static void AddStyles(Styles styles)
        {
            // Disabled controls: same face, greyed text (the palette's disabled colours handle the rest).
        }

        public static TextBlock Label(string text, double size = 12, bool semi = false, Color? colour = null)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = semi ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = B(colour ?? Muted),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
        }

        public static Border Card(Control child, Thickness? padding = null)
        {
            return new Border
            {
                Background = B(Surface),
                CornerRadius = new CornerRadius(4),
                Padding = padding ?? new Thickness(14, 10, 14, 12),
                Child = child
            };
        }

        public static Button Button(string text, bool accent = false)
        {
            var b = new Button
            {
                Content = text,
                FontWeight = FontWeight.SemiBold,
                FontSize = 12,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Foreground = B(Text),
                Padding = new Thickness(8, 4)
            };
            Paint(b, accent ? Accent : Surface2, accent ? Accent : Border);
            return b;
        }

        public static void Paint(Button b, Color fill, Color border)
        {
            b.Background = B(fill);
            b.BorderBrush = B(border);
        }

        public static ComboBox Combo()
        {
            return new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Background = B(Surface2),
                BorderBrush = B(Border),
                Foreground = B(Text),
                FontSize = 13,
                MinHeight = 28,
                MaxDropDownHeight = 360
            };
        }

        public static EditCombo Edit() { return new EditCombo { MinHeight = 28, VerticalAlignment = VerticalAlignment.Center }; }

        public static TextBox TextBox()
        {
            return new TextBox
            {
                Background = B(Surface2),
                BorderBrush = B(Border),
                Foreground = B(Text),
                FontSize = 13,
                MinHeight = 28,
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        public static CheckBox Check(string text)
        {
            return new CheckBox { Content = text, Foreground = B(Text), FontSize = 13 };
        }

        /// <summary>A two-column label / control grid, like the Windows field grids.</summary>
        public sealed class FieldGrid
        {
            public readonly Grid Grid;
            private int _row;
            public FieldGrid(double labelWidth)
            {
                Grid = new Grid { ColumnDefinitions = new ColumnDefinitions(labelWidth.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",*") };
            }
            public void Add(string label, Control c, double height = 34)
            {
                Grid.RowDefinitions.Add(new RowDefinition(height, GridUnitType.Pixel));
                var l = Label(label, 12.5);
                Grid.SetRow(l, _row); Grid.SetColumn(l, 0);
                c.Margin = new Thickness(0, 3);
                Grid.SetRow(c, _row); Grid.SetColumn(c, 1);
                Grid.Children.Add(l);
                Grid.Children.Add(c);
                _row++;
            }
        }

        public static WindowIcon AppIcon()
        {
            try
            {
                var s = typeof(LTheme).Assembly.GetManifestResourceStream("DatvTx.ico");
                return s != null ? new WindowIcon(s) : null;
            }
            catch { return null; }
        }
    }
}
