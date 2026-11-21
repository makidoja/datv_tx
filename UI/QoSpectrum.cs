using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DatvTx.UI
{
    /// <summary>
    /// QO-100 wideband spectrum from the BATC live FFT feed (as used by m0dts' QO-100 WB Live Tune
    /// and OpenTuner), with the wideband channel plan underneath. Clicking a channel or the trace
    /// picks the uplink frequency (downlink - 8089.5 MHz) and, for channel blocks, the symbol rate.
    /// </summary>
    public sealed class QoSpectrum : Control
    {
        public const double StartMHz = 10490.5;
        public const double SpanMHz = 9.0;
        public const double UplinkOffsetMHz = 8089.5;
        private const string FeedUrl = "wss://eshail.batc.org.uk/wb/fft";
        private const string FeedProtocol = "fft_m0dtslivetune";

        /// <summary>Raised with the downlink centre (MHz) and symbol rate (KS, 0 = keep current).</summary>
        public event Action<double, int> ChannelPicked;

        /// <summary>
        /// Level of the transmit channel relative to the beacon in dB (dBb), or NaN when there is no
        /// beacon reference or nothing above the noise on the channel. Raised on the feed thread per frame.
        /// </summary>
        public event Action<double> LevelUpdated;

        // BATC feed scaling: 3276.8 FFT units per dB (as used by the BATC/OpenTuner detector).
        private const double UnitsPerDb = 3276.8;
        private double _beaconLevel = double.NaN, _txLevel = double.NaN, _dbb = double.NaN;

        private struct Slot { public double Centre; public int Sr; public int Row; }
        private readonly List<Slot> _slots = new List<Slot>();

        private readonly object _sync = new object();
        private ushort[] _fft;
        private DateTime _lastFrame = DateTime.MinValue;
        private CancellationTokenSource _cts;
        private string _status = "BATC wideband spectrum - connecting...";
        private double _txDownlinkMHz = double.NaN;
        private int _txSr;
        private Point _mouse = new Point(-1, -1);
        private bool _onAir;

        private const int RowHeight = 9;
        private const int Rows = 4;

        public QoSpectrum()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Cursor = Cursors.Hand;

            // BATC QO-100 WB band plan V3: row 0 very narrow (125/66/33 kS, 250 kHz spacing),
            // row 1 narrow 333/250 kS (ch 1-14), row 2 narrow 500 kS (ch 1-9 only), row 3 wide 1 MS + beacon.
            _slots.Add(new Slot { Centre = 10491.50, Sr = 1500, Row = 3 }); // beacon
            foreach (double f in new[] { 10493.25, 10494.75, 10496.25 })
                _slots.Add(new Slot { Centre = f, Sr = 1000, Row = 3 });
            for (double f = 10492.75; f <= 10496.75 + 1e-6; f += 0.5)
                _slots.Add(new Slot { Centre = f, Sr = 500, Row = 2 });
            for (double f = 10492.75; f <= 10499.25 + 1e-6; f += 0.5)
                _slots.Add(new Slot { Centre = f, Sr = 333, Row = 1 });
            for (double f = 10492.75; f <= 10499.25 + 1e-6; f += 0.25)
                _slots.Add(new Slot { Centre = f, Sr = 125, Row = 0 });
        }

        /// <summary>Marks the current transmit channel (uplink MHz) on the plot.</summary>
        public void SetTransmit(double uplinkMHz, int srKs, bool onAir)
        {
            _txDownlinkMHz = uplinkMHz + UplinkOffsetMHz;
            _txSr = srKs;
            _onAir = onAir;
            Invalidate();
        }

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(() => RunAsync(token));
        }

        public void Stop()
        {
            if (_cts != null) _cts.Cancel();
            _cts = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Stop();
            base.Dispose(disposing);
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                using (var ws = new ClientWebSocket())
                {
                    try
                    {
                        ws.Options.AddSubProtocol(FeedProtocol);
                        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                        await ws.ConnectAsync(new Uri(FeedUrl), token);
                        SetStatus(null);
                        var buffer = new byte[65536];
                        var frame = new List<byte>(4096);
                        while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                        {
                            frame.Clear();
                            WebSocketReceiveResult r;
                            do
                            {
                                r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                                if (r.MessageType == WebSocketMessageType.Close) break;
                                for (int i = 0; i < r.Count; i++) frame.Add(buffer[i]);
                            } while (!r.EndOfMessage);
                            if (r.MessageType == WebSocketMessageType.Binary && frame.Count >= 16)
                            {
                                var data = new ushort[frame.Count / 2];
                                for (int i = 0; i < data.Length; i++)
                                    data[i] = (ushort)(frame[2 * i] | (frame[2 * i + 1] << 8));
                                lock (_sync) { _fft = data; _lastFrame = DateTime.Now; }
                                Measure(data);
                                SafeInvalidate();
                            }
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { SetStatus("BATC spectrum unavailable (" + ex.Message + ") - retrying..."); }
                }
                try { await Task.Delay(5000, token); } catch { return; }
            }
        }

        private void SetStatus(string s) { _status = s; SafeInvalidate(); }

        public double Dbb { get { return _dbb; } }

        private static int Bin(ushort[] d, double mhz)
        {
            int i = (int)Math.Round((mhz - StartMHz) / SpanMHz * (d.Length - 1));
            return Math.Max(0, Math.Min(d.Length - 1, i));
        }

        /// <summary>Mean FFT level over the central part of a signal (the flat top).</summary>
        private static double TopLevel(ushort[] d, double centreMHz, double widthMHz)
        {
            int a = Bin(d, centreMHz - widthMHz / 2), b = Bin(d, centreMHz + widthMHz / 2);
            if (b < a) { int t = a; a = b; b = t; }
            double sum = 0; int n = 0;
            for (int i = a; i <= b; i++) { sum += d[i]; n++; }
            return n > 0 ? sum / n : double.NaN;
        }

        private void Measure(ushort[] d)
        {
            // Beacon: 1.5 MS at 10491.5 MHz; use the middle 0.6 MHz of its flat top.
            double beacon = TopLevel(d, 10491.5, 0.6);

            // Noise floor: 20th percentile of the whole span.
            var sorted = (ushort[])d.Clone();
            Array.Sort(sorted);
            double noise = sorted[sorted.Length / 5];

            double dbb = double.NaN;
            double tx = double.NaN;
            if (!double.IsNaN(_txDownlinkMHz) && _txSr > 0)
            {
                // middle half of the occupied bandwidth (at least ~3 bins)
                double width = Math.Max(_txSr / 1000.0 * 0.5, 3 * SpanMHz / d.Length);
                tx = TopLevel(d, _txDownlinkMHz, width);
                bool beaconOk = beacon > noise + 3 * UnitsPerDb;
                bool txVisible = tx > noise + 1.0 * UnitsPerDb;
                if (beaconOk && txVisible) dbb = (tx - beacon) / UnitsPerDb;
            }

            // light smoothing so the readout is steady while adjusting power
            if (double.IsNaN(dbb)) _dbb = double.NaN;
            else _dbb = double.IsNaN(_dbb) ? dbb : _dbb * 0.6 + dbb * 0.4;
            _beaconLevel = beacon;
            _txLevel = tx;

            var h = LevelUpdated;
            double v = _dbb;
            if (h != null)
                try { h(v); } catch { } // called on the feed thread; the form marshals to the UI thread
        }

        private void SafeInvalidate()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke((MethodInvoker)Invalidate); else Invalidate();
            }
            catch { }
        }

        // ------------------------------------------------------------------ geometry

        private Rectangle PlotRect { get { return new Rectangle(10, 6, Math.Max(20, Width - 20), Math.Max(30, Height - 12 - Rows * (RowHeight + 2) - 14)); } }

        private Rectangle RowRect(int row)
        {
            var p = PlotRect;
            int top = p.Bottom + 4 + (Rows - 1 - row) * (RowHeight + 2);
            return new Rectangle(p.Left, top, p.Width, RowHeight);
        }

        private float X(double mhz) { var p = PlotRect; return p.Left + (float)((mhz - StartMHz) / SpanMHz * p.Width); }
        private double Mhz(int x) { var p = PlotRect; return StartMHz + (x - p.Left) / (double)p.Width * SpanMHz; }

        private RectangleF SlotRect(Slot s)
        {
            var row = RowRect(s.Row);
            double bw = s.Sr / 1000.0 * 1.2;          // occupied bandwidth
            double pitch = s.Row == 0 ? 0.25 : (s.Row == 1 || s.Row == 2) ? 0.5 : bw;
            double w = Math.Min(bw, pitch) - 0.04;
            float x0 = X(s.Centre - w / 2), x1 = X(s.Centre + w / 2);
            return new RectangleF(x0, row.Top, Math.Max(2, x1 - x0), row.Height);
        }

        private Slot? SlotAt(Point pt)
        {
            foreach (var s in _slots)
                if (SlotRect(s).Contains(pt)) return s;
            return null;
        }

        // ------------------------------------------------------------------ mouse

        protected override void OnMouseMove(MouseEventArgs e) { _mouse = e.Location; Invalidate(); base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { _mouse = new Point(-1, -1); Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            var slot = SlotAt(e.Location);
            if (slot.HasValue)
            {
                if (slot.Value.Centre < 10492) return; // the beacon is not a transmit channel
                Raise(slot.Value.Centre, slot.Value.Sr);
                return;
            }
            if (!PlotRect.Contains(e.Location)) return;
            double f = Math.Round((Mhz(e.X) - 10492.75) / 0.25) * 0.25 + 10492.75;
            if (f < 10492.75 || f > 10499.25) return;
            Raise(f, 0);
        }

        private void Raise(double downlink, int sr)
        {
            var h = ChannelPicked;
            if (h != null) h(downlink, sr);
        }

        // ------------------------------------------------------------------ paint

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var plot = PlotRect;
            using (var bg = new SolidBrush(Color.FromArgb(4, 10, 18))) g.FillRectangle(bg, plot);

            ushort[] fft;
            DateTime last;
            lock (_sync) { fft = _fft; last = _lastFrame; }

            // grid every 1 MHz
            using (var grid = new Pen(Color.FromArgb(40, 142, 165, 190)) { DashStyle = DashStyle.Dot })
            using (var f = Theme.Ui(7.5f))
                for (int m = 10491; m <= 10499; m++)
                {
                    float x = X(m);
                    g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                    TextRenderer.DrawText(g, m.ToString(CultureInfo.InvariantCulture), f, new Point((int)x - 14, Height - 14), Theme.Muted);
                }

            if (fft != null && (DateTime.Now - last).TotalSeconds < 10)
                DrawTrace(g, plot, fft);
            else
                using (var f = Theme.Semi(9f))
                    TextRenderer.DrawText(g, _status ?? "BATC wideband spectrum - waiting for data...", f, new Point(plot.Left + 10, plot.Top + 8), Theme.Muted);

            // beacon (0 dBb) reference line and the transmit level
            if (fft != null && !double.IsNaN(_beaconLevel))
            {
                float yb = LevelY(plot, _beaconLevel);
                using (var p = new Pen(Color.FromArgb(200, 255, 255, 255), 1f) { DashStyle = DashStyle.Dash })
                    g.DrawLine(p, plot.Left, yb, plot.Right, yb);
                using (var f = Theme.Ui(7.5f))
                    TextRenderer.DrawText(g, "beacon 0 dBb", f, new Point(plot.Right - 72, (int)yb - 14), Color.FromArgb(220, 255, 255, 255));
            }
            if (!double.IsNaN(_dbb))
            {
                Color lc = DbbColour(_dbb);
                string txt = (_onAir ? "TX  " : "channel  ") + (_dbb >= 0 ? "+" : "") + _dbb.ToString("0.0", CultureInfo.InvariantCulture) + " dBb";
                using (var f = Theme.Semi(13f))
                {
                    var sz = TextRenderer.MeasureText(txt, f);
                    var box = new Rectangle(plot.Right - sz.Width - 14, plot.Top + 4, sz.Width + 10, sz.Height + 2);
                    using (var b = new SolidBrush(Color.FromArgb(200, 13, 28, 45))) g.FillRectangle(b, box);
                    TextRenderer.DrawText(g, txt, f, new Point(box.X + 5, box.Y + 1), lc);
                }
                if (!double.IsNaN(_txLevel))
                    using (var p = new Pen(lc, 1.5f))
                    {
                        float y = LevelY(plot, _txLevel);
                        double bw = Math.Max(0.03, _txSr / 1000.0 * 1.2);
                        g.DrawLine(p, X(_txDownlinkMHz - bw / 2), y, X(_txDownlinkMHz + bw / 2), y);
                    }
            }

            // transmit marker
            if (!double.IsNaN(_txDownlinkMHz))
            {
                double bw = Math.Max(0.03, _txSr / 1000.0 * 1.2);
                float x0 = X(_txDownlinkMHz - bw / 2), x1 = X(_txDownlinkMHz + bw / 2);
                Color c = _onAir ? Color.FromArgb(255, 70, 70) : Theme.Amber;
                using (var b = new SolidBrush(Color.FromArgb(50, c))) g.FillRectangle(b, x0, plot.Top, Math.Max(2, x1 - x0), plot.Height);
                using (var p = new Pen(c, 1.5f)) g.DrawLine(p, X(_txDownlinkMHz), plot.Top, X(_txDownlinkMHz), plot.Bottom);
                using (var f = Theme.Semi(8f))
                    TextRenderer.DrawText(g, (_onAir ? "TX " : "") + _txDownlinkMHz.ToString("0.00", CultureInfo.InvariantCulture), f, new Point((int)x1 + 3, plot.Top + 3), c);
            }

            // channel rows
            var hover = SlotAt(_mouse);
            foreach (var s in _slots)
            {
                var r = SlotRect(s);
                bool isTx = !double.IsNaN(_txDownlinkMHz) && Math.Abs(s.Centre - _txDownlinkMHz) < 0.01 && s.Sr == _txSr;
                bool isHover = hover.HasValue && hover.Value.Centre == s.Centre && hover.Value.Row == s.Row;
                Color fill = s.Centre < 10492 ? Color.FromArgb(70, 90, 110)
                    : isTx ? (_onAir ? Color.FromArgb(214, 48, 49) : Theme.Amber)
                    : isHover ? Theme.Accent : Color.FromArgb(95, 105, 170);
                using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
            }

            // hover readout
            if (_mouse.X >= 0)
            {
                string text = null;
                if (hover.HasValue && hover.Value.Centre >= 10492)
                    text = string.Format(CultureInfo.InvariantCulture, "{0:0.00} ↓  {1:0.00} ↑  {2} KS", hover.Value.Centre, hover.Value.Centre - UplinkOffsetMHz, hover.Value.Sr);
                else if (plot.Contains(_mouse))
                {
                    double f = Math.Round((Mhz(_mouse.X) - 10492.75) / 0.25) * 0.25 + 10492.75;
                    if (f >= 10492.75 && f <= 10499.25)
                        text = string.Format(CultureInfo.InvariantCulture, "{0:0.00} ↓  {1:0.00} ↑", f, f - UplinkOffsetMHz);
                }
                if (text != null)
                    using (var f = Theme.Semi(8.5f))
                    {
                        var size = TextRenderer.MeasureText(text, f);
                        int x = Math.Min(Math.Max(plot.Left, _mouse.X + 12), plot.Right - size.Width - 4);
                        var box = new Rectangle(x, plot.Top + 4, size.Width + 8, size.Height + 4);
                        using (var b = new SolidBrush(Color.FromArgb(220, 13, 28, 45))) g.FillRectangle(b, box);
                        TextRenderer.DrawText(g, text, f, new Point(box.X + 4, box.Y + 2), Theme.Text);
                    }
            }
        }

        public static Color DbbColour(double dbb)
        {
            if (dbb >= 0) return Color.FromArgb(255, 80, 80);
            if (dbb > -1) return Theme.Amber;
            return Theme.Green;
        }

        private static float LevelY(Rectangle plot, double level)
        {
            float n = (float)Math.Max(0, Math.Min(1, level / 65535.0 * 1.55));
            return plot.Bottom - n * plot.Height;
        }

        private void DrawTrace(Graphics g, Rectangle plot, ushort[] data)
        {
            var pts = new PointF[data.Length + 2];
            pts[0] = new PointF(plot.Left, plot.Bottom);
            for (int i = 0; i < data.Length; i++)
            {
                float x = plot.Left + i / (float)Math.Max(1, data.Length - 1) * plot.Width;
                float n = Math.Max(0f, Math.Min(1f, data[i] / 65535f * 1.55f));
                pts[i + 1] = new PointF(x, plot.Bottom - n * plot.Height);
            }
            pts[pts.Length - 1] = new PointF(plot.Right, plot.Bottom);
            using (var fill = new LinearGradientBrush(new Point(0, plot.Top), new Point(0, plot.Bottom), Color.FromArgb(200, 170, 40, 160), Color.FromArgb(200, 30, 40, 230)))
                g.FillPolygon(fill, pts);
        }
    }
}
