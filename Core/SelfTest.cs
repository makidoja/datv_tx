using System;
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
            foreach (string codec in cpu)
                foreach (int sr in rates)
                {
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

            // Smooth motion: 30 fps source blended down to 10 / 12.5 / 15 fps
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
                bool ok = code == 0 && args.Contains("tmix=frames=") && fpsOk;
                if (!ok) failures++;
                log.AppendLine((ok ? "PASS " : "FAIL ") + "smooth motion " + want + "  exit=" + code + "  " + FfmpegBuilder.SmoothMotionFilter(p));
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
                if (!present) failures++;
                log.AppendLine((present ? "HAVE " : "MISSING ") + c.Value);
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
            catch (Exception ex) { log.AppendLine("FAIL update check: " + ex.Message); failures++; }

            log.AppendLine(failures == 0 ? "SELFTEST OK" : "SELFTEST FAILED: " + failures);
            File.WriteAllText(logPath, log.ToString());
            return failures == 0 ? 0 : 1;
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
