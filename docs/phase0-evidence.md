# Phase 0 — input-path evidence

Date: 2026-10-23
Status: measured on the machine; hardware-dependent items still open (no guns
attached at the time of these tests).

This document records what was actually measured, not what the earlier
assessment assumed. One conclusion in
`docs/lightgun-integration-assessment.md` is now **disproved** and is corrected
in §7.

Environment:

- Windows 10 22H2, build 19045.
- Primary display 3840×2160, secondary 2560×1440 (left of primary).
- DemulShooter 17.10.0.0; bundled `DsDiag.exe` reports 16.0.0.0 (metadata
  not bumped — source is the same as DS 17.10).
- ViGEmBus running; feeder pads created through `Nefarius.ViGEm.Client`.
- No guns and no J3 console adapter were connected for these tests.

---

## 1. Windows display scaling — PASS

Measured with `GetDpiForMonitor` after `SetProcessDPIAware`:

| Monitor | Bounds | Work area | DPI | Scale |
|---|---|---|---|---|
| `\\.\DISPLAY1` (primary) | 3840×2160 at 0,0 | 3840×2120 | 96 | **100%** |
| `\\.\DISPLAY2` | 2560×1440 at −2560,370 | 2560×1400 | 96 | **100%** |

`HKCU\Control Panel\Desktop\Win8DpiScaling = 0`. The Sinden/DS requirement of
100% scaling is satisfied.

Note for later: DS maps to the **primary** screen and its work area. The second
monitor is at negative X, so any per-game window mapping must not assume a
single desktop origin at (0,0).

---

## 2. DsDiag launch — Mark-of-the-Web gotcha

`C:\games\demulshooter\DsDiag.exe` carries a `Zone.Identifier` stream and will
not start in place (process never appears, no event-log entry, `Start-Process`
blocks). Copying `DsDiag.exe` + `DsCore.dll` to a local folder and
`Unblock-File` makes it start normally.

`DsDiag` registers exactly the same RawInput usages as DemulShooter itself
(`DsDiag/Ds_Diag.cs:50-65`): Joystick `0x01/0x04`, Mouse `0x01/0x02`, Gamepad
`0x01/0x05`. So a device that DsDiag can read is a device DS can read.

---

## 3. ViGEm pad is a real HID gamepad — measured

A pad created with `Nefarius.ViGEm.Client` exposes a **present** HID interface:

```
\\?\hid#vid_045e&pid_028e&ig_01#3&966d8b0&0&0000#{4d1e55b2-...}
VID=0x045E PID=0x028E
UsagePage=0x01 Usage=0x05   (Generic Desktop / Game Pad)
InputReportByteLength=15
Input value caps=6, input button caps=1
```

DsDiag lists it as:

```
[Gamepad] [Unknown Manufacturer] - Controller (XBOX 360 For Windows)
DeviceType: RIM_TYPEHID - Gamepad
VID 0x045E / PID 0x028E
Axis X: min 0, max 65535   Axis Y: min 0, max 65535   (absolute)
Number of buttons: 10
```

With a scripted pad, DsDiag's RawInput values changed when the pad's axes were
moved:

- pad X/Y at −32768 → raw `[0x00000000, 0x0000FFFF]`
- pad X/Y at +32767 → raw `[0x0000FFFF, 0x00000000]`

**Conclusion: DemulShooter receives and parses the ViGEm pad over RawInput
HID.** No XInput daemon, vJoy or extra driver is needed for this path.

This matches DS source (`DsCore/RawInput/RawInputController.cs`): HID reports
are parsed with `HidP_GetUsages`/`HidP_GetUsageValue` using report ID 0, which is
what the ViGEm HID report uses. Gamepad usage `0x01/0x05` is registered.

---

## 4. ViGEm HID device paths are stable — measured

Single pad, 5 connect/disconnect cycles:

```
IG_01 # 3&966d8b0&0&0000     (identical every cycle)
```

Two pads, 3 connect/disconnect cycles:

```
IG_01 # 3&966d8b0&0&0000
IG_04 # 3&f54e826&0&0000     (identical every cycle)
```

So one pad is always IG_01; two pads are always IG_01 + IG_04 on this machine.
The feeder creates pads in pad-index order, so gun 1 → IG_01 and gun 2 → IG_04
is deterministic. (Reboot stability still to be confirmed, but the parent
device ID is `USB\VID_045E&PID_028E\01` with a fixed serial and the instance
suffix is derived from it.)

DS matches a configured player to a device by **exact string equality** on the
full interface path (`DemulShooterWindow.cs:200`). There is no fuzzy fallback.

---

## 5. Why the previous ViGEm/DS attempt failed — two config bugs

Checked against `C:\games\demulshooter\config.ini`:

1. **Stale device path.** P1 points at
   `...&IG_02#3&XXXXXXXX&0&0000...`, which is a non-present devnode. The live
   pad is `IG_01#3&966d8b0&0&0000`. Exact-match means DS never binds P1 → no
   input, while DsDiag can still show the device.
