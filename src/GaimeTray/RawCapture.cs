using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace GaimeTray
{
    // Timed RawInput capture (replaces tools/hid_log.ps1): digitizer +
    // keyboard + mouse, N seconds, returns decoded lines for saving/showing.
    static class RawCapture
    {
        public static List<string> Capture(int seconds)
        {
            List<string> res = null;
            var t = new Thread(() =>
            {
                using (var f = new CapForm(seconds)) { Application.Run(f); res = f.Lines; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join((seconds + 10) * 1000);
            return res ?? new List<string>();
        }

        sealed class CapForm : Form
        {
            public readonly List<string> Lines = new List<string>();
            readonly Dictionary<string, string> _names = new Dictionary<string, string>();
            readonly DateTime _t0 = DateTime.UtcNow;

            const int WM_INPUT = 0x00FF;
            const uint RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007;
            const uint SINK = 0x100;

            [StructLayout(LayoutKind.Sequential)]
            struct Dev { public ushort Page, Usage; public uint Flags; public IntPtr Hwnd; }
            [StructLayout(LayoutKind.Sequential)]
            struct Head { public uint Type, Size; public IntPtr Dev, Param; }
            [StructLayout(LayoutKind.Sequential)]
            struct Hid { public uint Count, SizeHid; }

            [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(Dev[] d, uint n, uint s);
            [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint c, IntPtr d, ref uint s, uint hsz);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            static extern uint GetRawInputDeviceInfo(IntPtr h, uint c, StringBuilder b, ref uint s);

            public CapForm(int secs)
            {
                ShowInTaskbar = false; Opacity = 0; ShowIcon = false;
                Load += (s, e) =>
                {
                    var devs = new[] {
                        new Dev { Page = 0x0D, Usage = 0x04, Flags = SINK, Hwnd = Handle },
                        new Dev { Page = 0x01, Usage = 0x06, Flags = SINK, Hwnd = Handle },
                        new Dev { Page = 0x01, Usage = 0x02, Flags = SINK, Hwnd = Handle },
                    };
                    if (!RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf(typeof(Dev))))
                        Lines.Add("RegisterRawInputDevices FAILED err=" + Marshal.GetLastWin32Error());
                    else Lines.Add("listening: digitizer(0D/04) + keyboard(01/06), INPUTSINK");
                    var t = new System.Windows.Forms.Timer { Interval = secs * 1000 };
                    t.Tick += (s2, e2) => { t.Stop(); Close(); };
                    t.Start();
                };
            }

            string NameOf(IntPtr h)
            {
                string k = h.ToString();
                if (_names.TryGetValue(k, out var n)) return n;
                uint s = 0;
                GetRawInputDeviceInfo(h, RIDI_DEVICENAME, null, ref s);
                var sb = new StringBuilder((int)s + 8);
                GetRawInputDeviceInfo(h, RIDI_DEVICENAME, sb, ref s);
                _names[k] = sb.ToString();
                return _names[k];
            }

            void Add(string l) { lock (Lines) Lines.Add(l); }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_INPUT)
                {
                    uint sz = 0, hsz = (uint)Marshal.SizeOf(typeof(Head));
                    GetRawInputData(m.LParam, RID_INPUT, IntPtr.Zero, ref sz, hsz);
                    if (sz > 0)
                    {
                        IntPtr buf = Marshal.AllocHGlobal((int)sz);
                        try
                        {
                            if (GetRawInputData(m.LParam, RID_INPUT, buf, ref sz, hsz) == sz)
                            {
                                var h = (Head)Marshal.PtrToStructure(buf, typeof(Head));
                                string el = (DateTime.UtcNow - _t0).TotalSeconds.ToString("F3");
                                string dn = NameOf(h.Dev), hx = h.Dev.ToInt64().ToString("X");
                                if (h.Type == 2)
                                {
                                    IntPtr ph = new IntPtr(buf.ToInt64() + hsz);
                                    var hid = (Hid)Marshal.PtrToStructure(ph, typeof(Hid));
                                    int nb = (int)(hid.Count * hid.SizeHid);
                                    var raw = new byte[nb];
                                    Marshal.Copy(new IntPtr(ph.ToInt64() + 8), raw, 0, nb);
                                    string ex = "";
                                    if (nb >= 6 && raw[0] == 1)
                                        ex = string.Format(" tip={0} in_range={1} X={2} Y={3}",
                                            raw[1] & 1, (raw[1] >> 1) & 1, raw[2] | (raw[3] << 8), raw[4] | (raw[5] << 8));
                                    Add(el + "s h=" + hx + " " + dn + " " +
                                        BitConverter.ToString(raw).Replace("-", "") + ex);
                                }
                                else if (h.Type == 1)
                                {
                                    int mk = Marshal.ReadInt16(buf, (int)hsz);
                                    int vk = Marshal.ReadInt16(buf, (int)hsz + 6);
                                    int msg = Marshal.ReadInt32(buf, (int)hsz + 8);
                                    Add(string.Format("{0}s h={1} {2} KEY vk=0x{3:X} make=0x{4:X} msg={5}",
                                        el, hx, dn, vk, mk, msg));
                                }
                                else if (h.Type == 0)
                                {
                                    int btn = Marshal.ReadInt16(buf, (int)hsz + 4) & 0xFFFF;
                                    int dx = Marshal.ReadInt32(buf, (int)hsz + 12);
                                    int dy = Marshal.ReadInt32(buf, (int)hsz + 16);
                                    Add(string.Format("{0}s h={1} {2} MOUSE btn=0x{3:X} dx={4} dy={5}",
                                        el, hx, dn, btn, dx, dy));
                                }
                            }
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                }
                base.WndProc(ref m);
            }
        }
    }
}
