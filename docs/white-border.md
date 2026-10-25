# The white reference border (one thickness, everywhere)

Rule: every white reference border is **32 screen pixels** thick and is on by
default. Gallery, 5-point calibration window and the game (ReShade over
TeknoParrot's ELF loader) all draw the same 32 px.

Why a fixed pixel count and not a percentage: the border is the frame the
player aims inside. If it scales with the window or the resolution, the
reference moves between the calibration window, the test gallery and the game,
and the aim "shifts" for reasons that have nothing to do with the gun. Sinden
does the same thing — fixed pixels, drawn at the game's render resolution.

One number lives in two places, both documented here:

| where | constant |
|---|---|
| tray gallery + 5-point window | `src/GaimeFeeder/ScreenBorder.cs`, `ScreenBorder.Pixels = 32` |
| ReShade over the game | `tools/reshade/GaimeBorder.fx` / preset `BORDER_PX=32` |

Change both together, then redeploy the tray and rerun the ReShade installer.

**What it does and does not do for G'AIM'E.** The gun has no camera: aim is
computed on-device (NN + LSM6DS3 IMU/AHRS/Kalman), so the border is *not* an IR
reference and the gun's solver cannot see it. Its job is the player's: a
constant frame that shows where the game's active area ends (useful with 4:3
content inside a 16:9 screen) and gives something straight-edged to judge
off-angle aim against. Sinden's camera does use its border; ours does not.

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

`C:\games\teknoparrot\ElfLdr2\ReShade.log` after launching a game from
TeknoParrot:

```
| INFO  | Initializing crosire's ReShade version '6.8.0.2156' (32-bit) loaded
          from '...ElfLdr2\opengl32.dll' into '...BudgieLoader.exe' ...
| INFO  | Successfully compiled '...\GaimeBorder.fx' ...
```

If the log does not exist after a launch, the loader did not pick up the proxy
(check that `opengl32.dll` is next to `BudgieLoader.exe` and that the game was
started from TP, not from the game folder). If it initialises but the frame is
absent, check the preset name and that the technique is enabled
(`Techniques=GaimeBorder@GaimeBorder.fx`).

Note: because the proxy covers ElfLdr2, a **non-lightgun** ELF game also gets
the border. Remove the proxy with `-Uninstall` if that is ever a problem.
