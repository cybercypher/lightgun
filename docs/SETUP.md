# Getting a G'AIM'E two-gun rig working

This is the whole path from bare guns to two-player lightgun games on a PC,
including the order the calibrations have to be done in. It is written for the
hardware this repo was built against (G'AIM'E guns + a Raspberry Pi Pico 2), but
the PC side is the same for any absolute-mouse bridge.

- G'AIM'E guns: USB `VID 2E2C / PID 0631`, 5 interfaces (keyboard, digitizer,
  vendor `0xFF00`, two UVC). Aim comes from the gun itself (IMU + camera/NPU).
- Pico bridge: `VID 1209 / PID 0A11`, two absolute HID mice + one CDC serial
  port. Mouse 1 = player 1, mouse 2 = player 2, CDC = the feed protocol.
- Tray app `GaimeTray`: reads both guns, applies the PC-side calibration, and
  writes the Pico frames. It is the only program that talks to the guns.

## What you need

| Item | Notes |
|---|---|
| 2 × G'AIM'E gun | any firmware that reports `system_info.json sn` |
| Raspberry Pi Pico 2 | the bridge firmware is in `pico/gun_bridge`; a prebuilt `gun_bridge.uf2` is attached to the release |
| Windows 10/11 x64 | 100 % display scaling (a scaled desktop breaks absolute aiming) |
| TeknoParrot and/or DemulShooter | whichever your games need |
| The tray release | `GaimeTray-win-x64.zip` from the release page — self-contained, no .NET install needed |

## 1. Flash the bridge

The Pico presents two absolute mice and a CDC port. Build from source
(`pico/gun_bridge`, Pico SDK 2.2.0, `cmake` + `ninja`) or flash the
`gun_bridge.uf2` from the release: hold BOOTSEL, plug the Pico in, copy the
`.uf2` to the `RPI-RP2` drive. Its device serial is fixed
(`GAIME-BRIDGE-0001`), so it keeps its identity across ports.

Check it in Device Manager: `G'AIM'E Bridge Mouse 1`, `G'AIM'E Bridge Mouse 2`,
and a COM port (the feed channel). Note the COM number — the tray finds it
itself, but you will want it for troubleshooting.

## 2. Unzip the tray and start it

Unzip `GaimeTray-win-x64.zip` anywhere, for example `C:\games\feeder`, and run
`GaimeTray.exe`. It starts windowed the first time and closes to the task tray.
`guns.json` sits next to the exe:

```json
{ "guns": { "11111": 1, "22222": 2 } }
```

That maps a gun's **on-device serial** to a player number. If your guns do not
report `11111` / `22222` yet, do step 3 first, then fix this file.

### The setup wizard

On the first start (no `setup.json` next to the exe yet) the tray opens a
step-by-step wizard. It walks steps 3–7 below in the right order, so you can
follow it instead of this page. Reopen it any time from the tray menu
(**Setup…**) or the Log tab (**Setup wizard**).

| Page | What it does |
|---|---|
| System check | display scaling 100 %, both bridge mice, the bridge COM port, guns connected; **Flash bridge…** copies `gun_bridge.uf2` to a Pico held in BOOTSEL |
| Guns | lists the connected guns by serial, flags guns that share a serial, **Write serial…** (one gun at a time, §3), assigns Player 1 / 2 and saves `guns.json` |
| Calibrate | per gun: **1 · Box Cal → 2 · Host grid → 3 · Test**, with the time of each and a warning when the host grid is older than the last Box Cal or was made at another resolution |
| Games | DemulShooter folder + **Bind to bridge** (kept bound on every tray start); TeknoParrot folder, which profiles have both guns bound, and **Bind to guns**; the ReShade white border for ElfLdr2 games; the list of game processes that turn the bridge on (`bridge-processes.txt`) |
| Finish | summary, start with Windows |

While the wizard is open, a gun without a host calibration does not pop the
calibration screen by itself — the Calibrate page runs it after Box Cal. The
PC records each Box Cal it sends in `calibrations/boxcal_<serial>.json`; the
main window's gun card shows the same "redo the host grid" warning.

