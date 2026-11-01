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
            MakeJunction(link, tpDir);
            return "linked " + link + " -> " + tpDir;
        }

        // A directory junction (mklink /J needs no admin). Deleting it later
        // with Directory.Delete(path) (not recursive) removes only the link.
        static void MakeJunction(string link, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
                if (!Directory.Exists(link)) throw new InvalidOperationException("mklink failed: " + err.Trim());
            }
        }

        static bool IsJunction(string dir) => new DirectoryInfo(dir).LinkTarget != null;

        // ---- RetroBat lists exactly the TeknoParrot games bound to the guns --

        public sealed class GameLine
        {
            public string Game, RetroBat, Guns, Border;
            public bool Ok;
        }

        // The game's top folder: GamePath minus the exe's location inside the
        // game, from TeknoParrot's GameSetup\<name>.xml (Jurassic Park: "game";
        // Luigi's Mansion: "exe\x64\VACUUM.exe"). Falls back to GamePath's folder.
        static string GameRoot(string tpDir, string name, string gamePath)
        {
            try
            {
                var setup = Path.Combine(tpDir, "GameSetup", name + ".xml");
                if (File.Exists(setup))
                {
                    string rel = ((string)System.Xml.Linq.XDocument.Load(setup).Root?.Element("GameExecutableLocation") ?? "").Replace('/', '\\').Trim('\\');
                    string gp = gamePath.Replace('/', '\\');
                    if (rel.Length > 0 && gp.EndsWith("\\" + rel, StringComparison.OrdinalIgnoreCase))
                        return gp.Substring(0, gp.Length - rel.Length - 1);
                }
            }
            catch { }
            return Path.GetDirectoryName(gamePath);
        }

        // For every TeknoParrot game bound to the guns: link it into
        // roms\teknoparrot as <Name>.teknoparrot (the name RetroBat matches to
        // TeknoParrot's Metadata), put its process on the bridge list, and
        // report guns / border. Links this made for games no longer bound are
        // removed; real folders are never touched. Also links RetroBat's
        // TeknoParrot to this one and adds the Setup entry.
        public static List<GameLine> SyncTeknoParrotGames(string retroBatDir, string tpDir, Action<string> log)
        {
            if (!Valid(retroBatDir)) throw new InvalidOperationException("set the RetroBat folder first");
            if (string.IsNullOrEmpty(tpDir) || !File.Exists(Path.Combine(tpDir, GaimeFeeder.SetupConfig.TeknoParrotExe)))
                throw new InvalidOperationException("set the TeknoParrot folder on the Games page first");
            log?.Invoke(LinkTeknoParrot(retroBatDir, tpDir));
            var roms = Path.Combine(retroBatDir, "roms", "teknoparrot");
            Directory.CreateDirectory(roms);

            var lines = new List<GameLine>();
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var processes = new List<string>();
            foreach (var p in TeknoParrotProfiles.Scan(tpDir).Where(p => p.P1))
            {
                string name = Path.GetFileNameWithoutExtension(p.File);
                var line = new GameLine { Game = p.Game, Guns = p.P2 ? "P1 + P2" : "P1 only" };
                lines.Add(line);
                string gamePath = "";
                try { gamePath = (string)System.Xml.Linq.XDocument.Load(p.File).Root?.Element("GamePath") ?? ""; } catch { }
                var bt = ReshadeBorder.For(tpDir, p.File);
                var bs = ReshadeBorder.Get(bt);
                line.Border = bs == ReshadeBorder.State.On ? "on" : bs == ReshadeBorder.State.Off ? "off"
                    : bs == ReshadeBorder.State.Unsupported ? "n/a" : bs == ReshadeBorder.State.NotInstalled ? "not installed" : "old — reinstall";
                string root = gamePath.Length == 0 ? null : GameRoot(tpDir, name, gamePath);
                if (root == null || !Directory.Exists(root)) { line.RetroBat = "game folder not found — set the game in TeknoParrot"; continue; }
                if (!File.Exists(Path.Combine(tpDir, "Metadata", name + ".json")))
                    log?.Invoke(name + ": no Metadata\\" + name + ".json in TeknoParrot — RetroBat may not list it");
                var entry = Path.Combine(roms, name + ".teknoparrot");
                wanted.Add(Path.GetFileName(entry));
                try
                {
                    if (Directory.Exists(entry))
                    {
                        var info = new DirectoryInfo(entry);
                        if (info.LinkTarget == null) { line.RetroBat = "listed (its own folder, left alone)"; line.Ok = true; }
                        else if (string.Equals(Path.GetFullPath(info.LinkTarget).TrimEnd('\\'), Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        { line.RetroBat = "listed"; line.Ok = true; }
                        else { Directory.Delete(entry); MakeJunction(entry, root); line.RetroBat = "listed (link updated)"; line.Ok = true; }
                    }
                    else { MakeJunction(entry, root); line.RetroBat = "added"; line.Ok = true; }
                }
                catch (Exception ex) { line.RetroBat = "failed: " + ex.Message; }
                var proc = TeknoParrotProfiles.GameProcess(p.File);
                if (proc != null) processes.Add(proc);
            }

            // Links this sync made earlier for games that are no longer bound.
            foreach (var dir in Directory.GetDirectories(roms, "*.teknoparrot"))
                if (!wanted.Contains(Path.GetFileName(dir)) && IsJunction(dir))
                {
                    try { Directory.Delete(dir); log?.Invoke("removed " + Path.GetFileName(dir) + " from RetroBat (no longer bound)"); }
                    catch (Exception ex) { log?.Invoke("could not remove " + dir + ": " + ex.Message); }
                }

            int added = GaimeFeeder.PicoOutput.EnsureGames(processes);
            if (added > 0) log?.Invoke(added + " game process(es) added to the bridge list");
            try { log?.Invoke(AddSetupEntry(retroBatDir)); } catch (Exception ex) { log?.Invoke("Setup entry: " + ex.Message); }
            return lines;
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
