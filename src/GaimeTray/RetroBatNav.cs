using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GaimeTray
{
    // RetroBat's menu (EmulationStation) with the guns. RetroBat reads a
    // keyboard out of the box with fixed keys (wiki, navigation keys): arrows
    // move, X launches, W goes back (QWERTY), Enter opens the main menu,
    // Backspace is Select / options. The guns' own buttons send other keys
    // (A Space, B b, Start q, Coin c) and the trigger sends none, so RetroBat
    // saw "no controller". While emulationstation is in front:
    //   trigger: left / right edge -> Left / Right (spin the carousel),
    //            top / bottom edge -> Up / Down, centre -> X (launch)
    //   A -> X, B -> W, Start -> Enter, Coin -> Backspace
    // using each gun's calibrated aim. The bridge does not stream there, so
    // the cursor is left alone.
    sealed class RetroBatNav
    {
        readonly FeederEngine _eng;
        const string Frontend = "emulationstation";

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
        const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8, KEYEVENTF_EXTENDEDKEY = 0x1;

        public RetroBatNav(FeederEngine eng)
        {
            _eng = eng;
            _eng.GunUiPress += OnTrigger;
            _eng.GunKey += OnKey;
        }

        static bool FrontendInFront()
        {
            try
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                using (var p = Process.GetProcessById((int)pid))
                    return string.Equals(p.ProcessName, Frontend, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        void OnTrigger(string sn)
        {
            if (!FrontendInFront() || !_eng.GunScreenPoint(sn, out var pt)) return;
            var b = Screen.PrimaryScreen.Bounds;
            double fx = (pt.X - b.X) / (double)b.Width, fy = (pt.Y - b.Y) / (double)b.Height;
            Keys key;
            if (fx < 0.25) key = Keys.Left;
            else if (fx > 0.75) key = Keys.Right;
            else if (fy < 0.25) key = Keys.Up;
            else if (fy > 0.75) key = Keys.Down;
            else key = Keys.X;
            Tap(key);
        }

        void OnKey(string sn, int vkey, bool down)
        {
            if (!FrontendInFront()) return;
            Keys key;
            switch (vkey)
            {
                case 0x20: key = Keys.X; break;          // A     -> launch
                case 0x42: key = Keys.W; break;          // B     -> back
                case 0x51: key = Keys.Enter; break;      // Start -> main menu
                case 0x43: key = Keys.Back; break;       // Coin  -> select / options
                default: return;
            }
            Send(key, down);
        }

        static void Tap(Keys key) { Send(key, true); Send(key, false); }

        // Scan codes, which SDL (EmulationStation) reads; arrows are extended keys.
        static void Send(Keys key, bool down)
        {
            ushort scan = (ushort)MapVirtualKey((uint)key, 0);
            bool ext = key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down;
            var input = new INPUT { type = INPUT_KEYBOARD };
            input.ki.wVk = (ushort)key;
            input.ki.wScan = scan;
            input.ki.dwFlags = KEYEVENTF_SCANCODE | (ext ? KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : KEYEVENTF_KEYUP);
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}
