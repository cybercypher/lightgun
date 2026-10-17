param([string[]]$Keys = @("sn", "fw.ver", "model.ver"))
Add-Type -TypeDefinition @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public class Q {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Auto)] public struct IFACE { public int Sz; public Guid G; public int F; public IntPtr R; }
  [StructLayout(LayoutKind.Sequential)] public struct OVERLAPPED { public IntPtr a; public IntPtr b; public int c; public int d; public IntPtr hEvent; }
  [DllImport("setupapi.dll", CharSet=CharSet.Auto)] public static extern IntPtr SetupDiGetClassDevs(ref Guid g, string e, IntPtr h, uint f);
  [DllImport("setupapi.dll")] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref IFACE f);
  [DllImport("setupapi.dll", CharSet=CharSet.Auto)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref IFACE f, IntPtr det, int sz, ref int need, IntPtr d);
  [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
  [DllImport("kernel32.dll", CharSet=CharSet.Auto, SetLastError=true)] public static extern IntPtr CreateFile(string f, uint a, uint s, IntPtr sec, uint c, uint fl, IntPtr t);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool WriteFile(IntPtr h, byte[] b, uint n, ref uint w, ref OVERLAPPED o);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadFile(IntPtr h, byte[] b, uint n, ref uint r, ref OVERLAPPED o);
  [DllImport("kernel32.dll")] public static extern IntPtr CreateEvent(IntPtr a, bool m, bool i, string n);
  [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetOverlappedResult(IntPtr h, ref OVERLAPPED o, ref uint n, bool w);
  [DllImport("kernel32.dll")] public static extern bool CancelIo(IntPtr h);
  const uint OV = 0x40000000;
  public static List<string> VendorPaths() {
    var o = new List<string>(); Guid g = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
    IntPtr set = SetupDiGetClassDevs(ref g, null, IntPtr.Zero, 0x12);
    for (int i = 0; ; i++) {
      IFACE fd = new IFACE(); fd.Sz = Marshal.SizeOf(typeof(IFACE));
      if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref fd)) break;
      int need = 0; SetupDiGetDeviceInterfaceDetail(set, ref fd, IntPtr.Zero, 0, ref need, IntPtr.Zero);
      IntPtr mem = Marshal.AllocHGlobal(need);
      try {
        Marshal.WriteInt32(mem, 8);
        if (!SetupDiGetDeviceInterfaceDetail(set, ref fd, mem, need, ref need, IntPtr.Zero)) continue;
        string p = Marshal.PtrToStringAuto(new IntPtr(mem.ToInt64() + 4));
        if (p != null && p.ToLower().IndexOf("vid_2e2c") >= 0 && p.ToLower().IndexOf("mi_02") >= 0) o.Add(p);
      } finally { Marshal.FreeHGlobal(mem); }
    }
    SetupDiDestroyDeviceInfoList(set); return o;
  }
  static ushort Crc(byte[] b, int n) {
    ushort c = 0xFFFF;
    for (int i = 0; i < n; i++) { c ^= b[i]; for (int k = 0; k < 8; k++) c = (ushort)(((c & 1) != 0) ? ((c >> 1) ^ 0xA001) : (c >> 1)); }
    return c;
  }
  static byte[] Frame(byte func, uint index, byte[] payload) {
    var b = new List<byte>(); b.Add(func); b.Add((byte)payload.Length);
    b.AddRange(BitConverter.GetBytes(index)); b.AddRange(payload);
    var a = b.ToArray(); ushort c = Crc(a, a.Length);
    b.AddRange(BitConverter.GetBytes(c));
    while (b.Count < 64) b.Add(0);
    return b.ToArray();
  }
  static byte[] Io(IntPtr h, byte[] report) {
    byte[] pkt = new byte[65]; Array.Copy(report, 0, pkt, 1, 64);
    OVERLAPPED o = new OVERLAPPED(); o.hEvent = CreateEvent(IntPtr.Zero, true, false, null);
    try {
      uint w = 0;
      if (!WriteFile(h, pkt, 65, ref w, ref o)) { if (Marshal.GetLastWin32Error() != 997) return null; }
      if (WaitForSingleObject(o.hEvent, 1000) != 0) { CancelIo(h); return null; }
      GetOverlappedResult(h, ref o, ref w, false);
      byte[] buf = new byte[65];
      o = new OVERLAPPED(); o.hEvent = CreateEvent(IntPtr.Zero, true, false, null);
      try {
        uint r = 0;
        if (!ReadFile(h, buf, 65, ref r, ref o)) { if (Marshal.GetLastWin32Error() != 997) return null; }
        if (WaitForSingleObject(o.hEvent, 1000) != 0) { CancelIo(h); return null; }
        GetOverlappedResult(h, ref o, ref r, false);
        if (r < 8) return null;
        byte[] outp = new byte[r]; Array.Copy(buf, outp, r); return outp;
      } finally { CloseHandle(o.hEvent); }
    } finally { CloseHandle(o.hEvent); }
  }
  public static string Query(string path, byte func, uint index, string key) {
    IntPtr h = CreateFile(path, 3221225472u, 3u, IntPtr.Zero, 3u, OV, IntPtr.Zero);
    if (h.ToInt64() == -1) return "<open failed>";
    try {
      byte[] pl = string.IsNullOrEmpty(key) ? new byte[0] : Encoding.UTF8.GetBytes(key);
      byte[] r = Io(h, Frame(func, index, pl));
      if (r == null) return "<no reply>";
      int len = r[2];
      string s = Encoding.UTF8.GetString(r, 7, Math.Min(len, r.Length - 7));
      return s.TrimEnd('\0');
    } finally { CloseHandle(h); }
  }
}
"@
