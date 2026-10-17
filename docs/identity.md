# Gun identity (solved 2026-10-17, verified on hardware)

## Problem
Two identical G'AIM'E guns (VID 2E2C / PID 0631) must map stably to J1/J2.
USB serial, container GUID, and HID path suffixes all **randomize per boot**
(7 distinct IDs observed from 2 guns, incl. across one same-port replug).
Descriptors are otherwise identical. No tracking by USB identity is possible.

## Solution: on-device serial number
The gun runs Linux; `/app/system_info.json` holds `sn: {"num":"12345"}`
(factory placeholder, identical both guns). The vendor HID protocol
(reconstructed from decompiled `/app/bin/gun`, gaime_mods) supports
`func 6 set_info` with JSON payload `{"path":"sn.num","value":"<sn>"}` —
a vendor-designed key write (same path their service tooling uses).

- Gun 1 (labeled): `sn` = `11111`. Gun 2: `sn` = `22222`.
- Verified: write → read-back → unplug/replug → value persists (ext4 `/app`).
- Untouched: `fw.ver`/`model.ver` re-read after every write.
- Read-only ops used: func 5/idx2 (version), func 7 (get), func 4 (status).
  NEVER sent: func 1/2/3 (OTA state machine), func 5 idx 0/1 (reboot/reset).
- Rollback: write `12345` back. Caveat: the MD5 accessory handshake may cover
  `system_info.json` — a changed `sn` could fail pairing with the official
  mini-console (irrelevant for PC play; reversible).

## Dead ends (all tested, not assumed)
- USB serial/container/HID suffix stability — disproven by same-port replug test.
- Vendor `get_info` keys (`sn`/`fw.ver`/`model.ver` only; `sn` identical pre-write).
- Windows exclusive-open seize of the digitizer — sharing violation (err 32).

## Feeder integration
`src/GaimeFeeder/HidIdentity.cs`: groups HID interfaces by USB composite,
queries `sn` per composite over the vendor interface (overlapped HID I/O),
routes all reports by sn. `guns.json` and `calibrations/cal_<sn>.json` keyed by sn.
