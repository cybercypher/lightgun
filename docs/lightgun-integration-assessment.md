# G'AIM'E → DemulShooter / TeknoParrot integration assessment

Date: 2026-10-23
Status: research + plan, not yet implemented

> **2026-10-23 correction:** the claim below that ViGEm pads are not a
> DemulShooter path is **wrong**. ViGEmBus exposes a HID gamepad interface
> (usage `0x01/0x05`, report ID 0, absolute axes), DS registers that usage, and
> DS 17.10 binds it (`P1 device plugged and found`). The earlier failure was a
> stale device path plus the wrong trigger button, not a missing device class.
> See `HANDOFF.md` §6a and `docs/phase0-evidence.md`. Consequences: vJoy is not
> required for DS, and the Pico bridge is optional (TP-native / mouse-only
> consumers), not the critical path.

Goal: make the two G'AIM'E guns behave like Sinden / Gun4IR / AimTrak / X-Gunner
for PC lightgun games — easy DemulShooter setup, working trigger, calibration
that reaches the game, and both guns at once in 2-player titles.

---

## 1. What the reference guns actually are

| Gun | Sensing | What Windows sees |
|---|---|---|
| Sinden | camera + software-rendered white border | **absolute mouse** |
| Gun4IR | camera + 4 IR LEDs around the screen | **absolute mouse** (or gamepad) |
| AimTrak | camera + IR bar | **absolute mouse** |
| X-Gunner | camera + 4 IR sensors around the screen | **absolute mouse** |

Two guns = two distinct absolute mouse devices. That is the whole compatibility
trick: every app, emulator and DemulShooter already reads a mouse, and RawInput
gives each mouse its own device path so P1/P2 can be separated.

The G'AIM'E is different:

- HID **digitizer** `0x0D/0x04`, report ID **1**, absolute `0..10000`, tip bit 0
  (`docs/gaime_protocol.md`).
- Buttons arrive as a **keyboard** (`0x01/0x06`): A=Space, B=b, Coin=c, Start=q.
- Aim is computed **on-device** by a neural net (`nna_get_xy`) fused with an
  LSM6DS3 IMU / AHRS / Kalman loop (confirmed by strings in `/app/bin/gun`).
- It never presents as a mouse.

---

## 2. Verified facts (source-level, not guesses)

### DemulShooter

| Finding | Where |
|---|---|
| Registers RawInput for **Joystick (0x01/0x04), Mouse (0x01/0x02), Gamepad (0x01/0x05)** only | `DemulShooter/DemulShooterWindow.cs:283-299` |
| Our digitizer usage is `0x0D/0x04` → **DS never receives a report from it** | same |
| HID parsing hard-codes **report ID 0** in `HidP_GetUsages` / `HidP_GetUsageValue` | `DsCore/RawInput/RawInputController.cs:397,448,469` |
| Our gun uses report ID 1 → parsing would fail even if registered | `docs/gaime_protocol.md` |
| The **XInput daemon is commented out** → DS is effectively RawInput-only | `DemulShooter/DemulShooterWindow.cs:333-345` |
| Scales a device's absolute range linearly to `Screen.PrimaryScreen.WorkingArea`, no per-device calibration | `DemulShooter_GUI/Wnd_DemulShooterGui.cs:471-477` |
| Matches a player to a device by **device name** (unique instance path) | `DemulShooterWindow.cs:193-205` |
| GUI lists **all** HID + mouse devices (so the gun appears, but stays silent) | `DemulShooter_GUI/Wnd_DemulShooterGui.cs:63` |
| HID mode is driven by **absolute** value caps (`IsAbsolute`) and per-usage axes | `RawInputController.cs:327-336` |
| No `LICENSE` file in the repo → all rights reserved; no redistributable fork | repo root |

### TeknoParrot

- Native lightgun support requires **RawInput** under GAME SETTINGS; **up to two
  guns** natively.
- Its device list is **mice** ("select your mice devices from the list").
- With per-device mouse selection "it is normal that your Windows cursor does
  not line up with the crosshair" — documented, expected.
- Single player can use the **"Windows Mouse Cursor"** option, which *does*
  follow the desktop cursor.

### Sinden's own DemulShooter guide

- **Windows display scaling must be 100%**, or DS aiming is wrong.
- Changing the game's native resolution also breaks aiming.

### Consequence

The gun cannot be consumed by DS or TP as-is. Something has to translate it into
a device class those tools already read, and that translation is also the right
place to apply calibration.

---

## 3. Why Paint is right and the game is off

These are two different pipelines, and Paint only proves the first one:

1. **Paint = the Windows cursor.** Windows maps the digitizer to the desktop
   itself, including DPI virtualisation. It proves the gun agrees with Windows,
   nothing about raw HID coordinates.
2. **DS/TP = raw device coordinates**, scaled to the working area or the game
   window. Different numbers, different origin.
3. **DPI scaling ≠ 100%** shifts DS's mapping (documented by Sinden).
4. **Game resolution/aspect ≠ desktop** (letterbox/pillarbox) → offset + scale
   error.
5. **Two guns, one cursor.** The cursor follows the last gun that moved, so
   Paint can look perfect for P1 while P2 is silently wrong.
6. **In-game calibration must be redone with the final input path.** Doing it
   before switching paths invalidates it.

So "calibration passes through" must mean: **calibrate once in raw units, and
every consumer downstream receives already-screen-correct coordinates.**

---

## 4. Architecture options

### A. vJoy HID output (superseded 2026-10-23)

> The ViGEm HID gamepad path in the correction note at the top already gives DS
> an absolute device; vJoy is no longer required for DS. This option remains a
> fallback/reference.

vJoy creates signed, **absolute** HID joystick devices:

