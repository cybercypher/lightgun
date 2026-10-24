@echo off
rem Double-click: console feeder, logged to C:\games\feeder\logs\
rem (alternative to the tray app; runtime is deployed to C:\games\feeder)
start "GaimeFeeder" powershell -NoExit -ExecutionPolicy Bypass -File "C:\games\feeder\Feeder.ps1"
