using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using GaimeFeeder;

namespace GaimeTray
{
    sealed class GunSnapshot
    {
        public string Serial, Label;
        // Why the host grid needs redoing (CalStatus), or null.
        public string CalProblem;
        public int PadIndex, RawX, RawY;
        public bool TriggerDown, BtnA, BtnB, BtnCoin, BtnStart, Calibrated;
        public double AgeMs, RateHz;
    }

    // Tray orchestration: guns, calibration, hot-plug, gallery, feed.
    // All mutations run on the UI thread (RawInput callbacks, Post()ed hotplug
    // attaches, UI button handlers). Poll/hotplug threads only read + Feed.
    // Logs go to the Logged event + logs/tray-<date>.log instead of Console.
    sealed class FeederEngine
    {
        public event Action<string> Logged;
        public event Action GunsChanged;
        public event Action<string, string> Balloon;

        public bool Feeding { get; private set; }

        // Off while the setup wizard is open: it runs Box Cal before the host
        // grid, so an uncalibrated gun must not pop the grid on attach.
        public bool AutoCalibrate { get; set; } = true;

        // Hot-plug rescan interval. Each poll opens the gun's vendor interface
        // and sends a get_info query; the gun writes an ~85-byte heartbeat log
        // line per query. 2 s produced ~3.7 MB/day and filled the gun's 3.9 MB
        // /app partition, which killed its app layer. See tools/gun/README.md.
        const int HotplugRescanMs = 10000;

        readonly Dictionary<string, GunState> _guns = new Dictionary<string, GunState>();
        readonly Dictionary<string, (int x, int y)> _latest = new Dictionary<string, (int x, int y)>();
        readonly Queue<string> _pendingCal = new Queue<string>();
        readonly Dictionary<string, string> _calProblem = new Dictionary<string, string>();
        // Calibrate (all steps): the gun whose Box Cal -> host grid -> test run
        // is in progress, so each step opens the next without the mouse.
        string _chainSn;
        CalibrationWindow _calib;
        string _calibSn;
        ShootingGallery _gallery;
        RawInputWindow _raw;
        readonly SynchronizationContext _ui;
        bool _threadsOn;
        StreamWriter _fileLog;

        public FeederEngine()
        {
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
            ShootingGallery.BoxCalSent = sn =>
            {
                lock (_guns)
                    if (_guns.TryGetValue(sn, out var g)) _calProblem[sn] = CalStatus.GridProblem(sn, g.Calib);
                GunsChanged?.Invoke();
                if (sn == _chainSn)
                {
                    Emit(sn + ": Box Cal sent — step 2, the host grid, opens now (3 pulls on each of 9 targets).");
                    Recalibrate(sn);
                }
            };
        }

        void Emit(string s)
        {
            try
            {
                _fileLog?.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s);
                _fileLog?.Flush();
            }
            catch { }
            try { Logged?.Invoke(s); } catch { }
        }

        // Log helper for UI that is not part of the feed path (the serial
        // writer), so its actions land in the same tray log.
        public void Note(string s) => Emit(s);

        public void Start()
        {
            if (Feeding) return;
            try
            {
                Directory.CreateDirectory(LogDir());
                _fileLog = new StreamWriter(Path.Combine(LogDir(),
                    "tray-" + DateTime.Now.ToString("yyyyMMdd") + ".log"), true);
            }
            catch { }
            var sns = new List<string>();
            try { sns = HidIdentity.EnumerateGunSns(); }
            catch (Exception ex) { Emit("Enumerate failed: " + ex.Message); }
            var numbering = GunMap.Load(sns);
            foreach (var s in sns) AddGun(s, numbering, silent: true);
            try { RawInputWindow.SetSnMap(HidIdentity.PathSuffixToSn()); } catch { }
            _raw = new RawInputWindow(OnPointer, OnKey);
            RawInputWindow.Log = s => Emit("raw: " + s);
            _raw.Show(); // invisible (Opacity 0): exists only for INPUTSINK
            Feeding = true;
            Emit("Output: Pico bridge (" + PicoOutput.Probe() + ")");
            GunState.LatchMs = AppPaths.ReadInt(AppPaths.LatchMs, 0, 0, 300);
            Emit("Press latch: " + GunState.LatchMs + " ms before the trigger switch closes (latch-ms.txt). " +
                "Box Cal headroom: " + AppPaths.ReadInt(AppPaths.BoxCalHeadroom, 0, 0, 20) +
                "% (boxcal-headroom.txt).");
            Emit(sns.Count == 0
                ? "No guns yet: plug one in, it attaches within ~10 s."
                : "Feeding: " + string.Join(", ", sns));
            GunsChanged?.Invoke();
            EnsureThreads();
            CalibrateNext();
        }

