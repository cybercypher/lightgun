using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;
using GaimeFeeder;

namespace GaimeTray
{
    sealed class CheckResult
    {
        public Check State;
        public string Name, Detail;
        public CheckResult(Check s, string name, string detail) { State = s; Name = name; Detail = detail; }
    }

    // The PC-side prerequisites from SETUP.md, as checks the wizard can show.
    static class SystemChecks
    {
        // Scale of the primary display in percent. Read from the registry
        // because a DPI-unaware process is told 96 DPI whatever the setting.
        public static int ScalePercent()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop\WindowMetrics"))
                    if (k?.GetValue("AppliedDPI") is int dpi && dpi > 0) return dpi * 100 / 96;
            }
            catch { }
            return 100;
        }

        public static CheckResult Scaling()
        {
            int pct = ScalePercent();
            return pct == 100
                ? new CheckResult(Check.Ok, "Display scaling", "100 %")
                : new CheckResult(Check.Fail, "Display scaling",
                    pct + " % — absolute aiming needs 100 % (Settings → Display → Scale), then sign out and back in");
        }

        public static CheckResult Resolution()
        {
            var b = Screen.PrimaryScreen.Bounds;
            return new CheckResult(Check.Info, "Primary screen",
                b.Width + " x " + b.Height + " — calibrations are tied to this size");
        }

        public static CheckResult BridgeMice()
        {
            Dictionary<int, string> mice;
            try { mice = BridgeDevices.Mice(); }
            catch (Exception ex) { return new CheckResult(Check.Fail, "Pico bridge", "lookup failed: " + ex.Message); }
            bool p1 = mice.ContainsKey(0), p2 = mice.ContainsKey(1);
            if (p1 && p2) return new CheckResult(Check.Ok, "Pico bridge", "Bridge Mouse 1 and 2 present");
            if (p1 || p2) return new CheckResult(Check.Warn, "Pico bridge",
                "only Bridge Mouse " + (p1 ? "1" : "2") + " found — reflash the bridge firmware");
            return new CheckResult(Check.Fail, "Pico bridge",
                "not found (VID 1209 / PID 0A11) — plug it in, or flash it below");
        }

        public static CheckResult BridgePort()
        {
            string status = PicoOutput.Status;
            if (status.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                return new CheckResult(Check.Ok, "Bridge feed port", status + " open");
            string port = PicoOutput.BridgePort();
            return port == null
                ? new CheckResult(Check.Fail, "Bridge feed port", "no COM port assigned to the bridge yet")
                : new CheckResult(Check.Warn, "Bridge feed port", port + " known, not open (" + status + ")");
        }

        // Counts vendor interfaces only: no gun query, so this is safe to poll.
        public static CheckResult Guns()
        {
            int n;
            try { n = HidIdentity.VendorPaths().Count; }
            catch (Exception ex) { return new CheckResult(Check.Fail, "Guns", "lookup failed: " + ex.Message); }
            if (n == 2) return new CheckResult(Check.Ok, "Guns", "2 guns connected");
            if (n == 1) return new CheckResult(Check.Warn, "Guns", "1 gun connected — fine for one player");
            if (n == 0) return new CheckResult(Check.Fail, "Guns",
                "none — plug a gun in (a gun whose app layer died looks unplugged too, see tools/gun/README.md)");
            return new CheckResult(Check.Warn, "Guns", n + " guns connected — the bridge has two players");
        }

        public static CheckResult Feeding(FeederEngine eng) =>
            eng.Feeding
                ? new CheckResult(Check.Ok, "Tray", "feeding")
                : new CheckResult(Check.Warn, "Tray", "stopped — press Start in the main window");

        public static List<CheckResult> All(FeederEngine eng) =>
            new List<CheckResult> { Scaling(), Resolution(), BridgeMice(), BridgePort(), Guns(), Feeding(eng) };

        // A Pico in BOOTSEL mode shows up as a removable drive with INFO_UF2.TXT.
        public static string Uf2Drive()
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType == DriveType.Removable && d.IsReady &&
                        File.Exists(Path.Combine(d.RootDirectory.FullName, "INFO_UF2.TXT")))
                        return d.RootDirectory.FullName;
                }
                catch { }
            }
            return null;
        }

        // gun_bridge.uf2 shipped next to the exe (release zip), if any.
        public static string BundledUf2()
        {
            foreach (var dir in new[] { AppContext.BaseDirectory, AppPaths.Root })
            {
                var p = Path.Combine(dir, "gun_bridge.uf2");
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }
}
