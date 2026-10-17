using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace GaimeFeeder
{
    // XInput-native slot viewer: shows exactly what Teknoparrot sees
    // (XInput slots 0-3), unlike joy.cpl which is a DirectInput view.
    // Run in a SECOND console while the feeder runs:
    //   GaimeFeeder --xcheck
    static class XCheck
    {
        [StructLayout(LayoutKind.Sequential)]
        struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        static extern uint GetState14(uint idx, out XINPUT_STATE state);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        static extern uint GetState91(uint idx, out XINPUT_STATE state);

        const ushort RB = 0x0200, LB = 0x0100, A = 0x1000, B = 0x2000, BACK = 0x0020, START = 0x0010;

        static bool TryGet(uint i, out XINPUT_STATE st)
        {
            try { if (GetState14(i, out st) == 0) return true; }
            catch (DllNotFoundException) { }
            try { return GetState91(i, out st) == 0; }
            catch (DllNotFoundException) { st = new XINPUT_STATE(); return false; }
        }

        public static void Run()
        {
            Console.WriteLine("XInput slots 0-3 @10Hz. Feeder T/G = RB+RT on its pad. Ctrl+C quits.");
            Console.WriteLine("Which slot lights = which XInput-N Teknoparrot will bind.");
            string[] last = new string[4];
            while (true)
            {
                for (uint i = 0; i < 4; i++)
                {
                    string line;
                    if (TryGet(i, out var st))
                    {
                        var g = st.Gamepad;
                        line = $"slot {i}: #{st.dwPacketNumber} " +
                               $"RB={((g.wButtons & RB) != 0 ? 1 : 0)} " +
                               $"RT={g.bRightTrigger,3} LT={g.bLeftTrigger,3} " +
                               $"LX={g.sThumbLX,6} LY={g.sThumbLY,6} " +
                               $"A={((g.wButtons & A) != 0 ? 1 : 0)} " +
                               $"B={((g.wButtons & B) != 0 ? 1 : 0)} " +
                               $"Back={((g.wButtons & BACK) != 0 ? 1 : 0)} " +
                               $"Start={((g.wButtons & START) != 0 ? 1 : 0)}";
                    }
                    else line = $"slot {i}: (absent)";
                    if (line != last[i])
                    {
                        Console.WriteLine(line);
                        last[i] = line;
                    }
                }
                Thread.Sleep(100);
            }
        }
    }
}
