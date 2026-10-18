# Gun camera tap — recipe + risks (from gaime_mods, verified by digest 2026-10-18)

Source: `/home/USER/gaime_mods` (shallow clone of github.com/mattkanwisher/gaime_mods —
same hardware as ours, same fw-era anchors). Lab notes: `FINDINGS.md`,
`docs/gun-camera-abi.md`. Web-hunt + digest delegated to deepseek-v4.1-flash; facts below
are theirs, cited to that repo; not yet verified on OUR guns.

## What exists
- UVC (`"Dashine UVC"`, IF3/4) is a hardcoded **test-pattern feeder** — no V4L2
  capture in `uvc-gadget`. Camera frames never appear on UVC. (Explains our earlier
  "test pattern only" observation.) `tools/camera_probe.ps1` parked as a future-fw check.
- Real frames: 224x224 @ 60 fps into the gun's NPU via `libvideo.so`
  `video_stream_*` ABI; `/dev/video3` is EBUSY while `/app/bin/gun` runs.
- `frame_tap.so`: LD_PRELOAD interposer on `video_stream_set_frame_cb`, complete but
  **never fired** (author's camera FPC disconnected). Frame pixel-data offset in the
  struct is UNRESOLVED (SIGSEGV probe design). We would be the first to run it.

## Deployment (USB-only, no disassembly) — via `tools/gun_ota.py push`
- func 0x01-0x08 flow = arbitrary file write as root to `/app`, MD5-gated
  (mismatch leaves `temp_file.bin`, target untouched), chmod 0777.
- Sequence: 1) push stock `bin/gun` → `/app/bin/gun.real` (verify MD5 `b073cf7d…`),
  2) push `frame_tap.so` → `/app/frame_tap.so`, 3) push `gun.wrapper` → `/app/bin/gun`
  (usb_monitor execs `/app/bin/gun` every boot), 4) `reboot --yes`.
- `gun_ota.py` needs a 1-line Windows port (hardcodes macOS hidapi lib path).
- `/app` is ext4, only **4 MiB** — space budget tight (5 RGB frames ≈ 752 KB).

## Recovery (ranked)
1. OTA rewrite of pristine files from `backup/gun/app_fs/` — works only while
   `/app/bin/gun` still launches (the OTA handler lives INSIDE gun).
2. **J3 UART**: unpopulated through-holes `GND/TX/RX/V3.3` on the main board,
   115200 8N1, root, no password — requires opening the gun. `tools/uart_shell.py`.
3. No FEL/loader mode for the gun; MSC mod exposes NAND read-only (cannot restore).

## Risks
- A broken `/app/bin/gun` (or a .so that aborts gun) = aim dead + OTA dead → J3.
  Mitigations: verify-stock-first push, defensive wrapper (LD_TRACE_LOADED_OBJECTS
  check + no-preload fallback — proposed, not upstream), MD5-gated transport.
- Tap installs SIGSEGV handlers inside gun's process; first 5 frames do synchronous
  NAND writes inside the ISP callback thread (startup stall risk).
- Frame exfil: NO HID read path (func 0x07 get_info reads `system_info.json` keys
  only). J3 serial ≈ 4-5 KB/s (gray 224x224 ≈ 10-12 s/frame), or MSC mod (Windows
  ext4 mount problem; disables UVC).
- **Live streaming is NOT built** — tap dumps ≤ 5 stills (bring-up design). A
  calibration rig needs a redesign: RAM ring buffer + trigger-synced dump (tip bit
  known in-process) + a fast exfil channel.

## TC3 scatter bug — camera is NOT on the critical path
Gun-side aim is proven true (tray log raw stable ±~100 counts at trigger edges;
MSPaint end-to-end true). The scatter is fork/game-side: the pending instruments are
the Recalibrate→Trigger bind test, REA/Frida on `pcsx2-qtx64.exe`, and the fork's own
framebuffer capture. The camera rig is the universal instrument (any game/emulator,
true optical end-to-end) for AFTER the TC3 fix.
