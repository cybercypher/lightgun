# G'AIM'E Pico 2 absolute-mouse bridge

Turns a Raspberry Pi Pico 2 into **two independent absolute HID mice** for
Windows, fed by the PC over USB CDC. The G'AIM'E guns stay plugged into the PC;
the host reads and calibrates them and sends already-mapped X/Y + buttons to
the Pico.

This replaces the ViGEm Xbox pad path for DemulShooter/TeknoParrot: absolute
mice are the device class DS and TP already understand, and a dedicated USB
device with fixed descriptors gives stable RawInput paths across
unplug/replug and reboots.

> **Status:** built and verified on hardware 2026-10-23 (Pico 2, Pico SDK
> 2.2.0, TinyUSB 0.18.0, arm-none-eabi-gcc 13.3.1, 0 warnings). Flashed and
> tested: both HID mice enumerate with the fixed serial, the CDC port comes up
> healthy, absolute moves map to the primary screen, player 1/2 routing works,
> a button frame produces a left click, and the bridge is silent when idle.

## Layout

```
pico/gun_bridge/
├── CMakeLists.txt
├── main.c                  firmware: CDC parser -> two HID mice
├── usb_descriptors.c/.h    device/config/report/string descriptors
├── tusb_config.h
├── PROTOCOL.md             wire format
└── host/
    ├── gun_bridge_sender.py         demo sender (pyserial)
    ├── fix_demulshooter_paths.ps1   rewrite DS P1/P2 paths
    ├── start_demulshooter.ps1       rewrite paths, then start DS
    ├── requirements.txt
    └── validate_descriptors.py      descriptor sanity check
```

## Hardware

- Raspberry Pi Pico 2 (RP2350). Pico 1 also works; build with
  `-DPICO_BOARD=pico`.
- One data-capable USB cable, Pico powered from the PC.
- One Pico serves both mice; no other hardware.

## Build

Prerequisites: Pico SDK 2.x, CMake ≥ 3.13, `arm-none-eabi-gcc`, Ninja or Make.
Tested with Pico SDK 2.2.0, arm-none-eabi-gcc 13.3.1, CMake 4.4 and Ninja 1.13
(0 warnings).

```bash
export PICO_SDK_PATH=$HOME/pico-sdk
cmake -S pico/gun_bridge -B pico/gun_bridge/build -DPICO_BOARD=pico2 -G Ninja
cmake --build pico/gun_bridge/build -j
```

Output: `pico/gun_bridge/build/gun_bridge.uf2`.

## Flash

1. Hold **BOOTSEL**, plug the Pico into USB, release. (With USB already
   connected: hold BOOTSEL, tap **RUN**, release BOOTSEL.)
2. It mounts as `RP2350` (Pico 2) or `RPI-RP2` (Pico 1); copy
   `gun_bridge.uf2` onto it.
3. The Pico reboots into the bridge.

## What Windows sees

- USB device: `G'AIM'E Bridge`, VID `1209`, PID `0A11`, serial
  `GAIME-BRIDGE-0001`, two interfaces:
  - `MI_00` — absolute mouse 1 (usage page `0x01`, usage `0x02`, report ID 0,
    6-byte reports (5 buttons + wheel + X/Y), logical range 0..32767).
  - `MI_01` — absolute mouse 2.
- A CDC port (e.g. `COM7`).

Verify:

```powershell
Get-PnpDevice -PresentOnly |
  Where-Object InstanceId -Like 'HID\VID_1209&PID_0A11&MI_*' |
  Select-Object FriendlyName, InstanceId
```

The two RawInput paths use the **mouse** interface GUID (not the HID gamepad
one) and look like:

```
\\?\HID#VID_1209&PID_0A11&MI_00#<instance>#{378de44c-56ef-11d1-bc8c-00a0c91405dd}
\\?\HID#VID_1209&PID_0A11&MI_01#<instance>#{378de44c-56ef-11d1-bc8c-00a0c91405dd}
```

Windows merges absolute mice into one desktop pointer, so with both guns live
the cursor will jump between them. That is normal; DS/TP read RawInput per
device and keep the players separate.

## Host demo

