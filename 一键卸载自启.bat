@echo off
setlocal
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Uninstall-Startup.ps1" %*
if errorlevel 1 (
    echo 移除自启失败，请查看上面的错误信息。
    pause
    exit /b 1
)
echo 已移除所选的自启快捷方式，程序文件和设置均已保留。
pause
