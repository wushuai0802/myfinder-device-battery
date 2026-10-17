param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Manifest.Common.ps1')
$release=Read-DeviceBatteryRelease $projectRoot
$buildOutput = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $projectRoot 'outputs' }
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework compiler is unavailable.' }
New-Item -ItemType Directory -Path $buildOutput -Force | Out-Null
$hidSource = Join-Path $PSScriptRoot 'HidBattery.cs'
$headsetSource = Join-Path $PSScriptRoot 'HeadsetBattery.cs'
$audioSource = Join-Path $PSScriptRoot 'AudioControl.cs'
$visualSource = Join-Path $PSScriptRoot 'VisualDesign.cs'
$traySource = Join-Path $PSScriptRoot 'TrayGlyph.cs'
$settingsSource = Join-Path $PSScriptRoot 'DeviceSettings.cs'
& $compiler /nologo /target:exe /platform:x64 /optimize+ "/out:$(Join-Path $buildOutput 'AtkBatteryProbe.exe')" $hidSource (Join-Path $PSScriptRoot 'Probe.cs') (Join-Path $PSScriptRoot 'Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
$assembly=Join-Path $buildOutput 'AssemblyInfo.generated.cs'
$metadata=@'
using System.Reflection;
[assembly: AssemblyTitle("MyFinder Device Battery")]
[assembly: AssemblyProduct("MyFinder Device Battery")]
[assembly: AssemblyDescription("Local mouse, headset and keyboard battery cards for Windows")]
[assembly: AssemblyVersion("{ASSEMBLY}")]
[assembly: AssemblyFileVersion("{ASSEMBLY}")]
[assembly: AssemblyInformationalVersion("{VERSION}")]
'@
[IO.File]::WriteAllText($assembly,$metadata.Replace('{ASSEMBLY}',$release.assemblyVersion).Replace('{VERSION}',$release.version),(New-Object Text.UTF8Encoding($false)))
$uiSources=@(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | Where-Object {$_.Name -notin @('Probe.cs','Tests.cs','AssemblyInfo.cs')} | Sort-Object Name | ForEach-Object {$_.FullName})
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll /reference:System.Runtime.Serialization.dll "/out:$(Join-Path $buildOutput 'AtkMouseBattery.exe')" $uiSources $assembly
if ($LASTEXITCODE -ne 0) { throw 'Tray build failed.' }
& (Join-Path $buildOutput 'AtkBatteryProbe.exe') --self-test
if ($LASTEXITCODE -ne 0) { throw 'Offline tests failed.' }
& (Join-Path $buildOutput 'AtkMouseBattery.exe') --preview $buildOutput
if ($LASTEXITCODE -ne 0) { throw 'Appearance preview failed.' }
& (Join-Path $projectRoot 'tests\Run.ps1') -OutputDirectory (Join-Path $buildOutput 'tests')
if(-not $?){throw 'Regression checks failed.'}
$buildInfo=[ordered]@{schema=1;version=$release.version;releaseSha256=(Get-FileHash -LiteralPath (Join-Path $projectRoot 'release.json') -Algorithm SHA256).Hash.ToLowerInvariant();
    files=@('AtkMouseBattery.exe','AtkBatteryProbe.exe' | ForEach-Object {[pscustomobject]@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $buildOutput $_) -Algorithm SHA256).Hash.ToLowerInvariant()}});
    sources=@(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | Sort-Object Name | ForEach-Object {[pscustomobject]@{path=('scripts/'+$_.Name);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})}
[IO.File]::WriteAllText((Join-Path $buildOutput 'build-info.json'),($buildInfo | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
