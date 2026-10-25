# The white reference border (one thickness, everywhere)

Rule: every white reference border is **32 screen pixels** thick and is on by
default. Gallery, host calibration window and the game (ReShade over
TeknoParrot's ELF loader) all draw the same 32 px.

Why a fixed pixel count and not a percentage: the border is the frame the
player aims inside. If it scales with the window or the resolution, the
reference moves between the calibration window, the test gallery and the game,
and the aim "shifts" for reasons that have nothing to do with the gun. Sinden
does the same thing — fixed pixels, drawn at the game's render resolution.

One number lives in two places, both documented here:

| where | constant |
|---|---|
| tray gallery + host calibration window | `src/GaimeFeeder/ScreenBorder.cs`, `ScreenBorder.Pixels = 32` |
| ReShade over the game | `tools/reshade/GaimeBorder.fx` / preset `BORDER_PX=32` |

Change both together, then redeploy the tray and rerun the ReShade installer.

**Why the gun cares (corrected 2026-10-24).** G'AIM'E *is* a camera tracker: the
gun feeds real 224x224 @ 60 fps frames into its NPU (`libvideo.so` →
`/dev/video3`; the UVC interfaces are only a test-pattern gadget) and fuses that
with the LSM6DS3 IMU. The box's own apps confirm the frame is a first-class
feature, not decoration: `hidcommunication.apk` carries
`game_frame_white`, `game_frame_black`, `game_frame_pink`, the settings button
`imageButton_configure_game_frame_color` (`setupConfigureGameFrameColor`,
`updateFrameColorButton`, `updateFrameColorButton`) and the binder pair
`TRANSACTION_setFrameColorMode` / `getFrameColorMode` — i.e. the colour mode is
pushed to the device over a transaction, not just painted on the UI. So the
official firmware offers a white border as a tracking aid, which is exactly the
Sinden model. The old "no camera, no border, the solver cannot see it" line in
this repo was wrong; it came from an early look at `/app/bin/gun` strings and
should not be repeated.

Consequence for us: keep the border on while calibrating (it is, now), and treat
the gallery's `M` + border toggle as an A/B experiment — same held gun, border on
vs off, compare the logged `noise X.X px`. If the tracker uses the frame, the
numbers should move.

**Which path actually aims.** `/app/bin/gun` runs two independent sources:

- **IMU first.** `LSM6DS3TR-C` on `/dev/i2c-2` (`/dev/I2C2_LSM6DS3`), read as
  gyro x/y/z with per-axis bias (`get_gyrozero*`, `FusionOffset*`), fused by
  **Fusion AHRS** (`FusionAhrsUpdateNoMagnetometer`, quaternion → `comp_imu_get_yaw/
  pitch/roll`) and a Kalman filter. This is what makes the gun work with no
  screen reference at all.
- **Camera second.** `/dev/video3` → `comp_video_start` → 224x224 frames into the
  NPU (`nna_gaime_open/process`, `model/gaime/gaime.ezb`) → `nna_get_xy`, with a
  distance estimate (`calculate_distance`, `kalman_filter_modify_distance`,
  `cal_mean_second_distance_vector`). This is the absolute/drift/distance
  correction the white frame feeds.

So the border only matters to the camera path. **Gun 1 has the operator's 3D-printed
black lens cover**, and it still aims (~31 px in the gallery): that is the IMU
path running alone, which is also why an anchorless gun drifts and needs
recalibration. A border A/B on Gun 1 must therefore show *no* change; Gun 2 is
the sighted gun and the one to measure.

Hypothesis worth checking: the covered lens is zero light into the ISP, and the
handoff's `ISP0 select timeout → mcsi_reset → sensor_set_stream` loop appeared on
Gun 1 — the cover may be part of why that loop shows up there.

## Tray windows

`ScreenBorder` is shared, `Enabled` defaults to **true**, and the toggles stay
for A/B noise measurements only:

- gallery: `V` key, or the gun's `C` button;
- calibration window: `B`;
- each toggle is logged (`gallery [...]: border ON/OFF`) so the noise figures
  before and after are comparable.

Verified 2026-10-24 at 3840x2160 (96 DPI): a desktop screenshot of
`GaimeTray.exe --test` measured a 32 px white band on all four edges
(pure-white rows 1..32, with a 1 px DWM edge blend at the boundary).

## ReShade over TeknoParrot (ELF games)

TeknoParrot loads the Raw Thrills / Linux titles through `ElfLdr2\BudgieLoader.exe`
(32-bit), and the game's renderer is **OpenGL** (`nvoglv32.dll`). ReShade's GL
path is a proxy named `opengl32.dll` sitting next to the exe, so the install
goes into `C:\games\teknoparrot\ElfLdr2\` and **every ElfLdr2 game gets it**,
not just Terminator Salvation. The border is applied on launch from a preset —
no `Home` key, no overlay interaction.

Install / change / revert:

```
powershell -NoProfile -ExecutionPolicy Bypass -File \\wsl.localhost\Ubuntu-24.04\home\USER\lightgun\tools\Install-ReshadeBorder.ps1
powershell ... -BorderPx 24
powershell ... -Uninstall
```

Files it writes into `C:\games\teknoparrot\ElfLdr2\`:

```
opengl32.dll                     ReShade 6.8.0.2156 (32-bit), copied from the
                                 existing ReShade install on this machine
ReShade.ini                      preset auto-load, tutorial skipped
ReShadePreset.ini                Techniques=GaimeBorder@GaimeBorder.fx
reshade-shaders\Shaders\GaimeBorder.fx
reshade-shaders\Shaders\ReShade.fxh
```

ReShade's DLL is a universal proxy (one binary exports the D3D11, Vulkan and
wgl entry points) — the copy on this machine is the `D3D11.DLL` proxy from the
Wolverine install and it works as `opengl32.dll`. The script refuses a 64-bit
DLL, refuses to overwrite a foreign `opengl32.dll`, and checks that
`BudgieLoader.exe` is where it expects.

## Verifying

Measured 2026-10-24 22:26 while launching Terminator Salvation from TP
(`Windowed=1`, 3840x2141 client):

```
| INFO | Initializing crosire's ReShade version '6.8.0.2156' (32-bit) loaded
         from 'C:\games\teknoparrot\ElfLdr2\OPENGL32.dll' into
         '...\BudgieLoader.exe' ...
| INFO | Redirecting wglCreateContext / wglSetPixelFormat ...      <- GL hooked
| INFO | Resizing device context ... to 3840x2141
| INFO | Successfully compiled '...\GaimeBorder.fx' in 0.007000 s.
```

Screenshot at 3840x2160 from that run: the left border is columns 8..39 =
**32 px** and the top edge is 32 px of border under the window's white title
bar. The user confirmed in the game that it looks right.

The ReShade log is the check for every future launch:

```
C:\games\teknoparrot\ElfLdr2\ReShade.log
```

If the log does not exist after a launch, the loader did not pick up the proxy
(check that `opengl32.dll` is next to `BudgieLoader.exe` and that the game was
started from TP, not from the game folder). If it initialises but the frame is
absent, check the preset name and that the technique is enabled
(`Techniques=GaimeBorder@GaimeBorder.fx`).

**Windowed-mode caveat:** with `Windowed=1` and the custom resolution still at
3840x2160, the loader creates a 3840x2141 client *plus* a window frame, which is
larger than the 3840x2160 screen. The window then overflows by ~8 px on the
right and ~11 px at the bottom, so the right and bottom border edges are cut off
by the screen (measured: 24 px visible on the right, none at the bottom). In
fullscreen (`Windowed=0`) the client is a true 3840x2160 and all four edges show
the full 32 px. For a fully visible frame while windowed, pick a smaller custom
resolution (e.g. 2560x1440).

Note: because the proxy covers ElfLdr2, a **non-lightgun** ELF game also gets
the border. Remove the proxy with `-Uninstall` if that is ever a problem.
