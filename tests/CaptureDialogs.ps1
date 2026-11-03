[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$OutputDirectory,
    [switch]$ChecksOnly
)
$ErrorActionPreference='Stop'
try {
    $root=Split-Path -Parent $PSScriptRoot
    $output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\','/')
    $temporaryRoot=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\','/')
    if(-not $output.StartsWith($temporaryRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Capture output and compilation must be under TEMP.'
    }
    $rootPath=[IO.Path]::GetFullPath($root).TrimEnd('\','/')
    if($output -eq $rootPath -or $output.StartsWith($rootPath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Capture output must be outside the worktree.'
    }
    # No real tray app, settings, updater, clipboard, browser or feedback transport.
    $build=Join-Path $output ('build-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $build -Force | Out-Null
    $sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -Filter '*.cs' -File |
        Where-Object {$_.Name -notin @('Probe.cs','Tests.cs','AssemblyInfo.cs')} |
        Sort-Object Name | ForEach-Object {$_.FullName})
    . (Join-Path $root 'scripts\Manifest.Common.ps1')
    $release=Read-DeviceBatteryRelease $root
    $metadata=Join-Path $build 'AssemblyInfo.capture.cs'
    [IO.File]::WriteAllText($metadata,('[assembly:System.Reflection.AssemblyInformationalVersion("'+$release.version+'")]'),(New-Object Text.UTF8Encoding($false)))
    $sources+=$metadata
    $executable=Join-Path $build 'CaptureDialogs.exe'
    $compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /main:CaptureDialogs /reference:System.Runtime.Serialization.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$executable" $sources (Join-Path $PSScriptRoot 'CaptureDialogs.cs')
    if($LASTEXITCODE -ne 0){throw 'Capture harness compilation failed.'}
    # The console host catches all UI/background exceptions and has a watchdog.
    # It shows only fixture dialogs and exits nonzero if the desktop is inaccessible.
    if($ChecksOnly){& $executable --checks-only} else {& $executable $output}
    if($LASTEXITCODE -ne 0){throw ('Capture/checks failed with exit code '+$LASTEXITCODE+'. See stderr and retained PNGs.')}
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    exit 1
}
