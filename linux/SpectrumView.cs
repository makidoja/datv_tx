using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace DatvTx.LinuxUi
{
    /// <summary>
    /// QO-100 wideband spectrum from the BATC live FFT feed with the band plan underneath
    /// (same feed, levels and channel plan as the Windows QoSpectrum). Click a channel to pick it.
    /// </summary>
    public sealed class SpectrumView : Control
    {
        public const double StartMHz = 10490.5;
        public const double SpanMHz = 9.0;
        public const double UplinkOffsetMHz = 8089.5;
        private const string FeedUrl = "wss://eshail.batc.org.uk/wb/fft";
        private const string FeedProtocol = "fft_m0dtslivetune";
        private const double UnitsPerDb = 3276.8;

        public event Action<double, int> ChannelPicked;
        public event Action<double> LevelUpdated;

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
        private const double RowHeight = 9;
        private const int Rows = 4;

        public SpectrumView()
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            _slots.Add(new Slot { Centre = 10491.50, Sr = 1500, Row = 3 });
            foreach (double f in new[] { 10493.25, 10494.75, 10496.25 }) _slots.Add(new Slot { Centre = f, Sr = 1000, Row = 3 });
            for (double f = 10492.75; f <= 10496.75 + 1e-6; f += 0.5) _slots.Add(new Slot { Centre = f, Sr = 500, Row = 2 });
            for (double f = 10492.75; f <= 10499.25 + 1e-6; f += 0.5) _slots.Add(new Slot { Centre = f, Sr = 333, Row = 1 });
            for (double f = 10492.75; f <= 10499.25 + 1e-6; f += 0.25) _slots.Add(new Slot { Centre = f, Sr = 125, Row = 0 });
        }

        public void SetTransmit(double uplinkMHz, int srKs, bool onAir)
        {
            double down = uplinkMHz + UplinkOffsetMHz;
            if (down == _txDownlinkMHz && srKs == _txSr && onAir == _onAir) return;
            _txDownlinkMHz = down; _txSr = srKs; _onAir = onAir;
            InvalidateVisual();
        }

        /// <summary>Synthetic trace for screenshots: beacon, a few signals and noise.</summary>
        public void ShowDemo()
        {
            var d = new ushort[918];
            var rnd = new Random(7);
            for (int i = 0; i < d.Length; i++)
            {
                double f = StartMHz + i * SpanMHz / (d.Length - 1);
                double v = 13000 + rnd.Next(0, 1500);
                if (Math.Abs(f - 10491.5) < 0.85) v = 33000 + rnd.Next(0, 600);
                if (Math.Abs(f - 10494.25) < 0.29) v = 30000 + rnd.Next(0, 600);
                if (Math.Abs(f - 10496.25) < 0.28) v = 29000 + rnd.Next(0, 600);
                if (Math.Abs(f - 10499.0) < 0.1) v = 24000 + rnd.Next(0, 600);
                d[i] = (ushort)v;
            }
            lock (_sync) { _fft = d; _lastFrame = DateTime.Now.AddHours(1); }
            Measure(d);
            InvalidateVisual();
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
                                for (int i = 0; i < data.Length; i++) data[i] = (ushort)(frame[2 * i] | (frame[2 * i + 1] << 8));
                                lock (_sync) { _fft = data; _lastFrame = DateTime.Now; }
                                Measure(data);
                                Redraw();
                            }
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { SetStatus("BATC spectrum unavailable (" + ex.Message + ") - retrying..."); }
                }
                try { await Task.Delay(5000, token); } catch { return; }
            }
        }

        private void SetStatus(string s) { _status = s; Redraw(); }
        private void Redraw() { Dispatcher.UIThread.Post(InvalidateVisual); }

        private static int Bin(ushort[] d, double mhz)
        {
            int i = (int)Math.Round((mhz - StartMHz) / SpanMHz * (d.Length - 1));
            return Math.Max(0, Math.Min(d.Length - 1, i));
        }

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
            double beacon = TopLevel(d, 10491.5, 0.6);
            var sorted = (ushort[])d.Clone();
            Array.Sort(sorted);
            double noise = sorted[sorted.Length / 5];
            double dbb = double.NaN, tx = double.NaN;
            if (!double.IsNaN(_txDownlinkMHz) && _txSr > 0)
            {
                double width = Math.Max(_txSr / 1000.0 * 0.5, 3 * SpanMHz / d.Length);
                tx = TopLevel(d, _txDownlinkMHz, width);
                bool beaconOk = beacon > noise + 3 * UnitsPerDb;
                bool txVisible = tx > noise + 1.0 * UnitsPerDb;
                if (beaconOk && txVisible) dbb = (tx - beacon) / UnitsPerDb;
            }
            if (double.IsNaN(dbb)) _dbb = double.NaN;
            else _dbb = double.IsNaN(_dbb) ? dbb : _dbb * 0.6 + dbb * 0.4;
            _beaconLevel = beacon;
            _txLevel = tx;
            var h = LevelUpdated;
            double v = _dbb;
            if (h != null) try { h(v); } catch { }
        }

        // ------------------------------------------------------------------ geometry

        private Rect PlotRect
        {
            get
            {
                double w = Bounds.Width, h = Bounds.Height;
                return new Rect(10, 6, Math.Max(20, w - 20), Math.Max(30, h - 12 - Rows * (RowHeight + 2) - 14));
            }
        }

        private Rect RowRect(int row)
        {
            var p = PlotRect;
            double top = p.Bottom + 4 + (Rows - 1 - row) * (RowHeight + 2);
            return new Rect(p.Left, top, p.Width, RowHeight);
        }

        private double X(double mhz) { var p = PlotRect; return p.Left + (mhz - StartMHz) / SpanMHz * p.Width; }
        private double Mhz(double x) { var p = PlotRect; return StartMHz + (x - p.Left) / p.Width * SpanMHz; }

        private Rect SlotRect(Slot s)
        {
            var row = RowRect(s.Row);
            double bw = s.Sr / 1000.0 * 1.2;
            double pitch = s.Row == 0 ? 0.25 : (s.Row == 1 || s.Row == 2) ? 0.5 : bw;
            double w = Math.Min(bw, pitch) - 0.04;
            double x0 = X(s.Centre - w / 2), x1 = X(s.Centre + w / 2);
            return new Rect(x0, row.Top, Math.Max(2, x1 - x0), row.Height);
        }

        private Slot? SlotAt(Point pt)
        {
            foreach (var s in _slots) if (SlotRect(s).Contains(pt)) return s;
            return null;
        }

        // ------------------------------------------------------------------ mouse

        protected override void OnPointerMoved(PointerEventArgs e) { _mouse = e.GetPosition(this); InvalidateVisual(); base.OnPointerMoved(e); }
        protected override void OnPointerExited(PointerEventArgs e) { _mouse = new Point(-1, -1); InvalidateVisual(); base.OnPointerExited(e); }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var pt = e.GetPosition(this);
            var slot = SlotAt(pt);
            if (slot.HasValue)
            {
                if (slot.Value.Centre < 10492) return;
                Raise(slot.Value.Centre, slot.Value.Sr);
                return;
            }
            if (!PlotRect.Contains(pt)) return;
            double f = Math.Round((Mhz(pt.X) - 10492.75) / 0.25) * 0.25 + 10492.75;
            if (f < 10492.75 || f > 10499.25) return;
            Raise(f, 0);
        }

        private void Raise(double downlink, int sr) { var h = ChannelPicked; if (h != null) h(downlink, sr); }

        // ------------------------------------------------------------------ paint

        private static FormattedText T(string s, double size, bool semi, Color c) { return TrafficLightView.Text(s, size, semi, c); }

        public override void Render(DrawingContext g)
        {
            var plot = PlotRect;
            g.FillRectangle(LTheme.B(LTheme.Surface), new Rect(Bounds.Size));
            g.FillRectangle(new SolidColorBrush(Color.FromRgb(4, 10, 18)), plot);

            ushort[] fft; DateTime last;
            lock (_sync) { fft = _fft; last = _lastFrame; }

            var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 142, 165, 190)), 1, DashStyle.Dot);
            for (int m = 10491; m <= 10499; m++)
            {
                double x = X(m);
                g.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
                g.DrawText(T(m.ToString(CultureInfo.InvariantCulture), 10, false, LTheme.Muted), new Point(x - 14, Bounds.Height - 14));
            }

            if (fft != null && (DateTime.Now - last).TotalSeconds < 10) DrawTrace(g, plot, fft);
            else g.DrawText(T(_status ?? "BATC wideband spectrum - waiting for data...", 12, true, LTheme.Muted), new Point(plot.Left + 10, plot.Top + 8));

            if (fft != null && !double.IsNaN(_beaconLevel))
            {
                double yb = LevelY(plot, _beaconLevel);
                g.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1, DashStyle.Dash), new Point(plot.Left, yb), new Point(plot.Right, yb));
                g.DrawText(T("beacon 0 dBb", 10, false, Color.FromArgb(220, 255, 255, 255)), new Point(X(10492.3), yb - 14));
            }
            if (!double.IsNaN(_dbb))
            {
                Color lc = DbbColour(_dbb);
                string txt = (_onAir ? "TX  " : "channel  ") + (_dbb >= 0 ? "+" : "") + _dbb.ToString("0.0", CultureInfo.InvariantCulture) + " dBb";
                var ft = T(txt, 17, true, lc);
                var box = new Rect(plot.Right - ft.Width - 14, plot.Top + 4, ft.Width + 10, ft.Height + 2);
                g.FillRectangle(new SolidColorBrush(Color.FromArgb(200, 13, 28, 45)), box);
                g.DrawText(ft, new Point(box.X + 5, box.Y + 1));
                if (!double.IsNaN(_txLevel))
                {
                    double y = LevelY(plot, _txLevel);
                    double bw = Math.Max(0.03, _txSr / 1000.0 * 1.2);
                    g.DrawLine(new Pen(new SolidColorBrush(lc), 1.5), new Point(X(_txDownlinkMHz - bw / 2), y), new Point(X(_txDownlinkMHz + bw / 2), y));
                }
            }

            if (!double.IsNaN(_txDownlinkMHz))
            {
                double bw = Math.Max(0.03, _txSr / 1000.0 * 1.2);
                double x0 = X(_txDownlinkMHz - bw / 2), x1 = X(_txDownlinkMHz + bw / 2);
                Color c = _onAir ? Color.FromRgb(255, 70, 70) : LTheme.Amber;
                g.FillRectangle(new SolidColorBrush(Color.FromArgb(50, c.R, c.G, c.B)), new Rect(x0, plot.Top, Math.Max(2, x1 - x0), plot.Height));
                g.DrawLine(new Pen(new SolidColorBrush(c), 1.5), new Point(X(_txDownlinkMHz), plot.Top), new Point(X(_txDownlinkMHz), plot.Bottom));
                var lbl = T((_onAir ? "TX " : "") + _txDownlinkMHz.ToString("0.00", CultureInfo.InvariantCulture), 11, true, c);
                double lx = x1 + 3;
                if (lx + lbl.Width > plot.Right) lx = x0 - lbl.Width - 3;
                g.DrawText(lbl, new Point(lx, plot.Bottom - 18));
            }

            var hover = SlotAt(_mouse);
            foreach (var s in _slots)
            {
                var r = SlotRect(s);
                bool isTx = !double.IsNaN(_txDownlinkMHz) && Math.Abs(s.Centre - _txDownlinkMHz) < 0.01 && s.Sr == _txSr;
                bool isHover = hover.HasValue && hover.Value.Centre == s.Centre && hover.Value.Row == s.Row;
                Color fill = s.Centre < 10492 ? Color.FromRgb(70, 90, 110)
                    : isTx ? (_onAir ? LTheme.Red : LTheme.Amber)
                    : isHover ? LTheme.Accent : Color.FromRgb(95, 105, 170);
                g.FillRectangle(new SolidColorBrush(fill), r);
            }

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
                {
                    var ft = T(text, 11.5, true, LTheme.Text);
                    double x = Math.Min(Math.Max(plot.Left, _mouse.X + 12), plot.Right - ft.Width - 4);
                    var box = new Rect(x, plot.Top + 4, ft.Width + 8, ft.Height + 4);
                    g.FillRectangle(new SolidColorBrush(Color.FromArgb(220, 13, 28, 45)), box);
                    g.DrawText(ft, new Point(box.X + 4, box.Y + 2));
                }
            }
        }

        public static Color DbbColour(double dbb)
        {
            if (dbb >= 0) return Color.FromRgb(255, 80, 80);
            if (dbb > -1) return LTheme.Amber;
            return LTheme.Green;
        }

        private static double LevelY(Rect plot, double level)
        {
            double n = Math.Max(0, Math.Min(1, level / 65535.0 * 1.55));
            return plot.Bottom - n * plot.Height;
        }

        private static void DrawTrace(DrawingContext g, Rect plot, ushort[] data)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(plot.Left, plot.Bottom), true);
                for (int i = 0; i < data.Length; i++)
                {
                    double x = plot.Left + i / (double)Math.Max(1, data.Length - 1) * plot.Width;
                    double n = Math.Max(0, Math.Min(1, data[i] / 65535.0 * 1.55));
                    ctx.LineTo(new Point(x, plot.Bottom - n * plot.Height));
                }
                ctx.LineTo(new Point(plot.Right, plot.Bottom));
                ctx.EndFigure(true);
            }
            var fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(200, 170, 40, 160), 0), new GradientStop(Color.FromArgb(200, 30, 40, 230), 1) }
            };
            g.DrawGeometry(fill, null, geo);
        }
    }
}
