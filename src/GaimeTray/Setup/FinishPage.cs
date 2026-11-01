using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using GaimeFeeder;

namespace GaimeTray
{
    sealed class FinishPage : WizardPage
    {
        public override string Title => "Finish";

        readonly Label _summary;
        readonly CheckBox _autostart;

        public FinishPage(SetupWizard w) : base(w)
        {
            _summary = Ui.Text("", 0, 0, 650, 260, null, 10);
            _summary.Font = new Font("Consolas", 10);
            _autostart = new CheckBox
            {
                Text = "Start GaimeTray with Windows", Location = new Point(0, 272), AutoSize = true,
                ForeColor = Ui.Fg, BackColor = Ui.Bg,
            };
            _autostart.CheckedChanged += (s, e) => Autostart.Set(_autostart.Checked);
            Controls.Add(_summary);
            Controls.Add(_autostart);
            Controls.Add(Ui.Text(
                "Finish closes the wizard; the tray keeps feeding. Reopen it any time from the tray menu (Setup…) " +
                "or the Log tab. Rerun the host grid after any Box Cal, stance change or resolution change.",
                0, 306, 650, 54, Ui.Dim));
        }

        public override void ShowPage()
        {
            _autostart.Checked = Autostart.IsOn();
            Tick();
        }

        public override void Tick()
        {
            var lines = new List<string>();
            foreach (var c in SystemChecks.All(Wizard.Engine))
                lines.Add(Ui.Glyph(c.State) + " " + c.Name.PadRight(18) + c.Detail);
            lines.Add("");
            var snap = Wizard.Engine.Snapshot();
            if (snap.Count == 0) lines.Add("! no guns attached");
            foreach (var g in snap)
            {
                string state = !g.Calibrated ? "✖ not calibrated" : g.CalProblem != null ? "! " + g.CalProblem : "✔ calibrated";
                lines.Add(("P" + g.PadIndex + " " + g.Label).PadRight(28) + state);
            }
            _summary.Text = string.Join("\n", lines);
        }
    }
}
