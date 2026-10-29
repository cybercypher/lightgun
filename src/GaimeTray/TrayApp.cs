using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using GaimeFeeder;

namespace GaimeTray
{
    // Tray lifetime: the app lives here, window is optional.
    sealed class TrayContext : ApplicationContext
    {
        readonly NotifyIcon _tray;
        readonly FeederEngine _eng = new FeederEngine();
        MainForm _win;
        bool _exiting;
        bool _told;

        public TrayContext()
        {
            _tray = new NotifyIcon
            {
                Icon = MakeIcon(),
                Text = "G'AIM'E Tray",
                Visible = true,
            };
            _tray.DoubleClick += (s, e) => ToggleWindow();
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show", null, (s, e) => ShowWindow());
            menu.Items.Add("Setup…", null, (s, e) => SetupWizard.Open(_eng));
            menu.Items.Add("Recalibrate Gun 1", null, (s, e) => Recal(1));
            menu.Items.Add("Recalibrate Gun 2", null, (s, e) => Recal(2));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => Exit());
            _tray.ContextMenuStrip = menu;
            _eng.Balloon += (t, x) =>
            {
                try { _tray.ShowBalloonTip(3000, t, x, ToolTipIcon.Info); } catch { }
            };
            ShowWindow();
            // First run: the wizard owns calibration order (Box Cal before the
            // host grid), so attaching must not pop the grid on its own.
            bool firstRun = SetupWizard.FirstRun;
            ScreenBorder.Mode = ScreenBorder.Enabled = SetupConfig.Load().BorderOn;
            if (firstRun) _eng.AutoCalibrate = false;
            _eng.Start();
            if (firstRun) SetupWizard.Open(_eng);
            else
                try
                {
                    var line = DemulShooterBinding.KeepBound(SetupConfig.Load().DemulShooterDir, BridgeDevices.Mice());
                    if (line != null) _eng.Note(line);
                }
                catch (Exception ex) { _eng.Note("DemulShooter re-bind skipped: " + ex.Message); }
            // `--test` opens the gallery straight away (shortcut / verification).
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, "--test", StringComparison.OrdinalIgnoreCase))
                {
                    _win.Hide();
                    _eng.OpenGallery();
                }
        }

        static Icon MakeIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(24, 25, 28));
                using (var p = new Pen(Color.FromArgb(140, 255, 120), 2.5f))
                {
                    g.DrawEllipse(p, 7, 7, 18, 18);
                    g.DrawLine(p, 16, 2, 16, 10);
                    g.DrawLine(p, 16, 22, 16, 30);
                    g.DrawLine(p, 2, 16, 10, 16);
                    g.DrawLine(p, 22, 16, 30, 16);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        void ShowWindow()
        {
            if (_win == null || _win.IsDisposed)
            {
                _win = new MainForm(_eng);
                _win.FormClosing += (s, e) =>
                {
                    if (!_exiting && e.CloseReason == CloseReason.UserClosing)
                    {
                        e.Cancel = true;
                        _win.Hide();
                        if (!_told)
                        {
                            _told = true;
                            _tray.ShowBalloonTip(3000, "Still feeding",
                                "GaimeTray stays in the tray. Right-click → Exit to quit.",
                                ToolTipIcon.Info);
                        }
                    }
                };
                _win.Show();
            }
            else
            {
                _win.Show();
                _win.WindowState = FormWindowState.Normal;
                _win.Activate();
            }
        }

        void ToggleWindow()
        {
            if (_win == null || _win.IsDisposed || !_win.Visible) ShowWindow();
            else _win.Hide();
        }

        void Recal(int pad)
        {
            foreach (var g in _eng.Snapshot())
                if (g.PadIndex == pad) { _eng.Recalibrate(g.Serial); return; }
            _tray.ShowBalloonTip(3000, "No gun", "Gun " + pad + " is not attached.", ToolTipIcon.Warning);
        }

        void Exit()
        {
            _exiting = true;
            try { _eng.Stop(); } catch { }
            _tray.Visible = false;
            Application.Exit();
        }
    }

    static class Program
    {
        // A tray app has no console: without this, an exception surfaces as a
        // raw .NET dialog and leaves nothing behind to diagnose. Write the full
        // stack to logs\crash.log first.
        static void LogCrash(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Logs);
                File.AppendAllText(Path.Combine(AppPaths.Logs, "crash.log"),
                    DateTime.Now.ToString("u") + "\r\n" + ex + "\r\n\r\n");
            }
            catch { }
        }

        [STAThread]
        static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                LogCrash(e.Exception);
                MessageBox.Show("GaimeTray hit an error:\r\n\r\n" + e.Exception.Message +
                    "\r\n\r\nDetails: " + Path.Combine(AppPaths.Logs, "crash.log"),
                    "G'AIM'E Tray", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException +=
                (s, e) => LogCrash(e.ExceptionObject as Exception);

            bool fresh;
            using (var m = new Mutex(true, "GaimeTraySingleInstance", out fresh))
            {
                if (!fresh) return; // already in the tray — no second instance
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
        }
    }
}
