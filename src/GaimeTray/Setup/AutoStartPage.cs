using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;
using GaimeFeeder;

namespace GaimeTray
{
    // Launch a game at startup and keep it running across exits and reboots.
    // A reboot only comes back to the game when Windows signs in by itself and
    // starts the tray; this page checks both, plus sleep and screen-off.
    sealed class AutoStartPage : WizardPage
    {
        public override string Title => "Auto-start";

        readonly ComboBox _game;
        readonly CheckBox _launch, _restart, _withWindows;
        readonly NumericUpDown _combo;
        readonly Label _gameNote, _kiosk;
        readonly (Label glyph, Label text)[] _checks = new (Label, Label)[3];

        public AutoStartPage(SetupWizard w) : base(w)
        {
            Controls.Add(Ui.Text(
                "After a reboot the tray starts, waits for the bridge and the guns, checks the calibration, launches " +
                "the game and keeps it in front. If the game closes it is started again. To leave the game, hold " +
                "Start + Coin on a gun.",
                0, 0, 650, 54, Ui.Dim));

            Controls.Add(Ui.Text("Game", 0, 62, 60, 22, null, 10, true));
            _game = new ComboBox
            {
                Location = new Point(64, 60), Size = new Size(300, 24), DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Ui.Panel, ForeColor = Ui.Fg, FlatStyle = FlatStyle.Flat,
            };
            _game.SelectedIndexChanged += (s, e) => Save();
            _gameNote = Ui.Text("", 372, 63, 280, 40, Ui.Dim);
            Controls.AddRange(new Control[] { _game, _gameNote });

            _launch = MkCheck("Launch this game when the tray starts", 0, 100);
            _restart = MkCheck("Start it again if it closes", 0, 126);
            Controls.Add(Ui.Text("Hold Start + Coin on a gun for", 0, 156, 196, 22));
            _combo = new NumericUpDown
            {
                Location = new Point(198, 153), Size = new Size(48, 24), Minimum = 2, Maximum = 15,
                BackColor = Ui.Panel, ForeColor = Ui.Fg,
            };
            _combo.ValueChanged += (s, e) => Save();
            Controls.Add(_combo);
            Controls.Add(Ui.Text("seconds to leave the game (until the next restart of the tray)", 252, 156, 400, 22));

            var startNow = Ui.Btn("Start game now", 0, 184, 140);
            startNow.Click += (s, e) =>
            {
                Save();
                Kiosk.Instance?.StartNow();
                Tick();
            };
            _kiosk = Ui.Text("", 150, 190, 500, 22, Ui.Dim);
            Controls.AddRange(new Control[] { startNow, _kiosk });

            // ---- survive a reboot
            Controls.Add(Ui.Text("Coming back after a reboot", 0, 230, 400, 22, null, 10, true));
            _withWindows = MkCheck("Start GaimeTray with Windows", 0, 256);
            int y = 284;
            for (int i = 0; i < _checks.Length; i++, y += 30)
            {
                _checks[i] = (Ui.Text("", 0, y, 22, 22, null, 11, true), Ui.Text("", 24, y + 2, 470, 26, Ui.Dim));
                Controls.Add(_checks[i].glyph);
                Controls.Add(_checks[i].text);
            }
            var signIn = Ui.Btn("Sign-in settings", 508, 280, 130);
            signIn.Click += (s, e) => Run("netplwiz", "");
            var power = Ui.Btn("Never sleep", 508, 310, 130);
            power.Click += (s, e) => SetNeverSleep();
            Controls.AddRange(new Control[] { signIn, power });
        }

        CheckBox MkCheck(string text, int x, int y)
        {
            var c = new CheckBox { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = Ui.Fg, BackColor = Ui.Bg };
            c.CheckedChanged += (s, e) => { if (_loading) return; if (c == _withWindows) Autostart.Set(c.Checked); Save(); };
            Controls.Add(c);
            return c;
        }

        bool _loading;
        (Check, string) _sleep, _screen;

        // powercfg is a process start: only on entry and after a change.
        void ReadPower()
        {
            _sleep = PowerCheck("SUB_SLEEP", "STANDBYIDLE", "Sleep");
            _screen = PowerCheck("SUB_VIDEO", "VIDEOIDLE", "Screen off");
        }

