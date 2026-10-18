# HANDOFF2 — G'AIM'E dual-gun project (2026-10-17 evening, fresh session start)

Goal: 2x G'AIM'E lightguns as GunCon-style controllers (separate J1/J2) for
Time Crisis 3 arcade (System 246) in Teknoparrot. House rules: user swears;
tolerate, stay factual, never guess — verify on the machine, label hypotheses.
User asked that Tekno UI items be called **buttons**, not rows.

## Settled (verified on hardware unless noted)

- **Gun identity**: on-device `sn.num` — Gun 1 (labeled) = `11111`, Gun 2 = `22222`.
  USB serials/paths randomize per boot. Tools: `tools/gun_sn.ps1`,
  `tools/gun_set.ps1 -Value`. Details: `docs/identity.md`.
- **Gun protocol**: IF1 digitizer `01 <flags> X Y` 0–10000 (~265 Hz aimed,
  ~12 Hz idle; tip=trigger; flutter ~30 ms). IF0 keyboard: A=SPACE, B=B,
  Coin=C, Start=Q (all REAL keystrokes, verified in raw log with tone bursts:
  38x tip=1 in 137 ms hold, SPACE/B/C key events). Docs: `docs/`.
- **Feeder** (`src/GaimeFeeder`, C# net8.0-windows, builds 0/0): sn routing,
  per-gun ViGEm Xbox360 J1/J2 (stick=aim, RB+RT255=trigger, A/B/Back/Start),
  5-pt cal → `calibrations/cal_<sn>.json`, 60 Hz poll loop, trigger DOWN/UP +
  blip logging, `--xcheck` XInput slot viewer, disconnect-on-exit, try/catch
  hardened inputs, console-focused ESC/F12 only (game ESC can't kill it),
  cal sampling gated 1/750 ms, **cursor quarantine** (parks OS cursor at 0,0
  while gun streams fast; idle mouse unaffected). Launcher:
  `tools/Launch-Guns.cmd` (double-click, both windows, logged to `logs/`).
  Git: 7 commits, each fix isolated (`git log`).
- **XInput path proven**: slot 0 only, no phantoms; T-pulse, aim, trigger all
  visible in `--xcheck`. Emulator's XInput source drops it 0.2 ms after
  connect EVERY launch (deterministic, 3x reproduced) while SDL holds it —
  so binds use `SDL-0/*`. 7/8 names accepted; `SDL-0/A` was invalid, and the
  file now shows `SDL-0/FaceSouth` (origin unclear — NOT hand-edited this
  session; validate in next log, do not assume it works).
- **In-game P1 state**: trigger FIRES, A = cover (via Tekno Pedal button),
  coin via native `C`. Aim chain is TRUE end-to-end (service-screen faint
  cursor lands where aimed; desktop/Paint tracking correct; fresh
  `cal_11111.json` full-span, InvertY=false).
- **Service/test**: `[JVS] TestMode=true` boots the service menu (current ini
  state!). TC3 operator manual facts kept as text in
  `docs/tc3_manual_notes.md` (source PDF deleted — it kept tripping a
  harness PDF-parser error). Manual facts: coin-door Select Up/Down + Enter are
  PHYSICAL switches; GUN INITIALIZE shortcut = hold Service while Test
  turns on; gun adjust = shoot markers, pedal/Service to adjust, Enter exits;
  OTHERS → BACKUP MEMORY INITIALIZE = factory reset incl. gun sights;
  SWITCH TEST screen shows all inputs live.
- **Tekno forwarding rule (discovered)**: gamepad-device buttons forward to
  the fork (Pedal button0 works); keyboard-device buttons appear dead
  (Test/Select/Enter/Service-as-keyboard all dead). Service button hand-set
  to J1 button1 (`logs/timecrs3.xml.bak` holds original) — shortcut still
  failed: Tekno does not forward Service1 at all. Service entry is CLOSED.
- **Dongle = memcard**: removing `NM00012.ps2` breaks boot ("dongle image
  does not exist"). Restored, hash-verified identical. NVRAM wipe is OFF
  the table. Backups: `logs/NM00012.ps2.bak`.
- **Cursor chaos (cause, not fix)**: gun is a system touchscreen (MI_01
  `HID-compliant touch screen`) + keyboard (MI_00). Windows drives the
  cursor straight from the gun, parallel to the feeder — feeder cannot block
  it. Disable test inconclusive (needs admin; device vanished on unplug
  before observation). Quarantine built, UNTESTED.

## NOT working / unresolved

1. **Aim warped in gameplay** (prime suspect: stale NVRAM gun cal in
   dongle; service cursor true + gameplay warped = gameplay-only transform).
   Center aims land high-left repeatably; corners off differently. In-menu
   reset unreachable (nav dead). P2 untouched.
2. **Test / Service / Select / Enter**: all dead toward the fork. TestMode
   ini flag is the only working service control.
3. **Feeder must tolerate launch-before-plug** (user requirement): hot-plug
   NOT implemented — feeder snapshots guns once at startup. Design agreed
   (2 s rescan, drop binding-mode pads, UI-marshalled cal) but unbuilt.
4. Quarantine untested. `FaceSouth` origin unexplained. "Link malfunction"
   line in service menu unexplained (likely benign single-cabinet status).

## Machine state right now

- Gun UNPLUGGED (0 present VID_2E2C devices). Feeder state unknown (assume
  down; DLL unlocked — last build succeeded post-close).
- `PCSX2.ini` (`.../OneDrive/Documents/PCSX2x6/inis/`): `d7d41aeb...`,
  `[USB1]` guncon2 on `SDL-0` (Pointer, Relative×4, Trigger, Start,
  A=`FaceSouth`), `[JVS] Coin1=Keyboard/C`, **`TestMode = true`** (flip
  back to false for normal boot!). `[USB2]` None. P2 untouched.
- Tekno profile: Test=7, Coin=6, Pedal=0 (J1); Service=gamepad Buttons1
  (hand edit — do NOT open controller page or UI may overwrite); keyboard
  Service/Select/Enter rows present but dead.
- Cals: fresh `cal_11111.json` (X 601..9683, Y 370..9049 per last read —
  re-verify); orphans from old scheme remain; no `cal_22222.json` yet.

## Next order

1. Flip `TestMode=false`. Plug Gun 1 → launcher → verify quarantine
   (cursor parks while aiming, mouse returns on desk) + trigger DOWN/UP.
2. Build hot-plug (launcher-first flow).
3. Aim warp: entry to gun-init is closed; options are REA on the fork
   binary (JVS service-bit source + warp code — user pre-approved) or
   measured feeder-side compensation. Do NOT re-attempt NVRAM wipe.
4. P2: plug Gun 2 → `cal_22222.json` → J2 → `USB2` guncon2 + SDL-1 binds,
   log-first like J1.
