@echo off
rem Double-click: opens feeder + XInput viewer, both logged to logs\
start "GaimeFeeder" powershell -NoExit -ExecutionPolicy Bypass -File "\\wsl.localhost\Ubuntu-24.04\home\USER\lightgun\tools\Feeder.ps1"
start "XCheck" powershell -NoExit -ExecutionPolicy Bypass -File "\\wsl.localhost\Ubuntu-24.04\home\USER\lightgun\tools\XCheck.ps1"
