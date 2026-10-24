using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint s);
        [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hsize);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, StringBuilder b, ref uint s);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);

        // Cursor anchor: named RawInput mice are the user's real mouse (the
        // guns are digitizer + keyboard). Windows also synthesizes mouse
        // reports for the gun's touch digitizer with an empty device name;
        // those are filtered below and must never move the anchor.
        // Remember where the user leaves the OS cursor; PinCursor() and the
        // guard thread put the cursor back there. Windows moves the cursor
        // from the digitizer, so holding it is the only way to keep the
        // pointer still without a filter driver (RIDEV_NOLEGACY only suppresses
        // this app's legacy messages, not the system cursor).
        public static int AnchorX, AnchorY;
        public static volatile bool HaveAnchor;

        // Continuous guard. PinCursor() alone runs only when a report arrives,
        // but Windows applies the digitizer's mouse movement after the WM_INPUT
        // handler, so the cursor alternates between the anchor and the gun at
        // report rate (measured 2026-10-23: 987 digitizer + 412 synthetic-mouse
        // reports in 10 s, cursor visibly bouncing). A 1 ms thread holds the
        // anchor; it stands down for 250 ms after real mouse movement so the
        // user's mouse is never fought.
        static volatile bool _guardRun;
        // TickCount64 + Volatile so the loop always re-reads these. A bare
        // non-volatile field let the JIT hoist the read and the guard kept
        // fighting the real mouse (measured 2026-10-23).
        static long _mouseActiveUntil;
        static long _gunActiveUntil;
        static Thread _guard;

        // Pin only while a gun has a valid tracking lock. A gun that is idle or
        // unlocked must not hold the cursor: that is what stole the user's
        // mouse while drawing. NoteGunLock() is called from GunState.PushPointer
        // for both apps; the 500 ms tail keeps the pin alive between reports.
        public static void NoteGunLock(bool locked)
        {
            if (locked) Volatile.Write(ref _gunActiveUntil, Environment.TickCount64 + 500);
        }
        static bool GunActive => Environment.TickCount64 < Volatile.Read(ref _gunActiveUntil);
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        static void StartCursorGuard()
        {
            if (_guard != null) return;
            _guardRun = true;
            timeBeginPeriod(1);
            _guard = new Thread(() =>
            {
                while (_guardRun)
                {
                    try
                    {
                        if (HaveAnchor && GunActive &&
                            Environment.TickCount64 >= Volatile.Read(ref _mouseActiveUntil))
                        {
                            if (GetCursorPos(out var p) && (p.X != AnchorX || p.Y != AnchorY))
                                SetCursorPos(AnchorX, AnchorY);
                        }
                    }
                    catch { }
                    Thread.Sleep(1);
                }
            })
            { IsBackground = true, Name = "CursorGuard", Priority = ThreadPriority.AboveNormal };
            _guard.Start();
        }

        // Diagnostic sink (the tray wires this to its log). One line per raw
        // device on first sight is cheap and answers "which device is this?".
        public static Action<string> Log;
        readonly HashSet<IntPtr> _loggedDev = new HashSet<IntPtr>();

        // Put the OS cursor back on the real mouse's last position. Called once
        // per gun pointer report; a no-op when the cursor is already there.
        public static void PinCursor()
        {
            if (!GunActive) return;
            if (!HaveAnchor)
            {
                if (!GetCursorPos(out var seed)) return;
                AnchorX = seed.X; AnchorY = seed.Y; HaveAnchor = true;
                return;
            }
            if (!GetCursorPos(out var pt)) return;
            if (pt.X == AnchorX && pt.Y == AnchorY) return;
            SetCursorPos(AnchorX, AnchorY);
        }

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
                // Seed the anchor before the first gun report can move the
                // cursor (a keyboard-only cabinet never produces a mouse
                // report, but the pointer must still stay put).
                if (GetCursorPos(out var seed))
                {
                    AnchorX = seed.X; AnchorY = seed.Y; HaveAnchor = true;
                }
                RegisterRawInputDevices(new[]
                {
                    new RAWINPUTDEVICE { Page = 0x0D, Usage = 0x04, Flags = RIDEV_INPUTSINK, Hwnd = Handle },
                    new RAWINPUTDEVICE { Page = 0x01, Usage = 0x06, Flags = RIDEV_INPUTSINK, Hwnd = Handle },
                    new RAWINPUTDEVICE { Page = 0x01, Usage = 0x02, Flags = RIDEV_INPUTSINK, Hwnd = Handle },
                }, 3, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
                StartCursorGuard();
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _guardRun = false;
                try { timeEndPeriod(1); } catch { }
            }
            base.Dispose(disposing);
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
                            if (_loggedDev.Add(hdr.Dev))
                            {
                                try
                                {
                                    Log?.Invoke("raw dev type " + hdr.Type + ": " +
                                        DeviceName(hdr.Dev));
                                }
                                catch { }
                            }
                            string name = DeviceName(hdr.Dev);
                            if (hdr.Type == 0)
                            {
                                // Windows synthesizes mouse reports for the gun's
                                // touch digitizer. They arrive with an empty device
                                // name and huge deltas, and they are not the user's
                                // mouse. Re-anchoring from them makes the anchor
                                // follow the gun, which is exactly what PinCursor
                                // is meant to prevent (measured 2026-10-23: 412
                                // such reports vs 0 real-mouse reports in 10 s,
                                // cursor swept 335..3801 px).
                                bool gunSyntheticMouse = name.Length == 0 ||
                                    name.IndexOf("VID_2E2C", StringComparison.OrdinalIgnoreCase) >= 0;
                                if (gunSyntheticMouse)
                                {
                                    PinCursor();
                                }
                                else
                                {
                                    // MOUSEINPUT starts right after the header:
                                    // dx at +0, dy at +4 (the old +12/+16 read
                                    // dwFlags/time and was meaningless).
                                    int dx = Marshal.ReadInt32(buf, (int)hsz);
                                    int dy = Marshal.ReadInt32(buf, (int)hsz + 4);
                                    if (dx != 0 || dy != 0)
                                    {
                                        // Real movement: let the user's mouse win
                                        // for a full second, then resume guarding.
                                        if (GetCursorPos(out var pt))
                                        {
                                            AnchorX = pt.X; AnchorY = pt.Y; HaveAnchor = true;
                                        }
                                        Volatile.Write(ref _mouseActiveUntil, Environment.TickCount64 + 1000);
                                    }
                                }
                            }
                            else
                            {
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