        public void Stop()
        {
            if (!Feeding) return;
            Feeding = false;
            try { _gallery?.Close(); } catch { }
            try
            {
                if (_calib != null)
                {
                    _calib.DialogResult = DialogResult.Cancel;
                    _calib.Close();
                }
            }
            catch { }
            try { _raw?.Close(); } catch { }
            _raw = null;
            Emit("Stopped.");
            GunsChanged?.Invoke();
        }

        // guns.json changed (player reassigned): rebuild every gun from it.
        // GunState's player number is fixed at construction.
        public void ReloadGuns()
        {
            bool was = Feeding;
            Stop();
            lock (_guns)
            {
                _guns.Clear();
                _latest.Clear();
                _pendingCal.Clear();
                _calProblem.Clear();
            }
            if (was) Start();
            else GunsChanged?.Invoke();
        }

        // The border setting changed: re-check every gun's calibration.
        public void RefreshCalStatus()
        {
            lock (_guns)
                foreach (var g in _guns.Values) _calProblem[g.Serial] = CalStatus.GridProblem(g.Serial, g.Calib);
            GunsChanged?.Invoke();
        }

        // Fullscreen gallery straight into Box Cal, locked to one gun.
        public bool OpenBoxCal(string sn)
        {
            GunState g;
            lock (_guns)
                if (!_guns.TryGetValue(sn, out g)) return false;
            if (!OpenGallery()) return false;
            _gallery.BeginBoxCal(g);
            return true;
        }

        // Box Cal, then the host grid as soon as Box Cal reaches the gun, then
        // the gallery to test: one click for the whole order in SETUP.md §4.
        public bool CalibrateAll(string sn)
        {
            if (!OpenBoxCal(sn)) return false;
            _chainSn = sn;
            Emit(sn + ": calibrate all steps — step 1, Box Cal (3 pulls on each of 8 targets). Esc stops the run.");
            return true;
        }

        public void Recalibrate(string sn)
        {
            if (!Feeding || _calib != null) return;
            lock (_guns)
            {
                if (!_guns.ContainsKey(sn)) return;
                _pendingCal.Enqueue(sn);
            }
            CalibrateNext();
        }

        public void TestTrigger(string sn, int ms = 400)
        {
            lock (_guns)
                if (_guns.TryGetValue(sn, out var g))
                {
                    g.TestTriggerUntil = DateTime.UtcNow.AddMilliseconds(ms);
                    Emit(g.Label + ": test pulse");
                }
        }

        // Open the fullscreen shooting gallery. Needs the raw-input capture that
        // only exists while feeding, so it reports false if stopped.
        public bool OpenGallery()
        {
            if (!Feeding) return false;
            if (_gallery != null && !_gallery.IsDisposed)
            {
                _gallery.Activate();
                return true;
            }
            _gallery = new ShootingGallery();
            ShootingGallery.Log = s => Emit(s);
            _gallery.FormClosed += (s, e) =>
            {
                _gallery = null;
                // Closing the gallery before Box Cal finished ends the run.
                if (_calib == null) _chainSn = null;
            };
            _gallery.Show();
            _gallery.Activate();
            return true;
        }

