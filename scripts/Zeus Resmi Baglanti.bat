@echo off
setlocal
set "SCRIPT=%~dp0Prepare-Official.ps1"
if not exist "%SCRIPT%" set "SCRIPT=%~dp0scripts\Prepare-Official.ps1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Start -OpenSslFrontend
if errorlevel 1 pause
