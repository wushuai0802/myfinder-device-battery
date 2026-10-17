param([Parameter(Mandatory=$true)][string]$ZipPath,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.IO.Compression.FileSystem
$testRoot=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('package-smoke-'+[Guid]::NewGuid().ToString('N'))
$extracted=Join-Path $testRoot 'unpacked complete package'
$target=Join-Path $testRoot (([string][char]0x4e2d)+[char]0x6587+' installed program')
$data=Join-Path $testRoot 'fake data';$startup=Join-Path $testRoot 'fake Startup';$programs=Join-Path $testRoot 'fake Programs'
New-Item -ItemType Directory -Path $extracted,$startup,$programs -Force | Out-Null
$zip=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ZipPath))
try {
    foreach($entry in $zip.Entries) {
        $candidate=[IO.Path]::GetFullPath((Join-Path $extracted $entry.FullName))
        if(-not $candidate.StartsWith($extracted+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'ZIP entry escapes extraction directory.'}
        if($entry.FullName.EndsWith('/')){continue}
        New-Item -ItemType Directory -Path (Split-Path -Parent $candidate) -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$candidate,$false)
    }
} finally {$zip.Dispose()}
. (Join-Path $extracted 'scripts\Install.Common.ps1')
# Never inspect or change the real user's StartupApproved registry values.
function Get-DeviceBatteryStartupApproval([string]$Name){return $null}
$manifest=Read-DeviceBatteryPackageManifest $extracted
$checksum=Join-Path (Split-Path -Parent $ZipPath) 'SHA256SUMS.txt'
$actualHash=(Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedLine=$actualHash+'  '+[IO.Path]::GetFileName($ZipPath)
if(-not (Test-Path -LiteralPath $checksum) -or $expectedLine -cnotin @(Get-Content -LiteralPath $checksum -Encoding UTF8)){throw 'The final ZIP does not match SHA256SUMS.txt.'}
$installed=Install-DeviceBattery -SourceRoot $extracted -InstallDirectory $target -DataDirectory $data -StartupDirectory $startup -ProgramsDirectory $programs
if(Test-Path -LiteralPath (Join-Path $target 'engine\tests')){throw 'Development protocol fixtures leaked into the release.'}
$preview=Join-Path $testRoot 'preview'
$previewProcess=Start-Process -FilePath $installed.InstalledExecutable -ArgumentList @('--preview',('"'+$preview+'"')) -WindowStyle Hidden -PassThru
try {
    if(-not $previewProcess.WaitForExit(15000) -or $previewProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $preview 'appearance-preview.png'))){throw 'The installed application could not render its offline preview.'}
} finally {
    if(-not $previewProcess.HasExited){$previewProcess.Kill();$null=$previewProcess.WaitForExit(5000)}
    $previewProcess.Dispose()
}

function Start-InstalledSmoke([string]$File,[string]$Arguments) {
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$File;$info.Arguments=$Arguments;$info.WorkingDirectory=Split-Path -Parent $File
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $process=New-Object Diagnostics.Process;$process.StartInfo=$info
    # Framework Process.Start creates an autoflushing stdin StreamWriter using
    # Console.InputEncoding. An inherited BOM must not prefix JSON Lines.
    $previousInputEncoding=[Console]::InputEncoding
    try {
        [Console]::InputEncoding=New-Object Text.UTF8Encoding($false)
        if(-not $process.Start()){throw 'The installed smoke process did not start.'}
    } finally {[Console]::InputEncoding=$previousInputEncoding}
    return $process
}

function Get-SmokeDescendants([int]$ParentId) {
    $all=@(Get-CimInstance -ClassName Win32_Process -Property ProcessId,ParentProcessId,ExecutablePath)
    $known=New-Object 'System.Collections.Generic.HashSet[int]';$null=$known.Add($ParentId)
    for($round=0;$round -lt 4;$round++) {
        foreach($item in $all){if($known.Contains([int]$item.ParentProcessId)){$null=$known.Add([int]$item.ProcessId)}}
    }
    return @($all | Where-Object {$_.ProcessId -ne $ParentId -and $known.Contains([int]$_.ProcessId)})
}

