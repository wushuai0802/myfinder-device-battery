$projectRoot = Split-Path -Parent $PSScriptRoot
$trayExe = Join-Path $projectRoot 'outputs\AtkMouseBattery.exe'
if (-not (Test-Path -LiteralPath $trayExe)) {
    Write-Host '未找到程序 outputs\AtkMouseBattery.exe，源码仓库不包含预编译程序。'
    Write-Host '请前往 https://github.com/wushuai0802/myfinder-device-battery/releases 下载完整发行包，'
    Write-Host '或在源码目录的 PowerShell 中依次执行以下两步：'
    Write-Host '1. 不带参数运行 scripts\Build.ps1（默认输出到 outputs\）。'
    Write-Host '2. 运行 scripts\Build-Backend.ps1 -OutputDirectory .\engine\runtime，准备通用设备读取组件。'
    exit 1
}
# This is a GUI-subsystem tray application: no console is created. Normal lets
# the user-opened details panel remain visible instead of inheriting Hidden.
Start-Process -FilePath $trayExe -ArgumentList '--show' -WindowStyle Normal
