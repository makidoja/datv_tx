using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DatvTx.Core;

namespace DatvTx.Hardware
{
    /// <summary>
    /// LimeSDR Mini / HackRF One via datvtx_mod.exe: a DVB-S/S2 modulator (F5OEO libdvbmod) that receives
    /// the transport stream from ffmpeg by UDP and drives the radio through LimeSuite.dll / hackrf.dll.
    /// Control (PTT, gain, frequency) goes over the helper's stdin.
    /// </summary>
    public class SdrHelperTransmitter : Transmitter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private readonly string _radio;
        private Process _proc;
        private TaskCompletionSource<string> _ready;
        private bool _stopping;

        public SdrHelperTransmitter(AppSettings s, string radio) : base(s) { _radio = radio; }

        private bool IsHackRf { get { return _radio == "hackrf"; } }
        private bool IsMuntjac { get { return _radio == "muntjac"; } }
        private bool IsEsp32 { get { return _radio == "esp32"; } }

        public override string DisplayName { get { return IsEsp32 ? "ESP32-C3 DATV" : IsMuntjac ? "BATC Muntjac" : IsHackRf ? "HackRF One" : "LimeSDR Mini"; } }

        public override double[] RollOffs
        {
            get
            {
                if (IsEsp32) return new[] { 0.35 }; // the firmware's RRC filter is fixed at 0.35
                return IsMuntjac ? new[] { 0.20, 0.15, 0.25, 0.35 } : new[] { 0.20, 0.25, 0.35 };
            }
        }

        public override string Validate(TxParams p)
        {
            if (IsEsp32)
            {
                if (!p.DvbS2 || p.Constellation != "qpsk") return "The ESP32-C3 DATV support is DVB-S2 QPSK only.";
                if (p.SymbolRateKs < 25 || p.SymbolRateKs > 1000) return "ESP32-C3 DATV symbol rates: 25 to 1000 KS.";
                double half = p.SymbolRateKs * 1.35 / 2000.0;
                if (p.TxFrequencyMHz - half < 2300 || p.TxFrequencyMHz + half > 2450) return "The ESP32-DATV firmware transmits only in 2300-2450 MHz.";
                if (Settings.Esp32MaxLevel < 1 || Settings.Esp32MaxLevel > 480) return "Settings → ESP32 level at 0 dB must be 1 to 480.";
                return null;
            }
            if (!IsMuntjac) return null;
            if (!p.DvbS2) return "The Muntjac transmits DVB-S2 only.";
            if (p.Constellation != "qpsk" && p.Constellation != "8psk") return "The Muntjac supports QPSK and 8PSK only.";
            if (Array.IndexOf(new[] { 125, 250, 333, 500, 1000 }, p.SymbolRateKs) < 0) return "Muntjac symbol rates: 125, 250, 333, 500 or 1000 KS.";
            if (p.Constellation == "8psk" && p.SymbolRateKs == 1000) return "The Muntjac cannot do 8PSK at 1000 KS.";
            double f = p.TxFrequencyMHz;
            if (!((f >= 389.5 && f <= 510) || (f >= 770 && f <= 1030) || (f >= 2390 && f <= 2490)))
                return "Muntjac frequency ranges: 389.5-510, 770-1030 and 2390-2490 MHz.";
            return null;
        }

        /// <summary>Folder for Muntjac LO calibration files (copy &lt;serial&gt;.mjo from the Portsdown here).</summary>
        public static string CalibrationFolder
        {
            get { return Path.GetDirectoryName(AppSettings.SettingsPath); }
        }

        /// <summary>COM port of the first Muntjac (USB VID 2E8A, PID 000A), or null.</summary>
        public static string FindMuntjacPort() { return FindUsbSerial("2E8A", "000A"); }

        /// <summary>Serial port of the first ESP32-C3 on its native USB (Espressif USB JTAG/serial, 303A:1001), or null.</summary>
        public static string FindEsp32Port() { return FindUsbSerial("303A", "1001"); }

