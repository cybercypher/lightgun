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

            try { WriteGamelist(roms, tpDir, wanted.Select(Path.GetFileNameWithoutExtension), log); }
            catch (Exception ex) { log?.Invoke("gamelist.xml not written: " + ex.Message); }
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

        // ---- kiosk mode: retrobat.ini InterfaceMode (0 normal, 1 kiosk, 2 kid) --
        // RetroBat's wiki: the main menu is cut to the volume control, metadata
        // and favourites editing are off, all games stay playable. Leave it
        // with the pad sequence A A A B A (on the guns: A, A, A, B, A — the
        // tray turns the gun's A/B into RetroBat's keys), or here.

        static string Ini(string dir) => Path.Combine(dir, "retrobat.ini");
        static readonly Regex ModeLine = new Regex(@"^\s*InterfaceMode\s*=\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        public static bool KioskOn(string dir)
        {
            try
            {
                var m = ModeLine.Match(File.ReadAllText(Ini(dir)));
                return m.Success && m.Groups[1].Value == "1";
            }
            catch { return false; }
        }

        // Takes effect the next time RetroBat starts.
        public static string SetKiosk(string dir, bool on)
        {
            if (!Valid(dir)) throw new InvalidOperationException("set the RetroBat folder first");
            var ini = Ini(dir);
            string text = File.Exists(ini) ? File.ReadAllText(ini) : "";
            string line = "InterfaceMode=" + (on ? "1" : "0");
            text = ModeLine.IsMatch(text)
                ? Regex.Replace(text, @"^\s*InterfaceMode\s*=.*$", line, RegexOptions.Multiline | RegexOptions.IgnoreCase)
                : text.TrimEnd() + (text.Length > 0 ? "\r\n" : "") + line + "\r\n";
            File.WriteAllText(ini, text);
            return "kiosk mode " + (on ? "on" : "off") + " — applies the next time RetroBat starts";
        }

        // ---- game list: names, genre, year and TeknoParrot's icon -------------
        // EmulationStation's roms\<system>\gamelist.xml (<gameList><game><path>,
        // <name>, <image>, ...). Only our games' entries are added or updated;
        // nothing else in the file is removed.
        static void WriteGamelist(string roms, string tpDir, IEnumerable<string> names, Action<string> log)
        {
            var file = Path.Combine(roms, "gamelist.xml");
            System.Xml.Linq.XDocument doc;
            try { doc = File.Exists(file) ? System.Xml.Linq.XDocument.Load(file) : new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("gameList")); }
            catch (Exception ex) { log?.Invoke("gamelist.xml unreadable, left alone: " + ex.Message); return; }
            var root = doc.Root;
            Directory.CreateDirectory(Path.Combine(roms, "images"));
            foreach (var name in names)
            {
                string path = "./" + name + ".teknoparrot";
                var game = root.Elements("game").FirstOrDefault(g => string.Equals((string)g.Element("path"), path, StringComparison.OrdinalIgnoreCase));
                if (game == null) { game = new System.Xml.Linq.XElement("game", new System.Xml.Linq.XElement("path", path)); root.Add(game); }
                void Set(string el, string value) { if (!string.IsNullOrEmpty(value)) { var e = game.Element(el); if (e == null) game.Add(new System.Xml.Linq.XElement(el, value)); else e.Value = value; } }
                try
                {
                    var meta = Path.Combine(tpDir, "Metadata", name + ".json");
                    if (File.Exists(meta))
                    {
                        using var j = System.Text.Json.JsonDocument.Parse(File.ReadAllText(meta));
                        string Str(string k) => j.RootElement.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                        Set("name", Str("game_name"));
                        Set("genre", Str("game_genre"));
                        Set("desc", Str("platform"));
                        if (Str("release_year") is string y && Regex.IsMatch(y, @"^\d{4}$")) Set("releasedate", y + "0101T000000");
                        var icon = Str("icon_name");
                        var src = icon == null ? null : Path.Combine(tpDir, "Icons", icon);
                        if (src != null && File.Exists(src))
                        {
                            var dst = Path.Combine(roms, "images", name + Path.GetExtension(src));
                            File.Copy(src, dst, true);
                            Set("image", "./images/" + Path.GetFileName(dst));
                        }
                    }
                }
                catch (Exception ex) { log?.Invoke(name + ": game list details skipped: " + ex.Message); }
            }
            // Entries for our .teknoparrot links that no longer exist.
            foreach (var g in root.Elements("game").ToList())
            {
                var pth = (string)g.Element("path") ?? "";
                if (pth.EndsWith(".teknoparrot", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(Path.Combine(roms, pth.TrimStart('.', '/'))))
                    g.Remove();
            }
            doc.Save(file);
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
