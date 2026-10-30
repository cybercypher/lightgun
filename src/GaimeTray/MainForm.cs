using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using GaimeFeeder;

namespace GaimeTray
{
    sealed class MainForm : Form
    {
        readonly FeederEngine _eng;
        readonly GunCard _c1 = new GunCard();
        readonly GunCard _c2 = new GunCard();
        readonly Label _emptyHint, _statePill;
        readonly Button _startStop;
        readonly Button _testBtn;
        readonly CheckBox _autostart;
        readonly RichTextBox _log = new RichTextBox();
        readonly NumericUpDown _secs = new NumericUpDown();
        Button _capBtn;
        readonly Label _capResult = new Label();
        readonly Timer _tick = new Timer();
        ComboBox _game;
        Label _gameState;
        bool _wasFeeding;

        static readonly Color Bg = Color.FromArgb(24, 25, 28);
        static readonly Color Panel = Color.FromArgb(32, 33, 36);
        static readonly Color Fg = Color.FromArgb(230, 230, 230);
        static readonly Color Dim = Color.FromArgb(140, 140, 140);
        static readonly Color Lime = Color.FromArgb(140, 255, 120);

        public MainForm(FeederEngine eng)
        {
            _eng = eng;
            Text = "G'AIM'E Tray";
            Size = new Size(800, 644);
            MinimumSize = new Size(760, 584);
            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 9);
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F12) RecalFirst();
                if (e.Control && e.KeyCode == Keys.T) OpenGallery();
            };

            // header
            var head = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Panel };
            var title = new Label
            {
                Text = "G'AIM'E  lightguns", Location = new Point(14, 6),
                AutoSize = true, Font = new Font("Segoe UI", 14, FontStyle.Bold),
                ForeColor = Fg, BackColor = Panel,
            };
            _statePill = new Label
            {
                Location = new Point(240, 12), Size = new Size(110, 30),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
            };
            _startStop = new Button
            {
                Location = new Point(360, 12), Size = new Size(90, 30),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(50, 50, 52), ForeColor = Fg,
            };
            _startStop.Click += (s, e) => Toggle();
            _testBtn = new Button
            {
                Text = "Test", Location = new Point(630, 12), Size = new Size(70, 30),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(50, 50, 52), ForeColor = Fg,
            };
            _testBtn.Click += (s, e) => OpenGallery();
            new ToolTip().SetToolTip(_testBtn, "Shooting gallery - checks the calibration (Ctrl+T)");
            _autostart = new CheckBox
            {
                Text = "Start with Windows", Location = new Point(462, 16),
                AutoSize = true, ForeColor = Dim, BackColor = Panel,
                Checked = Autostart.IsOn(),
            };
            _autostart.CheckedChanged += (s, e) => Autostart.Set(_autostart.Checked);
            var trayNote = new Label
            {
                Text = "closes to tray · F12 recalibrates",
                Location = new Point(462, 36), AutoSize = true,
                ForeColor = Dim, BackColor = Panel, Font = new Font("Segoe UI", 8),
            };
            head.Controls.AddRange(new Control[] { title, _statePill, _startStop, _testBtn, _autostart, trayNote });
            Controls.Add(head);
            Controls.Add(MkGameBar());

            // gun cards
            var cards = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 210, BackColor = Bg,
                Padding = new Padding(12, 10, 12, 10),
                FlowDirection = FlowDirection.LeftToRight,
            };
            _c1.Calibrate += sn => _eng.Recalibrate(sn);
            _c1.Pulse += sn => _eng.TestTrigger(sn);
            _c2.Calibrate += sn => _eng.Recalibrate(sn);
            _c2.Pulse += sn => _eng.TestTrigger(sn);
            cards.Controls.AddRange(new Control[] { _c1, _c2 });
            Controls.Add(cards);
            _emptyHint = new Label
            {
                Text = "no guns yet — plug one in, it attaches in ~10 s (launcher-first OK)",
                Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Dim, BackColor = Bg, Visible = false,
            };
            Controls.Add(_emptyHint);

            // tabs
            var tabs = new TabControl { Dock = DockStyle.Fill, BackColor = Bg };
            tabs.TabPages.Add(MkLogPage());
            tabs.TabPages.Add(MkCapPage());
            Controls.Add(tabs);

            _eng.Logged += OnLog;
            _eng.GunsChanged += RefreshChrome;
            _tick.Interval = 100;
            _tick.Tick += (s, e) => RefreshLive();
            _tick.Start();
            RefreshChrome();
        }

        // Game bar: pick a TeknoParrot game, start it through the same
        // controller as auto-start (waits for the guns, checks calibration,
        // keeps the game in front, restarts it, Start + Coin exits).
        Panel MkGameBar()
        {
            var bar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Bg };
            bar.Controls.Add(new Label
            {
                Text = "Game", Location = new Point(14, 14), AutoSize = true,
                ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 9, FontStyle.Bold),
            });
            _game = new ComboBox
            {
                Location = new Point(62, 10), Size = new Size(260, 24), DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Panel, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
            };
            _game.DropDown += (s, e) => FillGames();
            _game.SelectionChangeCommitted += (s, e) =>
            {
                if (!(_game.SelectedItem is GameItem g)) return;
                var cfg = SetupConfig.Load();
                cfg.AutoLaunchProfile = g.FileName;
                try { cfg.Save(); } catch { }
            };
            var start = MkBtn("Start game", 332, 8);
            start.Size = new Size(100, 28);
            start.Click += (s, e) =>
            {
                if (_game.SelectedItem == null)
                {
                    MessageBox.Show(this, "Pick a game first. Games come from TeknoParrot's profiles (set the folder in the setup wizard).",
                        "Start game", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Kiosk.Instance?.StartNow();
            };
            var stop = MkBtn("Stop game", 438, 8);
            stop.Size = new Size(100, 28);
            stop.Click += (s, e) => Kiosk.Instance?.Stop("main window", true);
            new ToolTip().SetToolTip(stop, "Close the game and stop restarting it (or hold Start + Coin on a gun)");
            _gameState = new Label
            {
                Location = new Point(548, 14), Size = new Size(230, 20), AutoEllipsis = true,
                ForeColor = Dim, BackColor = Bg,
            };
            bar.Controls.AddRange(new Control[] { _game, start, stop, _gameState });
            FillGames();
            return bar;
        }

        void FillGames()
        {
            var cfg = SetupConfig.Load();
            string keep = (_game.SelectedItem as GameItem)?.FileName ?? cfg.AutoLaunchProfile;
            _game.Items.Clear();
            foreach (var p in TeknoParrotProfiles.Scan(cfg.TeknoParrotDir))
                _game.Items.Add(new GameItem(p));
            _game.SelectedItem = _game.Items.Cast<GameItem>().FirstOrDefault(i =>
                string.Equals(i.FileName, keep, StringComparison.OrdinalIgnoreCase));
        }

        sealed class GameItem
        {
            readonly TeknoParrotProfiles.Profile _p;
            public GameItem(TeknoParrotProfiles.Profile p) { _p = p; }
            public string FileName => Path.GetFileName(_p.File);
            public override string ToString() => _p.Game + (_p.P1 && _p.P2 ? "" : "  (guns not bound)");
        }

        TabPage MkLogPage()
        {
            var p = new TabPage("Log") { BackColor = Bg };
            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;
            _log.BackColor = Color.FromArgb(16, 17, 19);
            _log.ForeColor = Color.FromArgb(200, 220, 200);
            _log.Font = new Font("Consolas", 9);
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = Bg };
            var clear = MkBtn("Clear", 8, 4);
            clear.Click += (s, e) => _log.Clear();
            var folder = MkBtn("Open logs folder", 200, 4);
            folder.Click += (s, e) =>
            {
                try { Process.Start("explorer.exe", LogDir()); } catch { }
            };
            var snBtn = MkBtn("Write S/N", 340, 4);
            snBtn.Click += (s, e) =>
            {
                using (var dlg = new SnWriterForm(_eng.Note)) dlg.ShowDialog(this);
            };
            new ToolTip().SetToolTip(snBtn,
                "Write a gun's on-device serial: plug in ONE gun, type 11111 or 22222, Write, replug");
            var setupBtn = MkBtn("Setup wizard", 480, 4);
            setupBtn.Click += (s, e) => SetupWizard.Open(_eng);
            new ToolTip().SetToolTip(setupBtn,
                "Step-by-step: system check, gun serials and players, calibration order, game bindings");
            bar.Controls.AddRange(new Control[] { clear, folder, snBtn, setupBtn });
            p.Controls.AddRange(new Control[] { _log, bar });
            return p;
        }

        TabPage MkCapPage()
        {
            var p = new TabPage("Capture") { BackColor = Bg };
            var info = new Label
            {
                Text = "timed RawInput logger (was hid_log.ps1) — aim, move, pull trigger, press A/B/Coin/Start",
                Dock = DockStyle.Top, Height = 30, ForeColor = Dim, BackColor = Bg,
                Padding = new Padding(10, 4, 0, 0),
            };
            var row = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Bg };
            var lab = new Label
            {
                Text = "seconds:", Location = new Point(12, 12), AutoSize = true,
                ForeColor = Fg, BackColor = Bg,
            };
            _secs.Location = new Point(80, 10);
            _secs.Size = new Size(60, 24);
            _secs.Minimum = 5; _secs.Maximum = 120; _secs.Value = 15;
            _secs.BackColor = Panel; _secs.ForeColor = Fg;
            _capBtn = MkBtn("Start capture", 152, 8);
            _capBtn.Click += (s, e) => RunCapture();
            _capResult.Location = new Point(260, 12);
            _capResult.Size = new Size(480, 24);
            _capResult.ForeColor = Lime; _capResult.BackColor = Bg;
            row.Controls.AddRange(new Control[] { lab, _secs, _capBtn, _capResult });
            p.Controls.AddRange(new Control[] { row, info });
            return p;
        }

        static Button MkBtn(string t, int x, int y)
        {
            return new Button
            {
                Text = t, Location = new Point(x, y), Size = new Size(130, 28),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(50, 50, 52),
                ForeColor = Color.FromArgb(230, 230, 230),
            };
        }

        void Toggle()
        {
            if (_eng.Feeding) _eng.Stop(); else _eng.Start();
            RefreshChrome();
        }

        void OpenGallery()
        {
            if (_eng.OpenGallery()) return;
            MessageBox.Show(this,
                "Start feeding first: the gallery reads the guns through the same " +
                "raw-input capture that feeds the pads.",
                "Test gallery", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void RecalFirst()
        {
            var snap = _eng.Snapshot();
            if (snap.Count > 0) _eng.Recalibrate(snap[0].Serial);
        }

        void RefreshChrome()
        {
            if (InvokeRequired) { BeginInvoke(new Action(RefreshChrome)); return; }
            bool f = _eng.Feeding;
            _wasFeeding = f;
            _statePill.Text = f ? "● FEEDING" : "○ STOPPED";
            _statePill.BackColor = f ? Color.FromArgb(30, 80, 30) : Color.FromArgb(50, 50, 52);
            _statePill.ForeColor = f ? Lime : Dim;
            _startStop.Text = f ? "Stop" : "Start";
            var snap = _eng.Snapshot();
            _emptyHint.Visible = snap.Count == 0;
            _c1.Visible = snap.Count > 0;
            _c2.Visible = snap.Count > 1;
            if (snap.Count > 0) _c1.Show(snap[0]);
            if (snap.Count > 1) _c2.Show(snap[1]);
        }

        void RefreshLive()
        {
            var k = Kiosk.Instance;
            if (_gameState != null && k != null)
            {
                string st = k.Active ? k.Status : "not running";
                if (_gameState.Text != st) _gameState.Text = st;
            }
            var snap = _eng.Snapshot();
            if (snap.Count > 0) _c1.Show(snap[0]);
            if (snap.Count > 1) _c2.Show(snap[1]);
        }

        void OnLog(string s)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + s + "\r\n");
                    if (_log.Lines.Length > 2000)
                    {
                        var lines = new string[1500];
                        Array.Copy(_log.Lines, _log.Lines.Length - 1500, lines, 0, 1500);
                        _log.Lines = lines;
                    }
                    _log.SelectionStart = _log.TextLength;
                    _log.ScrollToCaret();
                }));
            }
            catch { }
        }

        void RunCapture()
        {
            int secs = (int)_secs.Value;
            _capBtn.Enabled = false;
            _capResult.Text = "capturing " + secs + "s — aim + move + trigger + buttons…";
            Task.Run(() =>
            {
                List<string> lines;
                try { lines = RawCapture.Capture(secs); }
                catch (Exception ex) { lines = new List<string> { "capture FAILED: " + ex.Message }; }
                string path = Path.Combine(LogDir(),
                    "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                try { File.WriteAllLines(path, lines); } catch { }
                int tips = 0, keys = 0;
                foreach (var l in lines)
                {
                    if (l.Contains("tip=1")) tips++;
                    if (l.Contains(" KEY ")) keys++;
                }
                string msg = lines.Count + " lines → " + Path.GetFileName(path) +
                    "  (tip=1 x" + tips + ", keys x" + keys + ")";
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        _capResult.Text = msg;
                        _capBtn.Enabled = true;
                    }));
                }
                catch { }
            });
        }

        // Deployed: beside the exe (C:\games\feeder). Dev: repo root. See AppPaths.
        static string LogDir()
        {
            Directory.CreateDirectory(AppPaths.Logs);
            return AppPaths.Logs;
        }
    }
}
