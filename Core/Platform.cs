using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DatvTx.Core
{
    /// <summary>Small helpers so the shared code runs on Windows and Linux.</summary>
    public static class Platform
    {
        public static readonly bool IsWindows = Path.DirectorySeparatorChar == '\\';

        /// <summary>"ffmpeg" → "ffmpeg.exe" on Windows.</summary>
        public static string Exe(string name) { return IsWindows ? name + ".exe" : name; }

        /// <summary>Full path of a program on the PATH, or null.</summary>
        public static string FindOnPath(string exe)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string full = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        /// <summary>The value in the last [brackets] of a device label such as "Logitech C920 [/dev/video0]".</summary>
        public static string DeviceId(string label)
        {
            if (string.IsNullOrEmpty(label)) return label;
            var m = Regex.Match(label, @"\[([^\[\]]+)\]\s*$");
            return m.Success ? m.Groups[1].Value : label.Trim();
        }

        // ------------------------------------------------------------------ Linux devices

        /// <summary>Video capture devices as "Name [/dev/videoN]" (V4L2; metadata nodes skipped).</summary>
        public static List<string> LinuxVideoDevices()
        {
            var list = new List<string>();
            const string root = "/sys/class/video4linux";
            if (!Directory.Exists(root)) return list;
            foreach (string dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                string node = Path.GetFileName(dir);
                if (!node.StartsWith("video", StringComparison.Ordinal)) continue;
                // UVC cameras expose a second /dev/videoN for metadata; its index is 1.
                string index = ReadText(Path.Combine(dir, "index"));
                if (index != null && index.Trim() != "0") continue;
                string name = (ReadText(Path.Combine(dir, "name")) ?? node).Trim();
                list.Add(name + " [/dev/" + node + "]");
            }
            return list;
        }

        /// <summary>
        /// PulseAudio / PipeWire sources as "Description [source.name]". Real inputs first, then
        /// "Monitor of ..." sources (these capture what the PC is playing, e.g. a browser).
        /// </summary>
        public static List<string> LinuxAudioDevices()
        {
            var inputs = new List<string>();
            var monitors = new List<string>();
            string text = Run("pactl", "list sources", 5000);
            if (text != null)
            {
                string name = null;
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("Name:", StringComparison.Ordinal)) name = line.Substring(5).Trim();
                    else if (line.StartsWith("Description:", StringComparison.Ordinal) && name != null)
                    {
                        string label = line.Substring(12).Trim() + " [" + name + "]";
                        (name.EndsWith(".monitor", StringComparison.Ordinal) ? monitors : inputs).Add(label);
                        name = null;
                    }
                }
            }
            var all = new List<string> { "Default input [default]" };
            all.AddRange(inputs);
            all.AddRange(monitors);
            return all;
        }

        /// <summary>The serial device of a USB VID:PID (hex) on Linux, e.g. /dev/ttyACM0, or null.</summary>
        public static string LinuxFindUsbSerial(string vid, string pid)
        {
            const string root = "/sys/class/tty";
            if (!Directory.Exists(root)) return null;
            int wantV = Convert.ToInt32(vid, 16), wantP = Convert.ToInt32(pid, 16);
            foreach (string dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                string node = Path.GetFileName(dir);
                if (!node.StartsWith("ttyACM", StringComparison.Ordinal) && !node.StartsWith("ttyUSB", StringComparison.Ordinal)) continue;
                // The USB interface's uevent has PRODUCT=<vid>/<pid>/<bcd> in hex without leading zeros.
                string ev = ReadText(Path.Combine(dir, "device", "uevent"));
                if (ev == null) continue;
                foreach (string line in ev.Split('\n'))
                {
                    if (!line.StartsWith("PRODUCT=", StringComparison.Ordinal)) continue;
                    string[] f = line.Substring(8).Trim().Split('/');
                    try
                    {
                        if (f.Length >= 2 && Convert.ToInt32(f[0], 16) == wantV && Convert.ToInt32(f[1], 16) == wantP)
                            return "/dev/" + node;
                    }
                    catch { }
                }
            }
            return null;
        }

        private static string ReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; }
        }

        /// <summary>Runs a program and returns its standard output, or null.</summary>
        public static string Run(string exe, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(timeoutMs);
                    return o;
                }
            }
            catch { return null; }
        }
    }
}
