using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GaimeFeeder
{
    // The Pico bridge's two absolute mice as RawInput sees them: the exact
    // device names DemulShooter and TeknoParrot bind to. MI_00 = player 1,
    // MI_01 = player 2 (same lookup as fix_demulshooter_paths.ps1).
    static class BridgeDevices
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetRawInputDeviceList([In, Out] RAWINPUTDEVICELIST[] list, ref uint count, uint size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

        const uint RIDI_DEVICENAME = 0x20000007;
        static readonly Regex Pattern = new Regex(@"VID_1209&PID_0A11&MI_([0-9A-F]{2})", RegexOptions.IgnoreCase);

        // Interface number -> RawInput device name. Empty when the bridge is
        // not plugged in.
        public static Dictionary<int, string> Mice()
        {
            var found = new Dictionary<int, string>();
            uint size = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
            uint count = 0;
            if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0) return found;
            var devices = new RAWINPUTDEVICELIST[count];
            if (GetRawInputDeviceList(devices, ref count, size) == uint.MaxValue) return found;
            foreach (var d in devices)
            {
                uint chars = 0;
                GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
                if (chars <= 1) continue;
                IntPtr buf = Marshal.AllocHGlobal((int)chars * 2);
                try
                {
                    GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICENAME, buf, ref chars);
                    string name = Marshal.PtrToStringUni(buf);
                    var m = Pattern.Match(name ?? "");
                    if (m.Success) found[Convert.ToInt32(m.Groups[1].Value, 16)] = name;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return found;
        }
    }
}
