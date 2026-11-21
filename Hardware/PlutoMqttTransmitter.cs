using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DatvTx.Core;
using MQTTnet;
using MQTTnet.Client;

namespace DatvTx.Hardware
{
    /// <summary>
    /// ADALM-Pluto with F5OEO MQTT firmware. Uses the same topics and order as DATV-TX.bat / CONTROL.bat,
    /// but over one persistent MQTT connection instead of starting mosquitto_pub.exe for every command.
    /// </summary>
    public class PlutoMqttTransmitter : Transmitter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private IMqttClient _client;
        private readonly ConcurrentDictionary<string, string> _latest = new ConcurrentDictionary<string, string>();
        private TaskCompletionSource<string> _bitrateWaiter;
        private bool _wasConnected;

        public PlutoMqttTransmitter(AppSettings s) : base(s) { }

        public override string DisplayName { get { return "Pluto F5OEO " + Settings.PlutoIp; } }
        public override bool SupportsPtt { get { return true; } }
        public override bool SupportsLiveControl { get { return true; } }
        public override bool NeedsConnection { get { return true; } }
        public override bool IsConnected { get { return _client != null && _client.IsConnected; } }

        private string Root { get { return Settings.EffectiveCmdRoot; } }

        public override string StreamUrl()
        {
            return FfmpegBuilder.StreamUrl(Settings.PlutoIp, Settings.PlutoPort, Settings.Protocol, Settings.FifoBufMb);
        }

        public override string HelpText
        {
            get { return "F5OEO MQTT firmware  •  " + Root; }
        }

        // ------------------------------------------------------------------ connection

        public override async Task ConnectAsync()
        {
            if (IsConnected) return;
            if (_client != null) { try { _client.Dispose(); } catch { } _client = null; }
            var factory = new MqttFactory();
            _client = factory.CreateMqttClient();
            _client.ApplicationMessageReceivedAsync += OnMessage;
            _client.DisconnectedAsync += e =>
            {
                if (!_wasConnected) return Task.FromResult(0);
                _wasConnected = false;
                RaiseLog("Pluto MQTT disconnected.");
                RaiseConnectionChanged();
                return Task.FromResult(0);
            };

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(Settings.PlutoIp, Settings.PlutoMqttPort)
                .WithClientId("DatvTx-" + Guid.NewGuid().ToString("N").Substring(0, 8))
                .WithCleanSession()
                .WithTimeout(TimeSpan.FromSeconds(10))
                .Build();

            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await _client.ConnectAsync(options, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("the Pluto did not answer within 10 s. Is it powered up (it takes about 30 s to boot) and is the IP address right?");
            }
            _wasConnected = true;
            await _client.SubscribeAsync("dt/#");
            RaiseLog("Connected to Pluto MQTT at " + Settings.PlutoIp + ".");
            RaiseConnectionChanged();
        }

        public override async Task DisconnectAsync()
        {
            var c = _client;
            _client = null;
            if (c != null)
            {
                try { if (c.IsConnected) await c.DisconnectAsync(); } catch { }
                c.Dispose();
            }
            RaiseConnectionChanged();
        }

        private Task OnMessage(MqttApplicationMessageReceivedEventArgs e)
        {
            string topic = e.ApplicationMessage.Topic;
            string payload = e.ApplicationMessage.ConvertPayloadToString() ?? "";
            _latest[topic] = payload;

            if (topic.EndsWith("/tx/dvbs2/ts/bitrate", StringComparison.Ordinal) || topic.EndsWith("/tx/dvbs/ts/bitrate", StringComparison.Ordinal))
            {
                var w = _bitrateWaiter;
                if (w != null) w.TrySetResult(payload);
            }

            // Pass a short key such as "tx/gain" or "system/temperature" to the UI.
            string key = topic;
            int idx = topic.IndexOf("/tx/", StringComparison.Ordinal);
            if (idx < 0) idx = topic.IndexOf("/system/", StringComparison.Ordinal);
            if (idx >= 0) key = topic.Substring(idx + 1);
            RaiseTelemetry(key, payload);
            return Task.FromResult(0);
        }

        private async Task Pub(string topic, string value)
        {
            if (!IsConnected) throw new InvalidOperationException("Not connected to the Pluto.");
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(value)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await _client.PublishAsync(msg);
        }

        private Task Cmd(string sub, string value) { return Pub(Root + "/" + sub, value); }

        private string StreamMode(TxParams p) { return p.DvbS2 ? Settings.TxStreamMode : "dvbs"; }

        private static string F(double d) { return d.ToString("0.###", Inv); }
        private static string Hz(double mhz) { return mhz.ToString("0.######", Inv) + "e6"; }

        // ------------------------------------------------------------------ modulator

