using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Linq;
using GaimeFeeder;

namespace GaimeTray
{
    // Auto-start: after the tray starts (it starts with Windows), wait for the
    // bridge and the guns, check the calibrations, launch the TeknoParrot
    // profile, keep the game in front (the bridge only streams to a foreground
    // game) and relaunch it when it exits. Holding Start + Coin on one gun
    // stops it for this session; the next start of the tray resumes.
    //
    // Runs on a 250 ms UI timer; everything here is on the UI thread.
    sealed class Kiosk
    {
        enum Phase { Idle, WaitDevices, Blocked, Launching, Running, Cooldown, Stopped }

        const int DeviceWaitSeconds = 120;   // launch anyway after this
        const int LaunchSeconds = 90;        // game process must appear by then
        const int GoneSeconds = 5;           // game process absent this long = exited
        const int RelaunchDelaySeconds = 5;
        const int CrashWindowSeconds = 120;  // an exit this soon after launch is a failure
        const int MaxQuickFailures = 3;

        readonly FeederEngine _eng;
        readonly Timer _timer = new Timer { Interval = 250 };
        SetupConfig _cfg;
        Phase _phase = Phase.Idle;
        DateTime _since, _launchedAt, _lastSeen, _comboSince = DateTime.MinValue;
        Process _tp;
        string _gameProcess;
        int _quickFailures;
        string _lastBlock;

        public event Action<string, string> Balloon;   // title, text
        public event Action Stopped;                   // exit combo / crash loop: show the window
        public event Action Changed;

        // The tray's one instance, for the wizard's "Start game now".
        public static Kiosk Instance { get; private set; }

        public Kiosk(FeederEngine eng)
        {
            Instance = this;
            _eng = eng;
            _timer.Tick += (s, e) => { try { Tick(); } catch (Exception ex) { _eng.Note("auto-start: " + ex.Message); } };
        }

        public bool Active => _phase != Phase.Idle && _phase != Phase.Stopped;

        public string Status
        {
            get
            {
                switch (_phase)
                {
                    case Phase.Idle: return "off";
                    case Phase.WaitDevices: return "waiting for the bridge and guns";
                    case Phase.Blocked: return "waiting: " + _lastBlock;
                    case Phase.Launching: return "launching " + _cfg?.AutoLaunchProfile;
                    case Phase.Running: return "running " + _cfg?.AutoLaunchProfile;
                    case Phase.Cooldown: return "restarting the game";
                    default: return "stopped for this session";
                }
            }
        }

        // Tray start: only when the wizard turned it on.
        public void StartIfEnabled()
        {
            _cfg = SetupConfig.Load();
            if (!_cfg.AutoLaunch || string.IsNullOrEmpty(_cfg.AutoLaunchProfile)) return;
            Begin("tray start");
        }

        // "Start game now" from the tray menu / wizard: same path, also when off.
        public void StartNow()
        {
            _cfg = SetupConfig.Load();
            if (string.IsNullOrEmpty(_cfg.AutoLaunchProfile))
            {
                Balloon?.Invoke("Auto-start", "Pick a game on the wizard's Auto-start page first.");
                return;
            }
            _quickFailures = 0;
            Begin("started from the menu");
        }

        void Begin(string why)
        {
            _gameProcess = GameProcessName(_cfg);
            _eng.Note("auto-start: " + why + ", profile " + _cfg.AutoLaunchProfile + ", watching process " + _gameProcess);
            Enter(Phase.WaitDevices);
            _timer.Start();
        }

        // Exit combo, tray menu, or crash loop: close the game, no relaunch.
        public void Stop(string why, bool closeGame)
        {
            if (_phase == Phase.Idle || _phase == Phase.Stopped) return;
            _eng.Note("auto-start: stopped (" + why + ")");
            Enter(Phase.Stopped);
            if (closeGame) CloseGame();
            Stopped?.Invoke();
        }

        void Enter(Phase p)
        {
            _phase = p;
            _since = DateTime.UtcNow;
            Changed?.Invoke();
        }

