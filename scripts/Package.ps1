param([string]$OutputDirectory,[string]$BuildDirectory,[switch]$SkipBuild,[string]$Prerelease='beta.1')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Install.Common.ps1')
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root 'outputs\releases'}
if(-not $BuildDirectory){$BuildDirectory=Join-Path $root 'tmp\package-build'}
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'Build.ps1') -OutputDirectory $BuildDirectory}
$exe=Join-Path $BuildDirectory 'AtkMouseBattery.exe'
if(-not (Test-Path -LiteralPath $exe -PathType Leaf)){throw 'Build the application before packaging.'}
$versionInfo=(Get-Item -LiteralPath $exe).VersionInfo
$version='{0}.{1}.{2}' -f $versionInfo.FileMajorPart,$versionInfo.FileMinorPart,$versionInfo.FileBuildPart
if($Prerelease){if($Prerelease -notmatch '^[a-zA-Z0-9.-]+$'){throw 'Invalid release suffix.'};$version+='-'+$Prerelease}
if($version -eq '0.0.0'){throw 'Application version metadata is missing.'}
$name='myfinder-device-battery-v'+$version+'-windows-x64'
$stage=Join-Path $OutputDirectory $name
if(Test-Path -LiteralPath $stage){throw 'Package staging already exists; choose a new output directory.'}
New-Item -ItemType Directory -Path (Join-Path $stage 'outputs'),(Join-Path $stage 'scripts') -Force|Out-Null
foreach($file in @('AtkMouseBattery.exe','AtkBatteryProbe.exe')){Copy-Item -LiteralPath (Join-Path $BuildDirectory $file) -Destination (Join-Path $stage 'outputs')}
foreach($file in @('Install.Common.ps1','Install-Startup.ps1','Uninstall-Startup.ps1','Start.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $stage 'scripts')}
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $stage
$runtime=Join-Path $root 'engine\runtime'
if(-not (Test-Path -LiteralPath (Join-Path $runtime 'python.exe'))){& (Join-Path $PSScriptRoot 'Build-Backend.ps1') -OutputDirectory $runtime}
$engine=Join-Path $root 'engine'
foreach($file in (Get-DeviceBatteryEngineFiles $engine)) {
    $relative=$file.FullName.Substring($root.Length+1)
    $destination=Join-Path $stage $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
Get-ChildItem -LiteralPath $root -Filter '*.bat' -File|Copy-Item -Destination $stage
if(Test-Path -LiteralPath (Join-Path $root 'docs\images')){New-Item -ItemType Directory -Path (Join-Path $stage 'docs')|Out-Null;Copy-Item -LiteralPath (Join-Path $root 'docs\images') -Destination (Join-Path $stage 'docs') -Recurse}
if(Test-Path -LiteralPath (Join-Path $root 'docs\compatibility.md')){Copy-Item -LiteralPath (Join-Path $root 'docs\compatibility.md') -Destination (Join-Path $stage 'docs\compatibility.md')}
$zip=Join-Path $OutputDirectory ($name+'.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$sum=Join-Path $OutputDirectory 'SHA256SUMS.txt'
[IO.File]::WriteAllText($sum,((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($zip)+"`n"),(New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{Version=$version;Zip=$zip;Checksums=$sum;Stage=$stage}
