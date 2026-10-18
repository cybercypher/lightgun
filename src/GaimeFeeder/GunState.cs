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
        // Report-rate tracking for cursor quarantine (see QuarantineCursor).
        long _lastTick;
        public double LastIntervalMs = 1000;
        public double LastPressSpanMs;
        public double LastReleaseQuietMs;
        public int LastBlip;

        static double MsSince(long t0) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 /
            System.Diagnostics.Stopwatch.Frequency;

        // Test pulse (console keys): synthesize a trigger hold for binding capture.
        public DateTime TestTriggerUntil = DateTime.MinValue;
        // Test stick pulse: -1..1 deflection until time (999 = inactive).
        public DateTime TestStickUntil = DateTime.MinValue;
        public float TestStickX, TestStickY;
        // Test buttons bitmask until time: 1=A 2=B 4=Coin(Back) 8=Start.
        public DateTime TestBtnUntil = DateTime.MinValue;
        public int TestBtnMask;

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
            RawX = x; RawY = y; InRange = inRange;
            LastReport = DateTime.UtcNow;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastTick != 0) LastIntervalMs = (now - _lastTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _lastTick = now;
            _lx[_li] = x; _ly[_li] = y; _li = (_li + 1) % LatchN;

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
