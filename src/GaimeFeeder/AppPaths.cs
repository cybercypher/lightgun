using System;
using System.IO;

namespace GaimeFeeder
{
    // Data root for GaimeTray (this file is linked into it).
    //
    // Deployed (C:\games\feeder\): guns.json + calibrations\ + logs\ sit next to
    // the exe, so the install is self-contained and survives a WSL wipe.
    // Dev: the repo root, five folders up from
    // src\<App>\bin\<Config>\net8.0-windows\.
    //
    // Resolution: walk up from the base directory until a guns.json is found
    // (deployed finds it in the exe folder, dev finds it at the repo root). If
    // none exists yet, treat the exe folder as the root so a fresh deploy
    // creates its data in place.
    static class AppPaths
    {
        static string _root;

        public static string Root => _root ?? (_root = Resolve());

        static string Resolve()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var probe = dir;
            for (int i = 0; i <= 6 && !string.IsNullOrEmpty(probe); i++)
            {
                if (File.Exists(Path.Combine(probe, "guns.json"))) return probe;
                var parent = Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent) || parent == probe) break;
                probe = parent;
            }
            return dir;
        }

        public static string GunsJson => Path.Combine(Root, "guns.json");
        // Optional list of game process names that unlock the bridge output.
        public static string BridgeProcesses => Path.Combine(Root, "bridge-processes.txt");
        // Debug switch: while this file exists, stream regardless of foreground.
        public static string BridgeForce => Path.Combine(Root, "bridge-force-on.txt");
        // Debug switch: while this file exists, the trigger is sent as a square
        // wave (first line = period in ms). Not needed for any game. Keep it off.
        public static string BridgePulseTrigger => Path.Combine(Root, "bridge-pulse-trigger.txt");
        // Press latch in ms before the trigger switch closes (first line, an
        // integer 0..300). Absent = 0. Read once when the tray starts.
        public static string LatchMs => Path.Combine(Root, "latch-ms.txt");
        // Box Cal headroom in percent (first line, an integer 0..20). Absent =
        // 0. While set, Box Cal pulls the eight base points that far toward the
        // screen centre, so the gun's raw range covers more than the screen.
        // Run the host calibration again after every Box Cal.
        public static string BoxCalHeadroom => Path.Combine(Root, "boxcal-headroom.txt");
        public static string Calibrations => Path.Combine(Root, "calibrations");
        public static string Logs => Path.Combine(Root, "logs");

        // First line of a switch file as an integer clamped to min..max;
        // fallback when the file is absent or does not parse.
        public static int ReadInt(string path, int fallback, int min, int max)
        {
            try
            {
                if (!File.Exists(path)) return fallback;
                foreach (var line in File.ReadAllLines(path))
                {
                    var s = line.Trim();
                    if (s.Length == 0) continue;
                    if (!int.TryParse(s, out int v)) return fallback;
                    return Math.Max(min, Math.Min(max, v));
                }
            }
            catch { }
            return fallback;
        }
    }
}
