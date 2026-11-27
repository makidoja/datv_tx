using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DatvTx.Core;
using DatvTx.Hardware;

namespace DatvTx.LinuxUi
{
    /// <summary>
    /// The Linux main window: same layout and behaviour as the Windows MainForm (header, QO-100
    /// spectrum, DVB / SOURCE / MEMORIES / STATUS tabs, transmit column with traffic light, PTT and
    /// power slider, compact mode and ON TOP), on the shared Core / Hardware code.
    /// </summary>
    public sealed class MainWindow : Window
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const string ProductTitle = "DATV TX (M0CKE)";

        private AppSettings _s;
        private Transmitter _tx;
        private readonly FfmpegRunner _ffmpeg = new FfmpegRunner();
        private Process _player;
        private bool _running, _busy, _ptt, _loading, _closing;
        private TxParams _active;
        private readonly ConcurrentDictionary<string, string> _telemetry = new ConcurrentDictionary<string, string>();
        private EncoderStats _lastStats;
        private DateTime? _slowSince;
        private readonly DispatcherTimer _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly DispatcherTimer _gainTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private long _lastUnderruns = -1;
        private readonly bool _demo;

        // header
        private TextBlock _lblHardware, _lblHelp, _lblCredit;
        private Button _btnConnect, _btnSettings, _btnSpectrum, _lnkUpdate;
        private string _updateUrl;

        // layout
        private Grid _root, _body;
        private Control _header, _tabsCard, _logCard, _txCard, _spectrumCard;
        private SpectrumView _spectrum;
        private Button _btnCompact, _btnTop;
        private bool _compact;

        // tabs
        private readonly List<Button> _tabButtons = new List<Button>();
        private readonly List<Control> _tabPages = new List<Control>();

        // source & encoder
        private ComboBox _cmbInput, _cmbCodec, _cmbPreset, _cmbChannels, _cmbAbr;
        private EditCombo _cmbVideo, _cmbAudio, _cmbSize, _cmbFps;
        private TextBox _txtFile;
        private Button _btnRefresh, _btnBrowse;

        // modulator
        private ComboBox _cmbStandard, _cmbConst, _cmbFec, _cmbFrame, _cmbRollOff;
        private EditCombo _cmbSr, _cmbFreq;
        private CheckBox _chkPilots, _chkSmooth;
        private NumericUpDown _numDGain;
        private TextBlock _lblTs, _lblVideo, _lblVideoTitle;

        // memories
        private ListBox _lstMem;
        private TextBox _txtMemName;

        // transmit column
        private double _dbb = double.NaN;
        private TextBlock _lblDbb, _lblDownlink, _lblSummary, _lblStats, _lblStatus, _lblPower;
        private Border _dbbBox;
        private TrafficLightView _light;
        private Slider _trkPower;
        private Button _btnStart, _btnPtt, _btnMonitor;

        private TextBox _log;
        private readonly List<Control> _lockWhileRunning = new List<Control>();

        public MainWindow() : this(false) { }

        public MainWindow(bool demo)
        {
            _demo = demo;
            _s = demo ? new AppSettings() : AppSettings.Load();
            if (demo) _s.Normalise();

            Title = ProductTitle + "  v" + Program.Version;
            Icon = LTheme.AppIcon();
            Background = LTheme.B(LTheme.Bg);
            Foreground = LTheme.B(LTheme.Text);
            MinWidth = 960; MinHeight = 820;
            Width = Math.Max(960, _s.NormalWidth); Height = Math.Max(820, _s.NormalHeight);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            BuildUi();
            CreateTransmitter();
            ApplyParams(_s.LastRun ?? new TxParams());
            RefreshMemories();
            ShowSpectrum(_s.ShowSpectrum);
            if (_s.CompactMode && !demo) SetCompact(true);
            ApplyTopMost();

            _ffmpeg.Log += l => UI(() =>
            {
                AppendLog(l);
                if (l.IndexOf("required nvenc API version", StringComparison.OrdinalIgnoreCase) >= 0)
                    AppendLog(">> Your NVIDIA driver is older than this FFmpeg needs. Update the driver, or set Settings → FFmpeg folder to another FFmpeg build.");
                else if (l.IndexOf("Cannot load libcuda", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("No capable devices found", StringComparison.OrdinalIgnoreCase) >= 0)
                    AppendLog(">> No usable NVIDIA GPU found - choose a CPU codec, or install the NVIDIA driver.");
                else if (l.IndexOf("Device or resource busy", StringComparison.OrdinalIgnoreCase) >= 0)
                    AppendLog(">> The camera is in use by another program (OBS, a browser, a video call). Close it, or use OBS's Virtual Camera here.");
                else if (l.IndexOf("Unknown input format", StringComparison.OrdinalIgnoreCase) >= 0 && l.IndexOf("pulse", StringComparison.OrdinalIgnoreCase) >= 0)
                    AppendLog(">> This FFmpeg has no PulseAudio input. Install the distribution's FFmpeg (sudo apt install ffmpeg) and leave Settings → FFmpeg folder blank.");
                else if (l.IndexOf("not supported", StringComparison.OrdinalIgnoreCase) >= 0 && (l.IndexOf("YUV", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("pixel format", StringComparison.OrdinalIgnoreCase) >= 0))
                    AppendLog(">> The camera's picture format isn't accepted by this encoder - try a CPU codec (libx265/libx264).");
            });
            _ffmpeg.Stats += st => { _lastStats = st; };
            _ffmpeg.Exited += code => UI(() => OnEncoderExited(code));

            _uiTimer.Tick += (s, e) => RefreshLive();
            _uiTimer.Start();
            _gainTimer.Tick += async (s, e) => { _gainTimer.Stop(); await GainCommitted(); };

            Opened += async (s, e) =>
            {
                if (_demo) return;
                await RefreshDevicesAsync();
                await CheckForUpdateAsync();
            };
            Closing += OnClosing;
        }

        // ================================================================== layout

        private void BuildUi()
        {
            _root = new Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("70,200,*,120") };

            _header = BuildHeader();
            Grid.SetRow(_header, 0);
            _root.Children.Add(_header);

            _spectrum = new SpectrumView();
            _spectrum.ChannelPicked += (down, sr) => UI(async () => await ChannelPicked(down, sr));
            _spectrum.LevelUpdated += v => UI(() => { _dbb = v; ShowDbb(); });
            var specTitle = LTheme.Label("QO-100 WIDEBAND  •  BATC live spectrum  •  click a channel to set the TX frequency", 11.5, true);
            specTitle.Margin = new Thickness(6, 0, 0, 2);
            var specDock = new DockPanel();
            DockPanel.SetDock(specTitle, Dock.Top);
            specDock.Children.Add(specTitle);
            specDock.Children.Add(_spectrum);
            _spectrumCard = LTheme.Card(specDock, new Thickness(6, 4));
            _spectrumCard.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(_spectrumCard, 1);
            _root.Children.Add(_spectrumCard);

            _body = new Grid { Margin = new Thickness(0, 10), ColumnDefinitions = new ColumnDefinitions("*,300") };
            Grid.SetRow(_body, 2);
            _root.Children.Add(_body);

            _tabsCard = BuildTabs();
            _tabsCard.Margin = new Thickness(0, 0, 6, 0);
            _body.Children.Add(_tabsCard);
            _txCard = BuildTransmitColumn();
            _txCard.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(_txCard, 1);
            _body.Children.Add(_txCard);

            _log = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Background = LTheme.B(LTheme.LogBg),
                Foreground = LTheme.B(LTheme.Muted),
                BorderThickness = new Thickness(0),
                FontFamily = LTheme.Mono,
                FontSize = 11.5
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_log, ScrollBarVisibility.Auto);
            _logCard = LTheme.Card(_log, new Thickness(10, 6, 10, 8));
            Grid.SetRow(_logCard, 3);
            _root.Children.Add(_logCard);

            Content = _root;
        }

