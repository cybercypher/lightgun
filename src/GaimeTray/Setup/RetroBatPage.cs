using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GaimeFeeder;

namespace GaimeTray
{
    // RetroBat as the frontend the tray starts. One button makes RetroBat list
    // exactly the TeknoParrot games bound to the guns, with the guns working in
    // them (RetroBat.SyncTeknoParrotGames); binding a game on the Games page
    // runs the same sync. (It used to sit at the bottom of the Games page,
    // below the fold, where it was easy to miss.)
    sealed class RetroBatPage : WizardPage
    {
        public override string Title => "RetroBat";

        readonly TextBox _dir;
        readonly Label _state, _result;
        readonly ListView _games;
        readonly CheckBox _kiosk;

        public RetroBatPage(SetupWizard w) : base(w)
        {
            Controls.Add(Ui.Text(
                "Optional. RetroBat lists your gun games and launches them; the tray keeps feeding the guns. Bind games on the " +
                "Games page, then press Set up RetroBat. Pick \"RetroBat (frontend)\" in the main window or on Auto-start to start it.",
                0, 0, 650, 54, Ui.Dim));

            Controls.Add(Ui.Text("RetroBat folder", 0, 60, 300, 22, null, 10, true));
            _dir = Ui.Box(w.Config.RetroBatDir, 0, 86, 440);
            var browse = Ui.Btn("Browse…", 450, 83, 90);
            browse.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = "RetroBat.exe|RetroBat.exe", Title = "Find RetroBat.exe" })
                    if (dlg.ShowDialog(this) == DialogResult.OK) _dir.Text = Path.GetDirectoryName(dlg.FileName);
                Refresh_();
            };
            var find = Ui.Btn("Find", 548, 83, 90);
            find.Click += (s, e) => { _dir.Text = SetupConfig.DetectRetroBat() ?? _dir.Text; Refresh_(); };
            _dir.Leave += (s, e) => Refresh_();
            _state = Ui.Text("", 0, 116, 650, 22, Ui.Dim);
            Controls.AddRange(new Control[] { _dir, browse, find, _state });

            var sync = Ui.Btn("Set up RetroBat for my bound games", 0, 146, 300);
            sync.Click += (s, e) => Sync(true);
            new ToolTip().SetToolTip(sync, "Links RetroBat's TeknoParrot to yours, lists every gun-bound TeknoParrot game in RetroBat " +
                "(roms\\teknoparrot, as links — no copies), puts each game on the bridge list and adds the GaimeTray Setup entry.");
            _games = new ListView
            {
                Location = new Point(0, 184), Size = new Size(638, 120), View = View.Details, FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, BackColor = Ui.Panel, ForeColor = Ui.Fg, BorderStyle = BorderStyle.None,
            };
            _games.Columns.Add("Game", 220);
            _games.Columns.Add("In RetroBat", 200);
            _games.Columns.Add("Guns", 90);
            _games.Columns.Add("Border", 110);
            _result = Ui.Text("", 0, 310, 650, 54, Ui.Lime);
            Controls.AddRange(new Control[] { sync, _games, _result });

            _kiosk = new CheckBox
            {
                Text = "Kiosk mode (players can't change RetroBat's settings; leave it with A A A B A)", Location = new Point(0, 366),
                AutoSize = true, ForeColor = Ui.Fg, BackColor = Ui.Bg,
            };
            _kiosk.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                Save();
                try { Say(Check.Ok, RetroBat.SetKiosk(Wizard.Config.RetroBatDir, _kiosk.Checked)); }
                catch (Exception ex) { Say(Check.Fail, ex.Message); }
            };
            Controls.Add(_kiosk);
            Controls.Add(Ui.Text("With the guns in RetroBat's menu: shoot the left/right edge to move, the top/bottom edge for up/down, " +
                "the centre to launch. A launches, B goes back, Start opens the menu, Coin is Select. Update RetroBat's game lists after " +
                "setting up; set the carousel under Select → Gamelist view style (depends on the theme).", 0, 392, 650, 54, Ui.Dim));

            Controls.Add(Ui.Text("Other systems (optional)", 0, 452, 400, 22, null, 10, true));
            var emus = Ui.Btn("Add emulators to bridge", 0, 476, 200);
            emus.Click += (s, e) =>
            {
                Save();
                var names = RetroBat.EmulatorProcesses(_dir.Text.Trim());
                try { Say(Check.Ok, PicoOutput.EnsureGames(names) + " new emulator programs added (" + names.Count + " found)"); }
                catch (Exception ex) { Say(Check.Fail, ex.Message); }
            };
            Controls.Add(Ui.Text("Only for non-TeknoParrot games (MAME, Dolphin…): lets the guns aim in RetroBat's other emulators. " +
                "Those emulators still need their own gun settings.", 210, 476, 440, 48, Ui.Dim));
            Controls.Add(emus);
        }

        bool _loading;

        public override void ShowPage()
        {
            if (_dir.Text.Trim().Length == 0) _dir.Text = SetupConfig.DetectRetroBat() ?? "";
            _loading = true;
            _kiosk.Checked = RetroBat.Valid(_dir.Text.Trim()) && RetroBat.KioskOn(_dir.Text.Trim());
            _loading = false;
            Refresh_();
        }

        // Also called after Bind to guns (quiet: no message unless it fails).
        public void Sync(bool loud)
        {
            Save();
            try
            {
                var notes = new System.Collections.Generic.List<string>();
                var lines = RetroBat.SyncTeknoParrotGames(Wizard.Config.RetroBatDir, Wizard.Config.TeknoParrotDir, notes.Add);
                foreach (var n in notes) Wizard.Engine.Note("setup: RetroBat " + n);
                _games.Items.Clear();
                foreach (var l in lines)
                {
                    var item = new ListViewItem(new[] { l.Game, l.RetroBat, l.Guns, l.Border }) { ForeColor = l.Ok ? Ui.Lime : Ui.Amber };
                    _games.Items.Add(item);
                }
                int ok = lines.Count(l => l.Ok);
                Wizard.Engine.Note("setup: RetroBat lists " + ok + " of " + lines.Count + " bound games");
                if (loud || ok < lines.Count)
                    Say(ok == lines.Count ? Check.Ok : Check.Warn, lines.Count == 0
                        ? "no games are bound to the guns yet — Games page, Bind to guns"
                        : ok + " of " + lines.Count + " bound games are in RetroBat. Update RetroBat's game lists to see them.");
            }
            catch (Exception ex) { Say(Check.Fail, ex.Message); }
            Refresh_();
        }

        void Say(Check c, string text)
        {
            _result.ForeColor = Ui.Tone(c);
            _result.Text = Ui.Glyph(c) + "  " + text;
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
                Set(Check.Ok, tpIn == null ? "found — Set up RetroBat links your TeknoParrot into it"
                    : link.Exists && link.LinkTarget != null ? "found — TeknoParrot linked to " + link.LinkTarget
                    : "found — it has its own TeknoParrot copy (" + tpIn + "); move it away so it can be linked to yours");
            }
        }

        void Set(Check c, string text)
        {
            _state.ForeColor = Ui.Tone(c);
            _state.Text = Ui.Glyph(c) + "  " + text;
        }
    }
}
