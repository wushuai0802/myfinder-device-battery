[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$OutputDirectory,[switch]$InteractionsOnly,[switch]$DialogsOnly,[switch]$UpdateBadgesOnly,[switch]$MainPanelsOnly,[ValidateSet('all','ring','bar','grid')][string]$PanelLayout='all',[switch]$FullPanelMatrix)
$ErrorActionPreference='Stop'
if($InteractionsOnly -and $DialogsOnly){throw 'InteractionsOnly and DialogsOnly cannot be combined.'}
if($UpdateBadgesOnly -and ($InteractionsOnly -or $DialogsOnly)){throw 'UpdateBadgesOnly cannot be combined with other modes.'}
if($MainPanelsOnly -and ($InteractionsOnly -or $DialogsOnly -or $UpdateBadgesOnly)){throw 'MainPanelsOnly cannot be combined with other modes.'}
if($FullPanelMatrix -and ($DialogsOnly -or $UpdateBadgesOnly)){throw 'FullPanelMatrix requires main panel rendering.'}
$root=Split-Path -Parent $PSScriptRoot
$previewOutput=[IO.Path]::GetFullPath($OutputDirectory)
$temporaryRoot=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\','/')
if($UpdateBadgesOnly){$temporaryRoot=[IO.Path]::GetFullPath((Join-Path $root 'tmp')).TrimEnd('\','/')}
if(-not $previewOutput.StartsWith($temporaryRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Preview images must be written under TEMP (update badges: worktree/tmp).'
}
$rootPath=[IO.Path]::GetFullPath($root).TrimEnd('\','/')
if(-not $UpdateBadgesOnly -and ($previewOutput.TrimEnd('\','/') -eq $rootPath -or $previewOutput.StartsWith($rootPath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase))) {
    throw 'Preview images must be written outside the worktree (use a directory under TEMP).'
}
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not (Test-Path -LiteralPath $compiler)){throw 'Windows .NET Framework compiler is unavailable.'}
$compileDirectory=Join-Path $env:TEMP ('myfinder-render-themes-'+[Guid]::NewGuid().ToString('N'))
if($UpdateBadgesOnly){$compileDirectory=Join-Path $previewOutput '.build'}
New-Item -ItemType Directory -Path $compileDirectory -Force | Out-Null
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -Filter '*.cs' -File | Where-Object {$_.Name -notin @('Probe.cs','Tests.cs','AssemblyInfo.cs')} | Sort-Object Name | ForEach-Object {$_.FullName})
. (Join-Path $root 'scripts\Manifest.Common.ps1')
$previewRelease=Read-DeviceBatteryRelease $root
$previewMetadata=Join-Path $compileDirectory 'AssemblyInfo.preview.cs'
[IO.File]::WriteAllText($previewMetadata,('[assembly:System.Reflection.AssemblyInformationalVersion("'+$previewRelease.version+'")]'),(New-Object Text.UTF8Encoding($false)))
$sources+=$previewMetadata
if($UpdateBadgesOnly){
    $badgeExecutable=Join-Path $compileDirectory 'UpdateBadgeTests.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /main:UpdateBadgeTests /reference:System.Runtime.Serialization.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$badgeExecutable" $sources (Join-Path $PSScriptRoot 'UpdateBadgeTests.cs')
    if($LASTEXITCODE -ne 0){throw 'Update badge preview compilation failed.'}
    & $badgeExecutable --render $previewOutput
    if($LASTEXITCODE -ne 0){throw 'Update badge pixel verification failed.'}
    return
}
if(-not $DialogsOnly){
$panelExecutable=Join-Path $compileDirectory 'RenderMainPanels.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:PanelDesignTests /reference:System.Runtime.Serialization.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$panelExecutable" $sources (Join-Path $PSScriptRoot 'PanelDesignTests.cs')
if($LASTEXITCODE -ne 0){throw 'Main panel preview compilation failed.'}
$mainRenderStarted=[DateTime]::UtcNow
if($FullPanelMatrix){& $panelExecutable --render $previewOutput $PanelLayout --matrix}
elseif($PanelLayout -eq 'all'){& $panelExecutable --render $previewOutput}else{& $panelExecutable --render $previewOutput $PanelLayout}
if($LASTEXITCODE -ne 0){throw 'Main panel preview rendering failed.'}
$mainManifest=[ordered]@{
    schema=1; generatedAtUtc=[DateTime]::UtcNow.ToString('o'); renderer='tests/RenderThemes.ps1 -> PanelDesignTests --render'
    baseCommit=(& git -C $root rev-parse HEAD); version=$previewRelease.version; panelLayout=$PanelLayout; fullPanelMatrix=[bool]$FullPanelMatrix
    scale=2; simulatedState=$true; liveHardware=$false; openingFillProgress=1
    matrix=[ordered]@{layouts=@(if($PanelLayout -eq 'all'){'ring';'bar';'grid'}else{$PanelLayout});palettes=@('ocean','native','amber','mist');appearance=@('light','dark');fonts=@('normal','large');headset=@($false,$true);highContrast=@($false,$true);renderPaths=@('native','wireless')}
    fileParameters='Matrix: main-panel-{layout}-{palette}-{appearance}-{font}-{headset|no-headset}-{standard|high-contrast}-{native|wireless}.png. Composite sheets: standard native / multiple devices / large native / multiple devices. Overflow: five mice with eight DPI stages, logical maximum height 520, top/bottom control scroll. Chooser: ring selected, 56x36 logical thumbnails.'
    sources=@($sources | ForEach-Object {[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
    files=@(Get-ChildItem -LiteralPath $previewOutput -Filter '*.png' -File | Where-Object {$_.LastWriteTimeUtc -ge $mainRenderStarted -and ($_.Name -like 'main-panel-*' -or $_.Name -like 'layout-chooser-*')} | Sort-Object Name | ForEach-Object {[ordered]@{name=$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
}
$mainManifest.sources+=([ordered]@{path=$PSCommandPath;sha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash})
$mainManifest.sources+=([ordered]@{path=(Join-Path $PSScriptRoot 'PanelDesignTests.cs');sha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'PanelDesignTests.cs') -Algorithm SHA256).Hash})
$manifestPath=Join-Path $previewOutput 'main-panel-manifest.json'
[IO.File]::WriteAllText($manifestPath,($mainManifest | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
Write-Output $manifestPath
if($MainPanelsOnly){return}
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
