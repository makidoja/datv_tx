using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using DatvTx.Core;
using DatvTx.Hardware;

namespace DatvTx.UI
{
    public sealed class SettingsForm : Form
    {
        public AppSettings Result { get; private set; }
        private readonly PropertyGrid _grid;
        private readonly Label _status;

        public SettingsForm(AppSettings working, Transmitter current)
        {
            Result = working;
            Text = "Settings - " + MainForm.ProductTitle;
            Icon = Theme.AppIcon();
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9f);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(Theme.S(720), Theme.S(760));
            MinimumSize = new Size(Theme.S(560), Theme.S(500));
            AutoScaleMode = AutoScaleMode.None;

            _grid = new PropertyGrid
            {
                Dock = DockStyle.Fill,
                SelectedObject = working,
                PropertySort = PropertySort.Categorized,
                ToolbarVisible = false,
                HelpVisible = true,
                BackColor = Theme.Surface,
                ViewBackColor = Theme.Surface2,
                ViewForeColor = Theme.Text,
                LineColor = Theme.Surface,
                CategoryForeColor = Theme.Text,
                HelpBackColor = Theme.Surface,
                HelpForeColor = Theme.Muted,
                CommandsBackColor = Theme.Surface,
                CommandsForeColor = Theme.Text
            };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(58), BackColor = Theme.Surface, Padding = new Padding(Theme.S(12), Theme.S(10), Theme.S(12), Theme.S(10)) };
            var ok = Theme.Button("SAVE", true); ok.Size = new Size(Theme.S(100), Theme.S(36)); ok.Dock = DockStyle.Right;
            var cancel = Theme.Button("CANCEL"); cancel.Size = new Size(Theme.S(100), Theme.S(36)); cancel.Dock = DockStyle.Right;
            var gap = new Panel { Dock = DockStyle.Right, Width = Theme.S(8) };
            var callsign = Theme.Button("SEND CALLSIGN TO PLUTO"); callsign.Size = new Size(Theme.S(190), Theme.S(36)); callsign.Dock = DockStyle.Left;
            var gap2 = new Panel { Dock = DockStyle.Left, Width = Theme.S(8) };
            var test = Theme.Button("TEST CONNECTION"); test.Size = new Size(Theme.S(140), Theme.S(36)); test.Dock = DockStyle.Left;
            _status = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Theme.S(8), Theme.S(0), Theme.S(0), Theme.S(0)) };

            bottom.Controls.Add(_status);
            bottom.Controls.Add(test);
            bottom.Controls.Add(gap2);
            bottom.Controls.Add(callsign);
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(gap);
            bottom.Controls.Add(ok);

            Controls.Add(_grid);
            Controls.Add(bottom);

            ok.Click += (s, e) => { Result.Normalise(); DialogResult = DialogResult.OK; Close(); };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            AcceptButton = ok;
            CancelButton = cancel;

            test.Click += async (s, e) => await WithPluto(false);
            callsign.Click += async (s, e) => await WithPluto(true);

            Load += (s, e) => Theme.DarkTitleBar(this);
        }

        private async Task WithPluto(bool sendCallsign)
        {
            bool is0303 = Result.Hardware == HardwareType.Pluto0303;
            if (Result.Hardware != HardwareType.PlutoF5oeoMqtt && !(is0303 && !sendCallsign))
            {
                _status.Text = is0303 ? "On 0303 firmware the callsign is sent at each START." : "Only needed for a Pluto with F5OEO MQTT or 0303 firmware.";
                return;
            }
            _status.Text = "Connecting to " + Result.PlutoIp + "...";
            Transmitter tx = is0303 ? (Transmitter)new Pluto0303Transmitter(Result) : new PlutoMqttTransmitter(Result);
            try
            {
                await tx.ConnectAsync();
                if (sendCallsign)
                {
                    await tx.SendCallsignAsync();
                    _status.Text = "Callsign sent. Reboot the Pluto.";
                }
                else _status.Text = "Connected OK to " + Result.PlutoIp + ".";
            }
            catch (Exception ex) { _status.Text = "Failed: " + ex.Message; }
            finally
            {
                try { await tx.DisconnectAsync(); } catch { }
                tx.Dispose();
            }
        }
    }
}
