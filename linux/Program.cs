using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DatvTx.Core;

namespace DatvTx.LinuxUi
{
    public static class Program
    {
        public static string Version
        {
            get
            {
                var a = Assembly.GetEntryAssembly() ?? typeof(Program).Assembly;
                var v = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                return UpdateChecker.DisplayVersion(v != null ? v.InformationalVersion : "0.0.0");
            }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            // datvtx --selftest [log]     : run the encoder self-test (no display needed)
            // datvtx --screenshot <dir>   : render the windows headless to PNG files (CI)
            if (args.Length > 0 && args[0] == "--selftest")
                return SelfTest.Run(args.Length > 1 ? args[1] : "selftest.log");
            if (args.Length > 1 && args[0] == "--screenshot")
                return Screenshots(args[1]);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
        }

        private static int Screenshots(string dir)
        {
            Directory.CreateDirectory(dir);
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();

            void Shot(Window w, string name)
            {
                w.Show();
                for (int i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
                var frame = w.CaptureRenderedFrame();
                if (frame != null) frame.Save(Path.Combine(dir, name + ".png"));
                Console.WriteLine("screenshot " + name + (frame != null ? " OK" : " FAILED"));
            }

            var main = new MainWindow(demo: true) { Width = 1100, Height = 960 };
            Shot(main, "main-dvb");
            main.SelectTabForScreenshot(1); Shot(main, "main-source");
            main.SelectTabForScreenshot(2); Shot(main, "main-memories");
            main.SelectTabForScreenshot(0);
            main.DemoOnAir(); Shot(main, "main-onair");
            main.ToggleCompactForScreenshot(); main.Width = 320; main.Height = 760; Shot(main, "compact");
            var settings = new SettingsWindow(AppSettings.Load(), null) { Width = 760, Height = 700 };
            Shot(settings, "settings");
            return 0;
        }
    }

    public sealed class App : Application
    {
        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Dark;
            var fluent = new FluentTheme();
            fluent.Palettes[ThemeVariant.Dark] = LTheme.Palette();
            Styles.Add(fluent);
            LTheme.AddStyles(Styles);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new MainWindow();
            base.OnFrameworkInitializationCompleted();
        }
    }
}
