using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GaimeFeeder
{
    // setup.json beside guns.json: what the setup wizard learned about this PC.
    // Only paths and the "wizard finished" flag live here; guns, calibrations
    // and the bridge game list keep their own files.
    sealed class SetupConfig
    {
        public string TeknoParrotDir { get; set; } = "";
        public string DemulShooterDir { get; set; } = "";
        public bool Completed { get; set; }
        // White reference border on (tray screens + ReShade in games).
        public bool BorderOn { get; set; } = true;
        // In games, shrink the picture inside the border instead of drawing
        // the border over its edges (ReShade FIT; PicoOutput maps the aim).
        public bool BorderFit { get; set; } = true;

        // Auto-start (wizard Auto-start page, GaimeTray Kiosk): launch this
        // TeknoParrot profile (UserProfiles file name) when the tray starts,
        // relaunch it when it exits, until the exit combo stops it.
        public bool AutoLaunch { get; set; }
        public string AutoLaunchProfile { get; set; } = "";
        public bool RestartOnExit { get; set; } = true;
        // Hold Start + Coin on one gun this long to leave the game.
        public int ExitComboSeconds { get; set; } = 5;

        // Border DLL name chosen by hand for a game exe whose graphics API
        // cannot be read from the file (protected exes): exe path -> proxy
        // DLL name (dxgi.dll, d3d9.dll, opengl32.dll).
        public Dictionary<string, string> BorderProxy { get; set; } = new Dictionary<string, string>();

        public static string FilePath => Path.Combine(AppPaths.Root, "setup.json");

        public static SetupConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<SetupConfig>(File.ReadAllText(FilePath))
                        ?? new SetupConfig();
            }
            catch { }
            return new SetupConfig();
        }

        public void Save() =>
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));

        // First folder that holds the exe, searched in the usual places on
        // every fixed drive. Null when none does.
        public static string Detect(string exe, params string[] names)
        {
            var roots = new[] { "", "games", "Games", "emulators", "Emulators", "Program Files", "Program Files (x86)" };
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                foreach (var root in roots)
                    foreach (var name in names)
                    {
                        var dir = Path.Combine(drive.RootDirectory.FullName, root, name);
                        if (File.Exists(Path.Combine(dir, exe))) return dir;
                    }
            foreach (var sub in new[] { "Desktop", "Downloads", "Documents" })
                foreach (var name in names)
                {
                    var dir = Path.Combine(home, sub, name);
                    if (File.Exists(Path.Combine(dir, exe))) return dir;
                }
            return null;
        }

        public const string TeknoParrotExe = "TeknoParrotUi.exe";
        public const string DemulShooterExe = "DemulShooter.exe";

        public static string DetectTeknoParrot() =>
            Detect(TeknoParrotExe, "teknoparrot", "TeknoParrot", "TeknoParrotUI");

        public static string DetectDemulShooter() =>
            Detect(DemulShooterExe, "demulshooter", "DemulShooter");
    }
}
