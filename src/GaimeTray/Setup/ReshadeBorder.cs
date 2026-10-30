using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GaimeFeeder;

namespace GaimeTray
{
    // The white reference border inside games: ReShade as a proxy DLL next to
    // the process that renders, plus GaimeBorder.fx and a preset (the shader
    // files ship next to the exe in reshade\).
    //
    // Where it goes depends on the TeknoParrot profile (a Target):
    //   ElfLdr2 games (Jurassic Park, Terminator): one shared install next to
    //     ElfLdr2\BudgieLoader.exe, 32-bit, opengl32.dll.
    //   Windows games (Luigi's Mansion): next to the game's own exe, 32- or
    //     64-bit like the exe, named for the graphics API the exe (or a DLL
    //     beside it) imports: dxgi.dll for D3D10/11/12, d3d9.dll, opengl32.dll.
    static class ReshadeBorder
    {
        public sealed class Target
        {
            public string Dir, Proxy, Api, Label;
            public string Process;      // the process the border is drawn in
            public bool Is64, Shared;
            public string Problem;      // non-null: no border possible for this game
            public string Key => (Dir + "|" + Proxy).ToLowerInvariant();
        }

        public static string ElfLdr(string tpDir) =>
            string.IsNullOrEmpty(tpDir) ? null : Path.Combine(tpDir, "ElfLdr2");

        static string Source => Path.Combine(AppContext.BaseDirectory, "reshade");

        // ---- which target a profile needs -------------------------------------

        public static Target For(string tpDir, string profileFile)
        {
            XDocument doc;
            try { doc = XDocument.Load(profileFile); }
            catch (Exception ex) { return new Target { Problem = "profile unreadable: " + ex.Message }; }
            string emu = (string)doc.Root?.Element("EmulatorType") ?? "";
            if (emu.StartsWith("ElfLdr", StringComparison.OrdinalIgnoreCase))
            {
                var dir = ElfLdr(tpDir);
                return new Target
                {
                    Dir = dir, Proxy = "opengl32.dll", Api = "OpenGL", Is64 = false, Shared = true, Process = "BudgieLoader",
                    Label = "ElfLdr2 (shared by every ElfLdr2 game)",
                    Problem = dir != null && File.Exists(Path.Combine(dir, "BudgieLoader.exe"))
                        ? null : "no ElfLdr2\\BudgieLoader.exe in the TeknoParrot folder",
                };
            }
            string exe = (string)doc.Root?.Element("GamePath") ?? "";
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return new Target { Problem = "the profile's game path is not a Windows .exe (" + Path.GetFileName(exe) + ") — set the game in TeknoParrot" };
            if (!File.Exists(exe)) return new Target { Problem = "game exe not found: " + exe };
            // Reading a large game exe on every refresh is slow; reuse the answer
            // until the exe changes.
            var stamp = exe.ToLowerInvariant() + "|" + File.GetLastWriteTimeUtc(exe).Ticks;
            lock (Cache) if (Cache.TryGetValue(stamp, out var cached)) return cached;
            var made = ForExe(exe);
            lock (Cache) Cache[stamp] = made;
            return made;
        }

        static readonly Dictionary<string, Target> Cache = new Dictionary<string, Target>();

        static Target ForExe(string exe)
        {
            byte[] b;
            try { b = File.ReadAllBytes(exe); }
            catch (Exception ex) { return new Target { Problem = "cannot read " + Path.GetFileName(exe) + ": " + ex.Message }; }
            ushort machine = Machine(b);
            if (machine != 0x14c && machine != 0x8664) return new Target { Problem = Path.GetFileName(exe) + " is not an x86/x64 program" };
            var (api, proxy) = DetectApi(exe, b);
            if (proxy == null && Chosen(exe) is string pick)
                (api, proxy) = (ApiOf(pick) + " (chosen)", pick);
            var t = new Target
            {
                Dir = Path.GetDirectoryName(exe), Proxy = proxy, Api = api, Is64 = machine == 0x8664,
                Process = Path.GetFileNameWithoutExtension(exe),
                Label = Path.GetFileName(exe) + " (" + (machine == 0x8664 ? "64" : "32") + "-bit, " + (api ?? "?") + ")",
            };
            if (proxy == null)
                t.Problem = api == null
                    ? "could not tell which graphics API " + Path.GetFileName(exe) + " uses — Install border lets you pick it"
                    : api + " is not supported for the border";
            return t;
        }

