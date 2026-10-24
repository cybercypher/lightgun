# Build + deploy the G'AIM'E feeder runtime to C:\games\feeder.
# Publishes GaimeTray (Release, framework-dependent) into the
# install folder and copies data (guns.json, calibrations\*.json) alongside.
# logs\ in the install folder is left untouched.
$ErrorActionPreference = "Stop"
$repo = "\\wsl.localhost\Ubuntu-24.04\home\USER\lightgun"
$dest = "C:\games\feeder"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"

New-Item -ItemType Directory -Force -Path $dest, "$dest\calibrations", "$dest\logs" | Out-Null
foreach ($p in "GaimeTray") {
    & $dotnet publish "$repo\src\$p\$p.csproj" -c Release -o $dest
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $p" }
}
# ViGEm output and the GaimeFeeder console app were removed; delete their
# leftovers from older deploys.
foreach ($f in "Nefarius.ViGEm.Client.dll", "XCheck.ps1", "output.txt",
               "GaimeFeeder.exe", "GaimeFeeder.dll", "GaimeFeeder.pdb",
               "GaimeFeeder.deps.json", "GaimeFeeder.runtimeconfig.json",
               "Feeder.ps1", "Launch-Feeder.cmd", "Launch-Guns.cmd") {
    Remove-Item -LiteralPath (Join-Path $dest $f) -ErrorAction SilentlyContinue
}
Copy-Item "$repo\guns.json" $dest -Force
Copy-Item "$repo\calibrations\*.json" "$dest\calibrations\" -Force
Write-Host "Deployed to $dest"