        public List<GunSnapshot> Snapshot()
        {
            var now = DateTime.UtcNow;
            lock (_guns)
                return _guns.Values.OrderBy(g => g.PadIndex).Select(g => new GunSnapshot
                {
                    Serial = g.Serial,
                    Label = g.Label,
                    PadIndex = g.PadIndex,
                    RawX = g.RawX,
                    RawY = g.RawY,
                    TriggerDown = g.TriggerDown,
                    BtnA = g.BtnA,
                    BtnB = g.BtnB,
                    BtnCoin = g.BtnCoin,
                    BtnStart = g.BtnStart,
                    Calibrated = g.Calib != null,
                    CalProblem = _calProblem.TryGetValue(g.Serial, out var cp) ? cp : null,
                    AgeMs = (now - g.LastReport).TotalMilliseconds,
                    RateHz = g.LastIntervalMs > 0 ? 1000.0 / g.LastIntervalMs : 0,
                }).ToList();
        }

        // ---- internals (UI thread unless noted) ----

        void AddGun(string sn, Dictionary<string, int> numbering, bool silent)
        {
            if (_guns.ContainsKey(sn)) return;
            int n = numbering.TryGetValue(sn, out var nn) ? nn : 1;
            _guns[sn] = new GunState(sn, n, "Gun " + n + " (sn" + sn + ")");
            _latest[sn] = (5000, 5000);
            var c = RectCalib.Load(sn);
            if (c == null) { if (AutoCalibrate) _pendingCal.Enqueue(sn); }
            else _guns[sn].Calib = c;
            _calProblem[sn] = CalStatus.GridProblem(sn, c);
            if (!silent)
            {
                Emit("Hot-plug: attached " + _guns[sn].Label +
                    " (cal " + (c == null ? "needed - aim + trigger" : "loaded") + ").");
                Balloon?.Invoke("Gun attached", _guns[sn].Label);
            }
        }

        void AttachGuns(List<string> sns)
        {
            var allKnown = _guns.Keys.Concat(sns).Distinct().ToList();
            var numbering = GunMap.Load(allKnown);
            foreach (var s in sns) AddGun(s, numbering, silent: false);
            try { RawInputWindow.SetSnMap(HidIdentity.PathSuffixToSn()); } catch { }
            GunsChanged?.Invoke();
            CalibrateNext();
        }

        void CalibrateNext()
        {
            if (!Feeding) return;
            if (_calib != null || _pendingCal.Count == 0) return;
            string sn = _pendingCal.Dequeue();
            if (!_guns.ContainsKey(sn)) { CalibrateNext(); return; }
            _calibSn = null;
            // Sample the press aim the game gets on its press frame: the
            // filtered aim GunState.LatchMs before the trigger switch closed.
            var cg = _guns[sn];
            _calib = new CalibrationWindow("Calibrating " + cg.Label,
                () => (cg.LatchedX, cg.LatchedY));
            _calib.FormClosed += (s, e) =>
            {
                string target = _calibSn ?? sn;
                if (_calib.DialogResult == DialogResult.OK && _calib.Result != null
                    && _calib.Result.IsValid)
                {
                    _calib.Result.FitInfo = _calib.FitInfo;
                    _calib.Result.Border = ScreenBorder.Enabled;
                    _calib.Result.Save(target);
                    if (_guns.TryGetValue(target, out var g)) g.Calib = _calib.Result;
                    _calProblem[target] = CalStatus.GridProblem(target, _calib.Result);
                    Emit(_guns[target].Label + ": calibrated (" + _calib.FitInfo + ").");
                    if (target == _chainSn)
                    {
                        _chainSn = null;
                        Emit(_guns[target].Label + ": step 3 — test it: shoot 20-40 shots in the gallery, Esc when done.");
                        _ui.Post(_ => OpenGallery(), null);
                    }
                }
                else if (_calib.DialogResult == DialogResult.OK)
                    Emit(_guns[target].Label + ": bad samples, skipped (" + _calib.FitInfo +
                        "; Calibrate to retry).");
                else
                    Emit(_guns[target].Label + ": calibration skipped.");
                // A B test toggle ends with the calibration window.
                ScreenBorder.Enabled = ScreenBorder.Mode;
                if (target == _chainSn) _chainSn = null; // grid cancelled or bad: the run ends here
                _calib = null;
                GunsChanged?.Invoke();
                CalibrateNext();
            };
            _calib.Show();
        }

