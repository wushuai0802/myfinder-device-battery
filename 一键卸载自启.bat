@echo off
chcp 65001 >nul
set "SHORTCUT=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\MyFinder 设备电量.lnk"
set "MANUAL=%APPDATA%\Microsoft\Windows\Start Menu\Programs\MyFinder 设备电量.lnk"
if exist "%SHORTCUT%" del /f /q "%SHORTCUT%" >nul 2>&1
if exist "%MANUAL%" del /f /q "%MANUAL%" >nul 2>&1
echo.
echo [完成] 已成功移除开机自启与开始菜单快捷方式。
echo.
pause