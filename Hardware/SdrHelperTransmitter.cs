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

        public override string DisplayName { get { return IsHackRf ? "HackRF One" : "LimeSDR Mini"; } }
        public override bool SupportsPtt { get { return true; } }
        public override bool SupportsLiveControl { get { return _proc != null && !_proc.HasExited; } }
        public override bool IsConnected { get { return true; } }

        public override string HelpText
        {
            get
            {
                return IsHackRf
                    ? "HackRF via the built-in modulator. Needs hackrf.dll (e.g. PothosSDR) and the WinUSB driver (Zadig). Gain 0 dB = TX VGA 47 dB."
                    : "LimeSDR via the built-in modulator. Needs LimeSuite.dll (e.g. PothosSDR). Gain 0 dB = full normalised TX gain.";
            }
        }

        public static string HelperExe
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "datvtx_mod.exe"); }
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
            if (!File.Exists(HelperExe)) throw new FileNotFoundException("datvtx_mod.exe is missing from the program folder.", HelperExe);
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
            if (!File.Exists(HelperExe)) throw new FileNotFoundException("datvtx_mod.exe is missing from the program folder.", HelperExe);

            double gain = Math.Min(p.GainDb, Settings.PowerLimitDb);
            var args = new StringBuilder("--radio " + _radio);
            args.Append(" --freq ").Append((p.TxFrequencyMHz * 1e6).ToString("0", Inv));
            args.Append(ModArgs(p));
            args.Append(" --gain ").Append(gain.ToString("0.##", Inv));
            args.Append(" --port ").Append(Settings.ModulatorPort);
            if (pttOn) args.Append(" --ptt");
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
            if (line.StartsWith("ERROR", StringComparison.Ordinal) || line.StartsWith("WARN", StringComparison.Ordinal))
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
            RaiseLog("Digital gain is a Pluto feature; use Gain on " + DisplayName + ".");
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
