using System;
using System.Drawing;
using System.Windows.Forms;
using GaimeFeeder;

namespace GaimeTray
{
    // First-run setup, reopened from the tray menu or the Log tab: system
    // check, guns (serials + players), calibration in the right order, game
    // bindings, finish. Every step can be skipped; nothing here is needed to
    // feed, it only drives the same engine the main window uses.
    sealed class SetupWizard : Form
    {
        public readonly FeederEngine Engine;
        public readonly SetupConfig Config;

        readonly WizardPage[] _pages;
        readonly Label[] _nav;
        readonly Panel _host;
        readonly Label _title;
        readonly Button _back, _next;
        readonly Timer _tick = new Timer { Interval = 1000 };
        int _index;

        static SetupWizard _open;

        // One wizard at a time (tray menu, Log tab, first run).
        public static void Open(FeederEngine eng)
        {
            if (_open != null && !_open.IsDisposed)
            {
                _open.WindowState = FormWindowState.Normal;
                _open.Activate();
                return;
            }
            _open = new SetupWizard(eng);
            _open.FormClosed += (s, e) => _open = null;
            _open.Show();
        }

        // First start of this install: no setup.json yet.
        public static bool FirstRun => !System.IO.File.Exists(SetupConfig.FilePath);

        public SetupWizard(FeederEngine eng)
        {
            Engine = eng;
            Config = SetupConfig.Load();
            Text = "G'AIM'E setup";
            Size = new Size(900, 640);
            MinimumSize = new Size(900, 640);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Ui.Bg;
            ForeColor = Ui.Fg;
            Font = new Font("Segoe UI", 9);

            _pages = new WizardPage[]
            {
                new CheckPage(this), new GunsPage(this), new CalibratePage(this),
                new GamesPage(this), new AutoStartPage(this), new FinishPage(this),
            };

            var side = new Panel { Dock = DockStyle.Left, Width = 190, BackColor = Ui.Panel };
            side.Controls.Add(new Label
            {
                Text = "Setup", Location = new Point(16, 14), AutoSize = true,
                Font = new Font("Segoe UI", 14, FontStyle.Bold), ForeColor = Ui.Fg, BackColor = Ui.Panel,
            });
            _nav = new Label[_pages.Length];
            for (int i = 0; i < _pages.Length; i++)
            {
                int n = i;
                _nav[i] = new Label
                {
                    Text = (i + 1) + ".  " + _pages[i].Title,
                    Location = new Point(0, 64 + i * 40), Size = new Size(190, 36),
                    Padding = new Padding(16, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font("Segoe UI", 10), Cursor = Cursors.Hand,
                };
                _nav[i].Click += (s, e) => Go(n);
                side.Controls.Add(_nav[i]);
            }

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Ui.Panel };
            _back = Ui.Btn("Back", 0, 11, 100);
            _back.Click += (s, e) => Go(_index - 1);
            _next = Ui.Btn("Next", 0, 11, 100);
            _next.Click += (s, e) =>
            {
                if (_index == _pages.Length - 1) Finish();
                else Go(_index + 1);
            };
            var close = Ui.Btn("Close", 16, 11, 100);
            close.Click += (s, e) => Close();
            bottom.Controls.AddRange(new Control[] { close, _back, _next });
            bottom.Resize += (s, e) =>
            {
                _next.Left = bottom.Width - 116;
                _back.Left = bottom.Width - 224;
            };

            _title = new Label
            {
                Dock = DockStyle.Top, Height = 48, Padding = new Padding(20, 12, 0, 0),
                Font = new Font("Segoe UI", 13, FontStyle.Bold), ForeColor = Ui.Fg, BackColor = Ui.Bg,
            };
            _host = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding(20, 0, 20, 12) };

            Controls.Add(_host);
            Controls.Add(_title);
            Controls.Add(bottom);
            Controls.Add(side);

            _tick.Tick += (s, e) => { try { _pages[_index].Tick(); } catch { } };
            Shown += (s, e) => { Go(0); _tick.Start(); };
            FormClosed += (s, e) =>
            {
                _tick.Stop();
                // Closing without Finish still records the paths, so the wizard
                // does not reopen on every start; the tray menu brings it back.
                try { Config.Save(); } catch { }
                Engine.AutoCalibrate = true;
            };
            Engine.AutoCalibrate = false;
        }

