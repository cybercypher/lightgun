using System.Drawing;
using System.Windows.Forms;

namespace GaimeTray
{
    // One gun, live status card. Updated by MainForm's 10 Hz timer.
    sealed class GunCard : UserControl
    {
        readonly Label _title, _sn, _status, _cal, _pos, _rate;
        readonly Label _trig, _a, _b, _coin, _start;
        readonly Button _calBtn, _pulseBtn;
        public string Serial { get; private set; }
        public event System.Action<string> Calibrate, Pulse;

        static readonly Color Bg = Color.FromArgb(32, 33, 36);
        static readonly Color Fg = Color.FromArgb(230, 230, 230);
        static readonly Color Dim = Color.FromArgb(140, 140, 140);
        static readonly Color Lime = Color.FromArgb(140, 255, 120);
        static readonly Color Red = Color.FromArgb(255, 110, 110);

        public GunCard()
        {
            BackColor = Bg;
            Size = new Size(360, 196);
            Font = new Font("Segoe UI", 9);
            _title = Mk("", 13, true, 12, 8);
            _sn = Mk("", 9, false, 12, 34); _sn.ForeColor = Dim;
            _status = Mk("", 9, true, 220, 10);
            _cal = Mk("", 9, false, 12, 56); _cal.ForeColor = Dim;
            _pos = Mk("", 10, false, 12, 78); _pos.Font = new Font("Consolas", 10);
            _rate = Mk("", 9, false, 12, 100); _rate.ForeColor = Dim;
            var pills = new FlowLayoutPanel
            {
                Location = new Point(12, 122), Size = new Size(336, 28),
                BackColor = Bg, FlowDirection = FlowDirection.LeftToRight,
            };
            _trig = Pill("TRIG"); _a = Pill("A"); _b = Pill("B");
            _coin = Pill("COIN"); _start = Pill("START");
            pills.Controls.AddRange(new Control[] { _trig, _a, _b, _coin, _start });
            Controls.Add(pills);
            _calBtn = Btn("Calibrate", 12, 154);
            _calBtn.Click += (s, e) => Calibrate?.Invoke(Serial);
            _pulseBtn = Btn("Pulse trigger", 132, 154);
            _pulseBtn.Click += (s, e) => Pulse?.Invoke(Serial);
            Controls.AddRange(new Control[] { _calBtn, _pulseBtn });
        }

        Label Mk(string t, float size, bool bold, int x, int y)
        {
            var l = new Label
            {
                Text = t, Location = new Point(x, y), AutoSize = true,
                ForeColor = Fg, BackColor = Bg,
                Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            };
            Controls.Add(l);
            return l;
        }

        static Label Pill(string t)
        {
            return new Label
            {
                Text = t, AutoSize = false, Size = new Size(60, 24),
                TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 0, 6, 0),
                BackColor = Color.FromArgb(50, 50, 52), ForeColor = Dim,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
            };
        }

        static Button Btn(string t, int x, int y)
        {
            return new Button
            {
                Text = t, Location = new Point(x, y), Size = new Size(110, 30),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(50, 50, 52),
                ForeColor = Fg, Font = new Font("Segoe UI", 9),
            };
        }

        static void Lit(Label p, bool on, bool red)
        {
            p.BackColor = on ? (red ? Red : Lime) : Color.FromArgb(50, 50, 52);
            p.ForeColor = on ? Color.Black : Dim;
        }

        public void Show(GunSnapshot g)
        {
            Serial = g.Serial;
            _title.Text = g.Label;
            _sn.Text = "sn " + g.Serial + "  ·  P" + g.PadIndex;
            bool live = g.AgeMs < 3000;
            _status.Text = live ? "● LIVE" : "○ QUIET";
            _status.ForeColor = live ? Lime : Dim;
            _cal.Text = g.Calibrated ? "calibrated" : "NOT calibrated — press Calibrate";
            _cal.ForeColor = g.Calibrated ? Dim : Red;
            _pos.Text = "X " + g.RawX.ToString().PadLeft(5) + "  Y " + g.RawY.ToString().PadLeft(5);
            _rate.Text = g.RateHz.ToString("F0").PadLeft(4) + " Hz";
            Lit(_trig, g.TriggerDown, true);
            Lit(_a, g.BtnA, false); Lit(_b, g.BtnB, false);
            Lit(_coin, g.BtnCoin, false); Lit(_start, g.BtnStart, false);
        }
    }
}