```bash
pip install pyserial
python pico/gun_bridge/host/gun_bridge_sender.py --demo --players 0,1
```

The demo walks two squares and clicks the left button every two seconds. See
`--help` and `PROTOCOL.md`.

## DemulShooter

DemulShooter already registers RawInput **Mouse 0x01/0x02 with report ID 0**,
which is exactly what this device is, so no patch or virtual pad is needed.

1. Plug the bridge in and confirm both `MI_00`/`MI_01` interfaces exist.
2. Close DemulShooter.
3. Bind the paths:

   ```powershell
   powershell -ExecutionPolicy Bypass -File pico\gun_bridge\host\fix_demulshooter_paths.ps1
   ```

   It finds `MI_00`/`MI_01`, backs up `config.ini`, and writes
   `P1DeviceName`/`P2DeviceName` (and sets both modes to `RAWINPUT`). Use
   `-DryRun` to preview, `-ConfigPath` if DS is not in `C:\games\demulshooter`.
4. Start DemulShooter, then the game. Check `Debug_*.txt` (`-v` required) for
   `P1 device plugged and found`.

   Or do steps 3-4 in one go:

   ```powershell
   powershell -ExecutionPolicy Bypass -File pico\gun_bridge\host\start_demulshooter.ps1 -Target model2 -Rom vcop2
   ```

Trigger = left mouse button; B = right, A = middle, Coin = back, Start =
forward; the wheel carries a signed delta for testing. Aim = the absolute X/Y
in the report. DS scales
the device's absolute range to the **primary screen**, so keep Windows display
scaling at 100% and run the game at the screen resolution (the same rules as
Sinden/AimTrak).

**DS enumerates once at startup.** Replugging the bridge while DS runs needs a
DS restart even though the path is unchanged.

## TeknoParrot

TP's native lightgun mode reads RawInput mice (up to two). The bridge appears
in its device list; bind P1/P2 and map the trigger to left click. Both mice
report the same product string (`G'AIM'E Bridge`), so use the path or test each
entry — the `MI_00`/`MI_01` interface is the only difference Windows exposes.

## Identity and stability

Why the path should not change any more:

- The USB device has a **fixed serial string**. Windows' USB instance id is
  `USB\VID_1209&PID_0A11\GAIME-BRIDGE-0001`, which does not depend on the port.
- HID interface instance ids are derived from that parent id plus the interface
  number (`MI_00`, `MI_01`), so they survive replugs, port changes, and
  reboots.
- `bcdDevice` is fixed at `1.00`. Changing the descriptor layout, serial, VID
  or PID makes Windows create a **new** instance and breaks existing bindings.

Remaining caveats:

- One bridge per PC with the default serial. For a second Pico, change
  `STRING_DESC_SERIAL` (and ideally the PID) at build time.
- If Windows assigns a duplicate-suffixed instance (`&0001`) or a driver
  reinstall creates a new path, re-run `fix_demulshooter_paths.ps1`.
- DS still matches the exact path; the helper exists for the cases where
  Windows does move it.

## Protocol

See [PROTOCOL.md](PROTOCOL.md). Short version: 12-byte frames
`A5 5A | ver | player | buttons | 0 | x u16 | y u16 | seq | crc8`, sent over
CDC at up to 250 Hz. Coordinates are 0..32767, top-left origin; calibration and
window mapping happen on the host.

## Troubleshooting

| symptom | check |
|---|---|
| no CDC port | Device Manager → Ports; try another cable; re-flash |
| Pico not enumerated | BOOTSEL flash; `Get-PnpDevice` for `VID_1209` |
| mice appear but do not move | sender's `--port`, CRC, `player` 0/1 |
| DS does not see P1 | DS `-v` log; run the path helper; restart DS |
| cursor jumps between guns | expected with two absolute mice on the desktop |
| aim offset in game | 100% scaling, game resolution, host calibration, DS range |

## Out of scope

- Reading the G'AIM'E guns on the Pico (host does that).
- Homography / game-window mapping (host side).
- Wiring this into `GaimeFeeder`/`GaimeTray`; the CDC protocol above is the
  integration point.
