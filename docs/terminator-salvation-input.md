# Terminator Salvation (TP + Pico bridge) — input findings, 2026-10-24

Everything here was measured on the machine, from screenshots, the game's own
syslog, TP's profile XML and DemulShooter's verbose log. Hypotheses are marked.

## TL;DR

- **TP alone drives the game.** With `P1 Light Gun` set to the Pico bridge
  mouse, TP feeds aim and the trigger; no DemulShooter is needed for
  Terminator Salvation. The game's own log confirms a real 1-player game:
  `GameState 1 GameRec type 0 size 0 numpl 1`.
- **Aim is accurate to about 1 % and 10 px.** Measured with a cyan-reticle
  detector at 3840x2141: `reticle_x = 0.996*aim_x - 2`, `reticle_y =
  1.008*aim_y - 17`. That is inside the game's own reticle smoothing and well
  under a target.
- **Reload (right) and grenades (middle) reach the game.** The reload press
  advanced the game's own "RELOAD" instruction screen.
- **Start/Coin bindings do not reach the game.** Bound as mouse Button4/5 and
  then as keyboard Q/C — neither moves the game. The mission-select "PRESS
  START TO JOIN" banner never clears on a Start press. *Hypothesis:* TP's
  RawThrills gun-board emulation consumes only the lightgun device's axes and
  left button, and its generic button bindings are not wired into that board.
- **The feeder dropped the top two button bits.** `PicoOutput.Send()` masked
  the button byte with `0x07`, so Coin (0x08) and Start (0x10) never left the
  PC. Fixed to `0x1F` and deployed.
- **Starting a game works from the gun:** at Mission Select, the trigger
  selects the mission card; the game then logs a 1P game state and plays the
  mission intro.
- **Shoot-and-move works.** The trigger no longer freezes the aim: the tray
  feeder was holding the pre-press latch for the whole hold (fixed 10:31,
  confirmed in-game). See the last section.

## The working TP recipe (Terminator Salvation)

`C:\games\teknoparrot\UserProfiles\Terminator.xml`, hand-edited (TP must be
restarted to load edits; opening the controller page and saving overwrites
them):

| field | binding |
|---|---|
| P1 Light Gun | `G'AIM'E Bridge Mouse 1` (DevicePath = `MI_00` of VID_1209/PID_0A11) |
| Trigger P1 | `LeftButton` on the same device |
| Reload P1 | `RightButton` |
| Grenades P1 | `MiddleButton` |
| Start Button P1 | `Button5` (forward) — accepted by the UI but not seen in game |
| Coin | `Button4` (back) — same |
| P2 Light Gun | `G'AIM'E Bridge Mouse 2` (`MI_01`) |
| Trigger P2 | `LeftButton` on mouse 2 |
| Reload P2 | `RightButton` |
| Grenades P2 | `MiddleButton` |
| Start Button P2 | `Button5` |

The P2 rows were **unbound** until 2026-10-24: they existed in the profile with no
`RawInputButton` at all, so TP only ever opened Bridge Mouse 1 and every P2
report was dropped (aim included, not just buttons). Binding them to `MI_01`
made gun 2 work in-game (user-confirmed). Backup:
`logs/Terminator.xml.pre-p2-bind`.

Enum values matter: TP's `RawMouseButton` is
`None, LeftButton, RightButton, MiddleButton, Button4, Button5`. Writing
`Button2`/`Button3` makes TP abort with "Error loading UserProfiles\
Terminator.xml" and offer to delete the profile.

