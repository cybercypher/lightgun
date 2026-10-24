# Phase 0 probe: ViGEm pad HID identity and path stability.
#
# Creates 1 or 2 ViGEm Xbox360 pads (no guns needed), records the present HID
# interface paths and their usage page/usage/report caps, repeats for N cycles.
# Used to prove that DemulShooter's RawInput HID path can consume the pads and
# that the interface paths are stable enough to bind P1/P2 in config.ini.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Probe-ViGEm.ps1
#   powershell ... -File Probe-ViGEm.ps1 -Pads 2 -Cycles 3

param(
  [ValidateRange(1, 2)][int]$Pads = 1,
  [int]$Cycles = 1,
  [string]$OutDir = (Join-Path $PSScriptRoot 'out'),
  [string]$ViGEmDll = 'C:\games\feeder\Nefarius.ViGEm.Client.dll'
)
$ErrorActionPreference = 'Stop'
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class Phase0Vigem {
  [StructLayout(LayoutKind.Sequential)] public struct ATTR { public int Size; public ushort Vid; public ushort Pid; public ushort Ver; }
  [StructLayout(LayoutKind.Sequential)] public struct CAPS {
    public ushort Usage; public ushort UsagePage; public ushort InputReportByteLength; public ushort OutputReportByteLength;
    public ushort FeatureReportByteLength; [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
    public ushort NumberLinkCollectionNodes; public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps;
    public ushort NumberInputDataIndices; public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps;
    public ushort NumberOutputDataIndices; public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps;
    public ushort NumberFeatureDataIndices;
  }
  [StructLayout(LayoutKind.Sequential)] public struct IFACE { public int Sz; public Guid G; public int F; public IntPtr R; }
  [DllImport("setupapi.dll", CharSet=CharSet.Auto)] public static extern IntPtr SetupDiGetClassDevs(ref Guid g, string e, IntPtr h, uint f);
  [DllImport("setupapi.dll")] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref IFACE f);
  [DllImport("setupapi.dll", CharSet=CharSet.Auto)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref IFACE f, IntPtr det, int sz, ref int need, IntPtr d);
  [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
  [DllImport("kernel32.dll", CharSet=CharSet.Auto, SetLastError=true)] public static extern IntPtr CreateFile(string f, uint a, uint s, IntPtr sec, uint c, uint fl, IntPtr t);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(IntPtr h, ref ATTR a);
  [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr p);
  [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
  [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr p, ref CAPS c);
  public static List<string> Caps() {
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
        if (p == null || p.ToLower().IndexOf("vid_045e&pid_028e") < 0) continue;
        IntPtr h = CreateFile(p, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.ToInt64() == -1) { o.Add(p + " | OPEN_FAILED " + Marshal.GetLastWin32Error()); continue; }
        try {
          ATTR a = new ATTR(); a.Size = Marshal.SizeOf(typeof(ATTR)); HidD_GetAttributes(h, ref a);
          IntPtr pp; CAPS c = new CAPS(); string caps = "NO_CAPS";
          if (HidD_GetPreparsedData(h, out pp)) {
            HidP_GetCaps(pp, ref c); HidD_FreePreparsedData(pp);
            caps = string.Format("UP=0x{0:X2} U=0x{1:X2} inLen={2} btnCaps={3} valCaps={4}",
              c.UsagePage, c.Usage, c.InputReportByteLength, c.NumberInputButtonCaps, c.NumberInputValueCaps);
          }
          o.Add(string.Format("{0} | VID=0x{1:X4} PID=0x{2:X4} | {3}", p, a.Vid, a.Pid, caps));
        } finally { CloseHandle(h); }
      } finally { Marshal.FreeHGlobal(mem); }
    }
    SetupDiDestroyDeviceInfoList(set); return o;
  }
}
"@
Add-Type -Path $ViGEmDll

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$log = Join-Path $OutDir 'vigem-report.txt'
"# ViGEm probe $(Get-Date -Format o) pads=$Pads cycles=$Cycles" | Set-Content -Encoding UTF8 $log
function L($m) { $m | Out-File -Append -Encoding UTF8 $log; Write-Output $m }
function Short([string]$p) { if ($p -match 'ig_(\d+)#([^#]+)#') { "IG_$($matches[1]) # $($matches[2])" } else { $p } }

for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
  $clientList = @(); $padList = @()
  for ($n = 1; $n -le $Pads; $n++) {
    $client = New-Object Nefarius.ViGEm.Client.ViGEmClient
    $pad = $client.CreateXbox360Controller()
    $pad.AutoSubmitReport = $false
    $pad.Connect()
    $clientList += $client; $padList += $pad
  }
  Start-Sleep -Milliseconds 1200
  L "cycle $cycle : $Pads pad(s)"
  foreach ($line in [Phase0Vigem]::Caps()) { L ("   " + $line) }
  foreach ($pad in $padList) { $pad.Disconnect() }
  foreach ($client in $clientList) { $client.Dispose() }
  Start-Sleep -Milliseconds 1500
}
L "report=$log"
