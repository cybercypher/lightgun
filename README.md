# G'AIM'E Lightgun → ViGEm (GunCon-style) for Teknoparrot / DemulShooter

Goal: make each G'AIM'E gun appear as its **own Xbox360 pad** (ViGEmBus) so 2 guns work
simultaneously in Teknoparrot / DemulShooter, with calibration. Pattern follows
`psakhis/guncon2` (per-gun process, `index` → vJoy id) and `gameotaku79/Guncon3Windows`
(5-point cal, `mapping.txt`-style buttons), but with maintained **ViGEmBus** instead of
archived TetherScript / vJoy.

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
Gun 1 (serial <usb-serial>) ──hidapi path──┐
                                     ├─► per-gun process ─► calibration (bbox + homography)
Gun 2 (serial ?????) ──hidapi path──┘      ├─► ViGEm Xbox360 Pad 1 (J1): left-stick = aim, X = trigger
                                           └─► ViGEm Xbox360 Pad 2 (J2): same
```

- Separation key: **USB serial + hidapi path + ContainerId**, *not* X/Y (both guns emit 0..10000).
- One OS cursor exists — never feed the system mouse. Feed only ViGEm pads.
- Calibration: 4 corners + center (like Guncon3Windows `RectCalib`), bbox min/max + `InvertY`,
  upgrade to homography if aiming at an angle. Persist per-serial `calibration_<serial>.json`.
- Filters: median-of-5 spike reject + max-jump gate + trigger latch-and-hold (quantify jitter first).

## Layout

- `tools/gun_probe_win.py` — enumerate (serial/path/ContainerId) + log digitizer & keyboard reports.
- `src/gaime/` — feeder (planned): `hid_reader.py`, `calibration.py`, `filters.py`, `vigem_pad.py`.
- `docs/gaime_protocol.md` — wire format + reference-repo notes.
- `calibrations/` — per-serial calibration files (gitignored).

## Next steps

1. Install on **Windows** Python (not WSL): `pip install -r requirements.txt`, install **ViGEmBus**.
2. `python tools/gun_probe_win.py list` — confirm serial(s). Plug in 2nd gun → two serials.
3. `python tools/gun_probe_win.py log --seconds 15` — aim at lit display, move, pull trigger,
   press A/B/Coin/Start. Send the log + observed button map.
4. Then: calibration tool + ViGEm feeder (per-gun `index` → pad id, like guncon2 `-index N`).

## Feeder (`src/GaimeFeeder`, C# net8.0-windows, builds clean)

- `GaimeFeeder --list` — shows the gun sns + HID routing, exits.
- Per gun → Xbox360 pad J1/J2: left stick = calibrated aim, **X = trigger**
  (button 3; debounced, latched pre-press), A/B/Back/Start per `docs/buttons.md`.
- Stable names in `guns.json`: Gun 1 = sn `11111`, Gun 2 = sn `22222`
  (on-device serials, stable across reboots/ports — see `docs/identity.md`).
  Gun number = ViGEm pad number (J1/J2), forever, created in pad-index order.
- Aim is forwarded only on a valid on-screen lock (~200+ Hz); idle wander
  (<60 Hz) is dropped so the virtual stick holds. The OS cursor never moves
  while feeding: every gun report is followed by restoring the cursor to the
  real mouse's last position (see `PinCursor` in `RawInput.cs`).
- **Test button** (or Ctrl+T, or `GaimeTray.exe --test`) opens a fullscreen
  shooting gallery. Shots are drawn where `RectCalib.Map()` puts them — the same
  numbers the pad receives — so it checks the real calibration: one ring target,
  a hit scores and moves it, and the HUD shows raw, mapped, IN/OUT, Hz/LOCK and
  the distance from the last shot to the target centre in screen pixels.
- F12 = recalibrate, ESC = quit (ESC also closes the gallery).

## Deployment — `C:\games\feeder`

The runtime install lives at **`C:\games\feeder`** (Windows-native, not the
WSL repo), next to the other emulator folders. It is self-contained:

```
C:\games\feeder\
  GaimeTray.exe / GaimeFeeder.exe (+ .dll, deps, runtimeconfig, Nefarius)
  guns.json
  calibrations\cal_<sn>.json
  logs\  (tray-<date>.log, feeder.log, xcheck.log, capture-*.txt)
  Launch-Tray.cmd   Launch-Feeder.cmd   Feeder.ps1   XCheck.ps1
```

- Daily driver: `C:\games\feeder\Launch-Tray.cmd` (or the repo's
  `tools\Launch-Tray.cmd`, which starts the deployed copy).
- The apps resolve their data root by walking up from the exe to the folder
  holding `guns.json` (`AppPaths.cs`) — so the deployed folder is the root,
  and a dev build under `src\<App>\bin\...\net8.0-windows\` still resolves to
  the repo root.
- Rebuild + redeploy after source changes: run `tools\Deploy-Feeder.ps1`
  (`dotnet publish -c Release` of both projects into `C:\games\feeder`).
- Requires **ViGEmBus** (https://github.com/ViGEm/ViGEmBus) and the .NET 8
  Windows desktop runtime.