        public void Go(int i)
        {
            if (i < 0 || i >= _pages.Length) return;
            _index = i;
            _host.Controls.Clear();
            var page = _pages[i];
            page.Dock = DockStyle.Fill;
            _host.Controls.Add(page);
            _title.Text = page.Title;
            for (int n = 0; n < _nav.Length; n++)
            {
                _nav[n].BackColor = n == i ? Ui.Bg : Ui.Panel;
                _nav[n].ForeColor = n == i ? Ui.Lime : Ui.Dim;
            }
            _back.Enabled = i > 0;
            _next.Text = i == _pages.Length - 1 ? "Finish" : "Next";
            try { page.ShowPage(); } catch (Exception ex) { Engine.Note("setup: " + page.Title + ": " + ex.Message); }
        }

        // The one border switch: tray screens + the ReShade border in games.
        // The gun's camera sees the border, so every gun calibrated under the
        // other state needs Box Cal and the host grid again (CalStatus).
        public bool SetBorder(bool on)
        {
            if (on == ScreenBorder.Mode) return true;
            var answer = MessageBox.Show(this,
                "Turn the white border " + (on ? "ON" : "OFF") + "?\r\n\r\n" +
                "The gun's camera uses the border, so every gun will need a full recalibration " +
                "(Box Cal, then the host grid) before it aims right again.",
                "White border", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (answer != DialogResult.OK) return false;
            ScreenBorder.Mode = ScreenBorder.Enabled = on;
            Config.BorderOn = on;
            try { Config.Save(); } catch { }
            string game = "";
            var st = ReshadeBorder.Get(Config.TeknoParrotDir);
            if (st == ReshadeBorder.State.On || st == ReshadeBorder.State.Off)
            {
                try { ReshadeBorder.SetEnabled(Config.TeknoParrotDir, on); game = ", game border " + (on ? "on" : "off"); }
                catch (Exception ex) { game = ", game border NOT switched: " + ex.Message; }
            }
            Engine.Note("setup: white border " + (on ? "ON" : "OFF") + game + " — all guns need Box Cal + host grid");
            Engine.RefreshCalStatus();
            return true;
        }

        void Finish()
        {
            Config.Completed = true;
            try { Config.Save(); } catch { }
            Engine.Note("setup: finished");
            Close();
        }
    }

    abstract class WizardPage : Panel
    {
        protected readonly SetupWizard Wizard;
        protected WizardPage(SetupWizard w)
        {
            Wizard = w;
            BackColor = Ui.Bg;
            AutoScroll = true;
        }
        public abstract string Title { get; }
        // Page became visible: refresh anything that can change underneath.
        public virtual void ShowPage() { }
        // Once a second while visible. Keep it cheap: no gun queries here (each
        // vendor query costs the gun a log line, see FeederEngine).
        public virtual void Tick() { }
    }

    enum Check { Ok, Warn, Fail, Info }

    // Shared look for the setup pages (same palette as MainForm/GunCard).
    static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(24, 25, 28);
        public static readonly Color Panel = Color.FromArgb(32, 33, 36);
        public static readonly Color Fg = Color.FromArgb(230, 230, 230);
        public static readonly Color Dim = Color.FromArgb(140, 140, 140);
        public static readonly Color Lime = Color.FromArgb(140, 255, 120);
        public static readonly Color Amber = Color.FromArgb(255, 200, 90);
        public static readonly Color Red = Color.FromArgb(255, 110, 110);
        public static readonly Color BtnBg = Color.FromArgb(50, 50, 52);

        public static Button Btn(string text, int x, int y, int w = 130)
        {
            return new Button
            {
                Text = text, Location = new Point(x, y), Size = new Size(w, 30),
                FlatStyle = FlatStyle.Flat, BackColor = BtnBg, ForeColor = Fg,
            };
        }

        public static Label Text(string text, int x, int y, int w, int h, Color? color = null,
            float size = 9, bool bold = false)
        {
            return new Label
            {
                Text = text, Location = new Point(x, y), Size = new Size(w, h),
                ForeColor = color ?? Fg, BackColor = Color.Transparent,
                Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            };
        }

        public static TextBox Box(string text, int x, int y, int w)
        {
            return new TextBox
            {
                Text = text, Location = new Point(x, y), Size = new Size(w, 24),
                BackColor = Panel, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            };
        }

        public static string Glyph(Check c) =>
            c == Check.Ok ? "✔" : c == Check.Warn ? "!" : c == Check.Fail ? "✖" : "•";

        public static Color Tone(Check c) =>
            c == Check.Ok ? Lime : c == Check.Warn ? Amber : c == Check.Fail ? Red : Dim;

        public static string When(DateTime? utc) =>
            utc.HasValue ? utc.Value.ToLocalTime().ToString("d MMM HH:mm") : "never";
    }
}
