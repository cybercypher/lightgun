# Box calibration flow — `GaimeCalibration.apk` (static analysis)

Date: 2026-10-23
Source: extracted Android system from the decrypted `GAIMEENC` box firmware.
Status: calibration flow confirmed; exact USB buffer layout pending the
`GaimeService`/`hidcommunication` worker.

## App

- Package `com.dashine.gaimecalibration`, v1.0 (vc 1), minSdk 26, targetSdk 34.
- `sharedUserId="android.uid.system"`; permission
  `com.dashine.gaimeservice.PERMISSION`.
- One activity: `CalibrationActivity` (MAIN/LAUNCHER + HOME, exported,
  singleTop).
- The launcher is triggered from `hidcommunication` with
  `Calibrate_mode=0` on a 3000 ms key hold.

## Calibration sequence

1. Two tap-to-continue instruction pages.
2. **8 points** on a 3×3 grid with the centre skipped (`calibrationPoint`
   4 jumps to 5).
3. **3 taps per point = 24 taps**, each validated by XY variance ≤ `0.0017`.
4. 20 ms tap debounce; input ignored while the shot GIF plays.
5. Key code 30 aborts (`resetCalibrationMode()` + `closePage()`).
6. Finish calls the service, not a local file.

## Service call

- Explicit bind: package `com.dashine.gaimeservice`, action
  `com.dashine.gaimeservice.GAIMESERVICE`.
- AIDL `IGaimeAidlInterface`.
- Only two calls:
  - `updateCalibrationDataBuffer(byte[48][5])` → 240 bytes flattened
  - `updateBasePointDataBuffer(byte[16][5])` → 75/80 bytes flattened
    (worker reported 16×5; exact flattened size to confirm)

## Transport (from the calibration app + prior knowledge)

- HID report `0x05 0x21 0x06` + **Modbus CRC16 (0xA001)** over 39 bytes.
- **200 ms** between chunks.
- VID/PID reported by the app worker as `0x0631`/`0x2E2C`, which is the reverse
  of the gun's USB descriptor (`VID 0x2E2C`, `PID 0x0631`). Resolve with the
  protocol worker before formatting frames.
- Gun stores the calibration; the box has no calibration file on disk.
- The gun's writable filesystems contain no calibration file (`snapA`), so the
  stored data is likely in the MCU/NVRAM, not `/app`/`/data`.

## Open

- Exact 8-point screen positions (decode `activity_grid.xml` / drawables).
- Meaning of the 48×5 and 16×5 buffers: point count, coordinate range,
  per-point fields, ordering, and whether target screen coordinates are
  included or implied by the fixed grid.
- Ack/status handshake and whether calibration survives gun power cycles.
- Two-gun path (`setGunMode`/`setSwitchGun`) from the protocol worker.

## Evidence

- Worker summary from `GaimeCalibration.apk` DEX/VDEX/manifest; strings quoted:
  `Calibrate_mode`, `com.dashine.gaimeservice.GAIMESERVICE`,
  `calibration_data_buffer out of bound...`, `CalibrationActivity`,
  `Finish Calibration...`.
- No colour/pattern calibration code was found; the docx black/white/pink
  reference is not this flow.
