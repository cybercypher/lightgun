@echo off
rem Single tray app: guns + log + XInput + capture, lives in the task tray.
start "GaimeTray" "C:\Program Files\dotnet\dotnet.exe" "\\wsl.localhost\Ubuntu-24.04\home\USER\lightgun\src\GaimeTray\bin\Debug\net8.0-windows\GaimeTray.dll"
