using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Diagnostics;
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
        readonly Kiosk _kiosk;
        readonly GunPointer _pointer;
        readonly RetroBatNav _nav;
        // `GaimeTray.exe --setup` (RetroBat's entry) signals OpenEvent; the tray
        // opens the wizard and signals ClosedEvent when it closes.
        public const string OpenEvent = "GaimeTray.OpenSetup", ClosedEvent = "GaimeTray.SetupClosed";
        readonly EventWaitHandle _openEvt = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEvent);
        readonly EventWaitHandle _closedEvt = new EventWaitHandle(false, EventResetMode.AutoReset, ClosedEvent);
        readonly Control _invoker = new Control();
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
            menu.Items.Add("Fix touchscreen", null, (s, e) => { ShowWindow(); _win?.FixTouch(); });
            menu.Items.Add("Start game now", null, (s, e) => _kiosk.StartNow());
            var stopAuto = menu.Items.Add("Stop auto-restart", null, (s, e) => _kiosk.Stop("tray menu", false));
            menu.Opening += (s, e) => stopAuto.Enabled = _kiosk.Active;
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Recalibrate Gun 1", null, (s, e) => Recal(1));
            menu.Items.Add("Recalibrate Gun 2", null, (s, e) => Recal(2));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => Exit());
            _tray.ContextMenuStrip = menu;
            _eng.Balloon += (t, x) =>
            {
                try { _tray.ShowBalloonTip(3000, t, x, ToolTipIcon.Info); } catch { }
            };
            // Guns click tray windows at their calibrated aim (GunPointer), not
            // through Windows' touch taps (GunTouchFilter drops those).
            Application.AddMessageFilter(new GunTouchFilter(s => _eng.Note(s)));
            // With Windows touch off, nothing moves the cursor from the guns, so
            // the cursor guard has nothing to undo (outside a game).
            RawInputWindow.TouchMovesCursor = !SystemChecks.TouchGateOff();
            _pointer = new GunPointer(_eng);
            _nav = new RetroBatNav(_eng);
            _invoker.CreateControl();
            var setupWait = new Thread(() =>
            {
                while (true)
                {
                    _openEvt.WaitOne();
                    try { _invoker.BeginInvoke(new Action(OpenSetupForCaller)); } catch { }
                }
            }) { IsBackground = true, Name = "SetupRequests" };
            setupWait.Start();
            _kiosk = new Kiosk(_eng);
            _kiosk.Balloon += (t, x) =>
            {
                try { _tray.ShowBalloonTip(5000, t, x, ToolTipIcon.Warning); } catch { }
            };
            _kiosk.Stopped += () => { if (!_exiting) ShowWindow(); };
            // First run: the wizard owns calibration order (Box Cal before the
            // host grid), so attaching must not pop the grid on its own.
            bool firstRun = SetupWizard.FirstRun;
            var setup = SetupConfig.Load();
            ScreenBorder.Mode = ScreenBorder.Enabled = setup.BorderOn;
            // With auto-start the window stays in the tray, so it does not
            // take the foreground from the game (the bridge streams only to a
            // foreground game).
            bool kiosk = !firstRun && setup.AutoLaunch && !string.IsNullOrEmpty(setup.AutoLaunchProfile);
            if (!kiosk) ShowWindow();
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
            // Border presets follow the current screen size; tell the bridge
            // which games draw their picture inside the border.
            ReshadeBorder.Apply(setup.TeknoParrotDir);
            if (kiosk) _kiosk.StartIfEnabled();
            // `--test` opens the gallery straight away (shortcut / verification).
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, "--test", StringComparison.OrdinalIgnoreCase))
                {
                    _win?.Hide();
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

        // Opened from RetroBat: in front of its fullscreen menu until closed,
        // then tell the waiting `--setup` process so RetroBat carries on.
        void OpenSetupForCaller()
        {
            var w = SetupWizard.Open(_eng);
            w.TopMost = true;
            w.Activate();
            w.FormClosed += (s, e) => { try { _closedEvt.Set(); } catch { } };
            _eng.Note("setup: opened from another program (--setup)");
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
            // Leave a running game alone; just stop restarting it.
            try { _kiosk.Stop("tray exit", false); } catch { }
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

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

        // `GaimeTray.exe --setup`: what RetroBat runs. Ask the running tray to
        // open the setup wizard (start the tray first if it isn't running),
        // then wait until the wizard closes, so RetroBat waits like for a game.
        static void SetupHelper()
        {
            if (!EventWaitHandle.TryOpenExisting(TrayContext.OpenEvent, out var open))
            {
                try { Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory }); }
                catch { }
                var until = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < until && !EventWaitHandle.TryOpenExisting(TrayContext.OpenEvent, out open))
                    Thread.Sleep(250);
                if (open == null)
                {
                    MessageBox.Show("GaimeTray did not start.", "G'AIM'E setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            EventWaitHandle.TryOpenExisting(TrayContext.ClosedEvent, out var closed);
            AllowSetForegroundWindow(-1); // let the tray put the wizard in front
            open.Set();
            // Done when the wizard closes, or when the tray goes away.
            while (closed != null && !closed.WaitOne(1000))
                if (Process.GetProcessesByName("GaimeTray").Length < 2) break;
        }

        [STAThread]
        static void Main()
        {
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, "--setup", StringComparison.OrdinalIgnoreCase))
                {
                    Application.EnableVisualStyles();
                    SetupHelper();
                    return;
                }
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
