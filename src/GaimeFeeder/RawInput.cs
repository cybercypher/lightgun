using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace GaimeFeeder
{
    // RawInput INPUTSINK capture for VID_2E2C/PID_0631 only.
    // Digitizer (0D/04) arrives as HID Type 2; gun buttons arrive as legacy
    // keyboard Type 1 (A=SPACE, B=B, Coin=C, Start=Q). Other vendors ignored.
    sealed class RawInputWindow : Form
    {
        const int WM_INPUT = 0x00FF;
        const uint RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007;
        const uint RIDEV_INPUTSINK = 0x100;

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE { public ushort Page; public ushort Usage; public uint Flags; public IntPtr Hwnd; }
        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTHEADER { public uint Type; public uint Size; public IntPtr Dev; public IntPtr Param; }
        [StructLayout(LayoutKind.Sequential)]
        struct RAWHID { public uint Count; public uint SizeHid; }

        [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint s);
        [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hsize);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, StringBuilder b, ref uint s);

        static readonly Regex GaimePath = new Regex(
            @"VID_2E2C&PID_0631&MI_0(\d)#", RegexOptions.IgnoreCase);

        readonly Dictionary<IntPtr, string> _serialByHandle = new Dictionary<IntPtr, string>();
        readonly Dictionary<string, IntPtr> _kbHandleBySerial = new Dictionary<string, IntPtr>();
        readonly Action<string, int, int, bool> _onPointer; // serial, x, y, tip
        readonly Action<string, int, bool> _onKey;          // serial, vkey, down

        public RawInputWindow(Action<string, int, int, bool> onPointer,
                              Action<string, int, bool> onKey)
        {
            _onPointer = onPointer; _onKey = onKey;
            ShowInTaskbar = false; Opacity = 0; ShowIcon = false;
            Load += (s, e) =>
            {
                RegisterRawInputDevices(new[]
                {
                    new RAWINPUTDEVICE { Page = 0x0D, Usage = 0x04, Flags = RIDEV_INPUTSINK, Hwnd = Handle },
                    new RAWINPUTDEVICE { Page = 0x01, Usage = 0x06, Flags = RIDEV_INPUTSINK, Hwnd = Handle },
                    new RAWINPUTDEVICE { Page = 0x01, Usage = 0x02, Flags = RIDEV_INPUTSINK, Hwnd = Handle },
                }, 3, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            };
        }

        string DeviceName(IntPtr h)
        {
            uint s = 0;
            GetRawInputDeviceInfo(h, RIDI_DEVICENAME, null, ref s);
            var sb = new StringBuilder((int)s + 8);
            GetRawInputDeviceInfo(h, RIDI_DEVICENAME, sb, ref s);
            return sb.ToString();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                uint sz = 0, hsz = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
                GetRawInputData(m.LParam, RID_INPUT, IntPtr.Zero, ref sz, hsz);
                if (sz > 0)
                {
                    IntPtr buf = Marshal.AllocHGlobal((int)sz);
                    try
                    {
                        if (GetRawInputData(m.LParam, RID_INPUT, buf, ref sz, hsz) == sz)
                        {
                            var hdr = (RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(RAWINPUTHEADER));
                            string name = DeviceName(hdr.Dev);
                            var gm = GaimePath.Match(name);
                            if (gm.Success)
                            {
                                if (hdr.Type == 2) // digitizer
                                {
                                    IntPtr ph = new IntPtr(buf.ToInt64() + hsz);
                                    var hid = (RAWHID)Marshal.PtrToStructure(ph, typeof(RAWHID));
                                    int nb = (int)(hid.Count * hid.SizeHid);
                                    if (nb >= 6)
                                    {
                                        byte[] raw = new byte[nb];
                                        Marshal.Copy(new IntPtr(ph.ToInt64() + 8), raw, 0, nb);
                                        if (raw[0] == 1)
                                        {
                                            string ser = SnOf(name);
                                            int fl = raw[1];
                                            _onPointer(ser, raw[2] | (raw[3] << 8), raw[4] | (raw[5] << 8), (fl & 1) != 0);
                                        }
                                    }
                                }
                                else if (hdr.Type == 1) // legacy keyboard: gun buttons
                                {
                                    int vkey = Marshal.ReadInt16(buf, (int)hsz + 6);
                                    int msg = Marshal.ReadInt32(buf, (int)hsz + 8);
                                    _onKey(SnOf(name), vkey, msg == 256);
                                }
                            }
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            base.WndProc(ref m);
        }

        // Resolve HID path (no sn in RawInput name) -> on-device sn via matching
        // MI_xx suffix against vendor-interface query done at startup.
        static Dictionary<string, string> _pathSuffixToSn = new Dictionary<string, string>();
        public static void SetSnMap(Dictionary<string, string> map) => _pathSuffixToSn = map;

        static string SnOf(string rawInputName)
        {
            var mi = Regex.Match(rawInputName, @"MI_0\d#(.*?)#", RegexOptions.IgnoreCase);
            if (mi.Success)
            {
                string key = mi.Groups[1].Value.ToLowerInvariant();
                if (_pathSuffixToSn.TryGetValue(key, out var sn)) return sn;
            }
            return "unknown";
        }
    }
}