function Assert-SmokeChildrenExited($Children) {
    foreach($child in @($Children)) {
        $live=Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
        if($live) {
            if(-not $live.WaitForExit(5000)) {
                # Only clean up this fixture's exact portable reader path.
                if($live.Path -ieq $child.ExecutablePath -and $live.Path.StartsWith($target+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){$live.Kill();$null=$live.WaitForExit(5000)}
                throw 'An installed smoke child survived its parent.'
            }
            $live.Dispose()
        }
    }
}

function Read-SmokeResult($Process) {
    # .NET Framework may inherit a BOM-emitting Console.InputEncoding from
    # PowerShell 7. JSON Lines must begin with a JSON token, not a UTF-8 BOM.
    $command=[Text.Encoding]::ASCII.GetBytes('{"command":"poll","requestId":1,"generation":1,"force":true}'+[char]10)
    $Process.StandardInput.BaseStream.Write($command,0,$command.Length)
    $Process.StandardInput.BaseStream.Flush()
    for($frame=0;$frame -lt 64;$frame++) {
        $line=$Process.StandardOutput.ReadLineAsync()
        if(-not $line.Wait(15000) -or [string]::IsNullOrWhiteSpace($line.Result)){throw 'The installed portable worker did not return a bounded result.'}
        $result=$line.Result | ConvertFrom-Json
        if($result.complete) {
            if($result.schema -ne 2 -or $result.packageSmoke -ne $true -or $result.requestId -ne 1 -or $result.generation -ne 1 -or
                $result.providerCount -ne 26 -or @($result.devices).Count -ne 0 -or @($result.errors).Count -ne 0){throw ('The installed portable engine failed its no-hardware self-test: '+($result | ConvertTo-Json -Compress -Depth 6))}
            return $result
        }
    }
    throw 'Too many portable worker progress frames.'
}

$main=$null;$worker=$null;$children=@()
try {
    # Test the actual installed GUI binary and its real GenericBatteryWorker,
    # not the tiny executable used by the isolated installer unit tests.
    $main=Start-InstalledSmoke $installed.InstalledExecutable '--package-smoke'
    $read=$main.StandardOutput.ReadToEndAsync()
    if(-not $main.WaitForExit(20000) -or -not $read.Wait(2000) -or $main.ExitCode -ne 0){throw 'The installed application/reader smoke harness failed.'}
    $appResult=$read.Result.Trim() | ConvertFrom-Json
    if($appResult.schema -ne 1 -or $appResult.packageSmoke -ne $true -or $appResult.version -cne $manifest.version -or $appResult.providerCount -ne 26 -or $appResult.error){throw 'The installed application smoke result is invalid.'}
    $main.Dispose();$main=$null
    $worker=Start-InstalledSmoke (Join-Path $target 'engine\runtime\python.exe') ('-I -B -u "'+(Join-Path $target 'engine\server.py')+'" --package-smoke')
    $result=Read-SmokeResult $worker
    $children=Get-SmokeDescendants $worker.Id
    if($children.Count -lt 1){throw 'Real engine isolation children were not exercised.'}
    $worker.StandardInput.BaseStream.Close()
    if(-not $worker.WaitForExit(10000) -or $worker.ExitCode -ne 0){throw 'The installed worker failed to exit on parent pipe EOF.'}
    Assert-SmokeChildrenExited $children
    $worker.Dispose();$worker=$null
    # An abrupt parent exit must also close the private Windows jobs.
    $worker=Start-InstalledSmoke (Join-Path $target 'engine\runtime\python.exe') ('-I -B -u "'+(Join-Path $target 'engine\server.py')+'" --package-smoke')
    $null=Read-SmokeResult $worker
    $children=Get-SmokeDescendants $worker.Id
    if($children.Count -lt 1){throw 'Abrupt-exit child lifecycle was not exercised.'}
    $worker.Kill();$null=$worker.WaitForExit(5000)
    Assert-SmokeChildrenExited $children
    $worker.Dispose();$worker=$null
    $leftovers=@(Get-Process -Name AtkMouseBattery,python,pythonw -ErrorAction SilentlyContinue | Where-Object {$_.Path -and $_.Path.StartsWith($target+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)})
    if($leftovers.Count -gt 0){throw 'Installed package smoke left running child processes.'}
    Uninstall-DeviceBatteryStartup -SourceRoot $extracted -DataDirectory $data -StartupDirectory $startup -ProgramsDirectory $programs -RemoveProgram -RemoveData|Out-Null
    $report=[ordered]@{schema=1;version=$manifest.version;zipSha256=$actualHash;packageFiles=@($manifest.files).Count;
        isolatedInstall=$true;actualAppSmoke=$true;portableReaderImports=$true;providerCount=$result.providerCount;
        gracefulEofCleanup=$true;abruptParentCleanup=$true;hardwareAccess=$false;realStartupChanged=$false}
    [IO.File]::WriteAllText((Join-Path $testRoot 'package-smoke-result.json'),($report | ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
    Write-Output ('PASS complete final ZIP smoke: '+$manifest.version+'; actual app + portable worker; no hardware or real startup changes')
} finally {
    foreach($process in @($main,$worker)){if($process){if(-not $process.HasExited){$process.Kill();$null=$process.WaitForExit(5000)};$process.Dispose()}}
    Assert-SmokeChildrenExited $children
}
