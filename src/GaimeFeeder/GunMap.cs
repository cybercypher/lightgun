using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GaimeFeeder
{
    // Stable Gun 1 / Gun 2 naming, keyed by USB serial. Gun 1 = <usb-serial>
    // (plugged in first, physically labeled). Persists in guns.json so a
    // gun is always the same number -> same ViGEm pad (J1/J2).
    static class GunMap
    {
        static string Path()
        {
            var dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", ".."));
            return System.IO.Path.Combine(dir, "guns.json");
        }

        public static Dictionary<string, int> Load(IEnumerable<string> seen)
        {
            var map = new Dictionary<string, int>();
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(Path()));
                foreach (var kv in doc.RootElement.GetProperty("guns").EnumerateObject())
                    map[kv.Name] = kv.Value.GetInt32();
            }
            catch { }
            bool changed = false;
            int next = map.Count == 0 ? 1 : map.Values.Max() + 1;
            foreach (var s in seen)
                if (!map.ContainsKey(s)) { map[s] = next++; changed = true; }
            if (changed) Save(map);
            return map;
        }

        static void Save(Dictionary<string, int> map)
        {
            var obj = new Dictionary<string, Dictionary<string, int>> { ["guns"] = map };
            File.WriteAllText(Path(), JsonSerializer.Serialize(obj,
                new JsonSerializerOptions { WriteIndented = true }));
        }

        public static string Label(Dictionary<string, int> map, string serial) =>
            map.TryGetValue(serial, out int n) ? $"Gun {n} ({serial})" : $"Gun ? ({serial})";
    }
}
