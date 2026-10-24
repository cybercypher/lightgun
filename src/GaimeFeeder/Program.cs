using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace GaimeFeeder
{
    // GaimeFeeder (console): each G'AIM'E gun -> one player on the Pico bridge
    // (absolute mouse 1/2). Identity key is the on-device system_info.json
    // serial (sn 11111/22222); USB serials randomize per boot and are unusable.
    //
    //   GaimeFeeder --list          show gun sns + HID routing, exit
    //   GaimeFeeder                 calibrate (if needed) + feed
    //
    // Requires: the Pico bridge (pico/gun_bridge). F12 = recalibrate, ESC = quit.
    // Console keys: T/G pulse P1/P2 trigger, arrows pulse P1 aim to a screen
    // edge, 1-4 pulse P1 A/B/Coin/Start. Do not run with GaimeTray at the same
    // time: both would feed the Pico.
    static class Program
    {
        static readonly Dictionary<string, GunState> Guns = new Dictionary<string, GunState>();
        static readonly Dictionary<string, (int x, int y)> LatestRaw = new Dictionary<string, (int x, int y)>();
        static readonly Queue<string> PendingCalib = new Queue<string>();
        static CalibrationWindow ActiveCalib;
        static string ActiveCalibSn;
        static System.Threading.Mutex _mutex;
        static Form MainWin; // marshals console hotkeys onto the UI thread

        [STAThread]
        static void Main(string[] args)
        {
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
            Console.WriteLine(sns.Count == 0
                ? "No guns yet: plug one in, hot-plug attaches it within ~10 s."
                : "Guns: " + string.Join(", ", sns));

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

            var win = new RawInputWindow(OnPointer, OnKey);
            RawInputWindow.Log = s => Console.WriteLine("raw: " + s);
            MainWin = win;
            win.Load += (s, e) => CalibrateNext();
            Console.WriteLine("Feeding. F12 = recalibrate, ESC = quit (feeder console focused).");
            Console.WriteLine("Hot-plug on: plug a gun any time, it attaches within ~10 s.");
            var keys = new System.Threading.Thread(ReadKeys) { IsBackground = true };
            keys.Start();
            StartPoll();
            StartHotplug();
            Application.Run(win);
        }

        static void CalibrateNext()
        {
            if (ActiveCalib != null || PendingCalib.Count == 0) return;
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

        // Hot-plug: launcher-first flow. The feeder snapshots guns at startup,
        // so a gun plugged after launch was previously ignored forever. A
        // background thread re-enumerates sns every HotplugRescanMs;
        // newcomers attach on the UI thread (cal windows + RawInput map are
        // UI-thread affine).
        // Removal needs no action: the GunState stays and just stops
        // receiving reports.
        //
        // Rescan interval is deliberately slow: each poll opens the gun's
        // vendor interface and sends a get_info query, and the gun writes a
        // ~85-byte heartbeat line to its /app log for EVERY query. At 2 s that
        // is ~43k lines/day (~3.7 MB), which filled the gun's 3.9 MB /app
        // partition and killed its app layer. Slower here is strictly better;
        // 10 s still attaches a hot-plugged gun promptly.
        const int HotplugRescanMs = 10000;

        static void StartHotplug()
        {
            var t = new System.Threading.Thread(() =>
            {
                while (true)
                {
                    System.Threading.Thread.Sleep(HotplugRescanMs);
                    List<string> sns;
                    try { sns = HidIdentity.EnumerateGunSns(); }
                    catch { continue; }
                    List<string> fresh = null;
                    lock (Guns)
                        fresh = sns.Where(s => !Guns.ContainsKey(s)).ToList();
                    try { RawInputWindow.SetSnMap(HidIdentity.PathSuffixToSn()); }
                    catch { }
                    if (fresh.Count == 0) continue;
                    try { MainWin?.BeginInvoke(new Action(() => AttachGuns(fresh))); }
                    catch { }
                }
            })
            { IsBackground = true };
            t.Start();
        }

        static void AttachGuns(List<string> sns)
        {
            // Runs on the UI thread (see StartHotplug BeginInvoke).
            var allKnown = Guns.Keys.Concat(sns).Distinct().ToList();
            var numbering = GunMap.Load(allKnown);
            foreach (var s in sns)
            {
                if (Guns.ContainsKey(s)) continue;
                int n = numbering.TryGetValue(s, out var nn) ? nn : 1;
                Guns[s] = new GunState(s, n, $"Gun {n} (sn{s})");
                LatestRaw[s] = (5000, 5000);
                var c = RectCalib.Load(s);
                if (c == null) PendingCalib.Enqueue(s);
                else Guns[s].Calib = c;
                Console.WriteLine($"Hot-plug: attached {Guns[s].Label} (cal {(c == null ? "needed - aim + trigger" : "loaded")}).");
            }
            try { RawInputWindow.SetSnMap(HidIdentity.PathSuffixToSn()); } catch { }
            CalibrateNext();
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
            try
            {
            // The gun is a system digitizer, so Windows has just moved the OS
            // cursor from this report. Put it back before any calibration/feed
            // gating so the pointer never moves, calibrated or not.
            RawInputWindow.PinCursor();

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
            catch (Exception ex) { Console.WriteLine($"OnPointer ERROR: {ex.GetType().Name}: {ex.Message}"); }
        }

        static void OnKey(string sn, int vkey, bool down)
        {
            try
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
            catch (Exception ex) { Console.WriteLine($"OnKey ERROR: {ex.GetType().Name}: {ex.Message}"); }
        }

        static void Feed(GunState g) => FeedCore.Feed(g);

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
            Console.WriteLine("Keys: T/G = pulse P1/P2 trigger; Arrows = pulse P1 aim to a screen edge; 1/2/3/4 = pulse P1 A/B/Coin/Start. (Console focused only — game keys never reach the feeder.)");
            while (true)
            {
                var k = Console.ReadKey(intercept: true).Key;
                // Console keys arrive only when THIS console is focused, so game
                // input (incl. the emulator's own ESC pause menu) can never quit us.
                if (k == ConsoleKey.Escape) { MainWin?.BeginInvoke(new Action(() => OnHotkey(2))); continue; }
                if (k == ConsoleKey.F12) { MainWin?.BeginInvoke(new Action(() => OnHotkey(1))); continue; }
                List<GunState> ordered;
                lock (Guns) ordered = Guns.Values.OrderBy(v => v.PadIndex).ToList();
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
                    Console.WriteLine($"{g1.Label}: aim pulse {k}");
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
