# Box gun calibration protocol (recovered)

Date: 2026-10-23
Source: `GaimeCalibration.apk` + `GaimeService.apk` from the decrypted box
firmware. Static analysis only; no runtime capture.

This is the calibration the proprietary Android box sends to the gun. It is
**separate** from the host-side 5-point `RectCalib` mapping used by the feeder.

## 8-point geometry

The box uses a 3×3 grid with the centre cell skipped, in row-major order:

```
0 TL      1 TC      2 TR
3 ML                4 MR
5 BL      6 BC      7 BR
```

Target image width = `screenWidth / 7`. The target is square (the static
`calibration_shoot3_static` is square; the animated GIFs are 600×600), so:

- target size = `screenW/7 × screenW/7`
- columns are edge-aligned: x = 0, `screenW/3 - w/2`, `screenW - w`
- rows are edge-aligned: y = 0, `screenH/3 - h/2`, `screenH - h`
- base point = target centre, scaled to **0…32767**:
  - x: `1/14`, `1/3`, `13/14` of screen width
  - y: `(screenW/14)/screenH`, `1/3`, `1 - (screenW/14)/screenH`
  - on 16:9 this is `8/63`, `1/3`, `55/63`.

The box computes these from the real display metrics; the fractions above are
the same for any 16:9 display.

## Samples

Each point needs **3 taps**. The box records the raw touch coordinate scaled to
16-bit:

```
x16 = clamp(0, 32767, rawX / screenWidth  * 32768)
y16 = clamp(0, 32767, rawY / screenHeight * 32768)
```

On the box the digitizer maps 0…10000 to screen pixels, so this is effectively
the gun's raw aim scaled to 0…32768. On the PC use the gun's raw 0…10000:

```
v16 = clamp(0, 32767, raw * 32768 / 10000)
```

## HID report

64-byte vendor report, sent as a 65-byte HID write (report ID 0 + 64 bytes),
8 reports (one per point), **200 ms** apart.

```
off  size  content
0    1     0x05
1    1     0x21
2    1     0x06
3    3     0x00
6    1     point index 0..7
7    2     X tap0, u16 LE
11   2     Y tap0, u16 LE
15   2     X tap1, u16 LE
19   2     Y tap1, u16 LE
23   2     X tap2, u16 LE
27   2     Y tap2, u16 LE
31   2     base X, u16 LE
35   2     base Y, u16 LE
39   1     CRC16 low
40   1     CRC16 high
41   23    0x00
```

Each 2-byte value sits at the start of a 4-byte slot; the following two bytes
are zero. CRC is Modbus CRC16 (poly 0xA001, init 0xFFFF) over bytes 0…38.

The 6 sample rows are interleaved X/Y for the 3 taps; the 2 base rows are the
target centre X/Y for this point. The point index is repeated in byte 6.

## Wire transport

The box uses USB bulk transfer to the vendor interface. On the PC the gun's
vendor interface is `VID_2E2C&PID_0631&MI_02`; the existing
`gun_q_lib.ps1`/`HidIdentity` path writes a 65-byte buffer (report ID 0 + 64
bytes). Use the same path, 200 ms between reports.

## Implementation

1. Add a **Box Cal** mode to the tray gallery: the 8 targets above, 3 taps per
   point, a trigger pull samples the current raw aim, Start/Q enters/cancels.
2. Build the 8 reports and send them through the vendor interface.
3. Do not overwrite the host `cal_<sn>.json`; the gun stores this calibration.
4. Keep the host 5-point `RectCalib` calibration separate (tray Calibrate/F12).
5. Log each point/tap and the final send result.

## Post-send finalize: disproven (2026-10-23 18:30)

Re-decompiled `GaimeService.apk` shows the box sends **nothing** after the
eight reports:

- `GaimeCalibration` calls only two service methods:
  `updateCalibrationDataBuffer([B)` (48×5 flattened, at finish) and
  `updateBasePointDataBuffer([B)` (16×5 flattened, when the last base point is
  set). It never calls `setStartClaimInterface`, `setStopClaimInterface`,
  `setCalibrationModeInGame` or `setSendGunMode`.
- Those claim/mode calls belong to the in-game path. On Android they
  claim/release the USB interface and set flags; they send nothing to the gun.
- The service writes the eight reports only when both buffers are flagged,
  then clears its sending flag. There is no commit/ack report.
- `sendGunModeReport` is a separate single/multi-shot setting
  (`05 01 05 06` / `05 01 05 01`), not a calibration finalize.

The packer (`encryptDataBuffer`) is misnamed: it writes header `05 21 06`,
takes bytes 2–3 of each 5-byte group as the u16 value, packs the base point at
31/35 and the CRC at 39. `GunBoxCal` already matches it byte for byte.

Hardware corroboration: the trigger build was run twice (18:01:15 and
18:02:01 in `tray-20261023.log`) and the gun stayed alive; `GaimeFeeder
--list` at 18:30 still returned `sn11111`. The 17:55:41 wedge was a one-off
(unexplained), not a missing finalize.

## Open

- Hold-still noise after the run: **117.1 px, max jump 63 px** over 120
  samples (best so far; previous best 136.4 px). Whether aim/lock improves in
  a game is still unmeasured.
- The box does not read an ack either, so there is no ack to replicate.
- Two-gun path: the service switches devices; each gun needs its own run.