        double In() => (DateTime.UtcNow - _since).TotalSeconds;

        void Tick()
        {
            CheckExitCombo();
            switch (_phase)
            {
                case Phase.WaitDevices:
                    if (DevicesReady() || In() > DeviceWaitSeconds)
                    {
                        if (!DevicesReady()) _eng.Note("auto-start: bridge/guns not all here after " + DeviceWaitSeconds + " s, launching anyway");
                        Enter(Phase.Blocked);
                    }
                    break;

                case Phase.Blocked:
                    // Calibration must be current, and never launch over the
                    // setup wizard (it may be calibrating).
                    string block = BlockReason();
                    if (block == null) { Launch(); break; }
                    if (block != _lastBlock)
                    {
                        _lastBlock = block;
                        _eng.Note("auto-start: waiting — " + block);
                        Balloon?.Invoke("Game not started", block);
                        Changed?.Invoke();
                    }
                    break;

                case Phase.Launching:
                    if (GameRunning()) { _lastSeen = DateTime.UtcNow; Enter(Phase.Running); break; }
                    if (In() > LaunchSeconds) Exited("the game did not start within " + LaunchSeconds + " s");
                    break;

                case Phase.Running:
                    if (GameRunning())
                    {
                        _lastSeen = DateTime.UtcNow;
                        // The bridge only streams to a foreground game; a game
                        // started at login can open behind other windows.
                        if (In() < 30) BringToFront();
                    }
                    else if ((DateTime.UtcNow - _lastSeen).TotalSeconds > GoneSeconds)
                        Exited("the game closed");
                    break;

                case Phase.Cooldown:
                    if (In() > RelaunchDelaySeconds) Enter(Phase.Blocked);
                    break;
            }
        }

        bool DevicesReady()
        {
            if (!BridgeDevices.Mice().ContainsKey(0)) return false;
            int expected = Math.Max(1, GunMap.Read().Values.Count(v => v == 1 || v == 2));
            return _eng.Snapshot().Count >= expected;
        }

        string BlockReason()
        {
            if (SetupWizardOpen()) return "the setup wizard is open";
            if (!_eng.Feeding) return "the tray is stopped (press Start)";
            foreach (var g in _eng.Snapshot())
            {
                if (!g.Calibrated) return g.Label + " is not calibrated";
                if (g.CalProblem != null) return g.Label + ": " + g.CalProblem;
            }
            return null;
        }

        static bool SetupWizardOpen() =>
            Application.OpenForms.OfType<SetupWizard>().Any(f => !f.IsDisposed && f.Visible);

