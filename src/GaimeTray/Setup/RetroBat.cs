using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GaimeTray
{
    // RetroBat (EmulationStation frontend) as the thing the tray starts: the
    // tray keeps turning the guns into the bridge mice, RetroBat launches the
    // games. RetroBat.exe starts emulationstation.exe; games run in RetroBat's
    // emulators (RetroBat\emulators\<name>\...), TeknoParrot games in the
    // TeknoParrot unzipped to RetroBat\emulators\teknoparrot.
    static class RetroBat
    {
        public static string Exe(string dir) =>
            string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, GaimeFeeder.SetupConfig.RetroBatExe);

        public static bool Valid(string dir) => Exe(dir) is string e && File.Exists(e);

        // The frontend's own process; the tray watches it like a game.
        public const string FrontendProcess = "emulationstation";

        // Its TeknoParrot copy, when one has been unzipped there.
        public static string TeknoParrotDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            var tp = Path.Combine(dir, "emulators", "teknoparrot");
            return File.Exists(Path.Combine(tp, GaimeFeeder.SetupConfig.TeknoParrotExe)) ? tp : null;
        }

        // RetroBat looks for TeknoParrot in emulators\teknoparrot. Instead of a
        // second copy, make that folder a junction to the TeknoParrot already
        // installed, so profiles, gun bindings and borders are shared.
        // Junctions need no admin (mklink /J); refuses when the folder exists
        // with anything in it.
        public static string LinkTeknoParrot(string retroBatDir, string tpDir)
        {
            if (!Valid(retroBatDir)) throw new InvalidOperationException("set the RetroBat folder first");
            if (string.IsNullOrEmpty(tpDir) || !File.Exists(Path.Combine(tpDir, GaimeFeeder.SetupConfig.TeknoParrotExe)))
                throw new InvalidOperationException("set the TeknoParrot folder first");
            var link = Path.Combine(retroBatDir, "emulators", "teknoparrot");
            if (Directory.Exists(link))
            {
                var info = new DirectoryInfo(link);
                if (info.LinkTarget != null) return "already linked to " + info.LinkTarget;
                if (Directory.EnumerateFileSystemEntries(link).Any())
                    throw new InvalidOperationException(link + " already has a TeknoParrot copy in it — move it away first");
                Directory.Delete(link);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + tpDir + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
                if (!Directory.Exists(link)) throw new InvalidOperationException("mklink failed: " + err.Trim());
            }
            return "linked " + link + " -> " + tpDir;
        }

        // A "game" in RetroBat's Windows system (roms\windows, .bat accepted)
        // that opens this tray's setup wizard: `GaimeTray.exe --setup` asks the
        // running tray to open it and waits until it closes, so RetroBat waits
        // too. RetroBat shows it after its game lists are updated.
        public static string AddSetupEntry(string retroBatDir)
        {
            if (!Valid(retroBatDir)) throw new InvalidOperationException("set the RetroBat folder first");
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("run the installed GaimeTray.exe to add the entry");
            var dir = Path.Combine(retroBatDir, "roms", "windows");
            Directory.CreateDirectory(dir);
            var bat = Path.Combine(dir, "GaimeTray Setup.bat");
            File.WriteAllText(bat, "@echo off\r\nrem Opens the G'AIM'E lightgun setup (GaimeTray). Written by the tray's setup wizard.\r\n" +
                "\"" + exe + "\" --setup\r\n");
            return "added " + bat + " — in RetroBat, update the game lists to see it under Windows";
        }

        // Programs that are installers, updaters or helpers, not games.
        static readonly Regex NotAGame = new Regex(
            // TeknoParrot's UI and loader are not games (its games are added by
            // Bind to guns); streaming in TP's UI would grab the cursor there.
            @"^(teknoparrotui|openparrot.*|teknomacaw.*|unins\d*|.*setup.*|.*update.*|.*updater.*|.*install.*|crashpad.*|.*crash.*report.*|7z.*|vc_?redist.*|dxwebsetup|.*helper.*|.*server.*)$",
            RegexOptions.IgnoreCase);

        // Process names of RetroBat's emulators (exe names, without .exe), so
        // the bridge streams while any of them is in front. Up to three folders
        // deep under emulators\, which covers the usual layouts.
        public static List<string> EmulatorProcesses(string dir)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var root = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "emulators");
            if (root == null || !Directory.Exists(root)) return names.ToList();
            void Walk(string d, int depth)
            {
                try
                {
                    foreach (var exe in Directory.GetFiles(d, "*.exe"))
                    {
                        var n = Path.GetFileNameWithoutExtension(exe);
                        if (!NotAGame.IsMatch(n)) names.Add(n);
                    }
                    if (depth < 3)
                        foreach (var sub in Directory.GetDirectories(d)) Walk(sub, depth + 1);
                }
                catch { }
            }
            Walk(root, 1);
            return names.ToList();
        }
    }
}
