param([string]$OutputDirectory,[string]$BuildDirectory,[switch]$SkipBuild,[string]$Prerelease)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Install.Common.ps1')
$release=Read-DeviceBatteryRelease $root
if($Prerelease -and $release.version -cne ($release.version.Split('-')[0]+'-'+$Prerelease)){throw 'Release suffix must match the single release.json version source.'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root 'outputs\releases'}
if(-not $BuildDirectory){$BuildDirectory=Join-Path $root 'tmp\package-build'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory);$BuildDirectory=[IO.Path]::GetFullPath($BuildDirectory)
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'Build.ps1') -OutputDirectory $BuildDirectory}
$exe=Join-Path $BuildDirectory 'AtkMouseBattery.exe'
$buildInfoPath=Join-Path $BuildDirectory 'build-info.json'
if(-not (Test-Path -LiteralPath $buildInfoPath -PathType Leaf)){throw 'Build with Build.ps1 and use its complete validated build directory.'}
$info=Get-Content -LiteralPath $buildInfoPath -Raw -Encoding UTF8 | ConvertFrom-Json
if($info.schema -ne 1 -or $info.version -cne $release.version -or
    $info.releaseSha256 -ine (Get-FileHash -LiteralPath (Join-Path $root 'release.json') -Algorithm SHA256).Hash) {throw 'Build metadata does not match this release.'}
foreach($entry in @($info.files)) {
    $file=Resolve-DeviceBatteryPayloadPath $BuildDirectory $entry.path
    if(-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $entry.sha256){throw 'Built binaries changed after validation.'}
}
foreach($entry in @($info.sources)) {
    $file=Resolve-DeviceBatteryPayloadPath $root $entry.path
    if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $entry.sha256){throw 'Source changed after the validated build. Build again before packaging.'}
}
if((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -cne $release.version){throw 'Binary informational version does not match release.json.'}
$name='myfinder-device-battery-v'+$release.version+'-windows-x64'
$stage=Join-Path $OutputDirectory $name;$zip=Join-Path $OutputDirectory ($name+'.zip')
if((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $zip)){throw 'Package output already exists; choose a new output directory.'}
$runtime=Join-Path $root 'engine\runtime'
$null=& (Join-Path $PSScriptRoot 'Build-Backend.ps1') -OutputDirectory $runtime
New-Item -ItemType Directory -Path (Join-Path $stage 'outputs'),(Join-Path $stage 'scripts') -Force|Out-Null
foreach($file in @('AtkMouseBattery.exe','AtkBatteryProbe.exe')){Copy-Item -LiteralPath (Join-Path $BuildDirectory $file) -Destination (Join-Path $stage 'outputs')}
foreach($file in @('Install.Common.ps1','Manifest.Common.ps1','Install-Startup.ps1','Uninstall-Startup.ps1','Start.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $stage 'scripts')}
foreach($file in @('README.md','CHANGELOG.md','THIRD_PARTY_NOTICES.md','LICENSE','release.json')){Copy-Item -LiteralPath (Join-Path $root $file) -Destination $stage}
Copy-Item -LiteralPath $buildInfoPath -Destination (Join-Path $stage 'build-info.json')
foreach($file in (Get-DeviceBatteryEngineFiles (Join-Path $root 'engine'))) {
    $relative=$file.FullName.Substring($root.Length+1).Replace('\','/')
    $destination=Resolve-DeviceBatteryPayloadPath $stage $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
Get-ChildItem -LiteralPath $root -Filter '*.bat' -File | Copy-Item -Destination $stage
if(Test-Path -LiteralPath (Join-Path $root 'docs\images')){
    New-Item -ItemType Directory -Path (Join-Path $stage 'docs')|Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'docs\images') -Destination (Join-Path $stage 'docs') -Recurse
}
foreach($document in @('compatibility.md','verification.md')) {
    if(Test-Path -LiteralPath (Join-Path $root ('docs\'+$document))){
        New-Item -ItemType Directory -Path (Join-Path $stage 'docs') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root ('docs\'+$document)) -Destination (Join-Path $stage ('docs\'+$document))
    }
}
$manifest=New-DeviceBatteryPackageManifest $stage
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
$sum=Join-Path $OutputDirectory 'SHA256SUMS.txt'
[IO.File]::WriteAllText($sum,((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($zip)+[Environment]::NewLine),(New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{Version=$release.version;Zip=$zip;Checksums=$sum;Stage=$stage;Manifest=(Join-Path $stage 'package-manifest.json');Files=@($manifest.files).Count}
