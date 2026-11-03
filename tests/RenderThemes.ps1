[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$previewOutput=[IO.Path]::GetFullPath($OutputDirectory)
$rootPath=[IO.Path]::GetFullPath($root).TrimEnd('\','/')
if($previewOutput.TrimEnd('\','/') -eq $rootPath -or $previewOutput.StartsWith($rootPath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Preview images must be written outside the worktree (use a directory under TEMP).'
}
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not (Test-Path -LiteralPath $compiler)){throw 'Windows .NET Framework compiler is unavailable.'}
$compileDirectory=Join-Path $env:TEMP ('myfinder-render-themes-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $compileDirectory -Force | Out-Null
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -Filter '*.cs' -File | Where-Object {$_.Name -notin @('Probe.cs','Tests.cs','AssemblyInfo.cs')} | Sort-Object Name | ForEach-Object {$_.FullName})
$executable=Join-Path $compileDirectory 'RenderThemes.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:ThemeTests /reference:System.Runtime.Serialization.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$executable" $sources (Join-Path $PSScriptRoot 'ThemeTests.cs')
if($LASTEXITCODE -ne 0){throw 'Theme preview compilation failed.'}
# --render only paints injected fixtures; it never runs settings tests or actions.
& $executable --render $previewOutput
if($LASTEXITCODE -ne 0){throw 'Theme preview rendering failed.'}
