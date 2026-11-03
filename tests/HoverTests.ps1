[CmdletBinding()]
param([switch]$Related)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$testOutput=Join-Path $env:TEMP ('myfinder-hover-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testOutput | Out-Null
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -Filter '*.cs' -File | Where-Object {$_.Name -notin @('Probe.cs','Tests.cs','AssemblyInfo.cs')} | Sort-Object Name | ForEach-Object {$_.FullName})
$executable=Join-Path $testOutput 'HoverTests.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:HoverTests /reference:System.Runtime.Serialization.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$executable" $sources (Join-Path $PSScriptRoot 'HoverTests.cs')
if($LASTEXITCODE -ne 0){throw 'Hover test compilation failed.'}
& $executable
if($LASTEXITCODE -ne 0){throw 'Hover tests failed.'}
if($Related){
    $failed=@()
    foreach($name in @('ThemeTests','PopupStateRegressionTests','UiAndControllerTests','UiFeedbackTests','UiMenusTests','WirelessTests','WirelessDpiUiTests','ThemedDialogTests')){
        $exe=Join-Path $testOutput ($name+'.exe')
        & $compiler /nologo /target:exe /platform:x64 /optimize+ ("/main:"+$name) /reference:System.Runtime.Serialization.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ("/out:"+$exe) $sources (Join-Path $root 'scripts\AssemblyInfo.cs') (Join-Path $PSScriptRoot ($name+'.cs'))
        if($LASTEXITCODE -ne 0){throw ($name+' compilation failed.')}
        if($name -eq 'ThemedDialogTests'){& $exe --soft-only}else{& $exe $testOutput}
        if($LASTEXITCODE -ne 0){$failed+=$name}
    }
    Write-Output ('Focused regression output: '+$testOutput)
    if($failed.Count){throw ('Failed: '+($failed -join ', '))}
}
