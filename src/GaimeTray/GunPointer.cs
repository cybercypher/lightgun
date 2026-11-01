using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using GaimeFeeder;

namespace GaimeTray
{
    // Windows treats each gun as a touchscreen, so a trigger pull is a touch
    // tap at the gun's *uncalibrated* position. Inside the tray's own windows
    // that is a click in the wrong place (it hit the calibration grid's
    // Cancel button). This filter drops pointer messages that come from a gun
    // before they reach any tray window; the gun clicks through GunPointer
    // instead, at its calibrated aim. Touch from other devices (a Surface's own
    // screen) and the mouse pass untouched. Native dialogs (MessageBox) run
    // their own loop and are not filtered.
    sealed class GunTouchFilter : IMessageFilter
    {
        const int WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247,
                  WM_POINTERENTER = 0x0249, WM_POINTERLEAVE = 0x024A;

        [StructLayout(LayoutKind.Sequential)]
        struct POINTER_INFO
        {
            public int pointerType; public uint pointerId; public uint frameId; public int pointerFlags;
            public IntPtr sourceDevice; public IntPtr hwndTarget;
            public Point ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
            public uint dwTime, historyCount; public int InputData; public uint dwKeyStates;
            public ulong PerformanceCount; public int ButtonChangeType;
        }

        [DllImport("user32.dll")] static extern bool GetPointerInfo(uint pointerId, out POINTER_INFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);
        const uint RIDI_DEVICENAME = 0x20000007;

        readonly Dictionary<IntPtr, bool> _isGun = new Dictionary<IntPtr, bool>();
        readonly Action<string> _log;

