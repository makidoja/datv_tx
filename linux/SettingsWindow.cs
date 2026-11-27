using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DatvTx.Core;
using DatvTx.Hardware;

namespace DatvTx.LinuxUi
{
    /// <summary>
    /// Settings editor built from the [Category] / [DisplayName] / [Description] attributes on
    /// AppSettings (the Windows app shows the same properties in a PropertyGrid).
    /// </summary>
    public sealed class SettingsWindow : Window
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public AppSettings Result { get; private set; }
        private readonly List<Action> _commit = new List<Action>();
        private readonly TextBlock _status;

        public SettingsWindow(AppSettings working, Transmitter tx)
        {
            Result = working;
            Title = "Settings - DATV TX (M0CKE)";
            Width = 760; Height = 700;
            MinWidth = 560; MinHeight = 420;
            Background = LTheme.B(LTheme.Bg);
            Icon = LTheme.AppIcon();
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var list = new StackPanel { Spacing = 2, Margin = new Thickness(14, 10, 14, 10) };
            var props = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .Where(p => { var b = p.GetCustomAttribute<BrowsableAttribute>(); return b == null || b.Browsable; })
                .Where(p => p.GetCustomAttribute<Newtonsoft.Json.JsonIgnoreAttribute>() == null)
                .Select(p => new { P = p, Cat = (p.GetCustomAttribute<CategoryAttribute>()?.Category) ?? "Other" })
                .OrderBy(x => x.Cat, StringComparer.Ordinal)
                .ToList();

            string lastCat = null;
            foreach (var x in props)
            {
                var editor = Editor(x.P);
                if (editor == null) continue;
                if (x.Cat != lastCat)
                {
                    lastCat = x.Cat;
                    var head = LTheme.Label(StripNumber(x.Cat).ToUpperInvariant(), 12.5, true, LTheme.Text);
                    head.Margin = new Thickness(0, 14, 0, 4);
                    list.Children.Add(head);
                }
                string name = x.P.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? x.P.Name;
                string desc = x.P.GetCustomAttribute<DescriptionAttribute>()?.Description;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), MinHeight = 32 };
                var l = LTheme.Label(name, 12.5);
                ToolTip.SetTip(l, desc);
                ToolTip.SetTip(editor, desc);
                editor.Margin = new Thickness(0, 2);
                Grid.SetColumn(editor, 1);
                row.Children.Add(l);
                row.Children.Add(editor);
                list.Children.Add(row);
                if (!string.IsNullOrEmpty(desc))
                {
                    var d = LTheme.Label(desc, 11);
                    d.Margin = new Thickness(260, 0, 0, 4);
                    d.Opacity = 0.8;
                    list.Children.Add(d);
                }
            }

            var scroll = new ScrollViewer { Content = list, Background = LTheme.B(LTheme.Surface) };

            var save = LTheme.Button("SAVE", true); save.Width = 100; save.Height = 36;
            var cancel = LTheme.Button("CANCEL"); cancel.Width = 100; cancel.Height = 36;
            var test = LTheme.Button("TEST CONNECTION"); test.Width = 150; test.Height = 36;
            var call = LTheme.Button("SEND CALLSIGN TO PLUTO"); call.Width = 200; call.Height = 36;
            _status = LTheme.Label("", 12);
            var bottom = new DockPanel { Margin = new Thickness(14, 10), LastChildFill = true };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            right.Children.Add(save); right.Children.Add(cancel);
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            left.Children.Add(call); left.Children.Add(test);
            DockPanel.SetDock(right, Dock.Right);
            DockPanel.SetDock(left, Dock.Left);
            _status.Margin = new Thickness(10, 0);
            bottom.Children.Add(right);
            bottom.Children.Add(left);
            bottom.Children.Add(_status);

            var root = new DockPanel();
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            root.Children.Add(scroll);
            Content = root;

            save.Click += (s, e) =>
            {
                try { foreach (var c in _commit) c(); }
                catch (Exception ex) { _status.Text = ex.Message; return; }
                Result.Normalise();
                Close(true);
            };
            cancel.Click += (s, e) => Close(false);
            test.Click += async (s, e) => await WithPluto(false);
            call.Click += async (s, e) => await WithPluto(true);
        }

        private static string StripNumber(string cat)
        {
            int i = 0;
            while (i < cat.Length && (char.IsDigit(cat[i]) || char.IsLetter(cat[i]) && i > 0 && char.IsDigit(cat[i - 1]))) i++;
            return cat.Substring(i).Trim();
        }

        private Control Editor(PropertyInfo p)
        {
            Type t = p.PropertyType;
            object value = p.GetValue(Result);
            if (t == typeof(bool))
            {
                var c = new CheckBox { IsChecked = (bool)value };
                _commit.Add(() => p.SetValue(Result, c.IsChecked == true));
                return c;
            }
            if (t.IsEnum)
            {
                var values = Enum.GetValues(t).Cast<object>().ToList();
                var c = LTheme.Combo();
                foreach (var v in values) c.Items.Add(EnumDescConverter.Describe(v));
                c.SelectedIndex = Math.Max(0, values.IndexOf(value));
                _commit.Add(() => { if (c.SelectedIndex >= 0) p.SetValue(Result, values[c.SelectedIndex]); });
                return c;
            }
            if (t == typeof(string) || t == typeof(int) || t == typeof(double) || t == typeof(decimal) || t == typeof(long))
            {
                var tb = LTheme.TextBox();
                tb.Text = Convert.ToString(value, Inv);
                string name = p.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? p.Name;
                _commit.Add(() =>
                {
                    string s = tb.Text ?? "";
                    try
                    {
                        if (t == typeof(string)) p.SetValue(Result, s);
                        else if (t == typeof(int)) p.SetValue(Result, int.Parse(s.Trim(), Inv));
                        else if (t == typeof(long)) p.SetValue(Result, long.Parse(s.Trim(), Inv));
                        else if (t == typeof(double)) p.SetValue(Result, double.Parse(s.Trim().Replace(',', '.'), Inv));
                        else p.SetValue(Result, decimal.Parse(s.Trim().Replace(',', '.'), Inv));
                    }
                    catch { throw new FormatException("\"" + name + "\" is not a valid number."); }
                });
                return tb;
            }
            return null;
        }

        private async Task WithPluto(bool sendCallsign)
        {
            try { foreach (var c in _commit) c(); } catch (Exception ex) { _status.Text = ex.Message; return; }
            bool is0303 = Result.Hardware == HardwareType.Pluto0303;
            if (Result.Hardware != HardwareType.PlutoF5oeoMqtt && !(is0303 && !sendCallsign))
            {
                _status.Text = is0303 ? "On 0303 firmware the callsign is sent at each START." : "Only needed for a Pluto with F5OEO MQTT or 0303 firmware.";
                return;
            }
            if (sendCallsign && string.IsNullOrWhiteSpace(Result.Callsign)) { _status.Text = "Enter your callsign first (Station → Callsign)."; return; }
            _status.Text = "Connecting to " + Result.PlutoIp + "...";
            Transmitter tx = is0303 ? (Transmitter)new Pluto0303Transmitter(Result) : new PlutoMqttTransmitter(Result);
            try
            {
                await tx.ConnectAsync();
                if (sendCallsign) { await tx.SendCallsignAsync(); _status.Text = "Callsign sent. Reboot the Pluto."; }
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
