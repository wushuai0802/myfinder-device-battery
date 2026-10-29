@echo off
if exist "%~dp0outputs\AtkMouseBattery.exe" goto start_program
chcp 65001 >nul
echo 未找到程序 outputs\AtkMouseBattery.exe，源码仓库不包含预编译程序。
echo 请前往 https://github.com/wushuai0802/myfinder-device-battery/releases 下载完整发行包，
echo 或在 PowerShell 中运行 scripts\Build.ps1 构建后再启动。
pause
exit /b 1
:start_program
start "" "%~dp0outputs\AtkMouseBattery.exe" --show
