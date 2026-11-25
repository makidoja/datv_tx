using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DatvTx.Core;

namespace DatvTx.Hardware
{
    /// <summary>
    /// Pluto with F5OEO's 0303 ("Perseverance", F5UII interface) firmware in DATV / UDP mode, driven
    /// through the same web requests its Controller page uses, so the browser isn't needed:
    /// <list type="bullet">
    /// <item>Settings: read /settings.txt ("key value" lines), change the modulator fields and post the
    /// whole set to /modulator_save.php, which rewrites settings.txt. The firmware's watchudp.sh notices
    /// the change within a second and restarts the modulator with the new values.</item>
    /// <item>PTT: /requests.php?PTT=on|off (GPIO + TX LO power-down), state from /requests.php?onair.</item>
    /// <item>Live power / frequency: written to the AD9361 (out_voltage0_hardwaregain,
    /// out_altvoltage1_TX_LO_frequency) through requests.php's command hook, without a restart.</item>
    /// </list>
    /// Reference: github.com/F5OEO/datvplutofrm (topic-f5uii), board/pluto/overlay/www and /root.
    /// </summary>
    public sealed class Pluto0303Transmitter : Transmitter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private readonly HttpClient _http;
        private bool _connected;
        private CancellationTokenSource _poll;
        private bool _ptt;

