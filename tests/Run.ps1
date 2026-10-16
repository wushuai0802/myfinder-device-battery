param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root 'tmp\tests'}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$test=Join-Path $OutputDirectory 'AudioSettingsTests.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /reference:System.Runtime.Serialization.dll "/out:$test" `
    (Join-Path $root 'scripts\DeviceSettings.cs') (Join-Path $root 'scripts\AudioControl.cs') (Join-Path $root 'scripts\HidBattery.cs') (Join-Path $root 'scripts\HeadsetBattery.cs') (Join-Path $root 'scripts\ReadingCache.cs') (Join-Path $PSScriptRoot 'AudioSettingsTests.cs')
if($LASTEXITCODE -ne 0){throw 'Audio/settings test build failed.'}
& $test $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Audio/settings tests failed.'}
$menuTest=Join-Path $OutputDirectory 'AudioMenuTests.exe'
$menuSources=@('HidBattery.cs','HeadsetBattery.cs','AudioControl.cs','DeviceSettings.cs','VisualDesign.cs','TrayGlyph.cs','TrayApp.cs','UiState.cs','NativeSupport.cs','SettingsForm.cs','PopupPages.cs','ReadingCache.cs','CompactRenderer.cs') |
    ForEach-Object {Join-Path $root ('scripts\'+$_)}
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:AudioMenuTests /reference:System.Runtime.Serialization.dll `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$menuTest" $menuSources (Join-Path $PSScriptRoot 'AudioMenuTests.cs')
if($LASTEXITCODE -ne 0){throw 'Audio menu test build failed.'}
& $menuTest
if($LASTEXITCODE -ne 0){throw 'Audio menu tests failed.'}
$uiTest=Join-Path $OutputDirectory 'UiAndControllerTests.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:UiAndControllerTests /reference:System.Runtime.Serialization.dll `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$uiTest" $menuSources (Join-Path $PSScriptRoot 'UiAndControllerTests.cs')
if($LASTEXITCODE -ne 0){throw 'UI/controller test build failed.'}
& $uiTest $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'UI/controller tests failed.'}
$installer=Join-Path $PSScriptRoot 'InstallerTests.ps1'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -OutputDirectory $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Windows PowerShell installer tests failed.'}
if(Get-Command pwsh.exe -ErrorAction SilentlyContinue){
    & pwsh.exe -NoProfile -File $installer -OutputDirectory $OutputDirectory
    if($LASTEXITCODE -ne 0){throw 'PowerShell 7 installer tests failed.'}
}
