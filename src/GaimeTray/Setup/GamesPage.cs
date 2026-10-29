using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GaimeFeeder;

namespace GaimeTray
{
    // Where the emulators live, their bindings to the bridge mice, and which
    // game processes unlock the bridge stream (bridge-processes.txt).
    sealed class GamesPage : WizardPage
    {
        public override string Title => "Games";

        readonly TextBox _tpDir, _dsDir, _games;
        readonly Label _dsState, _tpState, _gamesState, _borderState;
        readonly ListView _profiles;

        public GamesPage(SetupWizard w) : base(w)
        {
            int y = 0;
            // ---- DemulShooter
            Controls.Add(Ui.Text("DemulShooter", 0, y, 300, 22, null, 10, true));
            _dsDir = Ui.Box(w.Config.DemulShooterDir, 0, y + 26, 440);
            var dsBrowse = Ui.Btn("Browse…", 450, y + 23, 90);
            dsBrowse.Click += (s, e) => Browse(_dsDir, SetupConfig.DemulShooterExe);
            var dsFind = Ui.Btn("Find", 548, y + 23, 90);
            dsFind.Click += (s, e) => { _dsDir.Text = SetupConfig.DetectDemulShooter() ?? _dsDir.Text; Refresh_(); };
            _dsState = Ui.Text("", 0, y + 58, 500, 22, Ui.Dim);
            var dsBind = Ui.Btn("Bind to bridge", 508, y + 54, 130);
            dsBind.Click += (s, e) => BindDs();
            Controls.AddRange(new Control[] { _dsDir, dsBrowse, dsFind, _dsState, dsBind });

            // ---- TeknoParrot
            y += 100;
            Controls.Add(Ui.Text("TeknoParrot", 0, y, 300, 22, null, 10, true));
            _tpDir = Ui.Box(w.Config.TeknoParrotDir, 0, y + 26, 440);
            var tpBrowse = Ui.Btn("Browse…", 450, y + 23, 90);
            tpBrowse.Click += (s, e) => Browse(_tpDir, SetupConfig.TeknoParrotExe);
            var tpFind = Ui.Btn("Find", 548, y + 23, 90);
            tpFind.Click += (s, e) => { _tpDir.Text = SetupConfig.DetectTeknoParrot() ?? _tpDir.Text; Refresh_(); };
            _tpState = Ui.Text("", 0, y + 58, 500, 36, Ui.Dim);
            var tpOpen = Ui.Btn("Open profiles", 508, y + 54, 130);
            tpOpen.Click += (s, e) =>
            {
                var d = TeknoParrotProfiles.Dir(_tpDir.Text.Trim());
                if (d != null && Directory.Exists(d)) try { Process.Start("explorer.exe", d); } catch { }
            };
            _profiles = new ListView
            {
                Location = new Point(0, y + 96), Size = new Size(638, 110), View = View.Details,
                FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BackColor = Ui.Panel, ForeColor = Ui.Fg, BorderStyle = BorderStyle.None,
            };
            _profiles.Columns.Add("Game profile", 360);
            _profiles.Columns.Add("P1 gun", 130);
            _profiles.Columns.Add("P2 gun", 130);
            Controls.AddRange(new Control[] { _tpDir, tpBrowse, tpFind, _tpState, tpOpen, _profiles });

            // ---- white border (ReShade) for ElfLdr2 games
            y += 216;
            Controls.Add(Ui.Text("White border in ElfLdr2 games (ReShade)", 0, y, 400, 22, null, 10, true));
            _borderState = Ui.Text("", 0, y + 26, 360, 36, Ui.Dim);
            var borderOn = Ui.Btn("Install border", 370, y + 22, 130);
            borderOn.Click += (s, e) => InstallBorder();
            var borderOff = Ui.Btn("Remove", 508, y + 22, 130);
            borderOff.Click += (s, e) => Border(() => ReshadeBorder.Uninstall(_tpDir.Text.Trim()));
            Controls.AddRange(new Control[] { _borderState, borderOn, borderOff });

            // ---- bridge game list
            y += 72;
            Controls.Add(Ui.Text("Games that turn the bridge on", 0, y, 400, 22, null, 10, true));
            Controls.Add(Ui.Text(
                "The bridge only moves the cursor while one of these processes is in the foreground. One exe name per line.",
                0, y + 22, 640, 20, Ui.Dim));
            _games = new TextBox
            {
                Location = new Point(0, y + 46), Size = new Size(440, 96), Multiline = true,
                ScrollBars = ScrollBars.Vertical, BackColor = Ui.Panel, ForeColor = Ui.Fg,
                BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9),
            };
            var saveGames = Ui.Btn("Save list", 450, y + 46, 110);
            saveGames.Click += (s, e) => SaveGames();
            var defaults = Ui.Btn("Defaults", 450, y + 82, 110);
            defaults.Click += (s, e) => _games.Text = string.Join("\r\n", PicoOutput.DefaultGames);
            _gamesState = Ui.Text("", 450, y + 118, 190, 36, Ui.Dim);
            Controls.AddRange(new Control[] { _games, saveGames, defaults, _gamesState });

            _dsDir.Leave += (s, e) => Refresh_();
            _tpDir.Leave += (s, e) => Refresh_();
        }

        public override void ShowPage()
        {
            // Fill empty paths from the usual install places, once per visit.
            if (_dsDir.Text.Trim().Length == 0) _dsDir.Text = SetupConfig.DetectDemulShooter() ?? "";
            if (_tpDir.Text.Trim().Length == 0) _tpDir.Text = SetupConfig.DetectTeknoParrot() ?? "";
            _games.Text = string.Join("\r\n", PicoOutput.Games());
            _gamesState.Text = File.Exists(AppPaths.BridgeProcesses) ? "from bridge-processes.txt" : "built-in list";
            Refresh_();
        }