        private static string FindUsbSerial(string vid, string pid)
        {
            if (!Platform.IsWindows) return Platform.LinuxFindUsbSerial(vid, pid);
#if !LINUX
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE '%VID_" + vid + "&PID_" + pid + "%'"))
                {
                    foreach (System.Management.ManagementObject o in searcher.Get())
                    {
                        string name = Convert.ToString(o["Name"]);
                        var m = System.Text.RegularExpressions.Regex.Match(name ?? "", @"\((COM\d+)\)");
                        if (m.Success) return m.Groups[1].Value;
                    }
                }
            }
            catch { }
#endif
            return null;
        }
        public override bool SupportsPtt { get { return true; } }
        public override bool SupportsLiveControl { get { return _proc != null && !_proc.HasExited; } }
        public override bool IsConnected { get { return true; } }

        public override string HelpText
        {
            get
            {
                if (IsEsp32)
                    return "ESP32-C3 (SP8ESA ESP32-DATV firmware, flash it with C3CAST): DVB-S2 QPSK on 13 cm. Needs RF filtering and a PA on air. Level and frequency changes restart the transmission briefly.";
                if (IsMuntjac)
                    return "Muntjac by USB: DVB-S2 QPSK/8PSK, 125-1000 KS, 389.5-510 / 2390-2490 MHz. Power 0 dB = 0.20.";
                if (!Platform.IsWindows)
                    return IsHackRf
                        ? "HackRF via the built-in modulator (sudo apt install hackrf). Power 0 dB = TX VGA 47 dB."
                        : "LimeSDR via the built-in modulator (sudo apt install limesuite). Power 0 dB = full TX gain.";
                return IsHackRf
                    ? "HackRF via the built-in modulator (WinUSB driver via Zadig). Power 0 dB = TX VGA 47 dB."
                    : "LimeSDR via the built-in modulator (FTDI D3XX driver). Power 0 dB = full TX gain.";
            }
        }

        private static double[] Pair(string text, double a, double b)
        {
            var parts = (text ?? "").Replace(',', ' ').Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            double x, y;
            if (parts.Length >= 1 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, Inv, out x)) a = x;
            if (parts.Length >= 2 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, Inv, out y)) b = y;
            return new[] { a, b };
        }

        public static string HelperExe
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Platform.Exe("datvtx_mod")); }
        }

        public override string StreamUrl()
        {
            return FfmpegBuilder.StreamUrl("127.0.0.1", Settings.ModulatorPort, StreamProtocol.Udp, Settings.FifoBufMb);
        }

        private string ModArgs(TxParams p)
        {
            var sb = new StringBuilder();
            sb.Append(" --sr ").Append(p.SymbolRateKs * 1000);
            sb.Append(" --standard ").Append(p.DvbS2 ? "dvbs2" : "dvbs");
            sb.Append(" --const ").Append(p.Constellation);
            sb.Append(" --fec ").Append(p.Fec);
            if (p.Pilots) sb.Append(" --pilots");
            if (p.ShortFrames) sb.Append(" --short");
            sb.Append(" --rolloff ").Append((p.DvbS2 ? p.RollOff : 0.35).ToString("0.00", Inv));
            if (Settings.Upsample > 0) sb.Append(" --upsample ").Append(Settings.Upsample);
            return sb.ToString();
        }

        /// <summary>Exact net TS bitrate from the modulator itself (like fetching it from a Pluto).</summary>
        public override async Task<double?> FetchTsBitrateKAsync(TxParams p)
        {
            if (!File.Exists(HelperExe)) throw new FileNotFoundException(Platform.Exe("datvtx_mod") + " is missing from the program folder.", HelperExe);
            var psi = new ProcessStartInfo(HelperExe, "--bitrate --radio " + _radio + ModArgs(p))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            string output = await Task.Run(() =>
            {
                using (var proc = Process.Start(psi))
                {
                    string o = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);
                    return o;
                }
            });
            foreach (var line in output.Split('\n'))
            {
                var l = line.Trim();
                if (l.StartsWith("BITRATE ", StringComparison.Ordinal))
                {
                    double bps = double.Parse(l.Substring(8), Inv);
                    RaiseLog("Modulator TS bitrate: " + (bps / 1000).ToString("0.0", Inv) + " kbit/s");
                    return bps / 1000.0;
                }
                if (l.StartsWith("ERROR ", StringComparison.Ordinal)) throw new InvalidOperationException(l.Substring(6));
            }
            return null;
        }

        public override async Task ConfigureAsync(TxParams p, bool pttOn)
        {
            if (_proc != null) await ShutdownAsync();
            if (!File.Exists(HelperExe)) throw new FileNotFoundException(Platform.Exe("datvtx_mod") + " is missing from the program folder.", HelperExe);

            double gain = Math.Min(p.GainDb, Settings.PowerLimitDb);
            var args = new StringBuilder("--radio " + _radio);
            args.Append(" --freq ").Append((p.TxFrequencyMHz * 1e6).ToString("0", Inv));
            args.Append(ModArgs(p));
            args.Append(" --gain ").Append(gain.ToString("0.##", Inv));
            args.Append(" --port ").Append(Settings.ModulatorPort);
            if (pttOn) args.Append(" --ptt");
            if (IsMuntjac)
            {
                string port = string.IsNullOrWhiteSpace(Settings.MuntjacPort) ? FindMuntjacPort() : Settings.MuntjacPort.Trim();
                if (string.IsNullOrEmpty(port))
                    throw new InvalidOperationException("No Muntjac found. Plug it in (USB 2E8A:000A) or set Settings → Muntjac COM port" + (Platform.IsWindows ? "." : " (e.g. /dev/ttyACM0)."));
                args.Append(" --device ").Append(port);
                args.Append(" --driver-dir ").Append(FfmpegBuilder.Q(CalibrationFolder));
            }
            if (IsEsp32)
            {
                string port = string.IsNullOrWhiteSpace(Settings.Esp32Port) ? FindEsp32Port() : Settings.Esp32Port.Trim();
                if (string.IsNullOrEmpty(port))
                    throw new InvalidOperationException("No ESP32-C3 found. Plug it in by its USB-C port (Espressif 303A:1001) or set Settings → ESP32 COM port.");
                args.Append(" --device ").Append(port);
                args.Append(" --esp-level ").Append(Settings.Esp32MaxLevel.ToString(Inv));
                args.Append(" --esp-min-level ").Append(Math.Max(1, Settings.Esp32MinLevel).ToString(Inv));
                double[] dc = Pair(Settings.Esp32DcTrim, 0, 0), iq = Pair(Settings.Esp32IqTrim, 1.0, 0);
                if (Math.Abs(dc[0]) > 512 || Math.Abs(dc[1]) > 512) throw new InvalidOperationException("ESP32 DC trim must be -512 to 512.");
                if (iq[0] < 0.7 || iq[0] > 1.3 || Math.Abs(iq[1]) > 40) throw new InvalidOperationException("ESP32 IQ gain must be 0.7-1.3 and phase -40 to 40 degrees.");
                args.Append(" --esp-dc ").Append(Math.Round(dc[0] * 16).ToString("0", Inv)).Append(' ').Append(Math.Round(dc[1] * 16).ToString("0", Inv));
                args.Append(" --esp-iq ").Append(Math.Round(iq[0] * 1e4).ToString("0", Inv)).Append(' ').Append(Math.Round(iq[1] * 1e3).ToString("0", Inv));
            }
            if (IsHackRf && Settings.HackRfAmp) args.Append(" --amp");
            if (!IsHackRf && !Settings.LimeCalibrate) args.Append(" --no-cal");
            string drv = string.IsNullOrWhiteSpace(Settings.SdrDriverFolder) ? AppDomain.CurrentDomain.BaseDirectory : Settings.SdrDriverFolder;
            args.Append(" --driver-dir ").Append(FfmpegBuilder.Q(drv.TrimEnd('\\', '/')));

            var psi = new ProcessStartInfo(HelperExe, args.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            _ready = new TaskCompletionSource<string>();
            _stopping = false;
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (s, e) => { if (e.Data != null) OnLine(e.Data); };
            _proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) RaiseTelemetry("mod/log", e.Data.Trim()); };
            _proc.Exited += (s, e) =>
            {
                var r = _ready;
                if (r != null) r.TrySetResult("ERROR modulator exited");
                if (!_stopping) RaiseFault(DisplayName + " modulator stopped unexpectedly.");
            };
            RaiseLog("Starting " + DisplayName + " modulator: datvtx_mod " + args);
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            // Lime calibration can take several seconds.
            var done = await Task.WhenAny(_ready.Task, Task.Delay(30000));
            string result = done == _ready.Task ? _ready.Task.Result : "ERROR modulator did not start within 30 s";
            if (result.StartsWith("ERROR", StringComparison.Ordinal))
            {
                await ShutdownAsync();
                throw new InvalidOperationException(DisplayName + ": " + result.Substring(5).Trim());
            }
            RaiseLog(DisplayName + " ready (" + result.Substring(5).Trim() + ")" + (pttOn ? "" : " - carrier muted"));
        }

        private void OnLine(string line)
        {
            line = line.Trim();
            if (line.StartsWith("READY", StringComparison.Ordinal) || line.StartsWith("ERROR", StringComparison.Ordinal))
            {
                var r = _ready;
                if (r != null && r.TrySetResult(line)) return;
            }
            if (line.StartsWith("STAT ", StringComparison.Ordinal))
            {
                foreach (var kv in line.Substring(5).Split(' '))
                {
                    int eq = kv.IndexOf('=');
                    if (eq > 0) RaiseTelemetry("mod/" + kv.Substring(0, eq), kv.Substring(eq + 1));
                }
                return;
            }
            if (line.StartsWith("ERROR", StringComparison.Ordinal) || line.StartsWith("WARN", StringComparison.Ordinal) || line.StartsWith("MJ ", StringComparison.Ordinal))
                RaiseLog(DisplayName + ": " + line);
        }

        private Task Send(string cmd)
        {
            var p = _proc;
            if (p == null || p.HasExited) throw new InvalidOperationException("The " + DisplayName + " modulator is not running.");
            p.StandardInput.WriteLine(cmd);
            p.StandardInput.Flush();
            return Task.FromResult(0);
        }

        public override Task SetPttAsync(bool on) { return Send("ptt " + (on ? "1" : "0")); }

        public override Task SetGainAsync(double db)
        {
            if (db > Settings.PowerLimitDb) db = Settings.PowerLimitDb;
            return Send("gain " + db.ToString("0.##", Inv));
        }

        public override Task SetFrequencyAsync(double mhz) { return Send("freq " + (mhz * 1e6).ToString("0", Inv)); }

        public override Task SetRollOffAsync(double rollOff)
        {
            RaiseLog("Roll-off changes take effect at the next START on " + DisplayName + ".");
            return Task.FromResult(0);
        }

        public override Task SetDigitalGainAsync(double db)
        {
            RaiseLog("Digital gain is a Pluto feature; use the power slider on " + DisplayName + ".");
            return Task.FromResult(0);
        }

        public override async Task ShutdownAsync()
        {
            var p = _proc;
            _proc = null;
            if (p == null) return;
            _stopping = true;
            try
            {
                if (!p.HasExited)
                {
                    try { p.StandardInput.WriteLine("quit"); p.StandardInput.Close(); } catch { }
                    await Task.Run(() => p.WaitForExit(4000));
                    if (!p.HasExited) p.Kill();
                }
                RaiseLog(DisplayName + " stopped.");
            }
            catch (Exception ex) { RaiseLog("Stopping modulator: " + ex.Message); }
            finally { p.Dispose(); }
        }

        public override void Dispose()
        {
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
            _proc = null;
        }
    }
}
