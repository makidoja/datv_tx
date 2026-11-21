using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DatvTx.UI
{
    /// <summary>Colours and control styling copied from OpenTuner M0CKE (ModernConceptForm / ModernWindowTheme).</summary>
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(7, 17, 29);
        public static readonly Color Surface = Color.FromArgb(13, 28, 45);
        public static readonly Color Surface2 = Color.FromArgb(18, 38, 60);
        public static readonly Color Border = Color.FromArgb(37, 67, 94);
        public static readonly Color Accent = Color.FromArgb(28, 139, 253);
        public static readonly Color Green = Color.FromArgb(40, 222, 126);
        public static readonly Color GreenFill = Color.FromArgb(17, 91, 59);
        public static readonly Color GreenBorder = Color.FromArgb(38, 140, 86);
        public static readonly Color Grey = Color.FromArgb(75, 84, 95);
        public static readonly Color GreyBorder = Color.FromArgb(108, 119, 131);
        public static readonly Color Red = Color.FromArgb(214, 48, 49);
        public static readonly Color RedBorder = Color.FromArgb(255, 99, 99);
        public static readonly Color Amber = Color.FromArgb(240, 170, 40);
        public static readonly Color Text = Color.FromArgb(242, 247, 252);
        public static readonly Color Muted = Color.FromArgb(142, 165, 190);
        public static readonly Color LogBg = Color.FromArgb(9, 24, 39);

        /// <summary>Display scaling (DPI / 96). Set once at start-up; all layout pixel sizes go through S().</summary>
        public static float Scale = 1f;
        public static int S(int px) { return (int)Math.Round(px * Scale); }

        public static Font Ui(float size) { return new Font("Segoe UI", size); }
        public static Font Semi(float size) { return new Font("Segoe UI Semibold", size); }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void DarkTitleBar(Form f)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(f.Handle, 19, ref on, sizeof(int));
            }
            catch { }
        }

        public static Panel Card()
        {
            return new Panel { BackColor = Surface, ForeColor = Text, Padding = new Padding(S(14), S(10), S(14), S(12)) };
        }

        public static Label CardTitle(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Top, Height = S(30), ForeColor = Text, Font = Semi(10f), TextAlign = ContentAlignment.MiddleLeft };
        }

        public static Label FieldLabel(string text)
        {
            return new Label { Text = text, AutoSize = false, Dock = DockStyle.Fill, ForeColor = Muted, Font = Ui(9f), TextAlign = ContentAlignment.MiddleLeft };
        }

        public static Button Button(string text, bool accent = false)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = accent ? Accent : Surface2,
                ForeColor = Text,
                Font = Semi(9f),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderColor = accent ? Accent : Border;
            b.FlatAppearance.BorderSize = 1;
            return b;
        }

        public static void Paint(Button b, Color fill, Color border)
        {
            b.BackColor = fill;
            b.FlatAppearance.BorderColor = border;
        }

        public static ComboBox Combo(bool editable = false)
        {
            var cb = new DarkCombo
            {
                DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
                BackColor = Surface2,
                ForeColor = Text,
                Font = Ui(9.5f),
                Dock = DockStyle.Fill,
                Margin = new Padding(0, S(3), 0, S(3)),
                IntegralHeight = false,
                MaxDropDownItems = 16
            };
            cb.ItemHeight = S(20);
            return cb;
        }

        public static NumericUpDown Number(decimal min, decimal max, decimal step, int decimals)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Increment = step,
                DecimalPlaces = decimals,
                BackColor = Surface2,
                ForeColor = Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Ui(9.5f),
                Dock = DockStyle.Fill,
                Margin = new Padding(S(0), S(3), S(0), S(3))
            };
        }

        public static TextBox TextBox()
        {
            return new TextBox
            {
                BackColor = Surface2,
                ForeColor = Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Ui(9.5f),
                Dock = DockStyle.Fill,
                Margin = new Padding(S(0), S(3), S(0), S(3))
            };
        }

        public static CheckBox Check(string text)
        {
            return new CheckBox { Text = text, ForeColor = Text, Font = Ui(9.5f), Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Margin = new Padding(S(0), S(3), S(0), S(3)) };
        }

        /// <summary>A two-column label/control grid used inside each card.</summary>
        public static TableLayoutPanel FieldGrid(int labelWidth = 118)
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                BackColor = Surface,
                Padding = new Padding(S(0), S(4), S(0), S(0))
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(labelWidth)));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        public static void AddRow(TableLayoutPanel t, string label, Control c, int height = 32)
        {
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(height)));
            t.Controls.Add(FieldLabel(label), 0, row);
            t.Controls.Add(c, 1, row);
        }

        private static Icon _appIcon;

        /// <summary>The app logo (multi-size .ico embedded in the program), for windows and the taskbar.</summary>
        public static Icon AppIcon()
        {
            if (_appIcon != null) return _appIcon;
            try
            {
                using (var s = typeof(Theme).Assembly.GetManifestResourceStream("DatvTx.ico"))
                    if (s != null) _appIcon = new Icon(s);
            }
            catch { }
            return _appIcon ?? (_appIcon = BuildIcon());
        }

        /// <summary>Fallback: the logo drawn in code.</summary>
        public static Icon BuildIcon()
        {
            using (var bmp = new Bitmap(64, 64))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                var outer = new Rectangle(2, 2, 60, 60);
                using (var bg = new LinearGradientBrush(outer, Color.FromArgb(17, 72, 126), Color.FromArgb(5, 22, 38), 55f))
                    g.FillEllipse(bg, outer);
                using (var rim = new Pen(Color.FromArgb(70, 170, 255), 2.5f))
                    g.DrawEllipse(rim, new Rectangle(3, 3, 58, 58));
                using (var pen = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, 32, 26, 22, 52);
                    g.DrawLine(pen, 32, 26, 42, 52);
                    g.DrawLine(pen, 26, 42, 38, 42);
                }
                using (var dot = new SolidBrush(Color.FromArgb(255, 99, 99)))
                    g.FillEllipse(dot, 28, 20, 8, 8);
                using (var wave = new Pen(Color.FromArgb(140, 210, 255), 2.2f))
                {
                    g.DrawArc(wave, 20, 12, 24, 24, 200, 50);
                    g.DrawArc(wave, 20, 12, 24, 24, 290, 50);
                    g.DrawArc(wave, 13, 5, 38, 38, 205, 40);
                    g.DrawArc(wave, 13, 5, 38, 38, 295, 40);
                }
                IntPtr h = bmp.GetHicon();
                return (Icon)Icon.FromHandle(h).Clone();
            }
        }
    }
}