## 3. Give each gun its serial

The tray identifies a gun by the serial stored inside the gun (not the USB
serial, which changes on every replug). The Log tab has a **Write S/N** button
for exactly this (the wizard's Guns page opens the same dialog).

1. Plug in **one** gun. The dialog shows `detected 1 gun, serial …`.
2. Type `11111`, press **Write**. It prints the ack and the serial it reads
   back — check that the readback matches.
3. Unplug it, plug in the second gun, write `22222`.
4. Replug both. The tray's two cards should show one gun each.

It refuses whenever more than one gun is plugged in: the write goes to "the gun
that is connected", so one at a time is the only safe way.

## 4. Calibration — do it in this order

There are **two** calibrations and they are not interchangeable:

| | What it is | Where it lives | What it does |
|---|---|---|---|
| **Gun-side (Box Cal)** | the gun's own 8-point calibration | inside the gun | tells the gun what "screen corners" mean. It **changes the numbers the gun reports**. |
| **PC-side (host grid)** | 9-point grid fitted on the PC | `calibrations/cal_<serial>.json` | maps the numbers the gun reports to screen pixels |

**The order is gun-side first, PC-side second, for each gun.** The PC-side fit
is built on top of whatever the gun reports, so a Box Cal invalidates any
PC-side calibration made before it. Redo the PC-side calibration after **every**
Box Cal. Doing it the other way round silently bakes in the old gun behaviour
and the aim drifts.

The white reference border (32 px) is on by default in both screens. Leave it
alone during calibration — it is part of the reference.

The border is one setting (wizard → Calibrate, **Turn border off/on**; the
Games page button is the same switch). It covers the tray screens and the
ReShade border in games together, and is kept in `setup.json`. Every Box Cal
and host grid records the border state it was made under; after a change the
Calibrate page and the gun card mark each gun **full recalibration** until it
has a new Box Cal and grid. Box Cal and the grid always start with the
configured border, and a **V** / **B** test toggle ends when the gallery or the
grid closes. If the border was toggled during a run, that run is flagged.

### 4a. Gun-side: Box Cal (once per gun, redo whenever you like)

1. Tray → **Test** (or Ctrl+T) opens the fullscreen gallery.
2. With the gun you want to calibrate in hand, press its **Start** button (or
   the **K** key) to enter Box Cal. The first gun to pull the trigger locks the
   run to that gun.
3. Aim at each of the 8 lime targets and pull the trigger **3 times** per
   target (1 tap per 400 ms; 8 targets, 3 taps = 24 taps).
4. The gallery builds the 8 vendor reports and sends them to the gun. The log
   shows `sent 8 reports`.
5. Repeat for the other gun. Each gun stores its own calibration; there is no
   file on the PC for this step.

### 4b. PC-side: host grid (once per gun, after its Box Cal)

1. Back in the tray, press that gun's **Calibrate** button (or **F12** for the
   first gun). The screen shows 9 targets: a 3×3 grid inset 1/14 of the screen
   width from the edges.
2. Pull the trigger **3 times** at each target, 27 samples in total, holding the
   gun the way you will hold it while playing (stand on the same spot each
   time).
3. The tray fits least squares per axis, drops outliers once, and writes
   `calibrations/cal_<serial>.json`. The log line says how many samples were
   kept and the median residual, e.g. `calibrated (27 samples, 25 kept, median
   residual 34 px)`.
4. Repeat for the other gun. Both guns keep separate files.

### 4c. Check before you leave the gallery

Open **Test** again and shoot 20–40 shots with each gun. Every pull logs

```
gallery [shot]: Gun 1 (sn11111) miss 44 px HIT target (1690,687) shot (1666,724)
```

The median miss is the number that matters; anything under ~60 px at 4K is
inside a normal target. If a gun is much worse than that, redo 4b for that gun
(and 4a first if you changed anything on the gun side).

### Re-calibration rules

