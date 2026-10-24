using System;
using System.Collections.Generic;

namespace GaimeFeeder
{
    // One physical gun, keyed by USB serial (<usb-serial>, <usb-serial>, ...).
    // Mirrors guncon2's per-index device separation, keyed by serial instead.
    sealed class GunState
    {
        public readonly string Serial;
        public readonly int PadIndex; // 1-based ViGEm pad (J1/J2)
        public readonly string Label; // "Gun 1 (<usb-serial>)"
        public RectCalib Calib;

        public int RawX, RawY;
        public bool InRange;
        public DateTime LastReport = DateTime.MinValue;

        // ---- host-side smoothing (1 euro filter) --------------------------
        // The gun's NN+IMU output jitters by a few raw counts; mapped to a 4K
        // screen that is several pixels of visible shake. Median5 removes
        // spikes but not noise, so the median output is low-passed here.
        // 1 euro: heavy smoothing while still (noise), light while moving (no
        // perceptible lag) - the cutoff follows the estimated speed.
        // Measured in the tray gallery (B = border, F = this filter).
        public static volatile bool Smoothing = true;
        const double MinCutoff = 4.0;    // Hz at rest
        const double Beta = 0.012;       // Hz per count/s of speed
        const double DerivCutoff = 1.2;  // Hz on the speed estimate
        bool _fInit;
        double _fX, _fY, _fDX, _fLastRawX, _fLastRawY, _fT0;

        static double Alpha(double cutoff, double dt) =>
            1.0 / (1.0 + (1.0 / (2 * Math.PI * cutoff)) / dt);

        void Smooth(int mx, int my, out int ox, out int oy)
        {
            ox = mx; oy = my;
            if (!Smoothing) { _fInit = false; return; }
            double t = System.Diagnostics.Stopwatch.GetTimestamp() /
                       (double)System.Diagnostics.Stopwatch.Frequency;
            if (!_fInit)
            {
                _fX = _fLastRawX = mx; _fY = _fLastRawY = my;
                _fDX = 0; _fT0 = t; _fInit = true;
                return;
            }
            double dt = t - _fT0;
            _fT0 = t;
            if (dt <= 0 || dt > 0.2)
            {
                // Reacquired after a gap: snap instead of dragging the aim in.
                _fX = _fLastRawX = mx; _fY = _fLastRawY = my; _fDX = 0;
                return;
            }
            double dx = (mx - _fLastRawX) / dt;
            _fLastRawX = mx; _fLastRawY = my;
            _fDX += Alpha(DerivCutoff, dt) * (dx - _fDX);
            double a = Alpha(MinCutoff + Beta * Math.Abs(_fDX), dt);
            _fX += a * (mx - _fX);
            _fY += a * (my - _fY);
            ox = (int)Math.Round(_fX); oy = (int)Math.Round(_fY);
        }

        // Trigger debounce.
        // Wall-clock gates are banned here: DateTime.UtcNow quantizes to ~15 ms
        // on Windows, which randomly swallows real pulls (3 reports at 265 Hz
        // span only ~7.5 ms). All spans use Stopwatch (high-res). Press = 3
        // consecutive tip=1 spanning >=4 ms; release = 50 ms of quiet (or 8
        // zeros at low report rates). Sub-press runs are reported as blips.
        int _tipOnRun;
        int _tipOffRun;
        long _tipOnT0;
        long _tipOffT0;
        public bool TriggerDown;
        public DateTime TriggerEdge;
        // Set on the press edge and cleared by the first Feed(): the latched
        // pre-press aim is for the shot itself, not for the whole hold. A held
        // trigger has to keep tracking or full-auto cannot be aimed.
        public bool PressFramePending;
        // Report-rate tracking: drives the valid-lock gate below and the tray's
        // Hz readout.
        long _lastTick;
        public double LastIntervalMs = 1000;

        // Valid-lock gate (Change 3): a real on-screen lock streams ~265 Hz
        // (interval ~3.8 ms); an idle gun wanders the whole span at <60 Hz
        // (measured ~28 Hz). Only a fast, fresh stream may move the virtual
        // stick, so idle wander can't creep the aim. HYPOTHESIS: the ~265 Hz
        // vs <60 Hz split is the measured hardware behaviour. The rate is
        // averaged over a short window so one slow sample during a real aim
        // sweep can't freeze the stick.
        const int LockWin = 8;
        const double LockMinHz = 200.0;
        readonly double[] _ivWin = new double[LockWin];
        int _ivCount, _ivIdx;

        public bool ValidLock
        {
            get
            {
                if (_ivCount == 0) return false;
                double sum = 0;
                for (int i = 0; i < _ivCount; i++) sum += _ivWin[i];
                double avgMs = sum / _ivCount;
                return avgMs > 0 && 1000.0 / avgMs >= LockMinHz;
            }
        }
        public double LastPressSpanMs;
        public double LastReleaseQuietMs;
        public int LastBlip;

        static double MsSince(long t0) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 /
            System.Diagnostics.Stopwatch.Frequency;

        // One compare-exchange step of the fixed median-of-5 sorting network.
        static void Order(ref int a, ref int b)
        {
            if (a > b) { int t = a; a = b; b = t; }
        }