2. **Wrong trigger button.** `P1HidBtnOnscreenTrigger = 6`. DS button numbers
   are 1-based (`RawInputController.cs:254-271`), and HID button 6 on the
   Xbox 360 HID report is **RB**. The feeder puts the trigger on **X = button
   3** (`docs/buttons.md`). So even with a correct path, the trigger would do
   nothing.

The rest of the P1 config is plausible: `P1HidAxisX = 0x30`,
`P1HidAxisY = 0x31`, `P1InvertAxisY = False`, `P1Mode = RAWINPUT`. P2 is
unset.

### Fix applied and verified (2026-10-23)

Backed up `config.ini` to `config.ini.bak-20261023`, then:

```ini
P1DeviceName = \\?\HID#VID_045E&PID_028E&IG_01#3&966d8b0&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}
P1HidBtnOnscreenTrigger = 3
```

A verbose DS 17.10 run with the tray-created pad present logged:

```
Found 20 available RawInput devices :
P1 mode = RAWINPUT
P1 device = \\?\HID#VID_045E&PID_028E&IG_01#3&966d8b0&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}
P1 device plugged and found, Handle = 0x148249089
P1 device : [Unknown Manufacturer] / Controller (XBOX 360 For Windows)
```

So the binding is real, not just a device-list entry. The path is valid while
the feeder's first pad is IG_01; if the path shifts across a reboot or pad
rebuild, `config.ini` must be rebound (or written at feeder startup).

---

## 6. DS behaviour that constrains the integration

Source-verified in the 17.10 tree:

- Devices are enumerated **once at startup** (`DemulShooterWindow.cs:170`);
  there is no `WM_INPUT_DEVICE_CHANGE` handler. The feeder (and therefore both
  ViGEm pads) must exist before DS starts. Hot-plugging a pad while DS runs
  requires restarting DS.
- HID mode is selected by the player's device path; axes and button numbers are
  read from `config.ini`.
- Absolute axes are scaled linearly: `ScreenScale(value, Axis_Min, Axis_Max,
  0, ScreenWidth/Height)` (`DemulShooterWindow.cs:1057-1062`). The feeder's
  calibrated screen coordinate → stick 0..65535 mapping therefore round-trips,
  with a possible 1-pixel edge error (`screenW-1` vs `screenW`) and a Y-axis
  orientation that must be confirmed on hardware.
- DS uses the **primary screen** size, not the game window client rect. That is
  the remaining calibration problem, independent of the device path.

---

## 7. Correction to the assessment

`docs/lightgun-integration-assessment.md` §2 says:

> The XInput daemon is commented out → DS is effectively RawInput-only, so the
> ViGEm pads are not a DS path.

The first two clauses are true; the conclusion is **false**. ViGEmBus exposes
an HID gamepad interface (usage `0x01/0x05`, report ID 0), DS registers that
usage, and DsDiag demonstrably reads it. The earlier failure is explained by
the stale path and wrong trigger button in §5, not by a missing device class.

Consequences for `plan.md`:

- **vJoy is not needed for DemulShooter.** The existing ViGEm backend already
  presents a device DS can consume.
- **The Pico bridge is not needed for DS either.** Its remaining value is
  TeknoParrot-native input and any consumer that insists on a *mouse* device
  (plus stable mouse identity). That is now an optional endgame, not the
  critical path.
- The immediate work is calibration/mapping correctness and the two config
  fixes, not new hardware.

---

## 8. Still open (needs guns / console)

- In-game DS test with a real target (e.g. Model2 Virtua Cop 2): aim tracking,
  trigger, then two pads in a 2-player title.
- Confirm Y orientation and the 1-pixel edge mapping with the real gun.
- Confirm DS path stability across a reboot (expected stable; not yet measured).
- Gun's own calibration status (`get_calibrationStatus` / `/app` calibration
  files) via the J3 console on COM4.
- Gun digitizer in DsDiag: expected to appear in the device list but produce no
  RawInput changes, because `0x0D/0x04` is not one of DS's registered usages.
  This is the remaining Phase 0 item that requires a gun.
- TeknoParrot native device list (mice only per assessment; not re-checked).

---

## 9. Reproduction

The probe scripts used are in `tools/phase0/`:

- `Probe-DsDiag.ps1` — starts DsDiag from a MOTW-safe copy, lists every device
  (`-ListOnly`), or probes one index (`-Index N`) and records the detail pane
  plus an 800 ms live-data diff. Probe one index per run: selecting some vendor
  HID collections crashes DsDiag, so a blind sweep is not usable.
- `Probe-ViGEm.ps1` — creates 1–2 ViGEm pads and records the present HID
  interface paths, usage page/usage, report length, and axis/button caps.

Run from Windows PowerShell:

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\phase0\Probe-DsDiag.ps1 -ListOnly
powershell -NoProfile -ExecutionPolicy Bypass -File tools\phase0\Probe-DsDiag.ps1 -Index 6
powershell -NoProfile -ExecutionPolicy Bypass -File tools\phase0\Probe-ViGEm.ps1 -Pads 2 -Cycles 3
```

When a gun is attached, `-ListOnly` should show its digitizer, and
`-Index <that entry>` should show the HID caps but **no live change** while the
gun moves. That is the remaining Phase 0 measurement.
