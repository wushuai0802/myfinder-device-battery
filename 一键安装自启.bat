@echo off
setlocal
if exist "%~dp0outputs\AtkMouseBattery.exe" goto install_program
chcp 65001 >nul
echo 未找到程序 outputs\AtkMouseBattery.exe，源码仓库不包含预编译程序。
echo 请前往 https://github.com/wushuai0802/myfinder-device-battery/releases 下载完整发行包，
echo 或在 PowerShell 中运行 scripts\Build.ps1 构建后再安装。
pause
exit /b 1
:install_program
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Install-Startup.ps1" %*
if errorlevel 1 (
    echo Installation failed. See the error above.
    pause
    exit /b 1
)
echo Installed to a stable directory. Start Device Battery from the Start menu.
pause
