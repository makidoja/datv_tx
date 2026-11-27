using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DatvTx.Core
{
    [TypeConverter(typeof(EnumDescConverter))]
    public enum HardwareType
    {
        [Description("Pluto - F5OEO MQTT firmware")] PlutoF5oeoMqtt,
        [Description("Pluto - F5OEO 0303 firmware (web control)")] Pluto0303,
        [Description("Pluto - older F5OEO firmware (UDP only)")] PlutoLegacyUdp,
        [Description("Pluto - standard ADI firmware (PC modulator)")] PlutoStock,
        [Description("LimeSDR Mini (built-in modulator)")] LimeSdr,
        [Description("HackRF One (built-in modulator)")] HackRf,
        [Description("BATC Muntjac (70 cm / 13 cm, USB)")] Muntjac,
        [Description("ESP32-C3 DATV (SP8ESA firmware, 13 cm, USB)")] Esp32Datv,
        [Description("Local test - no radio")] LocalTest
    }

    [TypeConverter(typeof(EnumDescConverter))]
    public enum RunMode
    {
        [Description("Encode and transmit")] Encode,
        [Description("Control radio only (external encoder, e.g. OBS)")] ControlOnly
    }

    public enum InputType { Capture, NetworkUdp, NetworkRtmp, File }
    public enum EncoderType { Software, Nvidia }
    public enum StreamProtocol { Udp, Srt }
    public enum RxPlayer { Ffplay, Mpv }

    /// <summary>
    /// Persistent settings. Mirrors config-tx.ini / config-rx.ini from DATV-NotSoEasy (DL5OCD).
    /// Shown in the Settings dialog via a PropertyGrid.
    /// </summary>
    public class AppSettings
    {
        // ---------------- Station ----------------
        [Category("1 Station"), DisplayName("Callsign"), Description("Callsign used as the DVB service name and in the Pluto MQTT topics.")]
        public string Callsign { get; set; } = "";

        [Category("1 Station"), DisplayName("Service provider"), Description("DVB service provider name shown on receivers.")]
        public string ServiceProvider { get; set; } = "DATV-TX";

        // ---------------- Hardware ----------------
        [Category("2 Hardware"), DisplayName("Transmitter"), Description("Which radio / modulator to use.")]
        [JsonConverter(typeof(StringEnumConverter))]
        public HardwareType Hardware { get; set; } = HardwareType.PlutoF5oeoMqtt;

        [Category("2 Hardware"), DisplayName("Run mode"), Description("Encode: this app encodes and sends the stream. Control only: an external encoder (OBS etc.) sends the stream, this app only steers the radio.")]
        [JsonConverter(typeof(StringEnumConverter))]
        public RunMode Mode { get; set; } = RunMode.Encode;

        [Category("2 Hardware"), DisplayName("Pluto IP address")]
        public string PlutoIp { get; set; } = "192.168.2.1";

        [Category("2 Hardware"), DisplayName("Pluto stream port"), Description("UDP port the Pluto listens on for the transport stream (DATV-NotSoEasy default 8282).")]
        public int PlutoPort { get; set; } = 8282;

        [Category("2 Hardware"), DisplayName("Pluto MQTT port")]
        public int PlutoMqttPort { get; set; } = 1883;

        [Category("2 Hardware"), DisplayName("Pluto command root"), Description("MQTT command root. Leave blank to use cmd/pluto/<Callsign>.")]
        public string CmdRoot { get; set; } = "";

        [Category("2 Hardware"), DisplayName("Stream protocol"), Description("UDP (normal) or SRT.")]
        [JsonConverter(typeof(StringEnumConverter))]
        public StreamProtocol Protocol { get; set; } = StreamProtocol.Udp;

        [Category("2 Hardware"), DisplayName("Local test address"), Description("Where the stream goes in Local test mode.")]
        public string LocalTestUrl { get; set; } = "udp://230.0.0.10:10000";

        // ---------------- Pluto modulator (F5OEO MQTT) ----------------
        [Category("2 Hardware"), DisplayName("Power limit (dB)"), Description("Top of the power slider. 0 dB = full output. Set lower to protect a PA from overdrive.")]
        public double PowerLimitDb { get; set; } = 0;

        [Category("3 Pluto modulator"), DisplayName("Fetch TS bitrate from Pluto"), Description("Ask the Pluto for the exact TS bitrate instead of calculating it (most accurate).")]
        public bool FetchTsBitrate { get; set; } = true;

        [Category("3 Pluto modulator"), DisplayName("TX stream mode"), Description("dvbs2-ts (normal) or dvbs.")]
        public string TxStreamMode { get; set; } = "dvbs2-ts";

        [Category("3 Pluto modulator"), DisplayName("Variable FEC"), Description("VBR modulator: FEC is adjusted to the input bitrate between the minimum FEC for the mode and the FEC range.")]
        public bool VariableFec { get; set; } = false;

        [Category("3 Pluto modulator"), DisplayName("FEC range (0-11)")]
        public int FecRange { get; set; } = 10;

        [Category("3 Pluto modulator"), DisplayName("Encoder VBR"), Description("Only with Variable FEC. Not recommended at low SR.")]
        public bool Vbr { get; set; } = false;

        [Category("3 Pluto modulator"), DisplayName("AGC gain"), Description("-100 = AGC off.")]
        public int AgcGain { get; set; } = -100;

        [Category("3 Pluto modulator"), DisplayName("Gain variable")]
        public bool GainVariable { get; set; } = false;

        [Category("3 Pluto modulator"), DisplayName("NCO / RIT (Hz)")]
        public int NcoHz { get; set; } = 0;

        [Category("3 Pluto modulator"), DisplayName("TS source mode"), Description("0 = UDP, 1 = file, 2 = internal pattern.")]
        public int TsSourceMode { get; set; } = 0;

        [Category("3 Pluto modulator"), DisplayName("TS source file")]
        public string TsSourceFile { get; set; } = "\\path\\to\\file";

        [Category("3 Pluto modulator"), DisplayName("Reboot Pluto on stop")]
        public bool RebootOnStop { get; set; } = false;

        // ---------------- LimeSDR / HackRF (built-in modulator) ----------------
        [Category("3b LimeSDR / HackRF"), DisplayName("SDR driver folder"), Description("Folder with LimeSuite.dll / hackrf.dll. Blank = this program's folder, then C:\\Program Files\\PothosSDR\\bin, then PATH.")]
        public string SdrDriverFolder { get; set; } = "";

        [Category("3b LimeSDR / HackRF"), DisplayName("Modulator UDP port"), Description("Local port the built-in modulator listens on for the transport stream.")]
        public int ModulatorPort { get; set; } = 10100;

        [Category("3b LimeSDR / HackRF"), DisplayName("Samples per symbol"), Description("0 = automatic (Lime: >= 1 MS/s, HackRF: >= 2 MS/s).")]
        public int Upsample { get; set; } = 0;

        [Category("3b LimeSDR / HackRF"), DisplayName("HackRF RF amplifier"), Description("Adds about 11 dB. Off unless you need it - check your PA drive level first.")]
        public bool HackRfAmp { get; set; } = false;

        [Category("3c BATC Muntjac"), DisplayName("Muntjac COM port"), Description("Blank = find the Muntjac automatically (USB 2E8A:000A). Or enter e.g. COM7.")]
        public string MuntjacPort { get; set; } = "";

        [Category("3d ESP32-C3 DATV"), DisplayName("ESP32 COM port"), Description("Blank = find the board automatically (Espressif USB 303A:1001). Or enter e.g. COM5 or /dev/ttyACM0.")]
        public string Esp32Port { get; set; } = "";

        [Category("3d ESP32-C3 DATV"), DisplayName("ESP32 level at 0 dB"), Description("Firmware output level (1-480) when the power slider is at 0 dB. Not calibrated power. Usable range is about 15-250: linear to about 100, compresses above about 250 (more distortion). Default 250.")]
        public int Esp32MaxLevel { get; set; } = 250;

        [Category("3d ESP32-C3 DATV"), DisplayName("ESP32 lowest level"), Description("Level at the bottom of the power slider (1-480); the slider's lowest dB is worked out from this. Below about 15 the board's carrier leak shows as a spike in the signal; use an attenuator for less power. Default 15.")]
        public int Esp32MinLevel { get; set; } = 15;

        [Category("3d ESP32-C3 DATV"), DisplayName("ESP32 DC trim I / Q (codes)"), Description("Carrier leak correction for your board, as \"I Q\" in DAC codes (-512..512), e.g. \"1.5 -0.75\". Blank or 0 0 = none.")]
        public string Esp32DcTrim { get; set; } = "0 0";

        [Category("3d ESP32-C3 DATV"), DisplayName("ESP32 IQ gain / phase"), Description("Image correction for your board, as \"gain phase\": Q gain 0.7..1.3 and Q phase -40..40 degrees. Default \"1.0 0\".")]
        public string Esp32IqTrim { get; set; } = "1.0 0";

        [Category("3b LimeSDR / HackRF"), DisplayName("Lime calibration at start"), Description("Run LimeSuite TX calibration (reduces carrier leak and image).")]
        public bool LimeCalibrate { get; set; } = true;

        [Category("5 Encoder"), DisplayName("Monitor player"), Description("Player used by MONITOR STREAM.")]
        [JsonConverter(typeof(StringEnumConverter))]
        public RxPlayer Player { get; set; } = RxPlayer.Ffplay;

        // ---------------- Encoder ----------------
        [Category("5 Encoder"), DisplayName("FFmpeg folder"), Description("Folder containing ffmpeg.exe and ffplay.exe. Blank = the ffmpeg folder next to this program.")]
        public string FfmpegFolder { get; set; } = "";

        [Category("5 Encoder"), DisplayName("mpv.exe path"), Description("Optional, for the mpv player.")]
        public string MpvPath { get; set; } = "";

        [Browsable(false)]
        [JsonConverter(typeof(StringEnumConverter))]
        public EncoderType Encoder { get; set; } = EncoderType.Software;

        [Category("5 Encoder"), DisplayName("Audio codec"), Description("aac, libfdk_aac or ac3.")]
        public string AudioCodec { get; set; } = "aac";

        [Category("5 Encoder"), DisplayName("Audio sample rate")]
        public int AudioSampleRate { get; set; } = 48000;

        [Category("5 Encoder"), DisplayName("Software preset (x264/x265)")]
        public string PresetSoft { get; set; } = "medium";

        [Category("5 Encoder"), DisplayName("VVC preset")]
        public string PresetVvc { get; set; } = "faster";

        [Category("5 Encoder"), DisplayName("NVIDIA preset"), Description("Blank = follow the Speed preset on the SOURCE tab (p1 fastest to p7 best). Or enter an NVENC preset such as p4 or slow.")]
        public string PresetHard { get; set; } = "";

        [Category("5 Encoder"), DisplayName("AV1 quality (1-63)")]
        public int Av1Quality { get; set; } = 10;

        // ---------------- Input ----------------
        [Category("6 Input"), DisplayName("Start OBS automatically"), Description("When the video device is OBS's virtual camera and OBS isn't running, START opens OBS with its virtual camera on.")]
        public bool StartObs { get; set; } = true;

        [Category("6 Input"), DisplayName("OBS program"), Description("Blank = find OBS Studio automatically. Or the full path to obs64.exe (Linux: obs).")]
        public string ObsPath { get; set; } = "";

        [Category("6 Input"), DisplayName("OBS start-up wait (s)"), Description("How long to give OBS to load and start its virtual camera before encoding (3-60).")]
        public int ObsStartWaitSeconds { get; set; } = 8;

        [Category("6 Input"), DisplayName("UDP input"), Description("Used when input is Network UDP.")]
        public string InputUdp { get; set; } = "udp://230.0.0.11:20000";

        [Category("6 Input"), DisplayName("RTMP input"), Description("Used when input is Network RTMP (this app listens).")]
        public string InputRtmp { get; set; } = "rtmp://127.0.0.1:20000/live/aaaa";

        [Category("6 Input"), DisplayName("File loop"), Description("-1 = loop forever, 0 = play once.")]
        public int FileLoop { get; set; } = -1;

        // ---------------- Transport stream (advanced) ----------------
        [Category("9 Updates"), DisplayName("Check for updates"), Description("Look for a newer release on GitHub when the app starts.")]
        public bool CheckForUpdates { get; set; } = true;

        [Category("7 Transport stream"), DisplayName("Audio sync offset (s)")]
        public double AudioOffset { get; set; } = -0.20;
        [Category("7 Transport stream"), DisplayName("Video PID")] public int VideoPid { get; set; } = 256;
        [Category("7 Transport stream"), DisplayName("Audio PID")] public int AudioPid { get; set; } = 257;
        [Category("7 Transport stream"), DisplayName("PMT PID")] public int PmtPid { get; set; } = 4095;
        [Category("7 Transport stream"), DisplayName("Start PID (PCR)")] public int StartPid { get; set; } = 256;
        [Category("7 Transport stream"), DisplayName("Network ID")] public int NetworkId { get; set; } = 1;
        [Category("7 Transport stream"), DisplayName("Transport stream ID")] public int StreamId { get; set; } = 4095;
        [Category("7 Transport stream"), DisplayName("PAT period (s)")] public double PatPeriod { get; set; } = 0.4;
        [Category("7 Transport stream"), DisplayName("PCR period (ms)")] public int PcrPeriod { get; set; } = 20;
        [Category("7 Transport stream"), DisplayName("Realtime buffer (MB)")] public int RtBufMb { get; set; } = 300;
        [Category("7 Transport stream"), DisplayName("Max delay (ms)")] public int MaxDelayMs { get; set; } = 2000;
        [Category("7 Transport stream"), DisplayName("Max interleave (s)")] public int MaxInterleaveS { get; set; } = 4;
        [Category("7 Transport stream"), DisplayName("Buffer factor"), Description("Encoder buffer in seconds of video. Larger values can let bursts exceed the channel rate.")] public double BufFactor { get; set; } = 1;

        [Category("5 Encoder"), DisplayName("Constant video rate (old)"), Description("Force the encoder to a fixed rate (minrate = maxrate), as DATV-NotSoEasy does. Off = the encoder may spend fewer bits on still scenes and save them for motion; the transport stream stays at a constant rate either way. Off scored slightly better in VMAF tests.")]
        public bool StrictCbr { get; set; } = false;
        [Browsable(false)] public int ThreadQueueK { get; set; } = 10;
        [Category("7 Transport stream"), DisplayName("UDP FIFO buffer (M)")] public int FifoBufMb { get; set; } = 50;

        // ---------------- Not shown in the grid ----------------
        [Browsable(false)] public TxParams LastRun { get; set; } = new TxParams();
        [Browsable(false)] public List<TxParams> Profiles { get; set; } = new List<TxParams>();
        [Browsable(false)] public List<TxMemory> Memories { get; set; } = new List<TxMemory>();
        [Browsable(false)] public bool ShowSpectrum { get; set; } = true;
        [Browsable(false)] public int SettingsVersion { get; set; } = 0;
        [Browsable(false)] public bool CompactMode { get; set; } = false;
        [Browsable(false)] public int NormalWidth { get; set; } = 1060;
        [Browsable(false)] public int NormalHeight { get; set; } = 900;
        [Browsable(false)] public int CompactWidth { get; set; } = 320;
        [Browsable(false)] public int CompactHeight { get; set; } = 660;

        [Browsable(false)] public bool AlwaysOnTop { get; set; } = false;

        /// <summary>No longer used: the ON TOP button controls always-on-top in both views. Kept so old settings files load.</summary>
        [Browsable(false)] public bool CompactOnTop { get; set; } = false;

        [Browsable(false), JsonIgnore]
        public string EffectiveCmdRoot
        {
            get { return string.IsNullOrWhiteSpace(CmdRoot) ? "cmd/pluto/" + Callsign : CmdRoot.Trim().TrimEnd('/'); }
        }

        [Browsable(false), JsonIgnore]
        public string FfmpegExe { get { return Path.Combine(EffectiveFfmpegFolder, Platform.Exe("ffmpeg")); } }

        /// <summary>
        /// NVIDIA codecs use the bundled FFmpeg 7.1 build (NVENC SDK 11.1, driver 470+) unless the user
        /// chose their own FFmpeg folder; everything else uses the main build.
        /// </summary>
        public string FfmpegExeFor(string codec)
        {
            if (string.IsNullOrWhiteSpace(FfmpegFolder) && codec != null && codec.EndsWith("_nvenc", StringComparison.Ordinal))
            {
                string compat = Path.Combine(EffectiveFfmpegFolder, "nvenc-compat", Platform.Exe("ffmpeg"));
                if (File.Exists(compat)) return compat;
            }
            return FfmpegExe;
        }

        [Browsable(false), JsonIgnore]
        public string FfplayExe { get { return Path.Combine(EffectiveFfmpegFolder, Platform.Exe("ffplay")); } }

        [Browsable(false), JsonIgnore]
        public string EffectiveFfmpegFolder
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(FfmpegFolder)) return FfmpegFolder;
                string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");
                if (Platform.IsWindows || File.Exists(Path.Combine(bundled, "ffmpeg"))) return bundled;
                // Linux without a bundled build: the system FFmpeg (sudo apt install ffmpeg)
                string sys = Platform.FindOnPath("ffmpeg");
                return sys != null ? Path.GetDirectoryName(sys) : bundled;
            }
        }

        // ---------------- Load / save ----------------
        public static string SettingsPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DatvTx");
                return Path.Combine(dir, "settings.json");
            }
        }

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var s = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(SettingsPath));
                    if (s != null) { s.Normalise(); return s; }
                }
            }
            catch { /* fall back to defaults */ }
            var d = new AppSettings();
            d.Normalise();
            return d;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        public AppSettings Clone()
        {
            return JsonConvert.DeserializeObject<AppSettings>(JsonConvert.SerializeObject(this));
        }

        public void Normalise()
        {
            if (LastRun == null) LastRun = new TxParams();
            if (Profiles == null) Profiles = new List<TxParams>();
            while (Profiles.Count < 3) Profiles.Add(null);
            if (SettingsVersion < 2)
            {
                // v0.1 used DATV-NotSoEasy's PWRLIM of -10 dB; the slider now goes to 0 dB by default.
                if (PowerLimitDb == -10) PowerLimitDb = 0;
                SettingsVersion = 2;
            }
            if (SettingsVersion < 3)
            {
                // v0.1.2 and earlier always ran NVENC at "slow" (DATV-NotSoEasy); now it follows the Speed preset.
                if (PresetHard == "slow") PresetHard = "";
                SettingsVersion = 3;
            }
            if (SettingsVersion < 4)
            {
                // v1.2.2 and earlier: ESP32 level at 0 dB defaulted to 100; the usable range is 15-250.
                if (Esp32MaxLevel == 100) Esp32MaxLevel = 250;
                SettingsVersion = 4;
            }
            if (Memories == null) Memories = new List<TxMemory>();
            if (Memories.Count == 0)
                for (int i = 0; i < Profiles.Count; i++)
                    if (Profiles[i] != null) Memories.Add(new TxMemory { Name = "Profile " + (i + 1), Params = Profiles[i] });
        }
    }

    /// <summary>A named stored set-up (frequency, mode and encoder settings).</summary>
    public class TxMemory
    {
        public string Name { get; set; } = "";
        public TxParams Params { get; set; } = new TxParams();
        public override string ToString() { return Name; }
    }

    /// <summary>
    /// The per-transmission parameters (what DATV-TX.bat asked for interactively, and stored in params.ini / favorite-x.ini).
    /// </summary>
    public class TxParams
    {
        // Source
        [JsonConverter(typeof(StringEnumConverter))]
        public InputType Input { get; set; } = InputType.Capture;
        public string VideoDevice { get; set; } = "OBS Virtual Camera";
        public string AudioDevice { get; set; } = "";
        public string InputFile { get; set; } = "";

        // Encoder
        public string Codec { get; set; } = "libx265";
        public string Preset { get; set; } = "veryfast";
        public string ImageSize { get; set; } = "1280x720";
        public double Fps { get; set; } = 25;
        public int AudioChannels { get; set; } = 2;
        public int AudioBitrateK { get; set; } = 32;

        // Modulator
        public bool DvbS2 { get; set; } = true;
        public string Constellation { get; set; } = "qpsk";
        public int SymbolRateKs { get; set; } = 333;
        public string Fec { get; set; } = "2/3";
        public bool ShortFrames { get; set; } = false;
        public bool Pilots { get; set; } = false;
        /// <summary>Blend the frames dropped when going down to a low frame rate, for natural motion blur.</summary>
        public bool SmoothMotion { get; set; } = true;
        public double RollOff { get; set; } = 0.20;
        public double TxFrequencyMHz { get; set; } = 2403.25;
        public double GainDb { get; set; } = -20;

        public TxParams Clone()
        {
            return JsonConvert.DeserializeObject<TxParams>(JsonConvert.SerializeObject(this));
        }

        public string Summary()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:0.###} MHz  {1} {2}KS {3}  {4} {5}",
                TxFrequencyMHz, Constellation.ToUpperInvariant(), SymbolRateKs, Fec, Codec, ImageSize);
        }
    }
}
