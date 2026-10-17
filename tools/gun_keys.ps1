. "$(Split-Path -Parent $MyInvocation.MyCommand.Path)/gun_q_lib.ps1"
$paths = [Q]::VendorPaths()
foreach ($p in $paths) {
  Write-Output ("=== " + $p)
  foreach ($k in @("sn", "version", "fw.ver", "model.ver", "mac", "uuid", "device_id", "board", "hw_ver", "sw_ver", "cfg_ver")) {
    Write-Output ("get " + $k + " = [" + [Q]::Query($p, 7, 0, $k) + "]")
  }
}