| Event | What to redo |
|---|---|
| You ran Box Cal | the PC-side grid for that gun (4b) |
| White border turned on or off | **both** steps for **every** gun: Box Cal (4a), then the grid (4b) — the gun's camera sees the border |
| Gun dropped / mount knocked / new player stance | the PC-side grid (4b) |
| Screen resolution or Windows scaling changed | reboot-level: fix scaling to 100 %, then 4b for both |
| New gun or a gun with a wiped serial | write the serial (step 3), then 4a, then 4b |
| Nothing changed | nothing — calibrations persist per serial |

## 5. TeknoParrot

1. Add the game in TP once (so `UserProfiles\<Game>.xml` exists), then close TP.
   In the wizard's Games page select the game and press **Bind to guns**. It
   sets **Input API = RawInput** and writes every gun row below for both
   players (with a timestamped backup); operator rows (Test, Service, Volume,
   test-menu navigation) keep what you bound. Start/Coin go to the gun's own
   Start/Coin buttons (Button5/Button4). The manual route, for reference:
2. In `UserProfiles\<Game>.xml`, bind the lightguns to the bridge mice. TP's
   controller page cannot capture left/right/middle for a binding and it
   overwrites hand edits, so edit the XML with TP closed:

   | field | binding |
   |---|---|
   | P1 Light Gun | Bridge Mouse 1, `MouseButton None` |
   | Trigger P1 | Bridge Mouse 1, `LeftButton` |
   | Reload P1 | Bridge Mouse 1, `RightButton` |
   | Grenades P1 | Bridge Mouse 1, `MiddleButton` |
   | Start Button P1 | Bridge Mouse 1, `Button5` (gun Start) |
   | Coin | Bridge Mouse 1, `Button4` (gun 1 Coin) |
   | P2 Light Gun / Trigger / Reload / Grenades / Start | same on Bridge Mouse 2 |

   The device path looks like
   `\\?\HID#VID_1209&PID_0A11&MI_00#a&1b234567&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}`
   for mouse 1 and `MI_01` for mouse 2. `RawMouseButton` accepts exactly
   `None, LeftButton, RightButton, MiddleButton, Button4, Button5` — anything
   else makes TP offer to delete the profile.
3. Start TP, launch the game from its library. TP reads profiles at startup;
   a hand edit needs a TP restart.
4. If player 2 does nothing at all, the P2 rows are probably present but have no
   `RawInputButton` — that is the single most common "gun 2 buttons don't work"
   cause. Bind them as above.

## 6. DemulShooter

DS matches devices by exact RawInput path and only at startup. In the wizard's
Games page, set the DemulShooter folder and press **Bind to bridge**: it rewrites
the P1/P2 paths in `config.ini` to the bridge mice (with a timestamped backup)
and DS then shows `G'AIM'E / G'AIM'E Bridge Mouse 1/2`.

From then on the tray checks `config.ini` every time it starts and re-binds it
if the bridge paths changed, so there is no launcher to run. Start DS **after**
the game window is up, as usual.

## 7. The white border in games (optional but recommended)

Games render at whatever aspect they like; a constant white frame gives you (and
the gun's optical path) the same reference as the calibration screens. With
TeknoParrot's ELF loader (Raw Thrills / Linux titles), use **Install border**
on the wizard's Games page. Download the standard ReShade from reshade.me (not
the add-on build) and pick the `ReShade_Setup_*.exe` when asked: the wizard
takes `ReShade32.dll` out of it, so the ReShade installer never has to run.
A `ReShade32.dll` picked directly works too. ReShade itself is not shipped with
the tray; reshade.me asks for the binaries not to be redistributed.

It installs ReShade as an OpenGL proxy next to `ElfLdr2\BudgieLoader.exe` with a
border of the same thickness as the tray's own (`ScreenBorder.Pixels`, 32 px),
applied on launch — no Home key, no overlay. It covers every ElfLdr2 game.
**Turn off / Turn on** swaps it out and back in without reinstalling (from the
next game launch): off renames our `opengl32.dll` to `opengl32.dll.gaime-off`,
so the game uses the normal Windows OpenGL again — stock `ElfLdr2` has no
`opengl32.dll` of its own. If another `opengl32.dll` was already there, Install
keeps it as `opengl32.dll.gaime-orig` and it is back in place whenever the border
is off. **Remove** takes everything out and restores that original. See
`docs/white-border.md`.

