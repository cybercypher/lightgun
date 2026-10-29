using System;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace GaimeFeeder
{
    // Axis-aligned bbox calibration, same model as Guncon3Windows RectCalib:
    // 4 corners + center -> RawMin/Max, ScreenW/H, InvertY. Upgrade to homography
    // if angled-aim error exceeds ~2% of screen.
    sealed class RectCalib
    {
        public double RawMinX { get; set; }
        public double RawMaxX { get; set; }
        public double RawMinY { get; set; }
        public double RawMaxY { get; set; }
        public int ScreenW { get; set; }
        public int ScreenH { get; set; }
        public bool InvertY { get; set; } = false;
        // When the grid was saved and how well it fit (setup wizard, CalStatus).
        // Absent in files written before these existed.
        public DateTime? SavedUtc { get; set; }
        public string FitInfo { get; set; }

        public (double sx, double sy, bool inside) Map(int rawX, int rawY)
        {
            double nx = (rawX - RawMinX) / (RawMaxX - RawMinX);
            double ny = (rawY - RawMinY) / (RawMaxY - RawMinY);
            bool inside = nx >= 0 && nx <= 1 && ny >= 0 && ny <= 1;
            nx = Math.Min(1, Math.Max(0, nx));
            ny = Math.Min(1, Math.Max(0, ny));
            if (InvertY) ny = 1 - ny;
            return (nx * (ScreenW - 1), ny * (ScreenH - 1), inside);
        }

        static string Path(string serial)
        {
            // Deployed: beside the exe (C:\games\feeder). Dev: repo root. See AppPaths.
            var dir = AppPaths.Calibrations;
            Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, $"cal_{serial}.json");
        }

        public bool IsValid =>
            (RawMaxX - RawMinX) >= 500 && (RawMaxY - RawMinY) >= 500;

        public void Save(string serial)
        {
            SavedUtc = DateTime.UtcNow;
            File.WriteAllText(Path(serial), JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }

        public static RectCalib Load(string serial)
        {
            var p = Path(serial);
            if (!File.Exists(p)) return null;
            try { return JsonSerializer.Deserialize<RectCalib>(File.ReadAllText(p)); }
            catch { return null; }
        }
    }
}
