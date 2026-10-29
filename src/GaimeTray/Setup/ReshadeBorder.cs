using System;
using System.IO;
using System.IO.Compression;
using System.Text;
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

        static bool IsReshade(string path)
        {
            try { return File.Exists(path) && Identify(File.ReadAllBytes(path)) == null; }
            catch { return false; }
        }

        // Null when the bytes are a 32-bit ReShade DLL, else what is wrong.
        // A DLL (IMAGE_FILE_DLL), machine x86 (BudgieLoader is 32-bit), and
        // "ReShade" in its UTF-16 version resource. The flag check is what
        // rejects ReShade's own setup exe, which also names ReShade.
        static string Identify(byte[] b)
        {
            if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') return "not a Windows DLL";
            int pe = BitConverter.ToInt32(b, 0x3C);
            if (pe <= 0 || pe + 24 > b.Length || BitConverter.ToUInt32(b, pe) != 0x00004550) return "not a Windows DLL";
            if ((BitConverter.ToUInt16(b, pe + 22) & 0x2000) == 0) return "that is a program, not a DLL";
            ushort machine = BitConverter.ToUInt16(b, pe + 4);
            if (machine == 0x8664) return "that is 64-bit ReShade — BudgieLoader needs ReShade32";
            if (machine != 0x14c) return "not a 32-bit DLL";
            var name = Encoding.Unicode.GetBytes("ReShade");
            return IndexOf(b, name) >= 0 ? null : "not a ReShade DLL";
        }

        static int IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= hay.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        // ReShade32.dll from what the user picked: the DLL itself, or the
        // official ReShade_Setup_*.exe from reshade.me, which is the setup
        // program with a plain zip appended (ReShade32.dll, ReShade64.dll, ...).
        // The zip's offsets are relative to its own start, so the start is
        // found from the end-of-central-directory record.
        public static byte[] LoadReshade32(string path)
        {
            if (path == null || !File.Exists(path)) throw new FileNotFoundException("pick ReShade_Setup_*.exe or ReShade32.dll");
            var bytes = File.ReadAllBytes(path);
            byte[] dll = bytes;
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                int eocd = -1;
                for (int i = bytes.Length - 22; i >= Math.Max(0, bytes.Length - 65557); i--)
                    if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 5 && bytes[i + 3] == 6) { eocd = i; break; }
                if (eocd < 0) throw new InvalidOperationException("that file has no ReShade inside — use ReShade_Setup_*.exe from reshade.me");
                long start = eocd - BitConverter.ToUInt32(bytes, eocd + 12) - BitConverter.ToUInt32(bytes, eocd + 16);
                if (start < 0) throw new InvalidOperationException("could not read the ReShade setup archive");
                using (var zip = new ZipArchive(new MemoryStream(bytes, (int)start, bytes.Length - (int)start), ZipArchiveMode.Read))
                {
                    var entry = zip.GetEntry("ReShade32.dll")
                        ?? throw new InvalidOperationException("no ReShade32.dll in that setup file");
                    using (var s = entry.Open())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        dll = ms.ToArray();
                    }
                }
            }
            var problem = Identify(dll);
            if (problem != null) throw new InvalidOperationException(problem);
            return dll;
        }

        // On/off without reinstalling: off renames our opengl32.dll to OffName,
        // so BudgieLoader falls back to Windows' own opengl32 (stock ElfLdr2
        // has no opengl32.dll). A foreign opengl32.dll found at install time is
        // kept as OrigName and is back in place whenever the border is off or
        // removed.
        const string ProxyName = "opengl32.dll";
        const string OffName = "opengl32.dll.gaime-off";
        const string OrigName = "opengl32.dll.gaime-orig";

        public enum State { NoLoader, NotInstalled, On, Off, Foreign, ShaderMissing }

        public static State Get(string tpDir)
        {
            var dir = ElfLdr(tpDir);
            if (dir == null || !File.Exists(Path.Combine(dir, "BudgieLoader.exe"))) return State.NoLoader;
            var proxy = Path.Combine(dir, ProxyName);
            bool shader = File.Exists(Path.Combine(dir, "reshade-shaders", "Shaders", "GaimeBorder.fx"));
            if (File.Exists(proxy) && IsReshade(proxy)) return shader ? State.On : State.ShaderMissing;
            if (IsReshade(Path.Combine(dir, OffName))) return shader ? State.Off : State.ShaderMissing;
            return File.Exists(proxy) ? State.Foreign : State.NotInstalled;
        }

        public static string Describe(State st)
        {
            switch (st)
            {
                case State.NoLoader: return "no ElfLdr2\\BudgieLoader.exe in the TeknoParrot folder";
                case State.NotInstalled: return "not installed";
                case State.On: return "border ON in ElfLdr2 games";
                case State.Off: return "border OFF (installed; the game uses its original OpenGL)";
                case State.Foreign: return "another opengl32.dll is there — Install keeps it as a backup";
                default: return "border shader missing — Install again";
            }
        }

        // A ReShade32 DLL already in ElfLdr2 (a previous install), if any.
        public static string FindDll(string tpDir)
        {
            var dir = ElfLdr(tpDir);
            if (dir == null) return null;
            foreach (var name in new[] { ProxyName, OffName, "ReShade32.dll" })
            {
                var p = Path.Combine(dir, name);
                if (IsReshade(p)) return p;
            }
            return null;
        }

        // Swap ReShade in or out. Off: ours -> OffName, original back. On: the
        // reverse. Takes effect at the next game launch.
        public static string SetEnabled(string tpDir, bool on)
        {
            var dir = ElfLdr(tpDir);
            var st = Get(tpDir);
            var proxy = Path.Combine(dir ?? "", ProxyName);
            var off = Path.Combine(dir ?? "", OffName);
            var orig = Path.Combine(dir ?? "", OrigName);
            if (on)
            {
                if (st == State.On) return Describe(st);
                if (st != State.Off) throw new InvalidOperationException("install the border first");
                if (File.Exists(proxy))
                {
                    if (File.Exists(orig)) throw new InvalidOperationException("both opengl32.dll and its backup exist — remove one by hand");
                    File.Move(proxy, orig);
                }
                File.Move(off, proxy);
            }
            else
            {
                if (st == State.Off) return Describe(st);
                if (st != State.On && st != State.ShaderMissing) throw new InvalidOperationException("the border is not installed");
                if (File.Exists(off)) File.Delete(off);
                File.Move(proxy, off);
                if (File.Exists(orig)) File.Move(orig, proxy);
            }
            return Describe(Get(tpDir)) + " — applies at the next game launch";
        }

        // reshadeSource: ReShade_Setup_*.exe or a ReShade32.dll.
        public static string Install(string tpDir, string reshadeSource)
        {
            var dir = ElfLdr(tpDir);
            if (dir == null || !File.Exists(Path.Combine(dir, "BudgieLoader.exe")))
                throw new InvalidOperationException("no ElfLdr2\\BudgieLoader.exe in the TeknoParrot folder");
            if (!File.Exists(Path.Combine(Source, "GaimeBorder.fx")))
                throw new FileNotFoundException("reshade\\GaimeBorder.fx missing next to GaimeTray.exe");
            byte[] dll = LoadReshade32(reshadeSource);
            var proxy = Path.Combine(dir, ProxyName);
            var orig = Path.Combine(dir, OrigName);
            if (File.Exists(proxy) && !IsReshade(proxy))
            {
                // Someone else's proxy: keep it, it comes back when ours is off.
                if (File.Exists(orig)) throw new InvalidOperationException("ElfLdr2 already has " + OrigName + " — remove one by hand");
                File.Move(proxy, orig);
            }
            try { File.Delete(Path.Combine(dir, OffName)); } catch { }
            File.WriteAllBytes(proxy, dll);
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

        // Back to stock: our files out, any original opengl32.dll restored.
        public static string Uninstall(string tpDir)
        {
            var dir = ElfLdr(tpDir);
            if (dir == null || !Directory.Exists(dir)) return "nothing to remove";
            var proxy = Path.Combine(dir, ProxyName);
            var orig = Path.Combine(dir, OrigName);
            if (File.Exists(proxy) && !IsReshade(proxy) && !File.Exists(orig))
                return "opengl32.dll there is not ours — left alone";
            if (File.Exists(proxy) && IsReshade(proxy)) File.Delete(proxy);
            try { File.Delete(Path.Combine(dir, OffName)); } catch { }
            if (File.Exists(orig) && !File.Exists(proxy)) File.Move(orig, proxy);
            foreach (var f in new[] { "ReShade.ini", "ReShadePreset.ini", "ReShade.log" })
                try { File.Delete(Path.Combine(dir, f)); } catch { }
            var shaders = Path.Combine(dir, "reshade-shaders");
            if (File.Exists(Path.Combine(shaders, "Shaders", "GaimeBorder.fx"))) Directory.Delete(shaders, true);
            return "removed" + (File.Exists(proxy) ? " (original opengl32.dll restored)" : "");
        }
    }
}