        public GunTouchFilter(Action<string> log) { _log = log; }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg < WM_POINTERUPDATE || m.Msg > WM_POINTERLEAVE || m.Msg == 0x0248) return false;
            uint id = (uint)(m.WParam.ToInt64() & 0xFFFF);
            if (!GetPointerInfo(id, out var info)) return false;
            return IsGun(info.sourceDevice);
        }

        // The pointer's source device is the same handle RawInput uses, so
        // its device name tells a gun (VID_2E2C) from anything else.
        bool IsGun(IntPtr device)
        {
            if (device == IntPtr.Zero) return false;
            if (_isGun.TryGetValue(device, out bool gun)) return gun;
            string name = "";
            try
            {
                uint size = 0;
                GetRawInputDeviceInfo(device, RIDI_DEVICENAME, null, ref size);
                if (size > 1)
                {
                    var sb = new StringBuilder((int)size);
                    GetRawInputDeviceInfo(device, RIDI_DEVICENAME, sb, ref size);
                    name = sb.ToString();
                }
            }
            catch { }
            gun = name.IndexOf("VID_2E2C", StringComparison.OrdinalIgnoreCase) >= 0;
            _isGun[device] = gun;
            _log?.Invoke("touch device " + (name.Length == 0 ? "(name unknown)" : name) + (gun ? " — a gun, its taps are ignored in tray windows" : ""));
            return gun;
        }
    }

    // While a tray window is in front (main window, setup wizard), each gun
    // gets a crosshair at its calibrated aim and the trigger clicks what is
    // under it: the tray is usable with the guns alone. Calibration and the
    // gallery read the guns themselves, so the pointer stands down there.
    sealed class GunPointer
    {
        readonly FeederEngine _eng;
        readonly Timer _timer = new Timer { Interval = 33 };
        readonly Dictionary<string, Crosshair> _marks = new Dictionary<string, Crosshair>();
        static readonly Color[] PlayerColors = { Color.FromArgb(140, 255, 120), Color.FromArgb(90, 200, 255) };

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        const int BM_CLICK = 0x00F5;

        public GunPointer(FeederEngine eng)
        {
            _eng = eng;
            _eng.GunUiPress += OnPress;
            _timer.Tick += (s, e) => { try { Tick(); } catch { } };
            _timer.Start();
        }

        enum Mode { None, Click, ShowOnly }

        // Click: a tray window is in front (the trigger clicks it).
        // ShowOnly: RetroBat's menu (RetroBatNav handles the shots), or a game
        // on the bridge list when CrosshairInGames is on.
        // None while a calibration or the gallery reads the guns.
        Mode Current()
        {
            if (!_eng.UiFree) return Mode.None;
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            if (pid == (uint)Environment.ProcessId) return Mode.Click;
            string name = ProcessName(pid);
            if (string.Equals(name, RetroBat.FrontendProcess, StringComparison.OrdinalIgnoreCase)) return Mode.ShowOnly;
            if (_inGames && name != null && PicoOutput.Games().Contains(name, StringComparer.OrdinalIgnoreCase)) return Mode.ShowOnly;
            return Mode.None;
        }

        bool Active() => Current() == Mode.Click;

        // Foreground process name, looked up once per process.
        uint _namePid;
        string _name;
        string ProcessName(uint pid)
        {
            if (pid == _namePid) return _name;
            _namePid = pid;
            try { using (var p = Process.GetProcessById((int)pid)) _name = p.ProcessName; }
            catch { _name = null; }
            return _name;
        }

        // Set from setup.json at start and when the Games page checkbox changes.
        static volatile bool _inGames = GaimeFeeder.SetupConfig.Load().CrosshairInGames;
        public static void ShowInGames(bool on) => _inGames = on;

        void Tick()
        {
            bool on = Current() != Mode.None;
            var seen = new HashSet<string>();
            if (on)
                foreach (var g in _eng.Snapshot())
                {
                    if (g.AgeMs > 500 || !_eng.GunScreenPoint(g.Serial, out var pt)) continue;
                    seen.Add(g.Serial);
                    if (!_marks.TryGetValue(g.Serial, out var mark))
                    {
                        mark = new Crosshair(PlayerColors[(Math.Max(1, g.PadIndex) - 1) % PlayerColors.Length]);
                        _marks[g.Serial] = mark;
                    }
                    mark.MoveTo(pt);
                }
            foreach (var kv in _marks)
                if (!seen.Contains(kv.Key) && kv.Value.Visible) kv.Value.Hide();
        }

        void OnPress(string sn)
        {
            if (!Active() || !_eng.GunScreenPoint(sn, out var pt)) return;
            if (_marks.TryGetValue(sn, out var mark)) mark.Flash();
            try { Click(pt); } catch (Exception ex) { _eng.Note("gun click: " + ex.Message); }
        }

        // Act on the control under the point the way a mouse click would.
        void Click(Point screen)
        {
            IntPtr h = WindowFromPoint(screen);
            var c = Control.FromChildHandle(h);
            if (c == null)
            {
                // A native dialog (MessageBox): press its button directly.
                var cls = new StringBuilder(32);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() == "Button") SendMessage(h, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            // Deepest visible, enabled control under the point.
            while (true)
            {
                var child = c.GetChildAtPoint(c.PointToClient(screen), GetChildAtPointSkip.Invisible | GetChildAtPointSkip.Disabled);
                if (child == null) break;
                c = child;
            }
            if (!c.Enabled) return;
            var local = c.PointToClient(screen);
            switch (c)
            {
                case CheckBox cb: cb.Checked = !cb.Checked; break;
                case RadioButton rb: rb.Checked = true; break;
                case Button b: b.PerformClick(); break;
                case ComboBox box when box.Items.Count > 0:
                    // Pull the trigger to step through the choices.
                    box.SelectedIndex = (box.SelectedIndex + 1) % box.Items.Count;
                    Invoke(box, "OnSelectionChangeCommitted");
                    break;
                case ListView lv:
                    var hit = lv.HitTest(local).Item;
                    if (hit != null) { hit.Selected = !hit.Selected; hit.Focused = true; }
                    break;
                case TabControl tc:
                    for (int i = 0; i < tc.TabCount; i++)
                        if (tc.GetTabRect(i).Contains(local)) { tc.SelectedIndex = i; break; }
                    break;
                case TextBoxBase tb: tb.Focus(); break;
                default:
                    if (c.Parent is UpDownBase spin)
                    {
                        // The up/down arrows are an inner control of the spinner.
                        if (local.Y < c.Height / 2) spin.UpButton(); else spin.DownButton();
                    }
                    else Invoke(c, "OnClick"); // labels with a Click handler (wizard page list)
                    break;
            }
        }

        static void Invoke(Control c, string method)
        {
            var mi = typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(EventArgs) }, null)
                     ?? c.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(EventArgs) }, null);
            mi?.Invoke(c, new object[] { EventArgs.Empty });
        }

        // A ring the size of a fingertip, click-through, never takes focus.
        sealed class Crosshair : Form
        {
            const int Size_ = 44;
            readonly Color _color;
            DateTime _flashUntil = DateTime.MinValue;

            public Crosshair(Color color)
            {
                _color = color;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Size = new Size(Size_, Size_);
                BackColor = Color.Magenta;
                TransparencyKey = Color.Magenta;
                TopMost = true;
                DoubleBuffered = true;
            }

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    // layered + click-through + no-activate + tool window (no alt-tab)
                    cp.ExStyle |= 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080;
                    return cp;
                }
            }

            public void MoveTo(Point p)
            {
                var at = new Point(p.X - Size_ / 2, p.Y - Size_ / 2);
                if (Location != at) Location = at;
                if (!Visible) Show();
                if (DateTime.UtcNow < _flashUntil || _flashUntil != DateTime.MinValue) Invalidate();
            }

            public void Flash()
            {
                _flashUntil = DateTime.UtcNow.AddMilliseconds(150);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                bool flash = DateTime.UtcNow < _flashUntil;
                if (!flash) _flashUntil = DateTime.MinValue;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pen = new Pen(flash ? Color.White : _color, flash ? 5f : 3f))
                {
                    e.Graphics.DrawEllipse(pen, 6, 6, Size_ - 13, Size_ - 13);
                    e.Graphics.DrawLine(pen, Size_ / 2, 0, Size_ / 2, 10);
                    e.Graphics.DrawLine(pen, Size_ / 2, Size_ - 10, Size_ / 2, Size_);
                    e.Graphics.DrawLine(pen, 0, Size_ / 2, 10, Size_ / 2);
                    e.Graphics.DrawLine(pen, Size_ - 10, Size_ / 2, Size_, Size_ / 2);
                }
            }
        }
    }
}
