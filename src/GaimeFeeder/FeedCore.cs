using System;

namespace GaimeFeeder
{
    // The one feed path (GaimeTray calls it through FeederEngine.Feed). It
    // picks the aim for this frame and sends one frame to the Pico bridge.
    // Do not copy this logic into another file: two copies drifted once, and
    // the tray then froze the aim while the trigger was held.
    //
    // Threading is the same as before this file existed: the tray calls it
    // from the RawInput thread and from the 60 Hz poll thread.
    static class FeedCore
    {
        public static void Feed(GunState g)
        {
            // Tray "Pulse" button: a short synthetic trigger hold.
            bool testTrig = DateTime.UtcNow < g.TestTriggerUntil;

            // Latched pre-press aim for the press frame only, so the trigger
            // jolt cannot move the shot. Every later frame of a held trigger
            // uses the live aim, so full-auto can be aimed.
            bool pressFrame = g.PressFramePending && !testTrig;
            if (g.PressFramePending) g.PressFramePending = false;
            // Consume the flag even without a calibration, or it can go stale
            // across one and fire one old-position click (8f4bf56).
            if (g.Calib == null) return;

            bool down = g.TriggerDown || testTrig;

            var (sx, sy, inside) = g.Calib.Map(
                pressFrame ? g.LatchedX : g.RawX,
                pressFrame ? g.LatchedY : g.RawY);

            PicoOutput.Feed(g.PadIndex - 1, sx, sy,
                            g.Calib.ScreenW, g.Calib.ScreenH, inside, g, down);
        }
    }
}