        // The exe's imports first; engines often keep the renderer in a DLL
        // beside the exe, so those are checked when the exe names none.
        static (string api, string proxy) DetectApi(string exe, byte[] exeBytes)
        {
            var found = Classify(Imports(exeBytes));
            if (found.api != null && !found.api.Contains("inferred")) return found;
            try
            {
                foreach (var dll in Directory.GetFiles(Path.GetDirectoryName(exe), "*.dll"))
                {
                    var name = Path.GetFileName(dll).ToLowerInvariant();
                    if (name.EndsWith(".gaime-off") || IsReshade(dll)) continue;
                    var r = Classify(Imports(File.ReadAllBytes(dll)));
                    if (r.api != null && !r.api.Contains("inferred")) return r;
                }
            }
            catch { }
            return found;
        }

        // Choices for a game whose API cannot be read (Install border asks).
        public static readonly (string label, string proxy)[] Choices =
        {
            ("Direct3D 10 / 11 / 12", "dxgi.dll"),
            ("Direct3D 9", "d3d9.dll"),
            ("OpenGL", "opengl32.dll"),
        };

        static string ApiOf(string proxy) =>
            Choices.FirstOrDefault(c => c.proxy == proxy).label ?? proxy;

        static string Chosen(string exe)
        {
            var map = SetupConfig.Load().BorderProxy;
            return map != null && map.TryGetValue(exe.ToLowerInvariant(), out var p) &&
                   Choices.Any(c => c.proxy == p) ? p : null;
        }

        // Remember a hand-picked API for this exe; clears the cached answer.
        public static void Choose(string exe, string proxy)
        {
            var cfg = SetupConfig.Load();
            cfg.BorderProxy = cfg.BorderProxy ?? new Dictionary<string, string>();
            cfg.BorderProxy[exe.ToLowerInvariant()] = proxy;
            cfg.Save();
            lock (Cache) Cache.Clear();
        }

        // The exe the profile runs (null for ElfLdr2 / non-exe profiles).
        public static string ExeOf(string profileFile)
        {
            try
            {
                string exe = (string)XDocument.Load(profileFile).Root?.Element("GamePath") ?? "";
                return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : null;
            }
            catch { return null; }
        }

        static (string api, string proxy) Classify(List<string> imports)
        {
            bool Has(params string[] n) => imports.Any(i => n.Contains(i));
            if (Has("d3d12.dll")) return ("Direct3D 12", "dxgi.dll");
            if (Has("d3d11.dll")) return ("Direct3D 11", "dxgi.dll");
            if (Has("d3d10.dll", "d3d10_1.dll")) return ("Direct3D 10", "dxgi.dll");
            if (Has("d3d9.dll")) return ("Direct3D 9", "d3d9.dll");
            if (Has("opengl32.dll")) return ("OpenGL", "opengl32.dll");
            if (Has("dxgi.dll")) return ("DXGI", "dxgi.dll");
            if (Has("d3d8.dll")) return ("Direct3D 8", null);
            if (Has("vulkan-1.dll")) return ("Vulkan", null);
            // Protected exes (Capcom's packer on Luigi's Mansion) hide their real
            // imports; the unpacking stub still loads the Direct3D shader
            // compiler, which goes with Direct3D 10/11.
            if (imports.Any(i => i.StartsWith("d3dcompiler_")))
                return ("Direct3D 11 (inferred from " + imports.First(i => i.StartsWith("d3dcompiler_")) + ")", "dxgi.dll");
            return (null, null);
        }

