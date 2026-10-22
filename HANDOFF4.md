# HANDOFF4 — G'AIM'E gun recovery + feeder changes (2026-10-21, late)

Goal: 2× G'AIM'E guns as GunCon-style pads for Teknoparrot/DemulShooter.
House rules: verify on the machine; label hypotheses; never merge the two guns'
input; don't add admin requirements.

---

## 0. READ FIRST — blocking items

1. ~~The watchdog currently deployed on gun sn11111 is the BUGGY build.~~
   **DONE 2026-10-22 — rev 2 deployed and verified on the gun (sn11111,
   md5 `3c93094a909bc4af142ea3a953d80d0d`, 3,399 B).**

   History: rev 1 (2,078 B, `e214ca10…`) added `mkdir -p /tmp/logs` and created a
   target before each new symlink, but it only converted *real* files — on every
   boot after the first, `/app/*.log` are already symlinks while `/tmp` is fresh,
   so it skipped them and left all links dangling. Rev 2 recreates the missing
   target for existing symlinks at startup **and** once a minute, and its 5 MB
   cleanup now only counts non-empty logs (the empty recreated targets are always
   the newest mtime and were pushing the real logs out of the keep-2 window).

   Verified live by wiping `/tmp/logs` with the daemon running: all 9 targets
   were recreated, `/app/*.log` all resolve, and a feeder `--list` produced a new
   heartbeat line in `/tmp/logs/19700401.log` (86 B) — i.e. the daemon's log
   write still succeeds through the symlink.

   Still worth doing once: `sync; reboot` and confirm at boot —
   - `ls -la /app/*.log` → every entry is `-> /tmp/logs/<name>` and resolves
   - `ls -la /tmp/logs` → targets exist (not dangling)
   - `ps w` → `gun` and `{logs_watchdog.s}` both present
   - `df -h /app` → well under 100%

   Redeploy, if ever needed (uploads + md5-verifies + `mv` replaces atomically):
   ```
   powershell.exe -NoProfile -ExecutionPolicy Bypass `
     -File \\wsl.localhost\Ubuntu-24.04\home\USER\lightgun\tools\Upload-GunFile.ps1 `
     -LocalPath \\wsl.localhost\Ubuntu-24.04\home\USER\lightgun\tools\gun\logs_watchdog.sh `
     -RemotePath /app/shell/logs_watchdog.sh -Port COM4
   ```
   If the app is ever dead after a boot, check link targets first.
   The patched launcher is `tools/gun/app.sh`
   (md5 `b314d89b7c8b6730ea5aa6a9b02ba5b0`, already on the gun).
2. ~~Feeder poll rate is still 2 s.~~ **DONE** — rescan is now 10 s via
   `HotplugRescanMs` in `src/GaimeFeeder/Program.cs` and
   `src/GaimeTray/FeederEngine.cs`, built 0/0, committed (`83fafa4`) and
   redeployed to `C:\games\feeder`.
3. ~~Everything in the repo is uncommitted.~~ **DONE** — committed as
   `83fafa4` (feeder + deployment) and `b3ce325` (gun watchdog + uploader);
   working tree clean. `logs/gunref/` is gitignored; reference/deployable gun
   files live in `tools/gun/`.

---

## 1. Repo task (instructions.txt) — implemented, committed, builds clean

Committed as **`83fafa4`** (this task + deployment) and **`b3ce325`** (gun-side
watchdog + uploader). Working tree clean at handoff. `logs/gunref/` is
gitignored; `instructions.txt` was committed with the spec.

What changed (all four instructions.txt items done):

- **Change 1 — trigger = X only.** `VigemPad.Feed()` sets `Xbox360Button.X`
  only; `RightShoulder` and the `RightTrigger` slider are gone.
  `docs/buttons.md` now has the DemulShooter table:
  1 A / 2 B / 3 X (TRIGGER) / 4 Y / 5 LB / 6 RB / 7 Back (coin) / 8 Start /
  9 L3 / 10 R3. X Axis 0x30 / Y Axis 0x31 left alone.
  `XCheck.cs` display and help text updated to show `X` (extra, not in spec).
- **Change 2 — deterministic pad slots.** `EnsurePads()` iterates
  `OrderBy(g => g.PadIndex)` in both `Program.cs` and `FeederEngine.cs`.
  Both `AttachGuns` compute `needRebuild` (newcomer has lower PadIndex than an
  existing pad's gun) and tear all pads down (`CleanupPads()` / `DisposePads()`)
  so `EnsurePads` recreates them in order.
- **Change 3 — valid-lock aim gate** in shared `GunState.cs`:
  `LockWin = 8` interval ring, `LockMinHz = 200.0`, `ValidLock` property.
  Applied in feeder `Feed()` as `inside && (g.ValidLock || testStick)` and in
  tray `Feed()` as `inside && g.ValidLock`. Buttons/trigger always forward.
  NOTE: instructions claimed a "max-jump gate" exists — it does not; only the
  median-of-5 spike filter is present, and raw X/Y are already filtered.
- **Change 4 — cursor bodyguard** (replaces `QuarantineCursor`/`Quarantine`):
  `RawInput.cs` handles `hdr.Type == 0` (real mouse only; guns are
  digitizer+keyboard) and snapshots `GetCursorPos` into
  `RawInputWindow.MouseX/MouseY/HaveMousePos`. `CursorBodyguard` restores the
  cursor when the report is fresh (<500 ms) and `!g.ValidLock`; a valid lock is
  left alone. No more `SetCursorPos(0,0)`.

Extra work in the same tree:

- **`AppPaths.cs`** (new, linked into GaimeTray): data root = walk up from the
  exe until a `guns.json` is found, else the exe folder.
  Deployed → `C:\games\feeder`; dev → repo root. `GunMap`, `RectCalib`,
  `FeederEngine.LogDir`, `MainForm.LogDir` all use it.
- `README.md`: deployment section + trigger=X.
- `tools/Deploy-Feeder.ps1` (new) and all `tools/Launch-*` / `Feeder.ps1` /
  `XCheck.ps1` repointed at the deployed copy.

Build status: **0 warnings / 0 errors** for both projects (Debug builds and
`dotnet publish -c Release`), verified from WSL via
`"/mnt/c/Program Files/dotnet/dotnet.exe"`.

---

## 2. Deployment — `C:\games\feeder`

Self-contained runtime (Release publish of both apps into one flat folder):

```
GaimeTray.exe / GaimeFeeder.exe (+ dll, deps, runtimeconfig, pdb,
                                 Nefarius.ViGEm.Client.dll)
