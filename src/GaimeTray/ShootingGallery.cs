using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using GaimeFeeder;

namespace GaimeTray
{
    // Fullscreen shooting gallery for checking calibration.
    //
    // Every shot is drawn where RectCalib.Map() puts the pre-press latched
    // position - the same point the Pico bridge's press frame sends (FeedCore).
    // The crosshair and HUD use the live filtered position. The bridge does not
    // stream while this window is in front (it is not a game process) unless
    // bridge-force-on.txt exists.
    //
    // One target at a time: aim at the ring and pull the trigger. A hit scores
    // and a new target appears. The HUD shows raw, mapped, inside/lock state and
    // the distance from the last shot to the target centre, which is the actual
    // calibration error in screen pixels.
    sealed class ShootingGallery : Form
    {
        sealed class GunVis
        {
            public string Label;
            public Color Color;
            public bool HasFix, Inside, Lock;
            public double Sx, Sy;
            public int RawX, RawY;
            public double Hz;
            public int Shots, Hits;
            public double LastErr = -1;
            public DateTime Seen = DateTime.MinValue;
            // Stationary aim samples (decimated) for the noise readout.
            public readonly Queue<(float X, float Y)> Quiet = new Queue<(float, float)>();
            public int ReportN;
        }

        sealed class Shot
        {
            public float X, Y;
            public bool HasFix, Hit;
            public Color Color;
            public DateTime When;
        }

        sealed class Target
        {
            public float X, Y, R;
        }

        static readonly Color Bg = Color.FromArgb(10, 11, 13);
        static readonly Color Lime = Color.FromArgb(140, 255, 120);
        static readonly Color Amber = Color.FromArgb(255, 200, 80);
        static readonly Color Dim = Color.FromArgb(150, 150, 150);
        static readonly Color Warn = Color.FromArgb(255, 140, 60);

        readonly Dictionary<string, GunVis> _vis = new Dictionary<string, GunVis>();
        readonly List<Shot> _shots = new List<Shot>();
        readonly Random _rng = new Random();
        readonly Timer _tick = new Timer { Interval = 33 };
        readonly DateTime _opened = DateTime.UtcNow;
        DateTime _lastWaitPaint = DateTime.MinValue;

        // Wired to the tray log by FeederEngine. The noise figure is the whole
        // point of the border/smoothing/measure toggles, so each toggle writes
        // the period that just ended instead of leaving it on screen to be
        // forgotten.
        public static Action<string> Log;
        Target _target;
        bool _live;
        bool _measure;

        // Box-side 8-point calibration from inside the gallery. Start/Q toggles
        // it, each trigger pull takes one tap; the first gun to pull (or press
        // Start) owns the run. The reports go to the vendor interface, not to
        // cal_<sn>.json.
        bool _boxCal;
        string _boxSerial, _boxLabel;
        int _boxPoint, _boxTap;
        readonly List<int[]> _boxPoints = new List<int[]>(GunBoxCal.PointCount);
        readonly Dictionary<int, DateTime> _lastAction = new Dictionary<int, DateTime>();
        DateTime _lastBoxTap = DateTime.MinValue;

