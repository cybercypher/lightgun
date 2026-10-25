using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace GaimeFeeder
{
    // Stable gun identity via the on-device system_info.json serial number
    // (func 7 get_info "sn"), written once per gun with gun_set.ps1.
    // Survives reboots, replugs and port swaps; USB serials do not.
    static class HidIdentity
    {
        static readonly Guid HidGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
        const uint PRESENT_IFACE = 0x02 | 0x10;
        const uint RW = 0x80000000 | 0x40000000, SHARE = 3, OPEN = 3, OVERLAPPED_FLAG = 0x40000000;

        [StructLayout(LayoutKind.Sequential)] struct DEVINFO { public int Sz; public Guid G; public int DevInst; public IntPtr R; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct IFACE { public int Sz; public Guid G; public int F; public IntPtr R; }
        [StructLayout(LayoutKind.Sequential)] struct OV { public IntPtr A, B; public int C, D; public IntPtr HEvent; }

        [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, string e, IntPtr h, uint f);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref IFACE f);
        [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref IFACE f, IntPtr det, int sz, ref int need, ref DEVINFO dev);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("cfgmgr32.dll")] static extern int CM_Get_Parent(out int parent, int child, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Auto)]
        static extern int CM_Get_Device_ID(int devInst, StringBuilder buf, int len, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr CreateFile(string f, uint a, uint s, IntPtr sec, uint c, uint fl, IntPtr t);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(IntPtr h, byte[] b, uint n, ref uint w, ref OV o);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(IntPtr h, byte[] b, uint n, ref uint r, ref OV o);
        [DllImport("kernel32.dll")] static extern IntPtr CreateEvent(IntPtr a, bool m, bool i, string n);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetOverlappedResult(IntPtr h, ref OV o, ref uint n, bool w);
        [DllImport("kernel32.dll")] static extern bool CancelIo(IntPtr h);

        static Dictionary<string, string> _suffixToSn;
        static Dictionary<string, string> _snToVendor;

        public static List<string> EnumerateGunSns()
        {
            _suffixToSn = BuildMap();
            var sns = new List<string>(new HashSet<string>(_suffixToSn.Values));
            sns.Sort();
            return sns;
        }

        public static Dictionary<string, string> PathSuffixToSn() =>
            _suffixToSn ?? (_suffixToSn = BuildMap());

        // The MI_02 vendor interface for one gun's system_info sn. Rebuilds the
        // map first so a replug that moved the interface is picked up; returns
        // null when the gun is not present or its sn could not be read.
        public static string VendorPathForSn(string sn)
        {
            if (string.IsNullOrEmpty(sn)) return null;
            _suffixToSn = BuildMap();
            return _snToVendor.TryGetValue(sn, out var path) ? path : null;
        }

        static Dictionary<string, string> BuildMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // sn -> MI_02 path, refreshed on every build so callers never get a
            // stale path after a replug.
            var snToVendor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _snToVendor = snToVendor;
            // suffix -> composite key, composite key -> MI_02 path
            var suffixToComposite = new Dictionary<string, string>();
            var compositeToVendor = new Dictionary<string, string>();
            Guid g = HidGuid;
            IntPtr set = SetupDiGetClassDevs(ref g, null, IntPtr.Zero, PRESENT_IFACE);
            if (set.ToInt64() == -1) return map;
            try
            {
                for (int i = 0; ; i++)
                {
                    var fd = new IFACE { Sz = Marshal.SizeOf(typeof(IFACE)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref fd)) break;
                    int need = 0;
                    var dev = new DEVINFO { Sz = Marshal.SizeOf(typeof(DEVINFO)) };
                    SetupDiGetDeviceInterfaceDetail(set, ref fd, IntPtr.Zero, 0, ref need, ref dev);
                    IntPtr mem = Marshal.AllocHGlobal(need);
                    try
                    {
                        Marshal.WriteInt32(mem, IntPtr.Size == 8 ? 8 : 6);
                        dev = new DEVINFO { Sz = Marshal.SizeOf(typeof(DEVINFO)) };
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref fd, mem, need, ref need, ref dev)) continue;
                        string path = Marshal.PtrToStringAuto(new IntPtr(mem.ToInt64() + 4));
                        if (path.IndexOf("vid_2e2c&pid_0631", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string suffix = SuffixOf(path);
                        string composite = GrandparentId(dev.DevInst);
                        if (suffix == null || composite == null) continue;
                        suffixToComposite[suffix] = composite;
                        if (path.IndexOf("mi_02", StringComparison.OrdinalIgnoreCase) >= 0)
                            compositeToVendor[composite] = path;
                    }
                    finally { Marshal.FreeHGlobal(mem); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            var snByComposite = new Dictionary<string, string>();
            foreach (var kv in compositeToVendor)
            {
                string sn = QuerySn(kv.Value);
                if (sn == null) continue;
                snByComposite[kv.Key] = sn;
                snToVendor[sn] = kv.Value;
            }
            foreach (var kv in suffixToComposite)
                if (snByComposite.TryGetValue(kv.Value, out var sn)) map[kv.Key] = sn;
            return map;
        }

        static string SuffixOf(string hidPath)
        {
            var m = Regex.Match(hidPath, @"MI_0\d#(.*?)#", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        static string GrandparentId(int devInst)
        {
            if (CM_Get_Parent(out int p1, devInst, 0) != 0) return null;
            if (CM_Get_Parent(out int p2, p1, 0) != 0) return null;
            var sb = new StringBuilder(256);
            return CM_Get_Device_ID(p2, sb, sb.Capacity, 0) == 0 ? sb.ToString() : null;
        }

        static ushort Crc(byte[] b, int n)
        {
            ushort c = 0xFFFF;
            for (int i = 0; i < n; i++)
            {
                c ^= b[i];
                for (int k = 0; k < 8; k++)
                    c = (ushort)(((c & 1) != 0) ? ((c >> 1) ^ 0xA001) : (c >> 1));
            }
            return c;
        }

        static string QuerySn(string vendorPath)
        {
            try
            {
                byte[] pl = Encoding.UTF8.GetBytes("sn");
                var b = new List<byte> { 7, (byte)pl.Length };
                b.AddRange(BitConverter.GetBytes(0u));
                b.AddRange(pl);
                var a = b.ToArray();
                b.AddRange(BitConverter.GetBytes(Crc(a, a.Length)));
                while (b.Count < 64) b.Add(0);
                byte[] pkt = new byte[65];
                Array.Copy(b.ToArray(), 0, pkt, 1, 64);
                if (!SendPacket(vendorPath, pkt, out byte[] buf, out int r, out _)) return null;
                if (r < 10) return null;
                int len = buf[2];
                string s = Encoding.UTF8.GetString(buf, 7, Math.Min(len, buf.Length - 7));
                // payload looks like [{"num":"11111"}] — extract the value manually
                // (avoids regex quoting pitfalls).
                int ni = s.IndexOf("\"num\"");
                if (ni < 0) return null;
                int c1 = s.IndexOf('"', ni + 5);
                if (c1 < 0) return null;
                int c2 = s.IndexOf('"', c1 + 1);
                if (c2 < 0) return null;
                return s.Substring(c1 + 1, c2 - c1 - 1);
            }
            catch { return null; }
        }

        // One 65-byte vendor report out and one reply back (report ID 0 + 64
        // bytes, Modbus CRC16 over bytes 0..n-1 in the last two used bytes).
        static bool SendPacket(string vendorPath, byte[] pkt, out byte[] reply,
                               out int replyLen, out string error)
        {
            reply = new byte[65];
            replyLen = 0;
            error = null;
            IntPtr h = CreateFile(vendorPath, RW, SHARE, IntPtr.Zero, OPEN, OVERLAPPED_FLAG, IntPtr.Zero);
            if (h.ToInt64() == -1) { error = "CreateFile failed"; return false; }
            try
            {
                var o = new OV { HEvent = CreateEvent(IntPtr.Zero, true, false, null) };
                try
                {
                    uint w = 0;
                    if (!WriteFile(h, pkt, 65, ref w, ref o))
                    {
                        if (Marshal.GetLastWin32Error() != 997) { error = "WriteFile failed"; return false; }
                    }
                    if (WaitForSingleObject(o.HEvent, 1000) != 0) { CancelIo(h); error = "write timeout"; return false; }
                    GetOverlappedResult(h, ref o, ref w, false);
                    if (w != 65) { error = "short write"; return false; }
                }
                finally { CloseHandle(o.HEvent); }
                var o2 = new OV { HEvent = CreateEvent(IntPtr.Zero, true, false, null) };
                try
                {
                    uint r = 0;
                    if (!ReadFile(h, reply, 65, ref r, ref o2))
                    {
                        if (Marshal.GetLastWin32Error() != 997) { error = "ReadFile failed"; return false; }
                    }
                    if (WaitForSingleObject(o2.HEvent, 1500) != 0) { CancelIo(h); error = "no ack"; return false; }
                    GetOverlappedResult(h, ref o2, ref r, false);
                    replyLen = (int)r;
                    return r > 0;
                }
                finally { CloseHandle(o2.HEvent); }
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally { CloseHandle(h); }
        }

        // func 6 set_info: payload {"path":"sn.num","value":"<sn>"} — the same
        // write tools/gun_set.ps1 makes. The gun stores it in system_info.json
        // and applies it immediately; replug so the tray re-identifies it.
        public static bool SetSn(string vendorPath, string value, out string ack, out string error)
        {
            ack = null; error = null;
            if (string.IsNullOrWhiteSpace(value)) { error = "empty serial"; return false; }
            byte[] pl = Encoding.UTF8.GetBytes("{\"path\":\"sn.num\",\"value\":\"" + value + "\"}");
            if (pl.Length > 60) { error = "serial too long"; return false; }
            var b = new List<byte> { 6, (byte)pl.Length };
            b.AddRange(BitConverter.GetBytes(0u));
            b.AddRange(pl);
            var a = b.ToArray();
            b.AddRange(BitConverter.GetBytes(Crc(a, a.Length)));
            while (b.Count < 64) b.Add(0);
            byte[] pkt = new byte[65];
            Array.Copy(b.ToArray(), 0, pkt, 1, 64);
            if (!SendPacket(vendorPath, pkt, out byte[] buf, out int r, out error)) return false;
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(r, 8); i++) sb.Append(buf[i].ToString("X2")).Append(' ');
            ack = r + " bytes: " + sb.ToString().Trim();
            return true;
        }

        // Exactly one vendor interface is present, i.e. exactly one gun is
        // plugged in — the condition gun_set.ps1 insists on before writing.
        // Counts interfaces, not serials: two guns with the same serial are
        // still two guns.
        public static string SingleVendorPath(out int count)
        {
            var paths = VendorPaths();
            count = paths.Count;
            return count == 1 ? paths[0] : null;
        }

        // Every MI_02 vendor interface currently present (no serial query).
        public static List<string> VendorPaths()
        {
            var paths = new List<string>();
            Guid g = HidGuid;
            IntPtr set = SetupDiGetClassDevs(ref g, null, IntPtr.Zero, PRESENT_IFACE);
            if (set.ToInt64() == -1) return paths;
            try
            {
                for (int i = 0; ; i++)
                {
                    var fd = new IFACE { Sz = Marshal.SizeOf(typeof(IFACE)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref fd)) break;
                    int need = 0;
                    var dev = new DEVINFO { Sz = Marshal.SizeOf(typeof(DEVINFO)) };
                    SetupDiGetDeviceInterfaceDetail(set, ref fd, IntPtr.Zero, 0, ref need, ref dev);
                    IntPtr mem = Marshal.AllocHGlobal(need);
                    try
                    {
                        Marshal.WriteInt32(mem, IntPtr.Size == 8 ? 8 : 6);
                        dev = new DEVINFO { Sz = Marshal.SizeOf(typeof(DEVINFO)) };
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref fd, mem, need, ref need, ref dev)) continue;
                        string path = Marshal.PtrToStringAuto(new IntPtr(mem.ToInt64() + 4));
                        if (path.IndexOf("vid_2e2c&pid_0631", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (path.IndexOf("mi_02", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (!paths.Contains(path)) paths.Add(path);
                    }
                    finally { Marshal.FreeHGlobal(mem); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return paths;
        }

        public static string QuerySnOnce(string vendorPath) => QuerySn(vendorPath);
    }
}
