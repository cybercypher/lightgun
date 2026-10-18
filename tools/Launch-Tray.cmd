@echo off
rem Single tray app: guns + log + XInput + capture, lives in the task tray.
rem Uses GaimeTray.exe (windowed, no console). Do NOT run via dotnet.exe (console window).
start "GaimeTray" "\\wsl.localhost\Ubuntu-24.04\home\USER\lightgun\src\GaimeTray\bin\Debug\net8.0-windows\GaimeTray.exe"
