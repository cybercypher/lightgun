using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace GaimeFeeder
{
    // Box-side 8-point gun calibration (docs/box-calibration-protocol.md).
    //
    // The proprietary Android box sends eight 64-byte vendor reports, one per
    // target, as 65-byte HID writes (report ID 0 + packet) on the gun's MI_02
    // interface. The gun stores the result itself, so nothing here touches the
    // host cal_<sn>.json.
    static class GunBoxCal
    {
        public const int PointCount = 8;
        public const int TapsPerPoint = 3;
        public const int ReportSize = 64;

        const int SendGapMs = 200;
        const int WriteTimeoutMs = 1000;

        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint SHARE = 3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        const int ERROR_IO_PENDING = 997;
        const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 258;
        const int ERROR_SEM_TIMEOUT = 121;

        [StructLayout(LayoutKind.Sequential)]
        struct OV { public IntPtr A, B; public int C, D; public IntPtr HEvent; }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr CreateFile(string f, uint a, uint s, IntPtr sec, uint c, uint fl, IntPtr t);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(IntPtr h, IntPtr b, uint n, IntPtr w, IntPtr o);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateEvent(IntPtr a, bool m, bool i, string n);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetOverlappedResult(IntPtr h, IntPtr o, out uint n, bool w);
        [DllImport("kernel32.dll")] static extern bool CancelIo(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        // Row-major 3x3 grid with the centre cell skipped:
        // 0 TL, 1 TC, 2 TR, 3 ML, 4 MR, 5 BL, 6 BC, 7 BR.
        static readonly int[] Cols = { 0, 1, 2, 0, 2, 0, 1, 2 };
        static readonly int[] Rows = { 0, 0, 0, 1, 1, 2, 2, 2 };

        // The target is a square of screenW/7, edge-aligned, so its centre is at
        // 1/14, 1/3 and 13/14 of the width, and (screenW/14)/screenH, 1/3 and
        // 1 - (screenW/14)/screenH of the height.
        static double XFrac(int i) => Cols[i] == 0 ? 1.0 / 14.0
                                    : Cols[i] == 1 ? 1.0 / 3.0
                                    : 13.0 / 14.0;

        static double YFrac(int i, int width, int height) => Rows[i] == 0 ? (width / 14.0) / height
                                                        : Rows[i] == 1 ? 1.0 / 3.0
                                                        : 1.0 - (width / 14.0) / height;

        public static (int X, int Y) Centre(int index, int width, int height)
        {
            CheckIndex(index);
            int w = Math.Max(1, width), h = Math.Max(1, height);
            return ((int)Math.Round(XFrac(index) * w),
                    (int)Math.Round(YFrac(index, w, h) * h));
        }

        static int BaseX(int index, int width) =>
            Clamp16((int)Math.Round(XFrac(index) * 32768.0));

        static int BaseY(int index, int width, int height) =>
            Clamp16((int)Math.Round(YFrac(index, width, height) * 32768.0));

        // Raw 0..10000 aim -> the gun's 16-bit calibration space.
        public static int Sample16(int raw) =>
            Clamp16((int)Math.Round(raw * 32768.0 / 10000.0));

        static int Clamp16(int v) => v < 0 ? 0 : v > 32767 ? 32767 : v;

        static void CheckIndex(int index)
        {
            if (index < 0 || index >= PointCount)
                throw new ArgumentOutOfRangeException(nameof(index));
        }

        // points[i] holds the three taps for point i as
        // { rawX0, rawY0, rawX1, rawY1, rawX2, rawY2 }.
        public static byte[][] BuildReports(int width, int height,
            IReadOnlyList<int[]> points, out string error)
        {
            error = null;
            if (width <= 0 || height <= 0)
            {
                error = "invalid client size " + width + "x" + height;
                return null;
            }
            if (points == null || points.Count != PointCount)
            {
                error = "expected " + PointCount + " points, got " +
                    (points == null ? 0 : points.Count);
                return null;
            }
            for (int i = 0; i < PointCount; i++)
            {
                if (points[i] == null || points[i].Length != TapsPerPoint * 2)
                {
                    error = "point " + (i + 1) + "/8 needs " + TapsPerPoint +
                        " taps, got " + (points[i] == null ? 0 : points[i].Length / 2);
                    return null;
                }
            }

            var reports = new byte[PointCount][];
            for (int i = 0; i < PointCount; i++)
            {
                var r = new byte[ReportSize];
                r[0] = 0x05;
                r[1] = 0x21;
                r[2] = 0x06;
                r[6] = (byte)i;

                var taps = points[i];
                for (int t = 0; t < TapsPerPoint; t++)
                {
                    // X0,Y0,X1,Y1,X2,Y2 at 7,11,15,19,23,27: each value starts
                    // a 4-byte slot and the next two bytes stay 0, so one tap
                    // spans 8 bytes.
                    Put16(r, 7 + 8 * t, Sample16(taps[t * 2]));
                    Put16(r, 11 + 8 * t, Sample16(taps[t * 2 + 1]));
                }
                Put16(r, 31, BaseX(i, width));
                Put16(r, 35, BaseY(i, width, height));
                Put16(r, 39, Crc16(r, 39));
                reports[i] = r;
            }
            return reports;
        }

        static void Put16(byte[] b, int off, int v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
        }

        // Modbus CRC16: poly 0xA001 reflected, init 0xFFFF, low byte first.
        static ushort Crc16(byte[] b, int n)
        {
            ushort c = 0xFFFF;
            for (int i = 0; i < n; i++)
            {
                c ^= b[i];
                for (int k = 0; k < 8; k++)
                    c = (ushort)(((c & 1) != 0) ? ((c >> 1) ^ 0xA001) : (c >> 1));
            }
            return c;
        }

        // Send all eight reports to the gun identified by its system_info sn.
        // Each report goes out as a 65-byte write (report ID 0 + 64 bytes) with
        // a 1 s overlapped timeout, 200 ms apart.
        public static bool Send(string sn, byte[][] reports, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(sn))
            {
                error = "gun serial is empty";
                return false;
            }
            if (reports == null || reports.Length != PointCount)
            {
                error = "expected " + PointCount + " reports, got " +
                    (reports == null ? 0 : reports.Length);
                return false;
            }
            for (int i = 0; i < reports.Length; i++)
            {
                if (reports[i] == null || reports[i].Length != ReportSize)
                {
                    error = "report " + (i + 1) + "/8 is " +
                        (reports[i] == null ? 0 : reports[i].Length) +
                        " bytes, expected " + ReportSize;
                    return false;
                }
            }

            string path = HidIdentity.VendorPathForSn(sn);
            if (string.IsNullOrEmpty(path))
            {
                error = "no MI_02 vendor interface for sn " + sn;
                return false;
            }

            IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, SHARE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.ToInt64() == -1)
            {
                error = "CreateFile(" + path + ") failed: " +
                    Win32(Marshal.GetLastWin32Error());
                return false;
            }
            try
            {
                for (int i = 0; i < reports.Length; i++)
                {
                    if (!WritePacket(h, reports[i], out error))
                    {
                        error = "report " + (i + 1) + "/8: " + error;
                        return false;
                    }
                    if (i + 1 < reports.Length) Thread.Sleep(SendGapMs);
                }
            }
            finally { CloseHandle(h); }
            return true;
        }

        static bool WritePacket(IntPtr h, byte[] report, out string error)
        {
            error = null;
            var packet = new byte[65];
            packet[0] = 0; // HID report ID
            Buffer.BlockCopy(report, 0, packet, 1, ReportSize);

            IntPtr ev = CreateEvent(IntPtr.Zero, true, false, null);
            if (ev == IntPtr.Zero)
            {
                error = "CreateEvent failed: " + Win32(Marshal.GetLastWin32Error());
                return false;
            }
            int ovSize = Marshal.SizeOf(typeof(OV));
            int evOff = Marshal.OffsetOf(typeof(OV), nameof(OV.HEvent)).ToInt32();
            IntPtr ov = Marshal.AllocHGlobal(ovSize);
            GCHandle pin = GCHandle.Alloc(packet, GCHandleType.Pinned);
            try
            {
                for (int i = 0; i < ovSize; i++) Marshal.WriteByte(ov, i, 0);
                Marshal.WriteIntPtr(ov, evOff, ev);

                if (!WriteFile(h, pin.AddrOfPinnedObject(), 65, IntPtr.Zero, ov))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_IO_PENDING)
                    {
                        error = "WriteFile failed: " + Win32(err);
                        return false;
                    }
                    // Pended: wait for completion. A synchronous success needs
                    // no wait; GetOverlappedResult reports its byte count.
                    uint wait = WaitForSingleObject(ev, WriteTimeoutMs);
                    if (wait != WAIT_OBJECT_0)
                    {
                        int werr = Marshal.GetLastWin32Error();
                        CancelIo(h);
                        // Let the cancel complete before the buffer goes away.
                        WaitForSingleObject(ev, WriteTimeoutMs);
                        error = wait == WAIT_TIMEOUT
                            ? "write timed out after " + WriteTimeoutMs + " ms (" +
                              Win32(ERROR_SEM_TIMEOUT) + ")"
                            : "WaitForSingleObject returned " + wait + ": " + Win32(werr);
                        return false;
                    }
                }

                if (!GetOverlappedResult(h, ov, out uint written, false))
                {
                    error = "GetOverlappedResult failed: " +
                        Win32(Marshal.GetLastWin32Error());
                    return false;
                }
                if (written != 65)
                {
                    error = "short write " + written + "/65";
                    return false;
                }
                return true;
            }
            finally
            {
                pin.Free();
                Marshal.FreeHGlobal(ov);
                CloseHandle(ev);
            }
        }

        static string Win32(int code) =>
            code + ": " + new Win32Exception(code).Message;
    }
}
