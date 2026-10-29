using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using GaimeFeeder;

namespace GaimeTray
{
    // Per gun: Box Cal (inside the gun) -> host grid (cal_<sn>.json) -> test
    // shots. The order is the one from SETUP.md §4; CalStatus flags a grid
    // that is older than the last Box Cal or was made at another resolution.
    sealed class CalibratePage : WizardPage
    {
        public override string Title => "Calibrate";

        sealed class Row
        {
            public string Serial;
            public Label Name, Box, Grid, State;
            public Button BoxBtn, GridBtn, TestBtn;
        }

        readonly Panel _list;
        readonly Label _empty, _border;
        readonly Button _borderBtn;
        readonly List<Row> _rows = new List<Row>();
        string _shape = "";

        public CalibratePage(SetupWizard w) : base(w)
        {
            Controls.Add(Ui.Text(
                "Do each gun in this order. 1 · Box Cal: aim at each of the 8 lime targets and pull 3 times (Esc cancels). " +
                "2 · Host grid: 9 targets, 3 pulls each, standing where you will play. 3 · Test: shoot 20–40 shots; " +
                "the log shows the miss, under ~60 px at 4K is good. A Box Cal always needs a new host grid after it.",
                0, 0, 650, 72, Ui.Dim));
            _border = Ui.Text("", 0, 84, 500, 24, null, 9, true);
            _borderBtn = Ui.Btn("", 508, 78, 130);
            _borderBtn.Click += (s, e) => { Wizard.SetBorder(!ScreenBorder.Mode); Tick(); };
            _list = new Panel { Location = new Point(0, 120), Size = new Size(650, 330), BackColor = Ui.Bg };
            _empty = Ui.Text("", 0, 120, 650, 40, Ui.Amber);
            Controls.AddRange(new Control[] { _border, _borderBtn, _list, _empty });
        }

        public override void ShowPage() => Tick();

        public override void Tick()
        {
            var eng = Wizard.Engine;
            _border.Text = "White border: " + (ScreenBorder.Mode ? "ON" : "OFF") +
                "  ·  changing it needs a full recalibration of every gun";
            _border.ForeColor = ScreenBorder.Mode ? Ui.Lime : Ui.Amber;
            _borderBtn.Text = ScreenBorder.Mode ? "Turn border off" : "Turn border on";
            if (!eng.Feeding)
            {
                _empty.Text = "The tray is stopped. Press Start in the main window; calibration reads the guns through it.";
                _empty.Visible = true;
                _list.Visible = false;
                return;
            }
            var snap = eng.Snapshot();
            if (snap.Count == 0)
            {
                _empty.Text = "No guns attached yet — plug one in; it attaches within ~10 s.";
                _empty.Visible = true;
                _list.Visible = false;
                return;
            }
            _empty.Visible = false;
            _list.Visible = true;

            string shape = string.Join("|", snap.Select(g => g.Serial + ":" + g.PadIndex));
            if (shape != _shape) { Rebuild(snap); _shape = shape; }

            foreach (var g in snap)
            {
                var r = _rows.FirstOrDefault(x => x.Serial == g.Serial);
                if (r == null) continue;
                var cal = RectCalib.Load(g.Serial);
                var box = CalStatus.BoxCalUtc(g.Serial);
                r.Name.Text = g.Label + "  ·  P" + g.PadIndex + (g.AgeMs < 3000 ? "  ·  live" : "  ·  quiet");
                r.Box.Text = "Box Cal: " + (box.HasValue ? "sent " + Ui.When(box) : "not sent from this PC");
                r.Grid.Text = "Host grid: " + (cal == null ? "missing"
                    : Ui.When(CalStatus.GridUtc(g.Serial, cal)) +
                      (string.IsNullOrEmpty(cal.FitInfo) ? "" : "  ·  " + cal.FitInfo));
                string problem = CalStatus.GridProblem(g.Serial, cal);
                string boxProblem = CalStatus.BoxCalProblem(g.Serial);
                if (boxProblem != null) Set(r.State, Check.Fail, "full recalibration: " + boxProblem + " — next: Box Cal");
                else if (cal == null && !box.HasValue) Set(r.State, Check.Fail, "next: Box Cal");
                else if (cal == null) Set(r.State, Check.Fail, "next: host grid");
                else if (problem != null)
                    Set(r.State, Check.Warn, problem.Contains("redo") ? problem : problem + " — redo the host grid");
                else if (!box.HasValue) Set(r.State, Check.Ok, "calibrated (Box Cal done before this PC kept track)");
                else Set(r.State, Check.Ok, "ready — test it");
            }
        }

        static void Set(Label l, Check c, string text)
        {
            l.ForeColor = Ui.Tone(c);
            l.Text = Ui.Glyph(c) + "  " + text;
        }

        void Rebuild(List<GunSnapshot> snap)
        {
            _list.Controls.Clear();
            _rows.Clear();
            int y = 0;
            foreach (var g in snap)
            {
                string sn = g.Serial;
                var card = new Panel { Location = new Point(0, y), Size = new Size(650, 150), BackColor = Ui.Panel };
                var r = new Row
                {
                    Serial = sn,
                    Name = Ui.Text("", 12, 8, 620, 24, null, 11, true),
                    Box = Ui.Text("", 12, 36, 620, 20, Ui.Dim),
                    Grid = Ui.Text("", 12, 56, 620, 20, Ui.Dim),
                    State = Ui.Text("", 12, 78, 620, 22, null, 9, true),
                    BoxBtn = Ui.Btn("1 · Box Cal", 12, 108, 130),
                    GridBtn = Ui.Btn("2 · Host grid", 152, 108, 130),
                    TestBtn = Ui.Btn("3 · Test", 292, 108, 130),
                };
                r.BoxBtn.Click += (s, e) =>
                {
                    if (!Wizard.Engine.OpenBoxCal(sn))
                        MessageBox.Show(this, "That gun is not attached.", "Box Cal");
                };
                r.GridBtn.Click += (s, e) => Wizard.Engine.Recalibrate(sn);
                r.TestBtn.Click += (s, e) => Wizard.Engine.OpenGallery();
                card.Controls.AddRange(new Control[] { r.Name, r.Box, r.Grid, r.State, r.BoxBtn, r.GridBtn, r.TestBtn });
                _list.Controls.Add(card);
                _rows.Add(r);
                y += 162;
            }
        }
    }
}