Keyboard bindings carry **no** `DevicePath`, so a keyboard binding matches any
keyboard (including the gun's own `MI_00` keyboard interface).

## Evidence

- Screenshot with the reticle at four corners, measured by
  `tools/aim_probe.py`: deltas -8…-15 px x, -4…-12 px y.
- `Renderer/syslog_bb3.txt` (game log) at 01:22:33 and 01:29:20:
  `COULD NOT FIND PROGRAM s2_a10_1` / `LOCKUP: 0` /
  `GameState 1 GameRec type 0 size 0 numpl 1`. The missing program is
  non-fatal (the mission still runs).
- DS run at 01:32: `OnScreenTriggerDown/Up` logged, but the game stayed in
  attract. DS's injection is not what starts Terminator.
- TP run with the lightgun assigned: same trigger press selected the mission
  and the syslog recorded the 1P game.

## The game's gun hardware (from the game binary)

Terminator Salvation drives **IR camera guns**, not a mouse:

- strings in `game`: `GUN IS NOT CONNECTED`, `GUN CANNOT SEE CABINET LIGHTS`,
  `GUN HAS A DATA READ ERROR`, `GUN HAS SENT A CORRUPTED MESSAGE`,
  `PRESS START TO CONTINUE`, `COIN`.
- device paths in `game`: `/dev/ttyS%u`, `/dev/ttyS0`, `/dev/ttyD0`, `/dev/js0`,
  `/dev/rtc`, `/dev/Hardlock`.
- `D:\Games\terminator\irgun\` holds the gun firmware (`firmware/prom_9b.mcs`,
  `prom_9e.mcs`), `ggver` (an ELF), `fprog`, `gunrec_update`, and the game's own
  `cmgr%06i.txt` link log (created, empty).
- `ElfLdr2`'s loader log (`D:\Games\terminator\teknoparrot_log.txt`) is off by
  default; `ParrotData.xml` -> `<Elfldr2LogToFile>` enables it.

So TeknoParrot's RawThrills profile emulates the IR gun board on top of the
Linux syscall layer. TP's aim reaches that board (the in-game reticle follows
to within ~1 %), but Start/Coin have never been seen to reach it.

## Open (next session, in order)

1. **Wait for the attract's Mission Select, shoot Mission 1, then confirm a
   kill.** The game logs `GameState 1 ... numpl 1` in
   `T4User/log/syslog_bb3.txt` when a game really starts; that is the ground
   truth for "the game is playing". A kill counter increment is the ground
   truth for "shots land".
2. **Start/Coin.** Confirm with the service menu. If TP's board ignores them,
   synthesise keyboard `1`/`5` in the feeder when the gun's Start/Coin go down
   (the Raw Thrills PC builds use those; DS's docs agree). The feeder's mask
   bug (fixed) previously stopped the bits at the Pico anyway.
3. **Do not leave the PC idle:** a session lock killed the game
   ("SIGNAL LOST") once and blocked automation twice.
4. **Reboot stability:** TP stores the bridge's instance path
   (`...MI_00#a&XXXXXXXX&0&0000...`); if the Pico's instance changes on
   replug the lightgun binding needs re-pointing.

## Tools used (committed)

`tools/win/Screen.ps1` (capture), `tools/win/WinInput.ps1` (real mouse/keys),
`tools/win/Uia.ps1` (UI Automation: tree, invoke, focus a setup row, pick a
dropdown item), `tools/win/BridgeServer.ps1` + `tools/win/bridge` (persistent
CDC driver — opening the port per command makes the board re-emit its last
position and hijack the cursor), `tools/win/probe.sh` + `tools/aim_probe.py`
(aim accuracy).


## 2026-10-24: the reticle sticks while the trigger is pressed

Sweeping the gun moves the reticle smoothly; pressing the physical trigger —
a tap or a hold — sticks it at that spot until release.

| link | test | result |
|---|---|---|
| Pico | bridge drove the cursor to four corners with left held | moves |
| feeder | tray Test gallery while holding | **false pass**: the gallery maps `g.RawX/g.RawY` itself and never calls `Feed()`; it also doesn't stream while it is foreground |
| DemulShooter | TP lightgun = None, DS attached, 984 events | reticle stays at centre, DS cannot feed this build |
| trigger pulse | feeder square-wave trigger (160/500 ms) | gun fires in bursts; reticle still sticks — the feeder was sending latched coordinates the whole time |
| gun touch contact | trigger held 10 s while the bridge ran a square pattern, tray stopped | cursor followed, 751 samples, no plateau — Windows does not suppress the Pico |

Holding **B (right / reload) freezes nothing**, so the freeze is tied to the
trigger role, not to buttons on the lightgun device.

## 2026-10-24 10:31 — root cause was the tray feeder

Not TP. `src/GaimeTray/FeederEngine.cs` mapped `LatchedX/Y` for the whole time
`down` was true (`down = g.TriggerDown || testTrig`), so a held physical
trigger froze the coordinates the Pico sent. TP never received a moving
position to accept or reject; this also explains the pulse test — the pulse
changes the Pico button, not `g.TriggerDown`, so the latch stayed applied. There
is **no evidence TP latches anything**, and the `MiddleButton` / second-mouse
tests were not needed.

The 08:50 fix (`7c11de5`) had only changed `src/GaimeFeeder/Program.cs`, which
the tray does not compile; the deployed tray was built from the old
`FeederEngine.cs`. The gallery could not catch it because it maps raw
coordinates directly, and `PicoOutput` only streams while a game is foreground.

Fix: use `PressFramePending` for the press frame only, then live `RawX/RawY`
while held; consume the flag before the uncalibrated return. Built, deployed
and restarted 10:31; confirmed in-game that holding the trigger while sweeping
tracks continuously.

TP input facts confirmed: `Trigger P1` must be a **mouse** button on the
lightgun device (keyboard bindings never reach the RawThrills board, which is
also why Start/Coin on Q/C do nothing), and with no lightgun assigned the game
falls back to a centre reticle.
