using System;
using System.IO;

namespace GaimeFeeder
{
    // Which OS device the feeder drives:
    //   pico  - the G'AIM'E Pico bridge (two absolute HID mice over CDC); default
    //   vigem - the legacy ViGEm Xbox360 pads
    //
    // Exactly one is active at a time, so the two never compete for devices or
    // for DemulShooter's P1/P2 binding. Read once at startup from output.txt in
    // the data root (see AppPaths); a missing file means pico.
    static class OutputConfig
    {
        static readonly string _mode = Load();

        public static string Mode => _mode;
        public static bool UsePico =>
            !string.Equals(_mode, "vigem", StringComparison.OrdinalIgnoreCase);

        static string Load()
        {
            try
            {
                var path = AppPaths.OutputConfig;
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path).Trim().ToLowerInvariant();
                    if (text == "vigem" || text == "pico") return text;
                }
            }
            catch { }
            return "pico";
        }
    }
}
