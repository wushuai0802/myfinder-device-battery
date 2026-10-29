$projectRoot = Split-Path -Parent $PSScriptRoot
$trayExe = Join-Path $projectRoot 'outputs\AtkMouseBattery.exe'
if (-not (Test-Path -LiteralPath $trayExe)) {
    Write-Host '未找到程序 outputs\AtkMouseBattery.exe，源码仓库不包含预编译程序。'
    Write-Host '请前往 https://github.com/wushuai0802/myfinder-device-battery/releases 下载完整发行包，'
    Write-Host '或在 PowerShell 中运行 scripts\Build.ps1 构建后再启动。'
    exit 1
}
# This is a GUI-subsystem tray application: no console is created. Normal lets
# the user-opened details panel remain visible instead of inheriting Hidden.
Start-Process -FilePath $trayExe -ArgumentList '--show' -WindowStyle Normal
