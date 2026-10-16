$projectRoot = Split-Path -Parent $PSScriptRoot
$trayExe = Join-Path $projectRoot 'outputs\AtkMouseBattery.exe'
if (-not (Test-Path -LiteralPath $trayExe)) { throw 'Build the tray app first.' }
# This is a GUI-subsystem tray application: no console is created. Normal lets
# the user-opened details panel remain visible instead of inheriting Hidden.
Start-Process -FilePath $trayExe -ArgumentList '--show' -WindowStyle Normal
