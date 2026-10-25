using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GaimeFeeder
{
    // Fullscreen host calibration: a 3x3 grid of targets, 3 trigger pulls per
    // target (27 samples), least-squares fit per axis.
    //
    // The grid sits at the Box Cal positions (W/14 in from each edge, 274 px
    // at 4K) on purpose. The old 5-point put its corners 60 px from the edges,
    // where the gun's raw output clamps at 99/9900 or compresses after Box
    // Cal; those corner samples read too far inward and made the fit 2-9% too
    // wide (2026-10-24 data, docs/calibration-analysis-2026-10-24.md).
    sealed class CalibrationWindow : Form
    {
        public const int PullsPerTarget = 3;

        string _serial;
        string _display;
        Func<(int x, int y)> _latestRaw;
        readonly List<Point> _targets = new List<Point>();
        readonly List<(int x, int y)> _samples = new List<(int x, int y)>();
        readonly List<Point> _sampleTargets = new List<Point>();
        int _idx, _pull;

        // One line for the tray log: samples used and the fit residual.
        public string FitInfo { get; private set; } = "";
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

        // Keep the instructions and Cancel button clear of the reference border
        // and the targets: instructions along the bottom edge, Cancel at the
        // top edge. The grid is W/14 in from every edge, so both stay clear.
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
            int m = (int)Math.Round(w / 14.0);
            int[] xs = { m, w / 2, w - m };
            int[] ys = { m, h / 2, h - m };
            _targets.Clear();
            // Snake order (row 1 left->right, row 2 right->left, row 3
            // left->right) keeps every move to the next target short.
            for (int r = 0; r < 3; r++)
                for (int k = 0; k < 3; k++)
                    _targets.Add(new Point(xs[r == 1 ? 2 - k : k], ys[r]));
            _idx = 0; _pull = 0; _samples.Clear(); _sampleTargets.Clear();
            UpdateLabel();
            Invalidate();
        }

        void UpdateLabel()
        {
            string where = $"target {_idx + 1}/{_targets.Count}, pull {_pull + 1}/{PullsPerTarget}";
            string tail = $"   (B = border {(ScreenBorder.Enabled ? "ON" : "OFF")}, ESC cancels)";
            _label.Text = _serial == null
                ? $"Aim EITHER gun at {where}{tail}"
                : $"{_display} : aim at {where}{tail}";
        }

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
            _sampleTargets.Add(_targets[_idx]);
            if (++_pull >= PullsPerTarget) { _pull = 0; _idx++; }
            if (_idx >= _targets.Count) Finish();
            else { UpdateLabel(); Invalidate(); }
        }

        // Per axis, fit pixel = a * raw + b by least squares through all
        // samples at their targets' real pixel positions, then drop outliers
        // (2-D residual > max(80 px, 3 x median)) and fit again. Extrapolate to
        // the screen edges, so the stored RawMin/RawMax keep their meaning (raw
        // at pixel 0 and at pixel W-1) and RectCalib.Map() and the
        // cal_<sn>.json format stay the same.
        void Finish()
        {
            int w = Screen.PrimaryScreen.Bounds.Width, h = Screen.PrimaryScreen.Bounds.Height;
            int n = _samples.Count;
            var keep = new bool[n];
            for (int i = 0; i < n; i++) keep[i] = true;
            var (ax, bx, ay, by) = FitKept(keep);
            var res = Residuals(ax, bx, ay, by);
            double thr = Math.Max(80.0, 3.0 * Median(res, keep));
            int kept = 0;
            for (int i = 0; i < n; i++) { keep[i] = res[i] <= thr; if (keep[i]) kept++; }
            if (kept < n && kept >= Math.Max(12, (int)Math.Ceiling(0.7 * n)))
            {
                (ax, bx, ay, by) = FitKept(keep);
                res = Residuals(ax, bx, ay, by);
            }
            else
            {
                for (int i = 0; i < n; i++) keep[i] = true;
                kept = n;
            }
            FitInfo = $"{n} samples, {kept} kept, median residual {Median(res, keep):F0} px";
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

        (double ax, double bx, double ay, double by) FitKept(bool[] keep)
        {
            var rx = new List<double>(); var ry = new List<double>();
            var px = new List<double>(); var py = new List<double>();
            for (int i = 0; i < _samples.Count; i++)
            {
                if (!keep[i]) continue;
                rx.Add(_samples[i].x); ry.Add(_samples[i].y);
                px.Add(_sampleTargets[i].X); py.Add(_sampleTargets[i].Y);
            }
            var (ax, bx) = Fit(rx.ToArray(), px.ToArray());
            var (ay, by) = Fit(ry.ToArray(), py.ToArray());
            return (ax, bx, ay, by);
        }

        double[] Residuals(double ax, double bx, double ay, double by)
        {
            var r = new double[_samples.Count];
            for (int i = 0; i < r.Length; i++)
            {
                double dx = ax * _samples[i].x + bx - _sampleTargets[i].X;
                double dy = ay * _samples[i].y + by - _sampleTargets[i].Y;
                r[i] = Math.Sqrt(dx * dx + dy * dy);
            }
            return r;
        }

        static double Median(double[] v, bool[] keep)
        {
            var s = new List<double>();
            for (int i = 0; i < v.Length; i++) if (keep[i]) s.Add(v[i]);
            if (s.Count == 0) return 0;
            s.Sort();
            int m = s.Count / 2;
            return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
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
