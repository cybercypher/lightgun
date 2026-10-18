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
        public int PadIndex, RawX, RawY;
        public bool TriggerDown, BtnA, BtnB, BtnCoin, BtnStart, Calibrated;
        public double AgeMs, RateHz;
    }

    // Instance-based port of GaimeFeeder/Program.cs orchestration.
    // All mutations run on the UI thread (RawInput callbacks, Post()ed hotplug
    // attaches, UI button handlers). Poll/hotplug threads only read + Feed.
    // Logs go to the Logged event + logs/tray-<date>.log instead of Console.
    sealed class FeederEngine
    {
        public event Action<string> Logged;
        public event Action GunsChanged;
        public event Action<string, string> Balloon;

        public bool Feeding { get; private set; }

        readonly Dictionary<string, GunState> _guns = new Dictionary<string, GunState>();
        readonly Dictionary<string, VigemPad> _pads = new Dictionary<string, VigemPad>();
        readonly Dictionary<string, (int x, int y)> _latest = new Dictionary<string, (int x, int y)>();
        readonly Queue<string> _pendingCal = new Queue<string>();
        CalibrationWindow _calib;
        string _calibSn;
        RawInputWindow _raw;
        readonly SynchronizationContext _ui;
        bool _threadsOn;
        StreamWriter _fileLog;

        public FeederEngine()
        {
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
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
            _raw.FormClosed += (s, e) => DisposePads();
            _raw.Show(); // invisible (Opacity 0): exists only for INPUTSINK
            Feeding = true;
            Emit(sns.Count == 0
                ? "No guns: binding mode (plug a gun any time, attaches in ~2 s)."
                : "Feeding: " + string.Join(", ", sns));
            GunsChanged?.Invoke();
            EnsureThreads();
            CalibrateNext();
        }

        public void Stop()
        {
            if (!Feeding) return;
            Feeding = false;
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
            DisposePads();
            Emit("Stopped.");
            GunsChanged?.Invoke();
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
            if (c == null) _pendingCal.Enqueue(sn);
            else _guns[sn].Calib = c;
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
            if (_calib != null || _pendingCal.Count == 0) { EnsurePads(); return; }
            string sn = _pendingCal.Dequeue();
            if (!_guns.ContainsKey(sn)) { CalibrateNext(); return; }
            _calibSn = null;
            _calib = new CalibrationWindow("Calibrating " + _guns[sn].Label,
                () => _latest[sn]);
            _calib.FormClosed += (s, e) =>
            {
                string target = _calibSn ?? sn;
                if (_calib.DialogResult == DialogResult.OK && _calib.Result != null
                    && _calib.Result.IsValid)
                {
                    _calib.Result.Save(target);
                    if (_guns.TryGetValue(target, out var g)) g.Calib = _calib.Result;
                    Emit(_guns[target].Label + ": calibrated.");
                }
                else if (_calib.DialogResult == DialogResult.OK)
                    Emit(_guns[target].Label + ": bad samples, skipped (Calibrate to retry).");
                else
                    Emit(_guns[target].Label + ": calibration skipped.");
                _calib = null;
                GunsChanged?.Invoke();
                CalibrateNext();
            };
            _calib.Show();
        }

        void EnsurePads()
        {
            if (!Feeding) return;
            lock (_guns) lock (_pads)
            {
                foreach (var g in _guns.Values)
                    if (g.Calib != null && !_pads.ContainsKey(g.Serial))
                    {
                        try { _pads[g.Serial] = new VigemPad(g.PadIndex); }
                        catch (Exception ex)
                        {
                            Emit("Pad " + g.PadIndex + " FAILED: " + ex.Message +
                                " — is ViGEmBus installed?");
                        }
                    }
            }
            GunsChanged?.Invoke();
        }

        void DisposePads()
        {
            lock (_pads)
            {
                foreach (var p in _pads.Values)
                    try { p.Dispose(); } catch { }
                _pads.Clear();
            }
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
                    Thread.Sleep(2000);
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
                _latest[sn] = (x, y);
                if (!_guns.TryGetValue(sn, out var g) || g.Calib == null) return;
                bool was = g.TriggerDown;
                g.PushPointer(x, y, tip, true);
                if (g.TriggerDown != was)
                    Emit(g.Label + ": trigger " + (g.TriggerDown ? "DOWN" : "UP") +
                        " raw=(" + x + "," + y + ") tip=" + tip);
                if (g.TriggerDown && !was && _calib != null)
                {
                    if (_calibSn == null)
                    {
                        _calibSn = sn;
                        _calib.LockTo(sn, _guns[sn].Label, () => _latest[sn]);
                    }
                    if (sn == _calibSn) _calib.TriggerSample();
                }
                Feed(g);
                Quarantine(g);
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
                Feed(g);
            }
            catch (Exception ex) { Emit("OnKey ERROR: " + ex.Message); }
        }

        void Feed(GunState g)
        {
            lock (_pads)
            {
                if (!_pads.TryGetValue(g.Serial, out var p)) return;
                bool testTrig = DateTime.UtcNow < g.TestTriggerUntil;
                if (g.Calib == null) return;
                bool down = g.TriggerDown || testTrig;
                var (sx, sy, inside) = g.Calib.Map(
                    down && !testTrig ? g.LatchedX : g.RawX,
                    down && !testTrig ? g.LatchedY : g.RawY);
                p.Feed(sx, sy, g.Calib.ScreenW, g.Calib.ScreenH, inside, g, down);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetCursorPos(int X, int Y);

        // Cursor quarantine: the gun is a system touchscreen, so Windows drives
        // the OS cursor straight from it. Slow reports (<60 Hz) are idle/desk
        // wander (thousands of counts of garbage) — park the cursor at (0,0)
        // so stray clicks land on empty desktop. A fast aimed stream (~265 Hz)
        // is left alone so the cursor tracks aim (service menu etc).
        // Freshness gate: only park on a live report, never strand the cursor.
        void Quarantine(GunState g)
        {
            if ((DateTime.UtcNow - g.LastReport).TotalMilliseconds > 500) return;
            if (g.LastIntervalMs > 1000.0 / 60.0) SetCursorPos(0, 0);
        }

        static string LogDir()
        {
            var root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", ".."));
            return Path.Combine(root, "logs");
        }
    }
}
