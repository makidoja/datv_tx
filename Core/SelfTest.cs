using System;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DatvTx.Core
{
    /// <summary>
    /// Build-time check (DatvTx.exe --selftest): runs every CPU codec through the real command
    /// builder against the bundled ffmpeg, using a test pattern and tone, and fails if any encoder
    /// rejects its options. GPU encoders need hardware, so they are only checked for presence.
    /// </summary>
    public static class SelfTest
    {
        public static int Run(string logPath)
        {
            var log = new StringBuilder();
            var s = new AppSettings();
            s.Normalise();
            int failures = 0;
            string tmp = Path.Combine(Path.GetTempPath(), "datvtx-selftest");
            Directory.CreateDirectory(tmp);

            string[] cpu = { "libx264", "libx265", "libvvenc", "libsvtav1", "libaom-av1" };
            int[] rates = { 333, 125, 1000 };
            // Linux uses the distribution's FFmpeg, which may lack some encoders (e.g. H.266): skip those there.
            string available;
            RunFfmpeg(s.FfmpegExe, "-hide_banner -encoders", 30000, out available);
            log.AppendLine("FFmpeg: " + s.FfmpegExe);
            foreach (string codec in cpu)
                foreach (int sr in rates)
                {
                    if (!Platform.IsWindows && (available ?? "").IndexOf(" " + codec + " ", StringComparison.Ordinal) < 0)
                    {
                        if (sr == rates[0]) log.AppendLine("SKIP " + codec + " (not in this FFmpeg build)");
                        continue;
                    }
                    var p = new TxParams { Codec = codec, SymbolRateKs = sr, Fec = "2/3", Fps = 25, ImageSize = sr < 250 ? "640x360" : "1280x720", AudioBitrateK = 16 };
                    var plan = EncodePlan.Build(p, s, DvbCalc.TsBitrateK(p), false);
                    string outFile = Path.Combine(tmp, codec + "-" + sr + (Codecs.IsAv1(codec) ? ".nut" : ".ts"));
                    string input = "-f lavfi -i testsrc2=size=1280x720:rate=30,format=yuvj422p -f lavfi -i sine=frequency=1000:sample_rate=44100";
                    string args = FfmpegBuilder.BuildArgs(p, s, plan, outFile, input, "-t 3 -y");
                    string output;
                    int code = RunFfmpeg(s.FfmpegExe, args, 180000, out output);
                    long size = File.Exists(outFile) ? new FileInfo(outFile).Length : 0;
                    bool ok = code == 0 && size > 1000;
                    if (!ok) failures++;
                    log.AppendLine((ok ? "PASS " : "FAIL ") + codec + " " + sr + "KS  exit=" + code + " bytes=" + size);
                    if (!ok)
                    {
                        log.AppendLine("  ffmpeg " + args);
                        log.AppendLine(Indent(Tail(output, 25)));
                    }
                }

            // Smooth motion: blend at 10 fps; 12.5 / 15 fps are left unblended
            foreach (double fps in new[] { 10.0, 12.5, 15.0 })
            {
                var p = new TxParams { Codec = "libx265", SymbolRateKs = 500, Fec = "2/3", Fps = fps, ImageSize = "1280x720", AudioBitrateK = 16, SmoothMotion = true };
                var plan = EncodePlan.Build(p, s, DvbCalc.TsBitrateK(p), false);
                string outFile = Path.Combine(tmp, "smooth-" + fps.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".ts");
                string input = "-f lavfi -i testsrc2=size=1280x720:rate=30,format=yuvj422p -f lavfi -i sine=frequency=1000:sample_rate=44100";
                string args = FfmpegBuilder.BuildArgs(p, s, plan, outFile, input, "-t 4 -y");
                string output, probe;
                int code = RunFfmpeg(s.FfmpegExe, args, 180000, out output);
                RunFfmpeg(s.FfmpegExe, "-hide_banner -i \"" + outFile + "\"", 30000, out probe);
                string want = fps.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " fps";
                var fm = System.Text.RegularExpressions.Regex.Match(probe, @"([\d.]+) fps");
                double got;
                bool fpsOk = fm.Success && double.TryParse(fm.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out got) && Math.Abs(got - fps) < 0.01;
                bool blendExpected = fps <= 10;
                bool ok = code == 0 && args.Contains("tmix=frames=") == blendExpected && fpsOk;
                if (!ok) failures++;
                log.AppendLine((ok ? "PASS " : "FAIL ") + "smooth motion " + want + "  exit=" + code + "  " + (FfmpegBuilder.SmoothMotionFilter(p) ?? "no blend"));
                if (!ok) { log.AppendLine("  ffmpeg " + args); log.AppendLine(Indent(Tail(output + probe, 25))); }
            }

            // GPU encoders: present in the ffmpeg build each codec will use?
            string enc, encCompat;
            RunFfmpeg(s.FfmpegExe, "-hide_banner -encoders", 30000, out enc);
            RunFfmpeg(s.FfmpegExeFor("h264_nvenc"), "-hide_banner -encoders", 30000, out encCompat);
            log.AppendLine("NVENC codecs use: " + s.FfmpegExeFor("h264_nvenc"));
            foreach (var c in Codecs.All)
            {
                string list = c.Value.EndsWith("_nvenc", StringComparison.Ordinal) ? encCompat : enc;
                bool present = list.Contains(" " + c.Value + " ");
                // Windows bundles its own FFmpeg, which must have every encoder; on Linux the
                // distribution's FFmpeg decides, so a missing encoder is only reported.
                if (!present && Platform.IsWindows) failures++;
                log.AppendLine((present ? "HAVE " : Platform.IsWindows ? "MISSING " : "not in this FFmpeg: ") + c.Value);
            }

            // Low-bit-rate advice
            var adv = new[] {
                new { c = "libx265", sz = "1280x720", f = 15.0, v = 375.0, warn = true },
                new { c = "libx265", sz = "1280x720", f = 10.0, v = 375.0, warn = false },
                new { c = "libx265", sz = "1280x720", f = 15.0, v = 600.0, warn = false },
                new { c = "libx264", sz = "1280x720", f = 15.0, v = 600.0, warn = true },
            };
            foreach (var t in adv)
            {
                string a = Codecs.LowRateAdvice(t.c, t.sz, t.f, t.v);
                bool ok = (a != null) == t.warn;
                log.AppendLine((ok ? "PASS" : "FAIL") + " advice " + t.c + " " + t.sz + " " + t.f + "fps " + t.v + "k -> " + (a ?? "none"));
                if (!ok) failures++;
            }

            // Update check against the real GitHub releases
            try
            {
                var old = UpdateChecker.CheckAsync("0.0.1-beta").GetAwaiter().GetResult();
                bool okOld = old != null;
                log.AppendLine((okOld ? "PASS" : "FAIL") + " update check from 0.0.1-beta -> " + (old != null ? old.Tag + " " + old.Url : "none"));
                if (!okOld) failures++;
                var none = UpdateChecker.CheckAsync("99.0.0").GetAwaiter().GetResult();
                log.AppendLine((none == null ? "PASS" : "FAIL") + " update check from 99.0.0 -> " + (none != null ? none.Tag : "none"));
                if (none != null) failures++;
            }
            catch (System.Net.Http.HttpRequestException ex) { log.AppendLine("SKIP update check (GitHub not reachable: " + ex.Message + ")"); }
            catch (Exception ex) { log.AppendLine("FAIL update check: " + ex.Message); failures++; }

            failures += Pluto0303MockTest(log);

            // ESP32-C3 power slider floor follows the level settings (bottom of slider = lowest level, never below it)
            foreach (var t in new[] { new { max = 250, min = 15 }, new { max = 100, min = 15 }, new { max = 480, min = 1 } })
            {
                var es = new AppSettings { Esp32MaxLevel = t.max, Esp32MinLevel = t.min };
                double floorDb;
                using (var esp = new DatvTx.Hardware.SdrHelperTransmitter(es, "esp32")) floorDb = esp.MinGainDb;
                double lvl = t.max * Math.Pow(10, floorDb / 20);
                bool ok = Math.Round(lvl) >= t.min && Math.Round(t.max * Math.Pow(10, (floorDb - 0.25) / 20)) <= t.min;
                log.AppendLine((ok ? "PASS" : "FAIL") + " ESP32 level " + t.min + "-" + t.max + " -> slider floor " + floorDb.ToString("0.##") + " dB (level " + lvl.ToString("0.0") + ")");
                if (!ok) failures++;
            }
            {
                var es = new AppSettings();
                es.Normalise();
                double lo, hi;
                using (var esp = new DatvTx.Hardware.SdrHelperTransmitter(es, "esp32")) { lo = esp.MinGainDb; hi = esp.MaxGainDb; }
                bool ok = lo == -10.5 && hi == -6;
                log.AppendLine((ok ? "PASS" : "FAIL") + " ESP32 default slider " + lo + " to " + hi + " dB (want -10.5 to -6, level 75-125)");
                if (!ok) failures++;
            }

            log.AppendLine(failures == 0 ? "SELFTEST OK" : "SELFTEST FAILED: " + failures);
            File.WriteAllText(logPath, log.ToString());
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Drives Pluto0303Transmitter against a pretend 0303 web server (settings.txt,
        /// modulator_save.php, requests.php) and checks what it writes.
        /// </summary>
        private static int Pluto0303MockTest(StringBuilder log)
        {
            int fail = 0;
            var stored = new System.Collections.Generic.List<string>
            {
                "power -10", "callsign OLDCALL", "provname OLD", "pcrpts 800", "patperiod 200", "freq 2409.750",
                "f-central 2409.750", "finefreqtune 0", "channel 2409.75-333KS14", "mode DVBS", "mod QPSK", "sr 333",
                "srselect 333", "fec 12", "pilots On", "frame ShortFrame", "rolloff 0.35", "trvlo 0", "codec H265"
            };
            var requests = new System.Collections.Generic.List<string>();
            System.Net.HttpListener http = null;
            int port = 18303;
            try
            {
                http = new System.Net.HttpListener();
                http.Prefixes.Add("http://localhost:" + port + "/");
                http.Start();
            }
            catch (Exception ex)
            {
                log.AppendLine("SKIP 0303 mock test (cannot listen on localhost: " + ex.Message + ")");
                return 0;
            }
            var server = System.Threading.Tasks.Task.Run(() =>
            {
                while (http.IsListening)
                {
                    System.Net.HttpListenerContext ctx;
                    try { ctx = http.GetContext(); } catch { break; }
                    string path = ctx.Request.Url.AbsolutePath, body = "";
                    string reply = "";
                    lock (requests)
                    {
                        if (path == "/settings.txt") reply = string.Join("\n", stored) + "\n";
                        else if (path == "/modulator_save.php")
                        {
                            using (var r = new StreamReader(ctx.Request.InputStream)) body = r.ReadToEnd();
                            stored.Clear();
                            foreach (string kv in body.Split('&'))
                            {
                                int eq = kv.IndexOf('=');
                                string k = Uri.UnescapeDataString(kv.Substring(0, eq).Replace('+', ' '));
                                string v = Uri.UnescapeDataString(kv.Substring(eq + 1).Replace('+', ' '));
                                stored.Add(k + " " + v);
                            }
                            requests.Add("SAVE");
                        }
                        else if (path == "/requests.php")
                        {
                            string q = ctx.Request.QueryString["PTT"], c = ctx.Request.QueryString["cmd"];
                            if (q != null) requests.Add("PTT=" + q);
                            if (c != null) requests.Add("CMD " + c);
                            if (ctx.Request.QueryString.AllKeys.Contains("onair")) reply = "1";
                        }
                        else if (path == "/pluto_temp.php") reply = "<b>FPGA 45</b>";
                    }
                    byte[] bytes = Encoding.UTF8.GetBytes(reply);
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
            });

            void Check(bool ok, string what) { log.AppendLine((ok ? "PASS " : "FAIL ") + "0303 " + what); if (!ok) fail++; }
            try
            {
                var s = new AppSettings { PlutoIp = "localhost:" + port, Callsign = "M0CKE", ServiceProvider = "DATV-TX" };
                s.Normalise();
                var tx = new DatvTx.Hardware.Pluto0303Transmitter(s);
                tx.ConnectAsync().GetAwaiter().GetResult();
                Check(tx.IsConnected, "connect (settings.txt read)");
                var p = new TxParams { TxFrequencyMHz = 2403.25, DvbS2 = true, Constellation = "8psk", SymbolRateKs = 500, Fec = "3/4", Pilots = false, ShortFrames = false, RollOff = 0.20, GainDb = -12.5 };
                tx.ConfigureAsync(p, false).GetAwaiter().GetResult();
                tx.SetGainAsync(-15.25).GetAwaiter().GetResult();
                tx.SetFrequencyAsync(2403.75).GetAwaiter().GetResult();
                tx.SetPttAsync(true).GetAwaiter().GetResult();
                tx.ShutdownAsync().GetAwaiter().GetResult();
                tx.DisconnectAsync().GetAwaiter().GetResult();
                tx.Dispose();

                System.Collections.Generic.Dictionary<string, string> d;
                lock (requests) d = stored.Select(l => l.Split(new[] { ' ' }, 2)).ToDictionary(x => x[0], x => x.Length > 1 ? x[1] : "");
                string all = string.Join(", ", d.Select(kv => kv.Key + "=" + kv.Value));
                log.AppendLine("  settings.txt now: " + all);
                Check(d["freq"] == "2403.250" && d["f-central"] == "2403.250" && d["finefreqtune"] == "0", "frequency fields");
                Check(d["mode"] == "DVBS2" && d["mod"] == "8PSK" && d["sr"] == "500" && d["srselect"] == "500", "mode / constellation / SR");
                Check(d["fec"] == "34" && d["pilots"] == "Off" && d["frame"] == "LongFrame" && d["rolloff"] == "0.20", "FEC / pilots / frame / roll-off");
                Check(d["power"] == "-12.5" && d["callsign"] == "M0CKE" && d["provname"] == "DATV-TX", "power / callsign / provider");
                Check(d["pcrpts"] == "800" && d["patperiod"] == "200" && d["codec"] == "H265" && d["trvlo"] == "0", "other Pluto settings kept");
                string seq;
                lock (requests) seq = string.Join(" | ", requests);
                log.AppendLine("  requests: " + seq);
                int save = requests.IndexOf("SAVE");
                Check(save > 0 && requests[save - 1] == "PTT=off" && requests[save + 1] == "PTT=off", "carrier off before and after the modulator restart");
                Check(requests.Any(r => r.StartsWith("CMD echo -15.25 > /sys/bus/iio/devices/iio:device$(sh /root/device_sel.sh compact)/out_voltage0_hardwaregain", StringComparison.Ordinal)), "live power command");
                Check(requests.Any(r => r.StartsWith("CMD echo 2403750000 > ", StringComparison.Ordinal) && r.EndsWith("/out_altvoltage1_TX_LO_frequency", StringComparison.Ordinal)), "live frequency command");
                Check(requests.Count >= 2 && requests[requests.Count - 1] == "PTT=off" && requests.Contains("PTT=on"), "PTT on, then off at stop");
            }
            catch (Exception ex) { log.AppendLine("FAIL 0303 mock test: " + ex); fail++; }
            finally { try { http.Stop(); http.Close(); } catch { } }
            return fail;
        }

        private static int RunFfmpeg(string exe, string args, int timeoutMs, out string output)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            var sb = new StringBuilder();
            using (var proc = new Process { StartInfo = psi })
            {
                proc.ErrorDataReceived += (o, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                proc.OutputDataReceived += (o, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                proc.Start();
                proc.BeginErrorReadLine();
                proc.BeginOutputReadLine();
                if (!proc.WaitForExit(timeoutMs)) { try { proc.Kill(); } catch { } output = sb.ToString(); return -999; }
                proc.WaitForExit();
                output = sb.ToString();
                return proc.ExitCode;
            }
        }

        private static string Tail(string text, int lines)
        {
            var all = text.Replace("\r", "").Split('\n');
            int start = Math.Max(0, all.Length - lines);
            return string.Join("\n", all, start, all.Length - start);
        }

        private static string Indent(string text) { return "    " + text.Replace("\n", "\n    "); }
    }
}
