param([int]$Seconds = 12)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$code = @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
public class RawCap {
  const int WM_INPUT = 0x00FF;
  const uint RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007;
  const uint RIDEV_INPUTSINK = 0x00000100;
  [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort Page; public ushort Usage; public uint Flags; public IntPtr Hwnd; }
  [StructLayout(LayoutKind.Sequential)] struct RAWINPUTHEADER { public uint Type; public uint Size; public IntPtr Dev; public IntPtr Param; }
  [StructLayout(LayoutKind.Sequential)] struct RAWHID { public uint Count; public uint SizeHid; }
  [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint s);
  [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hsize);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, StringBuilder b, ref uint s);
  public class CapForm : Form {
    public List<string> lines = new List<string>();
    Dictionary<string,string> names = new Dictionary<string,string>();
    DateTime t0 = DateTime.UtcNow;
    public CapForm(int secs) {
      this.ShowInTaskbar = false; this.Opacity = 0; this.ShowIcon = false;
      this.Load += (s,e) => {
        var devs = new RAWINPUTDEVICE[] {
          new RAWINPUTDEVICE { Page=0x0D, Usage=0x04, Flags=RIDEV_INPUTSINK, Hwnd=this.Handle },
          new RAWINPUTDEVICE { Page=0x01, Usage=0x06, Flags=RIDEV_INPUTSINK, Hwnd=this.Handle },
          new RAWINPUTDEVICE { Page=0x01, Usage=0x02, Flags=RIDEV_INPUTSINK, Hwnd=this.Handle },
        };
        if (!RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
          lines.Add("RegisterRawInputDevices FAILED err=" + Marshal.GetLastWin32Error());
        else lines.Add("listening: digitizer(0D/04) + keyboard(01/06), INPUTSINK");
        var t = new System.Windows.Forms.Timer(); t.Interval = secs * 1000;
        t.Tick += (s2,e2) => { t.Stop(); this.Close(); };
        t.Start();
      };
    }
    string DevName(IntPtr h) {
      string k = h.ToString();
      if (names.ContainsKey(k)) return names[k];
      uint s = 0; GetRawInputDeviceInfo(h, RIDI_DEVICENAME, null, ref s);
      var sb = new StringBuilder((int)s + 8);
      GetRawInputDeviceInfo(h, RIDI_DEVICENAME, sb, ref s);
      string n = sb.ToString(); names[k] = n; return n;
    }
    protected override void WndProc(ref Message m) {
      if (m.Msg == WM_INPUT) {
        uint sz = 0; uint hsz = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
        GetRawInputData(m.LParam, RID_INPUT, IntPtr.Zero, ref sz, hsz);
        if (sz > 0) {
          IntPtr buf = Marshal.AllocHGlobal((int)sz);
          try {
            if (GetRawInputData(m.LParam, RID_INPUT, buf, ref sz, hsz) == sz) {
              var hdr = (RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(RAWINPUTHEADER));
              if (hdr.Type == 2) {
                IntPtr ph = new IntPtr(buf.ToInt64() + hsz);
                var hid = (RAWHID)Marshal.PtrToStructure(ph, typeof(RAWHID));
                int nb = (int)(hid.Count * hid.SizeHid);
                byte[] raw = new byte[nb];
                Marshal.Copy(new IntPtr(ph.ToInt64() + 8), raw, 0, nb);
                string dn = DevName(hdr.Dev);
                string el = ((DateTime.UtcNow - t0).TotalSeconds).ToString("F3");
                string hex = BitConverter.ToString(raw).Replace("-", "");
                string extra = "";
                if (nb >= 6 && raw[0] == 1) {
                  int fl = raw[1]; int x = raw[2] | (raw[3] << 8); int y = raw[4] | (raw[5] << 8);
                  extra = string.Format(" tip={0} in_range={1} X={2} Y={3}", fl & 1, (fl >> 1) & 1, x, y);
                }
                lock (lines) lines.Add(string.Format("{0}s h={1} {2} {3}{4}", el, hdr.Dev.ToInt64().ToString("X"), dn, hex, extra));
              } else if (hdr.Type == 1) {
                int mk = Marshal.ReadInt16(buf, (int)hsz);
                int vkey = Marshal.ReadInt16(buf, (int)hsz + 6);
                int msg = Marshal.ReadInt32(buf, (int)hsz + 8);
                string dn = DevName(hdr.Dev);
                string el = ((DateTime.UtcNow - t0).TotalSeconds).ToString("F3");
                lock (lines) lines.Add(string.Format("{0}s h={1} {2} KEY vk=0x{3:X} make=0x{4:X} msg={5}", el, hdr.Dev.ToInt64().ToString("X"), dn, vkey, mk, msg));
              } else if (hdr.Type == 0) {
                int btn = (int)(Marshal.ReadInt16(buf, (int)hsz + 4) & 0xFFFF);
                int dx = Marshal.ReadInt32(buf, (int)hsz + 12);
                int dy = Marshal.ReadInt32(buf, (int)hsz + 16);
                string dn = DevName(hdr.Dev);
                string el = ((DateTime.UtcNow - t0).TotalSeconds).ToString("F3");
                lock (lines) lines.Add(string.Format("{0}s h={1} {2} MOUSE btn=0x{3:X} dx={4} dy={5}", el, hdr.Dev.ToInt64().ToString("X"), dn, btn, dx, dy));
              }
            }
          } finally { Marshal.FreeHGlobal(buf); }
        }
      }
      base.WndProc(ref m);
    }
  }
  public static List<string> Capture(int secs) {
    List<string> res = null;
    var t = new Thread(() => { using (var f = new CapForm(secs)) { Application.Run(f); res = f.lines; } });
    t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
    t.Join((secs + 10) * 1000);
    return res ?? new List<string>();
  }
}
"@
Add-Type -TypeDefinition $code -ReferencedAssemblies System.Windows.Forms,System.Drawing
Write-Output "=== capture ${Seconds}s: AIM AT LIT DISPLAY, move slowly, pull trigger, press A/B/Coin/Start ==="
$lines = [RawCap]::Capture($Seconds)
Write-Output ("=== got " + $lines.Count + " lines ===")
foreach ($l in $lines) { Write-Output $l }
