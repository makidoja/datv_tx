using System;
using System.Collections.Generic;
using System.Globalization;

namespace DatvTx.Core
{
    /// <summary>
    /// Bitrate maths ported from DATV-TX.bat (DATV-NotSoEasy 1.7, DL5OCD).
    /// All bitrates in kbit/s unless stated.
    /// </summary>
    public static class DvbCalc
    {
        public static readonly string[] Constellations = { "qpsk", "8psk", "16apsk", "32apsk" };

        public static readonly int[] SymbolRates = { 35, 66, 125, 250, 333, 500, 1000, 1500, 2000 };

        public static string[] FecsFor(string constellation, bool dvbS2)
        {
            if (!dvbS2) return new[] { "1/2", "2/3", "3/4", "5/6", "7/8" };
            switch (constellation)
            {
                case "8psk": return new[] { "3/5", "2/3", "3/4", "5/6", "8/9", "9/10" };
                case "16apsk": return new[] { "2/3", "3/4", "5/6", "8/9", "9/10" };
                case "32apsk": return new[] { "3/4", "5/6", "8/9", "9/10" };
                default: return new[] { "1/4", "1/3", "2/5", "1/2", "3/5", "2/3", "3/4", "4/5", "5/6", "8/9", "9/10" };
            }
        }

        /// <summary>Lowest FEC used as the start point in variable-FEC mode.</summary>
        public static string MinFec(string constellation)
        {
            switch (constellation)
            {
                case "8psk": return "3/5";
                case "16apsk": return "2/3";
                case "32apsk": return "3/4";
                default: return "1/4";
            }
        }

        public static int BitsPerSymbol(string constellation)
        {
            switch (constellation)
            {
                case "8psk": return 3;
                case "16apsk": return 4;
                case "32apsk": return 5;
                default: return 2;
            }
        }

        public static double FecValue(string fec)
        {
            var parts = fec.Split('/');
            return double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        // Empirical per-FEC corrections from the script (x / 10000)
        private static readonly Dictionary<string, Dictionary<string, int>> TsCorrection = new Dictionary<string, Dictionary<string, int>>
        {
            { "qpsk", new Dictionary<string, int> { {"1/4",9895},{"1/3",9935},{"2/5",9970},{"1/2",9978},{"3/5",9994},{"2/3",10010},{"3/4",10009},{"4/5",10014},{"5/6",10021},{"8/9",10028},{"9/10",10030} } },
            { "8psk", new Dictionary<string, int> { {"3/5",9980},{"2/3",9988},{"3/4",9995},{"5/6",10005},{"8/9",10015},{"9/10",10016} } },
            { "16apsk", new Dictionary<string, int> { {"2/3",9980},{"3/4",9981},{"5/6",9992},{"8/9",10001},{"9/10",10002} } },
            { "32apsk", new Dictionary<string, int> { {"3/4",9967},{"5/6",9977},{"8/9",9987},{"9/10",9988} } },
        };

        // Video bitrate adjustment per constellation / FEC (percent)
        private static readonly Dictionary<string, Dictionary<string, int>> VideoAdjust = new Dictionary<string, Dictionary<string, int>>
        {
            { "qpsk", new Dictionary<string, int> { {"1/4",70},{"1/3",80},{"2/5",91},{"1/2",94},{"3/5",96},{"2/3",98} } },
            { "8psk", new Dictionary<string, int> { {"3/5",101},{"2/3",101},{"3/4",101},{"5/6",102},{"8/9",102},{"9/10",102} } },
            { "16apsk", new Dictionary<string, int> { {"2/3",103},{"3/4",103},{"5/6",103},{"8/9",103},{"9/10",103} } },
            { "32apsk", new Dictionary<string, int> { {"3/4",104},{"5/6",104},{"8/9",104},{"9/10",104} } },
        };

        /// <summary>Calculated useful TS bitrate in kbit/s.</summary>
        public static double TsBitrateK(TxParams p)
        {
            double ts = BitsPerSymbol(p.Constellation) * p.SymbolRateKs * 188.0 / 204.0 * FecValue(p.Fec);
            if (p.DvbS2) ts = ts * 1075.0 / 1000.0;

            Dictionary<string, int> corr;
            int c;
            if (TsCorrection.TryGetValue(p.Constellation, out corr) && corr.TryGetValue(p.Fec, out c))
                ts = ts * c / 10000.0;

            if (p.ShortFrames) ts = ts * 9484.0 / 10000.0;
            if (p.Pilots) ts = ts * 9766.0 / 10000.0;
            return ts;
        }

        private static int VideoShare(int sr)
        {
            if (sr <= 35) return 50;
            if (sr <= 66) return 56;
            if (sr <= 125) return 66;
            if (sr <= 250) return 75;
            if (sr <= 333) return 81;
            if (sr <= 500) return 84;
            if (sr <= 1000) return 86;
            if (sr <= 1500) return 87;
            if (sr <= 3000) return 88;
            return 89;
        }

        /// <summary>Video bitrate (kbit/s) for a given TS bitrate, exactly as the script derives it.</summary>
        public static double VideoBitrateK(TxParams p, double tsK)
        {
            double v = tsK * VideoShare(p.SymbolRateKs) / 100.0 - p.AudioBitrateK;

            Dictionary<string, int> adj;
            int a;
            if (VideoAdjust.TryGetValue(p.Constellation, out adj) && adj.TryGetValue(p.Fec, out a))
                v = v * a / 100.0;

            // :CHECK section - extra headroom at very low rates
            double test = Math.Truncate(v);
            if (test > 1 && test < 61) v = v * 50 / 100.0;
            else if (test > 60 && test < 101) v = v * 70 / 100.0;

            return Math.Max(v, 1);
        }
    }

