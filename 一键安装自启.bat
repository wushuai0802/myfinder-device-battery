@echo off
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Install-Startup.ps1"
if %errorlevel% equ 0 (
    echo.
    echo [成功] 已成功将“设备电量卡片”添加到当前用户开机自启与开始菜单！
    echo.
) else (
    echo.
    echo [失败] 添加自启失败，请检查输出提示。
    echo.
)
pause