using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace GaimeFeeder
{
    // Host side of the G'AIM'E Pico bridge (pico/gun_bridge/PROTOCOL.md).
    //
    // The bridge enumerates two absolute HID mice plus a CDC port. This class
    // takes the already-calibrated screen position, scales it to 0..32767 and
    // writes 12-byte frames over the CDC port. It is silent unless the feeder
    // calls Feed(), so an idle bridge never touches the desktop cursor.
    static class PicoOutput
    {
        const string DeviceKey =
            @"SYSTEM\CurrentControlSet\Enum\USB\VID_1209&PID_0A11&MI_02";

        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 0x01;
        const uint FILE_SHARE_WRITE = 0x02;
        const uint OPEN_EXISTING = 3;

        [StructLayout(LayoutKind.Sequential)]
        struct COMMTIMEOUTS
        {
            public uint ReadIntervalTimeout;
            public uint ReadTotalTimeoutMultiplier;
            public uint ReadTotalTimeoutConstant;
            public uint WriteTotalTimeoutMultiplier;
            public uint WriteTotalTimeoutConstant;
        }

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(IntPtr handle, byte[] buffer, uint count,
            out uint written, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetCommTimeouts(IntPtr handle, ref COMMTIMEOUTS timeouts);

        static readonly object Gate = new object();
        static IntPtr _handle = new IntPtr(-1);
        static string _port;
        static DateTime _nextRetry = DateTime.MinValue;
        static byte _seq;

        // Absolute reports own the shared Windows cursor, so the bridge only
        // streams while a game process is in the foreground. On the desktop the
        // Pico stays silent and the real mouse is free.
        // Built-in list; bridge-processes.txt replaces it. Declared before
        // _games: static initializers run in textual order.
        public static readonly string[] DefaultGames =
        {
            "DemulShooter", "DemulShooterX64", "TeknoParrot",
            "EMULATOR", "emulator_multicpu",
            "BudgieLoader", "game",
            "pcsx2", "pcsx2x6", "rpcs3", "Dolphin", "PPSSPP",
            "retroarch", "mame", "flycast", "supermodel",
            "BigPEmu", "demul", "Fusion", "gens",
        };

        static volatile HashSet<string> _games = LoadGames();

        // Games whose picture the ReShade border shrinks inside the frame
        // (FIT). While one of them is in front, the aim is mapped into the
        // inner area so it still lands on what the gun points at.
        static volatile HashSet<string> _insetGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static volatile bool _inset;

        public static void SetInsetGames(IEnumerable<string> names)
        {
            _insetGames = new HashSet<string>(names ?? new string[0], StringComparer.OrdinalIgnoreCase);
            Log("fit-inside-border games: " + (_insetGames.Count == 0 ? "none" : string.Join(", ", _insetGames)));
        }
        static readonly int _pulsePeriodMs = LoadPulsePeriod();
        static int _pulseTick;

        // bridge-pulse-trigger.txt: first line is the period in ms (default
        // 160). 0 or a missing file disables the pulse.
        static int LoadPulsePeriod()
        {
            try
            {
                if (!File.Exists(AppPaths.BridgePulseTrigger)) return 0;
                var text = File.ReadAllText(AppPaths.BridgePulseTrigger).Trim();
                if (int.TryParse(text, out int ms) && ms >= 40 && ms <= 2000) return ms;
                return 160;
            }
            catch { return 0; }
        }
        static readonly bool _force =
            File.Exists(AppPaths.BridgeForce) ||
            Environment.GetEnvironmentVariable("GAIME_BRIDGE_FORCE") == "1";
        static bool _active;
        static bool _wasStreaming;
        static string _lastGate;
        static DateTime _nextGate = DateTime.MinValue;
        static readonly object LogLock = new object();

        public static string Status
        {
            get { lock (Gate) return StatusLocked(); }
        }

        // Try to open now and report where we stand (used for the startup log).
        public static string Probe()
        {
            lock (Gate)
            {
                EnsureOpen();
                Log(_pulsePeriodMs > 0
                    ? "trigger pulse: " + _pulsePeriodMs + " ms square wave"
                    : "trigger pulse: off");
                return StatusLocked();
            }
        }

        static string StatusLocked() =>
            _handle == new IntPtr(-1)
                ? (_port == null ? "not connected" : "waiting for " + _port)
                : _port;

        // sx/sy are screen pixels from RectCalib.
        //
        // Universal gun-button mapping (no per-game rebinding):
        //   trigger on-screen      -> left button
        //   trigger off-screen     -> right button (DemulShooter's reload)
        //   B button               -> right button (reload without moving the gun)
        //   A button               -> middle button (pedal / action)
        //
        // DS's changelog confirms right-click reload for mouse controllers, and
        // its middle button is the action/pedal input.
        public static void Feed(int player, double sx, double sy,
            int screenW, int screenH, bool inside, GunState g, bool trigger)
        {
            if (!ShouldStream())
            {
                if (_wasStreaming)
                {
                    _wasStreaming = false;
                    RawInputWindow.ReleaseBridgePin();
                }
                return;
            }

            _wasStreaming = true;
            RawInputWindow.NoteBridgeActive();

            if (player < 0 || player > 1) return;

            if (_inset)
            {
                // The game fills the screen minus the border on every edge.
                double b = ScreenBorder.Pixels;
                sx = (sx - b) * (screenW - 1) / Math.Max(1.0, screenW - 1 - 2 * b);
                sy = (sy - b) * (screenH - 1) / Math.Max(1.0, screenH - 1 - 2 * b);
            }
            double fx = Math.Clamp(sx / Math.Max(1.0, screenW - 1), 0.0, 1.0);
            double fy = Math.Clamp(sy / Math.Max(1.0, screenH - 1), 0.0, 1.0);
            int x = (int)Math.Round(fx * 32767.0);
            int y = (int)Math.Round(fy * 32767.0);

            byte buttons = 0;
            bool fire = trigger && inside;
            if (fire && _pulsePeriodMs > 0)
            {
                // Debug switch only (bridge-pulse-trigger.txt). It was added for a
                // held-trigger aim freeze that was a feeder bug, not TP: the tray sent
                // the latched pre-press aim for the whole hold (fixed in FeedCore).
                // Keep it off.
                long phase = Environment.TickCount64 % _pulsePeriodMs;
                fire = phase < (_pulsePeriodMs / 2);
                _pulseTick++;
            }
            if (fire) buttons |= 0x01;                              // left
            if ((trigger && !inside) || g.BtnB) buttons |= 0x02;    // right
            if (g.BtnA) buttons |= 0x04;                            // middle
            if (g.BtnCoin) buttons |= 0x08;                         // back
            if (g.BtnStart) buttons |= 0x10;                        // forward

            Send(player, x, y, buttons);
        }

        static void Send(int player, int x, int y, byte buttons)
        {
            lock (Gate)
            {
                if (!EnsureOpen()) return;

                var frame = new byte[12];
                frame[0] = 0xA5;
                frame[1] = 0x5A;
                frame[2] = 0x01;
                frame[3] = (byte)player;
                frame[4] = (byte)(buttons & 0x1F);   // five buttons: L R M back forward
                frame[6] = (byte)(x & 0xFF);
                frame[7] = (byte)((x >> 8) & 0xFF);
                frame[8] = (byte)(y & 0xFF);
                frame[9] = (byte)((y >> 8) & 0xFF);
                frame[10] = _seq++;
                frame[11] = Crc8(frame, 11);

                if (!WriteFile(_handle, frame, 12, out uint written, IntPtr.Zero) ||
                    written != 12)
                {
                    CloseHandle(_handle);
                    _handle = new IntPtr(-1);
                    _nextRetry = DateTime.UtcNow.AddSeconds(2);
                }
            }
        }

        static bool EnsureOpen()
        {
            if (_handle != new IntPtr(-1)) return true;
            if (DateTime.UtcNow < _nextRetry) return false;

            var port = FindPort();
            if (port == null)
            {
                _nextRetry = DateTime.UtcNow.AddSeconds(2);
                return false;
            }

            // Synchronous handle with a short write timeout: a yanked Pico must
            // not stall the feeder's report loop.
            var handle = CreateFile(@"\\.\" + port, GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                0, IntPtr.Zero);
            if (handle == new IntPtr(-1))
            {
                _nextRetry = DateTime.UtcNow.AddSeconds(2);
                return false;
            }

            var timeouts = new COMMTIMEOUTS { WriteTotalTimeoutConstant = 50 };
            SetCommTimeouts(handle, ref timeouts);
            _handle = handle;
            _port = port;
            Log("opened " + port);
            return true;
        }

        // The COM name Windows assigned to the bridge's CDC function, or null.
        // Registry only: it stays set after the bridge is unplugged.
        public static string BridgePort() => FindPort();

        // Game process names that unlock the stream, as currently loaded.
        public static List<string> Games()
        {
            var list = new List<string>(_games);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // Re-read bridge-processes.txt after the setup wizard edits it. The
        // feed thread only reads the reference, so swapping it is enough.
        public static void ReloadGames() => _games = LoadGames();

        // Make sure one game process unlocks the stream (setup wizard, after
        // binding a game). Writes bridge-processes.txt from the current list
        // when it does not exist yet. True when the list changed.
        public static bool EnsureGame(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || _games.Contains(name)) return false;
            var names = Games();
            names.Add(name);
            File.WriteAllLines(AppPaths.BridgeProcesses,
                new[] { "# Game processes that turn the Pico bridge on (setup wizard)." }.Concat(names));
            ReloadGames();
            return true;
        }

        // The bridge's CDC function is USB\VID_1209&PID_0A11&MI_02; Windows
        // stores the assigned COM name under the instance's Device Parameters.
        static string FindPort()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(DeviceKey);
                if (key == null) return null;

                foreach (var instance in key.GetSubKeyNames())
                {
                    using var parameters = key.OpenSubKey(instance + @"\Device Parameters");
                    if (parameters?.GetValue("PortName") is string port &&
                        port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                        return port;
                }
            }
            catch { }
            return null;
        }

        // Cheap foreground check, done on the feed thread at most every 250 ms.
        // Absolute reports would otherwise pin the desktop cursor even when no
        // game is running.
        static bool ShouldStream()
        {
            if (_force)
            {
                if (!_active) { _active = true; Log("forced on"); }
                return true;
            }

            var now = DateTime.UtcNow;
            if (now >= _nextGate)
            {
                _nextGate = now.AddMilliseconds(250);

                string name = "?";
                bool active = false;
                try
                {
                    var window = GetForegroundWindow();
                    if (window != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(window, out uint pid);
                        using var process = Process.GetProcessById((int)pid);
                        name = process.ProcessName;
                        active = _games.Contains(name);
                        _inset = active && _insetGames.Contains(name);
                    }
                }
                catch { }

                _active = active;
                string gate = active ? "streaming: " + name + (_inset ? " (fit inside border)" : "") : "paused: " + name;
                if (gate != _lastGate)
                {
                    _lastGate = gate;
                    Log(gate);
                }
            }
            return _active;
        }

        static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(AppPaths.Logs);
                    File.AppendAllText(
                        Path.Combine(AppPaths.Logs, "bridge-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                        DateTime.Now.ToString("HH:mm:ss.fff ") + message + Environment.NewLine);
                }
            }
            catch { }
        }

        // bridge-processes.txt (one exe name per line, '#' comments) overrides
        // the built-in list.
        static HashSet<string> LoadGames()
        {
            var set = new HashSet<string>(DefaultGames, StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = AppPaths.BridgeProcesses;
                if (File.Exists(path))
                {
                    set.Clear();
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var name = line.Trim();
                        if (name.Length == 0 || name.StartsWith("#")) continue;
                        set.Add(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            ? name.Substring(0, name.Length - 4) : name);
                    }
                }
            }
            catch { }
            return set;
        }

        // CRC-8, polynomial 0x07, init 0x00 (PROTOCOL.md).
        static byte Crc8(byte[] data, int count)
        {
            byte crc = 0;
            for (int i = 0; i < count; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                    crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            }
            return crc;
        }
    }
}
