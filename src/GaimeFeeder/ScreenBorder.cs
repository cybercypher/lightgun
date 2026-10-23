using System;
using System.Drawing;

namespace GaimeFeeder
{
    // Sinden-style white reference border.
    //
    // One process-wide switch and one geometry, deliberately shared: the gun's
    // solver sees the calibration window and the tray gallery (and, later, an
    // overlay over a game) through the same camera, so the reference must be
    // identical everywhere or the aim shifts between screens.
    //
    // Thickness ~1.6% of the short edge (~10 px at 1080p, ~35 px at 4K), which
    // is the range Sinden setups tune to.
    static class ScreenBorder
    {
        public static bool Enabled;

        public static int Thickness(int w, int h) =>
            Math.Max(10, (int)(Math.Min(w, h) * 0.016f));

        public static void Draw(Graphics g, int w, int h)
        {
            if (!Enabled || w <= 0 || h <= 0) return;
            int t = Thickness(w, h);
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
