. "$(Split-Path -Parent $MyInvocation.MyCommand.Path)/gun_q_lib.ps1"
foreach ($p in [Q]::VendorPaths()) {
  Write-Output ("=== " + $p)
  Write-Output ("sn = [" + [Q]::Query($p, 7, 0, "sn") + "]")
}
