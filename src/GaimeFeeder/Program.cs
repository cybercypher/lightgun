using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace GaimeFeeder
{
    // GaimeFeeder: each G'AIM'E gun -> its own ViGEm Xbox360 pad (J1/J2).
    // Identity key is the on-device system_info.json serial (sn 11111/22222),
    // written once per gun; USB serials randomize per boot and are unusable.
    //
    //   GaimeFeeder --list          show gun sns + HID routing, exit
    //   GaimeFeeder                 calibrate (if needed) + feed
    //
    // Requires: ViGEmBus installed. F12 = recalibrate, ESC = quit.
    // Console keys: T/G pulse P1/P2 trigger, Arrows pulse P1 stick, 1-4 pulse P1 buttons.
    static class Program
    {
        static readonly Dictionary<string, GunState> Guns = new Dictionary<string, GunState>();
        static readonly Dictionary<string, VigemPad> Pads = new Dictionary<string, VigemPad>();
        static readonly Dictionary<string, (int x, int y)> LatestRaw = new Dictionary<string, (int x, int y)>();
        static readonly Queue<string> PendingCalib = new Queue<string>();
        static CalibrationWindow ActiveCalib;
        static string ActiveCalibSn;
        static System.Threading.Mutex _mutex;

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Contains("--xcheck"))
            {
                XCheck.Run();
                return;
            }
            bool fresh;
            _mutex = new System.Threading.Mutex(true, "GaimeFeederSingleInstance", out fresh);
            if (!fresh)
            {
                Console.WriteLine("Another GaimeFeeder is already running. Kill it first (ESC in its window).");
                Console.WriteLine("Press any key to exit.");
                Console.ReadKey();
                return;
            }
            var sns = HidIdentity.EnumerateGunSns();
            if (args.Contains("--list"))
            {
                Console.WriteLine("guns: " + (sns.Count == 0 ? "(none)" : string.Join(", ", sns)));
                var numbering0 = GunMap.Load(sns);
                foreach (var kv in numbering0.OrderBy(kv => kv.Value))
                    Console.WriteLine($"  Gun {kv.Value} = sn {kv.Key}");
                foreach (var kv in HidIdentity.PathSuffixToSn())
                    Console.WriteLine($"  {kv.Key} -> sn {kv.Value}");
                return;
            }
            if (sns.Count == 0)
            {
                // Binding mode: no guns needed. Pads exist for T/G test pulses only.
                Console.WriteLine("No guns found: binding mode (virtual pads only, T/G pulses).");
                Guns["bind1"] = new GunState("bind1", 1, "Pad 1 (no gun)");
                Guns["bind2"] = new GunState("bind2", 2, "Pad 2 (no gun)");
                try
                {
                    Pads["bind1"] = new VigemPad(1);
                    Pads["bind2"] = new VigemPad(2);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Pad creation FAILED: {ex.Message}");
                    Console.WriteLine("Is ViGEmBus installed? https://github.com/ViGEm/ViGEmBus/releases");
                    Console.WriteLine("Press any key to exit.");
                    Console.ReadKey();
                    return;
                }
                var win0 = new RawInputWindow(OnPointer, OnKey, OnHotkey);
                win0.FormClosed += (s, e) => CleanupPads();
                Console.WriteLine("Keys: T = pulse Pad 1 trigger, G = pulse Pad 2 trigger. ESC = quit.");
                var keys0 = new System.Threading.Thread(ReadKeys) { IsBackground = true };
                keys0.Start();
                StartPoll();
                Application.Run(win0);
                return;
            }
            Console.WriteLine("Guns: " + string.Join(", ", sns));

            var numbering = GunMap.Load(sns);
            foreach (var kv in numbering.OrderBy(kv => kv.Value))
                Console.WriteLine($"  Gun {kv.Value} = sn {kv.Key}");

            foreach (var s in sns)
            {
                int n = numbering[s];
                Guns[s] = new GunState(s, n, $"Gun {n} (sn{s})");
                LatestRaw[s] = (5000, 5000);
                if (RectCalib.Load(s) == null) PendingCalib.Enqueue(s);
                else Guns[s].Calib = RectCalib.Load(s);
            }
            RawInputWindow.SetSnMap(HidIdentity.PathSuffixToSn());

            var win = new RawInputWindow(OnPointer, OnKey, OnHotkey);
            win.FormClosed += (s, e) => CleanupPads();
            win.Load += (s, e) => CalibrateNext();
            Console.WriteLine("Feeding. F12 = recalibrate, ESC = quit.");
            var keys = new System.Threading.Thread(ReadKeys) { IsBackground = true };
            keys.Start();
            StartPoll();
            Application.Run(win);
        }

        static void CalibrateNext()
        {
            if (ActiveCalib != null || PendingCalib.Count == 0)
            {
                EnsurePads();
                return;
            }
            string sn = PendingCalib.Dequeue();
            ActiveCalibSn = null; // first trigger locks to whichever gun is used
            ActiveCalib = new CalibrationWindow("Calibrating " + Guns[sn].Label,
                () => LatestRaw[sn]);
            ActiveCalib.FormClosed += (s, e) =>
            {
                string target = ActiveCalibSn ?? sn;
                if (ActiveCalib.DialogResult == DialogResult.OK && ActiveCalib.Result != null
                    && ActiveCalib.Result.IsValid)
                {
                    ActiveCalib.Result.Save(target);
                    Guns[target].Calib = ActiveCalib.Result;
                    Console.WriteLine($"{Guns[target].Label}: calibrated.");
                }
                else if (ActiveCalib.DialogResult == DialogResult.OK)
                {
                    // Finished but samples are garbage (gun wasn't streaming).
                    // Skip rather than loop forever; F12 retries.
                    Console.WriteLine($"{Guns[target].Label}: bad samples, skipped (F12 to retry).");
                }
                else
                {
                    // User cancelled: skip this gun, keep feeding the rest.
                    Console.WriteLine($"{Guns[target].Label}: calibration skipped (F12 to retry).");
                }
                ActiveCalib = null;
                CalibrateNext();
            };
            ActiveCalib.Show();
        }

        static void EnsurePads()
        {
            foreach (var g in Guns.Values)
                if (g.Calib != null && !Pads.ContainsKey(g.Serial))
                {
                    try { Pads[g.Serial] = new VigemPad(g.PadIndex); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Pad {g.PadIndex} FAILED: {ex.Message}");
                        Console.WriteLine("Is ViGEmBus installed? https://github.com/ViGEm/ViGEmBus/releases");
                        Console.WriteLine("Press any key to exit.");
                        Console.ReadKey();
                        Environment.Exit(1);
                    }
                }
        }

        static void OnHotkey(int id)
        {
            if (id == 2)
            {
                // ESC cancels calibration first, quits when nothing is open.
                if (ActiveCalib != null)
                {
                    ActiveCalib.DialogResult = DialogResult.Cancel;
                    ActiveCalib.Close();
                }
                else Application.Exit();
                return;
            }
            if (id == 1 && ActiveCalib == null && Guns.Count > 0)
            {
                // Recalibrate: prefer a gun with no calibration, else Gun 1.
                var target = Guns.Values.OrderBy(g => g.PadIndex)
                    .FirstOrDefault(g => g.Calib == null) ?? Guns.Values.OrderBy(g => g.PadIndex).First();
                PendingCalib.Enqueue(target.Serial);
                CalibrateNext();
            }
        }

        static void OnPointer(string sn, int x, int y, bool tip)
        {
            LatestRaw[sn] = (x, y);
            if (!Guns.TryGetValue(sn, out var g) || g.Calib == null) return;
            bool wasDown = g.TriggerDown;
            g.PushPointer(x, y, tip, true);
            if (g.TriggerDown != wasDown)
                Console.WriteLine($"{g.Label}: trigger {(g.TriggerDown ? "DOWN" : "UP")} raw=({x},{y}) tip={tip} " +
                    (g.TriggerDown ? $"pressSpan={g.LastPressSpanMs:F1}ms" : $"quiet={g.LastReleaseQuietMs:F1}ms"));
            else if (g.LastBlip > 0)
            {
                Console.WriteLine($"{g.Label}: tip blip ({g.LastBlip} report(s), too short to press)");
                g.LastBlip = 0;
            }
            if (ActiveCalib != null && g.TriggerDown && !wasDown)
            {
                // First trigger locks calibration to whichever gun was used.
                if (ActiveCalibSn == null)
                {
                    ActiveCalibSn = sn;
                    ActiveCalib.LockTo(sn, Guns[sn].Label, () => LatestRaw[sn]);
                }
                if (sn == ActiveCalibSn) ActiveCalib.TriggerSample();
            }
            Feed(g);
        }

        static void OnKey(string sn, int vkey, bool down)
        {
            if (!Guns.TryGetValue(sn, out var g)) return;
            // docs/buttons.md: A=SPACE(0x20), B=0x42, Coin=C(0x43), Start=Q(0x51)
            switch (vkey)
            {
                case 0x20: g.BtnA = down; break;
                case 0x42: g.BtnB = down; break;
                case 0x43: g.BtnCoin = down; break;
                case 0x51: g.BtnStart = down; break;
            }
            Feed(g);
        }

        static void Feed(GunState g)
        {
            if (!Pads.TryGetValue(g.Serial, out var p)) return;
            bool testTrig = DateTime.UtcNow < g.TestTriggerUntil;
            bool testStick = DateTime.UtcNow < g.TestStickUntil;
            bool testBtn = DateTime.UtcNow < g.TestBtnUntil;
            if (g.Calib == null)
            {
                // Binding mode: submit only while a test pulse is active.
                if (testTrig || testStick || testBtn)
                    p.Feed(960 + (testStick ? g.TestStickX * 900 : 0),
                           540 + (testStick ? g.TestStickY * 500 : 0),
                           1920, 1080, true, g, testTrig);
                return;
            }
            bool down = g.TriggerDown || testTrig;
            var (sx, sy, inside) = g.Calib.Map(down && !testTrig ? g.LatchedX : g.RawX,
                                               down && !testTrig ? g.LatchedY : g.RawY);
            if (testStick)
            {
                sx = (g.Calib.ScreenW - 1) / 2 + g.TestStickX * (g.Calib.ScreenW - 1) / 2;
                sy = (g.Calib.ScreenH - 1) / 2 + g.TestStickY * (g.Calib.ScreenH - 1) / 2;
            }
            p.Feed(sx, sy, g.Calib.ScreenW, g.Calib.ScreenH, inside, g, down);
        }

        static void CleanupPads()
        {
            foreach (var p in Pads.Values)
                try { p.Dispose(); } catch { }
            Pads.Clear();
        }

        // Poll loop: Feed is otherwise event-only (pointer/key reports), so test
        // pulses (T/G) expire unseen when the gun is idle. Submit at ~60 Hz.
        static void StartPoll()
        {
            var t = new System.Threading.Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        lock (Guns)
                            foreach (var g in Guns.Values)
                                Feed(g);
                    }
                    catch { }
                    System.Threading.Thread.Sleep(16);
                }
            })
            { IsBackground = true };
            t.Start();
        }

        static void ReadKeys()
        {
            Console.WriteLine("Keys: T/G = pulse P1/P2 trigger; Arrows = pulse P1 stick; 1/2/3/4 = pulse P1 A/B/Back/Start.");
            while (true)
            {
                var k = Console.ReadKey(intercept: true).Key;
                var ordered = Guns.Values.OrderBy(v => v.PadIndex).ToList();
                var g1 = ordered.ElementAtOrDefault(0);
                var g2 = ordered.ElementAtOrDefault(1);
                if ((k == ConsoleKey.T || k == ConsoleKey.G) && (k == ConsoleKey.T ? g1 : g2) is GunState gt)
                {
                    gt.TestTriggerUntil = DateTime.UtcNow.AddMilliseconds(400);
                    Console.WriteLine($"{gt.Label}: test pulse");
                }
                else if (g1 != null && (k == ConsoleKey.LeftArrow || k == ConsoleKey.RightArrow ||
                         k == ConsoleKey.UpArrow || k == ConsoleKey.DownArrow))
                {
                    g1.TestStickX = k == ConsoleKey.LeftArrow ? -1 : k == ConsoleKey.RightArrow ? 1 : 0;
                    g1.TestStickY = k == ConsoleKey.UpArrow ? -1 : k == ConsoleKey.DownArrow ? 1 : 0;
                    g1.TestStickUntil = DateTime.UtcNow.AddMilliseconds(600);
                    Console.WriteLine($"{g1.Label}: stick pulse {k}");
                }
                else if (g1 != null && k >= ConsoleKey.D1 && k <= ConsoleKey.D4)
                {
                    g1.TestBtnMask = 1 << (k - ConsoleKey.D1);
                    g1.TestBtnUntil = DateTime.UtcNow.AddMilliseconds(400);
                    Console.WriteLine($"{g1.Label}: button pulse {k}");
                }
            }
        }
    }
}