        void Browse(TextBox box, string exe)
        {
            using (var dlg = new OpenFileDialog { Filter = exe + "|" + exe, Title = "Find " + exe })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                box.Text = Path.GetDirectoryName(dlg.FileName);
            }
            Refresh_();
        }

        void Refresh_()
        {
            var cfg = Wizard.Config;
            cfg.DemulShooterDir = _dsDir.Text.Trim();
            cfg.TeknoParrotDir = _tpDir.Text.Trim();

            string ini = DemulShooterBinding.ConfigPath(cfg.DemulShooterDir);
            if (cfg.DemulShooterDir.Length == 0) State(_dsState, Check.Info, "not set — skip if you do not use DemulShooter");
            else if (!File.Exists(Path.Combine(cfg.DemulShooterDir, SetupConfig.DemulShooterExe)))
                State(_dsState, Check.Fail, "no DemulShooter.exe in that folder");
            else if (!File.Exists(ini)) State(_dsState, Check.Warn, "no config.ini yet — run DemulShooter once");
            else
            {
                var (p1, p2) = DemulShooterBinding.Current(ini);
                bool b1 = DemulShooterBinding.Bound(p1, 0), b2 = DemulShooterBinding.Bound(p2, 1);
                State(_dsState, b1 && b2 ? Check.Ok : Check.Warn,
                    "P1 " + (b1 ? "bridge" : "not bridge") + ", P2 " + (b2 ? "bridge" : "not bridge") +
                    (b1 && b2 ? "" : " — press Bind"));
            }

            _profiles.Items.Clear();
            if (cfg.TeknoParrotDir.Length == 0) State(_tpState, Check.Info, "not set — skip if you do not use TeknoParrot");
            else if (!File.Exists(Path.Combine(cfg.TeknoParrotDir, SetupConfig.TeknoParrotExe)))
                State(_tpState, Check.Fail, "no TeknoParrotUi.exe in that folder");
            else
            {
                var list = TeknoParrotProfiles.Scan(cfg.TeknoParrotDir);
                foreach (var p in list)
                {
                    var item = new ListViewItem(new[] { p.Game, p.P1 ? "Bridge Mouse 1" : "—", p.P2 ? "Bridge Mouse 2" : "—" });
                    item.ForeColor = p.P1 && p.P2 ? Ui.Lime : p.P1 ? Ui.Amber : Ui.Dim;
                    _profiles.Items.Add(item);
                }
                int full = list.Count(p => p.P1 && p.P2);
                State(_tpState, list.Count == 0 ? Check.Warn : full > 0 ? Check.Ok : Check.Warn,
                    list.Count == 0 ? "no game profiles yet — add a game in TeknoParrot first"
                    : full + " of " + list.Count + " profiles bound for two guns. Bind in UserProfiles\\<Game>.xml with TP closed (SETUP.md §5).");
            }
            if (cfg.TeknoParrotDir.Length == 0) State(_borderState, Check.Info, "set the TeknoParrot folder first");
            else
            {
                string st = ReshadeBorder.Status(cfg.TeknoParrotDir);
                State(_borderState, st == "installed" ? Check.Ok : st == "not installed" ? Check.Info : Check.Warn, st);
            }
            try { cfg.Save(); } catch { }
        }

        void InstallBorder()
        {
            string tp = _tpDir.Text.Trim();
            string dll = ReshadeBorder.FindDll(tp);
            if (dll == null)
            {
                using (var dlg = new OpenFileDialog
                {
                    Filter = "ReShade 32-bit DLL (*.dll)|*.dll",
                    Title = "Pick a 32-bit ReShade DLL (ReShade32.dll from the ReShade installer)",
                })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    dll = dlg.FileName;
                }
            }
            Border(() => ReshadeBorder.Install(tp, dll));
        }

        void Border(Func<string> action)
        {
            try
            {
                string msg = action();
                Wizard.Engine.Note("setup: ReShade border " + msg);
                Refresh_();
                State(_borderState, Check.Ok, msg);
            }
            catch (Exception ex) { State(_borderState, Check.Fail, ex.Message); }
        }

        void BindDs()
        {
            string ini = DemulShooterBinding.ConfigPath(_dsDir.Text.Trim());
            if (DemulShooterBinding.Running() &&
                MessageBox.Show(this, "DemulShooter is running and only reads its config at startup. Bind anyway and restart it after?",
                    "DemulShooter", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            try
            {
                string msg = DemulShooterBinding.Bind(ini, BridgeDevices.Mice());
                Wizard.Engine.Note("setup: DemulShooter " + msg);
                Refresh_();
                State(_dsState, Check.Ok, msg);
            }
            catch (Exception ex) { State(_dsState, Check.Fail, ex.Message); }
        }

        void SaveGames()
        {
            var names = _games.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (names.Count == 0) { State(_gamesState, Check.Warn, "list is empty — the bridge would never stream"); return; }
            try
            {
                File.WriteAllLines(AppPaths.BridgeProcesses,
                    new[] { "# Game processes that turn the Pico bridge on (setup wizard)." }.Concat(names));
                PicoOutput.ReloadGames();
                Wizard.Engine.Note("setup: bridge games = " + string.Join(", ", names));
                State(_gamesState, Check.Ok, "saved, " + names.Count + " names");
            }
            catch (Exception ex) { State(_gamesState, Check.Fail, ex.Message); }
        }

        static void State(Label l, Check c, string text)
        {
            l.ForeColor = Ui.Tone(c);
            l.Text = Ui.Glyph(c) + "  " + text;
        }
    }
}
