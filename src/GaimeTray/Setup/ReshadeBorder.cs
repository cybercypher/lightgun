using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using GaimeFeeder;

namespace GaimeTray
{
    // The white reference border over TeknoParrot's ELF loader games, same
    // install as the old tools/Install-ReshadeBorder.ps1: ReShade as an
    // opengl32.dll proxy next to ElfLdr2\BudgieLoader.exe, plus GaimeBorder.fx
    // and a preset. The shader files ship next to the exe in reshade\.
    static class ReshadeBorder
    {
        public static string ElfLdr(string tpDir) =>
            string.IsNullOrEmpty(tpDir) ? null : Path.Combine(tpDir, "ElfLdr2");

        static string Source => Path.Combine(AppContext.BaseDirectory, "reshade");

        static bool IsReshade(string dll)
        {
            try { return (FileVersionInfo.GetVersionInfo(dll).ProductName ?? "").IndexOf("ReShade", StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { return false; }
        }

        // IMAGE_FILE_HEADER.Machine: 0x14c = x86. BudgieLoader is 32-bit.
        static bool Is32Bit(string dll)
        {
            var b = File.ReadAllBytes(dll);
            if (b.Length < 0x40) return false;
            int pe = BitConverter.ToInt32(b, 0x3C);
            return pe > 0 && pe + 6 <= b.Length && BitConverter.ToUInt16(b, pe + 4) == 0x14c;
        }

        public static string Status(string tpDir)
        {
            var dir = ElfLdr(tpDir);
            if (dir == null || !File.Exists(Path.Combine(dir, "BudgieLoader.exe"))) return "no ElfLdr2\\BudgieLoader.exe in the TeknoParrot folder";
            var proxy = Path.Combine(dir, "opengl32.dll");
            if (!File.Exists(proxy)) return "not installed";
            if (!IsReshade(proxy)) return "opengl32.dll there is not ReShade — left alone";
            return File.Exists(Path.Combine(dir, "reshade-shaders", "Shaders", "GaimeBorder.fx"))
                ? "installed" : "ReShade there, border shader missing — Install again";
        }

        public static bool Installed(string tpDir) => Status(tpDir) == "installed";

        // A ReShade32 DLL already in ElfLdr2 (a previous install), if any.
        public static string FindDll(string tpDir)
        {
            var dir = ElfLdr(tpDir);
            if (dir == null) return null;
            foreach (var name in new[] { "ReShade32.dll", "opengl32.dll" })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p) && IsReshade(p)) return p;
            }
            return null;
        }

        public static string Install(string tpDir, string reshadeDll)
        {
            var dir = ElfLdr(tpDir);
            if (dir == null || !File.Exists(Path.Combine(dir, "BudgieLoader.exe")))
                throw new InvalidOperationException("no ElfLdr2\\BudgieLoader.exe in the TeknoParrot folder");
            if (!File.Exists(Path.Combine(Source, "GaimeBorder.fx")))
                throw new FileNotFoundException("reshade\\GaimeBorder.fx missing next to GaimeTray.exe");
            if (reshadeDll == null || !File.Exists(reshadeDll)) throw new FileNotFoundException("pick a ReShade DLL");
            if (!Is32Bit(reshadeDll)) throw new InvalidOperationException("that DLL is not 32-bit — use ReShade32, not ReShade64");
            var proxy = Path.Combine(dir, "opengl32.dll");
            if (File.Exists(proxy) && !IsReshade(proxy))
                throw new InvalidOperationException("ElfLdr2\\opengl32.dll exists and is not ReShade — refusing to overwrite");

            if (!string.Equals(Path.GetFullPath(reshadeDll), Path.GetFullPath(proxy), StringComparison.OrdinalIgnoreCase))
                File.Copy(reshadeDll, proxy, true);
            var shaders = Path.Combine(dir, "reshade-shaders", "Shaders");
            Directory.CreateDirectory(shaders);
            File.Copy(Path.Combine(Source, "GaimeBorder.fx"), Path.Combine(shaders, "GaimeBorder.fx"), true);
            File.Copy(Path.Combine(Source, "ReShade.fxh"), Path.Combine(shaders, "ReShade.fxh"), true);
            File.Copy(Path.Combine(Source, "ReShade.ini"), Path.Combine(dir, "ReShade.ini"), true);
            // The preset carries the thickness; it always matches the tray's own border.
            var preset = Regex.Replace(File.ReadAllText(Path.Combine(Source, "ReShadePreset.ini")),
                @"(?m)^BORDER_PX=.*$", "BORDER_PX=" + ScreenBorder.Pixels + ".000000");
            File.WriteAllText(Path.Combine(dir, "ReShadePreset.ini"), preset);
            try { File.Delete(Path.Combine(dir, "ReShade.log")); } catch { }
            return "installed: " + ScreenBorder.Pixels + " px border on every ElfLdr2 game";
        }

        public static string Uninstall(string tpDir)
        {
            var dir = ElfLdr(tpDir);
            if (dir == null || !Directory.Exists(dir)) return "nothing to remove";
            var proxy = Path.Combine(dir, "opengl32.dll");
            if (File.Exists(proxy) && !IsReshade(proxy)) return "opengl32.dll is not ReShade — left alone";
            if (File.Exists(proxy)) File.Delete(proxy);
            foreach (var f in new[] { "ReShade.ini", "ReShadePreset.ini", "ReShade.log" })
                try { File.Delete(Path.Combine(dir, f)); } catch { }
            var shaders = Path.Combine(dir, "reshade-shaders");
            if (File.Exists(Path.Combine(shaders, "Shaders", "GaimeBorder.fx"))) Directory.Delete(shaders, true);
            return "removed";
        }
    }
}
