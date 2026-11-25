using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DatvTx.Core
{
    /// <summary>
    /// Builds the ffmpeg command line exactly as DATV-TX.bat does (:ENCODER section).
    /// </summary>
    public static class FfmpegBuilder
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Q(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            if (s.IndexOfAny(new[] { ' ', '&', '"', '(', ')', ',' }) < 0) return s;
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        private static string N(double d) { return d.ToString("0.###", Inv); }

        /// <summary>
        /// Audio sample rate: AAC at very low bitrates sounds better (and ffmpeg stops warning) at a lower rate.
        /// </summary>
        public static int AudioRate(TxParams p, AppSettings s)
        {
            double perChannel = p.AudioBitrateK / (double)System.Math.Max(1, p.AudioChannels);
            if (s.AudioCodec == "aac" || s.AudioCodec == "libfdk_aac")
            {
                if (perChannel <= 8) return System.Math.Min(s.AudioSampleRate, 24000);
                if (perChannel <= 16) return System.Math.Min(s.AudioSampleRate, 32000);
            }
            return s.AudioSampleRate;
        }

        /// <param name="inputOverride">Replaces the input section (used by the build self-test).</param>
        /// <param name="outputPrefix">Inserted before the output (e.g. "-t 4" for the self-test).</param>
        public static string BuildArgs(TxParams p, AppSettings s, EncodePlan e, string outputUrl, string inputOverride = null, string outputPrefix = null)
        {
            var a = new List<string>();
            a.Add("-hide_banner");
            a.Add("-itsoffset " + N(s.AudioOffset));
            // ---- input
            // Current FFmpeg (7.x/8.x) no longer accepts -thread_queue_size on inputs, so only the
            // DirectShow real-time buffer is set here.
            string rtbuf = "-rtbufsize " + s.RtBufMb + "M";
            if (inputOverride != null)
            {
                a.Add(inputOverride);
            }
            else
            {
                switch (p.Input)
                {
                    case InputType.Capture:
                        a.Add("-f dshow " + rtbuf + " -i " + Q("video=" + p.VideoDevice));
                        if (!string.IsNullOrWhiteSpace(p.AudioDevice))
                            a.Add("-f dshow " + rtbuf + " -i " + Q("audio=" + p.AudioDevice));
                        break;
                    case InputType.NetworkUdp:
                        a.Add("-i " + Q(s.InputUdp));
                        break;
                    case InputType.NetworkRtmp:
                        a.Add("-f flv -listen 1 -i " + Q(s.InputRtmp));
                        break;
                    case InputType.File:
                        a.Add("-re -stream_loop " + s.FileLoop + " -i " + Q(p.InputFile));
                        break;
                }
            }

            a.Add("-ar " + AudioRate(p, s));
            a.Add("-vcodec " + p.Codec);
            a.Add("-s " + p.ImageSize);
            // Webcams often deliver MJPEG/YUY2 (4:2:2). GPU encoders reject 4:2:2 and DATV receivers
            // expect 4:2:0, so always convert. VVC picks its own 10-bit format.
            if (p.Codec.EndsWith("_qsv", StringComparison.Ordinal)) a.Add("-pix_fmt nv12");
            else if (p.Codec != "libvvenc") a.Add("-pix_fmt yuv420p");
            a.Add("-b:v " + N(e.VideoBitrateK) + "K");

            string fps = N(p.Fps);
            string preset = string.IsNullOrEmpty(p.Preset) ? s.PresetSoft : p.Preset;
            // The TS muxer pads to the exact channel rate (-muxrate), so the encoder need not be forced
            // constant: cap it (maxrate) and let it spend less on still scenes. The buffer stays at the
            // original size; a bigger one let bursts exceed the channel rate.
            string shaping = s.StrictCbr
                ? "-minrate " + N(e.VideoBitrateK) + "K -maxrate " + N(e.VideoBitrateK) + "K -bufsize " + N(e.BufSizeK) + "K"
                : "-maxrate " + N(e.VideoBitrateK) + "K -bufsize " + N(e.BufSizeK) + "K";
            string muxrate = "-muxrate " + N(e.TsBitrateK) + "K";
            bool cbr = !s.Vbr;

            switch (p.Codec)
            {
                case "h264_nvenc":
                case "hevc_nvenc":
                    a.Add("-preset " + (string.IsNullOrWhiteSpace(s.PresetHard) ? Codecs.NvencPreset(preset) : s.PresetHard.Trim()) + " -r " + fps + " -rc-lookahead 10 -no-scenecut 1 -zerolatency 1 -b_ref_mode 0 -bf 0");
                    a.Add(shaping);
                    break;
                case "libx264":
                    a.Add("-preset " + preset + " -r " + fps + " -x264-params rc-lookahead=10:no-scenecut=1");
                    a.Add(shaping);
                    break;
                case "libx265":
                    a.Add("-preset " + preset + " -r " + fps);
                    a.Add(shaping);
                    break;
                case "h264_amf":
                    a.Add("-usage lowlatency -quality " + Codecs.AmfQuality(preset) + " -rc cbr -bf 0 -r " + fps);
                    a.Add(shaping);
                    break;
                case "hevc_amf":
                case "av1_amf":
                    a.Add("-usage lowlatency -quality " + Codecs.AmfQuality(preset) + " -rc cbr -r " + fps);
                    a.Add(shaping);
                    break;
                case "h264_qsv":
                case "hevc_qsv":
                    a.Add("-preset " + preset.Replace("ultrafast", "veryfast").Replace("superfast", "veryfast") + " -look_ahead 0 -r " + fps);
                    a.Add(shaping);
                    break;
                case "libvvenc":
                    // DL5OCD's custom build had -subjopt; standard FFmpeg's libvvenc does not.
                    a.Add("-preset " + s.PresetVvc + " -r " + fps);
                    a.Add("-vvenc-params RefreshSec=2:targetBitrate=" + e.TargetBitrate + ":MaxBitrate=" + e.MaxBitrate + ":FirstPassMode=2:POC0IDR=1");
                    break;
                case "av1_nvenc":
                    a.Add("-crf " + s.Av1Quality + " -usage realtime");
                    a.Add(shaping);
                    cbr = false; // script never sets muxrate for av1_nvenc
                    break;
                case "libaom-av1":
                    a.Add("-row-mt true -crf " + s.Av1Quality + " -usage realtime");
                    a.Add(shaping);
                    break;
                case "libsvtav1":
                    // Current SVT-AV1 rejects a max bitrate in VBR mode. Use its CBR mode
                    // (needs the low-delay prediction structure) and a real-time preset.
                    a.Add("-preset 10 -r " + fps + " -svtav1-params tune=0:rc=2:pred-struct=1");
                    break;
            }

            a.Add("-g " + e.Keyframe);
            a.Add("-acodec " + s.AudioCodec);
            a.Add("-ac " + p.AudioChannels);
            a.Add("-b:a " + p.AudioBitrateK + "k");
            a.Add("-f " + (Codecs.IsAv1(p.Codec) ? "nut" : "mpegts"));
            if (cbr) a.Add(muxrate);
            a.Add("-streamid 0:" + s.VideoPid + " -streamid 1:" + s.AudioPid);
            a.Add("-max_delay " + e.MaxDelayMs + "K");
            a.Add("-max_interleave_delta " + e.MaxInterleaveS + "M");
            a.Add("-pcr_period " + s.PcrPeriod + " -pat_period " + N(s.PatPeriod));
            a.Add("-mpegts_original_network_id " + s.NetworkId + " -mpegts_transport_stream_id " + s.StreamId);
            a.Add("-mpegts_pmt_start_pid " + s.PmtPid + " -mpegts_start_pid " + s.StartPid);
            a.Add("-metadata service_provider=" + Q(s.ServiceProvider));
            a.Add("-metadata service_name=" + Q(s.Callsign));
            a.Add("-af aresample=async=1");
            if (outputPrefix != null) a.Add(outputPrefix);
            a.Add(Q(outputUrl));

            var sb = new StringBuilder();
            foreach (var part in a) { if (sb.Length > 0) sb.Append(' '); sb.Append(part); }
            return sb.ToString();
        }

        /// <summary>UDP/SRT URL with the packet options the script uses.</summary>
        public static string StreamUrl(string host, int port, StreamProtocol protocol, int fifoMb)
        {
            if (protocol == StreamProtocol.Srt)
                return "srt://" + host + ":" + port + "?pkt_size=1316";
            return "udp://" + host + ":" + port + "?pkt_size=1316&overrun_nonfatal=1&fifo_size=" + fifoMb + "M";
        }
    }
}
