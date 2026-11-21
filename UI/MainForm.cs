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
        private readonly Timer _uiTimer = new Timer { Interval = 500 };

        // header
        private Label _lblHardware, _lblHelp;
        private Button _btnConnect, _btnSettings;

        // source & encoder
        private ComboBox _cmbInput, _cmbVideo, _cmbAudio, _cmbCodec, _cmbSize, _cmbFps, _cmbChannels, _cmbAbr;
        private TextBox _txtFile;
        private Button _btnRefresh, _btnBrowse;
        private Panel _fileRow, _videoRow;

        // modulator
        private ComboBox _cmbStandard, _cmbConst, _cmbSr, _cmbFec, _cmbFrame, _cmbRollOff, _cmbFreq;
        private CheckBox _chkPilots;
        private NumericUpDown _numGain, _numDGain;

        // transmit
        private Label _lblBigFreq, _lblSummary, _lblTs, _lblVideo, _lblStats, _lblStatus;
        private Button _btnStart, _btnPtt, _btnMonitor;

        // receive
        private CheckBox _chkRx;
        private ComboBox _cmbRxFreq, _cmbRxSr;
        private Button _btnPlayer;

        private TextBox _log;
        private readonly List<Control> _lockWhileRunning = new List<Control>();

        public MainForm()
        {
            _s = AppSettings.Load();

            Text = ProductTitle;
            Icon = Theme.BuildIcon();
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9f);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1180, 780);
            Size = new Size(1280, 860);
            AutoScaleMode = AutoScaleMode.Dpi;

            BuildUi();
            CreateTransmitter();
            ApplyParams(_s.LastRun ?? new TxParams());

            _ffmpeg.Log += l => UI(() => AppendLog(l));
            _ffmpeg.Stats += st => { _lastStats = st; };
            _ffmpeg.Exited += code => UI(() => OnEncoderExited(code));

            _uiTimer.Tick += (s, e) => RefreshLive();
            _uiTimer.Start();

            Load += async (s, e) => { Theme.DarkTitleBar(this); await RefreshDevicesAsync(); };
            FormClosing += OnClosing;
        }

        // ================================================================== layout

        private void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12), BackColor = Theme.Bg };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, 10, 0, 10) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.4f));
            root.Controls.Add(body, 0, 1);

            var c1 = BuildSourceCard(); c1.Margin = new Padding(0, 0, 6, 0);
            var c2 = BuildModulatorCard(); c2.Margin = new Padding(6, 0, 6, 0);
            var c3 = BuildTransmitCard(); c3.Margin = new Padding(6, 0, 0, 0);
            body.Controls.Add(c1, 0, 0);
            body.Controls.Add(c2, 1, 0);
            body.Controls.Add(c3, 2, 0);

            var logCard = Theme.Card();
            logCard.Dock = DockStyle.Fill;
            logCard.Padding = new Padding(10, 6, 10, 8);
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
            logCard.Controls.Add(Theme.CardTitle("LOG"));
            root.Controls.Add(logCard, 0, 2);
        }

        private Panel BuildHeader()
        {
            var p = Theme.Card();
            p.Dock = DockStyle.Fill;
            p.Margin = new Padding(0);

            var title = new Label { Text = "◉  " + ProductTitle, AutoSize = true, Location = new Point(16, 10), ForeColor = Theme.Text, Font = Theme.Semi(14f) };
            var credit = new Label { Text = "based on DATV-NotSoEasy by DL5OCD", AutoSize = true, Location = new Point(20, 44), ForeColor = Theme.Muted, Font = Theme.Ui(8.5f) };
            _lblHardware = new Label { AutoSize = true, Location = new Point(330, 14), ForeColor = Theme.Text, Font = Theme.Semi(10f) };
            _lblHelp = new Label { AutoSize = false, Location = new Point(330, 38), Size = new Size(560, 34), ForeColor = Theme.Muted, Font = Theme.Ui(8.5f), Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };

            _btnSettings = Theme.Button("SETTINGS");
            _btnSettings.Size = new Size(110, 38);
            _btnSettings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnSettings.Click += (s, e) => OpenSettings();

            _btnConnect = Theme.Button("CONNECT", true);
            _btnConnect.Size = new Size(150, 38);
            _btnConnect.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnConnect.Click += async (s, e) => await ToggleConnectionAsync();

            p.Controls.AddRange(new Control[] { title, credit, _lblHardware, _lblHelp, _btnConnect, _btnSettings });
            p.Resize += (s, e) =>
            {
                _btnSettings.Location = new Point(p.Width - _btnSettings.Width - 16, 16);
                _btnConnect.Location = new Point(_btnSettings.Left - _btnConnect.Width - 10, 16);
                _lblHelp.Width = Math.Max(200, _btnConnect.Left - _lblHelp.Left - 16);
            };
            return p;
        }

        private Panel BuildSourceCard()
        {
            var card = Theme.Card();
            card.Dock = DockStyle.Fill;
            var grid = Theme.FieldGrid();

            _cmbInput = Theme.Combo();
            _cmbInput.Items.AddRange(new object[] { "Capture device (OBS / camera)", "Network UDP", "Network RTMP", "Video file" });
            _cmbInput.SelectedIndexChanged += (s, e) => UpdateInputVisibility();
            Theme.AddRow(grid, "Input", _cmbInput);

            _cmbVideo = Theme.Combo(true);
            _btnRefresh = Theme.Button("↻");
            _btnRefresh.Width = 34; _btnRefresh.Dock = DockStyle.Right; _btnRefresh.Margin = new Padding(0);
            _btnRefresh.Click += async (s, e) => await RefreshDevicesAsync();
            _videoRow = Inline(_cmbVideo, _btnRefresh);
            Theme.AddRow(grid, "Video device", _videoRow);

            _cmbAudio = Theme.Combo(true);
            Theme.AddRow(grid, "Audio device", _cmbAudio);

            _txtFile = Theme.TextBox();
            _btnBrowse = Theme.Button("…");
            _btnBrowse.Width = 34; _btnBrowse.Dock = DockStyle.Right;
            _btnBrowse.Click += (s, e) => BrowseFile();
            _fileRow = Inline(_txtFile, _btnBrowse);
            Theme.AddRow(grid, "Video file", _fileRow);

            _cmbCodec = Theme.Combo();
            Theme.AddRow(grid, "Codec", _cmbCodec);

            _cmbSize = Theme.Combo(true);
            _cmbSize.Items.AddRange(new object[] { "640x360", "960x540", "1280x720", "1920x1080", "2560x1440" });
            Theme.AddRow(grid, "Resolution", _cmbSize);

            _cmbFps = Theme.Combo(true);
            _cmbFps.Items.AddRange(new object[] { "10", "20", "25", "30", "48", "50", "60" });
            Theme.AddRow(grid, "Frame rate", _cmbFps);

            _cmbChannels = Theme.Combo();
            _cmbChannels.Items.AddRange(new object[] { "Mono", "Stereo", "5.1", "7.1" });
            Theme.AddRow(grid, "Audio", _cmbChannels);

            _cmbAbr = Theme.Combo();
            _cmbAbr.Items.AddRange(new object[] { "8", "16", "32", "48", "64", "96", "128", "192", "256" });
            Theme.AddRow(grid, "Audio kbit/s", _cmbAbr);

            foreach (Control c in new Control[] { _cmbCodec, _cmbSize, _cmbFps, _cmbChannels, _cmbAbr })
                HookRecalc(c);

            _lockWhileRunning.AddRange(new Control[] { _cmbInput, _cmbVideo, _cmbAudio, _btnRefresh, _txtFile, _btnBrowse, _cmbCodec, _cmbSize, _cmbFps, _cmbChannels, _cmbAbr });

            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 56,
                ForeColor = Theme.Muted,
                Font = Theme.Ui(8.5f),
                Text = "Tip: in OBS use Start Virtual Camera, and send audio to a virtual cable (e.g. VB-Audio) to pick it up here."
            };

            card.Controls.Add(hint);
            card.Controls.Add(grid);
            card.Controls.Add(Theme.CardTitle("SOURCE & ENCODER"));
            return card;
        }

        private Panel BuildModulatorCard()
        {
            var card = Theme.Card();
            card.Dock = DockStyle.Fill;
            var grid = Theme.FieldGrid();

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
            _cmbFrame.Items.AddRange(new object[] { "Long", "Short" });
            Theme.AddRow(grid, "Frames", _cmbFrame);

            _chkPilots = Theme.Check("Pilots");
            Theme.AddRow(grid, "", _chkPilots);

            _cmbRollOff = Theme.Combo();
            _cmbRollOff.Items.AddRange(new object[] { "0.20", "0.15" });
            _cmbRollOff.SelectedIndexChanged += async (s, e) => { if (CanLive()) await Live(() => _tx.SetRollOffAsync(SelectedRollOff()), "Roll-off " + _cmbRollOff.Text); };
            Theme.AddRow(grid, "Roll-off", _cmbRollOff);

            _cmbFreq = Theme.Combo(true);
            for (int i = 0; i < 28; i++)
            {
                double up = 2403.25 + i * 0.25;
                _cmbFreq.Items.Add(string.Format(Inv, "{0:0.00}   (QO-100 {1:0.00})", up, up + 8089.5));
            }
            _cmbFreq.SelectionChangeCommitted += (s, e) => BeginInvoke((Action)(async () => await FrequencyCommitted()));
            _cmbFreq.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await FrequencyCommitted(); } };
            _cmbFreq.Leave += async (s, e) => await FrequencyCommitted();
            Theme.AddRow(grid, "TX frequency (MHz)", _cmbFreq);

            _numGain = Theme.Number(-50, 0, 0.25m, 2);
            _numGain.ValueChanged += async (s, e) => { if (CanLive()) await Live(() => _tx.SetGainAsync((double)_numGain.Value), "Gain " + _numGain.Value.ToString("0.00", Inv) + " dB"); };
            Theme.AddRow(grid, "Gain (dB)", _numGain);

            _numDGain = Theme.Number(-20, 3, 0.25m, 2);
            _numDGain.ValueChanged += async (s, e) => { if (CanLive()) await Live(() => _tx.SetDigitalGainAsync((double)_numDGain.Value), "Digital gain " + _numDGain.Value.ToString("0.00", Inv) + " dB"); };
            Theme.AddRow(grid, "Digital gain (QPSK)", _numDGain);

            foreach (Control c in new Control[] { _cmbStandard, _cmbConst, _cmbSr, _cmbFec, _cmbFrame, _chkPilots })
                HookRecalc(c);
            _lockWhileRunning.AddRange(new Control[] { _cmbStandard, _cmbConst, _cmbSr, _cmbFec, _cmbFrame, _chkPilots });

            // Profiles (favorite-1..3.ini)
            var prof = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 74, BackColor = Theme.Surface, Padding = new Padding(0, 6, 0, 0), WrapContents = true };
            var profTitle = new Label { Text = "PROFILES", Width = 360, Height = 22, ForeColor = Theme.Muted, Font = Theme.Semi(8.5f), TextAlign = ContentAlignment.MiddleLeft };
            prof.Controls.Add(profTitle);
            prof.SetFlowBreak(profTitle, true);
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var load = Theme.Button("Load " + (i + 1));
                load.Size = new Size(64, 30);
                load.Click += (s, e) => LoadProfile(idx);
                prof.Controls.Add(load);
                _lockWhileRunning.Add(load);
            }
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var save = Theme.Button("Save " + (i + 1));
                save.Size = new Size(64, 30);
                save.ForeColor = Theme.Muted;
                save.Click += (s, e) => SaveProfile(idx);
                prof.Controls.Add(save);
            }

            card.Controls.Add(prof);
            card.Controls.Add(grid);
            card.Controls.Add(Theme.CardTitle("MODULATOR"));
            return card;
        }

        private Panel BuildTransmitCard()
        {
            var card = Theme.Card();
            card.Dock = DockStyle.Fill;

            var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Theme.Surface };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            _lblBigFreq = new Label { Text = "— MHz", Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Semi(22f), TextAlign = ContentAlignment.MiddleLeft };
            _lblSummary = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Ui(9f), TextAlign = ContentAlignment.TopLeft };
            _lblTs = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Semi(10f), TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface2, Margin = new Padding(0, 2, 4, 2), Padding = new Padding(8, 0, 0, 0) };
            _lblVideo = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Semi(10f), TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface2, Margin = new Padding(4, 2, 0, 2), Padding = new Padding(8, 0, 0, 0) };

            _btnStart = Theme.Button("START", true);
            _btnStart.Dock = DockStyle.Fill; _btnStart.Font = Theme.Semi(12f); _btnStart.Margin = new Padding(0, 6, 4, 6);
            _btnStart.Click += async (s, e) => await StartStopAsync();

            _btnPtt = Theme.Button("PTT OFF");
            _btnPtt.Dock = DockStyle.Fill; _btnPtt.Font = Theme.Semi(12f); _btnPtt.Margin = new Padding(4, 6, 0, 6);
            _btnPtt.Click += async (s, e) => await TogglePttAsync();

            _lblStats = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Ui(9f), TextAlign = ContentAlignment.MiddleLeft };
            _lblStatus = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = new Font("Consolas", 8.5f), TextAlign = ContentAlignment.TopLeft };

            _btnMonitor = Theme.Button("MONITOR STREAM");
            _btnMonitor.Dock = DockStyle.Fill; _btnMonitor.Margin = new Padding(0, 4, 0, 4);
            _btnMonitor.Click += (s, e) => OpenMonitor();

            // Longmynd receive
            var rxTitle = new Label { Text = "RECEIVE (PLUTO LONGMYND)", Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Semi(8.5f), TextAlign = ContentAlignment.BottomLeft };
            _chkRx = Theme.Check("Start Longmynd RX with TX");
            _cmbRxFreq = Theme.Combo(true);
            _cmbRxFreq.Items.Add("10491.50   (QO-100 beacon)");
            for (int i = 0; i < 28; i++) _cmbRxFreq.Items.Add((10492.75 + i * 0.25).ToString("0.00", Inv));
            _cmbRxFreq.Margin = new Padding(0, 3, 4, 3);
            _cmbRxSr = Theme.Combo(true);
            foreach (int sr in DvbCalc.SymbolRates) _cmbRxSr.Items.Add(sr.ToString(Inv));
            _cmbRxSr.Margin = new Padding(4, 3, 0, 3);
            _cmbRxFreq.SelectionChangeCommitted += (s, e) => BeginInvoke((Action)(async () => await RxCommitted()));
            _cmbRxSr.SelectionChangeCommitted += (s, e) => BeginInvoke((Action)(async () => await RxCommitted()));
            _btnPlayer = Theme.Button("OPEN RX PLAYER");
            _btnPlayer.Dock = DockStyle.Fill; _btnPlayer.Margin = new Padding(0, 4, 0, 4);
            _btnPlayer.Click += (s, e) => OpenRxPlayer();

            int r = 0;
            AddSpan(stack, _lblBigFreq, r++, 48);
            AddSpan(stack, _lblSummary, r++, 40);
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            stack.Controls.Add(_lblTs, 0, r); stack.Controls.Add(_lblVideo, 1, r); r++;
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            stack.Controls.Add(_btnStart, 0, r); stack.Controls.Add(_btnPtt, 1, r); r++;
            AddSpan(stack, _lblStats, r++, 26);
            AddSpan(stack, _btnMonitor, r++, 38);
            AddSpan(stack, rxTitle, r++, 28);
            AddSpan(stack, _chkRx, r++, 30);
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            stack.Controls.Add(_cmbRxFreq, 0, r); stack.Controls.Add(_cmbRxSr, 1, r); r++;
            AddSpan(stack, _btnPlayer, r++, 38);
            AddSpan(stack, _lblStatus, r++, 0, true);
            stack.RowCount = r;

            card.Controls.Add(stack);
            card.Controls.Add(Theme.CardTitle("TRANSMIT"));
            _lockWhileRunning.Add(_chkRx);
            return card;
        }

        private static void AddSpan(TableLayoutPanel t, Control c, int row, int height, bool fill = false)
        {
            t.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
            t.Controls.Add(c, 0, row);
            t.SetColumnSpan(c, 2);
        }

        private static Panel Inline(Control main, Control side)
        {
            var p = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3), BackColor = Theme.Surface };
            main.Dock = DockStyle.Fill;
            main.Margin = new Padding(0);
            var gap = new Panel { Dock = DockStyle.Right, Width = 4, BackColor = Theme.Surface };
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
            foreach (var c in Codecs.For(_s.Encoder)) _cmbCodec.Items.Add(c);
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
                GainDb = (double)_numGain.Value,
                RxEnabled = _chkRx.Checked,
                RxFrequencyMHz = FirstNumber(_cmbRxFreq.Text, 10491.5),
                RxSymbolRateKs = (int)FirstNumber(_cmbRxSr.Text, 1500)
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
                _numGain.Maximum = (decimal)Math.Min(0, _s.PowerLimitDb);
                _numGain.Value = Clamp((decimal)p.GainDb, _numGain.Minimum, _numGain.Maximum);
                _numDGain.Value = 0;
                _numDGain.Enabled = p.Constellation == "qpsk";
                _chkRx.Checked = p.RxEnabled;
                SelectOrText(_cmbRxFreq, p.RxFrequencyMHz.ToString("0.00", Inv));
                SelectOrText(_cmbRxSr, p.RxSymbolRateKs.ToString(Inv));
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
                _lblBigFreq.Text = p.TxFrequencyMHz.ToString("0.00", Inv) + " MHz";
                double ts = DvbCalc.TsBitrateK(p);
                var plan = EncodePlan.Build(p, _s, ts, false);
                ShowPlan(p, plan, false);
            }
            catch { }
        }

        private void ShowPlan(TxParams p, EncodePlan plan, bool live)
        {
            _lblTs.Text = "TS  " + plan.TsBitrateK.ToString("0", Inv) + " k" + (plan.TsFromDevice ? "  ✓" : "");
            _lblVideo.Text = "Video  " + plan.VideoBitrateK.ToString("0", Inv) + " k";
            _lblSummary.Text = string.Format(Inv, "{0} {1}  {2} KS  FEC {3}  {4}{5}\n{6} {7} @ {8:0.##} fps, audio {9} k",
                p.DvbS2 ? "DVB-S2" : "DVB-S", p.Constellation.ToUpperInvariant(), p.SymbolRateKs, p.Fec,
                p.ShortFrames ? "short" : "long", p.Pilots ? ", pilots" : "",
                p.Codec, p.ImageSize, p.Fps, p.AudioBitrateK);
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
            _numGain.Maximum = (decimal)Math.Min(0, _s.PowerLimitDb);
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
                _btnStart.Text = _s.Mode == RunMode.Encode ? "START" : _s.Mode == RunMode.Relay ? "START RELAY" : "START CONTROL";
                Theme.Paint(_btnStart, Theme.Accent, Theme.Accent);
            }

            _btnPtt.Enabled = _running && _tx.SupportsPtt && !_busy;
            if (!_tx.SupportsPtt) { _btnPtt.Text = "PTT N/A"; Theme.Paint(_btnPtt, Theme.Surface2, Theme.Border); }
            else if (_ptt) { _btnPtt.Text = "ON AIR"; Theme.Paint(_btnPtt, Theme.Red, Theme.RedBorder); }
            else { _btnPtt.Text = "PTT OFF"; Theme.Paint(_btnPtt, Theme.Surface2, Theme.Border); }

            foreach (var c in _lockWhileRunning) c.Enabled = !_running && !_busy;
            _btnSettings.Enabled = !_running && !_busy;
            _btnMonitor.Enabled = _s.Hardware == HardwareType.LocalTest;
            bool rx = _tx.SupportsLongmynd;
            _chkRx.Enabled = rx && !_running;
            _cmbRxFreq.Enabled = rx;
            _cmbRxSr.Enabled = rx;
            _btnPlayer.Enabled = rx;
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
            _lblBigFreq.Text = f.ToString("0.00", Inv) + " MHz";
            if (CanLive() && _active != null && Math.Abs(_active.TxFrequencyMHz - f) > 0.0001)
            {
                _active.TxFrequencyMHz = f;
                await Live(() => _tx.SetFrequencyAsync(f), "TX frequency " + f.ToString("0.000", Inv) + " MHz");
            }
        }

        private async Task RxCommitted()
        {
            if (!CanLive() || _active == null || !_active.RxEnabled) return;
            _active.RxFrequencyMHz = FirstNumber(_cmbRxFreq.Text, _active.RxFrequencyMHz);
            _active.RxSymbolRateKs = (int)FirstNumber(_cmbRxSr.Text, _active.RxSymbolRateKs);
            await Live(() => _tx.SetLongmyndAsync(_active), "Longmynd " + _active.RxFrequencyMHz.ToString("0.00", Inv) + " MHz " + _active.RxSymbolRateKs + " KS");
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
            if (_s.Mode == RunMode.ControlOnly && !(_tx is PlutoMqttTransmitter || _tx is SdrHelperTransmitter)) return "Control-only mode needs a Pluto with F5OEO MQTT firmware, a LimeSDR or a HackRF.";
            if (_s.Mode == RunMode.Relay && !_tx.SupportsLongmynd) return "Relay mode needs a Pluto with Longmynd.";
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

                if ((p.RxEnabled || _s.Mode == RunMode.Relay) && _tx.SupportsLongmynd)
                {
                    await _tx.StartLongmyndAsync(p);
                    if (_s.Mode != RunMode.Relay) OpenRxPlayer();
                }

                if (_s.Mode == RunMode.Encode)
                {
                    string url = _tx.StreamUrl();
                    string args = FfmpegBuilder.BuildArgs(p, _s, plan, url);
                    AppendLog("ffmpeg " + args);
                    _ffmpeg.Start(_s.FfmpegExe, args);
                    // Most ffmpeg problems (device busy, bad option) make it exit straight away.
                    await Task.Delay(2000);
                    if (!_ffmpeg.IsRunning)
                        throw new InvalidOperationException("the encoder stopped straight away - see the ffmpeg message above.");
                    AppendLog("Encoder running -> " + url);
                }
                else
                {
                    AppendLog(_s.Mode == RunMode.Relay ? "Relay running." : "Control mode: start your external encoder (e.g. OBS) sending MPEG-TS to " + _tx.StreamUrl().Split('?')[0] + " at no more than " + plan.TsBitrateK.ToString("0", Inv) + " kbit/s.");
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

        private void OpenRxPlayer()
        {
            string url = "udp://@" + _s.RxOutputIp + ":" + _s.RxOutputPort;
            StartPlayer(url, "Longmynd RX");
        }

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

        private void LoadProfile(int i)
        {
            var p = _s.Profiles[i];
            if (p == null) { AppendLog("Profile " + (i + 1) + " is empty. Set things up and press Save " + (i + 1) + "."); return; }
            ApplyParams(p);
            AppendLog("Loaded profile " + (i + 1) + ": " + p.Summary());
        }

        private void SaveProfile(int i)
        {
            var p = ReadParams();
            _s.Profiles[i] = p;
            try { _s.Save(); AppendLog("Saved profile " + (i + 1) + ": " + p.Summary()); }
            catch (Exception ex) { AppendLog("Could not save: " + ex.Message); }
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
                _s = f.Result;
                try { _s.Save(); } catch (Exception ex) { AppendLog("Could not save settings: " + ex.Message); }
            }
            CreateTransmitter();
            ApplyParams(current);
            AppendLog("Settings saved. Transmitter: " + _tx.DisplayName);
        }

        // ================================================================== misc

        private void RefreshLive()
        {
            var st = _lastStats;
            if (_running && st != null)
            {
                _lblStats.Text = string.Format(Inv, "Encoder  {0:0.#} fps   {1}   speed {2}{3}", st.Fps, st.Bitrate, st.Speed, st.Dropped > 0 ? "   dropped " + st.Dropped : "");
                _lblStats.ForeColor = st.Speed.StartsWith("0.", StringComparison.Ordinal) ? Theme.Amber : Theme.Muted;
            }
            else if (_running && _s.Mode != RunMode.Encode) _lblStats.Text = _s.Mode == RunMode.Relay ? "Relay running" : "Control mode running";

            if (_telemetry.Count > 0)
            {
                var lines = _telemetry.OrderBy(k => k.Key).Take(12).Select(k => (k.Key.Length > 26 ? k.Key.Substring(k.Key.Length - 26) : k.Key).PadRight(27) + k.Value);
                _lblStatus.Text = "RADIO STATUS\n" + string.Join("\n", lines);
            }
            else _lblStatus.Text = "";
        }

        private void AppendLog(string line)
        {
            if (_log == null) return;
            if (_log.TextLength > 60000) _log.Text = _log.Text.Substring(_log.TextLength - 30000);
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine);
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
            try { _s.LastRun = ReadParams(); _s.Save(); } catch { }
            _uiTimer.Stop();
            ClosePlayer();
            _ffmpeg.Dispose();
            if (_tx != null) { try { await _tx.DisconnectAsync(); } catch { } _tx.Dispose(); }
        }
    }
}
