using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace GaimeTray
{
    // DemulShooter: same rewrite as pico/gun_bridge/host/fix_demulshooter_paths.ps1
    // (P1/P2DeviceName = the bridge mice, P1/P2Mode = RAWINPUT, timestamped
    // backup). DS reads config.ini once at startup.
    static class DemulShooterBinding
    {
        // file.bak-<time>, with -2, -3 ... when two binds land in one second.
        internal static string Backup(string file)
        {
            string stem = file + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string path = stem;
            for (int n = 2; File.Exists(path); n++) path = stem + "-" + n;
            File.Copy(file, path);
            return path;
        }

        public static string ConfigPath(string dsDir) =>
            string.IsNullOrEmpty(dsDir) ? null : Path.Combine(dsDir, "config.ini");

        public static bool Running() =>
            Process.GetProcessesByName("DemulShooter").Length > 0 ||
            Process.GetProcessesByName("DemulShooterX64").Length > 0;

        // Current P1/P2 device names in config.ini (null when absent).
        public static (string p1, string p2) Current(string config)
        {
            string p1 = null, p2 = null;
            if (config == null || !File.Exists(config)) return (null, null);
            foreach (var line in File.ReadAllLines(config))
            {
                var m = Regex.Match(line, @"^P([12])DeviceName\s*=\s*(.*)$");
                if (!m.Success) continue;
                if (m.Groups[1].Value == "1") p1 = m.Groups[2].Value.Trim();
                else p2 = m.Groups[2].Value.Trim();
            }
            return (p1, p2);
        }

        public static bool Bound(string value, int iface) =>
            value != null && value.IndexOf("VID_1209&PID_0A11&MI_0" + iface, StringComparison.OrdinalIgnoreCase) >= 0;

        // Tray start: once a DemulShooter folder is set in the wizard, keep its
        // config.ini on the current bridge paths, so DS never needs a launcher
        // script. Skipped while DS runs (it would not see the change) or when
        // the bridge is not enumerated yet. Returns a log line, or null.
        public static string KeepBound(string dsDir, Dictionary<int, string> mice)
        {
            string ini = ConfigPath(dsDir);
            if (ini == null || !File.Exists(ini) || !mice.ContainsKey(0) || Running()) return null;
            var (p1, p2) = Current(ini);
            bool same = p1 == mice[0] && (!mice.ContainsKey(1) || p2 == mice[1]);
            return same ? null : "DemulShooter re-bound to the bridge: " + Bind(ini, mice);
        }

        // Returns a one-line report; throws on anything that stops the write.
        public static string Bind(string config, Dictionary<int, string> mice)
        {
            if (config == null || !File.Exists(config)) throw new FileNotFoundException("config.ini not found", config);
            if (!mice.ContainsKey(0)) throw new InvalidOperationException("Bridge Mouse 1 not found — is the Pico plugged in?");
            bool p2 = mice.ContainsKey(1);
            var lines = File.ReadAllLines(config);
            var output = new List<string>(lines.Length);
            int changed = 0;
            foreach (var line in lines)
            {
                if (Regex.IsMatch(line, @"^P1DeviceName\s*=")) { output.Add("P1DeviceName = " + mice[0]); changed++; continue; }
                if (Regex.IsMatch(line, @"^P2DeviceName\s*=") && p2) { output.Add("P2DeviceName = " + mice[1]); changed++; continue; }
                if (Regex.IsMatch(line, @"^P1Mode\s*=")) { output.Add("P1Mode = RAWINPUT"); continue; }
                if (Regex.IsMatch(line, @"^P2Mode\s*=") && p2) { output.Add("P2Mode = RAWINPUT"); continue; }
                output.Add(line);
            }
            if (changed == 0)
                throw new InvalidOperationException("no P1DeviceName/P2DeviceName lines in config.ini — open DemulShooter once so it writes them");
            string backup = Backup(config);
            File.WriteAllLines(config, output, Encoding.ASCII);
            return "bound " + (p2 ? "P1 and P2" : "P1 only (no Bridge Mouse 2)") +
                "; backup " + Path.GetFileName(backup);
        }
    }

    // TeknoParrot UserProfiles\<Game>.xml. TP serializes each input as
    //   <JoystickButtons><ButtonName/>[<RawInputButton>DevicePath, DeviceType,
    //   MouseButton, KeyboardKey</RawInputButton>]<InputMapping/><AnalogType/>
    //   [<BindNameRi/><BindName/>]<HideWith.../></JoystickButtons>
    // and an unbound row simply has no RawInputButton (taken from a real
    // JurassicPark.xml, TP 2.0.0.11). A row is on the bridge when its
    // DevicePath is a bridge mouse: MI_00 = player 1, MI_01 = player 2.
    static class TeknoParrotProfiles
    {
        public sealed class Profile
        {
            public string Game, File;
            public bool P1, P2;
        }

        public static string Dir(string tpDir) =>
            string.IsNullOrEmpty(tpDir) ? null : Path.Combine(tpDir, "UserProfiles");

        public static bool Running() =>
            Process.GetProcessesByName("TeknoParrotUi").Length > 0;

        // The process the game runs in: ElfLdr2 games run inside BudgieLoader,
        // other loaders run the game's own exe (GamePath in the profile).
        public static string GameProcess(string profileFile)
        {
            try
            {
                var doc = XDocument.Load(profileFile);
                string emu = (string)doc.Root?.Element("EmulatorType") ?? "";
                if (emu.StartsWith("ElfLdr", StringComparison.OrdinalIgnoreCase)) return "BudgieLoader";
                string path = (string)doc.Root?.Element("GamePath") ?? "";
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFileNameWithoutExtension(path);
            }
            catch { }
            return null;
        }

        // Gun rows by TP's InputMapping, which is the same across games (the
        // ButtonName labels are not: "Trigger P1" in Jurassic Park is "Player 1
        // Vacuum Button" in Luigi's Mansion). Each maps to a button on that
        // player's bridge mouse, the ones the tray sends (docs/buttons.md):
        // trigger left, A middle, B right, Coin button 4, Start button 5.
        // The gun has three action buttons, so Button4 and up are left alone.
        static readonly (Regex mapping, string button)[] Rules =
        {
            (new Regex(@"^P([12])LightGun$"), "None"),
            (new Regex(@"^P([12])Button1$"), "LeftButton"),
            (new Regex(@"^P([12])Button2$"), "MiddleButton"),
            (new Regex(@"^P([12])Button3$"), "RightButton"),
            (new Regex(@"^P([12])ButtonStart$"), "Button5"),
            (new Regex(@"^Coin([12])$"), "Button4"),
        };

        // A row's label can name what the button does; that beats the slot
        // default (a game whose reload sits on Button2 still gets B).
        static string ByName(string name, string fallback) =>
            Regex.IsMatch(name, @"\bReload\b", RegexOptions.IgnoreCase) ? "RightButton"
            : Regex.IsMatch(name, @"\bGrenades?\b", RegexOptions.IgnoreCase) ? "MiddleButton"
            : fallback;

        // Operator rows keep whatever the user bound, even when TP reuses a
        // player slot for them (Jurassic Park's "TestMenu Up" is P1Button3,
        // Luigi's Mansion's "Select Switch" is P1Button4).
        static readonly Regex Operator = new Regex(@"^(Test|Service|Volume|Select)", RegexOptions.IgnoreCase);

        // TP refuses a profile with any other RawMouseButton value.
        static readonly HashSet<string> MouseButtons = new HashSet<string>
            { "None", "LeftButton", "RightButton", "MiddleButton", "Button4", "Button5" };

        static int BridgePlayer(string devicePath)
        {
            var m = Regex.Match(devicePath ?? "", @"VID_1209&PID_0A11&MI_0([01])", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value[0] - '0' + 1 : 0;
        }

        static IEnumerable<XElement> Rows(XDocument doc) =>
            doc.Root?.Element("JoystickButtons")?.Elements("JoystickButtons") ?? Enumerable.Empty<XElement>();

        // Player n counts as bound when its Light Gun row is on bridge mouse n.
        static bool LightGunBound(XDocument doc, int player) =>
            Rows(doc).Any(r => (string)r.Element("InputMapping") == "P" + player + "LightGun" &&
                BridgePlayer((string)r.Element("RawInputButton")?.Element("DevicePath")) == player);

        public static List<Profile> Scan(string tpDir)
        {
            var dir = Dir(tpDir);
            var list = new List<Profile>();
            if (dir == null || !Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "*.xml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                XDocument doc;
                try { doc = XDocument.Load(f); }
                catch { continue; }
                list.Add(new Profile
                {
                    Game = (string)doc.Root?.Element("GameNameInternal") ?? Path.GetFileNameWithoutExtension(f),
                    File = f,
                    P1 = LightGunBound(doc, 1),
                    P2 = LightGunBound(doc, 2),
                });
            }
            return list;
        }

        // Point the gun rows at the bridge mice and set Input API = RawInput.
        // Backs the file up first. TP must be closed: it reads profiles at
        // startup and writes them back when its settings are saved.
        public static string Bind(string file, Dictionary<int, string> mice)
        {
            if (!mice.ContainsKey(0)) throw new InvalidOperationException("Bridge Mouse 1 not found — is the Pico plugged in?");
            var doc = XDocument.Load(file);
            var rows = Rows(doc).ToList();
            if (rows.Count == 0) throw new InvalidOperationException("no JoystickButtons in " + Path.GetFileName(file));

            var bound = new List<string>();
            foreach (var row in rows)
            {
                string name = (string)row.Element("ButtonName") ?? "";
                string mapping = (string)row.Element("InputMapping") ?? "";
                if (Operator.IsMatch(name)) continue;
                foreach (var (rx, slot) in Rules)
                {
                    var m = rx.Match(mapping);
                    if (!m.Success) continue;
                    int iface = m.Groups[1].Value[0] - '1';
                    string button = slot == "None" || slot == "Button4" || slot == "Button5" ? slot : ByName(name, slot);
                    if (!mice.TryGetValue(iface, out var path)) break;
                    if (!MouseButtons.Contains(button)) throw new InvalidOperationException("bad mouse button " + button);
                    SetRow(row, path, button, "G'AIM'E Bridge Mouse " + (iface + 1));
                    bound.Add(name);
                    break;
                }
            }
            if (bound.Count == 0) throw new InvalidOperationException("no light gun rows (P1LightGun) in this profile");

            string api = "unchanged";
            var field = doc.Root.Element("ConfigValues")?.Elements("FieldInformation")
                .FirstOrDefault(f => (string)f.Element("FieldName") == "Input API");
            if (field?.Element("FieldValue") != null && (string)field.Element("FieldValue") != "RawInput")
            {
                field.Element("FieldValue").Value = "RawInput";
                api = "set to RawInput";
            }

            string backup = DemulShooterBinding.Backup(file);
            // Same shape TP writes: UTF-8 with BOM, CRLF, two-space indent.
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(true), Indent = true, IndentChars = "  ", NewLineChars = "\r\n",
            };
            using (var w = XmlWriter.Create(file, settings)) doc.Save(w);
            return "bound " + bound.Count + " rows (" + string.Join(", ", bound) + "), Input API " + api +
                "; backup " + Path.GetFileName(backup);
        }

        static void SetRow(XElement row, string path, string button, string device)
        {
            var ri = new XElement("RawInputButton",
                new XElement("DevicePath", path),
                new XElement("DeviceType", "Mouse"),
                new XElement("MouseButton", button),
                new XElement("KeyboardKey", "None"));
            var old = row.Element("RawInputButton");
            if (old != null) old.ReplaceWith(ri);
            else row.Element("ButtonName").AddAfterSelf(ri);

            string label = button == "None" ? device : device + " " + button;
            row.Element("BindNameRi")?.Remove();
            row.Element("BindName")?.Remove();
            var anchor = row.Element("AnalogType") ?? row.Element("InputMapping") ?? ri;
            anchor.AddAfterSelf(new XElement("BindNameRi", label), new XElement("BindName", label));
        }
    }
}
