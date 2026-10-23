# Gun-side fixes (G'AIM'E light gun, sn11111)

These are the files that keep the gun's 3.9 MB `/app` partition from filling up
with heartbeat logs — the failure that killed the gun's whole application layer
(no aim, no buttons, no config replies). Full diagnosis: `HANDOFF.md`.

## Files

| file | md5 | purpose |
|---|---|---|
| `logs_watchdog.sh` | `e214ca10316b0f42780d6d917ff6ae07` | Re-points `/app/*.log` into `/tmp/logs` (tmpfs) and caps their total size at 5 MB. |
| `app.sh` | `b314d89b7c8b6730ea5aa6a9b02ba5b0` | Vendor's launcher plus one line: `/app/shell/logs_watchdog.sh &`. The vendor original is kept on the gun as `/app/shell/app.sh.vendor`. |

Both are byte-verified on the gun (md5 compared after upload).

## Why

- The `gun` daemon writes one `/app/<day>.log` per gun-day.
- 98.8% of that log is a heartbeat line (`Success to response box, func 0x07`)
  written **once per vendor `get_info` query** — ~85 bytes each.
- The feeder's hot-plug rescan polls every 2 s → ~43k queries/day → ~3.7 MB/day,
  which fills `/app` in about one day of use. The app then cannot create its log
  and exits; the gun becomes invisible to the host entirely.
- `logs_watchdog.sh` makes that structurally impossible: log bytes land in RAM
  (wiped each boot) and are trimmed if they exceed 5 MB.

## Deploy

With the gun powered and a root shell on `J3` (115200 8N1 — see `HANDOFF.md` §5):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools\Upload-GunFile.ps1 `
  -LocalPath tools\gun\logs_watchdog.sh `
  -RemotePath /app/shell/logs_watchdog.sh -Port COM4

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools\Upload-GunFile.ps1 `
  -LocalPath tools\gun\app.sh `
  -RemotePath /app/shell/app.sh -Port COM4
```

Then reboot over the console (`sync; reboot`) and verify on the fresh boot:

```
ls -la /app/*.log      # every entry -> /tmp/logs/<name>
ls -la /tmp/logs       # targets exist (not dangling)
ps w                   # gun and {logs_watchdog.s} both running
df -h /app             # well under 100%
```

## Restore

The bit-verified `/app` + `/etc/init.d` backup of the healthy gun is
`logs/gunref/app_backup.tgz` (md5 `4e9a51e57096300d997e113b9b2c0e81`), taken
from sn22222 **before** any change:

```sh
cd / && tar xzf app_backup.tgz
```

`app.sh.vendor` on the gun is the unmodified vendor launcher.

## Do not

- Do **not** make the log write fail (read-only/chattr). The app exits when it
  cannot log — that is the original outage.
- Do **not** copy binaries from the public `gaime_mods` reference over this
  gun: the units carry different vendor builds (see `HANDOFF.md` §4).