## 8. Auto-start the game (optional)

The wizard's **Auto-start** page turns the PC into a cabinet: after a reboot it
comes back to the game by itself.

1. Pick the **Game** (a TeknoParrot profile, bound with **Bind to guns**).
2. Tick **Launch this game when the tray starts** and **Start it again if it
   closes**.
3. Tick **Start GaimeTray with Windows**.
4. Fix the rows under *Coming back after a reboot*:
   - **Automatic sign-in** — without it a reboot stops at the sign-in screen
     and nothing starts. **Sign-in settings** opens `netplwiz`: untick *Users
     must enter a user name and password*. If that box is missing, first turn
     off *only allow Windows Hello sign-in* in Settings → Accounts → Sign-in
     options. The wizard never stores your password itself.
   - **Sleep / screen off** — **Never sleep** sets both to never while plugged
     in (a sleeping PC drops the guns).

What happens at start: the tray stays in the task tray (its window would take
the foreground from the game), waits up to 2 minutes for the bridge and the
guns in `guns.json`, and does not launch while a gun needs calibration or the
setup wizard is open (a balloon says why). Then it runs
`TeknoParrotUi.exe --profile=<Game>.xml`, brings the game to the front for its
first 30 s (the bridge only streams to a foreground game), and watches the game
process (`BudgieLoader` for ElfLdr2 games). If the game closes, it is started
again 5 s later; three exits within 2 minutes of their launch in a row stop the
restarts and show the window instead.

**Leaving the game:** hold **Start + Coin** on either gun for 5 seconds (the
time is set on the page). The game closes, auto-restart stops for this session
and the tray window opens. The tray menu has **Stop auto-restart** (leaves the
game running) and **Start game now**. The next start of the tray resumes
auto-start. Holding Start + Coin also sends both buttons to the game for those
seconds.

## 9. Troubleshooting

| Symptom | Cause |
|---|---|
| "no guns yet" and no cards | the gun's app layer is dead (its `/app` filesystem full) or it is unplugged. A dead app layer looks exactly like an unplugged gun. |
| Cards appear but aim never moves | the gun is not reporting the digitizer; check Device Manager for the HID digitizer interface |
| Aim works in the gallery, not in the game | the game is reading a different device. TP: P1/P2 Light Gun must be the bridge mice, Input API = RawInput |
| Gun 2 does nothing in a game | its TP bindings are unbound (see §5.4), or the game only supports one player |
| Cursor jumps around while nothing is running | that is the gun's digitizer moving the Windows cursor; the tray only pins it while a gun is locked. The bridge does not stream on the desktop. |
| Aim drifts over a session | IMU drift with the camera path not locking; redo the PC-side grid (§4b) and check the border is on |
| Aim is scaled/wrong after redoing Box Cal | the PC-side grid was not redone. Order matters (§4) |
| Border missing in an ElfLdr2 game | check `C:\games\teknoparrot\ElfLdr2\ReShade.log` for `loaded from '...OPENGL32.dll'` and `Successfully compiled ... GaimeBorder.fx` |

Logs: `logs\tray-<date>.log` (guns, calibration, shots, noise),
`logs\bridge-<date>.log` (when the Pico feed switches on/off), and
`ElfLdr2\ReShade.log` for the border.

## Where things live

```
GaimeTray.exe            tray app (this is the only program to run)
guns.json                serial -> player 1/2
setup.json               setup wizard: TeknoParrot / DemulShooter folders, finished flag
calibrations/            cal_<serial>.json, the PC-side calibration;
                         boxcal_<serial>.json, when this PC last sent a Box Cal
logs/                    tray-<date>.log, bridge-<date>.log
latch-ms.txt             optional: press-latch before the trigger, ms (absent = 0)
boxcal-headroom.txt      optional: Box Cal headroom percent (absent = 0)
```

`docs/` holds the protocol, hardware and measurement notes behind these
decisions (start with `docs/calibration-analysis-2026-10-24.md` for why the two
calibrations exist and what the accuracy numbers are).
