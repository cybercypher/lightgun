@echo off
rem Single tray app: guns + log + capture, lives in the task tray.
rem Runtime is deployed to C:\games\feeder (see tools\Deploy-Feeder.ps1).
rem Uses GaimeTray.exe (windowed, no console). Do NOT run via dotnet.exe (console window).
start "GaimeTray" "C:\games\feeder\GaimeTray.exe"
