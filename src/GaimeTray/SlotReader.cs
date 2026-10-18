using System;
using System.Runtime.InteropServices;

namespace GaimeTray
{
    // XInput-native slot view: exactly what Teknoparrot binds to.
    static class SlotReader
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Gamepad
        {
            public ushort Buttons;
            public byte LT, RT;
            public short LX, LY, RX, RY;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct State
        {
            public uint Packet;
            public Gamepad Pad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        static extern uint Get14(uint i, out State s);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        static extern uint Get91(uint i, out State s);

        const ushort RB = 0x0200, A = 0x1000, B = 0x2000, BACK = 0x0020, START = 0x0010;

        public static bool TryDescribe(uint i, out string line)
        {
            try { if (Get14(i, out var s) == 0) { line = Fmt(i, s); return true; } }
            catch (DllNotFoundException) { }
            try
            {
                if (Get91(i, out var s2) == 0) { line = Fmt(i, s2); return true; }
            }
            catch (DllNotFoundException) { }
            line = "slot " + i + ": (absent)";
            return false;
        }

        static string Fmt(uint i, State s)
        {
            var g = s.Pad;
            return "slot " + i + ": #" + s.Packet +
                " RB=" + (((g.Buttons & RB) != 0) ? 1 : 0) +
                " RT=" + g.RT.ToString().PadLeft(3) +
                " LX=" + g.LX.ToString().PadLeft(6) +
                " LY=" + g.LY.ToString().PadLeft(6) +
                " A=" + (((g.Buttons & A) != 0) ? 1 : 0) +
                " B=" + (((g.Buttons & B) != 0) ? 1 : 0) +
                " Back=" + (((g.Buttons & BACK) != 0) ? 1 : 0) +
                " Start=" + (((g.Buttons & START) != 0) ? 1 : 0);
        }
    }
}
