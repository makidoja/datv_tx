using System;
using System.Threading.Tasks;
using DatvTx.Core;

namespace DatvTx.Hardware
{
    /// <summary>
    /// A DATV transmitter back end. The UI talks only to this class, so new radios can be added
    /// without touching the window code.
    /// </summary>
    public abstract class Transmitter : IDisposable
    {
        protected readonly AppSettings Settings;

        protected Transmitter(AppSettings settings) { Settings = settings; }

        public abstract string DisplayName { get; }

        /// <summary>True if the app can switch the carrier on/off itself.</summary>
        public virtual bool SupportsPtt { get { return false; } }

        /// <summary>True if frequency/gain/roll-off can be changed while running.</summary>
        public virtual bool SupportsLiveControl { get { return false; } }

        /// <summary>True if this back end needs a connection before use (shows the CONNECT button).</summary>
        public virtual bool NeedsConnection { get { return false; } }


        public virtual bool IsConnected { get { return !NeedsConnection; } }

        public event Action<string> Log;
        public event Action ConnectionChanged;
        public event Action<string, string> Telemetry;

        /// <summary>Raised when the transmitter stops by itself (e.g. the radio was unplugged).</summary>
        public event Action<string> Fault;
        protected void RaiseFault(string why) { var h = Fault; if (h != null) h(why); }

        protected void RaiseLog(string msg) { var h = Log; if (h != null) h(msg); }
        protected void RaiseConnectionChanged() { var h = ConnectionChanged; if (h != null) h(); }
        protected void RaiseTelemetry(string key, string value) { var h = Telemetry; if (h != null) h(key, value); }

        public virtual Task ConnectAsync() { return Task.FromResult(0); }
        public virtual Task DisconnectAsync() { return Task.FromResult(0); }

        /// <summary>Where ffmpeg should send the transport stream.</summary>
        public abstract string StreamUrl();

        /// <summary>Exact TS bitrate in kbit/s reported by the radio, or null to use the calculated value.</summary>
        public virtual Task<double?> FetchTsBitrateKAsync(TxParams p) { return Task.FromResult<double?>(null); }

        /// <summary>Configure the modulator before the stream starts.</summary>
        public virtual Task ConfigureAsync(TxParams p, bool pttOn) { return Task.FromResult(0); }

        /// <summary>Called after the encoder has been stopped.</summary>
        public virtual Task ShutdownAsync() { return Task.FromResult(0); }

        public virtual Task SetPttAsync(bool on) { return Task.FromResult(0); }
        public virtual Task SetGainAsync(double db) { return Task.FromResult(0); }
        public virtual Task SetDigitalGainAsync(double db) { return Task.FromResult(0); }
        public virtual Task SetFrequencyAsync(double mhz) { return Task.FromResult(0); }
        public virtual Task SetRollOffAsync(double rollOff) { return Task.FromResult(0); }

        /// <summary>Roll-off values this transmitter supports (first = default).</summary>
        public virtual double[] RollOffs { get { return new[] { 0.20, 0.25, 0.35 }; } }

        /// <summary>One-off setup (SETUP.bat): tell the Pluto its callsign.</summary>
        public virtual Task SendCallsignAsync() { return Task.FromResult(0); }

        /// <summary>Short help text shown when this back end is selected.</summary>
        public virtual string HelpText { get { return ""; } }

        /// <summary>Hardware-specific limits; returns an error message or null.</summary>
        public virtual string Validate(TxParams p) { return null; }

        public virtual void Dispose() { }

        public static Transmitter Create(AppSettings s)
        {
            switch (s.Hardware)
            {
                case HardwareType.PlutoF5oeoMqtt: return new PlutoMqttTransmitter(s);
                case HardwareType.Pluto0303: return new Pluto0303Transmitter(s);
                case HardwareType.PlutoLegacyUdp: return new PlutoLegacyTransmitter(s);
                case HardwareType.LimeSdr: return new SdrHelperTransmitter(s, "lime");
                case HardwareType.HackRf: return new SdrHelperTransmitter(s, "hackrf");
                case HardwareType.Muntjac: return new SdrHelperTransmitter(s, "muntjac");
                case HardwareType.Esp32Datv: return new SdrHelperTransmitter(s, "esp32");
                default: return new LocalTestTransmitter(s);
            }
        }
    }

    /// <summary>Pluto running older F5OEO firmware: the stream is sent by UDP and the radio is set up in its web page.</summary>
    public class PlutoLegacyTransmitter : Transmitter
    {
        public PlutoLegacyTransmitter(AppSettings s) : base(s) { }
        public override string DisplayName { get { return "Pluto (older F5OEO firmware) " + Settings.PlutoIp; } }
        public override string StreamUrl() { return FfmpegBuilder.StreamUrl(Settings.PlutoIp, Settings.PlutoPort, Settings.Protocol, Settings.FifoBufMb); }
        public override string HelpText
        {
            get { return "Older firmware: set frequency, SR, FEC and gain in the Pluto web page (http://" + Settings.PlutoIp + "). This app encodes and sends the stream only."; }
        }
    }

    /// <summary>No radio: the stream goes to a local address so it can be checked with ffplay/VLC.</summary>
    public class LocalTestTransmitter : Transmitter
    {
        public LocalTestTransmitter(AppSettings s) : base(s) { }
        public override string DisplayName { get { return "Local test (" + Settings.LocalTestUrl + ")"; } }
        public override string StreamUrl()
        {
            string u = Settings.LocalTestUrl;
            if (u.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) && u.IndexOf('?') < 0)
                u += "?pkt_size=1316&overrun_nonfatal=1&fifo_size=" + Settings.FifoBufMb + "M";
            return u;
        }
        public override string HelpText { get { return "Nothing is transmitted. Use Monitor to watch the encoded stream."; } }
    }
}