- usage `0x01/0x04` — DS **does** register it;
- report ID 0 — DS **can** parse it;
- absolute axes `0..32767`, 128 buttons;
- one device per player, each with a distinct device path.

We apply bbox/homography **in the feeder**, so DS's linear scaling lands on the
screen. Trigger becomes a HID button. One-time driver install, same category as
the ViGEmBus install already required. This is also what the repo's GunCon2
lineage did (`docs/gaime_protocol.md`).

### B. Patch DemulShooter to accept the digitizer

Register `0x0D/0x04` and use the report's actual report ID. Small patch, zero
extra drivers, makes the gun first-class. But DS has **no licence** (all rights
reserved), so the clean route is an **upstream PR**, not shipping a fork. Keep
as an option, not the foundation.

### C. Hardware absolute-mouse bridge (endgame)

~$5 RP2040/TinyUSB exposing one or two true absolute mice on USB. The guns then
become indistinguishable from Sinden/Gun4IR to *everything*: TP native, DS mouse
mode, Steam games, no vJoy, no driver. TetherScript's signed virtual-mouse
driver is effectively dead (certificates expired, no standalone download), so
hardware is the only way to get true HID mice on modern Windows.

### D. Keep ViGEm XInput

Unchanged, still useful for games that accept a pad. Not a DS path.

---

## 5. Calibration plan

- **Single source of truth:** calibration lives in the feeder and is applied
  before the output device.
- **Upgrade bbox → homography** (4 corners + centre, 8-DOF). Same data already
  collected; corrects off-axis/keystone error the bbox cannot.
- **Map to the game's client rect**, not the desktop: track the foreground
  window, subtract borders, handle letterboxing. Exclusive fullscreen = the
  screen; borderless = the client area.
- **100% Windows scaling** as a documented requirement, with a startup warning
  if it is not.
- **Per-game profile** (resolution, offsets, DS target/args) instead of one
  global mapping.
- **Verification:** the gallery already reports raw → mapped and noise. Add a
  "desktop cursor follows aim" toggle — the cursor is currently *pinned*, which
  is why single-player "Windows Mouse Cursor" mode cannot work today.

---

## 6. Trigger plan

- Raw tip bit (report byte 1, bit 0) → debounced press/release. The existing
  3-report / 4 ms press and 50 ms quiet release logic is sound.
- Output as **left mouse button** (mouse path) or **HID button 1** (vJoy/DS
  path) — never both.
- **Off-screen reload** as its own HID button/condition; DS has a dedicated
  off-screen trigger binding.
- Keep coin/start/A/B on the keyboard interface; DS and TP read keyboard
  independently.
- Suppress the digitizer's Windows touch-click so it cannot double-fire or click
  UI.

---

## 7. 2-player plan

- Gun serial → deterministic player slot (already in `guns.json`).
- One output device per gun with a stable device path; DS P1/P2 bound by name.
- Per-gun calibration (already keyed by serial) applied per device before output.
- Test matrix: DS dual-gun title, TP native dual-gun title, single-player
  mouse-cursor title.

---

## 8. Phases

**Phase 0 — evidence (30 min, no code).** Plug both guns; run `DsDiag.exe` (lists
every raw device DS sees, dumps HID caps) and DS with a target; confirm the
digitizer appears but stays silent. Check TP's RawInput device list. Confirm
Windows scaling is 100%. Record the gun's internal calibration status. This
decides whether A, B or C is needed.

**Phase 1 — calibration correctness.** Homography, window-rect mapping,
cursor-follow toggle, per-game profile. Independently valuable; fixes the
Paint-vs-game class of error.

**Phase 2 — vJoy backend.** Add alongside ViGEm; map calibrated X/Y + buttons;
validate P1 in one DS title.

**Phase 3 — DS 2-player.** Bind both vJoy devices, per-gun calibration, validate
a dual-gun title.

**Phase 4 — TP-native evaluation.** If TP lists the vJoy device, use it;
otherwise route TP titles through DS.

**Phase 5 — optional Pico HID bridge** to drop vJoy and become a true
Sinden-class device.

**Phase 6 — packaging.** One tray, per-game profiles, auto-launch DS/TP with the
right args, persisted settings, docs.

---

## 9. Risks / open questions

- vJoy is old (2.1.x) though widely used; the DS + HID + vJoy combination needs
  empirical validation — that is why Phase 0/2 are gates.
- DS's HID mode gives one extra "action" button; everything else stays on the
  keyboard interface.
- A DS fork cannot be redistributed; upstream PR or the vJoy path only.
- TP's native list may be mice-only; then TP gun titles go through DS (the norm
  anyway).
- The gun performs its own calibration (`05 21 06` frames, `nna_gaime_calibration`,
  `get_calibrationStatus` in `/app/bin/gun`) which we have never set. Vendor
  notes say the gun wants an 8-point calibration on every boot; if the on-device
  calibration is unset or stale it may contribute bias/noise before any host
  mapping. Worth checking early — it is cheap.

---

## 10. Sources

- DemulShooter source (commit at time of writing):
  <https://github.com/argonlefou/DemulShooter>
- DemulShooter wiki: <https://github.com/argonlefou/DemulShooter/wiki>
- TeknoParrot lightgun setup: <https://teknoparrot.com/wiki/getstarted.html>
- Sinden wiki — DemulShooter: <https://www.sindenwiki.org/wiki/Demulshooter>
- Sinden wiki — Borderless Gaming: <https://www.sindenwiki.org/wiki/Borderless_Gaming>
- Local gun protocol: `docs/gaime_protocol.md`, `HANDOFF.md`
- Local gun binary analysis: `logs/gunref/gun_ref` (`nna_gaime_process`,
  `nna_get_xy`, `Lsm6ds3TR_c_*`, `comp_kalman_filter_update`)
