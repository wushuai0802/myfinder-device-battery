param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildOutput = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $projectRoot 'outputs' }
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework compiler is unavailable.' }
New-Item -ItemType Directory -Path $buildOutput -Force | Out-Null
$hidSource = Join-Path $PSScriptRoot 'HidBattery.cs'
$headsetSource = Join-Path $PSScriptRoot 'HeadsetBattery.cs'
$audioSource = Join-Path $PSScriptRoot 'AudioControl.cs'
$visualSource = Join-Path $PSScriptRoot 'VisualDesign.cs'
$traySource = Join-Path $PSScriptRoot 'TrayGlyph.cs'
& $compiler /nologo /target:exe /platform:x64 /optimize+ "/out:$(Join-Path $buildOutput 'AtkBatteryProbe.exe')" $hidSource (Join-Path $PSScriptRoot 'Probe.cs') (Join-Path $PSScriptRoot 'Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$(Join-Path $buildOutput 'AtkMouseBattery.exe')" $hidSource $headsetSource $audioSource $visualSource $traySource (Join-Path $PSScriptRoot 'TrayApp.cs')
if ($LASTEXITCODE -ne 0) { throw 'Tray build failed.' }
& (Join-Path $buildOutput 'AtkBatteryProbe.exe') --self-test
if ($LASTEXITCODE -ne 0) { throw 'Offline tests failed.' }
& (Join-Path $buildOutput 'AtkMouseBattery.exe') --preview $buildOutput
