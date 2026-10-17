# G'AIM'E gun — wire protocol (from gaime_mods FINDINGS + gun_probe.py, confirmed here)

- USB: `VID 0x2E2C (11820) / PID 0x0631 (1585)`, bcd `0x0419`, project `LBQ-1585`.
- This unit: parent `USB\VID_2E2C&PID_0631\<usb-serial>` → serial `<usb-serial>`.
- 5 interfaces: 0 HID keyboard, 1 HID digitizer, 2 HID vendor FF00, 3/4 UVC.

## IF1 digitizer (the pointer)

- Usage page `0x0D`, usage `0x04` (Touch Screen), Report ID 1.
- 6-byte input report: `01 <flags> <X LE u16> <Y LE u16>`.
- `flags bit0` = tip switch = trigger. `bit1` = in_range (screen lock claim, but observed
  asserted continuously on garbage — do not trust alone).
- Logical 0..10000, observed clamp 99..9900. Absolute — no vendor software needed.
- Rate ~28–278 Hz. No-lock behavior: sweeps full space, mean jump ~90 counts, worst ~4800.

## IF0 keyboard (buttons)

- Usage page `0x01/0x06`, 8-byte boot reports.
- Reported map (MP1st, to verify here): `A=space, B=b, Coin=c, Start=q`, trigger = click.
- TODO: capture all buttons on this unit (A/B/Coin/Start/trigger/recoil/pedal?).

## IF2 vendor config (0xFF00, 64-byte, Modbus CRC-16 LE)

- `05 01 05 00 00 00 <mode>` + CRC7 → gunmode (0 single, 1 multi). Ack: same FC/SFC, byte6=1, valid CRC.
- `05 21 06 ...` calibration, 6 pts/report, chunk idx in byte6 & 7, CRC39, 200 ms gap.
- `GaimeService` claims IF1 with force=true during cal transfer (freeze pointer), then releases.
- AIDL: sendBufferToDevice / ReadBufferFromDevice / setFrameColorMode / setGunMode / setSwitchGun.

## Reference patterns (guncon2 / Guncon3Windows, both forks of sonik-br/GunconUSB)

- GunCon2: WinUSB `0B9A:016A`, 6-byte bulk `0x81` (buttons active-low + X/Y LE16), control-out
  mode select before reads. `run.py` per-gun process: `-index N` picks Nth device → `VJoyDevice(N)`,
  `-x/-y` ABS min/max from 4-corner `calibrate.py` extrapolate, `-m` mouse mode, C+D-pad live tweak.
- Guncon3: WinUSB `0B9A:0800`, 15-byte encrypted IR report + rolling-key decode. `RectCalib`:
  5-pt (corners+center) → `calibration_rect.txt` (RawMin/MaxX/Y, ScreenW/H, InvertY=1), F12 recal.
  Feeds TetherScript virtual abs-mouse (`F00F:0002`) + keyboard (`F00F:0003`) via `mapping.txt`.
  **No multi-gun support** (FirstOrDefault device). Homography.cs exists but unused.
- For GAIME: standard HID (no WinUSB driver), hidapi path/serial separates guns, ViGEm Xbox pads
  replace vJoy/Tether (archived). Cal math starts as RectCalib bbox, homography if angled.