        const double ShotLifeMs = 3000;
        public ShootingGallery()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds;
            TopMost = true;
            BackColor = Bg;
            KeyPreview = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);
            _tick.Tick += (s, e) => Tick();
            Shown += (s, e) => { SpawnTarget(); _tick.Start(); Activate(); };
            FormClosed += (s, e) => { _tick.Stop(); LogNoise("closed"); };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == Keys.V)
            {
                ToggleBorder();
                return true;
            }
            if (keyData == Keys.F)
            {
                // Host-side 1 euro filter on the aim stream.
                LogNoise("smoothing " + (GunState.Smoothing ? "on" : "off"));
                GunState.Smoothing = !GunState.Smoothing;
                foreach (var v in _vis.Values) { v.Quiet.Clear(); v.ReportN = 0; }
                Invalidate();
                return true;
            }
            if (keyData == Keys.M)
            {
                ToggleMeasure();
                return true;
            }
            if (keyData == Keys.K)
            {
                // Keyboard entry stays unlocked: the first A picks the gun.
                if (_boxCal) ExitBoxCal();
                else EnterBoxCal(null);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Sinden-style white border, shared with the calibration window. Log the
        // period that just ended, then clear: the with/without comparison is only
        // honest if the samples restart.
        void ToggleBorder()
        {
            LogNoise("border " + (ScreenBorder.Enabled ? "on" : "off"));
            ScreenBorder.Enabled = !ScreenBorder.Enabled;
            foreach (var v in _vis.Values) { v.Quiet.Clear(); v.ReportN = 0; }
            Invalidate();
        }

        // Measure mode: fixed centre mark, target hidden, so "hold still" has an
        // unambiguous place to hold.
        void ToggleMeasure()
        {
            if (_measure) LogNoise("measure end");
            _measure = !_measure;
            foreach (var v in _vis.Values) { v.Quiet.Clear(); v.ReportN = 0; }
            Invalidate();
        }

        // Called by the feeder for gun buttons (already on the UI thread). The
        // OS also turns some gun buttons into ordinary keyboard keys, so B is
        // deliberately absent from ProcessCmdKey: one press, one toggle.
        public void OnGunKey(GunState g, int vkey, bool down)
        {
            if (g == null || !down) return;
            switch (vkey)
            {
                case 0x42: if (Debounced(0x42)) ToggleMeasure(); break;
                case 0x43: if (Debounced(0x43)) ToggleBorder(); break;
                case 0x51:
                    if (!Debounced(0x51)) break;
                    if (_boxCal) ExitBoxCal();
                    else EnterBoxCal(g);
                    break;
            }
        }

        // Gun buttons have no key-up repeat of their own, but the digitizer also
        // emits keyboard keys and raw reports can double-fire; one action per
        // 250 ms is plenty.
        bool Debounced(int vkey)
        {
            var now = DateTime.UtcNow;
            if (_lastAction.TryGetValue(vkey, out var last) &&
                (now - last).TotalMilliseconds < 250) return false;
            _lastAction[vkey] = now;
            return true;
        }

        void EnterBoxCal(GunState g)
        {
            if (_measure) { LogNoise("measure end"); _measure = false; }
            _boxCal = true;
            _boxSerial = g?.Serial;
            _boxLabel = g?.Label;
            _boxPoint = 0;
            _boxTap = 0;
            _boxPoints.Clear();
            _boxPoints.Add(new int[GunBoxCal.TapsPerPoint * 2]);
            _lastBoxTap = DateTime.MinValue;
            _target = null;
            _shots.Clear();
            Log?.Invoke("gallery [boxcal]: " + (g == null
                ? "unlocked - first A picks the gun" : g.Label + " locked") +
                ", point 1/8 tap 1/3");
            Invalidate();
        }

        // Start/Q cancels: clearing here means a partial run is never sent.
        void ExitBoxCal()
        {
            _boxCal = false;
            _boxSerial = null;
            _boxLabel = null;
            _boxPoint = 0;
            _boxTap = 0;
            _boxPoints.Clear();
            _target = null;
            SpawnTarget();
            Invalidate();
        }

        void SampleBoxCal(GunState g)
        {
            if (!_boxCal || _boxPoint >= GunBoxCal.PointCount ||
                _boxTap >= GunBoxCal.TapsPerPoint) return;
            if (_boxSerial != null && g.Serial != _boxSerial) return;
            var now = DateTime.UtcNow;
            if ((now - _lastBoxTap).TotalMilliseconds < 400) return;
            _lastBoxTap = now;
            if (_boxSerial == null)
            {
                _boxSerial = g.Serial;
                _boxLabel = g.Label;
                Log?.Invoke("gallery [boxcal]: locked to " + g.Label);
            }
            var taps = _boxPoints[_boxPoint];
            taps[_boxTap * 2] = g.RawX;
            taps[_boxTap * 2 + 1] = g.RawY;
            _boxTap++;
            Log?.Invoke("gallery [boxcal]: " + _boxLabel + " point " +
                (_boxPoint + 1) + "/8 tap " + _boxTap + "/3");
            if (_boxTap >= GunBoxCal.TapsPerPoint)
            {
                if (_boxPoint >= GunBoxCal.PointCount - 1)
                {
                    FinishBoxCal(g);
                    return;
                }
                _boxPoint++;
                _boxTap = 0;
                _boxPoints.Add(new int[GunBoxCal.TapsPerPoint * 2]);
            }
            Invalidate();
        }

        // Build all eight reports, then hand them to the gun's vendor interface.
        // This is the box's calibration, so no cal_<sn>.json is written.
        void FinishBoxCal(GunState g)
        {
            string serial = _boxSerial ?? g.Serial;
            string label = _boxLabel ?? g.Label;
            byte[][] reports = GunBoxCal.BuildReports(ClientSize.Width, ClientSize.Height,
                _boxPoints, out string error);
            if (reports == null)
            {
                Log?.Invoke("gallery [boxcal]: " + label + " build FAILED: " + error);
                ExitBoxCal();
                return;
            }
            bool ok;
            try { ok = GunBoxCal.Send(serial, reports, out error); }
            catch (Exception ex)
            {
                Log?.Invoke("gallery [boxcal]: " + label + " send FAILED: " + ex.Message);
                ExitBoxCal();
                return;
            }
            if (!ok)
            {
                Log?.Invoke("gallery [boxcal]: " + label + " send FAILED: " + error);
                ExitBoxCal();
                return;
            }
            foreach (var v in _vis.Values) { v.Quiet.Clear(); v.ReportN = 0; }
            Log?.Invoke("gallery [boxcal]: " + label + " sent 8 reports");
            ExitBoxCal();
        }

        // Called by the feeder for every gun pointer report, calibrated or not.
        public void OnPointer(GunState g, bool pressed)
        {
            if (!_vis.TryGetValue(g.Serial, out var v))
            {
                int i = _vis.Count;
                v = new GunVis
                {
                    Label = g.Label,
                    Color = i == 0 ? Lime : Color.FromArgb(110, 210, 255),
                };
                _vis[g.Serial] = v;
            }

            v.RawX = g.RawX; v.RawY = g.RawY;
            v.Hz = g.LastIntervalMs > 0 ? 1000.0 / g.LastIntervalMs : 0;
            v.Lock = g.ValidLock;
            v.Seen = DateTime.UtcNow;

            var cal = g.Calib;
            if (cal == null)
            {
                v.HasFix = false;
            }
            else
            {
                var (sx, sy, inside) = cal.Map(g.RawX, g.RawY);
                v.HasFix = true; v.Inside = inside; v.Sx = sx; v.Sy = sy;
                // Noise sample: only on a valid lock (a real on-screen fix),
                // decimated to ~80 Hz so 1.5 s of history fits in 120 entries.
                if (v.Lock && ++v.ReportN % 6 == 0)
                {
                    // In measure mode only samples near the centre mark count,
                    // so relaxing between attempts does not poison the figure.
                    bool near = !_measure ||
                        Dist((float)sx, (float)sy, ClientSize.Width / 2f, ClientSize.Height / 2f) <= 300f;
                    if (near)
                    {
                        v.Quiet.Enqueue(((float)sx, (float)sy));
                        while (v.Quiet.Count > 120) v.Quiet.Dequeue();
                    }
                }
            }
            _live = true;

            if (!pressed) return;
            // Box Cal consumes the trigger: each debounced pull is one tap, and
            // a stray trigger must not score against a target that is not there.
            if (_boxCal) { SampleBoxCal(g); return; }

            var shot = new Shot { Color = v.Color, When = DateTime.UtcNow, HasFix = v.HasFix };
            v.Shots++;
            if (v.HasFix)
            {
                // Same point as the Pico's press frame: the pre-press latch,
                // not the jolted position at the trigger edge.
                var (lx, ly, _) = cal.Map(g.LatchedX, g.LatchedY);
                shot.X = (float)lx;
                shot.Y = (float)ly;
                if (_target != null)
                {
                    double d = Dist(shot.X, shot.Y, _target.X, _target.Y);
                    v.LastErr = d;
                    // One line per scored shot, so calibration error can be
                    // compared from the log (centre vs edges, before vs after).
                    Log?.Invoke("gallery [shot]: " + v.Label + " miss " + d.ToString("F0") +
                        " px " + (d <= _target.R ? "HIT" : "miss") +
                        " target (" + _target.X.ToString("F0") + "," + _target.Y.ToString("F0") +
                        ") shot (" + shot.X.ToString("F0") + "," + shot.Y.ToString("F0") + ")");
                    if (d <= _target.R)
                    {
                        shot.Hit = true;
                        v.Hits++;
                        SpawnTarget();
                    }
                }
            }
            _shots.Add(shot);
            if (_shots.Count > 32) _shots.RemoveAt(0);
        }

        void SpawnTarget()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            // Scale with the screen: a 60 px ring is tiny on a 4K panel.
            float r = Math.Max(70f, Math.Min(w, h) * 0.055f);
            int margin = (int)r + 40;
            if (w < 4 * margin || h < 4 * margin)
            {
                _target = new Target { X = w / 2f, Y = h / 2f, R = r };
                return;
            }
            for (int i = 0; i < 40; i++)
            {
                float x = _rng.Next(margin, w - margin);
                // Keep the top strip (HUD) clear of targets.
                float y = _rng.Next(margin + 110, h - margin);
                if (_target != null && Dist(x, y, _target.X, _target.Y) < r * 3 + 120) continue;
                _target = new Target { X = x, Y = y, R = r };
                return;
            }
            _target = new Target { X = w / 2f, Y = h / 2f, R = r };
        }

        void Tick()
        {
            var now = DateTime.UtcNow;
            bool shotsAlive = false;
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                if ((now - _shots[i].When).TotalMilliseconds > ShotLifeMs) _shots.RemoveAt(i);
                else shotsAlive = true;
            }
            if (_live || shotsAlive)
            {
                _live = false;
                Invalidate();
            }
            else if (_vis.Count == 0 && (now - _lastWaitPaint).TotalMilliseconds > 250)
            {
                // Nothing is reporting: repaint the "waiting / plug a gun in"
                // text a few times a second so its message can age.
                _lastWaitPaint = now;
                Invalidate();
            }
        }

        static double Dist(float ax, float ay, float bx, float by)
        {
            double dx = ax - bx, dy = ay - by;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var gfx = e.Graphics;
            gfx.SmoothingMode = SmoothingMode.AntiAlias;
            gfx.Clear(Bg);

            ScreenBorder.Draw(gfx, ClientSize.Width, ClientSize.Height);
            if (!_boxCal && !_measure) DrawTarget(gfx);
            DrawCentreMark(gfx);
            DrawShots(gfx);
            DrawCrosshairs(gfx);
            DrawHud(gfx);
            // On top of the HUD: the top-left and top-right corner targets sit
            // where the status rows live, and the bright ring must stay legible.
            if (_boxCal) DrawBoxCalTarget(gfx);
        }

        // Fixed centre reference. Always visible (dim) so there is a place to
        // hold; in measure mode it grows and brightens and the target hides, so
        // "hold still" has one unambiguous meaning.
        void DrawCentreMark(Graphics gfx)
        {
            float w = ClientSize.Width, h = ClientSize.Height;
            float cx = w / 2f, cy = h / 2f;
            float k = Math.Max(1f, Math.Min(w, h) / 1200f);
            float r = (_measure ? 46f : 16f) * k;
            var col = _measure ? Color.White : Color.FromArgb(80, 80, 80);
            using (var pen = new Pen(col, _measure ? 4f : 2f))
            {
                gfx.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
                float arm = 22 * k;
                gfx.DrawLine(pen, cx - r - arm, cy, cx - r + 6, cy);
                gfx.DrawLine(pen, cx + r - 6, cy, cx + r + arm, cy);
                gfx.DrawLine(pen, cx, cy - r - arm, cx, cy - r + 6);
                gfx.DrawLine(pen, cx, cy + r - 6, cx, cy + r + arm);
            }
            if (!_measure) return;
            using (var f = new Font("Consolas", 16 * k, FontStyle.Bold))
            using (var b = new SolidBrush(Color.White))
            {
                string s = "HOLD STILL on the centre mark";
                var sz = gfx.MeasureString(s, f);
                gfx.DrawString(s, f, b, cx - sz.Width / 2, cy + r + 26 * k);
            }
        }

        void DrawTarget(Graphics gfx)
        {
            if (_target == null) return;
            using (var pen = new Pen(Amber, 3f))
            {
                gfx.DrawEllipse(pen, _target.X - _target.R, _target.Y - _target.R,
                    _target.R * 2, _target.R * 2);
                gfx.DrawEllipse(pen, _target.X - _target.R * 0.62f, _target.Y - _target.R * 0.62f,
                    _target.R * 1.24f, _target.R * 1.24f);
            }
            using (var pen = new Pen(Color.FromArgb(160, Amber), 2f))
            {
                gfx.DrawLine(pen, _target.X - _target.R * 1.2f, _target.Y,
                    _target.X + _target.R * 1.2f, _target.Y);
                gfx.DrawLine(pen, _target.X, _target.Y - _target.R * 1.2f,
                    _target.X, _target.Y + _target.R * 1.2f);
            }
            using (var b = new SolidBrush(Amber))
                gfx.FillEllipse(b, _target.X - 4, _target.Y - 4, 8, 8);
        }

        // Box Cal target: the box's square target is screenW/7, so its ring has
        // radius screenW/14 and its centre is the same point the report uses.
        void DrawBoxCalTarget(Graphics gfx)
        {
            if (!_boxCal) return;
            var (cx, cy) = GunBoxCal.Centre(_boxPoint, ClientSize.Width, ClientSize.Height);
            float k = Math.Max(1f, Math.Min(ClientSize.Width, ClientSize.Height) / 1200f);
            float r = Math.Max(1f, ClientSize.Width / 14f);
            using (var pen = new Pen(Lime, 4f))
            {
                gfx.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
                gfx.DrawEllipse(pen, cx - r * 0.62f, cy - r * 0.62f,
                    r * 1.24f, r * 1.24f);
                gfx.DrawLine(pen, cx - r - 22 * k, cy, cx + r + 10 * k, cy);
                gfx.DrawLine(pen, cx, cy - r - 22 * k, cx, cy + r + 10 * k);
            }
            using (var b = new SolidBrush(Lime))
                gfx.FillEllipse(b, cx - 4, cy - 4, 8, 8);
            string s = "BOX CAL point " + (_boxPoint + 1) + "/8 tap " + (_boxTap + 1) +
                "/3 — aim at the ring, pull the trigger (Start cancels)";
            using (var f = new Font("Consolas", 16 * k, FontStyle.Bold))
            {
                var sz = gfx.MeasureString(s, f);
                float tx = Math.Max(8, Math.Min(ClientSize.Width - sz.Width - 8,
                    cx - sz.Width / 2));
                float ty = cy < ClientSize.Height / 2
                    ? cy + r + 16 * k
                    : cy - r - sz.Height - 16 * k;
                // The corner targets share space with the HUD; a solid backing
                // keeps the instruction readable over the status rows.
                using (var bg = new SolidBrush(Bg))
                    gfx.FillRectangle(bg, tx - 6, ty - 3, sz.Width + 12, sz.Height + 6);
                using (var b = new SolidBrush(Lime))
                    gfx.DrawString(s, f, b, tx, ty);
            }
        }

        void DrawShots(Graphics gfx)
        {
            var now = DateTime.UtcNow;
            float k = Math.Max(1f, Math.Min(ClientSize.Width, ClientSize.Height) / 1200f);
            foreach (var s in _shots)
            {
                double age = (now - s.When).TotalMilliseconds;
                int alpha = age < 1800 ? 255 : (int)Math.Max(0, 255 - (age - 1800) / (ShotLifeMs - 1800) * 255);
                if (!s.HasFix)
                {
                    // No calibration: cannot place the shot, say so in the HUD.
                    continue;
                }
                using (var pen = new Pen(Color.FromArgb(alpha, s.Color), s.Hit ? 4f : 3f))
                {
                    float r = (s.Hit ? 14 : 10) * k;
                    gfx.DrawLine(pen, s.X - r, s.Y - r, s.X + r, s.Y + r);
                    gfx.DrawLine(pen, s.X - r, s.Y + r, s.X + r, s.Y - r);
                    if (s.Hit)
                        gfx.DrawEllipse(pen, s.X - r - 6, s.Y - r - 6, (r + 6) * 2, (r + 6) * 2);
                }
            }
        }

        void DrawCrosshairs(Graphics gfx)
        {
            var now = DateTime.UtcNow;
            float k = Math.Max(1f, Math.Min(ClientSize.Width, ClientSize.Height) / 1200f);
            foreach (var v in _vis.Values)
            {
                if (!v.HasFix) continue;
                if ((now - v.Seen).TotalMilliseconds > 500) continue;
                var col = v.Inside ? v.Color : Warn;
                using (var pen = new Pen(Color.FromArgb(210, col), 2f))
                {
                    float r = 16 * k;
                    float x = (float)v.Sx, y = (float)v.Sy;
                    gfx.DrawEllipse(pen, x - r, y - r, r * 2, r * 2);
                    gfx.DrawLine(pen, x - r - 10 * k, y, x - r + 2, y);
                    gfx.DrawLine(pen, x + r - 2, y, x + r + 10 * k, y);
                    gfx.DrawLine(pen, x, y - r - 10 * k, x, y - r + 2);
                    gfx.DrawLine(pen, x, y + r - 2, x, y + r + 10 * k);
                }
                using (var f = new Font("Consolas", 13 * k, FontStyle.Bold))
                using (var b = new SolidBrush(col))
                    gfx.DrawString(v.Label, f, b, (float)v.Sx + 22 * k, (float)v.Sy + 14 * k);
            }
        }

        void DrawHud(Graphics gfx)
        {
            // One list of (text, colour) pairs: two parallel lists drifted by one
            // entry the first time and threw IndexOutOfRange mid-paint.
            var rows = new List<(string Text, Color Color)>
            {
                ("trigger = box cal tap · B = measure · C = border · Start = box cal · F = smoothing", Color.White),
                ("G'AIM'E  calibration gallery   ·   ESC to close   ·   V = border " +
                 (ScreenBorder.Enabled ? "ON" : "OFF") + "   ·   smoothing " +
                 (GunState.Smoothing ? "ON" : "OFF") + "   ·   M = measure   ·   K = box cal (unlocked)", Dim),
                ("", Dim),
            };

            if (_vis.Count == 0)
            {
                rows.Add((((DateTime.UtcNow - _opened).TotalSeconds > 1
                    ? "no gun reports - plug a gun in (tray must be feeding)"
                    : "waiting for the first gun report..."), Dim));
            }
            foreach (var v in _vis.Values)
            {
                string line;
                if (!v.HasFix)
                {
                    line = Pad(v.Label) + "  raw " + v.RawX + "," + v.RawY +
                           "   NO CALIBRATION - press Calibrate on the tray";
                }
                else
                {
                    double acc = v.Shots > 0 ? 100.0 * v.Hits / v.Shots : 0;
                    var (rms, maxJump) = Noise(v);
                    line = Pad(v.Label) +
                           "  raw " + v.RawX.ToString().PadLeft(5) + "," + v.RawY.ToString().PadLeft(5) +
                           "   screen " + ((int)v.Sx).ToString().PadLeft(4) + "," + ((int)v.Sy).ToString().PadLeft(4) +
                           (v.Inside ? "  IN " : "  OUT") +
                           "   " + v.Hz.ToString("F0").PadLeft(4) + " Hz" + (v.Lock ? " LOCK" : "     ") +
                           "   noise " + (rms < 0 ? "hold still" :
                               rms.ToString("F1") + " px (max " + maxJump.ToString("F0") + ")") +
                           "   hits " + v.Hits + "/" + v.Shots +
                           (v.Shots > 0 ? " (" + acc.ToString("F0") + "%)" : "") +
                           "   last miss " + (v.LastErr >= 0 ? v.LastErr.ToString("F0") + " px" : "-");
                }
                rows.Add((line, v.HasFix ? v.Color : Warn));
            }

            rows.Add(("", Dim));
            string footer;
            if (_boxCal)
                footer = "BOX CAL point " + (_boxPoint + 1) + "/8 tap " + (_boxTap + 1) +
                    "/3 — aim at the ring, pull the trigger (Start cancels)" +
                    (_boxSerial == null ? "   ·   unlocked: first A picks the gun"
                                        : "   ·   " + _boxLabel);
            else if (_measure)
                footer = "MEASURE - aim at the centre mark and hold still; press B (or M) when done (the figure is written to the log)";
            else
                footer = "aim at the ring, pull the trigger   ·   gun: B = measure, C = border, Start = box cal";
            rows.Add((footer, Dim));

            float k = Math.Max(1f, Math.Min(ClientSize.Width, ClientSize.Height) / 1200f);
            using (var f = new Font("Consolas", 15 * k))
            using (var title = new Font("Consolas", 18 * k, FontStyle.Bold))
            {
                float y = 22 * k;
                for (int i = 0; i < rows.Count; i++)
                {
                    var font = i == 0 ? title : f;
                    using (var b = new SolidBrush(rows[i].Color))
                        gfx.DrawString(rows[i].Text, font, b, 28 * k, y);
                    y += font.Height + 5;
                }
            }

            // Bottom-right: shot legend, sized to the text so it cannot clip.
            string legend = "x = shot     ring = hit     orange = off-screen";
            using (var f = new Font("Consolas", 14 * k))
            {
                var sz = gfx.MeasureString(legend, f);
                float bx = ClientSize.Width - sz.Width - 48;
                float by = ClientSize.Height - sz.Height - 32;
                using (var pen = new Pen(Color.FromArgb(90, Dim), 1f))
                    gfx.DrawRectangle(pen, bx - 16, by - 12, sz.Width + 32, sz.Height + 24);
                using (var b = new SolidBrush(Dim))
                    gfx.DrawString(legend, f, b, bx, by);
            }
        }

        // One log line per state change, carrying the settled numbers for the
        // period that just ended. Without this the whole measurement lives on
        // screen and is lost the moment the window closes.
        void LogNoise(string what)
        {
            var log = Log;
            if (log == null) return;
            string guns;
            if (_vis.Count == 0) guns = "no guns";
            else
            {
                var parts = new List<string>();
                foreach (var v in _vis.Values)
                {
                    var (rms, maxJump) = Noise(v);
                    parts.Add(v.Label + " " + (rms < 0
                        ? "no data (" + v.Quiet.Count + " samples)"
                        : "noise " + rms.ToString("F1") + " px, max jump " +
                          maxJump.ToString("F0") + " px, " + v.Quiet.Count + " samples"));
                }
                guns = string.Join("; ", parts);
            }
            log("gallery [" + what + "]: border " + (ScreenBorder.Enabled ? "ON" : "OFF") +
                ", smoothing " + (GunState.Smoothing ? "ON" : "OFF") + ", " + guns);
        }

        static string Pad(string s) => (s ?? "gun").PadRight(22);

        // Radial RMS spread and largest sample-to-sample jump over the recent
        // stationary window. -1 until there is enough data. This is the number
        // to compare with the border on vs off.
        static (double rms, double maxJump) Noise(GunVis v)
        {
            if (v.Quiet.Count < 30) return (-1, -1);
            double mx = 0, my = 0;
            foreach (var p in v.Quiet) { mx += p.X; my += p.Y; }
            mx /= v.Quiet.Count; my /= v.Quiet.Count;

            double var = 0, maxJump = 0;
            bool first = true;
            float px = 0, py = 0;
            foreach (var p in v.Quiet)
            {
                double dx = p.X - mx, dy = p.Y - my;
                var += dx * dx + dy * dy;
                if (!first)
                {
                    double jx = p.X - px, jy = p.Y - py;
                    maxJump = Math.Max(maxJump, Math.Sqrt(jx * jx + jy * jy));
                }
                first = false; px = p.X; py = p.Y;
            }
            return (Math.Sqrt(var / v.Quiet.Count), maxJump);
        }
    }
}
