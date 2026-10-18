# HANDOFF3 — G'AIM'E dual-gun project (2026-10-18 ~00:20, warp still unsolved)

Goal: 2x G'AIM'E lightguns as GunCon-style controllers (separate J1/J2) for
Time Crisis 3 arcade (System 246) in Teknoparrot. House rules: user swears;
tolerate, stay factual, never guess — verify on the machine, label hypotheses.
Call Tekno UI items **buttons**, not rows.

## Settled (verified on hardware unless noted)

- **Guns/feeder/tray**: identity via on-device `sn.num` (11111 = Gun 1,
  22222 = Gun 2); protocol docs in `docs/`; feeder builds 0/0.
- **GaimeTray** (`src/GaimeTray/`, C# net8.0-windows, 0 warn/0 err) is the
  daily driver: gun cards (LIVE/QUIET, Hz, X/Y, trigger+A/B/Coin/Start pills,
  Calibrate + Pulse per gun), Log tab (also `logs/tray-<date>.log`), XInput
  tab (live slots 0-3), Capture tab (timed RawInput logger →
  `logs/capture-*.txt`), launcher-first hot-plug (2 s rescan, balloon on
  attach), single-instance mutex, Start-with-Windows checkbox, closes to tray.
  Launch ONLY via `tools/Launch-Tray.cmd` → `GaimeTray.exe` (windowed; never
  `dotnet.exe`, which opens a console window). Engine shared with feeder via
  linked compile (`GaimeFeeder/*.cs` except `Program.cs`).
- **Quarantine (flipped, both tray + feeder)**: park OS cursor at 0,0 when gun
  reports SLOW (<60 Hz idle wander); leave fast aimed stream alone so cursor
  tracks aim. Freshness gate 500 ms (unplugged gun can never strand cursor).
  Tradeoff: real mouse fights the park while a gun sits plugged-idle.
- **Hot-plug**: 2 s rescan in feeder (`Program.cs`) and tray (`FeederEngine.cs`).
- **Manual**: source PDF deleted (kept tripping harness error
  `PDF parser FaaS call failed` — do NOT re-download any PDF). Facts as text:
  `docs/tc3_manual_notes.md`. Key bits: menu nav = Select Up/Down + Enter
  (physical coin-door switches); GUN INITIALIZE via menu OR hold Service while
  Test turns on; gun adjust = shoot markers, pedal/Service adjusts, Enter exits;
  SWITCH TEST rows flip live, exit = Up+Enter together; LINK MALFUNCTION
  number = 1/2 PCB fault, 3 version mismatch; OTHERS → BACKUP MEMORY
  INITIALIZE = factory wipe (**OFF LIMITS**, never again).
- **Service menu nav = CLOSED.** Tried Select Up→gamepad B, Select Down→gun
  Coin, Enter→gun Start (hand-edit
  `C:\games\teknoparrot\UserProfiles\timecrs3.xml`, backup
  `logs/timecrs3.xml.pre-nav`): highlight never moves. Only P1Button6 (pedal)
  and trigger reach the fork; Test/Service1/Select/Enter mappings are dead
  regardless of device. `TestMode=true` boots the choice menu but it can't be
  navigated. LINK MALFUNCTION **1** shown (= PCB/link fault per manual;
  single cab, no partner — game still plays).
- **Fork aim math (from upstream `guncon2.cpp`, NOT disassembly — strings
  match verbatim)**: with ANY Relative bind present, the Pointer bind is
  IGNORED — stick halves are the whole aim path (linear, no accumulation).
  pos = stick01 × screen − half + center − game_param. Serial `NM00012`
  matches no auto-config → defaults 640×240, center (320,120), 100%.
  TC3-class expects ≈ (390,154)/~90% → centered stick reads off-center.
  Service crosshair = raw pointer overlay (true); game transform (scale,
  center, NVRAM param) applies only to emulated gun — explains service-true
  vs gameplay-warped.
- **REA status: NO ENGINE ON THIS BOX.** `rea doctor`: MCP aligned, but no
  Java (no Ghidra), no Hopper, no IDA. User pre-approved a Ghidra install;
  NOT done — user chose runtime probes instead. Do not claim REA results.
- **Probe run (verbose + `guncon2_Recalibrate = SDL-0/LeftStick`)**: log shows
  NO `Invalid binding` (bind parsed), NO `Set Param` lines (need dev console
  or game never sends them). Recalibrate presence unconfirmed functionally.
- **Comp1 run (`custom_config=true`, 640×240, center 390/140, scale 100)**:
  impacts "still very random and off to the left" — NO systematic shift.
  Either key names are ignored silently, or warp isn't a static offset.
  Randomness is now the prime datum, not offset.

## NOT working / open

1. **In-game warp unsolved; impacts RANDOM + left.** Static-offset theory dead.
   Next: diagnose randomness FIRST — tray Hz/X-Y while aiming at game
   (~260 Hz steady = gun tracking, fault downstream; low/jumpy = sensor
   losing the display). Then real measurements: screenshots of impacts aimed
   at known anchors (corners + center), fit per-axis offset/scale.
2. Custom `guncon2_*` key names UNVERIFIED (silently-ignored vs applied
   unknown). If screenshots show zero response to comp1, find real names
   (Ghidra) or compensate feeder-side.
3. P2 path untouched (USB2 None, no `cal_22222.json`).
4. Quarantine flip feels OK per user, keep.
5. TestMode currently **false** (normal boot). EnableVerbose currently **true**.

## Machine state right now

- Tray running from repo path (last verified feeding Gun 1, slot 0 live).
  DLLs: tray `e090a16e…`, feeder `036b26dc…`.
- `PCSX2.ini` (`C:\Users\USER\OneDrive\Documents\PCSX2x6\inis\`):
  `742269bd…` — `[USB1]` guncon2 on SDL-0 (Pointer decorative, Relative×4
  live, Trigger, Start, A=FaceSouth, Recalibrate=LeftStick-unpressed) +
  custom 640×240 center 390/140 scale 100; `[JVS] Coin1=Keyboard/C`,
  `TestMode=false`; `[USB2]` None. Backups: `logs/PCSX2.ini.pre-probe(.sha)`,
  `logs/PCSX2.ini.pre-comp1.sha`. EnableVerbose=true (revert before final).
- Tekno `UserProfiles/timecrs3.xml`: gamepad nav binds (dead, harmless);
  backup `logs/timecrs3.xml.pre-nav`. Do NOT open Tekno controller page
  (overwrites hand edits). Original Service mapping in `logs/timecrs3.xml.bak`.
- Guns: Gun 1 (`11111`) has fresh `cal_11111.json`; orphans + no `cal_22222`.

## Next order

1. Tray Hz/X-Y check while aiming at game + impact screenshots at known
   anchors → decide gun-tracking vs downstream.
2. If downstream: fit offset/scale from screenshot pixels → comp2 ini values
   (or feeder-side compensation if fork keys ignored).
3. If gun-tracking: fix setup (distance/angle/brightness/renderer), recal.
4. Then P2 (Gun 2 → `cal_22222.json` → USB2 + SDL-1, log-first like J1).
5. Optional: Ghidra install (approved, not started) for fork internals.
