using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using GaimeFeeder;

namespace GaimeTray
{
    // RetroBat as the frontend the tray starts: its folder, sharing the
    // TeknoParrot install, its emulators on the bridge list, and a RetroBat
    // entry that opens this wizard. (It used to sit at the bottom of the Games
    // page, below the fold, where it was easy to miss.)
    sealed class RetroBatPage : WizardPage
    {
        public override string Title => "RetroBat";

        readonly TextBox _dir;
        readonly Label _state, _result;

        public RetroBatPage(SetupWizard w) : base(w)
        {
            Controls.Add(Ui.Text(
                "Optional. The tray can start RetroBat instead of one game (pick \"RetroBat (frontend)\" on the Auto-start page " +
                "or in the main window's game bar); RetroBat then launches the games and the tray keeps feeding the guns.",
                0, 0, 650, 54, Ui.Dim));

            Controls.Add(Ui.Text("RetroBat folder", 0, 62, 300, 22, null, 10, true));
            _dir = Ui.Box(w.Config.RetroBatDir, 0, 88, 440);
            var browse = Ui.Btn("Browse…", 450, 85, 90);
            browse.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = "RetroBat.exe|RetroBat.exe", Title = "Find RetroBat.exe" })
                    if (dlg.ShowDialog(this) == DialogResult.OK) _dir.Text = Path.GetDirectoryName(dlg.FileName);
                Refresh_();
            };
            var find = Ui.Btn("Find", 548, 85, 90);
            find.Click += (s, e) => { _dir.Text = SetupConfig.DetectRetroBat() ?? _dir.Text; Refresh_(); };
            _dir.Leave += (s, e) => Refresh_();
            _state = Ui.Text("", 0, 120, 650, 40, Ui.Dim);
            Controls.AddRange(new Control[] { _dir, browse, find, _state });

            int y = 170;
            Step(y, "1 · Share TeknoParrot", "Link TeknoParrot",
                "Makes RetroBat\\emulators\\teknoparrot a link to the TeknoParrot folder on the Games page, so RetroBat uses the same " +
                "install: same profiles, gun bindings and borders.",
                () => RetroBat.LinkTeknoParrot(_dir.Text.Trim(), Wizard.Config.TeknoParrotDir));
            Step(y + 80, "2 · Let the guns aim in its emulators", "Add emulators",
                "Adds every program under RetroBat\\emulators to the list of games the bridge sends aim to.",
                () =>
                {
                    var names = RetroBat.EmulatorProcesses(_dir.Text.Trim());
                    int n = PicoOutput.EnsureGames(names);
                    return n + " new emulator programs added (" + names.Count + " found)";
                });
            Step(y + 160, "3 · Open this setup from RetroBat", "Add Setup entry",
                "Adds \"GaimeTray Setup\" to RetroBat's Windows games. Update RetroBat's game lists to see it; launching it opens this " +
                "wizard in front of RetroBat, and RetroBat waits until it is closed.",
                () => RetroBat.AddSetupEntry(_dir.Text.Trim()));

            _result = Ui.Text("", 0, y + 244, 650, 40, Ui.Lime);
            Controls.Add(_result);
        }

        void Step(int y, string title, string button, string help, Func<string> action)
        {
            Controls.Add(Ui.Text(title, 0, y, 480, 22, null, 10, true));
            Controls.Add(Ui.Text(help, 0, y + 24, 480, 48, Ui.Dim));
            var b = Ui.Btn(button, 500, y + 18, 138);
            b.Click += (s, e) =>
            {
                Save();
                try
                {
                    string msg = action();
                    Wizard.Engine.Note("setup: RetroBat " + msg);
                    _result.ForeColor = Ui.Lime;
                    _result.Text = Ui.Glyph(Check.Ok) + "  " + msg;
                }
                catch (Exception ex)
                {
                    _result.ForeColor = Ui.Red;
                    _result.Text = Ui.Glyph(Check.Fail) + "  " + ex.Message;
                }
                Refresh_();
            };
            Controls.Add(b);
        }

        public override void ShowPage()
        {
            if (_dir.Text.Trim().Length == 0) _dir.Text = SetupConfig.DetectRetroBat() ?? "";
            Refresh_();
        }

        void Save()
        {
            Wizard.Config.RetroBatDir = _dir.Text.Trim();
            try { Wizard.Config.Save(); } catch { }
        }

        void Refresh_()
        {
            Save();
            string dir = Wizard.Config.RetroBatDir;
            if (dir.Length == 0) Set(Check.Info, "not set — press Find, or skip this page if you do not use RetroBat");
            else if (!RetroBat.Valid(dir)) Set(Check.Fail, "no RetroBat.exe in that folder");
            else
            {
                var tpIn = RetroBat.TeknoParrotDir(dir);
                var link = new DirectoryInfo(Path.Combine(dir, "emulators", "teknoparrot"));
                Set(Check.Ok, tpIn == null ? "found — no TeknoParrot in it yet: step 1 links yours"
                    : link.Exists && link.LinkTarget != null ? "found — TeknoParrot linked to " + link.LinkTarget
                    : "found — it has its own TeknoParrot copy (" + tpIn + "); bindings and borders there are separate");
            }
        }

        void Set(Check c, string text)
        {
            _state.ForeColor = Ui.Tone(c);
            _state.Text = Ui.Glyph(c) + "  " + text;
        }
    }
}
