param([string]$Value = "11111")
# Write system_info.json sn.num via func 6 set_info (index 0).
# Format from decompiled handle_set_info + parse_single_object + merge_json_by_path:
#   payload = {"path":"sn.num","value":"<sn>"} (must parse as JSON, path must be
#   a string, value key required; anything else is rejected WITHOUT writing).
# REFUSES unless exactly one vendor interface is present.
. "$(Split-Path -Parent $MyInvocation.MyCommand.Path)/gun_q_lib.ps1"
$paths = [Q]::VendorPaths()
Write-Output ("vendor interfaces: " + $paths.Count)
if ($paths.Count -ne 1) { Write-Output "REFUSING: need exactly 1 gun plugged in"; exit 1 }
$p = $paths[0]
Write-Output ("target: " + $p)
Write-Output ("pre  sn = [" + [Q]::Query($p, 7, 0, "sn") + "]")
$payload = [Text.Encoding]::UTF8.GetBytes(('{"path":"sn.num","value":"' + $Value + '"}'))
# raw send with ack dump (Query() parses as get-reply; here show raw ack bytes instead)
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public class W {
  [DllImport("kernel32.dll", CharSet=CharSet.Auto, SetLastError=true)] public static extern IntPtr CreateFile(string f, uint a, uint s, IntPtr sec, uint c, uint fl, IntPtr t);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
}
"@ | Out-Null
Write-Output "sending set_info..."
# reuse Q.Io? not exposed; do it inline via a second Add-Type is overkill:
# temporarily expose: call Query with func 6 through a tweaked lib is wrong shape.
# Fallback: use gun_q_lib's internals via reflection is messy. Instead inline C# here:
Add-Type -TypeDefinition @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public class S6 {
  [StructLayout(LayoutKind.Sequential)] public struct OV { public IntPtr a; public IntPtr b; public int c; public int d; public IntPtr hEvent; }
  [DllImport("kernel32.dll", CharSet=CharSet.Auto, SetLastError=true)] public static extern IntPtr CreateFile(string f, uint a, uint s, IntPtr sec, uint c, uint fl, IntPtr t);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool WriteFile(IntPtr h, byte[] b, uint n, ref uint w, ref OV o);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadFile(IntPtr h, byte[] b, uint n, ref uint r, ref OV o);
  [DllImport("kernel32.dll")] public static extern IntPtr CreateEvent(IntPtr a, bool m, bool i, string n);
  [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetOverlappedResult(IntPtr h, ref OV o, ref uint n, bool w);
  [DllImport("kernel32.dll")] public static extern bool CancelIo(IntPtr h);
  static ushort Crc(byte[] b, int n) {
    ushort c = 0xFFFF;
    for (int i = 0; i < n; i++) { c ^= b[i]; for (int k = 0; k < 8; k++) c = (ushort)(((c & 1) != 0) ? ((c >> 1) ^ 0xA001) : (c >> 1)); }
    return c;
  }
  public static string SetSn(string path, string value) {
    string json = "{\"path\":\"sn.num\",\"value\":\"" + value + "\"}";
    byte[] pl = Encoding.UTF8.GetBytes(json);
    var b = new List<byte>(); b.Add(6); b.Add((byte)pl.Length);
    b.AddRange(BitConverter.GetBytes(0u)); b.AddRange(pl);
    var a = b.ToArray(); b.AddRange(BitConverter.GetBytes(Crc(a, a.Length)));
    while (b.Count < 64) b.Add(0);
    byte[] pkt = new byte[65]; Array.Copy(b.ToArray(), 0, pkt, 1, 64);
    IntPtr h = CreateFile(path, 3221225472u, 3u, IntPtr.Zero, 3u, 0x40000000u, IntPtr.Zero);
    if (h.ToInt64() == -1) return "<open failed>";
    try {
      OV o = new OV(); o.hEvent = CreateEvent(IntPtr.Zero, true, false, null);
      try {
        uint w = 0;
        if (!WriteFile(h, pkt, 65, ref w, ref o)) { if (Marshal.GetLastWin32Error() != 997) return "<write failed>"; }
        if (WaitForSingleObject(o.hEvent, 1000) != 0) { CancelIo(h); return "<write timeout>"; }
        GetOverlappedResult(h, ref o, ref w, false);
        if (w != 65) return "<short write>";
      } finally { CloseHandle(o.hEvent); }
      byte[] buf = new byte[65];
      OV o2 = new OV(); o2.hEvent = CreateEvent(IntPtr.Zero, true, false, null);
      try {
        uint r = 0;
        if (!ReadFile(h, buf, 65, ref r, ref o2)) { if (Marshal.GetLastWin32Error() != 997) return "<read failed>"; }
        if (WaitForSingleObject(o2.hEvent, 1500) != 0) { CancelIo(h); return "<no ack>"; }
        GetOverlappedResult(h, ref o2, ref r, false);
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(r, 16); i++) sb.AppendFormat("{0:X2} ", buf[i]);
        return "ack bytes=" + r + ": " + sb.ToString();
      } finally { CloseHandle(o2.hEvent); }
    } finally { CloseHandle(h); }
  }
}
"@
Write-Output ([S6]::SetSn($p, $Value))
Write-Output ("post sn = [" + [Q]::Query($p, 7, 0, "sn") + "]")
Write-Output ("post fw.ver = [" + [Q]::Query($p, 7, 0, "fw.ver") + "]")
