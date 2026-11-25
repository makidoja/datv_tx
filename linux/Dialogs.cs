using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DatvTx.LinuxUi
{
    internal static class Dialogs
    {
        /// <summary>A small dark message box. Returns true for OK / Yes.</summary>
        public static Task<bool> Show(Window owner, string text, bool yesNo = false)
        {
            var w = new Window
            {
                Title = "DATV TX (M0CKE)",
                Width = 440, SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = LTheme.B(LTheme.Surface),
                Icon = LTheme.AppIcon()
            };
            var msg = LTheme.Label(text, 13.5, false, LTheme.Text);
            msg.Margin = new Thickness(0, 0, 0, 16);
            var ok = LTheme.Button(yesNo ? "YES" : "OK", true); ok.Width = 96; ok.Height = 34;
            var no = LTheme.Button("NO"); no.Width = 96; no.Height = 34;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            buttons.Children.Add(ok);
            if (yesNo) buttons.Children.Add(no);
            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(msg);
            root.Children.Add(buttons);
            w.Content = root;
            ok.Click += (s, e) => w.Close(true);
            no.Click += (s, e) => w.Close(false);
            return owner != null ? w.ShowDialog<bool>(owner) : Task.FromResult(true);
        }
    }
}