        private Control BuildHeader()
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*,Auto") };

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            left.Children.Add(LTheme.Label("◉  " + ProductTitle, 19, true, LTheme.Text));
            var creditRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 2, 0, 0) };
            _lblCredit = LTheme.Label("v" + Program.Version + "  •  based on DATV-NotSoEasy by DL5OCD", 11.5);
            _lnkUpdate = new Button
            {
                IsVisible = false, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                Foreground = LTheme.B(LTheme.Amber), FontWeight = FontWeight.SemiBold, FontSize = 11.5, Cursor = new Cursor(StandardCursorType.Hand)
            };
            _lnkUpdate.Click += (s, e) => OpenUrl(_updateUrl ?? UpdateChecker.ReleasesPage);
            creditRow.Children.Add(_lblCredit);
            creditRow.Children.Add(_lnkUpdate);
            left.Children.Add(creditRow);
            g.Children.Add(left);

            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0) };
            _lblHardware = LTheme.Label("", 13.5, true, LTheme.Text);
            _lblHelp = LTheme.Label("", 11.5);
            _lblHelp.MaxHeight = 34;
            mid.Children.Add(_lblHardware);
            mid.Children.Add(_lblHelp);
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            _btnSpectrum = LTheme.Button("SPECTRUM"); _btnSpectrum.Width = 124; _btnSpectrum.Height = 38;
            _btnSpectrum.Click += (s, e) => ShowSpectrum(!_s.ShowSpectrum);
            _btnConnect = LTheme.Button("CONNECT", true); _btnConnect.Width = 130; _btnConnect.Height = 38;
            _btnConnect.Click += async (s, e) => await ToggleConnectionAsync();
            _btnSettings = LTheme.Button("SETTINGS"); _btnSettings.Width = 100; _btnSettings.Height = 38;
            _btnSettings.Click += async (s, e) => await OpenSettings();
            right.Children.Add(_btnSpectrum); right.Children.Add(_btnConnect); right.Children.Add(_btnSettings);
            Grid.SetColumn(right, 2);
            g.Children.Add(right);

            return LTheme.Card(g, new Thickness(12, 6));
        }

        private void ShowSpectrum(bool show)
        {
            _s.ShowSpectrum = show;
            bool visible = show && !_compact;
            _spectrumCard.IsVisible = visible;
            _root.RowDefinitions[1].Height = visible ? new GridLength(200) : new GridLength(0);
            _btnSpectrum.Content = show ? "HIDE SPECTRUM" : "SPECTRUM";
            if (_demo) { _spectrum.ShowDemo(); return; }
            if (show) _spectrum.Start(); else _spectrum.Stop();
        }

        private void ApplyTopMost()
        {
            Topmost = _s.AlwaysOnTop; // the ON TOP button decides, in full and compact view
            if (_btnTop != null)
                LTheme.Paint(_btnTop, _s.AlwaysOnTop ? LTheme.Accent : LTheme.Surface2, _s.AlwaysOnTop ? LTheme.Accent : LTheme.Border);
        }

        private void SetCompact(bool compact)
        {
            if (compact == _compact) return;
            if (WindowState == WindowState.Normal)
            {
                if (compact) { _s.NormalWidth = (int)Width; _s.NormalHeight = (int)Height; }
                else { _s.CompactWidth = (int)Width; _s.CompactHeight = (int)Height; }
            }
            _compact = compact;
            _s.CompactMode = compact;
            _header.IsVisible = !compact;
            _tabsCard.IsVisible = !compact;
            _logCard.IsVisible = !compact;
            _root.RowDefinitions[0].Height = compact ? new GridLength(0) : new GridLength(70);
            _root.RowDefinitions[3].Height = compact ? new GridLength(0) : new GridLength(120);
            _root.Margin = compact ? new Thickness(4) : new Thickness(12);
            _body.Margin = compact ? new Thickness(0) : new Thickness(0, 10);
            _body.ColumnDefinitions[0].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            _body.ColumnDefinitions[1].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(300);
            _txCard.Margin = compact ? new Thickness(0) : new Thickness(6, 0, 0, 0);
            ShowSpectrum(_s.ShowSpectrum);
            _btnCompact.Content = compact ? "FULL VIEW" : "COMPACT";
            ApplyTopMost();

            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            MinWidth = compact ? 300 : 960;
            MinHeight = compact ? 560 : 820;
            Width = compact ? Math.Max(300, _s.CompactWidth) : Math.Max(960, _s.NormalWidth);
            Height = compact ? Math.Max(560, _s.CompactHeight) : Math.Max(820, _s.NormalHeight);
            try { _s.Save(); } catch { }
        }

        // ------------------------------------------------------------------ tabs

        private Control BuildTabs()
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 12) };
            var host = new Panel();
            AddTab(strip, host, "DVB", BuildDvbPage());
            AddTab(strip, host, "SOURCE", BuildSourcePage());
            AddTab(strip, host, "MEMORIES", BuildMemoriesPage());
            AddTab(strip, host, "STATUS", BuildStatusPage());
            var dock = new DockPanel();
            DockPanel.SetDock(strip, Dock.Top);
            dock.Children.Add(strip);
            dock.Children.Add(host);
            SelectTab(0);
            return LTheme.Card(dock, new Thickness(12, 8, 12, 10));
        }

        private void AddTab(StackPanel strip, Panel host, string name, Control page)
        {
            int index = _tabButtons.Count;
            var b = LTheme.Button(name);
            b.Width = 104; b.Height = 32;
            b.Click += (s, e) => SelectTab(index);
            strip.Children.Add(b);
            _tabButtons.Add(b);
            page.IsVisible = false;
            host.Children.Add(page);
            _tabPages.Add(page);
        }

        private void SelectTab(int index)
        {
            for (int i = 0; i < _tabPages.Count; i++)
            {
                bool on = i == index;
                _tabPages[i].IsVisible = on;
                LTheme.Paint(_tabButtons[i], on ? LTheme.Accent : LTheme.Surface2, on ? LTheme.Accent : LTheme.Border);
            }
        }

        public void SelectTabForScreenshot(int i) { SelectTab(i); }

        private static TextBlock GroupTitle(string text)
        {
            var t = LTheme.Label(text, 13, true, LTheme.Text);
            t.Margin = new Thickness(0, 6, 0, 6);
            return t;
        }

        private Control BuildDvbPage()
        {
            var page = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("62,*") };

            var tsBox = BitrateBox("Target TS bitrate", out _lblTs, out _);
            var vBox = BitrateBox("Video bitrate", out _lblVideo, out _lblVideoTitle);
            tsBox.Margin = new Thickness(0, 0, 10, 6);
            vBox.Margin = new Thickness(10, 0, 0, 6);
            Grid.SetColumn(vBox, 1);
            page.Children.Add(tsBox);
            page.Children.Add(vBox);

            // ---- MODE
            var grid = new LTheme.FieldGrid(120);
            _cmbStandard = LTheme.Combo();
            foreach (var x in new[] { "DVB-S2", "DVB-S" }) _cmbStandard.Items.Add(x);
            _cmbStandard.SelectionChanged += (s, e) => { if (!_loading) RefillFec(); };
            grid.Add("Standard", _cmbStandard);

            _cmbConst = LTheme.Combo();
            foreach (var x in new[] { "QPSK", "8PSK", "16APSK", "32APSK" }) _cmbConst.Items.Add(x);
            _cmbConst.SelectionChanged += (s, e) => { if (!_loading) RefillFec(); if (_numDGain != null) _numDGain.IsEnabled = Constellation() == "qpsk"; };
            grid.Add("Constellation", _cmbConst);

            _cmbSr = LTheme.Edit();
            foreach (int sr in DvbCalc.SymbolRates) _cmbSr.Items.Add(sr.ToString(Inv));
            grid.Add("Symbol rate (KS)", _cmbSr);

            _cmbFec = LTheme.Combo();
            grid.Add("FEC", _cmbFec);

            _cmbFrame = LTheme.Combo();
            foreach (var x in new[] { "Long (64800)", "Short (16200)" }) _cmbFrame.Items.Add(x);
            grid.Add("FEC frame", _cmbFrame);

            _cmbRollOff = LTheme.Combo();
            _cmbRollOff.SelectionChanged += async (s, e) => { if (CanLive()) await Live(() => _tx.SetRollOffAsync(SelectedRollOff()), "Roll-off " + CText(_cmbRollOff)); };
            grid.Add("Roll-off", _cmbRollOff);

            _chkPilots = LTheme.Check("Pilot symbols");
            grid.Add("", _chkPilots);

            _numDGain = new NumericUpDown { Minimum = -20, Maximum = 3, Increment = 0.25m, FormatString = "0.00", Value = 0, Background = LTheme.B(LTheme.Surface2), Foreground = LTheme.B(LTheme.Text), BorderBrush = LTheme.B(LTheme.Border) };
            _numDGain.ValueChanged += async (s, e) => { if (CanLive()) await Live(() => _tx.SetDigitalGainAsync((double)(_numDGain.Value ?? 0)), "Digital gain " + (_numDGain.Value ?? 0).ToString("0.00", Inv) + " dB"); };
            grid.Add("Digital gain (dB)", _numDGain);

            var mode = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            mode.Children.Add(GroupTitle("MODE"));
            mode.Children.Add(grid.Grid);
            Grid.SetRow(mode, 1);
            page.Children.Add(mode);

            // ---- VIDEO / AUDIO
            var g2 = new LTheme.FieldGrid(110);
            _cmbCodec = LTheme.Combo();
            g2.Add("Codec", _cmbCodec);
            _cmbPreset = LTheme.Combo();
            foreach (var x in Codecs.Presets) _cmbPreset.Items.Add(x);
            g2.Add("Speed preset", _cmbPreset);
            _cmbSize = LTheme.Edit();
            foreach (var x in new[] { "640x360", "960x540", "1280x720", "1920x1080", "2560x1440" }) _cmbSize.Items.Add(x);
            g2.Add("Image size", _cmbSize);
            _cmbFps = LTheme.Edit();
            foreach (var x in new[] { "10", "12.5", "15", "20", "25", "30", "48", "50", "60" }) _cmbFps.Items.Add(x);
            g2.Add("Frame rate", _cmbFps);
            _chkSmooth = LTheme.Check("Smooth motion at 10 fps and below");
            g2.Add("", _chkSmooth);
            _cmbChannels = LTheme.Combo();
            foreach (var x in new[] { "Mono", "Stereo", "5.1" }) _cmbChannels.Items.Add(x); // 7.1 AAC can't be carried in a DVB TS
            g2.Add("Audio", _cmbChannels);
            _cmbAbr = LTheme.Combo();
            foreach (var x in new[] { "8", "16", "32", "48", "64", "96", "128", "192", "256" }) _cmbAbr.Items.Add(x);
            g2.Add("Audio kbit/s", _cmbAbr);

            var av = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            av.Children.Add(GroupTitle("VIDEO / AUDIO"));
            av.Children.Add(g2.Grid);
            Grid.SetRow(av, 1); Grid.SetColumn(av, 1);
            page.Children.Add(av);

            foreach (var c in new Control[] { _cmbStandard, _cmbConst, _cmbSr, _cmbFec, _cmbFrame, _chkPilots, _chkSmooth, _cmbCodec, _cmbPreset, _cmbSize, _cmbFps, _cmbChannels, _cmbAbr })
                HookRecalc(c);
            _lockWhileRunning.AddRange(new Control[] { _cmbStandard, _cmbConst, _cmbSr, _cmbFec, _cmbFrame, _chkPilots, _chkSmooth, _cmbCodec, _cmbPreset, _cmbSize, _cmbFps, _cmbChannels, _cmbAbr });
            return new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }

        private static Control BitrateBox(string title, out TextBlock value, out TextBlock titleLabel)
        {
            titleLabel = LTheme.Label(title, 11.5);
            titleLabel.TextTrimming = TextTrimming.CharacterEllipsis;
            titleLabel.TextWrapping = TextWrapping.NoWrap;
            value = LTheme.Label("", 16, true, LTheme.Text);
            value.HorizontalAlignment = HorizontalAlignment.Center;
            var box = new Border { Background = LTheme.B(LTheme.Surface2), Child = value, Height = 36, Margin = new Thickness(0, 4, 0, 0) };
            var sp = new StackPanel();
            sp.Children.Add(titleLabel);
            sp.Children.Add(box);
            return sp;
        }

        private Control BuildSourcePage()
        {
            var grid = new LTheme.FieldGrid(130);
            _cmbInput = LTheme.Combo();
            foreach (var x in new[] { "Capture device (OBS Virtual Camera / camera)", "Network UDP", "Network RTMP", "Video file" }) _cmbInput.Items.Add(x);
            _cmbInput.SelectionChanged += (s, e) => UpdateInputVisibility();
            grid.Add("Input", _cmbInput);

            _cmbVideo = LTheme.Edit();
            _btnRefresh = LTheme.Button("↻"); _btnRefresh.Width = 34;
            _btnRefresh.Click += async (s, e) => await RefreshDevicesAsync();
            grid.Add("Video device", Inline(_cmbVideo, _btnRefresh));

            _cmbAudio = LTheme.Edit();
            grid.Add("Audio device", _cmbAudio);

            _txtFile = LTheme.TextBox();
            _btnBrowse = LTheme.Button("…"); _btnBrowse.Width = 34;
            _btnBrowse.Click += async (s, e) => await BrowseFile();
            grid.Add("Video file", Inline(_txtFile, _btnBrowse));

            var hint = LTheme.Label(
                "OBS: install v4l2loopback (sudo apt install v4l2loopback-dkms), then Tools → Start Virtual Camera and choose \"OBS Virtual Camera\" here.\n" +
                "Audio: pick a microphone, or a \"Monitor of …\" source to send what the PC is playing (OBS monitoring, a browser).\n" +
                "Network UDP / RTMP and the file loop are set in Settings → Input. Press ↻ after plugging in a device.", 11.5);
            hint.Margin = new Thickness(0, 12, 0, 0);

            var page = new StackPanel();
            page.Children.Add(GroupTitle("INPUT"));
            page.Children.Add(grid.Grid);
            page.Children.Add(hint);
            _lockWhileRunning.AddRange(new Control[] { _cmbInput, _cmbVideo, _cmbAudio, _btnRefresh, _txtFile, _btnBrowse });
            return page;
        }

        private static Control Inline(Control main, Control side)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,Auto") };
            Grid.SetColumn(side, 2);
            g.Children.Add(main);
            g.Children.Add(side);
            return g;
        }

        private Control BuildMemoriesPage()
        {
            _lstMem = new ListBox { Background = LTheme.B(LTheme.Surface2), Foreground = LTheme.B(LTheme.Text) };
            _lstMem.ItemTemplate = new FuncDataTemplate<TxMemory>((m, ns) =>
            {
                var sp = new StackPanel { Margin = new Thickness(2, 1) };
                if (m == null) return sp;
                sp.Children.Add(LTheme.Label(m.Name, 13.5, true, LTheme.Text));
                var p = m.Params;
                string detail = p == null ? "" : string.Format(Inv, "{0:0.00} MHz   {1} {2}  {3} KS  FEC {4}   {5}  {6}  {7:0.##} fps   {8:0.##} dB",
                    p.TxFrequencyMHz, p.DvbS2 ? "DVB-S2" : "DVB-S", p.Constellation.ToUpperInvariant(), p.SymbolRateKs, p.Fec, p.Codec, p.ImageSize, p.Fps, p.GainDb);
                sp.Children.Add(LTheme.Label(detail, 11.5));
                return sp;
            });
            _lstMem.DoubleTapped += (s, e) => RecallMemory();
            _lstMem.SelectionChanged += (s, e) => { var m = _lstMem.SelectedItem as TxMemory; if (m != null) _txtMemName.Text = m.Name; };

            _txtMemName = LTheme.TextBox();
            var recall = LTheme.Button("RECALL", true);
            var store = LTheme.Button("STORE NEW");
            var over = LTheme.Button("OVERWRITE");
            var del = LTheme.Button("DELETE");
            recall.Click += (s, e) => RecallMemory();
            store.Click += (s, e) => StoreMemory(false);
            over.Click += (s, e) => StoreMemory(true);
            del.Click += async (s, e) => await DeleteMemory();
            var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,96,96,96,96"), Height = 38, Margin = new Thickness(0, 8, 0, 0) };
            int col = 0;
            foreach (Control c in new Control[] { _txtMemName, recall, store, over, del })
            {
                c.Margin = new Thickness(0, 2, col == 4 ? 0 : 6, 2);
                Grid.SetColumn(c, col++);
                bar.Children.Add(c);
            }

            var hint = LTheme.Label("Name a set-up and press STORE NEW. Double-click or RECALL to load it (frequency, mode, SR, FEC, codec, power).", 11.5);
            var top = new StackPanel();
            top.Children.Add(GroupTitle("MEMORIES"));
            top.Children.Add(hint);
            var dock = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bar, Dock.Bottom);
            dock.Children.Add(top);
            dock.Children.Add(bar);
            dock.Children.Add(_lstMem);
            _lstMem.Margin = new Thickness(0, 8, 0, 0);
            _lockWhileRunning.Add(recall);
            return dock;
        }

        private Control BuildStatusPage()
        {
            _lblStatus = new TextBlock { Text = "No radio status yet.", FontFamily = LTheme.Mono, FontSize = 12, Foreground = LTheme.B(LTheme.Muted) };
            var logBtn = LTheme.Button("OPEN LOG FOLDER");
            logBtn.Height = 30;
            logBtn.Click += (o, e) => { try { Directory.CreateDirectory(LogFolder); OpenUrl(LogFolder); } catch { } };
            var dock = new DockPanel();
            var title = GroupTitle("RADIO / MODULATOR STATUS");
            DockPanel.SetDock(title, Dock.Top);
            DockPanel.SetDock(logBtn, Dock.Bottom);
            dock.Children.Add(title);
            dock.Children.Add(logBtn);
            dock.Children.Add(new ScrollViewer { Content = _lblStatus });
            return dock;
        }

        // ------------------------------------------------------------------ transmit column

        private Control BuildTransmitColumn()
        {
            var stack = new StackPanel();

            var freqLabel = LTheme.Label("FREQUENCY MHz", 11.5, true);
            freqLabel.Margin = new Thickness(0, 0, 0, 2);
            stack.Children.Add(freqLabel);

            _cmbFreq = LTheme.Edit();
            _cmbFreq.Box.FontSize = 20;
            _cmbFreq.Box.FontWeight = FontWeight.SemiBold;
            _cmbFreq.Height = 40;
            for (int i = 0; i < 28; i++) _cmbFreq.Items.Add((2403.25 + i * 0.25).ToString("0.00", Inv));
            _cmbFreq.SelectionChanged += (s, e) => { if (!_loading) UI(async () => await FrequencyCommitted()); };
            _cmbFreq.Box.KeyDown += async (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; await FrequencyCommitted(); } };
            _cmbFreq.Box.LostFocus += async (s, e) => await FrequencyCommitted();
            stack.Children.Add(_cmbFreq);

            _lblDownlink = LTheme.Label("", 12);
            _lblDownlink.Margin = new Thickness(0, 4, 0, 4);
            stack.Children.Add(_lblDownlink);

            _lblDbb = LTheme.Label("", 13.5, true);
            _dbbBox = new Border { Background = LTheme.B(LTheme.Surface2), Padding = new Thickness(6, 4), Child = _lblDbb, Margin = new Thickness(0, 2, 0, 6) };
            stack.Children.Add(_dbbBox);

            stack.Children.Add(LTheme.Label("TRANSMISSION", 11.5, true));
            _light = new TrafficLightView { Margin = new Thickness(0, 4, 0, 0) };
            stack.Children.Add(_light);

            _lblStats = LTheme.Label("", 11.5);
            _lblStats.MinHeight = 22;
            stack.Children.Add(_lblStats);

            _btnStart = LTheme.Button("START", true);
            _btnStart.FontSize = 17; _btnStart.Height = 46; _btnStart.Margin = new Thickness(0, 4);
            _btnStart.Click += async (s, e) => await StartStopAsync();
            stack.Children.Add(_btnStart);

            _btnPtt = LTheme.Button("PTT");
            _btnPtt.FontSize = 17; _btnPtt.Height = 46; _btnPtt.Margin = new Thickness(0, 4);
            _btnPtt.Click += async (s, e) => await TogglePttAsync();
            stack.Children.Add(_btnPtt);

            _btnMonitor = LTheme.Button("MONITOR STREAM");
            _btnMonitor.Height = 32; _btnMonitor.Margin = new Thickness(0, 4);
            _btnMonitor.Click += (s, e) => OpenMonitor();
            stack.Children.Add(_btnMonitor);

            _lblSummary = LTheme.Label("", 11.5);
            _lblSummary.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(_lblSummary);

            // power slider (DATV-Easy style)
            _trkPower = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = -200, Maximum = 0,
                TickFrequency = 20, SmallChange = 1, LargeChange = 4,
                TickPlacement = TickPlacement.Outside,
                IsSnapToTickEnabled = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _trkPower.PropertyChanged += (s, e) =>
            {
                if (e.Property != RangeBase.ValueProperty) return;
                double snapped = Math.Round(_trkPower.Value);
                if (snapped != _trkPower.Value) { _trkPower.Value = snapped; return; }
                _lblPower.Text = SliderGainDb().ToString("0.00", Inv) + "\ndB";
                if (CanLive()) { _gainTimer.Stop(); _gainTimer.Start(); }
            };
            _lblPower = LTheme.Label("", 11, true, LTheme.Text);
            _lblPower.TextAlignment = TextAlignment.Center;
            _lblPower.HorizontalAlignment = HorizontalAlignment.Center;
            var powerBox = new Border { Background = LTheme.B(LTheme.Surface2), Child = _lblPower, Height = 40, Padding = new Thickness(2) };
            var powerTitle = LTheme.Label("POWER", 10, true);
            powerTitle.HorizontalAlignment = HorizontalAlignment.Center;
            var power = new Grid { RowDefinitions = new RowDefinitions("22,*,40"), Margin = new Thickness(4, 0, 0, 0) };
            Grid.SetRow(_trkPower, 1);
            Grid.SetRow(powerBox, 2);
            power.Children.Add(powerTitle);
            power.Children.Add(_trkPower);
            power.Children.Add(powerBox);

            var cols = new Grid { ColumnDefinitions = new ColumnDefinitions("*,62") };
            cols.Children.Add(new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Grid.SetColumn(power, 1);
            cols.Children.Add(power);

            var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,6,Auto"), Height = 32, Margin = new Thickness(0, 0, 0, 6) };
            titleRow.Children.Add(LTheme.Label("TRANSMIT", 13.5, true, LTheme.Text));
            _btnTop = LTheme.Button("ON TOP"); _btnTop.FontSize = 11; _btnTop.Width = 66;
            _btnTop.Click += (s, e) => { _s.AlwaysOnTop = !_s.AlwaysOnTop; ApplyTopMost(); try { _s.Save(); } catch { } };
            _btnCompact = LTheme.Button("COMPACT"); _btnCompact.FontSize = 11; _btnCompact.Width = 84;
            _btnCompact.Click += (s, e) => SetCompact(!_compact);
            Grid.SetColumn(_btnTop, 1);
            Grid.SetColumn(_btnCompact, 3);
            titleRow.Children.Add(_btnTop);
            titleRow.Children.Add(_btnCompact);

            var dock = new DockPanel();
            DockPanel.SetDock(titleRow, Dock.Top);
            dock.Children.Add(titleRow);
            dock.Children.Add(cols);
            return LTheme.Card(dock, new Thickness(10, 8, 6, 10));
        }

        public void ToggleCompactForScreenshot() { SetCompact(!_compact); }

        private double SliderGainDb() { return _trkPower.Value / 4.0; }

        /// <summary>Slider range for the selected hardware: its lowest power (ESP32-C3: -15 dB) up to the power limit.</summary>
        private void ApplyPowerRange()
        {
            double min = Math.Round((_tx != null ? _tx.MinGainDb : -50) * 4);
            double max = Math.Max(min + 4, Math.Round(Math.Min(0, _s.PowerLimitDb) * 4));
            double v = Math.Max(min, Math.Min(max, _trkPower.Value));
            _trkPower.Minimum = min;
            _trkPower.Maximum = max;
            _trkPower.Value = v;
            _trkPower.TickFrequency = max - min > 80 ? 20 : 4;
            if (_lblPower != null) _lblPower.Text = SliderGainDb().ToString("0.00", Inv) + "\ndB";
        }

        private void SetGainSlider(double db)
        {
            double v = Math.Round(db * 4);
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
            double up = downlinkMHz - SpectrumView.UplinkOffsetMHz;
            if (srKs > 0)
            {
                if (_running && _active != null && _active.SymbolRateKs != srKs)
                    AppendLog("That channel is for " + srKs + " KS. Stop and START again to change the symbol rate.");
                else if (!_running)
                    SelectOrText(_cmbSr, srKs.ToString(Inv));
            }
            _loading = true;
            SelectOrText(_cmbFreq, up.ToString("0.00", Inv));
            _loading = false;
            AppendLog(string.Format(Inv, "QO-100 channel {0:0.00} MHz ↓ → TX {1:0.00} MHz{2}", downlinkMHz, up, srKs > 0 ? ", " + srKs + " KS" : ""));
            await FrequencyCommitted();
        }

        private void HookRecalc(Control c)
        {
            var cb = c as ComboBox;
            if (cb != null) { cb.SelectionChanged += (s, e) => Recalc(); return; }
            var ec = c as EditCombo;
            if (ec != null) { ec.TextChanged += (s, e) => Recalc(); return; }
            var ch = c as CheckBox;
            if (ch != null) ch.IsCheckedChanged += (s, e) => Recalc();
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
            string name = (_txtMemName.Text ?? "").Trim();
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

        private async Task DeleteMemory()
        {
            var m = _lstMem.SelectedItem as TxMemory;
            if (m == null) return;
            if (!await Dialogs.Show(this, "Delete memory \"" + m.Name + "\"?", true)) return;
            _s.Memories.Remove(m);
            try { _s.Save(); } catch { }
            RefreshMemories();
        }

        // ================================================================== parameters <-> UI

        private static string CText(ComboBox cb) { return cb.SelectedItem != null ? cb.SelectedItem.ToString() : ""; }
        private static string CText(EditCombo cb) { return cb.Text; }

        private string Constellation() { return (CText(_cmbConst) == "" ? "QPSK" : CText(_cmbConst)).ToLowerInvariant(); }

        private bool DvbS2() { return _cmbStandard.SelectedIndex != 1; }

        private void RefillFec()
        {
            string keep = _cmbFec.SelectedItem as string;
            if (!DvbS2() && Constellation() != "qpsk") { _loading = true; _cmbConst.SelectedIndex = 0; _loading = false; }
            _loading = true;
            _cmbFec.Items.Clear();
            foreach (var f in DvbCalc.FecsFor(Constellation(), DvbS2())) _cmbFec.Items.Add(f);
            int idx = keep != null ? _cmbFec.Items.IndexOf(keep) : -1;
            _cmbFec.SelectedIndex = idx >= 0 ? idx : Math.Min(_cmbFec.Items.Count - 1, Math.Max(0, _cmbFec.Items.Count / 2));
            _loading = false;
            Recalc();
        }

        private void RefillCodecs(string keep)
        {
            _cmbCodec.Items.Clear();
            foreach (var c in Codecs.ForPlatform()) _cmbCodec.Items.Add(c);
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
            value = value ?? "";
            for (int i = 0; i < cb.Items.Count; i++)
            {
                string it = Convert.ToString(cb.Items[i], Inv);
                if (it == value || it.StartsWith(value + " ", StringComparison.Ordinal)) { cb.SelectedIndex = i; return; }
            }
            if (cb.Items.Count > 0 && cb.SelectedIndex < 0) cb.SelectedIndex = 0;
        }

        private static void SelectOrText(EditCombo cb, string value)
        {
            value = value ?? "";
            for (int i = 0; i < cb.Items.Count; i++)
            {
                string it = Convert.ToString(cb.Items[i], Inv);
                if (it == value || it.StartsWith(value + " ", StringComparison.Ordinal)) { cb.SelectedIndex = i; return; }
            }
            cb.Text = value;
        }

        private TxParams ReadParams()
        {
            return new TxParams
            {
                Input = (InputType)Math.Max(0, _cmbInput.SelectedIndex),
                VideoDevice = CText(_cmbVideo).Trim(),
                AudioDevice = CText(_cmbAudio).Trim(),
                InputFile = (_txtFile.Text ?? "").Trim(),
                Codec = _cmbCodec.SelectedItem is Codecs.CodecChoice ? ((Codecs.CodecChoice)_cmbCodec.SelectedItem).Value : "libx265",
                Preset = (_cmbPreset.SelectedItem as string) ?? "veryfast",
                ImageSize = CText(_cmbSize).Trim(),
                Fps = FirstNumber(CText(_cmbFps), 25),
                AudioChannels = new[] { 1, 2, 6 }[Math.Min(2, Math.Max(0, _cmbChannels.SelectedIndex))],
                AudioBitrateK = (int)FirstNumber(CText(_cmbAbr), 32),
                DvbS2 = DvbS2(),
                Constellation = Constellation(),
                SymbolRateKs = (int)FirstNumber(CText(_cmbSr), 333),
                Fec = (_cmbFec.SelectedItem as string) ?? "1/2",
                ShortFrames = _cmbFrame.SelectedIndex == 1,
                Pilots = _chkPilots.IsChecked == true,
                SmoothMotion = _chkSmooth.IsChecked == true,
                RollOff = SelectedRollOff(),
                TxFrequencyMHz = FirstNumber(CText(_cmbFreq), 2403.25),
                GainDb = SliderGainDb()
            };
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
                int chIdx = Array.IndexOf(new[] { 1, 2, 6 }, p.AudioChannels);
                _cmbChannels.SelectedIndex = chIdx >= 0 ? chIdx : 1; // anything else (old 7.1) -> stereo
                SelectOrText(_cmbAbr, p.AudioBitrateK.ToString(Inv));
                _cmbStandard.SelectedIndex = p.DvbS2 ? 0 : 1;
                _cmbConst.SelectedIndex = Math.Max(0, Array.IndexOf(DvbCalc.Constellations, p.Constellation));
                SelectOrText(_cmbSr, p.SymbolRateKs.ToString(Inv));
                _cmbFec.Items.Clear();
                foreach (var f in DvbCalc.FecsFor(p.Constellation, p.DvbS2)) _cmbFec.Items.Add(f);
                int fi = _cmbFec.Items.IndexOf(p.Fec);
                _cmbFec.SelectedIndex = fi >= 0 ? fi : 0;
                _cmbFrame.SelectedIndex = p.ShortFrames ? 1 : 0;
                _chkPilots.IsChecked = p.Pilots;
                _chkSmooth.IsChecked = p.SmoothMotion;
                SelectRollOff(p.RollOff);
                SelectOrText(_cmbFreq, p.TxFrequencyMHz.ToString("0.00", Inv));
                ApplyPowerRange();
                SetGainSlider(p.GainDb);
                _numDGain.Value = 0;
                _numDGain.IsEnabled = p.Constellation == "qpsk";
            }
            finally { _loading = false; }
            UpdateInputVisibility();
            Recalc();
        }

        private double SelectedRollOff() { return FirstNumber(CText(_cmbRollOff), 0.20); }

        private void SelectRollOff(double value)
        {
            int best = 0; double diff = double.MaxValue;
            for (int i = 0; i < _cmbRollOff.Items.Count; i++)
            {
                double d = Math.Abs(FirstNumber(Convert.ToString(_cmbRollOff.Items[i], Inv), 0.20) - value);
                if (d < diff) { diff = d; best = i; }
            }
            if (_cmbRollOff.Items.Count > 0) _cmbRollOff.SelectedIndex = best;
        }

        private void UpdateInputVisibility()
        {
            var t = (InputType)Math.Max(0, _cmbInput.SelectedIndex);
            bool idle = !_running && !_busy;
            _cmbVideo.IsEnabled = t == InputType.Capture && idle;
            _btnRefresh.IsEnabled = t == InputType.Capture && idle;
            _cmbAudio.IsEnabled = t == InputType.Capture && idle;
            _txtFile.IsEnabled = t == InputType.File && idle;
            _btnBrowse.IsEnabled = t == InputType.File && idle;
        }

        private void Recalc()
        {
            if (_loading || _cmbFec == null || _cmbFec.SelectedItem == null || _lblTs == null) return;
            if (_running) return;
            try
            {
                var p = ReadParams();
                UpdateDownlink(p.TxFrequencyMHz);
                double ts = DvbCalc.TsBitrateK(p);
                var plan = EncodePlan.Build(p, _s, ts, false);
                ShowPlan(p, plan);
            }
            catch { }
        }

        private void ShowPlan(TxParams p, EncodePlan plan)
        {
            _lblTs.Text = plan.TsBitrateK.ToString("0", Inv) + " kb/s" + (plan.TsFromDevice ? "  ✓" : "");
            _lblVideo.Text = plan.VideoBitrateK.ToString("0", Inv) + " kb/s";
            string advice = Codecs.LowRateAdvice(p.Codec, p.ImageSize, p.Fps, plan.VideoBitrateK);
            _lblVideoTitle.Text = advice == null ? "Video bitrate" : "Video bitrate  •  " + advice;
            _lblVideoTitle.Foreground = LTheme.B(advice == null ? LTheme.Muted : LTheme.Amber);
            ToolTip.SetTip(_lblVideoTitle, advice);
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
            ApplyPowerRange();
            UpdateHeader();
            UpdateButtons();
        }

        private void UpdateHeader()
        {
            if (_tx == null) return;
            if (!_tx.NeedsConnection)
            {
                _btnConnect.Content = "READY"; _btnConnect.IsEnabled = false;
                LTheme.Paint(_btnConnect, LTheme.GreenFill, LTheme.GreenBorder);
            }
            else if (_busy && !_tx.IsConnected)
            {
                _btnConnect.Content = "WAITING"; _btnConnect.IsEnabled = false;
                LTheme.Paint(_btnConnect, LTheme.Grey, LTheme.GreyBorder);
            }
            else if (_tx.IsConnected)
            {
                _btnConnect.Content = "CONNECTED"; _btnConnect.IsEnabled = !_running;
                LTheme.Paint(_btnConnect, LTheme.GreenFill, LTheme.GreenBorder);
            }
            else
            {
                _btnConnect.Content = "CONNECT"; _btnConnect.IsEnabled = true;
                LTheme.Paint(_btnConnect, LTheme.Accent, LTheme.Accent);
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
            _btnStart.IsEnabled = !_busy;
            if (_running) { _btnStart.Content = "STOP"; LTheme.Paint(_btnStart, LTheme.Surface2, LTheme.RedBorder); }
            else { _btnStart.Content = _s.Mode == RunMode.Encode ? "START" : "START CONTROL"; LTheme.Paint(_btnStart, LTheme.Accent, LTheme.Accent); }

            _btnPtt.IsEnabled = _running && _tx.SupportsPtt && !_busy;
            if (!_tx.SupportsPtt) { _btnPtt.Content = "PTT N/A"; LTheme.Paint(_btnPtt, LTheme.Surface2, LTheme.Border); }
            else if (_ptt) { _btnPtt.Content = "ON AIR"; LTheme.Paint(_btnPtt, LTheme.Red, LTheme.RedBorder); }
            else { _btnPtt.Content = "PTT"; LTheme.Paint(_btnPtt, LTheme.Surface2, LTheme.Border); }

            foreach (var c in _lockWhileRunning) c.IsEnabled = !_running && !_busy;
            _btnSettings.IsEnabled = !_running && !_busy;
            _btnMonitor.IsEnabled = _s.Hardware == HardwareType.LocalTest;
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
            if (_loading) return;
            double f = FirstNumber(CText(_cmbFreq), 0);
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
            if (string.IsNullOrWhiteSpace(_s.Callsign)) return "Enter your callsign in Settings → Station first.";
            if (p.SymbolRateKs < 25 || p.SymbolRateKs > 4000) return "Symbol rate must be 25 to 4000 KS.";
            if (p.TxFrequencyMHz < 70 || p.TxFrequencyMHz > 6000) return "TX frequency must be 70 to 6000 MHz.";
            if (p.GainDb > _s.PowerLimitDb) return "Gain is above the power limit (" + _s.PowerLimitDb + " dB).";
            if (_s.Mode == RunMode.Encode)
            {
                if (!File.Exists(_s.FfmpegExe)) return "FFmpeg not found. Install it (sudo apt install ffmpeg) or set the FFmpeg folder in Settings.";
                if (p.Input == InputType.Capture && string.IsNullOrWhiteSpace(p.VideoDevice)) return "Choose a video device.";
                if (p.Input == InputType.Capture && string.IsNullOrWhiteSpace(p.AudioDevice)) return "Choose an audio device.";
                if (p.Input == InputType.File && !File.Exists(p.InputFile)) return "Video file not found.";
                if (!System.Text.RegularExpressions.Regex.IsMatch(p.ImageSize, @"^\d+x\d+$")) return "Resolution must look like 1280x720.";
            }
            string hwErr = _tx != null ? _tx.Validate(p) : null;
            if (hwErr != null) return hwErr;
            if (_s.Mode == RunMode.ControlOnly && !(_tx is PlutoMqttTransmitter || _tx is Pluto0303Transmitter || _tx is SdrHelperTransmitter)) return "Control-only mode needs a Pluto with F5OEO MQTT or 0303 firmware, a LimeSDR or a HackRF.";
            return null;
        }

        private async Task StartStopAsync()
        {
            if (_busy) return;
            if (_running) { await StopAsync("Stopped."); return; }

            var p = ReadParams();
            string err = Validate(p);
            if (err != null) { AppendLog(err); await Dialogs.Show(this, err); return; }

            _busy = true; UpdateHeader();
            _s.LastRun = p.Clone();
            try { _s.Save(); } catch { }

            try
            {
                if (_s.Mode == RunMode.Encode && p.Input == InputType.Capture)
                    await ObsLauncher.EnsureRunningAsync(_s, p.VideoDevice, m => AppendLog(m));

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
                ShowPlan(p, plan);

                await _tx.ConfigureAsync(p, false);
                _ptt = false;

                if (_s.Mode == RunMode.Encode)
                {
                    string url = _tx.StreamUrl();
                    string args = FfmpegBuilder.BuildArgs(p, _s, plan, url);
                    AppendLog("ffmpeg " + args);
                    _ffmpeg.Start(_s.FfmpegExeFor(p.Codec), args);
                    await Task.Delay(2000);
                    if (!_ffmpeg.IsRunning)
                        throw new InvalidOperationException("the encoder stopped straight away - see the ffmpeg message above.");
                    AppendLog("Encoder running -> " + url);
                }
                else
                    AppendLog("Control mode: start your external encoder (e.g. OBS) sending MPEG-TS to " + _tx.StreamUrl().Split('?')[0] + " at no more than " + plan.TsBitrateK.ToString("0", Inv) + " kbit/s.");

                _active = p;
                _running = true;
                AppendLog(_tx.SupportsPtt ? "Ready. Press PTT to go on air." : "Streaming. Transmit is controlled in the radio.");
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
            string url = _s.Hardware == HardwareType.LocalTest ? _s.LocalTestUrl : _tx.StreamUrl();
            int q = url.IndexOf('?'); if (q > 0) url = url.Substring(0, q);
            if (_s.Hardware != HardwareType.LocalTest)
                AppendLog("Note: monitoring a unicast stream to the radio only works if this PC can also receive it. Local test mode is best for checking the encoder.");
            try
            {
                ClosePlayer();
                ProcessStartInfo psi;
                if (_s.Player == RxPlayer.Mpv && !string.IsNullOrWhiteSpace(_s.MpvPath))
                    psi = new ProcessStartInfo(_s.MpvPath, "--profile=low-latency --title=Monitor " + FfmpegBuilder.Q(url));
                else
                {
                    if (!File.Exists(_s.FfplayExe)) { AppendLog("ffplay not found in " + _s.EffectiveFfmpegFolder + " (sudo apt install ffmpeg)."); return; }
                    psi = new ProcessStartInfo(_s.FfplayExe, "-hide_banner -fflags nobuffer -window_title Monitor " + FfmpegBuilder.Q(url));
                }
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                _player = Process.Start(psi);
                AppendLog("Monitor player opened on " + url);
            }
            catch (Exception ex) { AppendLog("Could not start player: " + ex.Message); }
        }

        private void ClosePlayer()
        {
            try { if (_player != null && !_player.HasExited) _player.Kill(); } catch { }
            _player = null;
        }

        // ================================================================== devices, files, settings

        private async Task RefreshDevicesAsync()
        {
            string v = CText(_cmbVideo), a = CText(_cmbAudio);
            var lists = await FfmpegRunner.ListDirectShowDevicesAsync(_s.FfmpegExe);
            _cmbVideo.Items.Clear(); foreach (var x in lists.Item1) _cmbVideo.Items.Add(x);
            _cmbAudio.Items.Clear(); foreach (var x in lists.Item2) _cmbAudio.Items.Add(x);
            if (!string.IsNullOrEmpty(v)) SelectOrText(_cmbVideo, v); else if (_cmbVideo.Items.Count > 0) _cmbVideo.SelectedIndex = 0;
            if (!string.IsNullOrEmpty(a)) SelectOrText(_cmbAudio, a); else if (_cmbAudio.Items.Count > 0) _cmbAudio.SelectedIndex = 0;
            AppendLog("Found " + lists.Item1.Count + " video and " + lists.Item2.Count + " audio capture devices.");
            if (!File.Exists(_s.FfmpegExe))
                AppendLog("FFmpeg not found - install it with: sudo apt install ffmpeg (or set Settings → FFmpeg folder).");
            else
                AppendLog("Using FFmpeg: " + _s.FfmpegExe);
        }

        private async Task BrowseFile()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Choose a video file",
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Video files") { Patterns = new[] { "*.ts", "*.mp4", "*.mkv", "*.mov", "*.avi", "*.mpg" } },
                    FilePickerFileTypes.All
                }
            });
            if (files != null && files.Count > 0) _txtFile.Text = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
        }

        private async Task OpenSettings()
        {
            if (_running) return;
            var current = ReadParams();
            var w = new SettingsWindow(_s.Clone(), _tx);
            bool ok = await w.ShowDialog<bool>(this);
            if (!ok) return;
            w.Result.LastRun = current;
            w.Result.Profiles = _s.Profiles;
            w.Result.Memories = _s.Memories;
            w.Result.ShowSpectrum = _s.ShowSpectrum;
            _s = w.Result;
            try { _s.Save(); } catch (Exception ex) { AppendLog("Could not save settings: " + ex.Message); }
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
                var r = await UpdateChecker.CheckAsync(Program.Version);
                if (r == null) return;
                _lblCredit.Text = "v" + Program.Version + "  •";
                _lnkUpdate.Content = r.Tag + " available - click to download";
                _updateUrl = r.Url;
                _lnkUpdate.IsVisible = true;
                Title = ProductTitle + "  v" + Program.Version + "   (" + r.Tag + " available)";
                AppendLog("New version " + r.Tag + " available: " + r.Url);
            }
            catch { }
        }

        private static void OpenUrl(string target)
        {
            try { Process.Start(new ProcessStartInfo("xdg-open", "\"" + target + "\"") { UseShellExecute = false }); } catch { }
        }

        private void ShowDbb()
        {
            if (_lblDbb == null) return;
            bool onAir = _running && _ptt;
            string text; Color colour;
            if (!_s.ShowSpectrum) { text = "QO-100 level: spectrum hidden"; colour = LTheme.Muted; }
            else if (double.IsNaN(_dbb)) { text = onAir ? "QO-100 level: not seen yet" : "QO-100 level vs beacon: -"; colour = LTheme.Muted; }
            else
            {
                string v = (_dbb >= 0 ? "+" : "") + _dbb.ToString("0.0", Inv) + " dBb";
                text = (onAir ? "Your signal  " : "Channel  ") + v + (_dbb >= 0 ? "   ABOVE BEACON" : "");
                colour = onAir ? SpectrumView.DbbColour(_dbb) : LTheme.Muted;
            }
            if (_lblDbb.Text != text) _lblDbb.Text = text;
            var b = _lblDbb.Foreground as SolidColorBrush;
            if (b == null || b.Color != colour) _lblDbb.Foreground = LTheme.B(colour);
        }

        private void UpdateDownlink(double uplinkMHz)
        {
            double down = uplinkMHz + SpectrumView.UplinkOffsetMHz;
            _lblDownlink.Text = down > 10490 && down < 10500 ? "QO-100 downlink " + down.ToString("0.00", Inv) + " MHz" : "";
        }

        private void RefreshLive()
        {
            bool ready = _tx != null && (!_tx.NeedsConnection || _tx.IsConnected);
            _light.State = _running ? (_ptt ? TxLight.OnAir : TxLight.Standby) : ready ? TxLight.Ready : TxLight.Off;

            string warn = null;
            var stw = _lastStats;
            if (_running && stw != null && stw.Elapsed > 6 && !double.IsNaN(stw.SpeedNow))
            {
                if (stw.SpeedNow < 0.93) _slowSince = _slowSince ?? DateTime.Now; else _slowSince = null;
            }
            else _slowSince = null;
            if (_slowSince.HasValue && (DateTime.Now - _slowSince.Value).TotalSeconds >= 5)
                warn = "Encoder too slow (" + stw.Speed + ") - try a GPU codec or faster preset";
            string ur; long underruns;
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

            if (_spectrum != null && _s.ShowSpectrum)
            {
                double f = _active != null ? _active.TxFrequencyMHz : FirstNumber(CText(_cmbFreq), 0);
                int sr = _active != null ? _active.SymbolRateKs : (int)FirstNumber(CText(_cmbSr), 0);
                _spectrum.SetTransmit(f, sr, _running && _ptt);
            }

            var st = _lastStats;
            if (_running && st != null)
            {
                _lblStats.Text = string.Format(Inv, "Encoder  {0:0.#} fps   {1}   speed {2}{3}", st.Fps, st.Bitrate, st.Speed, st.Dropped > 0 ? "   dropped " + st.Dropped : "");
                _lblStats.Foreground = LTheme.B(_slowSince.HasValue ? LTheme.Amber : LTheme.Muted);
            }
            else if (_running && _s.Mode != RunMode.Encode) _lblStats.Text = "Control mode running";

            if (_telemetry.Count > 0)
            {
                var lines = _telemetry.OrderBy(k => k.Key).Take(30).Select(k => (k.Key.Length > 26 ? k.Key.Substring(k.Key.Length - 26) : k.Key).PadRight(27) + k.Value);
                _lblStatus.Text = string.Join("\n", lines);
            }
            else _lblStatus.Text = "No radio status yet.";
        }

        /// <summary>Screenshot helper: show the on-air state with sample readings.</summary>
        public void DemoOnAir()
        {
            _running = true; _ptt = true;
            _active = ReadParams();
            _lastStats = new EncoderStats { Fps = 15.0, Bitrate = "614.2kbits/s", Speed = "1.00x", SpeedNow = 1.0, Elapsed = 20 };
            _telemetry["tx/mute"] = "0";
            _telemetry["tx/gain"] = "-10";
            _telemetry["system/temperature"] = "41.2";
            AppendLog("PTT ON - transmitting.");
            UpdateButtons();
            RefreshLive();
        }

        private void AppendLog(string line)
        {
            if (_log == null) return;
            string t = _log.Text ?? "";
            if (t.Length > 60000) t = t.Substring(t.Length - 30000);
            _log.Text = t + DateTime.Now.ToString("HH:mm:ss ") + line + "\n";
            _log.CaretIndex = _log.Text.Length;
            WriteLogFile(line);
        }

        public static string LogFolder { get { return Path.Combine(Path.GetDirectoryName(AppSettings.SettingsPath), "logs"); } }

        private bool _logPruned;

        private void WriteLogFile(string line)
        {
            if (_demo) return;
            try
            {
                Directory.CreateDirectory(LogFolder);
                string file = Path.Combine(LogFolder, "datvtx-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                if (!_logPruned)
                {
                    _logPruned = true;
                    foreach (var f in Directory.GetFiles(LogFolder, "datvtx-*.log"))
                        if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
                    File.AppendAllText(file, "\n==== DATV TX " + Program.Version + " (Linux) started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====\n");
                }
                File.AppendAllText(file, DateTime.Now.ToString("HH:mm:ss ") + line + "\n");
            }
            catch { }
        }

        private static void UI(Action a)
        {
            if (Dispatcher.UIThread.CheckAccess()) { try { a(); } catch { } }
            else Dispatcher.UIThread.Post(() => { try { a(); } catch { } });
        }

        private async void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (_closing) return;
            if (_running)
            {
                e.Cancel = true;
                await StopAsync("Stopped on exit.");
                _closing = true;
                Close();
                return;
            }
            if (WindowState == WindowState.Normal)
            {
                if (_compact) { _s.CompactWidth = (int)Width; _s.CompactHeight = (int)Height; }
                else { _s.NormalWidth = (int)Width; _s.NormalHeight = (int)Height; }
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
