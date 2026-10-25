using System;
using System.Drawing;
using System.Windows.Forms;

namespace GaimeFeeder
{
    // Write the gun's on-device serial (system_info.json sn.num) from the tray.
    //
    // The flow is deliberately one gun at a time, because the write goes to
    // "the gun that is plugged in": plug gun 1, type 11111, Write, unplug; plug
    // gun 2, type 22222, Write. It refuses whenever the number of vendor
    // interfaces is not exactly one, which is the same rule gun_set.ps1 uses and
    // the only way to know which gun the report reached.
    sealed class SnWriterForm : Form
    {
        static readonly Color Bg = Color.FromArgb(24, 25, 28);
        static readonly Color Panel = Color.FromArgb(32, 33, 36);
        static readonly Color Fg = Color.FromArgb(230, 230, 230);
        static readonly Color Dim = Color.FromArgb(140, 140, 140);
        static readonly Color Lime = Color.FromArgb(140, 255, 120);
        static readonly Color Amber = Color.FromArgb(255, 200, 90);

        readonly Label _status, _result;
        readonly TextBox _sn;
        readonly Button _write;
        readonly Timer _tick = new Timer { Interval = 1500 };
        readonly Action<string> _log;
        string _path;
        int _count;

        public SnWriterForm(Action<string> log)
        {
            _log = log;
            Text = "Write gun serial";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(430, 226);
            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 9);

            var head = new Label
            {
                Text = "Plug in ONE gun, type its serial, press Write. Then unplug it\n" +
                       "and repeat with the next gun.",
                Location = new Point(14, 12), Size = new Size(400, 36),
                ForeColor = Dim, BackColor = Bg,
            };
            _status = new Label
            {
                Text = "looking for a gun...",
                Location = new Point(14, 54), Size = new Size(400, 22),
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Amber, BackColor = Panel,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
            };
            var lab = new Label
            {
                Text = "serial:", Location = new Point(14, 90), AutoSize = true,
                ForeColor = Fg, BackColor = Bg,
            };
            _sn = new TextBox
            {
                Location = new Point(64, 87), Size = new Size(120, 24),
                BackColor = Panel, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
                Text = "11111", MaxLength = 12,
            };
            var pre1 = new Button
            {
                Text = "11111", Location = new Point(196, 86), Size = new Size(64, 26),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(50, 50, 52), ForeColor = Fg,
            };
            pre1.Click += (s, e) => _sn.Text = "11111";
            var pre2 = new Button
            {
                Text = "22222", Location = new Point(264, 86), Size = new Size(64, 26),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(50, 50, 52), ForeColor = Fg,
            };
            pre2.Click += (s, e) => _sn.Text = "22222";
            _write = new Button
            {
                Text = "Write", Location = new Point(340, 84), Size = new Size(76, 30),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(50, 50, 52), ForeColor = Fg,
                Enabled = false,
            };
            _write.Click += (s, e) => DoWrite();
            _result = new Label
            {
                Text = "", Location = new Point(14, 124), Size = new Size(402, 66),
                ForeColor = Lime, BackColor = Bg,
            };
            var close = new Button
            {
                Text = "Close", Location = new Point(340, 192), Size = new Size(76, 26),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(50, 50, 52), ForeColor = Fg,
                DialogResult = DialogResult.Cancel,
            };
            Controls.AddRange(new Control[] { head, _status, lab, _sn, pre1, pre2, _write, _result, close });
            CancelButton = close;
            _tick.Tick += (s, e) => RefreshState();
            Shown += (s, e) => { RefreshState(); _tick.Start(); };
            FormClosed += (s, e) => _tick.Stop();
        }

        void RefreshState()
        {
            _path = HidIdentity.SingleVendorPath(out _count);
            if (_count == 1)
            {
                string sn = HidIdentity.QuerySnOnce(_path);
                _status.Text = "detected 1 gun" + (string.IsNullOrEmpty(sn) ? " (serial unreadable)" : ", serial " + sn);
                _status.ForeColor = Lime;
                _write.Enabled = true;
            }
            else
            {
                _status.Text = _count == 0 ? "no gun detected - plug one in" : _count + " guns detected - unplug all but one";
                _status.ForeColor = Amber;
                _write.Enabled = false;
            }
        }

        void DoWrite()
        {
            string value = (_sn.Text ?? "").Trim();
            if (value.Length == 0) { _result.ForeColor = Amber; _result.Text = "type a serial first"; return; }
            _write.Enabled = false;
            _result.ForeColor = Fg;
            _result.Text = "writing " + value + " ...";
            Application.DoEvents();
            string path = HidIdentity.SingleVendorPath(out int count);
            if (count != 1)
            {
                _result.ForeColor = Amber;
                _result.Text = "refused: need exactly one gun plugged in (found " + count + ")";
                RefreshState();
                return;
            }
            bool ok = HidIdentity.SetSn(path, value, out string ack, out string error);
            string readback = HidIdentity.QuerySnOnce(path);
            if (ok)
            {
                _result.ForeColor = Lime;
                _result.Text = "wrote \"" + value + "\" (" + ack + ")\n" +
                               "reads back: " + (string.IsNullOrEmpty(readback) ? "?" : readback) + "\n" +
                               "Unplug the gun and plug the next one in.";
                _log?.Invoke("sn write: " + value + " ack=" + ack + " readback=" + (readback ?? "?"));
            }
            else
            {
                _result.ForeColor = Amber;
                _result.Text = "write failed: " + error + "\nreads back: " + (string.IsNullOrEmpty(readback) ? "?" : readback);
                _log?.Invoke("sn write: " + value + " FAILED " + error);
            }
            RefreshState();
        }
    }
}
