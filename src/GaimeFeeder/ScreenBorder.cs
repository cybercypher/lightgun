using System;
using System.Drawing;

namespace GaimeFeeder
{
    // Sinden-style white reference border, shared by the host calibration
    // window, the tray gallery and the ReShade border over a game.
    //
    // One process-wide switch and ONE THICKNESS IN SCREEN PIXELS. Do not make
    // it proportional to the window: the point of the border is that it is the
    // same reference everywhere, so a border that shrinks or grows with the
    // window silently moves the aim reference between screens. Sinden's own
    // border is a fixed pixel count for the same reason.
    //
    // Keep Pixels in step with GAIME_BORDER_PX in tools/reshade/GaimeBorder.fx
    // (the ReShade border over TeknoParrot games). 32 px at 3840x2160 is what
    // the gallery and calibration window have used since 2026-10-24.
    static class ScreenBorder
    {
        public const int Pixels = 32;

        // On by default: "always show a white border" is the user's rule, and
        // the toggles exist only for A/B noise measurements.
        public static bool Enabled = true;

        // Kept taking (w, h) so callers that lay out chrome around the border
        // do not have to care that the answer no longer depends on them.
        public static int Thickness(int w, int h) => Pixels;

        public static void Draw(Graphics g, int w, int h)
        {
            if (!Enabled || w <= 0 || h <= 0) return;
            int t = Pixels;
            if (t * 2 >= h || t * 2 >= w) return;
            using (var b = new SolidBrush(Color.White))
            {
                g.FillRectangle(b, 0, 0, w, t);
                g.FillRectangle(b, 0, h - t, w, t);
                g.FillRectangle(b, 0, t, t, h - 2 * t);
                g.FillRectangle(b, w - t, t, t, h - 2 * t);
            }
        }
    }
}