        void EnsureThreads()
        {
            if (_threadsOn) return;
            _threadsOn = true;
            var poll = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        if (Feeding) lock (_guns)
                                foreach (var g in _guns.Values) Feed(g);
                    }
                    catch { }
                    Thread.Sleep(16);
                }
            })
            { IsBackground = true };
            poll.Start();
            var hot = new Thread(() =>
            {
                while (true)
                {
                    // Slow by design: every poll queries the gun's vendor
                    // interface, and the gun logs an ~85-byte heartbeat line per
                    // query. At 2 s that was ~3.7 MB/day and filled its 3.9 MB
                    // /app partition (killed the app layer). 10 s is plenty for
                    // hot-plug. See tools/gun/README.md.
                    Thread.Sleep(HotplugRescanMs);
                    List<string> sns;
                    try { sns = HidIdentity.EnumerateGunSns(); }
                    catch { continue; }
                    List<string> fresh;
                    lock (_guns) fresh = sns.Where(s => !_guns.ContainsKey(s)).ToList();
                    try { RawInputWindow.SetSnMap(HidIdentity.PathSuffixToSn()); }
                    catch { }
                    if (fresh.Count == 0) continue;
                    _ui.Post(_ => { try { if (Feeding) AttachGuns(fresh); } catch { } }, null);
                }
            })
            { IsBackground = true };
            hot.Start();
        }

        void OnPointer(string sn, int x, int y, bool tip)
        {
            try
            {
                // The gun is a system digitizer, so Windows has just moved the
                // OS cursor from this report. Put it back before any
                // calibration/feed gating, so the pointer never moves - even
                // for a gun that is not calibrated yet.
                RawInputWindow.PinCursor();

                _latest[sn] = (x, y);
                if (!_guns.TryGetValue(sn, out var g)) return;
                bool was = g.TriggerDown;
                // Device state (filter, debounce, rate) does not need a
                // calibration, so update it first: the gallery must see a gun
                // that is not calibrated yet, and calibration itself needs the
                // debounced trigger edge.
                g.PushPointer(x, y, tip, true);
                bool pressed = g.TriggerDown && !was;
                _gallery?.OnPointer(g, pressed);
                // Calibration runs before the uncalibrated return: a gun with no
                // cal file must be able to calibrate with its own trigger.
                if (pressed && _calib != null)
                {
                    if (_calibSn == null)
                    {
                        _calibSn = sn;
                        _calib.LockTo(sn, g.Label, () => (g.LatchedX, g.LatchedY));
                    }
                    if (sn == _calibSn) _calib.TriggerSample();
                }
                if (g.Calib == null) return;
                if (g.TriggerDown != was)
                    Emit(g.Label + ": trigger " + (g.TriggerDown ? "DOWN" : "UP") +
                        " raw=(" + x + "," + y + ") tip=" + tip);
                Feed(g);
            }
            catch (Exception ex) { Emit("OnPointer ERROR: " + ex.Message); }
        }

        void OnKey(string sn, int vkey, bool down)
        {
            try
            {
                if (!_guns.TryGetValue(sn, out var g)) return;
                switch (vkey)
                {
                    case 0x20: g.BtnA = down; break;
                    case 0x42: g.BtnB = down; break;
                    case 0x43: g.BtnCoin = down; break;
                    case 0x51: g.BtnStart = down; break;
                    default: return;
                }
                _gallery?.OnGunKey(g, vkey, down);
                Feed(g);
            }
            catch (Exception ex) { Emit("OnKey ERROR: " + ex.Message); }
        }

        void Feed(GunState g) => FeedCore.Feed(g);

        // Deployed: beside the exe (C:\games\feeder). Dev: repo root. See AppPaths.
        static string LogDir() => AppPaths.Logs;
    }
}
