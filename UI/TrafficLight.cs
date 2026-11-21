using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DatvTx.UI
{
    public enum TxLight { Off, Ready, Standby, OnAir }

    /// <summary>
    /// Transmission status lamps: green READY (radio ready, idle), amber STANDBY (stream running,
    /// carrier muted), red ON AIR. A warning makes the amber lamp flash (e.g. encoder too slow).
    /// </summary>
    public sealed class TrafficLight : Control
    {
        private TxLight _state = TxLight.Off;
        private bool _warn, _blink;
        private string _warnText = "";

        public TrafficLight()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Height = 64;
        }

        public TxLight State { get { return _state; } set { if (_state != value) { _state = value; Invalidate(); } } }

        public void SetWarning(bool warn, string text)
        {
            if (_warn == warn && _warnText == text) return;
            _warn = warn; _warnText = text ?? "";
            Invalidate();
        }

        /// <summary>Called by the form's 500 ms timer to flash the warning lamp.</summary>
        public void Tick()
        {
            if (!_warn) return;
            _blink = !_blink;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            int d = 24;
            int gap = 9;
            int housingW = 3 * d + 4 * gap;
            var housing = new Rectangle(0, 2, housingW, d + 12);
            using (var path = Rounded(housing, 10))
            using (var b = new SolidBrush(Color.FromArgb(4, 10, 18)))
            using (var p = new Pen(Theme.Border))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }

            bool amber = _state == TxLight.Standby || (_warn && _blink);
            DrawLamp(g, new Rectangle(gap, 8, d, d), Color.FromArgb(255, 60, 60), _state == TxLight.OnAir);
            DrawLamp(g, new Rectangle(2 * gap + d, 8, d, d), Color.FromArgb(255, 184, 40), amber);
            DrawLamp(g, new Rectangle(3 * gap + 2 * d, 8, d, d), Color.FromArgb(40, 222, 126), _state == TxLight.Ready);

            string caption;
            Color colour;
            switch (_state)
            {
                case TxLight.OnAir: caption = "ON AIR"; colour = Color.FromArgb(255, 99, 99); break;
                case TxLight.Standby: caption = "STANDBY"; colour = Theme.Amber; break;
                case TxLight.Ready: caption = "READY"; colour = Theme.Green; break;
                default: caption = "NOT READY"; colour = Theme.Muted; break;
            }
            int tx = housingW + 10;
            using (var f = Theme.Semi(11f))
                TextRenderer.DrawText(g, caption, f, new Rectangle(tx, 2, Width - tx, d + 12), colour,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (_warn)
                using (var f = Theme.Ui(8.5f))
                    TextRenderer.DrawText(g, _warnText, f, new Rectangle(0, d + 18, Width, Height - d - 18), Theme.Amber,
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
        }

        private static void DrawLamp(Graphics g, Rectangle r, Color c, bool lit)
        {
            if (lit)
            {
                using (var glow = new SolidBrush(Color.FromArgb(60, c)))
                    g.FillEllipse(glow, Rectangle.Inflate(r, 4, 4));
                using (var b = new LinearGradientBrush(r, ControlPaint.Light(c, 0.6f), c, 60f))
                    g.FillEllipse(b, r);
            }
            else
            {
                using (var b = new SolidBrush(Color.FromArgb(40, c)))
                    g.FillEllipse(b, r);
            }
            using (var p = new Pen(Color.FromArgb(25, 40, 55), 1.5f))
                g.DrawEllipse(p, r);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
