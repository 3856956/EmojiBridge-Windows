@echo off
cd /d "%~dp0"
if not exist "EmojiBridge.exe" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if not exist "EmojiBridge.exe" (
  echo Build failed. Please check build.ps1.
  pause
  exit /b 1
)
start "" "%~dp0EmojiBridge.exe"