        public Pluto0303Transmitter(AppSettings s) : base(s)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("DatvTx");
        }

        private string Base { get { return "http://" + Settings.PlutoIp + "/"; } }

        public override string DisplayName { get { return "Pluto 0303 " + Settings.PlutoIp; } }
        public override bool SupportsPtt { get { return true; } }
        public override bool SupportsLiveControl { get { return true; } }
        public override bool NeedsConnection { get { return true; } }
        public override bool IsConnected { get { return _connected; } }
        public override double[] RollOffs { get { return new[] { 0.20, 0.25, 0.35, 0.15 }; } }

        public override string HelpText
        {
            get
            {
                return "F5OEO 0303 firmware: set the Pluto once to Main mode DATV, operating mode UDP (System → Setup). "
                     + "This app then sets the modulator and PTT itself; the web page isn't needed.";
            }
        }

        public override string StreamUrl() { return FfmpegBuilder.StreamUrl(Settings.PlutoIp, Settings.PlutoPort, Settings.Protocol, Settings.FifoBufMb); }

        // ------------------------------------------------------------------ connection

        public override async Task ConnectAsync()
        {
            if (_connected) return;
            Dictionary<string, string> current;
            try { current = await ReadSettingsAsync(); }
            catch (Exception ex) { throw new InvalidOperationException("no reply from the Pluto web server at " + Base + " (" + ex.Message + ")"); }
            if (!current.ContainsKey("mode") || !current.ContainsKey("freq"))
                throw new InvalidOperationException("this doesn't look like a 0303 Pluto: " + Base + "settings.txt has no modulator settings. "
                    + "Open the Pluto's Controller page once and press Apply Settings, then try again.");
            _connected = true;
            RaiseLog("Connected to Pluto 0303 at " + Settings.PlutoIp + " (modulator: " + Describe(current) + ").");
            RaiseConnectionChanged();
            StartPolling();
        }

        public override Task DisconnectAsync()
        {
            StopPolling();
            if (_connected) { _connected = false; RaiseConnectionChanged(); }
            return Task.FromResult(0);
        }

        private static string Describe(Dictionary<string, string> s)
        {
            string v;
            return string.Join(" ", new[] { "freq", "mode", "mod", "sr", "fec", "power" }
                .Select(k => s.TryGetValue(k, out v) ? k + "=" + v : null).Where(x => x != null));
        }

        // ------------------------------------------------------------------ settings

        private async Task<Dictionary<string, string>> ReadSettingsAsync()
        {
            // Cache-busting query: the page itself reads settings.txt with $.get.
            string text = await _http.GetStringAsync(Base + "settings.txt?t=" + DateTime.UtcNow.Ticks);
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                if (raw.Length == 0) continue;
                int sp = raw.IndexOf(' ');
                string key = sp < 0 ? raw : raw.Substring(0, sp);
                string value = sp < 0 ? "" : raw.Substring(sp + 1);
                if (key.Length > 0) d[key] = value;
            }
            return d;
        }

        /// <summary>The 0303 page's values: mode DVBS2/DVBS, mod QPSK…, fec "23", pilots On/Off, frame LongFrame/ShortFrame.</summary>
        private void Apply(Dictionary<string, string> s, TxParams p)
        {
            string freq = p.TxFrequencyMHz.ToString("0.000", Inv);
            s["freq"] = freq;
            s["f-central"] = freq;
            s["finefreqtune"] = "0";
            s["channel"] = "Custom";
            s["mode"] = p.DvbS2 ? "DVBS2" : "DVBS";
            s["mod"] = p.Constellation.ToUpperInvariant();
            s["sr"] = p.SymbolRateKs.ToString(Inv);
            s["srselect"] = new[] { 2000, 1500, 1000, 500, 333, 250, 125, 66, 33 }.Contains(p.SymbolRateKs) ? p.SymbolRateKs.ToString(Inv) : "Custom";
            s["fec"] = p.Fec.Replace("/", "");
            s["pilots"] = p.Pilots ? "On" : "Off";
            s["frame"] = p.ShortFrames ? "ShortFrame" : "LongFrame";
            s["rolloff"] = p.RollOff.ToString("0.00", Inv);
            s["power"] = Math.Min(0, p.GainDb).ToString("0.##", Inv);
            if (!string.IsNullOrWhiteSpace(Settings.Callsign)) s["callsign"] = Settings.Callsign.Trim();
            if (!string.IsNullOrWhiteSpace(Settings.ServiceProvider)) s["provname"] = Settings.ServiceProvider.Trim();
        }

        private async Task SaveSettingsAsync(Dictionary<string, string> s)
        {
            // modulator_save.php writes every posted field to settings.txt, so post the full set.
            using (var content = new FormUrlEncodedContent(s.ToList()))
            using (var r = await _http.PostAsync(Base + "modulator_save.php", content))
                r.EnsureSuccessStatusCode();
        }

        public override async Task ConfigureAsync(TxParams p, bool pttOn)
        {
            if (!_connected) await ConnectAsync();
            await SetPttAsync(false);
            var s = await ReadSettingsAsync();
            Apply(s, p);
            await SaveSettingsAsync(s);

            // Check it was written, then give watchudp.sh (1 s poll) time to restart the modulator.
            var check = await ReadSettingsAsync();
            string f, m;
            if (!check.TryGetValue("freq", out f) || f != s["freq"] || !check.TryGetValue("mode", out m) || m != s["mode"])
                throw new InvalidOperationException("the Pluto didn't store the new settings (" + Base + "modulator_save.php).");
            RaiseLog(string.Format(Inv, "Pluto 0303 set: {0} MHz  {1} {2}  {3} KS  FEC {4}  {5:0.##} dB (modulator restarting)",
                s["freq"], s["mode"], s["mod"], s["sr"], p.Fec, p.GainDb));
            await Task.Delay(2500);
            // A restarted modulator must not come up on air.
            await SetPttAsync(pttOn);
        }

        public override Task<double?> FetchTsBitrateKAsync(TxParams p)
        {
            // The 0303 modulator pads to the rate its own calculation gives (pluto_dvb -d), which
            // matches DvbCalc; use the calculated value.
            return Task.FromResult<double?>(null);
        }

        public override async Task ShutdownAsync()
        {
            try { await SetPttAsync(false); }
            catch (Exception ex) { RaiseLog("Could not switch the Pluto off air: " + ex.Message + " - check it in the web page!"); }
        }

        // ------------------------------------------------------------------ live control

        public override async Task SetPttAsync(bool on)
        {
            using (var r = await _http.GetAsync(Base + "requests.php?PTT=" + (on ? "on" : "off")))
                r.EnsureSuccessStatusCode();
            _ptt = on;
            RaiseTelemetry("tx/ptt", on ? "on" : "off");
        }

        /// <summary>Runs a shell command on the Pluto through the firmware's requests.php?cmd= hook.</summary>
        private async Task<string> PlutoCommandAsync(string cmd)
        {
            using (var r = await _http.GetAsync(Base + "requests.php?cmd=" + Uri.EscapeDataString(cmd)))
            {
                r.EnsureSuccessStatusCode();
                return await r.Content.ReadAsStringAsync();
            }
        }

        private const string Phy = "/sys/bus/iio/devices/iio:device$(sh /root/device_sel.sh compact)";

        public override async Task SetGainAsync(double db)
        {
            double g = Math.Max(-89.75, Math.Min(0, Math.Round(db * 4) / 4));
            await PlutoCommandAsync("echo " + g.ToString("0.00", Inv) + " > " + Phy + "/out_voltage0_hardwaregain");
        }

        public override async Task SetFrequencyAsync(double mhz)
        {
            long hz = (long)Math.Round(mhz * 1e6);
            await PlutoCommandAsync("echo " + hz.ToString(Inv) + " > " + Phy + "/out_altvoltage1_TX_LO_frequency");
        }

        public override Task SetRollOffAsync(double rollOff)
        {
            RaiseLog("On 0303 firmware the roll-off changes at the next START.");
            return Task.FromResult(0);
        }

        public override Task SetDigitalGainAsync(double db)
        {
            RaiseLog("Digital gain is not available on 0303 firmware.");
            return Task.FromResult(0);
        }

        public override string Validate(TxParams p)
        {
            if (p.TxFrequencyMHz < 70 || p.TxFrequencyMHz > 6000) return "Pluto frequency must be 70 to 6000 MHz.";
            if (p.GainDb > 0) return "Pluto power must be 0 dB or less.";
            return null;
        }

        // ------------------------------------------------------------------ status

        private void StartPolling()
        {
            StopPolling();
            var cts = _poll = new CancellationTokenSource();
            Task.Run(async () =>
            {
                int failures = 0;
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        // TX LO power-down: 0 = carrier on.
                        string onair = (await _http.GetStringAsync(Base + "requests.php?onair")).Trim();
                        RaiseTelemetry("tx/onair", onair == "0" ? "yes" : "no");
                        string temps = await _http.GetStringAsync(Base + "pluto_temp.php");
                        RaiseTelemetry("system/temperatures", System.Text.RegularExpressions.Regex.Replace(temps, "<[^>]+>", " ").Trim());
                        failures = 0;
                    }
                    catch (Exception ex)
                    {
                        if (cts.IsCancellationRequested) break;
                        if (++failures == 3)
                        {
                            _connected = false;
                            RaiseLog("Pluto 0303 not answering: " + ex.Message);
                            RaiseConnectionChanged();
                            if (_ptt) RaiseFault("lost contact with the Pluto");
                            break;
                        }
                    }
                    try { await Task.Delay(2000, cts.Token); } catch { break; }
                }
            });
        }

        private void StopPolling()
        {
            if (_poll != null) { _poll.Cancel(); _poll = null; }
        }

        public override void Dispose()
        {
            StopPolling();
            _http.Dispose();
        }
    }
}
