param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root 'tmp\tests'}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
. (Join-Path $root 'scripts\Manifest.Common.ps1')
$testRelease=Read-DeviceBatteryRelease $root
$testMetadata=Join-Path $OutputDirectory 'AssemblyInfo.tests.cs'
[IO.File]::WriteAllText($testMetadata,('[assembly:System.Reflection.AssemblyInformationalVersion("'+$testRelease.version+'")]'),(New-Object Text.UTF8Encoding($false)))
$appSources=@(Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -Filter '*.cs' -File | Where-Object {$_.Name -notin @('Probe.cs','Tests.cs','AssemblyInfo.cs')} | Sort-Object Name | ForEach-Object {$_.FullName})
$appSources+=$testMetadata
$test=Join-Path $OutputDirectory 'AudioSettingsTests.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:AudioSettingsTests /reference:System.Runtime.Serialization.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll "/out:$test" $appSources (Join-Path $PSScriptRoot 'AudioSettingsTests.cs')
if($LASTEXITCODE -ne 0){throw 'Audio/settings test build failed.'}
& $test $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Audio/settings tests failed.'}
$menuTest=Join-Path $OutputDirectory 'AudioMenuTests.exe'
$menuSources=$appSources
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:AudioMenuTests /reference:System.Runtime.Serialization.dll `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll "/out:$menuTest" $menuSources (Join-Path $PSScriptRoot 'AudioMenuTests.cs')
if($LASTEXITCODE -ne 0){throw 'Audio menu test build failed.'}
& $menuTest
if($LASTEXITCODE -ne 0){throw 'Audio menu tests failed.'}
$uiTest=Join-Path $OutputDirectory 'UiAndControllerTests.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:UiAndControllerTests /reference:System.Runtime.Serialization.dll `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll "/out:$uiTest" $menuSources (Join-Path $PSScriptRoot 'UiAndControllerTests.cs')
if($LASTEXITCODE -ne 0){throw 'UI/controller test build failed.'}
& $uiTest $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'UI/controller tests failed.'}
$trayTest=Join-Path $OutputDirectory 'TrayInteractionTests.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:TrayInteractionTests /reference:System.Runtime.Serialization.dll `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll "/out:$trayTest" $menuSources (Join-Path $PSScriptRoot 'TrayInteractionTests.cs')
if($LASTEXITCODE -ne 0){throw 'Tray interaction test build failed.'}
& $trayTest
if($LASTEXITCODE -ne 0){throw 'Tray interaction tests failed.'}
$wirelessTest=Join-Path $OutputDirectory 'WirelessTests.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:WirelessTests /reference:System.Runtime.Serialization.dll `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll "/out:$wirelessTest" $menuSources (Join-Path $PSScriptRoot 'WirelessTests.cs')
if($LASTEXITCODE -ne 0){throw 'Universal battery test build failed.'}
& $wirelessTest $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Universal battery tests failed.'}
$runtimeTest=Join-Path $OutputDirectory 'RuntimeTests.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:RuntimeTests /reference:System.Runtime.Serialization.dll `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll "/out:$runtimeTest" $menuSources (Join-Path $PSScriptRoot 'RuntimeTests.cs')
if($LASTEXITCODE -ne 0){throw 'Runtime integration test build failed.'}
& $runtimeTest $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Runtime integration tests failed.'}
$installer=Join-Path $PSScriptRoot 'InstallerTests.ps1'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -OutputDirectory $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Windows PowerShell installer tests failed.'}
if(Get-Command pwsh.exe -ErrorAction SilentlyContinue){
    & pwsh.exe -NoProfile -File $installer -OutputDirectory $OutputDirectory
    if($LASTEXITCODE -ne 0){throw 'PowerShell 7 installer tests failed.'}
}