        public override async Task<double?> FetchTsBitrateKAsync(TxParams p)
        {
            if (!Settings.FetchTsBitrate || !IsConnected) return null;

            _bitrateWaiter = new TaskCompletionSource<string>();
            await Cmd("tx/dvbs2/sr", (p.SymbolRateKs * 1000).ToString(Inv));
            await Cmd("tx/stream/mode", StreamMode(p));
            await Cmd("tx/dvbs2/fec", p.Fec);
            await Cmd("tx/dvbs2/constel", p.Constellation);
            await Cmd("tx/dvbs2/pilots", p.Pilots ? "1" : "0");
            await Cmd("tx/dvbs2/frame", p.ShortFrames ? "short" : "long");

            // Ignore any retained value from before the change, as the script's "timeout 1" did.
            await Task.Delay(1000);
            _bitrateWaiter = new TaskCompletionSource<string>();
            var done = await Task.WhenAny(_bitrateWaiter.Task, Task.Delay(4000));
            string value = done == _bitrateWaiter.Task ? _bitrateWaiter.Task.Result : null;
            _bitrateWaiter = null;

            if (value == null)
            {
                // fall back to the most recent value we have
                foreach (var kv in _latest)
                    if (kv.Key.EndsWith("/ts/bitrate", StringComparison.Ordinal)) value = kv.Value;
            }

            double bps;
            if (value != null && double.TryParse(value.Trim(), NumberStyles.Float, Inv, out bps) && bps > 0)
            {
                RaiseLog("TS bitrate from Pluto: " + F(bps / 1000) + " kbit/s");
                return bps / 1000.0;
            }
            RaiseLog("Pluto did not report a TS bitrate; using the calculated value.");
            return null;
        }

        public override async Task ConfigureAsync(TxParams p, bool pttOn)
        {
            bool variable = Settings.VariableFec;
            string fec = variable ? DvbCalc.MinFec(p.Constellation) : p.Fec;

            await Cmd("tx/dvbs2/tssourceaddress", Settings.PlutoIp + ":" + Settings.PlutoPort);
            await Cmd("tx/gain", F(Math.Min(p.GainDb, Settings.PowerLimitDb)));
            await Cmd("tx/mute", pttOn ? "0" : "1");
            await Cmd("tx/frequency", Hz(p.TxFrequencyMHz));
            await Cmd("tx/dvbs2/sr", (p.SymbolRateKs * 1000).ToString(Inv));
            await Cmd("tx/nco", Settings.NcoHz.ToString(Inv));
            await Cmd("tx/stream/mode", StreamMode(p));
            await Cmd("tx/dvbs2/fec", fec);
            await Cmd("tx/dvbs2/constel", p.Constellation);
            await Cmd("tx/dvbs2/pilots", p.Pilots ? "1" : "0");
            await Cmd("tx/dvbs2/frame", p.ShortFrames ? "short" : "long");
            await Cmd("tx/dvbs2/fecmode", variable ? "variable" : "fixed");
            await Cmd("tx/dvbs2/agcgain", Settings.AgcGain.ToString(Inv));
            await Cmd("tx/dvbs2/gainvariable", Settings.GainVariable ? "1" : "0");
            await Cmd("tx/dvbs2/digitalgain", "0");
            await Cmd("tx/dvbs2/fecrange", Settings.FecRange.ToString(Inv));
            await Cmd("tx/dvbs2/tssourcemode", Settings.TsSourceMode.ToString(Inv));
            await Cmd("tx/dvbs2/tssourcefile", Settings.TsSourceFile ?? "");
            await Cmd("tx/dvbs2/firfilter", FirFilter(p.RollOff));
            RaiseLog("Pluto configured: " + p.Summary() + (variable ? " (variable FEC from " + fec + ")" : ""));
        }

        public override async Task ShutdownAsync()
        {
            if (!IsConnected) return;
            try
            {
                await Cmd("tx/mute", "1");
                if (Settings.RebootOnStop) await Cmd("system/reboot", "1");
                RaiseLog("Pluto muted" + (Settings.RebootOnStop ? " and rebooting." : "."));
            }
            catch (Exception ex) { RaiseLog("Shutdown: " + ex.Message); }
        }

        public override Task SetPttAsync(bool on) { return Cmd("tx/mute", on ? "0" : "1"); }

        public override Task SetGainAsync(double db)
        {
            if (db > Settings.PowerLimitDb) db = Settings.PowerLimitDb;
            return Cmd("tx/gain", F(db));
        }

        public override Task SetDigitalGainAsync(double db)
        {
            db = Math.Max(-20, Math.Min(3, db));
            return Cmd("tx/dvbs2/digitalgain", db.ToString("0.##", Inv));
        }

        public override Task SetFrequencyAsync(double mhz) { return Cmd("tx/frequency", Hz(mhz)); }

        public override Task SetRollOffAsync(double rollOff) { return Cmd("tx/dvbs2/firfilter", FirFilter(rollOff)); }

        /// <summary>F5OEO firmware output filter: 0 = 0.20 roll-off, 1 = 0.15.</summary>
        public override double[] RollOffs { get { return new[] { 0.20, 0.15 }; } }

        private static string FirFilter(double rollOff) { return rollOff < 0.175 ? "1" : "0"; }

        public override async Task SendCallsignAsync()
        {
            await Pub("cmd/pluto/call", Settings.Callsign);
            RaiseLog("Callsign " + Settings.Callsign + " sent. Reboot the Pluto for it to take effect.");
        }

        public override void Dispose()
        {
            try { if (_client != null) _client.Dispose(); } catch { }
            _client = null;
        }
    }
}
