using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DatvTx.UI
{
    /// <summary>
    /// ComboBox drawn in the OpenTuner style: flat dark face, light text, thin border and a ▼ glyph.
    /// Editable combos keep their text box; everything around it (and the button) is painted over.
    /// Disabled combos are drawn dark as well, instead of Windows' light grey.
    /// </summary>
    public class DarkCombo : ComboBox
    {
        private bool _hover;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct COMBOBOXINFO
        {
            public int cbSize;
            public RECT rcItem;
            public RECT rcButton;
            public int stateButton;
            public IntPtr hwndCombo, hwndItem, hwndList;
        }

        [DllImport("user32.dll")]
        private static extern bool GetComboBoxInfo(IntPtr hwnd, ref COMBOBOXINFO info);

        private static readonly Color DisabledBack = Color.FromArgb(14, 30, 48);

        [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr hdc, int color);
        [DllImport("gdi32.dll")] private static extern int SetBkColor(IntPtr hdc, int color);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);

        private static IntPtr _enabledBrush, _disabledBrush;

        private static IntPtr Brush(bool enabled)
        {
            if (_enabledBrush == IntPtr.Zero) _enabledBrush = CreateSolidBrush(ColorTranslator.ToWin32(Theme.Surface2));
            if (_disabledBrush == IntPtr.Zero) _disabledBrush = CreateSolidBrush(ColorTranslator.ToWin32(DisabledBack));
            return enabled ? _enabledBrush : _disabledBrush;
        }

        public DarkCombo()
        {
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
        }

        protected override void WndProc(ref Message m)
        {
            // The edit box inside an editable combo asks its parent (this combo) for colours.
            // When disabled Windows would paint it white; answer with the dark theme instead.
            const int WM_CTLCOLOREDIT = 0x0133, WM_CTLCOLORSTATIC = 0x0138;
            if (m.Msg == WM_CTLCOLOREDIT || m.Msg == WM_CTLCOLORSTATIC)
            {
                SetTextColor(m.WParam, ColorTranslator.ToWin32(Enabled ? Theme.Text : Theme.Muted));
                SetBkColor(m.WParam, ColorTranslator.ToWin32(Enabled ? Theme.Surface2 : DisabledBack));
                m.Result = Brush(Enabled);
                return;
            }
            base.WndProc(ref m);
            const int WM_PAINT = 0x000F, WM_ENABLE = 0x000A, WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008;
            if (m.Msg == WM_PAINT || m.Msg == WM_ENABLE || m.Msg == WM_SETFOCUS || m.Msg == WM_KILLFOCUS)
                PaintFace();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (DropDownStyle == ComboBoxStyle.DropDown && IsHandleCreated)
                try { SelectionLength = 0; } catch { }
            Invalidate(true);
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            ClearSelectionLater();
            Invalidate();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            ClearSelectionLater();
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            ClearSelectionLater();
        }

        /// <summary>Stops the whole text showing highlighted when it is set from code.</summary>
        private void ClearSelectionLater()
        {
            if (DropDownStyle != ComboBoxStyle.DropDown || Focused || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { try { if (!Focused) SelectionLength = 0; } catch { } })); } catch { }
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            bool face = (e.State & DrawItemState.ComboBoxEdit) == DrawItemState.ComboBoxEdit;
            using (var bg = new SolidBrush(selected && !face ? Theme.Accent : Theme.Surface2))
                e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
                new Rectangle(e.Bounds.X + Theme.S(5), e.Bounds.Y, e.Bounds.Width - Theme.S(5), e.Bounds.Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        private void PaintFace()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
            var r = ClientRectangle;
            int bw = Theme.S(22);
            var btn = new Rectangle(r.Right - bw, r.Top, bw, r.Height);
            Color back = Enabled ? Theme.Surface2 : DisabledBack;
            Color fore = Enabled ? Theme.Text : Theme.Muted;
            Color border = !Enabled ? Theme.Border : (Focused || ContainsFocus || _hover || DroppedDown) ? Theme.Accent : Theme.Border;

            using (var g = Graphics.FromHwnd(Handle))
            using (var b = new SolidBrush(back))
            {
                bool drawText = DropDownStyle == ComboBoxStyle.DropDownList || !Enabled;
                if (drawText)
                {
                    g.FillRectangle(b, r);
                    string text = SelectedIndex >= 0 ? GetItemText(SelectedItem) : Text;
                    TextRenderer.DrawText(g, text, Font, new Rectangle(r.Left + Theme.S(6), r.Top, r.Width - bw - Theme.S(6), r.Height), fore,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                else
                {
                    // keep the live edit box; paint the frame and button area around it
                    var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf(typeof(COMBOBOXINFO)) };
                    Rectangle item = Rectangle.Empty;
                    if (GetComboBoxInfo(Handle, ref info))
                        item = Rectangle.FromLTRB(info.rcItem.Left, info.rcItem.Top, info.rcItem.Right, info.rcItem.Bottom);
                    using (var reg = new Region(r))
                    {
                        if (!item.IsEmpty) reg.Exclude(item);
                        g.FillRegion(b, reg);
                    }
                }

                using (var f = Theme.Ui(7.5f))
                    TextRenderer.DrawText(g, "▼", f, btn, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                using (var p = new Pen(border))
                    g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
            }
        }
    }
}
