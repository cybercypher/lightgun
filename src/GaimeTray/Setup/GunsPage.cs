using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using GaimeFeeder;

namespace GaimeTray
{
    // Which guns are here, their on-device serials, and which player each one
    // is. Serials are written with the existing one-gun-at-a-time dialog;
    // players are saved to guns.json.
    sealed class GunsPage : WizardPage
    {
        public override string Title => "Guns";

        readonly Label _summary;
        readonly Panel _list;
        readonly Label _result;
        readonly List<(string sn, ComboBox player)> _rows = new List<(string, ComboBox)>();

        public GunsPage(SetupWizard w) : base(w)
        {
            Controls.Add(Ui.Text(
                "Each gun is known by the serial stored inside it, and that serial picks its player " +
                "(Bridge Mouse 1 or 2). Give each gun its own serial first: plug in ONE gun, Write serial, " +
                "unplug, repeat with the next. Then plug both in and assign players.",
                0, 0, 650, 54, Ui.Dim));
            _summary = Ui.Text("", 0, 60, 650, 24, null, 10, true);
            _list = new Panel { Location = new Point(0, 90), Size = new Size(650, 150), BackColor = Ui.Panel };
            var refresh = Ui.Btn("Refresh", 0, 252, 110);
            refresh.Click += (s, e) => ShowPage();
            var write = Ui.Btn("Write serial…", 120, 252, 130);
            write.Click += (s, e) =>
            {
                using (var dlg = new SnWriterForm(Wizard.Engine.Note)) dlg.ShowDialog(this);
                ShowPage();
            };
            var save = Ui.Btn("Save players", 260, 252, 130);
            save.Click += (s, e) => Save();
            _result = Ui.Text("", 0, 292, 650, 60, Ui.Lime);
            Controls.AddRange(new Control[] { _summary, _list, refresh, write, save, _result });
        }

        // Queries every gun once (serial read). Only on enter / Refresh.
        public override void ShowPage()
        {
            _result.Text = "";
            int ifaces;
            List<string> sns;
            try
            {
                ifaces = HidIdentity.VendorPaths().Count;
                sns = HidIdentity.EnumerateGunSns();
            }
            catch (Exception ex)
            {
                _summary.ForeColor = Ui.Red;
                _summary.Text = "gun lookup failed: " + ex.Message;
                return;
            }

            if (ifaces == 0)
            {
                _summary.ForeColor = Ui.Red;
                _summary.Text = "No guns connected.";
            }
            else if (sns.Count < ifaces)
            {
                // Two interfaces, one serial: both guns carry the same serial,
                // or one would not answer the serial query.
                _summary.ForeColor = Ui.Amber;
                _summary.Text = ifaces + " guns connected but only " + sns.Count +
                    " distinct serial(s) — guns share a serial or one did not answer. Write a serial to each gun.";
            }
            else
            {
                _summary.ForeColor = Ui.Lime;
                _summary.Text = ifaces + " gun(s) connected, serials " + string.Join(", ", sns) + ".";
            }

            var map = GunMap.Read();
            _list.Controls.Clear();
            _rows.Clear();
            int y = 10;
            foreach (var sn in sns)
            {
                var name = Ui.Text("sn " + sn, 12, y + 5, 160, 22, null, 10, true);
                var combo = new ComboBox
                {
                    Location = new Point(180, y + 2), Size = new Size(140, 24),
                    DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Ui.Panel, ForeColor = Ui.Fg,
                    FlatStyle = FlatStyle.Flat,
                };
                combo.Items.AddRange(new object[] { "Player 1", "Player 2" });
                int player = map.TryGetValue(sn, out var p) ? p : 0;
                combo.SelectedIndex = player == 1 || player == 2 ? player - 1 : -1;
                string note = player == 0 ? "not in guns.json yet"
                    : player > 2 ? "guns.json says player " + player + " — the bridge only has 2"
                    : "";
                var hint = Ui.Text(note, 330, y + 5, 300, 22, Ui.Amber);
                _list.Controls.AddRange(new Control[] { name, combo, hint });
                _rows.Add((sn, combo));
                y += 36;
            }
            if (sns.Count == 0)
                _list.Controls.Add(Ui.Text("no readable gun serials", 12, 12, 400, 22, Ui.Dim));
        }

        void Save()
        {
            var chosen = _rows.Where(r => r.player.SelectedIndex >= 0)
                .Select(r => (r.sn, player: r.player.SelectedIndex + 1)).ToList();
            if (chosen.Count == 0) { Say(Ui.Amber, "Pick a player for at least one gun."); return; }
            var dup = chosen.GroupBy(c => c.player).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) { Say(Ui.Amber, "Two guns are set to Player " + dup.Key + ". Each player needs its own gun."); return; }

            var map = GunMap.Read();
            // A player number belongs to one serial: drop whatever had it.
            foreach (var c in chosen)
                foreach (var old in map.Where(kv => kv.Value == c.player && kv.Key != c.sn).Select(kv => kv.Key).ToList())
                    map.Remove(old);
            foreach (var c in chosen) map[c.sn] = c.player;
            try { GunMap.Save(map); }
            catch (Exception ex) { Say(Ui.Red, "Could not write guns.json: " + ex.Message); return; }
            Wizard.Engine.Note("setup: players " + string.Join(", ", chosen.Select(c => c.sn + "=P" + c.player)));
            Wizard.Engine.ReloadGuns();
            Say(Ui.Lime, "Saved to guns.json. " + string.Join(", ", chosen.Select(c => "sn " + c.sn + " → Player " + c.player)) + ".");
        }

        void Say(Color c, string text) { _result.ForeColor = c; _result.Text = text; }
    }
}
