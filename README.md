# G'AIM'E Lightgun → Pico bridge (absolute mice) for TeknoParrot / DemulShooter

Goal: make each G'AIM'E gun appear as its **own absolute mouse** on a Pico bridge
(Bridge Mouse 1 / Bridge Mouse 2) so 2 guns work simultaneously in TeknoParrot /
DemulShooter, with calibration. Pattern follows `psakhis/guncon2` (per-gun `index`)
and `gameotaku79/Guncon3Windows` (5-point cal, `mapping.txt`-style buttons).

## Hardware (confirmed on this machine)

- `VID 0x2E2C / PID 0x0631`, `REV_0419`, composite `USB\VID_2E2C&PID_0631\<serial>`
- This unit serial: `<usb-serial>` (parent InstanceId). ContainerId: `{XXXXXXXX-...}` (changes per gun).
- IF0 HID boot keyboard, 8-byte reports — buttons (`A=space, B=b, Coin=c, Start=q` per MP1st, verify).
- IF1 HID digitizer/touchscreen, report ID 1, 6 bytes: `01 <flags> Xlo Xhi Ylo Yhi`,
  X/Y absolute 16-bit, logical 0..10000 (clamped ~99..9900), `bit0=tip/trigger`, `bit1=in_range`
  (asserted even on garbage — **do not gate on it alone**).
- IF2 HID vendor `0xFF00`, 64-byte Modbus-CRC16: `05 01 05 .. mode` (0=single,1=multi),
  `05 21 06 .. cal` (6 pts/report, ROW=48 COL=5 REPx3), sub-7 MD5 handshake. Writable, no vendor sw needed.
- IF3/4 UVC "Dashine UVC" — test pattern only, ignore.
- Report rate ~28–278 Hz, full-span sweeps with no screen lock, trigger-pull jitter reported.

See `docs/gaime_protocol.md`.

## Architecture

```
Gun 1 (sn 11111) ──┐ front USB
                   ├─► GaimeTray: RawInput + calibration + filters + FeedCore
Gun 2 (sn 22222) ──┘          │ CDC, 12-byte frames (pico/gun_bridge/PROTOCOL.md)
                              ▼
                     Pico bridge (back USB)
                      ├─► Bridge Mouse 1 (P1): absolute aim + 5 buttons
                      └─► Bridge Mouse 2 (P2): same
```

- Separation key: **USB serial + hidapi path + ContainerId**, *not* X/Y (both guns emit 0..10000).
- The Pico's absolute mice move the OS cursor only while a listed game is in the
  foreground (`bridge-processes.txt`); on the desktop the bridge is silent.
- Calibration: 4 corners + center (like Guncon3Windows `RectCalib`), bbox min/max + `InvertY`,
  upgrade to homography if aiming at an angle. Persist per-serial `calibration_<serial>.json`.
- Filters: median-of-5 spike reject + max-jump gate + trigger latch-and-hold (quantify jitter first).

## Layout

- `tools/gun_probe_win.py` — enumerate (serial/path/ContainerId) + log digitizer & keyboard reports.
- `src/GaimeTray/` — tray app (the only app); `src/GaimeFeeder/` — engine files linked into it (no project).
- `pico/gun_bridge/` — Pico bridge firmware, `PROTOCOL.md`, DemulShooter helper scripts.
- `docs/gaime_protocol.md` — wire format + reference-repo notes.
- `calibrations/` — per-serial calibration files (gitignored).

## Next steps

1. Install on **Windows** Python (not WSL): `pip install -r requirements.txt`.
2. `python tools/gun_probe_win.py list` — confirm serial(s). Plug in 2nd gun → two serials.
3. `python tools/gun_probe_win.py log --seconds 15` — aim at lit display, move, pull trigger,
   press A/B/Coin/Start. Send the log + observed button map.
4. Then: calibration tool + feeder (per-gun player number, like guncon2 `-index N`).

## Tray app (`src/GaimeTray`, C# net8.0-windows)

- Per gun → Pico bridge mouse P1/P2: absolute calibrated aim; **trigger → left
  button** (debounced; the press frame uses the pre-press latched aim), B → right,
  A → middle, Coin → back, Start → forward (`docs/buttons.md`).
