using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DatvTx.Core;
using DatvTx.Hardware;

namespace DatvTx.UI
{
    public sealed class MainForm : Form
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const string ProductTitle = "DATV TX (M0CKE)";

        private AppSettings _s;
        private Transmitter _tx;
        private readonly FfmpegRunner _ffmpeg = new FfmpegRunner();
        private Process _player;
        private bool _running, _busy, _ptt, _loading;
        private TxParams _active;
        private readonly ConcurrentDictionary<string, string> _telemetry = new ConcurrentDictionary<string, string>();
        private EncoderStats _lastStats;
        private LinkLabel _lnkUpdate;
        private Label _lblCredit;
        private DateTime? _slowSince;
        private readonly Timer _uiTimer = new Timer { Interval = 500 };
        private readonly Timer _gainTimer = new Timer { Interval = 200 };
        private long _lastUnderruns = -1;

        // header
        private Label _lblHardware, _lblHelp;
        private Button _btnConnect, _btnSettings, _btnSpectrum;

        // spectrum
        private QoSpectrum _spectrum;
        private Panel _spectrumCard;
        private TableLayoutPanel _root, _body;
        private Control _header, _tabsCard, _logCard, _txCard;
        private Button _btnCompact, _btnTop;
        private bool _compact;

        // tabs
        private readonly List<Button> _tabButtons = new List<Button>();
        private readonly List<Control> _tabPages = new List<Control>();

        // source & encoder
        private ComboBox _cmbInput, _cmbVideo, _cmbAudio, _cmbCodec, _cmbPreset, _cmbSize, _cmbFps, _cmbChannels, _cmbAbr;
        private TextBox _txtFile;
        private Button _btnRefresh, _btnBrowse;
        private Panel _fileRow, _videoRow;

        // modulator
        private ComboBox _cmbStandard, _cmbConst, _cmbSr, _cmbFec, _cmbFrame, _cmbRollOff, _cmbFreq;
        private CheckBox _chkPilots;
        private NumericUpDown _numDGain;
        private Label _lblTs, _lblVideo;

        // memories
        private ListBox _lstMem;
        private TextBox _txtMemName;

        // transmit column
        private double _dbb = double.NaN;
        private Label _lblDbb;
        private Label _lblDownlink, _lblSummary, _lblStats, _lblStatus, _lblPower;
        private TrafficLight _light;
        private TrackBar _trkPower;
        private Button _btnStart, _btnPtt, _btnMonitor;

        // receive

        private TextBox _log;
        private readonly List<Control> _lockWhileRunning = new List<Control>();

        private static int S(int px) { return Theme.S(px); }

        public MainForm()
        {
            _s = AppSettings.Load();
            using (var g = CreateGraphics()) Theme.Scale = Math.Max(1f, g.DpiX / 96f);

            Text = ProductTitle + "  v" + UpdateChecker.DisplayVersion(Application.ProductVersion);
            Icon = Theme.AppIcon();
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9f);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(S(960), S(820));
            Size = new Size(Math.Max(S(960), _s.NormalWidth), Math.Max(S(820), _s.NormalHeight));
            AutoScaleMode = AutoScaleMode.None;

            BuildUi();
            CreateTransmitter();
            ApplyParams(_s.LastRun ?? new TxParams());
            RefreshMemories();
            ShowSpectrum(_s.ShowSpectrum);
            if (_s.CompactMode) SetCompact(true);
            ApplyTopMost();

