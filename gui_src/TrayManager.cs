using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ZapretGUI
{
    public class TrayManager : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly Window _mainWindow;
        private readonly ZapretCore _core;

        public TrayManager(Window mainWindow, ZapretCore core)
        {
            _mainWindow = mainWindow;
            _core = core;

            _notifyIcon = new NotifyIcon
            {
                Text = "ZapretVPN",
                Visible = true,
                Icon = CreateShieldIcon()
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Открыть ZapretVPN", null, (s, e) => ShowMainWindow());
            contextMenu.Items.Add(new ToolStripSeparator());
            
            var runItem = new ToolStripMenuItem("Быстрый старт (Standalone)", null, async (s, e) =>
            {
                var strategies = _core.GetStrategies();
                if (strategies.Count > 0)
                {
                    await _core.StartStandaloneAsync(strategies[0]);
                }
            });
            contextMenu.Items.Add(runItem);

            var stopItem = new ToolStripMenuItem("Остановить всё", null, async (s, e) =>
            {
                await _core.StopStandaloneAsync();
                await _core.StopServiceAsync();
            });
            contextMenu.Items.Add(stopItem);

            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("Выход", null, (s, e) =>
            {
                _notifyIcon.Visible = false;
                VpnService.CleanupOnAppExit();
                System.Windows.Application.Current.Shutdown();
            });

            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();

            _core.StatusChanged += UpdateStatus;
            UpdateStatus();
        }

        public void ShowMainWindow()
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }

        public void UpdateStatus()
        {
            string srv = _core.GetZapretServiceStatus();
            bool st = _core.IsStandaloneRunning();

            if (st)
            {
                _notifyIcon.Text = $"Zapret: Работает Standalone (PID: {_core.StandalonePid})";
            }
            else if (srv == "RUNNING")
            {
                string strat = _core.GetInstalledServiceStrategy();
                _notifyIcon.Text = $"Zapret: Служба активна [{strat}]";
            }
            else
            {
                _notifyIcon.Text = "Zapret: Остановлен";
            }
        }

        public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
        {
            _notifyIcon.ShowBalloonTip(3000, title, message, icon);
        }

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                return key?.GetValue("ZapretVPN") != null || key?.GetValue("ZapretGUI") != null;
            }
            catch { return false; }
        }

        public static void SetAutoStart(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;

                if (enabled)
                {
                    string exePath = Environment.ProcessPath ?? "";
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue("ZapretVPN", $"\"{exePath}\" --minimized");
                    }
                    key.DeleteValue("ZapretGUI", false);
                }
                else
                {
                    key.DeleteValue("ZapretVPN", false);
                    key.DeleteValue("ZapretGUI", false);
                }
            }
            catch { }
        }

        private static Icon CreateShieldIcon()
        {
            // Draw a stylish 32x32 shield icon procedurally so zero external files are needed
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Shield background (deep blue / indigo)
                using var brush = new SolidBrush(Color.FromArgb(88, 101, 242));
                System.Drawing.Point[] shieldPoints =
                {
                    new System.Drawing.Point(16, 2),
                    new System.Drawing.Point(28, 6),
                    new System.Drawing.Point(28, 18),
                    new System.Drawing.Point(16, 30),
                    new System.Drawing.Point(4, 18),
                    new System.Drawing.Point(4, 6)
                };
                g.FillPolygon(brush, shieldPoints);

                // Shield border (light neon accent)
                using var pen = new Pen(Color.FromArgb(255, 255, 255), 2f);
                g.DrawPolygon(pen, shieldPoints);

                // Lightning bolt in center
                using var boltBrush = new SolidBrush(Color.FromArgb(255, 220, 40));
                System.Drawing.Point[] boltPoints =
                {
                    new System.Drawing.Point(17, 7),
                    new System.Drawing.Point(11, 16),
                    new System.Drawing.Point(15, 16),
                    new System.Drawing.Point(14, 25),
                    new System.Drawing.Point(22, 14),
                    new System.Drawing.Point(17, 14)
                };
                g.FillPolygon(boltBrush, boltPoints);
            }
            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using var tempIcon = Icon.FromHandle(hIcon);
                return (Icon)tempIcon.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }
}
