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
        readonly Button _borderToggle;
        readonly CheckBox _fit;

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
            _tpState = Ui.Text("", 0, y + 58, 362, 36, Ui.Dim);
            var tpBind = Ui.Btn("Bind to guns", 370, y + 54, 130);
            tpBind.Click += (s, e) => BindTp();
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
            _profiles.Columns.Add("Game profile", 270);
            _profiles.Columns.Add("P1 gun", 115);
            _profiles.Columns.Add("P2 gun", 115);
            _profiles.Columns.Add("Border", 120);
            _profiles.SelectedIndexChanged += (s, e) => ShowBorder();
            Controls.AddRange(new Control[] { _tpDir, tpBrowse, tpFind, _tpState, tpBind, tpOpen, _profiles });

            // ---- white border (ReShade) for the selected game
            y += 216;
            Controls.Add(Ui.Text("White border in the selected game (ReShade)", 0, y, 400, 22, null, 10, true));
            _borderState = Ui.Text("", 0, y + 26, 250, 40, Ui.Dim);
            var borderInstall = Ui.Btn("Install border", 258, y + 22, 120);
            borderInstall.Click += (s, e) => InstallBorder();
            _borderToggle = Ui.Btn("Turn off", 386, y + 22, 114);
            _borderToggle.Click += (s, e) =>
            {
                // Same switch as the Calibrate page: the game border and the
                // calibration screens must agree, and changing it means a
                // full recalibration.
                if (Wizard.SetBorder(!ScreenBorder.Mode)) Refresh_();
            };
            var borderRemove = Ui.Btn("Remove", 508, y + 22, 130);
            borderRemove.Click += (s, e) => { var t = SelectedTarget(); if (t != null) Border(() => ReshadeBorder.Uninstall(t)); };
            new ToolTip().SetToolTip(_borderToggle, "The white border setting (games + calibration screens). Changing it needs a full recalibration; games pick it up at the next launch");
            new ToolTip().SetToolTip(borderRemove, "Take everything out of the selected game's folder and put back its original DLL if there was one");
            _fit = new CheckBox
            {
                Text = "Shrink the game inside the border (nothing is covered)", Location = new Point(0, y + 66),
                AutoSize = true, ForeColor = Ui.Fg, BackColor = Ui.Bg, Checked = w.Config.BorderFit,
            };
            _fit.CheckedChanged += (s, e) =>
            {
                Wizard.Config.BorderFit = _fit.Checked;
                try { Wizard.Config.Save(); } catch { }
                ReshadeBorder.Apply(_tpDir.Text.Trim());
                Wizard.Engine.Note("setup: border " + (_fit.Checked ? "shrinks the game inside it" : "covers the game's edges") +
                    " — applies at the next game launch");
            };
            new ToolTip().SetToolTip(_fit, "On: the game picture is scaled into the area inside the white frame and the tray maps the aim to match. " +
                "Off: the frame is drawn over the picture's edges. No recalibration needed either way.");
            Controls.AddRange(new Control[] { _borderState, borderInstall, _borderToggle, borderRemove, _fit });

            // ---- bridge game list
            y += 96;
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
                    var bt = ReshadeBorder.For(cfg.TeknoParrotDir, p.File);
                    var bs = ReshadeBorder.Get(bt);
                    string border = bs == ReshadeBorder.State.On ? "on" : bs == ReshadeBorder.State.Off ? "off"
                        : bs == ReshadeBorder.State.NotInstalled ? "not installed" : bs == ReshadeBorder.State.Unsupported ? "n/a"
                        : bs == ReshadeBorder.State.ShaderMissing ? "old — reinstall" : "check";
                    var item = new ListViewItem(new[] { p.Game, p.P1 ? "Bridge Mouse 1" : "—", p.P2 ? "Bridge Mouse 2" : "—", border });
                    item.ForeColor = p.P1 && p.P2 ? Ui.Lime : p.P1 ? Ui.Amber : Ui.Dim;
                    item.Tag = p;
                    _profiles.Items.Add(item);
                }
                int full = list.Count(p => p.P1 && p.P2);
                State(_tpState, list.Count == 0 ? Check.Warn : full > 0 ? Check.Ok : Check.Warn,
                    list.Count == 0 ? "no game profiles yet — add a game in TeknoParrot first"
                    : full + " of " + list.Count + " profiles bound for two guns. Select games, then Bind to guns.");
            }
            _borderToggle.Text = ScreenBorder.Mode ? "Turn off" : "Turn on";
            ShowBorder();
            try { cfg.Save(); } catch { }
        }

        // The border target of the game selected in the list (null: none).
        ReshadeBorder.Target SelectedTarget()
        {
            var p = _profiles.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as TeknoParrotProfiles.Profile).FirstOrDefault();
            if (p == null) { State(_borderState, Check.Info, "select a game in the list above"); return null; }
            return ReshadeBorder.For(_tpDir.Text.Trim(), p.File);
        }

        void ShowBorder()
        {
            if (Wizard.Config.TeknoParrotDir.Length == 0) { State(_borderState, Check.Info, "set the TeknoParrot folder first"); return; }
            if (_profiles.SelectedItems.Count == 0)
            {
                State(_borderState, Check.Info, "select a game in the list above (ElfLdr2 games share one install)");
                return;
            }
            var t = SelectedTarget();
            var st = ReshadeBorder.Get(t);
            State(_borderState, st == ReshadeBorder.State.On ? Check.Ok
                : st == ReshadeBorder.State.Off || st == ReshadeBorder.State.NotInstalled ? Check.Info : Check.Warn,
                ReshadeBorder.Describe(t, st));
            // Files changed by hand (or before the setting existed).
            if ((st == ReshadeBorder.State.On && !ScreenBorder.Mode) || (st == ReshadeBorder.State.Off && ScreenBorder.Mode))
                State(_borderState, Check.Warn, ReshadeBorder.Describe(t, st) + " — but the border setting is " +
                    (ScreenBorder.Mode ? "ON" : "OFF") + "; Install again to match");
        }

        void InstallBorder()
        {
            var t = SelectedTarget();
            if (t == null) return;
            if (t.Problem != null && t.Problem.StartsWith("could not tell"))
            {
                // Protected exes hide their graphics API: ask once, remember it.
                var p = (TeknoParrotProfiles.Profile)_profiles.SelectedItems[0].Tag;
                string exe = ReshadeBorder.ExeOf(p.File);
                string pick = PickApi(Path.GetFileName(exe));
                if (pick == null || exe == null) return;
                ReshadeBorder.Choose(exe, pick);
                t = ReshadeBorder.For(_tpDir.Text.Trim(), p.File);
            }
            if (t.Problem != null) { State(_borderState, Check.Fail, t.Problem); return; }
            string dll = ReshadeBorder.FindDll(t);
            if (dll == null)
            {
                using (var dlg = new OpenFileDialog
                {
                    Filter = "ReShade download (ReShade_Setup_*.exe, ReShade32/64.dll)|ReShade_Setup*.exe;ReShade32.dll;ReShade64.dll;*.dll|All files|*.*",
                    Title = "Pick the ReShade_Setup_*.exe downloaded from reshade.me",
                    InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    dll = dlg.FileName;
                }
            }
            // A fresh install is on; follow the border setting if it is off.
            Border(() => ReshadeBorder.Install(t, dll) +
                (ScreenBorder.Mode ? "" : "; " + ReshadeBorder.SetEnabled(t, false)));
        }

        // Small dialog: which graphics API does this game use?
        string PickApi(string exeName)
        {
            using (var f = new Form
            {
                Text = "Graphics API", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(420, 190), BackColor = Ui.Bg, ForeColor = Ui.Fg,
                Font = new Font("Segoe UI", 9),
            })
            {
                f.Controls.Add(Ui.Text("The border can't tell which graphics API " + exeName + " uses (its file is protected). " +
                    "Pick one; if the border doesn't show in the game, Remove and pick another.", 14, 12, 392, 54, Ui.Dim));
                var radios = ReshadeBorder.Choices.Select((c, i) => new RadioButton
                {
                    Text = c.label + "  (" + c.proxy + ")", Tag = c.proxy, Location = new Point(18, 70 + i * 26), AutoSize = true,
                    Checked = i == 0, ForeColor = Ui.Fg, BackColor = Ui.Bg,
                }).ToArray();
                f.Controls.AddRange(radios);
                var ok = Ui.Btn("OK", 230, 150, 86); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Btn("Cancel", 322, 150, 86); cancel.DialogResult = DialogResult.Cancel;
                f.Controls.AddRange(new Control[] { ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(this) == DialogResult.OK ? (string)radios.First(r => r.Checked).Tag : null;
            }
        }

        void Border(Func<string> action)
        {
            try
            {
                string msg = action();
                ReshadeBorder.Apply(_tpDir.Text.Trim());
                Wizard.Engine.Note("setup: ReShade border " + msg);
                var keep = _profiles.SelectedItems.Count > 0 ? ((TeknoParrotProfiles.Profile)_profiles.SelectedItems[0].Tag).File : null;
                Refresh_();
                foreach (ListViewItem i in _profiles.Items)
                    if (((TeknoParrotProfiles.Profile)i.Tag).File == keep) i.Selected = true;
                _borderState.Text = Ui.Glyph(Check.Ok) + "  " + msg;
            }
            catch (Exception ex) { State(_borderState, Check.Fail, ex.Message); }
        }

        // Selected profiles, or every profile not yet bound for two guns.
        void BindTp()
        {
            var picked = _profiles.SelectedItems.Cast<ListViewItem>().Select(i => (TeknoParrotProfiles.Profile)i.Tag).ToList();
            if (picked.Count == 0)
                picked = _profiles.Items.Cast<ListViewItem>().Select(i => (TeknoParrotProfiles.Profile)i.Tag)
                    .Where(p => !(p.P1 && p.P2)).ToList();
            if (picked.Count == 0) { State(_tpState, Check.Ok, "every profile is already bound"); return; }
            if (TeknoParrotProfiles.Running())
            {
                MessageBox.Show(this, "Close TeknoParrot first: it reads profiles at startup and writes them back when you save its settings.",
                    "TeknoParrot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var mice = BridgeDevices.Mice();
            int ok = 0;
            string last = "";
            foreach (var p in picked)
            {
                try
                {
                    last = p.Game + ": " + TeknoParrotProfiles.Bind(p.File, mice);
                    // The bridge only streams to listed game processes.
                    string proc = TeknoParrotProfiles.GameProcess(p.File);
                    if (proc != null && PicoOutput.EnsureGame(proc)) last += "; " + proc + " added to the bridge games";
                    Wizard.Engine.Note("setup: TeknoParrot " + last);
                    ok++;
                }
                catch (Exception ex)
                {
                    last = p.Game + ": " + ex.Message;
                    Wizard.Engine.Note("setup: TeknoParrot " + last);
                }
            }
            Refresh_();
            State(_tpState, ok == picked.Count ? Check.Ok : Check.Fail,
                ok + " of " + picked.Count + " bound. " + (picked.Count == 1 ? last : "Details in the Log tab."));
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
