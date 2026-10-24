using System;

namespace GaimeFeeder
{
    // The one feed path for both apps (GaimeFeeder console and GaimeTray).
    // It picks the aim for this frame, applies the test pulses, and sends one
    // frame to the Pico bridge. Do not copy this logic into another file: two
    // copies drifted once, and the tray then froze the aim while the trigger
    // was held.
    //
    // Threading is the same as before this file existed: callers run it from
    // the RawInput thread and from the 60 Hz poll thread.
    static class FeedCore
    {
        public static void Feed(GunState g)
        {
            var now = DateTime.UtcNow;
            bool testTrig = now < g.TestTriggerUntil;

            // Latched pre-press aim for the press frame only, so the trigger
            // jolt cannot move the shot. Every later frame of a held trigger
            // uses the live aim, so full-auto can be aimed.
            bool pressFrame = g.PressFramePending && !testTrig;
            if (g.PressFramePending) g.PressFramePending = false;
            // Consume the flag even without a calibration, or it can go stale
            // across one and fire one old-position click (8f4bf56).
            if (g.Calib == null) return;

            bool testStick = now < g.TestStickUntil;
            bool testBtn = now < g.TestBtnUntil;
            bool down = g.TriggerDown || testTrig;

            var (sx, sy, inside) = g.Calib.Map(
                pressFrame ? g.LatchedX : g.RawX,
                pressFrame ? g.LatchedY : g.RawY);

            // Console arrow keys: move P1's aim to a screen edge for a moment.
            if (testStick)
            {
                sx = (g.Calib.ScreenW - 1) / 2 + g.TestStickX * (g.Calib.ScreenW - 1) / 2;
                sy = (g.Calib.ScreenH - 1) / 2 + g.TestStickY * (g.Calib.ScreenH - 1) / 2;
            }

            PicoOutput.Feed(g.PadIndex - 1, sx, sy,
                            g.Calib.ScreenW, g.Calib.ScreenH, inside, g, down,
                            testBtn ? g.TestBtnMask : 0);
        }
    }
}
