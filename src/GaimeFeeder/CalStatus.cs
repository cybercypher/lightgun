using System;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace GaimeFeeder
{
    // Calibration state per gun, so the order rule from SETUP.md (Box Cal
    // first, host grid after every Box Cal) is checked instead of remembered.
    //
    // A Box Cal lives inside the gun, so the PC keeps a marker when it sends
    // one: calibrations/boxcal_<sn>.json. The host grid's time is
    // RectCalib.SavedUtc, or the file time for grids saved before that field.
    static class CalStatus
    {
        sealed class BoxCalMarker
        {
            public DateTime SentUtc { get; set; }
            public int HeadroomPercent { get; set; }
            public int ScreenW { get; set; }
            public int ScreenH { get; set; }
        }

        static string MarkerPath(string sn)
        {
            Directory.CreateDirectory(AppPaths.Calibrations);
            return Path.Combine(AppPaths.Calibrations, "boxcal_" + sn + ".json");
        }

        public static void MarkBoxCal(string sn, int headroom, int w, int h)
        {
            try
            {
                File.WriteAllText(MarkerPath(sn), JsonSerializer.Serialize(new BoxCalMarker
                {
                    SentUtc = DateTime.UtcNow, HeadroomPercent = headroom, ScreenW = w, ScreenH = h,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // Null when this PC never sent a Box Cal to the gun (it may still have
        // one from before the marker existed).
        public static DateTime? BoxCalUtc(string sn)
        {
            try
            {
                var p = MarkerPath(sn);
                if (!File.Exists(p)) return null;
                return JsonSerializer.Deserialize<BoxCalMarker>(File.ReadAllText(p))?.SentUtc;
            }
            catch { return null; }
        }

        public static DateTime? GridUtc(string sn, RectCalib cal)
        {
            if (cal == null) return null;
            if (cal.SavedUtc.HasValue) return cal.SavedUtc;
            var p = Path.Combine(AppPaths.Calibrations, "cal_" + sn + ".json");
            return File.Exists(p) ? File.GetLastWriteTimeUtc(p) : (DateTime?)null;
        }

        // Why the host grid needs redoing, or null when it is current.
        public static string GridProblem(string sn, RectCalib cal)
        {
            if (cal == null) return "no host calibration";
            var box = BoxCalUtc(sn);
            var grid = GridUtc(sn, cal);
            if (box.HasValue && (!grid.HasValue || box.Value > grid.Value))
                return "Box Cal is newer than the host calibration";
            var screen = Screen.PrimaryScreen.Bounds;
            if (cal.ScreenW != screen.Width || cal.ScreenH != screen.Height)
                return "calibrated at " + cal.ScreenW + "x" + cal.ScreenH +
                    ", screen is now " + screen.Width + "x" + screen.Height;
            return null;
        }
    }
}