        static ushort Machine(byte[] b)
        {
            if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') return 0;
            int pe = BitConverter.ToInt32(b, 0x3C);
            if (pe <= 0 || pe + 24 > b.Length || BitConverter.ToUInt32(b, pe) != 0x00004550) return 0;
            return BitConverter.ToUInt16(b, pe + 4);
        }

        // Lower-case DLL names from the import and delay-import tables.
        internal static List<string> Imports(byte[] b)
        {
            var names = new List<string>();
            try
            {
                if (Machine(b) == 0) return names;
                int pe = BitConverter.ToInt32(b, 0x3C);
                int sections = BitConverter.ToUInt16(b, pe + 6);
                int optSize = BitConverter.ToUInt16(b, pe + 20);
                int opt = pe + 24;
                bool plus = BitConverter.ToUInt16(b, opt) == 0x20b;
                int dirs = opt + (plus ? 112 : 96);
                int secTable = opt + optSize;

                long Offset(uint rva)
                {
                    for (int i = 0; i < sections; i++)
                    {
                        int s = secTable + i * 40;
                        uint va = BitConverter.ToUInt32(b, s + 12), size = Math.Max(BitConverter.ToUInt32(b, s + 8), BitConverter.ToUInt32(b, s + 16));
                        uint raw = BitConverter.ToUInt32(b, s + 20);
                        if (rva >= va && rva < va + size) return rva - va + raw;
                    }
                    return -1;
                }
                string Str(uint rva)
                {
                    long o = Offset(rva);
                    if (o < 0 || o >= b.Length) return null;
                    int end = (int)o;
                    while (end < b.Length && b[end] != 0 && end - o < 260) end++;
                    return Encoding.ASCII.GetString(b, (int)o, end - (int)o).ToLowerInvariant();
                }

                // Directory 1: imports (20-byte descriptors, name at +12).
                // Directory 13: delay imports (32-byte descriptors, name at +4).
                foreach (var (index, size, nameAt) in new[] { (1, 20, 12), (13, 32, 4) })
                {
                    uint rva = BitConverter.ToUInt32(b, dirs + index * 8);
                    if (rva == 0) continue;
                    long o = Offset(rva);
                    for (int n = 0; o >= 0 && o + size <= b.Length && n < 512; n++, o += size)
                    {
                        uint nameRva = BitConverter.ToUInt32(b, (int)o + nameAt);
                        if (nameRva == 0) break;
                        var s = Str(nameRva);
                        if (!string.IsNullOrEmpty(s)) names.Add(s);
                    }
                }
            }
            catch { }
            return names;
        }

        // ---- ReShade binaries -------------------------------------------------

