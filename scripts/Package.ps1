param([string]$OutputDirectory,[string]$BuildDirectory,[switch]$SkipBuild)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root 'outputs\releases'}
if(-not $BuildDirectory){$BuildDirectory=Join-Path $root 'tmp\package-build'}
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'Build.ps1') -OutputDirectory $BuildDirectory}
$exe=Join-Path $BuildDirectory 'AtkMouseBattery.exe'
if(-not (Test-Path -LiteralPath $exe -PathType Leaf)){throw 'Build the application before packaging.'}
$versionInfo=(Get-Item -LiteralPath $exe).VersionInfo
$version='{0}.{1}.{2}' -f $versionInfo.FileMajorPart,$versionInfo.FileMinorPart,$versionInfo.FileBuildPart
if($version -eq '0.0.0'){throw 'Application version metadata is missing.'}
$name='myfinder-device-battery-v'+$version+'-windows-x64'
$stage=Join-Path $OutputDirectory $name
if(Test-Path -LiteralPath $stage){throw 'Package staging already exists; choose a new output directory.'}
New-Item -ItemType Directory -Path (Join-Path $stage 'outputs'),(Join-Path $stage 'scripts') -Force|Out-Null
foreach($file in @('AtkMouseBattery.exe','AtkBatteryProbe.exe')){Copy-Item -LiteralPath (Join-Path $BuildDirectory $file) -Destination (Join-Path $stage 'outputs')}
foreach($file in @('Install.Common.ps1','Install-Startup.ps1','Uninstall-Startup.ps1','Start.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $stage 'scripts')}
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $stage
Get-ChildItem -LiteralPath $root -Filter '*.bat' -File|Copy-Item -Destination $stage
if(Test-Path -LiteralPath (Join-Path $root 'docs\images')){New-Item -ItemType Directory -Path (Join-Path $stage 'docs')|Out-Null;Copy-Item -LiteralPath (Join-Path $root 'docs\images') -Destination (Join-Path $stage 'docs') -Recurse}
$zip=Join-Path $OutputDirectory ($name+'.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$sum=Join-Path $OutputDirectory 'SHA256SUMS.txt'
[IO.File]::WriteAllText($sum,((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($zip)+"`n"),(New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{Version=$version;Zip=$zip;Checksums=$sum;Stage=$stage}
