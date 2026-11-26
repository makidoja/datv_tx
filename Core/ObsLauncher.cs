using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DatvTx.Core
{
    /// <summary>
    /// Starts OBS Studio with its virtual camera when the video source is OBS and OBS isn't running.
    /// OBS command line: --startvirtualcam, --minimize-to-tray (see the OBS wiki, "Launch Parameters").
    /// </summary>
    public static class ObsLauncher
    {
        /// <summary>True for OBS's virtual camera device names ("OBS Virtual Camera", "OBS-Camera", Linux "OBS Virtual Camera [/dev/video2]").</summary>
        public static bool IsObsDevice(string videoDevice)
        {
            return !string.IsNullOrEmpty(videoDevice) && videoDevice.IndexOf("OBS", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsRunning()
        {
            try
            {
                return Process.GetProcesses().Any(p =>
                {
                    try
                    {
                        string n = p.ProcessName.ToLowerInvariant();
                        return n == "obs64" || n == "obs32" || n == "obs";
                    }
                    catch { return false; }
                });
            }
            catch { return false; }
        }

        /// <summary>The OBS program: the setting if given, else the usual install places.</summary>
        public static string FindObs(AppSettings s)
        {
            if (!string.IsNullOrWhiteSpace(s.ObsPath)) return s.ObsPath.Trim().Trim('"');
            if (Platform.IsWindows)
            {
                foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    string exe = Path.Combine(root, "obs-studio", "bin", "64bit", "obs64.exe");
                    if (File.Exists(exe)) return exe;
                }
#if !LINUX
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OBS Studio"))
                    {
                        string dir = k != null ? k.GetValue("") as string : null;
                        if (!string.IsNullOrEmpty(dir))
                        {
                            string exe = Path.Combine(dir, "bin", "64bit", "obs64.exe");
                            if (File.Exists(exe)) return exe;
                        }
                    }
                }
                catch { }
#endif
                return null;
            }
            return Platform.FindOnPath("obs");
        }

        /// <summary>
        /// If the source is OBS and OBS isn't running, start it with the virtual camera on and give it time to come up.
        /// Returns false if OBS should have been started but couldn't be.
        /// </summary>
        public static async Task<bool> EnsureRunningAsync(AppSettings s, string videoDevice, Action<string> log)
        {
            if (!s.StartObs || !IsObsDevice(videoDevice)) return true;
            if (IsRunning())
            {
                log("OBS is running - make sure its virtual camera is started (Tools → Start Virtual Camera).");
                return true;
            }
            string exe = FindObs(s);
            if (exe == null)
            {
                log("OBS not found - set Settings → OBS program, or start OBS yourself.");
                return false;
            }
            try
            {
                var psi = new ProcessStartInfo(exe, "--startvirtualcam --minimize-to-tray --disable-shutdown-check")
                {
                    UseShellExecute = false,
                    // OBS must run from its own folder or it can't find its locale and plugin files.
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
                };
                Process.Start(psi);
                log("Starting OBS with its virtual camera (" + exe + ")...");
            }
            catch (Exception ex)
            {
                log("Could not start OBS: " + ex.Message);
                return false;
            }
            // OBS takes a few seconds to load scenes and start the virtual camera.
            int wait = Math.Max(3, Math.Min(60, s.ObsStartWaitSeconds));
            await Task.Delay(wait * 1000);
            if (!IsRunning()) { log("OBS did not stay running - start it yourself and check its log."); return false; }
            log("OBS is up.");
            return true;
        }
    }
}
