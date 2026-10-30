using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace GaimeTray
{
    sealed class CheckPage : WizardPage
    {
        public override string Title => "System check";

        readonly List<(Label glyph, Label name, Label detail)> _rows = new List<(Label, Label, Label)>();
        readonly Label _flashResult;

        public CheckPage(SetupWizard w) : base(w)
        {
            Controls.Add(Ui.Text(
                "Everything the rig needs on the PC side. Rows update by themselves; fix any red row before calibrating.",
                0, 0, 640, 36, Ui.Dim));
            for (int i = 0; i < 7; i++)
            {
                int y = 44 + i * 34;
                var g = Ui.Text("", 0, y, 24, 24, null, 12, true);
                var n = Ui.Text("", 28, y + 3, 150, 22, null, 9, true);
                var d = Ui.Text("", 180, y + 3, 480, 30, Ui.Dim);
                Controls.AddRange(new Control[] { g, n, d });
                _rows.Add((g, n, d));
            }

            int fy = 44 + 7 * 34 + 16;
            Controls.Add(Ui.Text("Pico bridge firmware", 0, fy, 300, 22, null, 10, true));
            Controls.Add(Ui.Text(
                "Hold BOOTSEL on the Pico while plugging it in (it shows up as a drive), then press Flash. " +
                "Uses gun_bridge.uf2 next to GaimeTray.exe, or asks for one.",
                0, fy + 24, 640, 36, Ui.Dim));
            var flash = Ui.Btn("Flash bridge…", 0, fy + 64, 140);
            flash.Click += (s, e) => Flash();
            var display = Ui.Btn("Display settings", 150, fy + 64, 140);
            display.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true }); } catch { }
            };
            var touch = Ui.Btn("Stop guns moving the cursor", 300, fy + 64, 210);
            touch.Click += (s, e) =>
            {
                try
                {
                    SystemChecks.SetTouchGateOff();
                    Wizard.Engine.Note("setup: TouchGate = 0 (Windows stops moving the cursor from the guns after sign-out)");
                    _flashResult.ForeColor = Ui.Lime;
                    _flashResult.Text = "Done. Sign out of Windows and back in for it to take effect. " +
                        "Undo: the same setting set to 1 (HKCU\\Software\\Microsoft\\Wisp\\Touch, TouchGate).";
                }
                catch (Exception ex)
                {
                    _flashResult.ForeColor = Ui.Amber;
                    _flashResult.Text = "Could not change the setting: " + ex.Message;
                }
                Tick();
            };
            new ToolTip().SetToolTip(touch, "Windows treats each gun as a touchscreen and moves the cursor where it points. " +
                "This per-user setting (no admin) stops that; the tray still reads the guns.");
            _flashResult = Ui.Text("", 0, fy + 102, 640, 40, Ui.Lime);
            Controls.AddRange(new Control[] { flash, display, touch, _flashResult });
        }

        public override void ShowPage() => Tick();

        public override void Tick()
        {
            var all = SystemChecks.All(Wizard.Engine);
            for (int i = 0; i < _rows.Count && i < all.Count; i++)
            {
                var (g, n, d) = _rows[i];
                g.Text = Ui.Glyph(all[i].State);
                g.ForeColor = Ui.Tone(all[i].State);
                n.Text = all[i].Name;
                d.Text = all[i].Detail;
            }
        }

        void Flash()
        {
            string drive = SystemChecks.Uf2Drive();
            if (drive == null)
            {
                _flashResult.ForeColor = Ui.Amber;
                _flashResult.Text = "No Pico in BOOTSEL mode found. Unplug it, hold BOOTSEL, plug it back in, then press Flash.";
                return;
            }
            string uf2 = SystemChecks.BundledUf2();
            if (uf2 == null)
            {
                using (var dlg = new OpenFileDialog { Filter = "UF2 firmware (*.uf2)|*.uf2", Title = "Pick gun_bridge.uf2" })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    uf2 = dlg.FileName;
                }
            }
            try
            {
                File.Copy(uf2, Path.Combine(drive, Path.GetFileName(uf2)), true);
                _flashResult.ForeColor = Ui.Lime;
                _flashResult.Text = "Copied " + Path.GetFileName(uf2) + " to " + drive +
                    ". The Pico reboots as the bridge; the rows above turn green in a few seconds.";
                Wizard.Engine.Note("setup: flashed " + uf2 + " to " + drive);
            }
            catch (Exception ex)
            {
                // The Pico reboots the moment the copy completes, which can
                // surface as an I/O error even though the flash worked.
                _flashResult.ForeColor = Ui.Amber;
                _flashResult.Text = "Copy reported: " + ex.Message +
                    " — if the Pico rebooted, the flash still worked; check the bridge row.";
            }
        }
    }
}
