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
            MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) TriggerSample(); };
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
        // Time-gated: one physical pull produces TWO events (debounced tip edge
        // + the Windows touch-click from the digitizer acting as a touchscreen),
        // plus tip flutter re-edges. Accept at most one sample per 750 ms.
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

        void Finish()
        {
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            foreach (var s in _samples)
            {
                minX = Math.Min(minX, s.x); maxX = Math.Max(maxX, s.x);
                minY = Math.Min(minY, s.y); maxY = Math.Max(maxY, s.y);
            }
            Result = new RectCalib
            {
                RawMinX = minX, RawMaxX = maxX, RawMinY = minY, RawMaxY = maxY,
                ScreenW = Screen.PrimaryScreen.Bounds.Width,
                ScreenH = Screen.PrimaryScreen.Bounds.Height,
                InvertY = false
            };
            DialogResult = DialogResult.OK;
            Close();
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
