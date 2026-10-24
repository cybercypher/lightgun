# GAIME box firmware — `GAIMEENC` container and decrypt

Date: 2026-10-23
Status: container verified; full image decryption in progress.
Source: `C:\games\feeder\GAIME_FWUpdate_V4.0.3.zip` (3.7 GB).

## Package

Phoenix-family Windows updater. Firmware payloads:

| SKU | File | Size |
|---|---|---|
| basic | `9c4f1e3b.dat` | 1,192,169,053 B |
| premium | `d8a21f90.dat` | 1,287,294,997 B |
| ultimate | `a4d7e9f0.dat` | 1,287,294,997 B |

The updater's `FirmwareDecryptor` maps SKU → `.dat`; the strings worker found the
same map in IL at `0x935a1d`.

## Container format (verified)

```
offset 0   8   "GAIMEENC"
offset 8   1   version = 1
offset 9   4   chunk size, u32 LE = 0x00400000 (4 MiB)
offset 13 12   base nonce
offset 25      records, repeated:
                 u32 LE plaintextLen
                 ciphertext[plaintextLen]
                 16-byte GCM tag
```

Cipher: **AES-256-GCM**, empty AAD, 16-byte tag.

Key (32 bytes, hard-coded in `GAIMEUpdater.dll` as a 64-char hex string):

```
2C4B5B4D84F9A5E0E4D4C2F78B6A1D9E0A0B1C2D3E4F5061728394A5B6C7D8E9
```

Per-record nonce:

```
n = base_nonce[:8]
n += little_endian_u32( base_nonce[8:12] XOR record_index )
```

`record_index` is 0-based. Chunks 0–3 of `9c4f1e3b.dat` were independently
decrypted and GCM-authenticated with this formula; plaintext begins
`IMAGEWTY`.

## Plaintext

Allwinner `IMAGEWTY` image. Header at offset 0:

- `0x00` `IMAGEWTY`
- `0x08` `0x00000300`
- `0x0c` `0x00000060` (header size 96)
- `0x10` `0x04d00000` (80,740,352)
- `0x14` `0x00100234`
- `0x18` `0x470ef800` (~1,192,163,328 — image size)
- `0x20` `0x00000400` (partition table offset)
- `0x3c` `0x00000031` (49 partition entries)
- `0x40` `0x00000400` (entry size)

Partition table starts at `0x400`, 49 entries of `0x400` bytes. Names seen:
`SYS_CONFIG`, `BOARD_CONFIG_BIN`, `BOOT0_NAND`, `UBOOT`, `BOOTPKG`, `BOOT`,
`VENDOR_BOOT`, `INIT_BOOT`, `SUPER`, `MISC`, `VBMETA`, `DTBO`, `MEDIA_DATA`,
`UDISK`, and others. `SUPER` is entry 37 at table offset `0x9800`; its entry
contains a size-like u64 `0x14c38c00` (~348 MB).

No calibration app or `setGunMode` code exists in the updater. The updater only
decrypts the Phoenix image and hands it to `PhoenixConsole.exe -e 1 -o 1 -p
<img>`. Transport: Allwinner USB VID `1F3A`, PID `EFE8` (FES/bootloader),
`4EE1` (success), `1000` (normal). The calibration logic must be in the Android
`super`/system image.

## Evidence

- REA worker: `GAIMEUpdater.dll` extracted from the .NET 8 single-file bundle;
  CIL for `FirmwareDecryptor::DecryptToTempImg` (RVA 0x2648) shows
  `AesGcm(Key)`, no AAD, and `nonce[8..12] ^= counter`.
- Independent Python check: AES-GCM authentication succeeded for records 0–3 of
  `9c4f1e3b.dat`; plaintext chunk 0 = `IMAGEWTY`.
- SKU map and partition names from the updater IL and decrypted chunk 0.

## Next

1. Finish decrypting `basic` and `ultimate` to `.imgy` and verify every tag.
2. Extract `super.fex` using the partition table; parse Android LP metadata or
   the filesystem inside it.
3. Find the box app / launcher and its calibration UI; trace the USB/HID calls
   to the gun (`setGunMode`, `sendBufferToDevice`, `05 21 06`, 8-point frames).
