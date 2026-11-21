using System;
using System.Windows.Forms;
using DatvTx.UI;

namespace DatvTx
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest")
                return Core.SelfTest.Run(args.Length > 1 ? args[1] : "selftest.log");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
                MessageBox.Show(e.Exception.Message, MainForm.ProductTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
