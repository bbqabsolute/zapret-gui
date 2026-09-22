using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;

namespace ZapretGUI
{
    public partial class App : System.Windows.Application
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        public static bool IsElevated { get; private set; }

        public static bool IsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public static bool RestartAsAdmin(string? additionalArgs = null)
        {
            try
            {
                string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(exePath)) return false;

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = additionalArgs ?? string.Join(" ", Environment.GetCommandLineArgs().Skip(1))
                };

                Process.Start(psi);
                Current.Shutdown();
                return true;
            }
            catch
            {
                return false;
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch
            {
                // Fallback for older OS versions
            }

            IsElevated = IsAdministrator();

            bool noElevate = e.Args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));

            if (!IsElevated && !noElevate)
            {
                try
                {
                    string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = exePath,
                            UseShellExecute = true,
                            Verb = "runas",
                            Arguments = string.Join(" ", e.Args)
                        };

                        var proc = Process.Start(psi);
                        if (proc != null)
                        {
                            Shutdown(0);
                            return;
                        }
                    }
                }
                catch (Win32Exception)
                {
                    // User cancelled UAC prompt or environment does not support interactive elevation.
                    // Continue running in non-elevated mode.
                }
                catch
                {
                    // Fallback to continue running un-elevated
                }
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            VpnService.CleanupOnAppExit();
            base.OnExit(e);
        }
    }
}