        // Median of five samples: allocation-free 9-comparator network, returns
        // the actual middle sample (no averaging, so a genuine fast sweep is not
        // smeared toward its neighbours).
        static int Median5(int v0, int v1, int v2, int v3, int v4)
        {
            Order(ref v0, ref v1); Order(ref v3, ref v4);
            Order(ref v2, ref v4); Order(ref v2, ref v3);
            Order(ref v0, ref v3); Order(ref v0, ref v2);
            Order(ref v1, ref v4); Order(ref v1, ref v3);
            Order(ref v1, ref v2);
            return v2;
        }

        // Test pulse (console keys): synthesize a trigger hold for binding capture.
        public DateTime TestTriggerUntil = DateTime.MinValue;
        // Test stick pulse: -1..1 deflection until time (999 = inactive).
        public DateTime TestStickUntil = DateTime.MinValue;
        public float TestStickX, TestStickY;
        // Test buttons bitmask until time: 1=A 2=B 4=Coin(Back) 8=Start.
        public DateTime TestBtnUntil = DateTime.MinValue;
        public int TestBtnMask;

        // Median-of-5 spike reject on the incoming stream. When the sensor loses
        // screen lock the raw output sweeps the whole 0..10000 space with
        // single-sample jumps up to ~4800 counts; a 1-2 sample glitch is outvoted
        // by the median, while a real fast aim sweep still passes (>=3 of 5
        // samples agree). Deliberately NOT paired with a max-jump gate: a hard
        // per-sample jump limit cannot tell a glitch from a fast flick and would
        // clip legitimate motion (and weapon-switch whips), so the median alone
        // does the rejecting.
        const int FilterN = 5;
        readonly int[] _mx = new int[FilterN];
        readonly int[] _my = new int[FilterN];
        int _mi;
        bool _filterSeeded;

        // Latch-and-hold: ring of pre-press positions, trigger jump is 144-810 counts.
        const int LatchN = 32;
        readonly int[] _lx = new int[LatchN];
        readonly int[] _ly = new int[LatchN];
        int _li;
        public int LatchedX, LatchedY;

        public bool BtnA, BtnB, BtnCoin, BtnStart;

        public GunState(string serial, int padIndex, string label)
        {
            Serial = serial;
            PadIndex = padIndex;
            Label = label;
        }

        public void PushPointer(int x, int y, bool tip, bool inRange)
        {
            // Seed the filter with the first sample so Median5 is valid from the
            // first report; seeding with zeros would drag startup aim toward 0,0.
            if (!_filterSeeded)
            {
                for (int i = 0; i < FilterN; i++) { _mx[i] = x; _my[i] = y; }
                _filterSeeded = true;
            }
            _mx[_mi] = x; _my[_mi] = y;
            int fx = Median5(_mx[0], _mx[1], _mx[2], _mx[3], _mx[4]);
            int fy = Median5(_my[0], _my[1], _my[2], _my[3], _my[4]);
            _mi = (_mi + 1) % FilterN;

            Smooth(fx, fy, out fx, out fy);
            RawX = fx; RawY = fy; InRange = inRange;
            LastReport = DateTime.UtcNow;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastTick != 0)
            {
                LastIntervalMs = (now - _lastTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _ivWin[_ivIdx] = LastIntervalMs;
                _ivIdx = (_ivIdx + 1) % LockWin;
                if (_ivCount < LockWin) _ivCount++;
            }
            _lastTick = now;
            // Only a real tracking lock may hold the OS cursor. Unlocked idle
            // wander must never take the mouse away from the user.
            RawInputWindow.NoteGunLock(ValidLock);
            // Latch ring stores filtered positions too: the pre-press latch must
            // not capture a glitch sample just before the trigger edge.
            _lx[_li] = fx; _ly[_li] = fy; _li = (_li + 1) % LatchN;

            if (tip)
            {
                _tipOffRun = 0;
                _tipOnRun++;
                if (_tipOnRun == 1) _tipOnT0 = System.Diagnostics.Stopwatch.GetTimestamp();
                // Debounce press: >=3 consecutive tip reports spanning >=4 ms
                // (Stopwatch time; ~7.5 ms nominal at 265 Hz).
                if (!TriggerDown && _tipOnRun >= 3 && MsSince(_tipOnT0) >= 4)
                {
                    TriggerDown = true;
                    TriggerEdge = DateTime.UtcNow;
                    LastPressSpanMs = MsSince(_tipOnT0);
                    // Latch position ~100 ms before the edge (not the jumped at-press sample).
                    int back = Math.Min(LatchN - 1, 26); // ~100 ms at 265 Hz
                    int idx = ((_li - 1 - back) % LatchN + LatchN) % LatchN;
                    LatchedX = _lx[idx]; LatchedY = _ly[idx];
                    PressFramePending = true;
                }
                return;
            }
            if (!TriggerDown && _tipOnRun >= 1 && _tipOnRun < 3)
                LastBlip = _tipOnRun; // swallowed tap: visible in console, not the pad
            _tipOnRun = 0;
            _tipOffRun++;
            if (_tipOffRun == 1) _tipOffT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // Release on sustained quiet: >=50 ms (or >=8 zeros at low rates,
            // e.g. ~12 Hz idle where 50 ms holds <1 report). Never sticks.
            if (TriggerDown && _tipOffRun >= 2 &&
                (MsSince(_tipOffT0) >= 50 || _tipOffRun >= 8))
            {
                TriggerDown = false;
                LastReleaseQuietMs = MsSince(_tipOffT0);
                _tipOffRun = 0;
            }
        }
    }
}