        public override void ShowPage()
        {
            _loading = true;
            var cfg = Wizard.Config;
            _game.Items.Clear();
            var profiles = TeknoParrotProfiles.Scan(cfg.TeknoParrotDir);
            if (RetroBat.Valid(cfg.RetroBatDir)) _game.Items.Add(new Item(null));
            foreach (var p in profiles) _game.Items.Add(new Item(p));
            var current = _game.Items.Cast<Item>().FirstOrDefault(i =>
                string.Equals(i.FileName, cfg.AutoLaunchProfile, StringComparison.OrdinalIgnoreCase));
            _game.SelectedItem = current;
            _launch.Checked = cfg.AutoLaunch;
            _restart.Checked = cfg.RestartOnExit;
            _combo.Value = Math.Max(_combo.Minimum, Math.Min(_combo.Maximum, cfg.ExitComboSeconds));
            _withWindows.Checked = Autostart.IsOn();
            _loading = false;
            ReadPower();
            if (profiles.Count == 0)
            {
                _gameNote.ForeColor = Ui.Amber;
                _gameNote.Text = "no TeknoParrot profiles — set the folder on the Games page";
            }
            Tick();
        }

        void Save()
        {
            if (_loading) return;
            var cfg = Wizard.Config;
            var item = _game.SelectedItem as Item;
            cfg.AutoLaunchProfile = item?.FileName ?? "";
            cfg.AutoLaunch = _launch.Checked;
            cfg.RestartOnExit = _restart.Checked;
            cfg.ExitComboSeconds = (int)_combo.Value;
            try { cfg.Save(); } catch { }
            if (item != null && item.Profile == null)
            {
                _gameNote.ForeColor = Ui.Dim;
                _gameNote.Text = "starts RetroBat; it launches the games";
            }
            else if (item != null)
            {
                bool both = item.Profile.P1 && item.Profile.P2;
                _gameNote.ForeColor = both ? Ui.Dim : Ui.Amber;
                _gameNote.Text = both ? "both guns bound" : "guns not bound for both players — Games page, Bind to guns";
            }
        }

        public override void Tick()
        {
            var k = Kiosk.Instance;
            _kiosk.Text = "auto-start: " + (k?.Status ?? "off") +
                (k != null && k.Active ? "  (it waits while this wizard is open)" : "");

            Show(0, AutoSignIn());
            Show(1, _sleep);
            Show(2, _screen);
        }

        void Show(int i, (Check state, string text) r)
        {
            _checks[i].glyph.Text = Ui.Glyph(r.state);
            _checks[i].glyph.ForeColor = Ui.Tone(r.state);
            _checks[i].text.Text = r.text;
        }

        // Automatic sign-in: without it a reboot stops at the lock screen and
        // nothing starts. The wizard only checks and opens netplwiz; storing
        // the password is the user's decision.
        static (Check, string) AutoSignIn()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
                    if ((k?.GetValue("AutoAdminLogon") as string) == "1")
                        return (Check.Ok, "Windows signs in automatically");
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device"))
                    if (k?.GetValue("DevicePasswordLessBuildVersion") is int v && v == 2)
                        return (Check.Warn, "no automatic sign-in. First turn off \"only allow Windows Hello sign-in\" " +
                            "(Settings → Accounts → Sign-in options), then Sign-in settings");
            }
            catch { }
            return (Check.Warn, "no automatic sign-in — a reboot stops at the sign-in screen. Sign-in settings: " +
                "untick \"Users must enter a user name and password\"");
        }

        // powercfg prints the possible-settings block and then the current AC
        // and DC values as the last two hex numbers; labels are translated, the
        // order is not.
        static (Check, string) PowerCheck(string sub, string setting, string name)
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/query SCHEME_CURRENT " + sub + " " + setting)
                    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    var hex = Regex.Matches(output, @"0x([0-9a-fA-F]{8})").Cast<Match>()
                        .Select(m => Convert.ToUInt32(m.Groups[1].Value, 16)).ToList();
                    if (hex.Count >= 2)
                    {
                        uint ac = hex[hex.Count - 2];
                        return ac == 0
                            ? (Check.Ok, name + ": never (plugged in)")
                            : (Check.Warn, name + " after " + (ac / 60) + " min when plugged in — Never sleep fixes it");
                    }
                }
            }
            catch { }
            return (Check.Info, name + ": could not read the power plan");
        }

        void SetNeverSleep()
        {
            foreach (var a in new[] { "standby-timeout-ac 0", "monitor-timeout-ac 0", "hibernate-timeout-ac 0" })
                Run("powercfg", "/change " + a, wait: true);
            Wizard.Engine.Note("setup: power plan set to never sleep / screen off while plugged in");
            ReadPower();
            Tick();
        }

        static void Run(string exe, string args, bool wait = false)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = !wait, CreateNoWindow = wait }))
                    if (wait) p?.WaitForExit(5000);
            }
            catch { }
        }

        sealed class Item
        {
            public readonly TeknoParrotProfiles.Profile Profile;
            public string FileName => Profile == null ? SetupConfig.RetroBatTarget : Path.GetFileName(Profile.File);
            public Item(TeknoParrotProfiles.Profile p) { Profile = p; }   // null = RetroBat
            public override string ToString() => Profile == null ? "RetroBat (frontend)" : Profile.Game;
        }
    }
}
