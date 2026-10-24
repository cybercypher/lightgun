using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GaimeFeeder
{
    // Fullscreen 5-point calibration (corners + center), Guncon3Windows-style.
    // Aim the named gun at each target and pull the trigger.
    sealed class CalibrationWindow : Form
    {
        string _serial;
        string _display;
        Func<(int x, int y)> _latestRaw;
        readonly List<Point> _targets = new List<Point>();
        readonly List<(int x, int y)> _samples = new List<(int x, int y)>();
        int _idx;
        readonly Label _label;
        readonly Button _cancel;

        public RectCalib Result { get; private set; }

        public CalibrationWindow(string display, Func<(int x, int y)> latestRaw)
        {
            _display = display; _latestRaw = latestRaw;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            BackColor = Color.Black;
            KeyPreview = true;
            _label = new Label
            {
                ForeColor = Color.White,
                Font = new Font("Consolas", 20),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Add(_label);
            _cancel = new Button
            {
                Text = "Cancel (ESC)",
                Font = new Font("Consolas", 12),
                Size = new Size(160, 40),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
            };
            _cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_cancel);
            // No mouse-click sampling: a click carries no gun position (the
            // sample source is the gun's pre-press latch), and with
            // TouchGate=0 the gun no longer produces Windows touch clicks.
            Shown += (s, e) => { Rebuild(); LayoutChrome(); Activate(); };
        }

        // Keep the instructions and Cancel button clear of both the reference
        // border and the five targets: instructions along the bottom centre
        // (narrower than the screen, so the bottom corner targets stay clear),
        // Cancel at the top centre (nothing but targets in the corners there).
        void LayoutChrome()
        {
            if (_label == null || _cancel == null) return;
            int w = ClientSize.Width, h = ClientSize.Height;
            int t = ScreenBorder.Thickness(w, h);
            int lw = Math.Min(w, Math.Max(320, (int)(w * 0.62)));
            _label.Bounds = new Rectangle((w - lw) / 2, h - t - 78, lw, 64);
            _cancel.Bounds = new Rectangle((w - 160) / 2, t + 16, 160, 40);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChrome();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            // Same shared switch as the tray gallery: the gun must see the same
            // reference while calibrating as it will in play.
            if (keyData == Keys.B)
            {
                ScreenBorder.Enabled = !ScreenBorder.Enabled;
                UpdateLabel();
                Invalidate();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Rebuild()
        {
            int w = Screen.PrimaryScreen.Bounds.Width, h = Screen.PrimaryScreen.Bounds.Height;
            _targets.Clear();
            _targets.Add(new Point(60, 60));
            _targets.Add(new Point(w - 60, 60));
            _targets.Add(new Point(w - 60, h - 60));
            _targets.Add(new Point(60, h - 60));
            _targets.Add(new Point(w / 2, h / 2));
            _idx = 0; _samples.Clear();
            UpdateLabel();
            Invalidate();
        }

        void UpdateLabel() =>
            _label.Text = _serial == null
                ? $"Aim EITHER gun at target {_idx + 1}/5, pull its trigger   (B = border {(ScreenBorder.Enabled ? "ON" : "OFF")}, ESC cancels)"
                : $"{_display} : aim at target {_idx + 1}/5, pull trigger   (B = border {(ScreenBorder.Enabled ? "ON" : "OFF")}, ESC cancels)";

        // First trigger locks the window to whichever gun was used.
        public void LockTo(string serial, string display, Func<(int x, int y)> latest)
        {
            _serial = serial; _display = display; _latestRaw = latest;
            UpdateLabel();
        }

        // Called by the feeder when this gun's debounced trigger fires.
        // Time-gated against tip flutter re-edges: accept at most one sample
        // per 750 ms.
        DateTime _lastSample = DateTime.MinValue;
        public void TriggerSample()
        {
            if ((DateTime.UtcNow - _lastSample).TotalMilliseconds < 750) return;
            _lastSample = DateTime.UtcNow;
            TakeSample();
        }

        void TakeSample()
        {
            if (_idx >= _targets.Count) return;
            var (x, y) = _latestRaw();
            _samples.Add((x, y));
            _idx++;
            if (_idx >= _targets.Count) Finish();
            else { UpdateLabel(); Invalidate(); }
        }

        // Per axis, fit pixel = a * raw + b by least squares through all five
        // targets (centre included), at their real pixel positions. Then
        // extrapolate to the screen edges, so the stored RawMin/RawMax keep
        // their meaning (raw at pixel 0 and at pixel W-1) and RectCalib.Map()
        // and the cal_<sn>.json format stay the same.
        void Finish()
        {
            int w = Screen.PrimaryScreen.Bounds.Width, h = Screen.PrimaryScreen.Bounds.Height;
            int n = _samples.Count;
            var rx = new double[n]; var ry = new double[n];
            var px = new double[n]; var py = new double[n];
            for (int i = 0; i < n; i++)
            {
                rx[i] = _samples[i].x; ry[i] = _samples[i].y;
                px[i] = _targets[i].X; py[i] = _targets[i].Y;
            }
            var (ax, bx) = Fit(rx, px);
            var (ay, by) = Fit(ry, py);
            // Raw grows to the right and down on these guns (InvertY = false).
            // A slope that is not finite and positive means bad samples: return
            // an empty result, which IsValid rejects ("bad samples, skipped").
            if (!(double.IsFinite(ax) && ax > 0 && double.IsFinite(ay) && ay > 0))
                Result = new RectCalib { ScreenW = w, ScreenH = h };
            else
                Result = new RectCalib
                {
                    RawMinX = (0 - bx) / ax, RawMaxX = (w - 1 - bx) / ax,
                    RawMinY = (0 - by) / ay, RawMaxY = (h - 1 - by) / ay,
                    ScreenW = w, ScreenH = h,
                    InvertY = false
                };
            DialogResult = DialogResult.OK;
            Close();
        }

        // Least-squares line px = a * raw + b.
        static (double a, double b) Fit(double[] raw, double[] px)
        {
            int n = raw.Length;
            double mr = 0, mp = 0;
            for (int i = 0; i < n; i++) { mr += raw[i]; mp += px[i]; }
            mr /= n; mp /= n;
            double sxy = 0, sxx = 0;
            for (int i = 0; i < n; i++)
            {
                sxy += (raw[i] - mr) * (px[i] - mp);
                sxx += (raw[i] - mr) * (raw[i] - mr);
            }
            double a = sxy / sxx;
            return (a, mp - a * mr);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // Reference border first, target on top of it.
            ScreenBorder.Draw(e.Graphics, ClientSize.Width, ClientSize.Height);
            if (_idx < _targets.Count)
            {
                var p = _targets[_idx];
                using (var pen = new Pen(Color.Lime, 4))
                {
                    e.Graphics.DrawEllipse(pen, p.X - 25, p.Y - 25, 50, 50);
                    e.Graphics.DrawLine(pen, p.X - 40, p.Y, p.X + 40, p.Y);
                    e.Graphics.DrawLine(pen, p.X, p.Y - 40, p.X, p.Y + 40);
                }
            }
        }
    }
}
