<#
.SYNOPSIS
Exports production-drawn 1.3.5 promo assets using in-memory demo data only.
.EXAMPLE
.\tests\RenderPromoAssets.ps1 -OutputDirectory (Join-Path $env:TEMP 'myfinder-promo-135')
.NOTES
Use a new, empty directory below TEMP. No application, hardware readers, settings,
network requests, clipboard operations or visible windows are started.
Scale applies to panels/dialogs/taskbar; tray PNGs retain their named pixel sizes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$OutputDirectory,
    [ValidateRange(1,6)][int]$Scale=3
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$promoOutput=[IO.Path]::GetFullPath($OutputDirectory)
$temporaryRoot=[IO.Path]::GetFullPath($env:TEMP)
# The Framework entry point validates paths before creating output. Its path
# normalization expands 8.3 aliases consistently for both TEMP and caller paths.
# Build files are independently confined to a new directory under TEMP.
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not (Test-Path -LiteralPath $compiler)){throw 'Windows .NET Framework compiler is unavailable.'}
$release=Get-Content -LiteralPath (Join-Path $root 'release.json') -Raw | ConvertFrom-Json
if($release.version -ne '1.3.5'){throw 'This exporter requires the 1.3.5 production source tree.'}
$sourceCommit=(& git -C $root rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[a-f0-9]{40}$'){throw 'Cannot identify the source commit.'}
& git -C $root diff --quiet HEAD -- scripts release.json
if($LASTEXITCODE -ne 0){throw 'Production source differs from HEAD; source-commit provenance would be inaccurate.'}
$untrackedSources=@(& git -C $root ls-files --others --exclude-standard -- 'scripts/*.cs')
if($LASTEXITCODE -ne 0 -or $untrackedSources.Count){throw 'Untracked production C# sources would invalidate provenance.'}
$compileDirectory=Join-Path $temporaryRoot ('myfinder-render-promo-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $compileDirectory | Out-Null
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -Filter '*.cs' -File |
    Where-Object {$_.Name -notin @('Probe.cs','Tests.cs','AssemblyInfo.cs')} | Sort-Object Name | ForEach-Object {$_.FullName})
$metadata=Join-Path $compileDirectory 'AssemblyInfo.promo.cs'
[IO.File]::WriteAllText($metadata,'[assembly:System.Reflection.AssemblyInformationalVersion("1.3.5")]',(New-Object Text.UTF8Encoding($false)))
$sources+=$metadata
$executable=Join-Path $compileDirectory 'RenderPromoAssets.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:RenderPromoAssets /reference:System.Runtime.Serialization.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$executable" $sources (Join-Path $PSScriptRoot 'RenderPromoAssets.cs')
if($LASTEXITCODE -ne 0){throw "Promo compilation failed. Build files: $compileDirectory"}
& $executable $promoOutput $Scale $sourceCommit $root
if($LASTEXITCODE -ne 0){throw "Promo rendering failed; output may be incomplete. Build files: $compileDirectory"}
Write-Host "Build files (TEMP): $compileDirectory"
