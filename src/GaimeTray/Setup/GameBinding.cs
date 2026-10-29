using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GaimeTray
{
    // DemulShooter: same rewrite as pico/gun_bridge/host/fix_demulshooter_paths.ps1
    // (P1/P2DeviceName = the bridge mice, P1/P2Mode = RAWINPUT, timestamped
    // backup). DS reads config.ini once at startup.
    static class DemulShooterBinding
    {
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
            string backup = config + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(config, backup);
            File.WriteAllLines(config, output, Encoding.ASCII);
            return "bound " + (p2 ? "P1 and P2" : "P1 only (no Bridge Mouse 2)") +
                "; backup " + Path.GetFileName(backup);
        }
    }

    // TeknoParrot: read-only for now. A profile is bound when its XML names the
    // bridge mice (MI_00 for P1, MI_01 for P2) — the unbound-P2 case is the
    // most common "gun 2 does nothing" (SETUP.md §5.4).
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

        public static List<Profile> Scan(string tpDir)
        {
            var dir = Dir(tpDir);
            var list = new List<Profile>();
            if (dir == null || !Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "*.xml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string text;
                try { text = File.ReadAllText(f); }
                catch { continue; }
                list.Add(new Profile
                {
                    Game = Path.GetFileNameWithoutExtension(f),
                    File = f,
                    P1 = text.IndexOf("VID_1209&amp;PID_0A11&amp;MI_00", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         text.IndexOf("VID_1209&PID_0A11&MI_00", StringComparison.OrdinalIgnoreCase) >= 0,
                    P2 = text.IndexOf("VID_1209&amp;PID_0A11&amp;MI_01", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         text.IndexOf("VID_1209&PID_0A11&MI_01", StringComparison.OrdinalIgnoreCase) >= 0,
                });
            }
            return list;
        }
    }
}
