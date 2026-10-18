[CmdletBinding()]
param([string]$OutputDirectory,[switch]$Animate,[switch]$BuildOnly)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root 'tmp\performance-tests'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
. (Join-Path $root 'scripts\Manifest.Common.ps1')
$release=Read-DeviceBatteryRelease $root
$metadata=Join-Path $OutputDirectory 'AssemblyInfo.performance.cs'
[IO.File]::WriteAllText($metadata,('[assembly:System.Reflection.AssemblyInformationalVersion("'+$release.version+'")]'),(New-Object Text.UTF8Encoding($false)))
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -Filter '*.cs' -File | Where-Object {$_.Name -notin @('Probe.cs','Tests.cs','AssemblyInfo.cs')} | Sort-Object Name | ForEach-Object {$_.FullName})
$sources+=$metadata
$binary=Join-Path $OutputDirectory 'InteractionPerformanceTests.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:InteractionPerformanceTests /reference:System.Runtime.Serialization.dll `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll "/out:$binary" $sources (Join-Path $PSScriptRoot 'InteractionPerformanceTests.cs')
if($LASTEXITCODE -ne 0){throw 'Interaction performance fixture build failed.'}
if(-not $BuildOnly){
    $arguments=@($OutputDirectory)
    if($Animate){$arguments+='--animate'}
    & $binary $arguments
    if($LASTEXITCODE -ne 0){throw 'Interaction lifecycle regression failed. Performance figures are informational.'}
}
