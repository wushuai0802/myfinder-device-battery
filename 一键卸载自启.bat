@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Uninstall-Startup.ps1" %*
if errorlevel 1 (
    echo Autostart removal failed. See the error above.
    pause
    exit /b 1
)
echo Requested startup shortcuts removed. Program files and audio settings were preserved.
pause