    /// <summary>All derived numbers needed to build an ffmpeg command.</summary>
    public class EncodePlan
    {
        public double TsBitrateK;
        public double VideoBitrateK;
        public double BufSizeK;
        public long MaxBitrate;      // bit/s (VVC)
        public long TargetBitrate;   // bit/s (VVC)
        public int MaxDelayMs;
        public int MaxInterleaveS;
        public int Keyframe;
        public bool TsFromDevice;

        public static EncodePlan Build(TxParams p, AppSettings s, double tsK, bool fromDevice)
        {
            var e = new EncodePlan { TsBitrateK = Math.Floor(tsK), TsFromDevice = fromDevice };
            e.VideoBitrateK = Math.Floor(DvbCalc.VideoBitrateK(p, tsK));
            e.MaxDelayMs = s.MaxDelayMs;
            e.MaxInterleaveS = s.MaxInterleaveS;

            e.MaxBitrate = (long)Math.Truncate(tsK * 910 - p.AudioBitrateK * 1000);
            e.TargetBitrate = (long)Math.Truncate(e.MaxBitrate / 1.65);
            if (p.Codec == "libvvenc")
            {
                e.VideoBitrateK = Math.Truncate(e.MaxBitrate / 1000.0);
                e.MaxDelayMs = e.MaxDelayMs * 10;
            }
            e.BufSizeK = Math.Truncate(e.VideoBitrateK * s.BufFactor);

            if (p.SymbolRateKs > 20 && p.SymbolRateKs < 36)
            {
                e.MaxInterleaveS = 0;
                e.MaxDelayMs = 2000;
            }

            e.Keyframe = (int)Math.Round(p.Fps * 5);
            if (Codecs.IsAv1(p.Codec)) e.Keyframe *= 2;
            return e;
        }
    }

    public static class Codecs
    {
        public static bool IsAv1(string c) { return c == "av1_nvenc" || c == "av1_amf" || c == "libaom-av1" || c == "libsvtav1"; }

        public class CodecChoice
        {
            public string Name; public string Value;
            public override string ToString() { return Name; }
        }

        public static readonly CodecChoice[] All =
        {
            new CodecChoice { Name = "H.264  CPU (x264)", Value = "libx264" },
            new CodecChoice { Name = "H.265  CPU (x265)", Value = "libx265" },
            new CodecChoice { Name = "H.264  AMD GPU", Value = "h264_amf" },
            new CodecChoice { Name = "H.265  AMD GPU", Value = "hevc_amf" },
            new CodecChoice { Name = "AV1    AMD GPU (RX 7000+)", Value = "av1_amf" },
            new CodecChoice { Name = "H.264  NVIDIA GPU", Value = "h264_nvenc" },
            new CodecChoice { Name = "H.265  NVIDIA GPU", Value = "hevc_nvenc" },
            new CodecChoice { Name = "AV1    NVIDIA GPU (RTX 40+)", Value = "av1_nvenc" },
            new CodecChoice { Name = "H.264  Intel GPU (QSV)", Value = "h264_qsv" },
            new CodecChoice { Name = "H.265  Intel GPU (QSV)", Value = "hevc_qsv" },
            new CodecChoice { Name = "H.266  CPU (vvenc)", Value = "libvvenc" },
            new CodecChoice { Name = "AV1    CPU (SVT-AV1)", Value = "libsvtav1" },
            new CodecChoice { Name = "AV1    CPU (libaom)", Value = "libaom-av1" },
        };

        public static readonly string[] Presets = { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow" };

        /// <summary>
        /// Advice when the video bit rate is too low for the picture size and frame rate (bits per pixel
        /// per frame below about 0.03 for H.265/AV1/VVC, 0.045 for H.264), or null. VMAF tests showed
        /// a lower frame rate is the most effective fix for blocking on motion.
        /// </summary>
        public static string LowRateAdvice(string codec, string imageSize, double fps, double videoK)
        {
            var m = System.Text.RegularExpressions.Regex.Match(imageSize ?? "", @"^(\d+)x(\d+)$");
            if (!m.Success || fps <= 0 || videoK <= 0) return null;
            double w = double.Parse(m.Groups[1].Value), h = double.Parse(m.Groups[2].Value);
            double bpp = videoK * 1000 / (w * h * fps);
            bool h264 = codec != null && codec.StartsWith("h264", System.StringComparison.Ordinal) || codec == "libx264";
            if (bpp >= (h264 ? 0.045 : 0.03)) return null;
            if (fps > 10) return "low for " + imageSize + " @ " + fps.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " fps: try 10 fps for less blocking on motion";
            if (w > 960) return "low for " + imageSize + ": try 960x540 or a higher symbol rate / FEC";
            return "low for this picture: try a higher symbol rate or FEC";
        }

        /// <summary>NVENC presets p1 (fastest) to p7 (best) for the x264-style speed presets.</summary>
        public static string NvencPreset(string preset)
        {
            int i = System.Array.IndexOf(Presets, preset);
            if (i < 0) i = 2;
            return new[] { "p1", "p2", "p3", "p4", "p4", "p5", "p6" }[i];
        }

        /// <summary>AMD AMF has three quality levels instead of x264-style presets.</summary>
        public static string AmfQuality(string preset)
        {
            int i = System.Array.IndexOf(Presets, preset);
            if (i < 0) i = 2;
            return i <= 2 ? "speed" : i <= 5 ? "balanced" : "quality";
        }
    }
}