        static bool IsReshade(string path, bool? is64 = null)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var b = File.ReadAllBytes(path);
                return is64.HasValue ? Identify(b, is64.Value) == null
                    : Identify(b, false) == null || Identify(b, true) == null;
            }
            catch { return false; }
        }

        // Null when the bytes are a ReShade DLL of the wanted bitness, else
        // what is wrong. A DLL (IMAGE_FILE_DLL), the right machine, and
        // "ReShade" in its UTF-16 version resource. The flag check is what
        // rejects ReShade's own setup exe, which also names ReShade.
        static string Identify(byte[] b, bool want64)
        {
            ushort machine = Machine(b);
            if (machine == 0) return "not a Windows DLL";
            int pe = BitConverter.ToInt32(b, 0x3C);
            if ((BitConverter.ToUInt16(b, pe + 22) & 0x2000) == 0) return "that is a program, not a DLL";
            if (machine == 0x8664 && !want64) return "that is 64-bit ReShade — this game needs ReShade32";
            if (machine == 0x14c && want64) return "that is 32-bit ReShade — this game needs ReShade64";
            if (machine != 0x14c && machine != 0x8664) return "not an x86/x64 DLL";
            return IndexOf(b, Encoding.Unicode.GetBytes("ReShade")) >= 0 ? null : "not a ReShade DLL";
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

        // ReShade32.dll or ReShade64.dll from what the user picked: the DLL
        // itself, or the official ReShade_Setup_*.exe from reshade.me, which is
        // the setup program with a plain zip appended (ReShade32.dll,
        // ReShade64.dll, ...). The zip's offsets are relative to its own start,
        // so the start is found from the end-of-central-directory record.
        public static byte[] LoadReshade(string path, bool is64)
        {
            string entryName = is64 ? "ReShade64.dll" : "ReShade32.dll";
            if (path == null || !File.Exists(path)) throw new FileNotFoundException("pick ReShade_Setup_*.exe or " + entryName);
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
                    var entry = zip.GetEntry(entryName)
                        ?? throw new InvalidOperationException("no " + entryName + " in that setup file");
                    using (var s = entry.Open())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        dll = ms.ToArray();
                    }
                }
            }
            var problem = Identify(dll, is64);
            if (problem != null) throw new InvalidOperationException(problem);
            return dll;
        }

        // A matching ReShade already at the target (a previous install), if any.
        public static string FindDll(Target t)
        {
            if (t?.Dir == null || t.Problem != null) return null;
            foreach (var name in new[] { t.Proxy, Off(t), t.Is64 ? "ReShade64.dll" : "ReShade32.dll" })
            {
                var p = Path.Combine(t.Dir, name);
                if (IsReshade(p, t.Is64)) return p;
            }
            return null;
        }

        // ---- state, on/off, install, remove -----------------------------------

        // On/off without reinstalling: off renames our proxy to <proxy>.gaime-off,
        // so the game falls back to Windows' own DLL (a stock game folder has no
        // dxgi.dll / d3d9.dll / opengl32.dll of its own). A foreign proxy found at
        // install time is kept as <proxy>.gaime-orig and is back in place
        // whenever the border is off or removed.
        static string Off(Target t) => t.Proxy + ".gaime-off";
        static string Orig(Target t) => t.Proxy + ".gaime-orig";

        public enum State { Unsupported, NotInstalled, On, Off, Foreign, ShaderMissing }

        public static State Get(Target t)
        {
            if (t == null || t.Problem != null || t.Dir == null) return State.Unsupported;
            var proxy = Path.Combine(t.Dir, t.Proxy);
            bool shader = File.Exists(Path.Combine(t.Dir, "reshade-shaders", "Shaders", "GaimeBorder.fx"));
            if (IsReshade(proxy, t.Is64)) return shader ? State.On : State.ShaderMissing;
            if (IsReshade(Path.Combine(t.Dir, Off(t)), t.Is64)) return shader ? State.Off : State.ShaderMissing;
            return File.Exists(proxy) ? State.Foreign : State.NotInstalled;
        }

        public static string Describe(Target t, State st)
        {
            switch (st)
            {
                case State.Unsupported: return t?.Problem ?? "no game selected";
                case State.NotInstalled: return "not installed — " + t.Label;
                case State.On: return "border ON — " + t.Label;
                case State.Off: return "border OFF (installed; the game uses its original " + t.Proxy + ") — " + t.Label;
                case State.Foreign: return "another " + t.Proxy + " is there — Install keeps it as a backup";
                default: return "border shader missing — Install again";
            }
        }

        // Swap ReShade in or out. Off: ours -> .gaime-off, original back. On:
        // the reverse. Takes effect at the next game launch.
        public static string SetEnabled(Target t, bool on)
        {
            var st = Get(t);
            if (st == State.Unsupported) throw new InvalidOperationException(t?.Problem ?? "no game");
            var proxy = Path.Combine(t.Dir, t.Proxy);
            var off = Path.Combine(t.Dir, Off(t));
            var orig = Path.Combine(t.Dir, Orig(t));
            if (on)
            {
                if (st == State.On) return Describe(t, st);
                if (st != State.Off) throw new InvalidOperationException("install the border first");
                if (File.Exists(proxy))
                {
                    if (File.Exists(orig)) throw new InvalidOperationException("both " + t.Proxy + " and its backup exist — remove one by hand");
                    File.Move(proxy, orig);
                }
                File.Move(off, proxy);
            }
            else
            {
                if (st == State.Off) return Describe(t, st);
                if (st != State.On && st != State.ShaderMissing) throw new InvalidOperationException("the border is not installed");
                if (File.Exists(off)) File.Delete(off);
                File.Move(proxy, off);
                if (File.Exists(orig)) File.Move(orig, proxy);
            }
            return Describe(t, Get(t)) + " — applies at the next game launch";
        }

        // reshadeSource: ReShade_Setup_*.exe or a ReShade32/64.dll.
        public static string Install(Target t, string reshadeSource)
        {
            if (t == null || t.Problem != null) throw new InvalidOperationException(t?.Problem ?? "no game");
            if (!File.Exists(Path.Combine(Source, "GaimeBorder.fx")))
                throw new FileNotFoundException("reshade\\GaimeBorder.fx missing next to GaimeTray.exe");
            byte[] dll = LoadReshade(reshadeSource, t.Is64);
            var dir = t.Dir;
            var proxy = Path.Combine(dir, t.Proxy);
            var orig = Path.Combine(dir, Orig(t));
            if (File.Exists(proxy) && !IsReshade(proxy, t.Is64))
            {
                // Someone else's proxy: keep it, it comes back when ours is off.
                if (File.Exists(orig)) throw new InvalidOperationException("the folder already has " + Orig(t) + " — remove one by hand");
                File.Move(proxy, orig);
            }
            try { File.Delete(Path.Combine(dir, Off(t))); } catch { }
            File.WriteAllBytes(proxy, dll);
            var shaders = Path.Combine(dir, "reshade-shaders", "Shaders");
            Directory.CreateDirectory(shaders);
            File.Copy(Path.Combine(Source, "GaimeBorder.fx"), Path.Combine(shaders, "GaimeBorder.fx"), true);
            File.Copy(Path.Combine(Source, "ReShade.fxh"), Path.Combine(shaders, "ReShade.fxh"), true);
            File.Copy(Path.Combine(Source, "ReShade.ini"), Path.Combine(dir, "ReShade.ini"), true);
            WritePreset(dir, SetupConfig.Load().BorderFit);
            try { File.Delete(Path.Combine(dir, "ReShade.log")); } catch { }
            return "installed: " + ScreenBorder.Pixels + " px border — " + t.Label;
        }

        // Back to stock: our files out, any original proxy DLL restored.
        public static string Uninstall(Target t)
        {
            if (t?.Dir == null || !Directory.Exists(t.Dir)) return "nothing to remove";
            var proxy = Path.Combine(t.Dir, t.Proxy);
            var orig = Path.Combine(t.Dir, Orig(t));
            if (File.Exists(proxy) && !IsReshade(proxy) && !File.Exists(orig))
                return t.Proxy + " there is not ours — left alone";
            if (IsReshade(proxy)) File.Delete(proxy);
            try { File.Delete(Path.Combine(t.Dir, Off(t))); } catch { }
            if (File.Exists(orig) && !File.Exists(proxy)) File.Move(orig, proxy);
            foreach (var f in new[] { "ReShade.ini", "ReShadePreset.ini", "ReShade.log" })
                try { File.Delete(Path.Combine(t.Dir, f)); } catch { }
            var shaders = Path.Combine(t.Dir, "reshade-shaders");
            if (File.Exists(Path.Combine(shaders, "Shaders", "GaimeBorder.fx"))) Directory.Delete(shaders, true);
            return "removed" + (File.Exists(proxy) ? " (original " + t.Proxy + " restored)" : "");
        }

        // ---- preset: thickness for this screen, fit or overlay -----------------

        // The screen the border is sized for (the primary display).
        public static Func<(int w, int h)> ScreenSize = () =>
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            return (b.Width, b.Height);
        };

        // The preset carries the thickness as a fraction of the image, so it is
        // ScreenBorder.Pixels screen pixels whatever the game renders at, plus
        // FIT (shrink the picture inside the frame). Rewritten when the screen
        // or the setting changes.
        static void WritePreset(string dir, bool fit)
        {
            var (w, h) = ScreenSize();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string text = File.ReadAllText(Path.Combine(Source, "ReShadePreset.ini"));
            text = Regex.Replace(text, @"(?m)^BORDER_FX=.*$", "BORDER_FX=" + (ScreenBorder.Pixels / (double)Math.Max(1, w)).ToString("0.000000", inv));
            text = Regex.Replace(text, @"(?m)^BORDER_FY=.*$", "BORDER_FY=" + (ScreenBorder.Pixels / (double)Math.Max(1, h)).ToString("0.000000", inv));
            text = Regex.Replace(text, @"(?m)^FIT=.*$", "FIT=" + (fit ? "1" : "0"));
            File.WriteAllText(Path.Combine(dir, "ReShadePreset.ini"), text);
        }

        // Bring every installed game's preset up to date (screen size, fit) and
        // return the processes whose picture is shrunk inside the border, for
        // PicoOutput.SetInsetGames. Called at tray start and after any change.
        public static List<string> Refresh(string tpDir)
        {
            bool fit = SetupConfig.Load().BorderFit;
            var inset = new List<string>();
            foreach (var t in AllTargets(tpDir))
            {
                var st = Get(t);
                if (st != State.On && st != State.Off) continue;
                try
                {
                    // An install from an older tray has the older shader.
                    var shaders = Path.Combine(t.Dir, "reshade-shaders", "Shaders");
                    foreach (var f in new[] { "GaimeBorder.fx", "ReShade.fxh" })
                    {
                        var src = Path.Combine(Source, f);
                        var dst = Path.Combine(shaders, f);
                        if (File.Exists(src) && Directory.Exists(shaders) &&
                            (!File.Exists(dst) || !File.ReadAllBytes(src).AsSpan().SequenceEqual(File.ReadAllBytes(dst))))
                            File.Copy(src, dst, true);
                    }
                    WritePreset(t.Dir, fit);
                }
                catch { }
                if (st == State.On && fit && t.Process != null) inset.Add(t.Process);
            }
            return inset;
        }

        // Refresh the presets and tell the bridge which games are shrunk.
        public static void Apply(string tpDir)
        {
            try { PicoOutput.SetInsetGames(Refresh(tpDir)); } catch { }
        }

        // ---- every game at once (the border setting) --------------------------

        // One target per install location: all ElfLdr2 games share one.
        public static List<Target> AllTargets(string tpDir) =>
            TeknoParrotProfiles.Scan(tpDir).Select(p => For(tpDir, p.File))
                .Where(t => t.Problem == null).GroupBy(t => t.Key).Select(g => g.First()).ToList();

        // Follow the border setting in every installed game. Not-installed
        // games are left alone. Returns one line per game that changed or failed.
        public static List<string> SetAll(string tpDir, bool on)
        {
            var lines = new List<string>();
            foreach (var t in AllTargets(tpDir))
            {
                var st = Get(t);
                if (st != State.On && st != State.Off) continue;
                if ((st == State.On) == on) continue;
                try { SetEnabled(t, on); lines.Add(t.Label + ": " + (on ? "on" : "off")); }
                catch (Exception ex) { lines.Add(t.Label + ": NOT switched — " + ex.Message); }
            }
            return lines;
        }
    }
}