guns.json
calibrations\cal_*.json
logs\   (tray-<date>.log, feeder.log, xcheck.log, capture-*.txt)
Launch-Tray.cmd   Launch-Feeder.cmd   Feeder.ps1   XCheck.ps1
```

- Dev source and builds stay in WSL (`/home/USER/lightgun`).
- Redeploy: `tools\Deploy-Feeder.ps1` (publishes Release into `C:\games\feeder`
  and copies `guns.json` + `calibrations\*.json`; leaves `logs\` alone).
- Autostart registry value is **not set**; ticking "Start with Windows" writes
  `Environment.ProcessPath` = `C:\games\feeder\GaimeTray.exe`.
- Verified `--list` resolves data in three cases: dev bin, deployed with
  `guns.json`, deployed without (falls back to the exe folder).

---

## 3. The gun failure — root cause and fix

**Bad gun = sn `11111`. Good gun = sn `22222`.** (Verified via
`/app/system_info.json` on the gun, and MI_02 `sn` query.)

### Symptom (host side)

- Gun enumerates: 8 PnP nodes, all Status OK, all 5 interfaces.
- UVC (MI_03/04) streams the synthetic test pattern at 30 fps.
- **Zero** reports on digitizer (0D/04) and keyboard (01/06).
- Vendor MI_02 (0xFF00) queries time out: `sn`, `fw.ver`, `model.ver` all
  `[<no reply>]`.
- Same behaviour in the vendor's own Android mini-console.
- `GaimeFeeder --list` / tray hot-plug show **no gun**, because enumeration
  requires the MI_02 `sn` reply — if the app layer is dead the gun is invisible
  by design.

### Root cause (confirmed by reading the gun with a root shell)

**The `/app` ext4 partition (nandblk5, 3.9 MB) was 100% full.**

The `gun` daemon writes one log file per gun-"day" to `/app/<day>.log`. Those
accumulated (45 files) until the partition filled. `gun` then died at startup:

```
write(): : No space left on device
gaime_log init:Filename: 19700323.log, Date: 1970-03-24
i2c-2 open dev success          <- then nothing
```

That kills the whole application layer → no aim, no buttons, no config replies.
Storage is the only reason; the app binary is present and runnable (72,312 bytes
on both units, but a **different build** — see §4) and runs fine once there is
space.

### Why it filled — our own poll rate

Measured in the 1.9 MB log (`19700211.log`):

| content | lines | bytes | share |
|---|---|---|---|
| `[hid_handler] Success to response box. Function code: 0x07 (0)` | 23,079 | 1,961,715 | **98.8%** |
| everything else (boot config + one `first push L = 0.100000`) | 3 | 23,224 | 1.2% |

23,079 heartbeat lines in one gun-day ≈ one query every ~3.7 s. The feeder's
hotplug rescan polls MI_02 every **2 s** forever → ~43,000 queries/day →
**~3.7 MB/day**, which is exactly the 3.8 MB observed. The vendor's own
`app.sh` cleaner is count-based (`if LOG_COUNT -gt 4 → rm oldest`) and did not
keep up.

The log is **write-only and never read** — 98.8% heartbeat, no diagnostic
value. Treat it as disposable.

### What was done to fix it

1. Backed up three stale logs off the gun first:
   `logs/gunref/old_logs.tgz` (71,609 B, md5 `72fd7ba9efe951b2ff345718e509d57e`,
   md5-verified after transfer).
2. Deleted the stale `/app/*.log` (kept the current day's).
   Result: `/app` 100% → 41% used, 2.2 MB free.
3. `reboot` → the app auto-started and held (`gun` running, CPU climbing).
4. **Permanent fix (deployed):**
   - `/app/shell/logs_watchdog.sh` (new): (a) re-points every real
     `/app/*.log` into `/tmp/logs/<name>` as a symlink — tmpfs, wiped each
     boot, so flash can never fill from logs; (b) every 60 s, if `/tmp/logs`
     exceeds 5 MB, deletes all but the two newest.
   - `/app/shell/app.sh` patched to launch it in the `start` branch
     (`/app/shell/logs_watchdog.sh &`), right after `mount -o rw,remount /`.
     Vendor original preserved as `/app/shell/app.sh.vendor`.
   - Both uploaded base64 in 64-char chunks, then md5-verified on the gun:
     `app.sh` = `b314d89b7c8b6730ea5aa6a9b02ba5b0` (1,234 B),
     `logs_watchdog.sh` = `ca0257c0…` (1,661 B) — **superseded, see §0**.
   - The vendor's `# $APP_NAME &` line was left commented exactly as shipped.

### Verified working after the fix

- `/app` 44% used; `/app/*.log` are symlinks to `/tmp/logs/...`;
  `logs_watchdog.sh` running; `gun` running with CPU time climbing.
- MI_02 answers: `sn [{"num":"11111"}]`, `fw.ver [v2.5.1]`.
- Host raw capture (20 s): 57 gun lines, keyboard make/break for `vk=0x51`
  (Q/Start) and `vk=0x42` (B), digitizer coordinates (e.g. X=5104, Y=4897)
  plus the expected full-range garbage without screen lock.

### Not yet explained / still open

- Kernel log shows a repeating ISP watchdog loop on this unit:
  `error: ISP0 select timeout.` → `viss_isp watchdog err: isp0` →
  `mcsi_reset` → `sensor_set_stream on:0/on:1` → `first frame done!`.
  The app still runs and streams with this going on, so it is not the
  blocker — but it is a real background instability, not chased.
- `usb_monitor` is the launcher: it kills `gun` on USB detach and starts it on
  USB configure (log lines: `USB state changed from 'configured' to 'not
  attached'. Preparing to kill gun.` and `'gun' process start requested
  (pid=925)`). `app.sh` ships with the app launch commented out on **both**
  units, so this is the normal path.
- `dmesg` also showed `SLC_DBG GC incomplete data block` + `mounting unchecked
  fs` warnings on nandblk1/nandblk5. `e2fsck -n /dev/block/app` said the FS was
  structurally fine: unclean-unmount flag, free-inode count off by 2,
  1004/1024 blocks used. A real `e2fsck -f` (needs `/app` unmounted) was
  never run.

---

## 4. Measured facts (keep for future comparisons)

### Partition hashes — good gun (sn22222) vs bad gun (sn11111)

Taken with `md5sum` on the block devices **on the gun** (read-only):

| device | good | bad | match |
|---|---|---|---|
| `/dev/nandblk` (81,144 blk) | `526cfab051fbe42a37d73261e0343b22` | `45e19b6c8bf53d234a99282336eacf84` | ✗ |
| `/dev/block/app` (nandblk5) | `833d649f76997df46f23ef41822cbd5c` | `06149879f0fbb417c0198017e3f0ab76` | ✗ |
| `/dev/block/userdata` (nandblk1) | `31ab7984b78648aa3519d81740efd85c` | `1112d3bd6208e98685333774a4b98f50` | ✗ |
| `/dev/block/private` (nandblk3) | `b6d81b360a5672d80c27430f39153e2c` | same | ✓ |
| `/dev/block/cache` (nandblk4) | `d20b434f55425481a1ad736188ff3f16` | `f683554521fd873f9c40cd8bc51c45c9` | ✗ |
| `/dev/mtdblock0` (misc) | `d3dd83fd976d18d0733dc5ba8f9c174c` | same | ✓ |
| `/dev/mtdblock1` (env) | `9ee1a8db54cb813edd4e87e38159ded3` | same | ✓ |

**Gap to be honest about:** the rootfs is `/dev/nandblk2` per the kernel cmdline
(`root=/dev/nandblk2 rootfstype=squashfs`), and **`nandblk2` was never
hashed**. The `nandblk` row above is a different device. If a rootfs comparison
is needed, hash `nandblk2` on both guns.

`/proc/partitions` (both guns, same layout):
```
nandblk 81144 · nandblk1 2278 · nandblk2 24576 · nandblk3 1024
nandblk4 49152 · nandblk5 4096 · mtdblock0/1 1024 each
```
Mounts: `/` = `/dev/root` squashfs ro; `/data` = nandblk1 ext4;
`/cache` = nandblk4 vfat; `/app` = nandblk5 ext4. Symlinks in `/dev/block`:
`app→nandblk5`, `cache→nandblk4`, `private→nandblk3`, `userdata→nandblk1`,
`env→mtdblock1`, `misc→mtdblock0`.

### App-layer binaries

| file | good gun (== GitHub `gaime_mods` reference) | bad gun |
|---|---|---|
| `/app/bin/gun` (72,312 B both) | `b073cf7dff7f9ccf29cd332777d19595` | `058bfedac046d01feeeb1c40250503d0` |
| `/app/bin/usb_monitor` (good 9,788 B / bad 14,024 B) | `9c6a498936301c1accb6309ca82ec843` | `af88b4aa14d84403c1d6cfc3f452312f` |
| `/app/model/gaime/gaime.ezb` (142,712 B) | `6f15285eae09e08ddd2a36c5452a09e2` | `959be1ec6cd6525a386f93464f4cd4f0` |

- The **good gun is byte-identical to the public reference** for all three, so
  the GitHub reference *is* valid known-good for this kit (settles the
  JP-vs-US firmware worry — at the app-layer binary level).
- The bad gun carries a **different, larger vendor build**. Its `usb_monitor`
  adds `gaime_log*` instrumentation, directory scanning (`opendir/readdir/
  sscanf/strftime/localtime`) and an explicit `reboot()` call.
  **Do not "repair" the bad gun by copying reference binaries — that would
  downgrade it.**

### Versions

- Good gun: `fw v2.5.1`, `model v1.0.1` (update dates not read from sysinfo).
- Bad gun `/app/system_info.json`: `fw v2.5.1` (`update_date 2025-10-11`),
  `model v1.0.1` (`2025-01-01`), `sn.num "11111"`, `main.status "enable"`.
- GitHub reference `app_fs/system_info.json`: same versions, `sn.num "12345"`.

---

## 5. Access / tooling notes (the gun console)

**Physical:** header **J3** on the gun mainboard, 4 unpopulated holes
silkscreened `GND / TX / RX / V3.3` (square pad on the reverse = pin 1).
Console is `ttySLB0` at **115200 8N1** (kernel cmdline).

**Wiring that worked:**
`adapter GND → J3 GND`, `adapter TXD → J3 RX`, `adapter RXD → J3 TX`,
**`V3.3` left unconnected** (gun is USB-powered; feeding the rail is not
needed and risks damage).

**Adapter:** CP2102. Windows had no driver at first (device status Error);
installed Silicon Labs VCP x64 (staged at
`C:\Users\Public\CP210xVCPInstaller_x64.exe`). Enumerates as
`Silicon Labs CP210x USB to UART Bridge (COM4)`.

**Console script:** `C:\Users\Public\j3_console.ps1` (pure PowerShell,
`System.IO.Ports.SerialPort`, no Python/usbipd). Actions:
```
probe                 open, listen 3 s, send newline, listen 3 s, report link
listen -Seconds N     raw read for N seconds
send  "cmd1" "cmd2"   run commands, print each reply (2.5 s window)
batch FILE [gapMs]    one command per line from a file (default gap 150 ms)
recv  FILE SECONDS    read raw for N s into FILE
capture "cmd" FILE S  send command then read its entire output into FILE
```
Every invocation opens and closes COM4 — nothing is left connected.

**Uploading files over this console (works, proven):** the tty mangles very
long single lines, so:
1. Base64 the file locally.
2. Split into **64-char chunks**.
3. On the gun: `rm -f /tmp/x.b64`, then one
   `printf '%s' '<chunk>' >> /tmp/x.b64` per chunk (via `batch`), then
   `base64 -d /tmp/x.b64 > <target>`.
4. **Always `md5sum` the result on the gun and compare with the local md5** —
   this is how both deployed files were verified.

**Extracting text from the gun (works):** run a command producing base64, then
`capture` it. Note the command echo lands in the captured file before the
payload — strip everything up to the **first newline** before base64-decoding,
and drop non-base64 chars. That exact procedure produced md5-verified copies.

---

## 6. Machine state at handoff

- Gun **unplugged** (user is disconnecting it); `COM4` is free and nothing is
  holding it.
- No `GaimeTray.exe` / `GaimeFeeder.exe` / `dotnet.exe` from this project
  running (the leftover `--xcheck` was killed earlier in the session).
- `C:\games\feeder` holds the deployed Release build from this session.
- `logs/gunref/` contents (all local, all md5-known):

```
gun_ref            72,312 B  b073cf7dff7f9ccf29cd332777d19595   GitHub ref bin/gun
usb_monitor_ref     9,788 B  9c6a498936301c1accb6309ca82ec843   GitHub ref usb_monitor
usb_monitor_bad    14,024 B  af88b4aa14d84403c1d6cfc3f452312f   pulled off bad gun
ezb_ref           142,712 B  6f15285eae09e08ddd2a36c5452a09e2   GitHub ref model
bb_ref            977,724 B  949feca2a68b1d4a912624d1959847ea   GitHub ref busybox
app_backup.tgz  1,305,588 B  4e9a51e57096300d997e113b9b2c0e81   good gun /app+init.d (bit-verified)
app_backup2.tgz 1,305,603 B  364dab9b3cb5532481076c9ae88c6080   superseded bad capture (do not use)
old_logs.tgz       71,609 B  72fd7ba9efe951b2ff345718e509d57e   three stale logs, bit-verified
old_logs/                    extracted copies of the above
app.sh.new          1,234 B  b314d89b7c8b6730ea5aa6a9b02ba5b0   patched launcher (= on gun)
logs_watchdog.sh    3,399 B  3c93094a909bc4af142ea3a953d80d0d   local copy (deployable is tools/gun/; rev 2)
tools/gun/app.sh    1,234 B  b314d89b7c8b6730ea5aa6a9b02ba5b0   patched launcher (already on gun)
```

`app_backup.tgz` is the **restore point** for the good gun's `/app` +
`/etc/init.d`; `app_backup2.tgz` was a partial/incorrect capture and should be
deleted.

---

## 7. Next steps, in order

1. ~~Re-upload the fixed `logs_watchdog.sh`~~ **DONE 2026-10-22** — rev 2
   (`3c93094a…`, 3,399 B) deployed and md5-verified on the gun, then verified by
   wiping `/tmp/logs` with the daemon running (all targets recreated, links
   resolve, a new heartbeat written to tmpfs). Still outstanding: one real
   `reboot` to confirm `app.sh` starts it early enough at boot.
2. ~~Slow the feeder hotplug poll~~ **DONE** — 2 s → 10 s via
   `HotplugRescanMs` in `src/GaimeFeeder/Program.cs` and
   `src/GaimeTray/FeederEngine.cs` (build 0/0; committed `83fafa4`; deployed).
   This is defence-in-depth now that logs live in tmpfs.
3. ~~Commit the repo~~ **DONE** — `83fafa4` (feeder + deployment) and
   `b3ce325` (gun watchdog + uploader). `logs/gunref/` is gitignored; the
   reference and deployable gun files are in `tools/gun/`.
4. Optional, still open: hash `/dev/nandblk2` on both guns to compare the
   actual rootfs; investigate the `ISP0 select timeout` reset loop; a one-time
   `e2fsck -f /dev/block/app` (needs `/app` unmounted) if space accounting
   drifts again.
5. Optional: full bit-exact (gzip'd) NAND backup of the good gun — sized
   ~160 MB raw / ~20-40 MB compressed / ~25-40 min at 115200. Not done.

---

## 8. Gotchas / do not repeat

- **Do not make the gun's log write fail.** When it cannot create its own log
  the app exits and the whole gun goes dark. Redirect the bytes (tmpfs) or make
  sure the write succeeds — never `chmod`/`chattr`/read-only the log path.
- **Create the symlink target before linking** (see §0). Dangling links are
  worse than a full disk: they fail instantly and silently.
- **`/app` is only 3.9 MB.** `du` of the file tree can look far smaller than
  `df` usage (metadata/drift); `e2fsck -n` is the read-only check.
- **A gun with a dead app layer is invisible to the feeder** — `HidIdentity`
  needs the MI_02 `sn` reply. "No guns found" can mean "app dead", not "not
  plugged in".
- **Replug/reboot do not fix a full `/app`** — the partition stays full.
- **Don't trust `in_range`** and don't gate on it (unchanged, already known).
- **`ps w` PID numbers change between boots** — always re-read, never assume a
  PID from an earlier note.
- Windows `dotnet` build from WSL works:
  `"/mnt/c/Program Files/dotnet/dotnet.exe" build '\\wsl.localhost\Ubuntu-24.04\home\USER\lightgun\src\<proj>\<proj>.csproj'`
- A running `GaimeFeeder.exe`/`GaimeTray.exe` locks its output DLL; stop it
  before rebuilding (a leftover `--xcheck` did exactly this).
