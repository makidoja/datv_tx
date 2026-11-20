using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DatvTx.Core
{
    public class EncoderStats
    {
        public long Frame;
        public double Fps;
        public string Bitrate = "";
        public string Speed = "";
        public int Dropped;
    }

    /// <summary>Runs ffmpeg as a child process and reports progress.</summary>
    public class FfmpegRunner : IDisposable
    {
        private Process _proc;
        private static readonly Regex StatsRx = new Regex(@"frame=\s*(\d+)\s+fps=\s*([\d.]+).*?bitrate=\s*(\S+).*?speed=\s*(\S+)", RegexOptions.Compiled);
        private static readonly Regex DropRx = new Regex(@"drop=\s*(\d+)", RegexOptions.Compiled);

        public event Action<string> Log;
        public event Action<EncoderStats> Stats;
        public event Action<int> Exited;

        public bool IsRunning { get { return _proc != null && !_proc.HasExited; } }

        public void Start(string exe, string args)
        {
            if (IsRunning) throw new InvalidOperationException("Encoder already running.");
            if (!File.Exists(exe)) throw new FileNotFoundException("ffmpeg not found. Set the FFmpeg folder in Settings.", exe);

            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.ErrorDataReceived += (s, e) => { if (e.Data != null) HandleLine(e.Data); };
            _proc.OutputDataReceived += (s, e) => { if (e.Data != null) RaiseLog(e.Data); };
            _proc.Exited += (s, e) =>
            {
                int code = 0;
                try { code = ((Process)s).ExitCode; } catch { }
                var h = Exited; if (h != null) h(code);
            };
            _proc.Start();
            _proc.BeginErrorReadLine();
            _proc.BeginOutputReadLine();
        }

        private void HandleLine(string line)
        {
            var m = StatsRx.Match(line);
            if (m.Success)
            {
                var st = new EncoderStats
                {
                    Frame = long.Parse(m.Groups[1].Value),
                    Fps = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Bitrate = m.Groups[3].Value,
                    Speed = m.Groups[4].Value
                };
                var d = DropRx.Match(line);
                if (d.Success) st.Dropped = int.Parse(d.Groups[1].Value);
                var h = Stats; if (h != null) h(st);
                return;
            }
            RaiseLog(line);
        }

        private void RaiseLog(string line) { var h = Log; if (h != null) h(line); }

        /// <summary>Asks ffmpeg to quit cleanly ('q'), then kills it if it does not exit.</summary>
        public async Task StopAsync()
        {
            var p = _proc;
            if (p == null) return;
            try
            {
                if (!p.HasExited)
                {
                    try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { }
                    await Task.Run(() => p.WaitForExit(3000));
                    if (!p.HasExited) p.Kill();
                }
            }
            catch { }
            _proc = null;
        }

        public void Dispose()
        {
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
            _proc = null;
        }

        // ------------------------------------------------------------------
        // DirectShow device discovery: ffmpeg -list_devices true -f dshow -i dummy
        // ------------------------------------------------------------------
        public static Task<Tuple<List<string>, List<string>>> ListDirectShowDevicesAsync(string ffmpegExe)
        {
            return Task.Run(() =>
            {
                var video = new List<string>();
                var audio = new List<string>();
                if (!File.Exists(ffmpegExe)) return Tuple.Create(video, audio);

                var psi = new ProcessStartInfo(ffmpegExe, "-hide_banner -list_devices true -f dshow -i dummy")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                string text;
                using (var p = Process.Start(psi))
                {
                    text = p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);
                }

                // Newer ffmpeg: [dshow @ ...] "Name" (video)
                // Older ffmpeg: section headers "DirectShow video devices" / "DirectShow audio devices"
                List<string> section = null;
                var nameRx = new Regex("\"([^\"]+)\"(\\s*\\((video|audio|none)\\))?");
                foreach (var raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.IndexOf("DirectShow video devices", StringComparison.OrdinalIgnoreCase) >= 0) { section = video; continue; }
                    if (line.IndexOf("DirectShow audio devices", StringComparison.OrdinalIgnoreCase) >= 0) { section = audio; continue; }
                    if (line.IndexOf("Alternative name", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var m = nameRx.Match(line);
                    if (!m.Success) continue;
                    string name = m.Groups[1].Value;
                    string kind = m.Groups[3].Value;
                    List<string> target = kind == "video" ? video : kind == "audio" ? audio : section;
                    if (kind == "none") continue;
                    if (target != null && !target.Contains(name)) target.Add(name);
                }
                return Tuple.Create(video, audio);
            });
        }
    }
}
