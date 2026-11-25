using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DatvTx.LinuxUi
{
    /// <summary>
    /// An editable drop-down (type any value or pick one from the list), like a Windows
    /// ComboBox with DropDownStyle.DropDown.
    /// </summary>
    public sealed class EditCombo : UserControl
    {
        public readonly TextBox Box;
        private readonly Button _arrow;
        private readonly Popup _popup;
        private readonly ListBox _list;
        private bool _filling;
        private int _selected = -1;

        public readonly List<object> Items = new List<object>();
        public event EventHandler SelectionChanged;
        public event EventHandler TextChanged;
        public event EventHandler DropDownClosed;

        public EditCombo()
        {
            Box = new TextBox
            {
                Background = LTheme.B(LTheme.Surface2),
                BorderBrush = LTheme.B(LTheme.Border),
                Foreground = LTheme.B(LTheme.Text),
                FontSize = 13,
                MinHeight = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(3, 0, 0, 3)
            };
            Box.TextChanged += (s, e) =>
            {
                if (_filling) return;
                _selected = Items.FindIndex(i => Convert.ToString(i, CultureInfo.InvariantCulture) == Box.Text);
                var h = TextChanged; if (h != null) h(this, EventArgs.Empty);
            };
            _arrow = new Button
            {
                Content = "▾",
                Width = 26,
                Padding = new Thickness(0),
                FontSize = 13,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = LTheme.B(LTheme.Surface2),
                BorderBrush = LTheme.B(LTheme.Border),
                BorderThickness = new Thickness(0, 1, 1, 1),
                Foreground = LTheme.B(LTheme.Text),
                CornerRadius = new CornerRadius(0, 3, 3, 0),
                Focusable = false
            };
            _list = new ListBox
            {
                Background = LTheme.B(LTheme.Surface2),
                Foreground = LTheme.B(LTheme.Text),
                MaxHeight = 360,
                FontSize = 13
            };
            _popup = new Popup
            {
                Child = new Border { BorderBrush = LTheme.B(LTheme.Border), BorderThickness = new Thickness(1), Child = _list },
                Placement = PlacementMode.Bottom,
                IsLightDismissEnabled = true,
                PlacementTarget = this
            };
            _popup.Closed += (s, e) => { var h = DropDownClosed; if (h != null) h(this, EventArgs.Empty); };
            _arrow.Click += (s, e) => Open();
            _list.SelectionChanged += (s, e) =>
            {
                if (_filling || _list.SelectedIndex < 0) return;
                int i = _list.SelectedIndex;
                _popup.IsOpen = false;
                SelectedIndex = i;
            };

            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(_arrow, 1);
            g.Children.Add(Box);
            g.Children.Add(_arrow);
            g.Children.Add(_popup);
            Content = g;
        }

        public string Text
        {
            get { return Box.Text ?? ""; }
            set { Box.Text = value ?? ""; }
        }

        public bool IsDropDownOpen { get { return _popup.IsOpen; } }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                _selected = value >= 0 && value < Items.Count ? value : -1;
                if (_selected >= 0)
                {
                    _filling = true;
                    Box.Text = Convert.ToString(Items[_selected], CultureInfo.InvariantCulture);
                    _filling = false;
                    var t = TextChanged; if (t != null) t(this, EventArgs.Empty);
                }
                var h = SelectionChanged; if (h != null) h(this, EventArgs.Empty);
            }
        }

        public object SelectedItem { get { return _selected >= 0 && _selected < Items.Count ? Items[_selected] : null; } }

        private void Open()
        {
            _filling = true;
            _list.Items.Clear();
            foreach (var i in Items) _list.Items.Add(Convert.ToString(i, CultureInfo.InvariantCulture));
            _list.SelectedIndex = -1;
            _filling = false;
            _popup.Width = Math.Max(80, Bounds.Width);
            _popup.IsOpen = true;
        }
    }
}