        void Launch()
        {
            _lastBlock = null;
            string exe = Path.Combine(_cfg.TeknoParrotDir ?? "", SetupConfig.TeknoParrotExe);
            if (!File.Exists(exe)) { Stop("TeknoParrotUi.exe not found — set the folder in the wizard", false); return; }
            if (!File.Exists(Path.Combine(_cfg.TeknoParrotDir, "UserProfiles", _cfg.AutoLaunchProfile)))
            {
                Stop("profile " + _cfg.AutoLaunchProfile + " not found in UserProfiles", false);
                return;
            }
            if (GameRunning())
            {
                // Already up (e.g. started by hand): just watch it.
                _eng.Note("auto-start: " + _gameProcess + " is already running, watching it");
                _launchedAt = DateTime.UtcNow;
                Enter(Phase.Launching);
                return;
            }
            // A TeknoParrot window left open without a game would catch the
            // new launch; start from a clean slate.
            foreach (var p in Process.GetProcessesByName("TeknoParrotUi"))
            {
                try { if (!p.CloseMainWindow() || !p.WaitForExit(3000)) p.Kill(true); } catch { }
            }
            string args = "--profile=" + _cfg.AutoLaunchProfile;
            _eng.Note("auto-start: launching \"" + exe + "\" " + args);
            _tp = Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = _cfg.TeknoParrotDir, UseShellExecute = false });
            _launchedAt = DateTime.UtcNow;
            Enter(Phase.Launching);
        }

        void Exited(string why)
        {
            bool quick = (DateTime.UtcNow - _launchedAt).TotalSeconds < CrashWindowSeconds;
            _quickFailures = quick ? _quickFailures + 1 : 0;
            _eng.Note("auto-start: " + why + (quick ? " (quick failure " + _quickFailures + "/" + MaxQuickFailures + ")" : ""));
            CloseGame(); // TP's UI can stay open after the game; clear it before relaunching
            if (!_cfg.RestartOnExit) { Stop("restart on exit is off", false); return; }
            if (_quickFailures >= MaxQuickFailures)
            {
                Balloon?.Invoke("Game keeps closing", "Stopped after " + MaxQuickFailures + " quick exits. See the Log tab.");
                Stop(MaxQuickFailures + " quick exits in a row", false);
                return;
            }
            Enter(Phase.Cooldown);
        }

        // ---- exit combo: Start + Coin held on one gun -------------------------

        void CheckExitCombo()
        {
            if (!Active) { _comboSince = DateTime.MinValue; return; }
            bool held = _eng.Snapshot().Any(g => g.BtnStart && g.BtnCoin);
            if (!held) { _comboSince = DateTime.MinValue; return; }
            if (_comboSince == DateTime.MinValue)
            {
                _comboSince = DateTime.UtcNow;
                _eng.Note("auto-start: Start + Coin held — keep holding " + _cfg.ExitComboSeconds + " s to exit");
                return;
            }
            if ((DateTime.UtcNow - _comboSince).TotalSeconds >= Math.Max(2, _cfg.ExitComboSeconds))
            {
                _comboSince = DateTime.MinValue;
                Stop("Start + Coin held on a gun", true);
            }
        }

        // ---- game process -----------------------------------------------------

        // ElfLdr2 games run inside BudgieLoader; other loaders run the game's
        // own exe (GamePath in the profile).
        static string GameProcessName(SetupConfig cfg)
        {
            try
            {
                var doc = XDocument.Load(Path.Combine(cfg.TeknoParrotDir, "UserProfiles", cfg.AutoLaunchProfile));
                string emu = (string)doc.Root?.Element("EmulatorType") ?? "";
                if (emu.StartsWith("ElfLdr", StringComparison.OrdinalIgnoreCase)) return "BudgieLoader";
                string path = (string)doc.Root?.Element("GamePath") ?? "";
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFileNameWithoutExtension(path);
            }
            catch { }
            return "BudgieLoader";
        }

        bool GameRunning() => Process.GetProcessesByName(_gameProcess).Length > 0;

        void CloseGame()
        {
            var procs = Process.GetProcessesByName(_gameProcess).ToList();
            if (_tp != null) { try { if (!_tp.HasExited) procs.Add(_tp); } catch { } }
            foreach (var p in procs.Concat(Process.GetProcessesByName("TeknoParrotUi")))
            {
                try
                {
                    if (p.HasExited) continue;
                    if (!p.CloseMainWindow() || !p.WaitForExit(3000)) p.Kill(true);
                }
                catch { }
            }
            _tp = null;
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        // Windows only lets the foreground thread hand over focus; attaching
        // to it for the call is the usual way for a background tray app.
        void BringToFront()
        {
            foreach (var p in Process.GetProcessesByName(_gameProcess))
            {
                IntPtr w = p.MainWindowHandle;
                if (w == IntPtr.Zero) continue;
                IntPtr fg = GetForegroundWindow();
                if (fg == w) return;
                uint fgThread = GetWindowThreadProcessId(fg, out _);
                uint me = GetCurrentThreadId();
                bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
                try
                {
                    if (IsIconic(w)) ShowWindow(w, 9); // SW_RESTORE
                    BringWindowToTop(w);
                    SetForegroundWindow(w);
                }
                finally { if (attached) AttachThreadInput(me, fgThread, false); }
                if (GetForegroundWindow() == w) _eng.Note("auto-start: brought " + _gameProcess + " to the front");
                return;
            }
        }
    }
}
