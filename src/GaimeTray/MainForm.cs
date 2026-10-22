using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
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
        readonly CheckBox _autostart;
        readonly RichTextBox _log = new RichTextBox();
        readonly Label[] _slots = new Label[4];
        readonly NumericUpDown _secs = new NumericUpDown();
        Button _capBtn;
        readonly Label _capResult = new Label();
        readonly Timer _tick = new Timer();
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
            Size = new Size(800, 600);
            MinimumSize = new Size(760, 540);
            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 9);
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F12) RecalFirst();
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
            _autostart = new CheckBox
            {
                Text = "Start with Windows", Location = new Point(462, 16),
                AutoSize = true, ForeColor = Dim, BackColor = Panel,
                Checked = IsAutostart(),
            };
            _autostart.CheckedChanged += (s, e) => SetAutostart(_autostart.Checked);
            var trayNote = new Label
            {
                Text = "closes to tray · F12 recalibrates",
                Location = new Point(462, 36), AutoSize = true,
                ForeColor = Dim, BackColor = Panel, Font = new Font("Segoe UI", 8),
            };
            head.Controls.AddRange(new Control[] { title, _statePill, _startStop, _autostart, trayNote });
            Controls.Add(head);

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
            tabs.TabPages.Add(MkSlotsPage());
            tabs.TabPages.Add(MkCapPage());
            Controls.Add(tabs);

            _eng.Logged += OnLog;
            _eng.GunsChanged += RefreshChrome;
            _tick.Interval = 100;
            _tick.Tick += (s, e) => RefreshLive();
            _tick.Start();
            RefreshChrome();
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
            var folder = MkBtn("Open logs folder", 100, 4);
            folder.Click += (s, e) =>
            {
                try { Process.Start("explorer.exe", LogDir()); } catch { }
            };
            bar.Controls.AddRange(new Control[] { clear, folder });
            p.Controls.AddRange(new Control[] { _log, bar });
            return p;
        }

        TabPage MkSlotsPage()
        {
            var p = new TabPage("XInput") { BackColor = Bg };
            var hint = new Label
            {
                Text = "what Teknoparrot sees (was the XCheck window) — @10 Hz",
                Dock = DockStyle.Top, Height = 26, ForeColor = Dim, BackColor = Bg,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0),
            };
            p.Controls.Add(hint);
            var box = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(12),
                FlowDirection = FlowDirection.TopDown,
            };
            for (int i = 0; i < 4; i++)
            {
                _slots[i] = new Label
                {
                    AutoSize = false, Size = new Size(700, 30),
                    Font = new Font("Consolas", 10), ForeColor = Fg, BackColor = Panel,
                    TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0),
                };
                box.Controls.Add(_slots[i]);
            }
            p.Controls.Add(box);
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
            var snap = _eng.Snapshot();
            if (snap.Count > 0) _c1.Show(snap[0]);
            if (snap.Count > 1) _c2.Show(snap[1]);
            for (uint i = 0; i < 4; i++)
            {
                bool present = SlotReader.TryDescribe(i, out var line);
                _slots[i].Text = "   " + line;
                _slots[i].ForeColor = present ? Lime : Dim;
            }
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

        static bool IsAutostart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                    return k?.GetValue("GaimeTray") != null;
            }
            catch { return false; }
        }

        static void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (on)
                    {
                        // Point at the windowed apphost exe, NOT dotnet.exe:
                        // dotnet <dll> opens a console window.
                        string exe = Environment.ProcessPath;
                        if (exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            exe = Path.ChangeExtension(exe, ".exe");
                        k.SetValue("GaimeTray", "\"" + exe + "\"");
                    }
                    else k.DeleteValue("GaimeTray", false);
                }
            }
            catch { }
        }
    }
}