            _ffmpeg.Log += l => UI(() =>
            {
                AppendLog(l);
                if (l.IndexOf("required nvenc API version", StringComparison.OrdinalIgnoreCase) >= 0)
                    AppendLog(">> Your NVIDIA driver is older than this FFmpeg needs. Update the driver, or set Settings → FFmpeg folder to another FFmpeg build.");
                else if (l.IndexOf("Cannot load nvcuda", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("No capable devices found", StringComparison.OrdinalIgnoreCase) >= 0)
                    AppendLog(">> No usable NVIDIA GPU found - choose a CPU or other GPU codec.");
                else if (l.IndexOf("Could not run graph", StringComparison.OrdinalIgnoreCase) >= 0)
                    AppendLog(">> Windows could not open the camera. Usually another program has it (OBS, Teams, Zoom, the Camera app or a browser tab): close it, or in OBS use Start Virtual Camera and choose OBS Virtual Camera here. Unplugging and replugging the camera also helps.");
                else if (l.IndexOf("not supported", StringComparison.OrdinalIgnoreCase) >= 0 && (l.IndexOf("YUV", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("pixel format", StringComparison.OrdinalIgnoreCase) >= 0))
                    AppendLog(">> The camera's picture format isn't accepted by this encoder - try a CPU codec (libx265/libx264).");
                else if (l.IndexOf("amf", StringComparison.OrdinalIgnoreCase) >= 0 && l.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0)
                    AppendLog(">> AMD encoder failed - check the AMD graphics driver is installed, or choose a CPU codec.");
            });
            _ffmpeg.Stats += st => { _lastStats = st; };
            _ffmpeg.Exited += code => UI(() => OnEncoderExited(code));

            _uiTimer.Tick += (s, e) => RefreshLive();
            _uiTimer.Start();
            _gainTimer.Tick += async (s, e) => { _gainTimer.Stop(); await GainCommitted(); };

            Load += async (s, e) => { Theme.DarkTitleBar(this); await RefreshDevicesAsync(); await CheckForUpdateAsync(); };
            FormClosing += OnClosing;
        }

        // ================================================================== layout

        private void BuildUi()
        {
            _root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(S(12)), BackColor = Theme.Bg };
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(70)));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(200)));
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(120)));
            Controls.Add(_root);

            _header = BuildHeader();
            _root.Controls.Add(_header, 0, 0);

            _spectrumCard = Theme.Card();
            _spectrumCard.Dock = DockStyle.Fill;
            _spectrumCard.Margin = new Padding(S(0), S(10), S(0), S(0));
            _spectrumCard.Padding = new Padding(S(6), S(4), S(6), S(4));
            _spectrum = new QoSpectrum { Dock = DockStyle.Fill };
            _spectrum.ChannelPicked += (down, sr) => BeginInvoke((Action)(async () => await ChannelPicked(down, sr)));
            _spectrum.LevelUpdated += v => UI(() => { _dbb = v; ShowDbb(); });
            var specTitle = new Label { Text = "QO-100 WIDEBAND  •  BATC live spectrum  •  click a channel to set the TX frequency", Dock = DockStyle.Top, Height = S(20), ForeColor = Theme.Muted, Font = Theme.Semi(8.5f), Padding = new Padding(S(6), S(0), S(0), S(0)) };
            _spectrumCard.Controls.Add(_spectrum);
            _spectrumCard.Controls.Add(specTitle);
            _root.Controls.Add(_spectrumCard, 0, 1);

            _body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(S(0), S(10), S(0), S(10)) };
            _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(300)));
            _root.Controls.Add(_body, 0, 2);

            _tabsCard = BuildTabs();
            _tabsCard.Margin = new Padding(S(0), S(0), S(6), S(0));
            _body.Controls.Add(_tabsCard, 0, 0);
            _txCard = BuildTransmitColumn();
            _txCard.Margin = new Padding(S(6), S(0), S(0), S(0));
            _body.Controls.Add(_txCard, 1, 0);

            var logCard = Theme.Card();
            _logCard = logCard;
            logCard.Dock = DockStyle.Fill;
            logCard.Padding = new Padding(S(10), S(6), S(10), S(8));
            _log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = Theme.LogBg,
                ForeColor = Theme.Muted,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.75f),
                WordWrap = true
            };
            logCard.Controls.Add(_log);
            _root.Controls.Add(logCard, 0, 3);
        }

        private void ShowSpectrum(bool show)
        {
            _s.ShowSpectrum = show;
            _spectrumCard.Visible = show && !_compact;
            _root.RowStyles[1].Height = show && !_compact ? S(200) : 0;
            _btnSpectrum.Text = show ? "HIDE SPECTRUM" : "SPECTRUM";
            if (show) _spectrum.Start(); else _spectrum.Stop();
        }

        /// <summary>Always on top: the ON TOP button, or the compact-window setting.</summary>
        private void ApplyTopMost()
        {
            bool top = _s.AlwaysOnTop || (_compact && _s.CompactOnTop);
            TopMost = top;
            if (_btnTop != null)
                Theme.Paint(_btnTop, _s.AlwaysOnTop ? Theme.Accent : Theme.Surface2, _s.AlwaysOnTop ? Theme.Accent : Theme.Border);
        }

        /// <summary>Compact mode: only the transmit panel, in a small window.</summary>
        private void SetCompact(bool compact)
        {
            if (compact == _compact) return;
            SuspendLayout();
            if (compact)
            {
                if (WindowState == FormWindowState.Normal) { _s.NormalWidth = Width; _s.NormalHeight = Height; }
            }
            else if (WindowState == FormWindowState.Normal) { _s.CompactWidth = Width; _s.CompactHeight = Height; }

            _compact = compact;
            _s.CompactMode = compact;
            _header.Visible = !compact;
            _tabsCard.Visible = !compact;
            _logCard.Visible = !compact;
            _root.RowStyles[0].Height = compact ? 0 : S(70);
            _root.RowStyles[3].Height = compact ? 0 : S(120);
            _root.Padding = compact ? new Padding(S(4)) : new Padding(S(12));
            _body.Margin = compact ? new Padding(S(0)) : new Padding(S(0), S(10), S(0), S(10));
            _body.ColumnStyles[0] = compact ? new ColumnStyle(SizeType.Absolute, S(0)) : new ColumnStyle(SizeType.Percent, 100);
            _body.ColumnStyles[1] = compact ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.Absolute, S(300));
            _txCard.Margin = compact ? new Padding(S(0)) : new Padding(S(6), S(0), S(0), S(0));
            ShowSpectrum(_s.ShowSpectrum); // hides the spectrum card but keeps the feed (dBb) running
            _btnCompact.Text = compact ? "FULL VIEW" : "COMPACT";
            ApplyTopMost();

            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            MinimumSize = compact ? new Size(S(300), S(560)) : new Size(S(960), S(820));
            Size = compact ? new Size(Math.Max(S(300), _s.CompactWidth), Math.Max(S(560), _s.CompactHeight))
                           : new Size(Math.Max(S(960), _s.NormalWidth), Math.Max(S(820), _s.NormalHeight));
            ResumeLayout(true);
            try { _s.Save(); } catch { }
        }

        private Panel BuildHeader()
        {
            var p = Theme.Card();
            p.Dock = DockStyle.Fill;
            p.Margin = new Padding(S(0));

            var title = new Label { Text = "◉  " + ProductTitle, AutoSize = true, Location = new Point(S(16), S(8)), ForeColor = Theme.Text, Font = Theme.Semi(14f) };
            var credit = _lblCredit = new Label { Text = "v" + UpdateChecker.DisplayVersion(Application.ProductVersion) + "  •  based on DATV-NotSoEasy by DL5OCD", AutoSize = true, Location = new Point(S(20), S(42)), ForeColor = Theme.Muted, Font = Theme.Ui(8.5f) };
            _lblHardware = new Label { AutoSize = true, Location = new Point(S(330), S(12)), ForeColor = Theme.Text, Font = Theme.Semi(10f) };
            _lblHelp = new Label { AutoSize = false, Location = new Point(S(330), S(34)), Size = new Size(S(400), S(32)), ForeColor = Theme.Muted, Font = Theme.Ui(8.5f) };

            _btnSettings = Theme.Button("SETTINGS");
            _btnSettings.Size = new Size(S(100), S(38));
            _btnSettings.Click += (s, e) => OpenSettings();

            _btnConnect = Theme.Button("CONNECT", true);
            _btnConnect.Size = new Size(S(130), S(38));
            _btnConnect.Click += async (s, e) => await ToggleConnectionAsync();

            _btnSpectrum = Theme.Button("SPECTRUM");
            _btnSpectrum.Size = new Size(S(124), S(38));
            _btnSpectrum.Click += (s, e) => ShowSpectrum(!_s.ShowSpectrum);

            _lnkUpdate = new LinkLabel
            {
                AutoSize = true, Visible = false, Font = Theme.Semi(8.5f),
                LinkColor = Theme.Amber, ActiveLinkColor = Theme.Text, VisitedLinkColor = Theme.Amber, LinkBehavior = LinkBehavior.HoverUnderline
            };
            _lnkUpdate.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo((string)_lnkUpdate.Tag ?? UpdateChecker.ReleasesPage) { UseShellExecute = true }); } catch { }
            };
            credit.SizeChanged += (s, e) => _lnkUpdate.Location = new Point(credit.Right + S(6), credit.Top);
            _lnkUpdate.Location = new Point(credit.Right + S(6), credit.Top);
            p.Controls.AddRange(new Control[] { title, credit, _lnkUpdate, _lblHardware, _lblHelp, _btnSpectrum, _btnConnect, _btnSettings });
            p.Resize += (s, e) =>
            {
                _btnSettings.Location = new Point(p.Width - _btnSettings.Width - S(14), S(15));
                _btnConnect.Location = new Point(_btnSettings.Left - _btnConnect.Width - S(8), S(15));
                _btnSpectrum.Location = new Point(_btnConnect.Left - _btnSpectrum.Width - S(8), S(15));
                _lblHelp.Width = Math.Max(S(150), _btnSpectrum.Left - _lblHelp.Left - S(12));
            };
            return p;
        }

        // ------------------------------------------------------------------ tabs

        private Control BuildTabs()
        {
            var card = Theme.Card();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(S(12), S(8), S(12), S(10));

            var strip = new FlowLayoutPanel { Dock = DockStyle.Top, Height = S(40), BackColor = Theme.Surface, WrapContents = false, Padding = new Padding(S(0), S(0), S(0), S(4)) };
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(S(0), S(8), S(0), S(0)) };

            AddTab(strip, host, "DVB", BuildDvbPage());
            AddTab(strip, host, "SOURCE", BuildSourcePage());
            AddTab(strip, host, "MEMORIES", BuildMemoriesPage());
            AddTab(strip, host, "STATUS", BuildStatusPage());

            card.Controls.Add(host);
            card.Controls.Add(strip);
            SelectTab(0);
            return card;
        }

        private void AddTab(FlowLayoutPanel strip, Panel host, string name, Control page)
        {
            int index = _tabButtons.Count;
            var b = Theme.Button(name);
            b.Size = new Size(S(104), S(32));
            b.Margin = new Padding(S(0), S(0), S(6), S(0));
            b.Click += (s, e) => SelectTab(index);
            strip.Controls.Add(b);
            _tabButtons.Add(b);

            page.Dock = DockStyle.Fill;
            page.Visible = false;
            host.Controls.Add(page);
            _tabPages.Add(page);
        }

        private void SelectTab(int index)
        {
            for (int i = 0; i < _tabPages.Count; i++)
            {
                bool on = i == index;
                _tabPages[i].Visible = on;
                Theme.Paint(_tabButtons[i], on ? Theme.Accent : Theme.Surface2, on ? Theme.Accent : Theme.Border);
            }
        }

        private static Label GroupTitle(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Top, Height = S(24), ForeColor = Theme.Text, Font = Theme.Semi(9.5f), TextAlign = ContentAlignment.MiddleLeft };
        }

        private Control BuildDvbPage()
        {
            var page = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, BackColor = Theme.Surface };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(58)));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _lblTs = BitrateBox("Target TS bitrate");
            _lblVideo = BitrateBox("Video bitrate");
            page.Controls.Add(_lblTs.Parent, 0, 0);
            page.Controls.Add(_lblVideo.Parent, 1, 0);

            // ---- MODE
            var mode = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = new Padding(S(0), S(0), S(10), S(0)) };
            var grid = Theme.FieldGrid(110);

            _cmbStandard = Theme.Combo();
            _cmbStandard.Items.AddRange(new object[] { "DVB-S2", "DVB-S" });
            _cmbStandard.SelectedIndexChanged += (s, e) => { if (!_loading) RefillFec(); };
            Theme.AddRow(grid, "Standard", _cmbStandard);

            _cmbConst = Theme.Combo();
            _cmbConst.Items.AddRange(new object[] { "QPSK", "8PSK", "16APSK", "32APSK" });
            _cmbConst.SelectedIndexChanged += (s, e) => { if (!_loading) RefillFec(); _numDGain.Enabled = Constellation() == "qpsk"; };
            Theme.AddRow(grid, "Constellation", _cmbConst);

            _cmbSr = Theme.Combo(true);
            foreach (int sr in DvbCalc.SymbolRates) _cmbSr.Items.Add(sr.ToString(Inv));
            Theme.AddRow(grid, "Symbol rate (KS)", _cmbSr);

            _cmbFec = Theme.Combo();
            Theme.AddRow(grid, "FEC", _cmbFec);

            _cmbFrame = Theme.Combo();
            _cmbFrame.Items.AddRange(new object[] { "Long (64800)", "Short (16200)" });
            Theme.AddRow(grid, "FEC frame", _cmbFrame);

            _cmbRollOff = Theme.Combo();
            _cmbRollOff.Items.AddRange(new object[] { "0.20", "0.15" });
            _cmbRollOff.SelectedIndexChanged += async (s, e) => { if (CanLive()) await Live(() => _tx.SetRollOffAsync(SelectedRollOff()), "Roll-off " + _cmbRollOff.Text); };
            Theme.AddRow(grid, "Roll-off", _cmbRollOff);

            _chkPilots = Theme.Check("Pilot symbols");
            Theme.AddRow(grid, "", _chkPilots);

            _numDGain = Theme.Number(-20, 3, 0.25m, 2);
            _numDGain.ValueChanged += async (s, e) => { if (CanLive()) await Live(() => _tx.SetDigitalGainAsync((double)_numDGain.Value), "Digital gain " + _numDGain.Value.ToString("0.00", Inv) + " dB"); };
            Theme.AddRow(grid, "Digital gain (dB)", _numDGain);

            mode.Controls.Add(grid);
            mode.Controls.Add(GroupTitle("MODE"));
            page.Controls.Add(mode, 0, 1);

            // ---- VIDEO / AUDIO
            var av = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = new Padding(S(10), S(0), S(0), S(0)) };
            var g2 = Theme.FieldGrid(100);

            _cmbCodec = Theme.Combo();
            _cmbCodec.MaxDropDownItems = 14;
            Theme.AddRow(g2, "Codec", _cmbCodec);

            _cmbPreset = Theme.Combo();
            _cmbPreset.Items.AddRange(Codecs.Presets.Cast<object>().ToArray());
            Theme.AddRow(g2, "Speed preset", _cmbPreset);

            _cmbSize = Theme.Combo(true);
            _cmbSize.Items.AddRange(new object[] { "640x360", "960x540", "1280x720", "1920x1080", "2560x1440" });
            Theme.AddRow(g2, "Image size", _cmbSize);

            _cmbFps = Theme.Combo(true);
            _cmbFps.Items.AddRange(new object[] { "10", "15", "20", "25", "30", "48", "50", "60" });
            Theme.AddRow(g2, "Frame rate", _cmbFps);

            _cmbChannels = Theme.Combo();
            _cmbChannels.Items.AddRange(new object[] { "Mono", "Stereo", "5.1", "7.1" });
            Theme.AddRow(g2, "Audio", _cmbChannels);

            _cmbAbr = Theme.Combo();
            _cmbAbr.Items.AddRange(new object[] { "8", "16", "32", "48", "64", "96", "128", "192", "256" });
            Theme.AddRow(g2, "Audio kbit/s", _cmbAbr);

            av.Controls.Add(g2);
            av.Controls.Add(GroupTitle("VIDEO / AUDIO"));
            page.Controls.Add(av, 1, 1);

            foreach (Control c in new Control[] { _cmbStandard, _cmbConst, _cmbSr, _cmbFec, _cmbFrame, _chkPilots, _cmbCodec, _cmbPreset, _cmbSize, _cmbFps, _cmbChannels, _cmbAbr })
                HookRecalc(c);
            _lockWhileRunning.AddRange(new Control[] { _cmbStandard, _cmbConst, _cmbSr, _cmbFec, _cmbFrame, _chkPilots, _cmbCodec, _cmbPreset, _cmbSize, _cmbFps, _cmbChannels, _cmbAbr });
            return page;
        }

        /// <summary>A titled read-only value box (DATV-Easy style). Returns the value label; its Parent is the box.</summary>
        private static Label BitrateBox(string title)
        {
            var box = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = new Padding(S(0), S(0), S(10), S(6)) };
            var t = new Label { Text = title, Dock = DockStyle.Top, Height = S(20), ForeColor = Theme.Muted, Font = Theme.Ui(8.5f) };
            var v = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Text, BackColor = Theme.Surface2, Font = Theme.Semi(12f), TextAlign = ContentAlignment.MiddleCenter };
            box.Controls.Add(v);
            box.Controls.Add(t);
            return v;
        }

        private Control BuildSourcePage()
        {
            var page = new Panel { BackColor = Theme.Surface };
            var grid = Theme.FieldGrid(120);

            _cmbInput = Theme.Combo();
            _cmbInput.Items.AddRange(new object[] { "Capture device (OBS Virtual Camera / camera)", "Network UDP", "Network RTMP", "Video file" });
            _cmbInput.SelectedIndexChanged += (s, e) => UpdateInputVisibility();
            Theme.AddRow(grid, "Input", _cmbInput);

            _cmbVideo = Theme.Combo(true);
            _btnRefresh = Theme.Button("↻");
            _btnRefresh.Width = S(34); _btnRefresh.Dock = DockStyle.Right; _btnRefresh.Margin = new Padding(S(0));
            _btnRefresh.Click += async (s, e) => await RefreshDevicesAsync();
            _videoRow = Inline(_cmbVideo, _btnRefresh);
            Theme.AddRow(grid, "Video device", _videoRow);

            _cmbAudio = Theme.Combo(true);
            Theme.AddRow(grid, "Audio device", _cmbAudio);

            _txtFile = Theme.TextBox();
            _btnBrowse = Theme.Button("…");
            _btnBrowse.Width = S(34); _btnBrowse.Dock = DockStyle.Right;
            _btnBrowse.Click += (s, e) => BrowseFile();
            _fileRow = Inline(_txtFile, _btnBrowse);
            Theme.AddRow(grid, "Video file", _fileRow);

            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = S(70),
                ForeColor = Theme.Muted,
                Font = Theme.Ui(8.5f),
                Padding = new Padding(S(0), S(10), S(0), S(0)),
                Text = "OBS: Tools → Start Virtual Camera, and send audio to a virtual cable (e.g. VB-Audio) or the OBS audio device.\n" +
                       "Network UDP / RTMP and the file loop are set in Settings → Input. Press ↻ after plugging in a device."
            };

            page.Controls.Add(hint);
            page.Controls.Add(grid);
            page.Controls.Add(GroupTitle("INPUT"));
            _lockWhileRunning.AddRange(new Control[] { _cmbInput, _cmbVideo, _cmbAudio, _btnRefresh, _txtFile, _btnBrowse });
            return page;
        }

        private Control BuildMemoriesPage()
        {
            var page = new Panel { BackColor = Theme.Surface };

            _lstMem = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Surface2,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None,
                Font = Theme.Ui(9.5f),
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = S(40),
                IntegralHeight = false
            };
            _lstMem.DrawItem += DrawMemory;
            _lstMem.DoubleClick += (s, e) => RecallMemory();

            var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = S(46), ColumnCount = 5, BackColor = Theme.Surface, Padding = new Padding(S(0), S(8), S(0), S(0)) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(96)));
            _txtMemName = Theme.TextBox();
            _txtMemName.Margin = new Padding(S(0), S(4), S(8), S(4));
            var recall = Theme.Button("RECALL", true); recall.Dock = DockStyle.Fill; recall.Margin = new Padding(S(0), S(2), S(6), S(2));
            var store = Theme.Button("STORE NEW"); store.Dock = DockStyle.Fill; store.Margin = new Padding(S(0), S(2), S(6), S(2));
            var over = Theme.Button("OVERWRITE"); over.Dock = DockStyle.Fill; over.Margin = new Padding(S(0), S(2), S(6), S(2));
            var del = Theme.Button("DELETE"); del.Dock = DockStyle.Fill; del.Margin = new Padding(S(0), S(2), S(0), S(2));
            recall.Click += (s, e) => RecallMemory();
            store.Click += (s, e) => StoreMemory(false);
            over.Click += (s, e) => StoreMemory(true);
            del.Click += (s, e) => DeleteMemory();
            bar.Controls.Add(_txtMemName, 0, 0);
            bar.Controls.Add(recall, 1, 0);
            bar.Controls.Add(store, 2, 0);
            bar.Controls.Add(over, 3, 0);
            bar.Controls.Add(del, 4, 0);
            _lstMem.SelectedIndexChanged += (s, e) =>
            {
                var m = _lstMem.SelectedItem as TxMemory;
                if (m != null) _txtMemName.Text = m.Name;
            };

            var hint = new Label { Text = "Name a set-up and press STORE NEW. Double-click or RECALL to load it (frequency, mode, SR, FEC, codec, power).", Dock = DockStyle.Top, Height = S(22), ForeColor = Theme.Muted, Font = Theme.Ui(8.5f) };
            page.Controls.Add(_lstMem);
            page.Controls.Add(bar);
            page.Controls.Add(hint);
            page.Controls.Add(GroupTitle("MEMORIES"));
            _lockWhileRunning.Add(recall);
            return page;
        }

        private void DrawMemory(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var m = (TxMemory)_lstMem.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (var b = new SolidBrush(sel ? Theme.Accent : (e.Index % 2 == 0 ? Theme.Surface2 : Color.FromArgb(15, 33, 52))))
                e.Graphics.FillRectangle(b, e.Bounds);
            using (var f = Theme.Semi(10f))
                TextRenderer.DrawText(e.Graphics, m.Name, f, new Point(e.Bounds.X + 8, e.Bounds.Y + 3), Theme.Text);
            var p = m.Params;
            string detail = p == null ? "" : string.Format(Inv, "{0:0.00} MHz   {1} {2}  {3} KS  FEC {4}   {5}  {6}  {7:0.##} fps   {8:0.##} dB",
                p.TxFrequencyMHz, p.DvbS2 ? "DVB-S2" : "DVB-S", p.Constellation.ToUpperInvariant(), p.SymbolRateKs, p.Fec, p.Codec, p.ImageSize, p.Fps, p.GainDb);
            using (var f = Theme.Ui(8.5f))
                TextRenderer.DrawText(e.Graphics, detail, f, new Point(e.Bounds.X + 8, e.Bounds.Y + 21), sel ? Theme.Text : Theme.Muted);
        }

        private Control BuildStatusPage()
        {
            var page = new Panel { BackColor = Theme.Surface };
            _lblStatus = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = new Font("Consolas", 9f), TextAlign = ContentAlignment.TopLeft, Text = "No radio status yet." };
            page.Controls.Add(_lblStatus);
            var logBtn = Theme.Button("OPEN LOG FOLDER");
            logBtn.Dock = DockStyle.Bottom; logBtn.Height = S(28);
            logBtn.Click += (o, e) =>
            {
                try { Directory.CreateDirectory(LogFolder); Process.Start("explorer.exe", "\"" + LogFolder + "\""); } catch { }
            };
            page.Controls.Add(logBtn);
            page.Controls.Add(GroupTitle("RADIO / MODULATOR STATUS"));
            return page;
        }

        // ------------------------------------------------------------------ transmit column

        private Control BuildTransmitColumn()
        {
            var card = Theme.Card();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(S(10), S(8), S(6), S(10));

            var cols = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface };
            cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            cols.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(62)));

            var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.Surface, Margin = new Padding(S(0)) };
            int r = 0;
            Action<Control, int> add = (c, h) =>
            {
                stack.RowStyles.Add(h > 0 ? new RowStyle(SizeType.Absolute, h) : new RowStyle(SizeType.Percent, 100));
                stack.Controls.Add(c, 0, r++);
            };

            add(new Label { Text = "FREQUENCY MHz", Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Semi(8.5f), TextAlign = ContentAlignment.BottomLeft }, 20);

            _cmbFreq = Theme.Combo(true);
            _cmbFreq.Font = Theme.Semi(15f);
            _cmbFreq.ItemHeight = S(26);
            for (int i = 0; i < 28; i++)
            {
                double up = 2403.25 + i * 0.25;
                _cmbFreq.Items.Add(up.ToString("0.00", Inv));
            }
            _cmbFreq.SelectionChangeCommitted += (s, e) => BeginInvoke((Action)(async () => await FrequencyCommitted()));
            _cmbFreq.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await FrequencyCommitted(); } };
            _cmbFreq.Leave += async (s, e) => await FrequencyCommitted();
            add(_cmbFreq, S(40));

            _lblDownlink = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Ui(9f), TextAlign = ContentAlignment.TopLeft };
            add(_lblDownlink, S(22));

            _lblDbb = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Semi(10f), TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface2, Padding = new Padding(S(6), S(0), S(0), S(0)), Margin = new Padding(S(0), S(2), S(0), S(2)) };
            add(_lblDbb, S(28));

            add(new Label { Text = "TRANSMISSION", Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Semi(8.5f), TextAlign = ContentAlignment.BottomLeft }, 22);
            _light = new TrafficLight { Dock = DockStyle.Fill };
            add(_light, S(92));

            _lblStats = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Ui(8.5f), TextAlign = ContentAlignment.MiddleLeft };
            add(_lblStats, S(24));

            _btnStart = Theme.Button("START", true);
            _btnStart.Dock = DockStyle.Fill; _btnStart.Font = Theme.Semi(13f); _btnStart.Margin = new Padding(S(0), S(4), S(0), S(4));
            _btnStart.Click += async (s, e) => await StartStopAsync();
            add(_btnStart, S(54));

            _btnPtt = Theme.Button("PTT");
            _btnPtt.Dock = DockStyle.Fill; _btnPtt.Font = Theme.Semi(13f); _btnPtt.Margin = new Padding(S(0), S(4), S(0), S(4));
            _btnPtt.Click += async (s, e) => await TogglePttAsync();
            add(_btnPtt, S(54));

            _btnMonitor = Theme.Button("MONITOR STREAM");
            _btnMonitor.Dock = DockStyle.Fill; _btnMonitor.Margin = new Padding(S(0), S(4), S(0), S(4));
            _btnMonitor.Click += (s, e) => OpenMonitor();
            add(_btnMonitor, S(38));

            _lblSummary = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Ui(8.5f), TextAlign = ContentAlignment.TopLeft, Padding = new Padding(S(0), S(6), S(0), S(0)) };
            add(_lblSummary, S(0));
            stack.RowCount = r;

            // power slider (DATV-Easy style)
            var power = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Surface, Margin = new Padding(S(4), S(0), S(0), S(0)) };
            power.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            power.RowStyles.Add(new RowStyle(SizeType.Absolute, S(22)));
            power.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            power.RowStyles.Add(new RowStyle(SizeType.Absolute, S(40)));
            power.Controls.Add(new Label { Text = "POWER", Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Semi(7.5f), TextAlign = ContentAlignment.BottomCenter }, 0, 0);
            _trkPower = new TrackBar
            {
                Orientation = Orientation.Vertical,
                Dock = DockStyle.Fill,
                Minimum = -200,
                Maximum = 0,
                TickFrequency = 20,
                SmallChange = 1,
                LargeChange = 4,
                TickStyle = TickStyle.Both,
                BackColor = Theme.Surface,
                AutoSize = false   // fill the column so the slider is centred over the dB box
            };
            _trkPower.ValueChanged += (s, e) =>
            {
                _lblPower.Text = SliderGainDb().ToString("0.00", Inv) + "\ndB";
                if (CanLive()) { _gainTimer.Stop(); _gainTimer.Start(); }
            };
            power.Controls.Add(_trkPower, 0, 1);
            _lblPower = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Semi(8f), TextAlign = ContentAlignment.MiddleCenter, BackColor = Theme.Surface2 };
            power.Controls.Add(_lblPower, 0, 2);

            cols.Controls.Add(stack, 0, 0);
            cols.Controls.Add(power, 1, 0);
            card.Controls.Add(cols);
            var titleRow = new Panel { Dock = DockStyle.Top, Height = S(32), BackColor = Theme.Surface };
            var title = Theme.CardTitle("TRANSMIT");
            title.Dock = DockStyle.Fill;
            _btnCompact = Theme.Button("COMPACT");
            _btnCompact.Dock = DockStyle.Right;
            _btnCompact.Width = S(84);
            _btnCompact.Font = Theme.Semi(8f);
            _btnCompact.Margin = new Padding(S(0));
            _btnCompact.Click += (s, e) => SetCompact(!_compact);
            _btnTop = Theme.Button("ON TOP");
            _btnTop.Dock = DockStyle.Right;
            _btnTop.Width = S(66);
            _btnTop.Font = Theme.Semi(8f);
            _btnTop.Click += (s, e) =>
            {
                _s.AlwaysOnTop = !_s.AlwaysOnTop;
                ApplyTopMost();
                try { _s.Save(); } catch { }
            };
            var gap = new Panel { Dock = DockStyle.Right, Width = S(6), BackColor = Theme.Surface };
            titleRow.Controls.Add(title);
            titleRow.Controls.Add(_btnTop);
            titleRow.Controls.Add(gap);
            titleRow.Controls.Add(_btnCompact);
            card.Controls.Add(titleRow);
            return card;
        }

        private double SliderGainDb() { return _trkPower.Value / 4.0; }

        private void SetGainSlider(double db)
        {
            int v = (int)Math.Round(db * 4);
            _trkPower.Value = Math.Max(_trkPower.Minimum, Math.Min(_trkPower.Maximum, v));
            _lblPower.Text = SliderGainDb().ToString("0.00", Inv) + "\ndB";
        }

        private async Task GainCommitted()
        {
            if (!CanLive()) return;
            double db = SliderGainDb();
            if (_active != null) _active.GainDb = db;
            await Live(() => _tx.SetGainAsync(db), "Power " + db.ToString("0.00", Inv) + " dB");
        }

        private async Task ChannelPicked(double downlinkMHz, int srKs)
        {
            double up = downlinkMHz - QoSpectrum.UplinkOffsetMHz;
            if (srKs > 0)
            {
                if (_running && _active != null && _active.SymbolRateKs != srKs)
                    AppendLog("That channel is for " + srKs + " KS. Stop and START again to change the symbol rate.");
                else if (!_running)
                    SelectOrText(_cmbSr, srKs.ToString(Inv));
            }
            _cmbFreq.Text = up.ToString("0.00", Inv);
            AppendLog(string.Format(Inv, "QO-100 channel {0:0.00} MHz ↓ → TX {1:0.00} MHz{2}", downlinkMHz, up, srKs > 0 ? ", " + srKs + " KS" : ""));
            await FrequencyCommitted();
        }

        private static void AddSpan(TableLayoutPanel t, Control c, int row, int height, bool fill = false)
        {
            t.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
            t.Controls.Add(c, 0, row);
            t.SetColumnSpan(c, 2);
        }

        private static Panel Inline(Control main, Control side)
        {
            var p = new Panel { Dock = DockStyle.Fill, Margin = new Padding(S(0), S(3), S(0), S(3)), BackColor = Theme.Surface };
            main.Dock = DockStyle.Fill;
            main.Margin = new Padding(S(0));
            var gap = new Panel { Dock = DockStyle.Right, Width = S(4), BackColor = Theme.Surface };
            p.Controls.Add(main);
            p.Controls.Add(gap);
            p.Controls.Add(side);
            return p;
        }

        private void HookRecalc(Control c)
        {
            var cb = c as ComboBox;
            if (cb != null) { cb.SelectedIndexChanged += (s, e) => Recalc(); cb.TextChanged += (s, e) => Recalc(); return; }
            var ch = c as CheckBox;
            if (ch != null) ch.CheckedChanged += (s, e) => Recalc();
        }

        // ================================================================== memories

        private void RefreshMemories()
        {
            object sel = _lstMem.SelectedItem;
            _lstMem.Items.Clear();
            foreach (var m in _s.Memories) _lstMem.Items.Add(m);
            if (sel != null && _lstMem.Items.Contains(sel)) _lstMem.SelectedItem = sel;
        }

        private void RecallMemory()
        {
            if (_running) { AppendLog("Stop transmitting before recalling a memory."); return; }
            var m = _lstMem.SelectedItem as TxMemory;
            if (m == null || m.Params == null) { AppendLog("Select a memory first."); return; }
            ApplyParams(m.Params);
            AppendLog("Recalled \"" + m.Name + "\": " + m.Params.Summary());
        }

        private void StoreMemory(bool overwrite)
        {
            string name = _txtMemName.Text.Trim();
            var p = ReadParams();
            if (overwrite)
            {
                var m = _lstMem.SelectedItem as TxMemory;
                if (m == null) { AppendLog("Select the memory to overwrite."); return; }
                if (name.Length > 0) m.Name = name;
                m.Params = p;
            }
            else
            {
                if (name.Length == 0) name = p.TxFrequencyMHz.ToString("0.00", Inv) + " " + p.SymbolRateKs + "KS";
                _s.Memories.Add(new TxMemory { Name = name, Params = p });
            }
            try { _s.Save(); } catch (Exception ex) { AppendLog("Could not save: " + ex.Message); }
            RefreshMemories();
            AppendLog("Stored \"" + name + "\".");
        }

        private void DeleteMemory()
        {
            var m = _lstMem.SelectedItem as TxMemory;
            if (m == null) return;
            if (MessageBox.Show(this, "Delete memory \"" + m.Name + "\"?", ProductTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _s.Memories.Remove(m);
            try { _s.Save(); } catch { }
            RefreshMemories();
        }

        // ================================================================== parameters <-> UI

        private string Constellation()
        {
            return (_cmbConst.SelectedItem ?? "QPSK").ToString().ToLowerInvariant();
        }

        private bool DvbS2() { return _cmbStandard.SelectedIndex != 1; }

        private void RefillFec()
        {
            string keep = _cmbFec.SelectedItem as string;
            _cmbFec.Items.Clear();
            if (!DvbS2() && Constellation() != "qpsk") { _loading = true; _cmbConst.SelectedIndex = 0; _loading = false; }
            foreach (var f in DvbCalc.FecsFor(Constellation(), DvbS2())) _cmbFec.Items.Add(f);
            int idx = keep != null ? _cmbFec.Items.IndexOf(keep) : -1;
            _cmbFec.SelectedIndex = idx >= 0 ? idx : Math.Min(_cmbFec.Items.Count - 1, Math.Max(0, _cmbFec.Items.Count / 2));
            Recalc();
        }

        private void RefillCodecs(string keep)
        {
            _cmbCodec.Items.Clear();
            foreach (var c in Codecs.All) _cmbCodec.Items.Add(c);
            int sel = 0;
            for (int i = 0; i < _cmbCodec.Items.Count; i++)
                if (((Codecs.CodecChoice)_cmbCodec.Items[i]).Value == keep) sel = i;
            if (_cmbCodec.Items.Count > 0) _cmbCodec.SelectedIndex = sel;
        }

        private static double FirstNumber(string text, double fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            string tok = text.Trim().Split(' ', '\t')[0].Replace(',', '.');
            double d;
            return double.TryParse(tok, NumberStyles.Float, Inv, out d) ? d : fallback;
        }

        private static void SelectOrText(ComboBox cb, string value)
        {
            for (int i = 0; i < cb.Items.Count; i++)
            {
                string it = cb.GetItemText(cb.Items[i]);
                if (it == value || it.StartsWith(value + " ", StringComparison.Ordinal)) { cb.SelectedIndex = i; return; }
            }
            if (cb.DropDownStyle == ComboBoxStyle.DropDown) cb.Text = value;
            else if (cb.Items.Count > 0 && cb.SelectedIndex < 0) cb.SelectedIndex = 0;
        }

        private TxParams ReadParams()
        {
            var p = new TxParams
            {
                Input = (InputType)Math.Max(0, _cmbInput.SelectedIndex),
                VideoDevice = _cmbVideo.Text.Trim(),
                AudioDevice = _cmbAudio.Text.Trim(),
                InputFile = _txtFile.Text.Trim(),
                Codec = _cmbCodec.SelectedItem is Codecs.CodecChoice ? ((Codecs.CodecChoice)_cmbCodec.SelectedItem).Value : "libx265",
                Preset = (_cmbPreset.SelectedItem as string) ?? "veryfast",
                ImageSize = _cmbSize.Text.Trim(),
                Fps = FirstNumber(_cmbFps.Text, 25),
                AudioChannels = new[] { 1, 2, 6, 8 }[Math.Max(0, _cmbChannels.SelectedIndex)],
                AudioBitrateK = (int)FirstNumber(_cmbAbr.Text, 32),
                DvbS2 = DvbS2(),
                Constellation = Constellation(),
                SymbolRateKs = (int)FirstNumber(_cmbSr.Text, 333),
                Fec = (_cmbFec.SelectedItem as string) ?? "1/2",
                ShortFrames = _cmbFrame.SelectedIndex == 1,
                Pilots = _chkPilots.Checked,
                RollOff = SelectedRollOff(),
                TxFrequencyMHz = FirstNumber(_cmbFreq.Text, 2403.25),
                GainDb = SliderGainDb()
            };
            return p;
        }

        private void ApplyParams(TxParams p)
        {
            _loading = true;
            try
            {
                _cmbInput.SelectedIndex = (int)p.Input;
                _cmbVideo.Text = p.VideoDevice ?? "";
                _cmbAudio.Text = p.AudioDevice ?? "";
                _txtFile.Text = p.InputFile ?? "";
                RefillCodecs(p.Codec);
                SelectOrText(_cmbPreset, string.IsNullOrEmpty(p.Preset) ? "veryfast" : p.Preset);
                SelectOrText(_cmbSize, p.ImageSize);
                SelectOrText(_cmbFps, p.Fps.ToString("0.##", Inv));
                _cmbChannels.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { 1, 2, 6, 8 }, p.AudioChannels));
                SelectOrText(_cmbAbr, p.AudioBitrateK.ToString(Inv));
                _cmbStandard.SelectedIndex = p.DvbS2 ? 0 : 1;
                _cmbConst.SelectedIndex = Math.Max(0, Array.IndexOf(DvbCalc.Constellations, p.Constellation));
                SelectOrText(_cmbSr, p.SymbolRateKs.ToString(Inv));
                _cmbFec.Items.Clear();
                foreach (var f in DvbCalc.FecsFor(p.Constellation, p.DvbS2)) _cmbFec.Items.Add(f);
                int fi = _cmbFec.Items.IndexOf(p.Fec);
                _cmbFec.SelectedIndex = fi >= 0 ? fi : 0;
                _cmbFrame.SelectedIndex = p.ShortFrames ? 1 : 0;
                _chkPilots.Checked = p.Pilots;
                SelectRollOff(p.RollOff);
                SelectOrText(_cmbFreq, p.TxFrequencyMHz.ToString("0.00", Inv));
                _trkPower.Maximum = Math.Max(-196, (int)Math.Round(Math.Min(0, _s.PowerLimitDb) * 4));
                SetGainSlider(p.GainDb);
                _numDGain.Value = 0;
                _numDGain.Enabled = p.Constellation == "qpsk";
            }
            finally { _loading = false; }
            UpdateInputVisibility();
            Recalc();
        }

        private double SelectedRollOff() { return FirstNumber(_cmbRollOff.Text, 0.20); }

        private void SelectRollOff(double value)
        {
            int best = 0;
            double diff = double.MaxValue;
            for (int i = 0; i < _cmbRollOff.Items.Count; i++)
            {
                double d = Math.Abs(FirstNumber(_cmbRollOff.Items[i].ToString(), 0.20) - value);
                if (d < diff) { diff = d; best = i; }
            }
            if (_cmbRollOff.Items.Count > 0) _cmbRollOff.SelectedIndex = best;
        }

        private static decimal Clamp(decimal v, decimal min, decimal max) { return v < min ? min : v > max ? max : v; }

        private void UpdateInputVisibility()
        {
            var t = (InputType)Math.Max(0, _cmbInput.SelectedIndex);
            _videoRow.Enabled = t == InputType.Capture;
            _cmbAudio.Enabled = t == InputType.Capture && !_running;
            _fileRow.Enabled = t == InputType.File && !_running;
        }

        private void Recalc()
        {
            if (_loading || _cmbFec == null || _cmbFec.SelectedItem == null) return;
            if (_running) return;
            try
            {
                var p = ReadParams();
                UpdateDownlink(p.TxFrequencyMHz);
                double ts = DvbCalc.TsBitrateK(p);
                var plan = EncodePlan.Build(p, _s, ts, false);
                ShowPlan(p, plan, false);
            }
            catch { }
        }

        private void ShowPlan(TxParams p, EncodePlan plan, bool live)
        {
            _lblTs.Text = plan.TsBitrateK.ToString("0", Inv) + " kb/s" + (plan.TsFromDevice ? "  ✓" : "");
            _lblVideo.Text = plan.VideoBitrateK.ToString("0", Inv) + " kb/s";
            _lblSummary.Text = string.Format(Inv, "{0} {1}  {2} KS  FEC {3}  {4}{5}\n{6} {10}  {7} @ {8:0.##} fps, audio {9} k",
                p.DvbS2 ? "DVB-S2" : "DVB-S", p.Constellation.ToUpperInvariant(), p.SymbolRateKs, p.Fec,
                p.ShortFrames ? "short" : "long", p.Pilots ? ", pilots" : "",
                p.Codec, p.ImageSize, p.Fps, p.AudioBitrateK, p.Preset);
        }

        // ================================================================== transmitter

        private void CreateTransmitter()
        {
            if (_tx != null) { var old = _tx; _tx = null; Task.Run(async () => { try { await old.DisconnectAsync(); } catch { } old.Dispose(); }); }
            _tx = Transmitter.Create(_s);
            _tx.Log += m => UI(() => AppendLog(m));
            _tx.ConnectionChanged += () => UI(UpdateHeader);
            _tx.Telemetry += (k, v) => _telemetry[k] = v;
            _tx.Fault += why => UI(async () =>
            {
                if (!_running || _busy) return;
                AppendLog(why);
                await StopAsync("Stopped: " + why);
            });
            double keepRollOff = SelectedRollOff();
            _loading = true;
            _cmbRollOff.Items.Clear();
            foreach (double ro in _tx.RollOffs) _cmbRollOff.Items.Add(ro.ToString("0.00", Inv));
            SelectRollOff(keepRollOff);
            _loading = false;
            _telemetry.Clear();
            _lblHardware.Text = _tx.DisplayName;
            _lblHelp.Text = _tx.HelpText;
            _trkPower.Maximum = Math.Max(-196, (int)Math.Round(Math.Min(0, _s.PowerLimitDb) * 4));
            if (_trkPower.Value > _trkPower.Maximum) _trkPower.Value = _trkPower.Maximum;
            UpdateHeader();
            UpdateButtons();
        }

        private void UpdateHeader()
        {
            if (_tx == null) return;
            if (!_tx.NeedsConnection)
            {
                _btnConnect.Text = "READY";
                _btnConnect.Enabled = false;
                Theme.Paint(_btnConnect, Theme.GreenFill, Theme.GreenBorder);
            }
            else if (_busy && !_tx.IsConnected)
            {
                _btnConnect.Text = "WAITING";
                _btnConnect.Enabled = false;
                Theme.Paint(_btnConnect, Theme.Grey, Theme.GreyBorder);
            }
            else if (_tx.IsConnected)
            {
                _btnConnect.Text = "CONNECTED";
                _btnConnect.Enabled = !_running;
                Theme.Paint(_btnConnect, Theme.GreenFill, Theme.GreenBorder);
            }
            else
            {
                _btnConnect.Text = "CONNECT";
                _btnConnect.Enabled = true;
                Theme.Paint(_btnConnect, Theme.Accent, Theme.Accent);
            }
            UpdateButtons();
        }

        private async Task ToggleConnectionAsync()
        {
            if (_tx == null || !_tx.NeedsConnection || _busy) return;
            _busy = true; UpdateHeader();
            try
            {
                if (_tx.IsConnected) await _tx.DisconnectAsync();
                else await _tx.ConnectAsync();
            }
            catch (Exception ex) { AppendLog("Could not connect to " + _s.PlutoIp + ": " + ex.Message); }
            finally { _busy = false; UpdateHeader(); }
        }

        private void UpdateButtons()
        {
            if (_btnStart == null || _tx == null) return;
            _btnStart.Enabled = !_busy;
            if (_running)
            {
                _btnStart.Text = "STOP";
                Theme.Paint(_btnStart, Theme.Surface2, Theme.RedBorder);
            }
            else
            {
                _btnStart.Text = _s.Mode == RunMode.Encode ? "START" : "START CONTROL";
                Theme.Paint(_btnStart, Theme.Accent, Theme.Accent);
            }

            _btnPtt.Enabled = _running && _tx.SupportsPtt && !_busy;
            if (!_tx.SupportsPtt) { _btnPtt.Text = "PTT N/A"; Theme.Paint(_btnPtt, Theme.Surface2, Theme.Border); }
            else if (_ptt) { _btnPtt.Text = "ON AIR"; Theme.Paint(_btnPtt, Theme.Red, Theme.RedBorder); }
            else { _btnPtt.Text = "PTT"; Theme.Paint(_btnPtt, Theme.Surface2, Theme.Border); }

            foreach (var c in _lockWhileRunning) c.Enabled = !_running && !_busy;
            _btnSettings.Enabled = !_running && !_busy;
            _btnMonitor.Enabled = _s.Hardware == HardwareType.LocalTest;
            UpdateInputVisibility();
        }

        private bool CanLive() { return !_loading && _running && _tx != null && _tx.SupportsLiveControl && _tx.IsConnected; }

        private async Task Live(Func<Task> action, string what)
        {
            try { await action(); AppendLog(what); }
            catch (Exception ex) { AppendLog(what + " failed: " + ex.Message); }
        }

        private async Task FrequencyCommitted()
        {
            double f = FirstNumber(_cmbFreq.Text, 0);
            if (f < 70 || f > 6000) return;
            UpdateDownlink(f);
            if (CanLive() && _active != null && Math.Abs(_active.TxFrequencyMHz - f) > 0.0001)
            {
                _active.TxFrequencyMHz = f;
                await Live(() => _tx.SetFrequencyAsync(f), "TX frequency " + f.ToString("0.000", Inv) + " MHz");
            }
        }

        // ================================================================== start / stop

        private string Validate(TxParams p)
        {
            if (p.SymbolRateKs < 25 || p.SymbolRateKs > 4000) return "Symbol rate must be 25 to 4000 KS.";
            if (p.TxFrequencyMHz < 70 || p.TxFrequencyMHz > 6000) return "TX frequency must be 70 to 6000 MHz.";
            if (p.GainDb > _s.PowerLimitDb) return "Gain is above the power limit (" + _s.PowerLimitDb + " dB).";
            if (_s.Mode == RunMode.Encode)
            {
                if (!File.Exists(_s.FfmpegExe)) return "ffmpeg.exe not found in " + _s.EffectiveFfmpegFolder + ". Set the FFmpeg folder in Settings.";
                if (p.Input == InputType.Capture && string.IsNullOrWhiteSpace(p.VideoDevice)) return "Choose a video device.";
                if (p.Input == InputType.Capture && string.IsNullOrWhiteSpace(p.AudioDevice)) return "Choose an audio device (a virtual audio cable works if you have no microphone).";
                if (p.Input == InputType.File && !File.Exists(p.InputFile)) return "Video file not found.";
                if (!System.Text.RegularExpressions.Regex.IsMatch(p.ImageSize, @"^\d+x\d+$")) return "Resolution must look like 1280x720.";
            }
            string hwErr = _tx != null ? _tx.Validate(p) : null;
            if (hwErr != null) return hwErr;
            if (_s.Mode == RunMode.ControlOnly && !(_tx is PlutoMqttTransmitter || _tx is SdrHelperTransmitter)) return "Control-only mode needs a Pluto with F5OEO MQTT firmware, a LimeSDR or a HackRF.";
            return null;
        }

        private async Task StartStopAsync()
        {
            if (_busy) return;
            if (_running) { await StopAsync("Stopped."); return; }

            var p = ReadParams();
            string err = Validate(p);
            if (err != null) { AppendLog(err); MessageBox.Show(this, err, ProductTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            _busy = true; UpdateHeader();
            _s.LastRun = p.Clone();
            try { _s.Save(); } catch { }

            try
            {
                if (_tx.NeedsConnection && !_tx.IsConnected)
                {
                    AppendLog("Connecting to " + _tx.DisplayName + "...");
                    await _tx.ConnectAsync();
                }

                double tsK = DvbCalc.TsBitrateK(p);
                bool fromDevice = false;
                var fetched = await _tx.FetchTsBitrateKAsync(p);
                if (fetched.HasValue) { tsK = fetched.Value; fromDevice = true; }
                var plan = EncodePlan.Build(p, _s, tsK, fromDevice);
                ShowPlan(p, plan, true);

                await _tx.ConfigureAsync(p, false);
                _ptt = false;

                if (_s.Mode == RunMode.Encode)
                {
                    string url = _tx.StreamUrl();
                    string args = FfmpegBuilder.BuildArgs(p, _s, plan, url);
                    AppendLog((_s.FfmpegExeFor(p.Codec) != _s.FfmpegExe ? "ffmpeg (NVENC compatibility build) " : "ffmpeg ") + args);
                    _ffmpeg.Start(_s.FfmpegExeFor(p.Codec), args);
                    // Most ffmpeg problems (device busy, bad option) make it exit straight away.
                    await Task.Delay(2000);
                    if (!_ffmpeg.IsRunning)
                        throw new InvalidOperationException("the encoder stopped straight away - see the ffmpeg message above.");
                    AppendLog("Encoder running -> " + url);
                }
                else
                {
                    AppendLog("Control mode: start your external encoder (e.g. OBS) sending MPEG-TS to " + _tx.StreamUrl().Split('?')[0] + " at no more than " + plan.TsBitrateK.ToString("0", Inv) + " kbit/s.");
                }

                _active = p;
                _running = true;
                if (_tx.SupportsPtt) AppendLog("Ready. Press PTT to go on air.");
                else AppendLog("Streaming. Transmit is controlled in the radio.");
            }
            catch (Exception ex)
            {
                AppendLog("Start failed: " + ex.Message);
                try { await _ffmpeg.StopAsync(); } catch { }
                try { await _tx.ShutdownAsync(); } catch { }
                _running = false;
            }
            finally
            {
                _busy = false;
                UpdateHeader();
            }
        }

        private async Task StopAsync(string reason)
        {
            _busy = true; UpdateHeader();
            try
            {
                if (_ptt && _tx.SupportsPtt) { try { await _tx.SetPttAsync(false); } catch { } }
                await _ffmpeg.StopAsync();
                await _tx.ShutdownAsync();
                ClosePlayer();
            }
            catch (Exception ex) { AppendLog("Stop: " + ex.Message); }
            finally
            {
                _ptt = false;
                _running = false;
                _active = null;
                _busy = false;
                _lastStats = null;
                _lblStats.Text = "";
                AppendLog(reason);
                UpdateHeader();
                Recalc();
            }
        }

        private async void OnEncoderExited(int code)
        {
            if (!_running || _busy) return;
            AppendLog("Encoder exited (code " + code + "). Muting the transmitter for safety.");
            await StopAsync("Stopped because the encoder exited. Check the log above for the ffmpeg error.");
        }

        private async Task TogglePttAsync()
        {
            if (!_running || !_tx.SupportsPtt || _busy) return;
            bool want = !_ptt;
            try
            {
                await _tx.SetPttAsync(want);
                _ptt = want;
                AppendLog(want ? "PTT ON - transmitting." : "PTT OFF.");
            }
            catch (Exception ex) { AppendLog("PTT failed: " + ex.Message); }
            UpdateButtons();
        }

        // ================================================================== players

        private void OpenMonitor()
        {
            string url;
            if (_s.Hardware == HardwareType.LocalTest)
            {
                url = _s.LocalTestUrl;
                int q = url.IndexOf('?'); if (q > 0) url = url.Substring(0, q);
            }
            else
            {
                var bits = _tx.StreamUrl();
                int q = bits.IndexOf('?'); if (q > 0) bits = bits.Substring(0, q);
                AppendLog("Note: monitoring a unicast stream to the radio only works if this PC can also receive it. Local test mode is best for checking the encoder.");
                url = bits;
            }
            StartPlayer(url, "Monitor");
        }

        private void StartPlayer(string url, string title)
        {
            try
            {
                ClosePlayer();
                ProcessStartInfo psi;
                if (_s.Player == RxPlayer.Mpv && File.Exists(_s.MpvPath))
                    psi = new ProcessStartInfo(_s.MpvPath, "--profile=low-latency --title=" + FfmpegBuilder.Q(title) + " " + FfmpegBuilder.Q(url));
                else
                {
                    if (!File.Exists(_s.FfplayExe)) { AppendLog("ffplay.exe not found in " + _s.EffectiveFfmpegFolder); return; }
                    psi = new ProcessStartInfo(_s.FfplayExe, "-hide_banner -fflags nobuffer -window_title " + FfmpegBuilder.Q(title) + " " + FfmpegBuilder.Q(url));
                }
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                _player = Process.Start(psi);
                AppendLog(title + " player opened on " + url);
            }
            catch (Exception ex) { AppendLog("Could not start player: " + ex.Message); }
        }

        private void ClosePlayer()
        {
            try { if (_player != null && !_player.HasExited) _player.Kill(); } catch { }
            _player = null;
        }

        // ================================================================== devices, files, profiles, settings

        private async Task RefreshDevicesAsync()
        {
            if (!File.Exists(_s.FfmpegExe))
            {
                AppendLog("ffmpeg.exe not found in " + _s.EffectiveFfmpegFolder + " - set the FFmpeg folder in Settings.");
                return;
            }
            string v = _cmbVideo.Text, a = _cmbAudio.Text;
            var lists = await FfmpegRunner.ListDirectShowDevicesAsync(_s.FfmpegExe);
            _cmbVideo.Items.Clear(); _cmbVideo.Items.AddRange(lists.Item1.Cast<object>().ToArray());
            _cmbAudio.Items.Clear(); _cmbAudio.Items.AddRange(lists.Item2.Cast<object>().ToArray());
            _cmbVideo.Text = v; _cmbAudio.Text = a;
            if (string.IsNullOrEmpty(_cmbVideo.Text) && _cmbVideo.Items.Count > 0) _cmbVideo.SelectedIndex = 0;
            if (string.IsNullOrEmpty(_cmbAudio.Text) && _cmbAudio.Items.Count > 0) _cmbAudio.SelectedIndex = 0;
            AppendLog("Found " + lists.Item1.Count + " video and " + lists.Item2.Count + " audio capture devices.");
        }

        private void BrowseFile()
        {
            using (var d = new OpenFileDialog { Filter = "Video files|*.ts;*.mp4;*.mkv;*.mov;*.avi;*.mpg|All files|*.*" })
                if (d.ShowDialog(this) == DialogResult.OK) _txtFile.Text = d.FileName;
        }

        private void OpenSettings()
        {
            if (_running) return;
            var current = ReadParams();
            using (var f = new SettingsForm(_s.Clone(), _tx))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                f.Result.LastRun = current;
                f.Result.Profiles = _s.Profiles;
                f.Result.Memories = _s.Memories;
                f.Result.ShowSpectrum = _s.ShowSpectrum;
                _s = f.Result;
                try { _s.Save(); } catch (Exception ex) { AppendLog("Could not save settings: " + ex.Message); }
            }
            CreateTransmitter();
            ApplyParams(current);
            AppendLog("Settings saved. Transmitter: " + _tx.DisplayName);
        }

        // ================================================================== misc

        private async Task CheckForUpdateAsync()
        {
            if (!_s.CheckForUpdates) return;
            try
            {
                var r = await UpdateChecker.CheckAsync(Application.ProductVersion);
                if (r == null || IsDisposed) return;
                _lblCredit.Text = "v" + UpdateChecker.DisplayVersion(Application.ProductVersion) + "  •";
                _lnkUpdate.Text = r.Tag + " available - click to download";
                Text = ProductTitle + "  v" + UpdateChecker.DisplayVersion(Application.ProductVersion) + "   (" + r.Tag + " available)";
                _lnkUpdate.Tag = r.Url;
                _lnkUpdate.Visible = true;
                AppendLog("New version " + r.Tag + " available: " + r.Url);
            }
            catch { } // offline or GitHub unavailable: say nothing
        }

        private static void SetLabel(Label l, string text, Color colour)
        {
            if (l.Text != text) l.Text = text;
            if (l.ForeColor != colour) l.ForeColor = colour;
        }

        private void ShowDbb()
        {
            if (_lblDbb == null) return;
            bool onAir = _running && _ptt;
            if (!_s.ShowSpectrum) { SetLabel(_lblDbb, "QO-100 level: spectrum hidden", Theme.Muted); return; }
            if (double.IsNaN(_dbb))
            {
                SetLabel(_lblDbb, onAir ? "QO-100 level: not seen yet" : "QO-100 level vs beacon: -", Theme.Muted);
                return;
            }
            string v = (_dbb >= 0 ? "+" : "") + _dbb.ToString("0.0", Inv) + " dBb";
            SetLabel(_lblDbb, (onAir ? "Your signal  " : "Channel  ") + v + (_dbb >= 0 ? "   ABOVE BEACON" : ""),
                onAir ? QoSpectrum.DbbColour(_dbb) : Theme.Muted);
        }

        private void UpdateDownlink(double uplinkMHz)
        {
            double down = uplinkMHz + QoSpectrum.UplinkOffsetMHz;
            _lblDownlink.Text = down > 10490 && down < 10500
                ? "QO-100 downlink " + down.ToString("0.00", Inv) + " MHz"
                : "";
        }

        private void RefreshLive()
        {
            // traffic light
            bool ready = _tx != null && (!_tx.NeedsConnection || _tx.IsConnected);
            _light.State = _running ? (_ptt ? TxLight.OnAir : TxLight.Standby) : ready ? TxLight.Ready : TxLight.Off;

            string warn = null;
            var stw = _lastStats;
            // Only warn when the encoder has been genuinely behind real time for several seconds:
            // live sources wobble a little, and the first seconds include the device start-up.
            if (_running && stw != null && stw.Elapsed > 6 && !double.IsNaN(stw.SpeedNow))
            {
                if (stw.SpeedNow < 0.93) _slowSince = _slowSince ?? DateTime.Now; else _slowSince = null;
            }
            else _slowSince = null;
            if (_slowSince.HasValue && (DateTime.Now - _slowSince.Value).TotalSeconds >= 5)
                warn = "Encoder too slow (" + stw.Speed + ") - try a GPU codec or faster preset";
            string ur;
            long underruns;
            if (_running && _telemetry.TryGetValue("mod/underruns", out ur) && long.TryParse(ur, out underruns))
            {
                if (_lastUnderruns >= 0 && underruns > _lastUnderruns) warn = "Radio buffer underrun - check USB / CPU";
                _lastUnderruns = underruns;
            }
            else _lastUnderruns = -1;
            if (_running && _ptt && !double.IsNaN(_dbb) && _dbb >= 0)
                warn = "Above beacon (+" + _dbb.ToString("0.0", Inv) + " dBb) - reduce power";
            _light.SetWarning(warn != null, warn);
            ShowDbb();
            _light.Tick();

            // spectrum marker
            if (_spectrum != null && _s.ShowSpectrum)
            {
                double f = _active != null ? _active.TxFrequencyMHz : FirstNumber(_cmbFreq.Text, 0);
                int sr = _active != null ? _active.SymbolRateKs : (int)FirstNumber(_cmbSr.Text, 0);
                _spectrum.SetTransmit(f, sr, _running && _ptt);
            }

            var st = _lastStats;
            if (_running && st != null)
            {
                _lblStats.Text = string.Format(Inv, "Encoder  {0:0.#} fps   {1}   speed {2}{3}", st.Fps, st.Bitrate, st.Speed, st.Dropped > 0 ? "   dropped " + st.Dropped : "");
                _lblStats.ForeColor = _slowSince.HasValue ? Theme.Amber : Theme.Muted;
            }
            else if (_running && _s.Mode != RunMode.Encode) _lblStats.Text = "Control mode running";

            if (_telemetry.Count > 0)
            {
                var lines = _telemetry.OrderBy(k => k.Key).Take(30).Select(k => (k.Key.Length > 26 ? k.Key.Substring(k.Key.Length - 26) : k.Key).PadRight(27) + k.Value);
                _lblStatus.Text = string.Join("\n", lines);
            }
            else _lblStatus.Text = "No radio status yet.";
        }

        private void AppendLog(string line)
        {
            if (_log == null) return;
            if (_log.TextLength > 60000) _log.Text = _log.Text.Substring(_log.TextLength - 30000);
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine);
            WriteLogFile(line);
        }

        /// <summary>Folder with one log file per day (%AppData%\DatvTx\logs), kept for 14 days.</summary>
        public static string LogFolder
        {
            get { return Path.Combine(Path.GetDirectoryName(AppSettings.SettingsPath), "logs"); }
        }

        private bool _logPruned;

        private void WriteLogFile(string line)
        {
            try
            {
                Directory.CreateDirectory(LogFolder);
                if (!_logPruned)
                {
                    _logPruned = true;
                    foreach (var f in Directory.GetFiles(LogFolder, "datvtx-*.log"))
                        if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
                    File.AppendAllText(Path.Combine(LogFolder, "datvtx-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log"),
                        Environment.NewLine + "==== DATV TX " + Application.ProductVersion + " started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====" + Environment.NewLine);
                }
                File.AppendAllText(Path.Combine(LogFolder, "datvtx-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log"),
                    DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine);
            }
            catch { }
        }

        private void UI(Action a)
        {
            if (IsDisposed) return;
            try { if (InvokeRequired) BeginInvoke(a); else a(); } catch { }
        }

        private async void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (_running)
            {
                e.Cancel = true;
                await StopAsync("Stopped on exit.");
                Close();
                return;
            }
            if (WindowState == FormWindowState.Normal)
            {
                if (_compact) { _s.CompactWidth = Width; _s.CompactHeight = Height; }
                else { _s.NormalWidth = Width; _s.NormalHeight = Height; }
            }
            try { _s.LastRun = ReadParams(); _s.Save(); } catch { }
            _uiTimer.Stop();
            _spectrum.Stop();
            ClosePlayer();
            _ffmpeg.Dispose();
            if (_tx != null) { try { await _tx.DisconnectAsync(); } catch { } _tx.Dispose(); }
        }
    }
}
