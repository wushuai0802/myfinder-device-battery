[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$OutputDirectory,[switch]$InteractionsOnly,[switch]$DialogsOnly)
$ErrorActionPreference='Stop'
if($InteractionsOnly -and $DialogsOnly){throw 'InteractionsOnly and DialogsOnly cannot be combined.'}
$root=Split-Path -Parent $PSScriptRoot
$previewOutput=[IO.Path]::GetFullPath($OutputDirectory)
$temporaryRoot=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\','/')
if(-not $previewOutput.StartsWith($temporaryRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Preview images must be written under TEMP.'
}
$rootPath=[IO.Path]::GetFullPath($root).TrimEnd('\','/')
if($previewOutput.TrimEnd('\','/') -eq $rootPath -or $previewOutput.StartsWith($rootPath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Preview images must be written outside the worktree (use a directory under TEMP).'
}
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not (Test-Path -LiteralPath $compiler)){throw 'Windows .NET Framework compiler is unavailable.'}
$compileDirectory=Join-Path $env:TEMP ('myfinder-render-themes-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $compileDirectory -Force | Out-Null
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -Filter '*.cs' -File | Where-Object {$_.Name -notin @('Probe.cs','Tests.cs','AssemblyInfo.cs')} | Sort-Object Name | ForEach-Object {$_.FullName})
. (Join-Path $root 'scripts\Manifest.Common.ps1')
$previewRelease=Read-DeviceBatteryRelease $root
$previewMetadata=Join-Path $compileDirectory 'AssemblyInfo.preview.cs'
[IO.File]::WriteAllText($previewMetadata,('[assembly:System.Reflection.AssemblyInformationalVersion("'+$previewRelease.version+'")]'),(New-Object Text.UTF8Encoding($false)))
$sources+=$previewMetadata
if(-not $DialogsOnly){
$interactionExecutable=Join-Path $compileDirectory 'RenderInteractions.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:HoverTests /reference:System.Runtime.Serialization.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$interactionExecutable" $sources (Join-Path $PSScriptRoot 'HoverTests.cs')
if($LASTEXITCODE -ne 0){throw 'Interaction preview compilation failed.'}
# Every interactive region, separately in default / hover / pressed / keyboard
# focus states, in all four palettes and both light/dark modes.
& $interactionExecutable --render $previewOutput
if($LASTEXITCODE -ne 0){throw 'Interaction preview rendering failed.'}
if($InteractionsOnly){return}
$executable=Join-Path $compileDirectory 'RenderThemes.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:ThemeTests /reference:System.Runtime.Serialization.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$executable" $sources (Join-Path $PSScriptRoot 'ThemeTests.cs')
if($LASTEXITCODE -ne 0){throw 'Theme preview compilation failed.'}
# --render paints injected fixtures and tray-icons.png (both styles, nine states
# including the minimum 1% fill and enlarged rounded device battery bars,
# four native sizes, light/dark taskbars); it never runs settings tests or actions.
& $executable --render $previewOutput
if($LASTEXITCODE -ne 0){throw 'Theme preview rendering failed.'}
}
$dialogExecutable=Join-Path $compileDirectory 'RenderDialogs.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:ThemedDialogTests /reference:System.Runtime.Serialization.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$dialogExecutable" $sources (Join-Path $PSScriptRoot 'ThemedDialogTests.cs')
if($LASTEXITCODE -ne 0){throw 'Dialog preview compilation failed.'}
# Real forms via DrawToBitmap, cropped to the client area. No update, feedback,
# browser or configuration actions run; the retained native title bar is annotated.
# Every dialog image pairs the real form with a same-palette, same-scale main
# panel rendered by CompactRenderer. Includes all update states, feedback,
# diagnostics, alias, confirmation, and short/long release notes in all palettes.
# Unified card-colored clients use 20px padding, 14px regions and inset text fields.
# DrawToBitmap may omit native themed scrollbars; injected-call tests verify theme selection.
& $dialogExecutable --render $previewOutput
if($LASTEXITCODE -ne 0){throw 'Dialog preview rendering failed.'}
