[CmdletBinding()]
param([string]$FixtureExecutable,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('runtime-sampler-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$fixtureDirectory=Join-Path $OutputDirectory ('sampler-fixtures-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$firstFixture=Join-Path $fixtureDirectory 'fixture-v1.exe';$secondFixture=Join-Path $fixtureDirectory 'fixture-v2.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$firstFixture" (Join-Path $PSScriptRoot 'RuntimeSamplerFixture.cs')
if($LASTEXITCODE -ne 0){throw 'Runtime sampler fixture build failed.'}
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /define:FIXTURE_VERSION_TWO "/out:$secondFixture" (Join-Path $PSScriptRoot 'RuntimeSamplerFixture.cs')
if($LASTEXITCODE -ne 0){throw 'Runtime sampler replacement fixture build failed.'}
if(-not $FixtureExecutable){$FixtureExecutable=$firstFixture}
$fixture=[IO.Path]::GetFullPath((Resolve-Path -LiteralPath $FixtureExecutable).ProviderPath)
$sampler=Join-Path $PSScriptRoot 'Measure-Runtime.ps1'
$script:checks=0
function Check([bool]$value,[string]$label){if(-not $value){throw ('FAIL '+$label)};$script:checks++}
function Reject([scriptblock]$action,[string]$label){$rejected=$false;try{& $action | Out-Null}catch{$rejected=$true};Check $rejected $label}
. (Join-Path $PSScriptRoot 'RuntimeObservation.Common.ps1')
$origin=[datetime]::SpecifyKind([datetime]'2020-01-01',[DateTimeKind]::Utc)
$continuous=New-RuntimeObservationState $origin 30
for($index=0;$index -le 960;$index++){Add-RuntimeObservationSample $continuous $origin.AddSeconds(30*$index)}
$continuity=Get-RuntimeObservationContinuity $continuous $origin.AddHours(8) $true
Check ($continuity.ContinuousEightHoursObserved -and $continuity.MaximumSampleIntervalSeconds -eq 30 -and $continuity.GapThresholdSeconds -eq 120) 'synthetic regular eight-hour samples meet the explicit continuity policy'
Check $continuity.CompleteTreeEightHoursObserved 'regular eight-hour root samples with complete child snapshots qualify both observation scopes'
$partialContinuity=Get-RuntimeObservationContinuity $continuous $origin.AddHours(8) $true 1
Check ($partialContinuity.ContinuousEightHoursObserved -and -not $partialContinuity.CompleteTreeEightHoursObserved) 'a partial child snapshot preserves continuous root observation but not complete-tree coverage'
$sparse=New-RuntimeObservationState $origin 30
Add-RuntimeObservationSample $sparse $origin;Add-RuntimeObservationSample $sparse $origin.AddHours(8)
$continuity=Get-RuntimeObservationContinuity $sparse $origin.AddHours(8) $true
Check (-not $continuity.ContinuousEightHoursObserved -and $continuity.LongSampleGapCount -eq 1 -and $continuity.MaximumSampleIntervalSeconds -eq 28800) 'synthetic sleep across eight hours cannot claim continuous observation'
$tail=New-RuntimeObservationState $origin 30
Add-RuntimeObservationSample $tail $origin;Add-RuntimeObservationSample $tail $origin.AddSeconds(30)
$continuity=Get-RuntimeObservationContinuity $tail $origin.AddHours(8) $true
Check (-not $continuity.ContinuousEightHoursObserved -and $continuity.LongObservationGapCount -eq 1) 'a long unsampled tail is included in observation continuity'
$regressed=New-RuntimeObservationState $origin .15
Add-RuntimeObservationSample $regressed $origin;Add-RuntimeObservationSample $regressed $origin.AddSeconds(-1)
$continuity=Get-RuntimeObservationContinuity $regressed $origin $true
Check (-not $continuity.ContinuousRequestedDurationObserved -and $continuity.ClockRegressionCount -eq 1 -and $continuity.GapThresholdSeconds -eq 2) 'clock regression invalidates continuity and small intervals retain the two-second threshold'
$version=[Diagnostics.FileVersionInfo]::GetVersionInfo($fixture).ProductVersion
$process=[Diagnostics.Process]::Start((New-Object Diagnostics.ProcessStartInfo -Property @{
    FileName=$fixture;Arguments='--runtime-fixture 8 --child';UseShellExecute=$false;CreateNoWindow=$true;WindowStyle='Hidden'}))
try {
    # Give only this inert fixture time to start its inert child.
    Start-Sleep -Milliseconds 150
    $fixtureChildren=@(Get-CimInstance Win32_Process -Filter ('ParentProcessId = '+$process.Id) | Where-Object {$_.ExecutablePath -eq $fixture})
    Check ($fixtureChildren.Count -eq 1) 'inert child is available for tree sampling'
    $fixtureChildId=[int]$fixtureChildren[0].ProcessId
    $started=$process.StartTime.ToUniversalTime()
    $statusPath=Join-Path $OutputDirectory 'fixture-status.json'
    [IO.File]::WriteAllText($statusPath,(@{version=$version;universalReady=$true;universalDeviceCount=0;liveVerified=$false;headset=@{receiverPresent=$false};panelOpen=$false;syncIntervalMs=60000}|ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    $result=& $sampler -ExecutablePath $fixture -TargetProcessId $process.Id -ExpectedVersion $version -ExpectedStartedAtUtc $started `
        -OutputDirectory $OutputDirectory -DurationSeconds 2.5 -IntervalSeconds .15 -WarmupSeconds 1 -RowsPerFile 1 -MaxSampleFiles 1 -StatusPath $statusPath -ScenarioNote 'Inert dummy-process self-test; zero physical devices queried.'
    $summary=Get-Content -LiteralPath $result.SummaryPath -Raw | ConvertFrom-Json
    Check ($summary.StopReason -eq 'duration_reached' -and $summary.RequestedDurationCompleted -and -not $summary.EightHoursCompleted) 'short requested duration never claims eight hours'
    Check (-not $summary.ContinuousEightHoursObserved -and -not $summary.CompleteTreeEightHoursObserved -and
        -not $result.CompleteTreeEightHoursObserved -and $summary.ContinuityGapThresholdSeconds -eq 2 -and $summary.MaximumSampleIntervalSeconds -ge 0) 'short real samples publish both scope boundaries and cannot claim continuous eight hours'
    Check ($summary.Identity.ProcessId -eq $process.Id -and $summary.Identity.Binary.Path -eq $fixture -and $summary.Identity.Binary.ProductVersion -eq $version) 'target identity preserves exact path, PID and version'
    Check (([datetime]$summary.Identity.ProcessStartedAtUtc).ToUniversalTime().Ticks -eq $started.Ticks) 'target identity preserves exact start time'
    Check ($summary.Identity.Binary.Sha256 -eq (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash) 'target identity binds binary content'
    Check ($summary.MaximumObservedProcessCount -ge 2 -and $summary.PartialTreeSampleCount -eq 0) 'sampler includes the inert child and any owned console hosts in complete tree totals'
    Check ($summary.SampleCount -ge 2 -and $summary.Statistics.'All.Root.PrivateBytes'.Count -eq $summary.SampleCount) 'streaming statistics cover every sample'
    $steady=$summary.Statistics.'Steady.Root.PrivateBytes'
    Check ($steady.Count -le $summary.SampleCount -and ($steady.Count -eq 0 -or $steady.FirstElapsedSeconds -ge $summary.WarmupSeconds)) 'steady statistics never include a pre-warmup sample'
    Check ($steady.Count -eq 0 -or ($null -ne $steady.FirstValue -and $null -ne $steady.LastValue -and $null -ne $steady.FirstToLastDelta)) 'available steady first/last values and deltas support later growth analysis'
    Check ($summary.LogicalProcessors -eq [Environment]::ProcessorCount -and $summary.Statistics.'All.Tree.CpuPercentNormalized'.Count -gt 0) 'CPU has interval data and explicit logical-core normalization'
    Check ($summary.LastDeviceScenario.State -eq 'no_current_battery_devices_reported' -and $summary.LastDeviceScenario.PanelOpen -eq $false -and $summary.LastDeviceScenario.SyncIntervalMs -eq 60000) 'zero-device app report includes reported panel state and polling period without a hardware query'
    $files=@(Get-ChildItem -LiteralPath $result.RunDirectory -Filter 'samples-*.jsonl' -File)
    Check ($files.Count -eq 1) 'rolling sample file count is bounded'
    $retained=0
    foreach($file in $files){$rows=@(Get-Content -LiteralPath $file.FullName);$retained+=$rows.Count;Check ($rows.Count -eq 1) 'rolling sample rows are bounded';foreach($row in $rows){$sample=$row|ConvertFrom-Json;Check ($sample.Processes.ProcessId -contains $process.Id -and $sample.Processes.ProcessId -contains $fixtureChildId) 'retained sample includes the bound root and its inert child'}}
    Check ($retained -eq 1 -and $summary.SampleCount -gt $retained) 'disk retention rolls over while streaming statistics retain all observations'
    Reject {& $sampler -ExecutablePath $fixture -TargetProcessId $process.Id -ExpectedVersion 'deliberately-wrong-version' -OutputDirectory $OutputDirectory -DurationSeconds .1} 'wrong expected version is rejected'
    Reject {& $sampler -ExecutablePath $fixture -TargetProcessId $process.Id -ExpectedStartedAtUtc $started.AddSeconds(-1) -OutputDirectory $OutputDirectory -DurationSeconds .1} 'wrong expected start time is rejected'
    Reject {& $sampler -ExecutablePath $fixture -TargetProcessId $PID -OutputDirectory $OutputDirectory -DurationSeconds .1} 'a different process at another executable path is rejected'
    Check (-not $process.HasExited) 'sampling and invalid targets never stop the inert fixture'
}finally{
    # This fixture owns its finite lifetime; no kill/stop operation is needed.
    if(-not $process.WaitForExit(10000)){throw 'Inert runtime fixture did not exit on its own.'}
    $process.Dispose()
}
$short=[Diagnostics.Process]::Start((New-Object Diagnostics.ProcessStartInfo -Property @{
    FileName=$fixture;Arguments='--runtime-fixture 2';UseShellExecute=$false;CreateNoWindow=$true;WindowStyle='Hidden'}))
try {
    $result=& $sampler -ExecutablePath $fixture -TargetProcessId $short.Id -ExpectedVersion $version -OutputDirectory $OutputDirectory `
        -DurationSeconds 4 -IntervalSeconds .2 -WarmupSeconds 0 -RowsPerFile 2 -MaxSampleFiles 2
    $summary=Get-Content -LiteralPath $result.SummaryPath -Raw | ConvertFrom-Json
    Check ($summary.StopReason -eq 'process_exited' -and -not $summary.RequestedDurationCompleted -and -not $summary.EightHoursCompleted) 'process exit stops early without claiming a completed soak'
    Check ($summary.LastDeviceScenario.State -eq 'unverified') 'omitting a status snapshot never invents a hardware scenario'
}finally{if(-not $short.WaitForExit(10000)){throw 'Short inert runtime fixture did not exit on its own.'};$short.Dispose()}
$activeFixture=Join-Path $fixtureDirectory 'active-version-fixture.exe'
Copy-Item -LiteralPath $firstFixture -Destination $activeFixture
$changing=[Diagnostics.Process]::Start((New-Object Diagnostics.ProcessStartInfo -Property @{
    FileName=$activeFixture;Arguments='--runtime-fixture 8';UseShellExecute=$false;CreateNoWindow=$true;WindowStyle='Hidden'}))
$monitor=$null
try {
    $versionOutput=Join-Path $OutputDirectory 'version-change'
    $shellExecutable=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$sampler+'" -ExecutablePath "'+$activeFixture+'" -TargetProcessId '+$changing.Id+' -ExpectedVersion "runtime-sampler-fixture-v1" -OutputDirectory "'+$versionOutput+'" -DurationSeconds 6 -IntervalSeconds 0.15 -WarmupSeconds 0'
    $monitor=[Diagnostics.Process]::Start((New-Object Diagnostics.ProcessStartInfo -Property @{
        FileName=$shellExecutable;Arguments=$arguments;UseShellExecute=$false;CreateNoWindow=$true;WindowStyle='Hidden';RedirectStandardOutput=$true;RedirectStandardError=$true}))
    $wait=[Diagnostics.Stopwatch]::StartNew();$readySummaryPath=$null
    while($null -eq $readySummaryPath -and $wait.Elapsed.TotalSeconds -lt 5){
        if(Test-Path -LiteralPath $versionOutput){
            foreach($candidate in @(Get-ChildItem -LiteralPath $versionOutput -Filter summary.json -Recurse -File)){
                try {
                    $ready=Get-Content -LiteralPath $candidate.FullName -Raw | ConvertFrom-Json
                    if(-not $ready.Finished -and $ready.StopReason -eq 'sampling' -and $ready.SampleCount -ge 1 -and
                            $ready.Identity.ProcessId -eq $changing.Id -and $ready.Identity.Binary.Path -eq $activeFixture -and
                            $ready.Identity.Binary.ProductVersion -ceq 'runtime-sampler-fixture-v1' -and
                            ([datetime]$ready.Identity.ProcessStartedAtUtc).ToUniversalTime().Ticks -eq $changing.StartTime.ToUniversalTime().Ticks){
                        $readySummaryPath=$candidate.FullName;break
                    }
                }catch{}
            }
        }
        if($monitor.HasExited){break}
        Start-Sleep -Milliseconds 50
    }
    Check ($null -ne $readySummaryPath) 'replacement waits for this invocation, exact process/start/path/version and a live first sample'
    # Windows permits renaming this test-owned loaded executable. Preserve it,
    # then install a different-version inert copy at its original exact path.
    Move-Item -LiteralPath $activeFixture -Destination ($activeFixture+'.original')
    Copy-Item -LiteralPath $secondFixture -Destination $activeFixture
    Check ($monitor.WaitForExit(5000)) 'binary replacement ends observation promptly'
    if($monitor.ExitCode -ne 0){throw ('Version-change sampler failed: '+$monitor.StandardError.ReadToEnd())}
    $summary=Get-Content -LiteralPath $readySummaryPath -Raw | ConvertFrom-Json
    Check ($summary.StopReason -eq 'executable_changed' -and -not $summary.RequestedDurationCompleted -and -not $summary.EightHoursCompleted) 'different executable version ends early without claiming a completed soak'
    Check (-not $changing.HasExited) 'binary observation never terminates the old dummy process'
}finally{
    if($null -ne $monitor){if(-not $monitor.WaitForExit(10000)){throw 'Version-change sampler did not finish.'};$monitor.Dispose()}
    if(-not $changing.WaitForExit(10000)){throw 'Version-change fixture did not exit on its own.'};$changing.Dispose()
}
Write-Output ('PASS '+$script:checks+' read-only runtime-sampler checks; inert fixture processes only, no installed app or hardware accessed')
