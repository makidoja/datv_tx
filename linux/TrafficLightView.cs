using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DatvTx.LinuxUi
{
    public enum TxLight { Off, Ready, Standby, OnAir }

    /// <summary>Red ON AIR / amber STANDBY / green READY lamps; the amber lamp flashes on a warning.</summary>
    public sealed class TrafficLightView : Control
    {
        private TxLight _state = TxLight.Off;
        private bool _warn, _blink;
        private string _warnText = "";

        public TrafficLightView() { Height = 92; }

        public TxLight State { get { return _state; } set { if (_state != value) { _state = value; InvalidateVisual(); } } }

        public void SetWarning(bool warn, string text)
        {
            if (_warn == warn && _warnText == (text ?? "")) return;
            _warn = warn; _warnText = text ?? "";
            InvalidateVisual();
        }

        public void Tick()
        {
            if (!_warn) return;
            _blink = !_blink;
            InvalidateVisual();
        }

        public override void Render(DrawingContext g)
        {
            double d = 24, gap = 9;
            double housingW = 3 * d + 4 * gap;
            var housing = new Rect(0, 2, housingW, d + 12);
            g.DrawRectangle(new SolidColorBrush(Color.FromRgb(4, 10, 18)), new Pen(LTheme.B(LTheme.Border), 1), housing, 10, 10);

            bool amber = _state == TxLight.Standby || (_warn && _blink);
            Lamp(g, new Rect(gap, 8, d, d), Color.FromRgb(255, 60, 60), _state == TxLight.OnAir);
            Lamp(g, new Rect(2 * gap + d, 8, d, d), Color.FromRgb(255, 184, 40), amber);
            Lamp(g, new Rect(3 * gap + 2 * d, 8, d, d), Color.FromRgb(40, 222, 126), _state == TxLight.Ready);

            string caption; Color colour;
            switch (_state)
            {
                case TxLight.OnAir: caption = "ON AIR"; colour = LTheme.RedBorder; break;
                case TxLight.Standby: caption = "STANDBY"; colour = LTheme.Amber; break;
                case TxLight.Ready: caption = "READY"; colour = LTheme.Green; break;
                default: caption = "NOT READY"; colour = LTheme.Muted; break;
            }
            var ft = Text(caption, 15, true, colour);
            g.DrawText(ft, new Point(housingW + 10, 2 + (d + 12 - ft.Height) / 2));

            if (_warn && _warnText.Length > 0)
            {
                var w = Text(_warnText, 11.5, false, LTheme.Amber);
                w.MaxTextWidth = Math.Max(50, Bounds.Width);
                w.MaxTextHeight = Math.Max(10, Bounds.Height - d - 16);
                w.Trimming = TextTrimming.CharacterEllipsis;
                g.DrawText(w, new Point(0, d + 16));
            }
        }

        private static void Lamp(DrawingContext g, Rect r, Color c, bool lit)
        {
            if (lit)
            {
                g.DrawEllipse(new SolidColorBrush(Color.FromArgb(60, c.R, c.G, c.B)), null, r.Inflate(4));
                var light = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.6), (byte)(c.G + (255 - c.G) * 0.6), (byte)(c.B + (255 - c.B) * 0.6));
                var brush = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.2, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0.8, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(light, 0), new GradientStop(c, 1) }
                };
                g.DrawEllipse(brush, null, r);
            }
            else g.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, c.R, c.G, c.B)), null, r);
            g.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(25, 40, 55)), 1.5), r);
        }

        /// <summary>The app font (Inter, bundled), so custom-drawn text matches the controls.</summary>
        internal static readonly FontFamily AppFont = new FontFamily("fonts:Inter#Inter, $Default");

        internal static FormattedText Text(string s, double size, bool semi, Color c)
        {
            return new FormattedText(s ?? "", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(AppFont, FontStyle.Normal, semi ? FontWeight.SemiBold : FontWeight.Normal), size, new SolidColorBrush(c));
        }
    }
}