- Stable names in `guns.json`: Gun 1 = sn `11111`, Gun 2 = sn `22222`
  (on-device serials, stable across reboots/ports — see `docs/identity.md`).
  Gun number = Pico player (P1/P2), forever.
- The bridge streams only while a listed game process is in the foreground
  (`bridge-processes.txt`; `bridge-force-on.txt` forces it for debugging).
  The gun's digitizer also produces
  synthetic mouse reports; those are filtered, and the OS cursor is pinned only
  while a gun is locked and the real mouse has been idle (see `RawInput.cs`).
- **Test button** (or Ctrl+T, or `GaimeTray.exe --test`) opens a fullscreen
  shooting gallery. Shots are drawn where `RectCalib.Map()` puts them — the same
  numbers the pad receives — so it checks the real calibration: one ring target,
  a hit scores and moves it, and the HUD shows raw, mapped, IN/OUT, Hz/LOCK and
  the distance from the last shot to the target centre in screen pixels.
  Gun **Start** (Q) enters the box's **8-point gun calibration** without
  leaving the gallery; keyboard **K** enters it unlocked, and the first gun to
  pull the trigger locks the run to that serial. Aim at each lime target and
  pull the trigger three times (one tap per 400 ms, eight targets, three taps
  each); the gallery builds the eight vendor reports and sends them to the gun
  (see `docs/box-calibration-protocol.md`). The host five-point bbox
  calibration remains the tray Calibrate/F12 path and is separate. Gun **B**
  toggles the hold-still measurement, gun **C** toggles the white reference
  border (keyboard **V**, shared with the calibration window), **F**
  toggles host-side smoothing and keyboard **M** toggles the measurement.
  **ESC** closes the gallery. Every border/smoothing/measure toggle (and closing
  the gallery) writes the settled `noise N.N px, max jump M px, K samples`
  figures for the period that just ended to `logs\tray-<date>.log`, so the
  comparison survives the window. Border geometry lives in `ScreenBorder.cs` so
  the two windows cannot drift apart. The game gets the **same 32 px** frame
  from ReShade (`tools/Install-ReshadeBorder.ps1`, target
  `C:\games\teknoparrot\ElfLdr2`); see `docs/white-border.md`.
- Aim is low-passed by a 1 euro filter on top of the median-of-5 spike
  rejection: heavy smoothing while still, light while moving. At ~478 Hz the
  rest-state time constant is ~40 ms but drops to a few ms during a fast sweep,
  so it removes shake without adding perceptible lag to a flick.
- F12 = recalibrate, ESC = quit (ESC also closes the gallery).

## Deployment — `C:\games\feeder`

The runtime install lives at **`C:\games\feeder`** (Windows-native, not the
WSL repo), next to the other emulator folders. It is self-contained:

```
C:\games\feeder\
  GaimeTray.exe (+ .dll, deps, runtimeconfig)
  guns.json
  calibrations\cal_<sn>.json
  logs\  (tray-<date>.log, bridge-<date>.log, capture-*.txt)
  Launch-Tray.cmd
```

- Daily driver: `C:\games\feeder\Launch-Tray.cmd` (or the repo's
  `tools\Launch-Tray.cmd`, which starts the deployed copy).
- The app resolves its data root by walking up from the exe to the folder
  holding `guns.json` (`AppPaths.cs`) — so the deployed folder is the root,
  and a dev build under `src\<App>\bin\...\net8.0-windows\` still resolves to
  the repo root.
- Rebuild + redeploy after source changes: run `tools\Deploy-Feeder.ps1`
  (`dotnet publish -c Release` of `GaimeTray` into `C:\games\feeder`).
- Requires the .NET 8 Windows desktop runtime and the Pico bridge
  (`pico/gun_bridge`, flashed and plugged in).
- Recommended one-time setting (no admin) so Windows does not move the cursor
  from the guns' touch digitizers:
  `reg add "HKCU\Software\Microsoft\Wisp\Touch" /v TouchGate /t REG_DWORD /d 0 /f`,
  then sign out and in. Undo with `/d 1`.
