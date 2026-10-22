# Build + deploy the G'AIM'E feeder runtime to C:\games\feeder.
# Publishes GaimeTray + GaimeFeeder (Release, framework-dependent) into the
# install folder and copies data (guns.json, calibrations\*.json) alongside.
# logs\ in the install folder is left untouched.
$ErrorActionPreference = "Stop"
$repo = "\\wsl.localhost\Ubuntu-24.04\home\USER\lightgun"
$dest = "C:\games\feeder"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"

New-Item -ItemType Directory -Force -Path $dest, "$dest\calibrations", "$dest\logs" | Out-Null
foreach ($p in "GaimeTray", "GaimeFeeder") {
    & $dotnet publish "$repo\src\$p\$p.csproj" -c Release -o $dest
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $p" }
}
Copy-Item "$repo\guns.json" $dest -Force
Copy-Item "$repo\calibrations\*.json" "$dest\calibrations\" -Force
Write-Host "Deployed to $dest"
