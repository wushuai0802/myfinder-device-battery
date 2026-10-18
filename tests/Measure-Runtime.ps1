<#
.SYNOPSIS
Read-only, identity-bound sampling of an already running application and its children.
.DESCRIPTION
Never launches, stops, writes to, or sends device/audio commands to the target.
Only the requested output directory is written. Defaults to eight hours with
30-second samples. Memory peaks are observed sample peaks, not lifetime maxima.
An optional app-produced status snapshot can describe the device scenario;
without a usable snapshot the scenario remains unverified.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ExecutablePath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateRange(0,2147483647)][int]$TargetProcessId=0,
    [string]$ExpectedVersion,
    [Nullable[datetime]]$ExpectedStartedAtUtc,
    [ValidateRange(0.1,604800)][double]$DurationSeconds=28800,
    [ValidateRange(0.1,3600)][double]$IntervalSeconds=30,
    [ValidateRange(0,86400)][double]$WarmupSeconds=300,
    [ValidateRange(1,4096)][int]$RowsPerFile=1024,
    [ValidateRange(1,16)][int]$MaxSampleFiles=4,
    [ValidateRange(1,512)][int]$MaxProcesses=128,
    [string]$StatusPath,
    [string]$ScenarioNote='Device count is unverified unless a current app status snapshot is supplied.'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
. (Join-Path $PSScriptRoot 'RuntimeObservation.Common.ps1')
$utf8=New-Object Text.UTF8Encoding($false)
$exactPath=[IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ExecutablePath).ProviderPath)
$logicalProcessors=[Environment]::ProcessorCount

function Read-BinaryIdentity {
    $file=Get-Item -LiteralPath $exactPath
    $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($exactPath)
    [pscustomobject]@{Path=$exactPath;Length=$file.Length;Sha256=(Get-FileHash -LiteralPath $exactPath -Algorithm SHA256).Hash;
        ProductVersion=$version.ProductVersion;FileVersion=$version.FileVersion}
}
function Read-Root {
    $candidate=Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue
    if($null -eq $candidate){return $null}
    try {
        if($candidate.HasExited){return $null}
        [pscustomobject]@{Id=$candidate.Id;Path=[IO.Path]::GetFullPath($candidate.Path);
            StartedAtUtc=$candidate.StartTime.ToUniversalTime()}
    }finally{$candidate.Dispose()}
}
function New-Statistics { @{Count=0;Sum=0.0;Minimum=[double]::PositiveInfinity;Maximum=[double]::NegativeInfinity;First=$null;Last=$null;FirstAtSeconds=$null;LastAtSeconds=$null} }
function Add-Statistics($stats,$value,$atSeconds) {
    if($null -eq $value){return}
    if($stats.Count -eq 0){$stats.First=[double]$value;$stats.FirstAtSeconds=$atSeconds}
    $stats.Last=[double]$value;$stats.LastAtSeconds=$atSeconds
    $stats.Count++;$stats.Sum+=[double]$value
    $stats.Minimum=[Math]::Min($stats.Minimum,[double]$value)
    $stats.Maximum=[Math]::Max($stats.Maximum,[double]$value)
}
function Export-Statistics($stats) {
    if($stats.Count -eq 0){return [pscustomobject]@{Count=0;Minimum=$null;Mean=$null;ObservedSamplePeak=$null}}
    $span=$stats.LastAtSeconds-$stats.FirstAtSeconds
    [pscustomobject]@{Count=$stats.Count;Minimum=$stats.Minimum;Mean=$stats.Sum/$stats.Count;ObservedSamplePeak=$stats.Maximum;
        FirstValue=$stats.First;LastValue=$stats.Last;FirstElapsedSeconds=$stats.FirstAtSeconds;LastElapsedSeconds=$stats.LastAtSeconds;
        FirstToLastDelta=$stats.Last-$stats.First;FirstToLastRatePerHour=$(if($span -gt 0){3600*($stats.Last-$stats.First)/$span}else{$null})}
}
function Read-DeviceScenario {
    if(-not $StatusPath){return [pscustomobject]@{State='unverified';Reason='No app status snapshot supplied.'}}
    try {
        $file=Get-Item -LiteralPath $StatusPath
        if($file.Length -gt 65536 -or $file.LastWriteTimeUtc -lt $boundStart -or
                ([datetime]::UtcNow-$file.LastWriteTimeUtc).TotalSeconds -gt 600){throw 'Status is stale, oversized, or predates this process.'}
        $status=Get-Content -LiteralPath $StatusPath -Raw | ConvertFrom-Json
        if($status.version -ne $boundBinary.ProductVersion){throw 'Status version does not match the bound executable.'}
        $state='app_snapshot'
        if($status.universalReady -and $status.universalDeviceCount -eq 0 -and
                -not $status.liveVerified -and -not $status.headset.receiverPresent){$state='no_current_battery_devices_reported'}
        [pscustomobject]@{State=$state;UniversalDeviceCount=$status.universalDeviceCount;
            NativeMouseReadingCurrent=[bool]$status.liveVerified;HeadsetReceiverReported=[bool]$status.headset.receiverPresent;
            PanelOpen=$(if($status.PSObject.Properties['panelOpen']){[bool]$status.panelOpen}else{$null});
            SyncIntervalMs=$(if($status.PSObject.Properties['syncIntervalMs']){[int]$status.syncIntervalMs}else{$null});
            SnapshotWrittenAtUtc=$file.LastWriteTimeUtc.ToString('o');
            Boundary='Shared app-produced snapshot, correlated by version and write time; no hardware inventory was queried.'}
    }catch{[pscustomobject]@{State='unverified';Reason='App snapshot unavailable, stale, incompatible, or not correlated with this process.'}}
}

$boundBinary=Read-BinaryIdentity
if($ExpectedVersion -and $boundBinary.ProductVersion -cne $ExpectedVersion){throw 'Executable product version does not match ExpectedVersion.'}
if($TargetProcessId -eq 0){
    $matches=@(Get-CimInstance Win32_Process -Property ProcessId,ExecutablePath | Where-Object {
        $_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath),$exactPath,[StringComparison]::OrdinalIgnoreCase)})
    if($matches.Count -ne 1){throw 'Expected exactly one process at ExecutablePath. Supply TargetProcessId to disambiguate.'}
    $TargetProcessId=[int]$matches[0].ProcessId
}
$initial=Read-Root
if($null -eq $initial -or -not [string]::Equals($initial.Path,$exactPath,[StringComparison]::OrdinalIgnoreCase)){
    throw 'TargetProcessId is not a running process at the exact ExecutablePath.'
}
$boundStart=$initial.StartedAtUtc
if($null -ne $ExpectedStartedAtUtc -and $boundStart.Ticks -ne $ExpectedStartedAtUtc.ToUniversalTime().Ticks){
    throw 'Process start time does not match ExpectedStartedAtUtc.'
}
$runDirectory=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('runtime-'+[datetime]::UtcNow.ToString('yyyyMMdd-HHmmss-ffff')+'-'+$TargetProcessId)
New-Item -ItemType Directory -Path $runDirectory -ErrorAction Stop | Out-Null
$summaryPath=Join-Path $runDirectory 'summary.json'
$metrics=@('PrivateBytes','WorkingSetBytes','Handles','Threads','CpuPercentNormalized')
$statistics=@{}
foreach($phase in @('All','Steady')){foreach($scope in @('Root','Tree')){foreach($metric in $metrics){
    $statistics[($phase+'.'+$scope+'.'+$metric)]=New-Statistics
}}}
$clock=[Diagnostics.Stopwatch]::StartNew()
$startedAtUtc=[datetime]::UtcNow
$observation=New-RuntimeObservationState $startedAtUtc $IntervalSeconds
$previousCpu=@{};$previousAt=$null;$sampleCount=0;$partialSamples=0;$maximumTree=0
$fileIndex=-1;$rowsInFile=$RowsPerFile;$sampleFile=$null;$stopReason='duration_reached';$samplingFailure=$null;$reachedDuration=$false
$lastScenario=[pscustomobject]@{State='unverified'};$scenarioCounts=@{}

function Save-Summary([bool]$finished) {
    $export=@{}
    foreach($key in $statistics.Keys){$export[$key]=Export-Statistics $statistics[$key]}
    $continuity=Get-RuntimeObservationContinuity $observation ([datetime]::UtcNow) ($finished -and $reachedDuration -and $stopReason -eq 'duration_reached') $partialSamples
    $summary=[ordered]@{Schema=1;Finished=$finished;StopReason=$(if($finished){$stopReason}else{'sampling'});
        StartedAtUtc=$startedAtUtc.ToString('o');UpdatedAtUtc=[datetime]::UtcNow.ToString('o');
        RequestedDurationSeconds=$DurationSeconds;ObservedDurationSeconds=$clock.Elapsed.TotalSeconds;
        RequestedDurationCompleted=($finished -and $reachedDuration -and $stopReason -eq 'duration_reached');
        EightHoursCompleted=($finished -and $reachedDuration -and $stopReason -eq 'duration_reached' -and $clock.Elapsed.TotalSeconds -ge 28800);
        ContinuousEightHoursObserved=$continuity.ContinuousEightHoursObserved;
        CompleteTreeEightHoursObserved=$continuity.CompleteTreeEightHoursObserved;ObservationContinuity=$continuity;
        MaximumSampleIntervalSeconds=$continuity.MaximumSampleIntervalSeconds;LongSampleGapCount=$continuity.LongSampleGapCount;
        LongObservationGapCount=$continuity.LongObservationGapCount;ContinuityGapThresholdSeconds=$continuity.GapThresholdSeconds;
        IntervalSeconds=$IntervalSeconds;WarmupSeconds=$WarmupSeconds;LogicalProcessors=$logicalProcessors;
        Identity=@{ProcessId=$TargetProcessId;ProcessStartedAtUtc=$boundStart.ToString('o');Binary=$boundBinary};
        SampleCount=$sampleCount;PartialTreeSampleCount=$partialSamples;MaximumObservedProcessCount=$maximumTree;
        RollingSamples=@{Directory=$runDirectory;FilePattern='samples-*.jsonl';RowsPerFile=$RowsPerFile;MaximumFiles=$MaxSampleFiles;MaximumRetainedRows=($RowsPerFile*$MaxSampleFiles)};
        ScenarioNote=$ScenarioNote;LastDeviceScenario=$lastScenario;DeviceScenarioSampleCounts=$scenarioCounts;
        Statistics=$export;SamplingFailure=$samplingFailure;
        Boundaries=@('Read-only observation; no target launch, restart, HID/audio command, configuration or startup change.',
            'Steady statistics exclude the configured initial warmup; peaks are sampled observations.',
            'CPU is interval processor time divided by wall time and logical processor count.',
            'EightHoursCompleted is elapsed-duration completion only. ContinuousEightHoursObserved additionally requires continuous root-process samples with no long observation gaps or clock regression.',
            'CompleteTreeEightHoursObserved additionally requires every sampled child tree to be complete. Partial tree samples are excluded from Tree statistics; this does not capture all transient children between samples.',
            'Short-lived processes between samples and their CPU/memory peaks can be missed; hardware workload and 60fps are not certified.')}
    $temporary=$summaryPath+'.tmp'
    [IO.File]::WriteAllText($temporary,($summary|ConvertTo-Json -Depth 12),$utf8)
    Move-Item -LiteralPath $temporary -Destination $summaryPath -Force
}
try {
    for(;;){
        $root=Read-Root
        if($null -eq $root){$stopReason='process_exited';break}
        if($root.StartedAtUtc.Ticks -ne $boundStart.Ticks){
            $stopReason='process_identity_changed';break
        }
        try{$binary=Read-BinaryIdentity}catch{$stopReason='executable_changed';break}
        if($binary.Sha256 -cne $boundBinary.Sha256 -or $binary.ProductVersion -cne $boundBinary.ProductVersion -or $binary.FileVersion -cne $boundBinary.FileVersion){
            $stopReason='executable_changed';break
        }
        if(-not [string]::Equals($root.Path,$exactPath,[StringComparison]::OrdinalIgnoreCase)){$stopReason='process_identity_changed';break}
        $inventory=@(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,CreationDate)
        $tree=@{};$tree[$TargetProcessId]=$boundStart
        do {
            $added=$false
            foreach($entry in $inventory){
                $childId=[int]$entry.ProcessId;$parentId=[int]$entry.ParentProcessId
                if($tree.ContainsKey($childId) -or -not $tree.ContainsKey($parentId) -or $null -eq $entry.CreationDate){continue}
                $created=$entry.CreationDate.ToUniversalTime()
                if($created -lt $tree[$parentId]){continue}
                $tree[$childId]=$created;$added=$true
                if($tree.Count -gt $MaxProcesses){throw 'Process tree exceeds MaxProcesses; sampling stopped without truncating totals.'}
            }
        }while($added)
        $at=[datetime]::UtcNow;$seconds=$clock.Elapsed.TotalSeconds;$currentCpu=@{};$processes=New-Object Collections.Generic.List[object]
        $totals=@{PrivateBytes=0L;WorkingSetBytes=0L;Handles=0L;Threads=0L;CpuPercentNormalized=$null}
        $cpuDelta=0.0;$rootDelta=$null;$partial=$false;$rootValues=$null
        foreach($childId in $tree.Keys){
            $process=$null
            try {
                $process=Get-Process -Id $childId -ErrorAction Stop
                $created=$process.StartTime.ToUniversalTime()
                if([Math]::Abs(($created-$tree[$childId]).TotalSeconds) -gt 1){throw 'Process identity changed during the sample.'}
                $key=$childId.ToString()+':'+$created.Ticks
                $cpu=$process.TotalProcessorTime.TotalSeconds;$currentCpu[$key]=$cpu
                $delta=$null
                if($previousCpu.ContainsKey($key)){$delta=[Math]::Max(0,$cpu-$previousCpu[$key])}
                elseif($null -ne $previousAt -and $created -ge $previousAt){$delta=$cpu}
                if($null -ne $delta){$cpuDelta+=$delta}
                $values=@{PrivateBytes=$process.PrivateMemorySize64;WorkingSetBytes=$process.WorkingSet64;
                    Handles=$process.HandleCount;Threads=$process.Threads.Count;CpuPercentNormalized=$null}
                if($null -ne $previousAt -and $null -ne $delta){$values.CpuPercentNormalized=100*$delta/($at-$previousAt).TotalSeconds/$logicalProcessors}
                foreach($metric in @('PrivateBytes','WorkingSetBytes','Handles','Threads')){$totals[$metric]+=$values[$metric]}
                if($childId -eq $TargetProcessId){$rootValues=$values;$rootDelta=$delta}
                $processes.Add([pscustomobject]@{ProcessId=$childId;StartedAtUtc=$created.ToString('o');Metrics=$values})
            }catch{$partial=$true}
            finally{if($null -ne $process){$process.Dispose()}}
        }
        if($null -eq $rootValues){
            $probe=Read-Root
            $stopReason=if($null -eq $probe){'process_exited'}elseif($probe.StartedAtUtc.Ticks -ne $boundStart.Ticks){'process_identity_changed'}else{'root_unavailable_during_sample'}
            break
        }
        if($null -ne $previousAt){$totals.CpuPercentNormalized=100*$cpuDelta/($at-$previousAt).TotalSeconds/$logicalProcessors}
        $previousCpu=$currentCpu;$previousAt=$at
        Add-RuntimeObservationSample $observation $at
        $sampleCount++;if($partial){$partialSamples++};$maximumTree=[Math]::Max($maximumTree,$processes.Count)
        $lastScenario=Read-DeviceScenario
        if(-not $scenarioCounts.ContainsKey($lastScenario.State)){$scenarioCounts[$lastScenario.State]=0};$scenarioCounts[$lastScenario.State]++
        foreach($phase in @('All','Steady')){
            if($phase -eq 'Steady' -and $seconds -lt $WarmupSeconds){continue}
            foreach($metric in $metrics){
                Add-Statistics $statistics[($phase+'.Root.'+$metric)] $rootValues[$metric] $seconds
                if(-not $partial){Add-Statistics $statistics[($phase+'.Tree.'+$metric)] $totals[$metric] $seconds}
            }
        }
        $sample=[ordered]@{Index=$sampleCount;AtUtc=$at.ToString('o');ElapsedSeconds=$seconds;
            Phase=$(if($seconds -lt $WarmupSeconds){'warmup'}else{'steady'});PartialTree=$partial;
            Root=$rootValues;Tree=$totals;Processes=$processes.ToArray();DeviceScenario=$lastScenario}
        if($rowsInFile -ge $RowsPerFile){
            $fileIndex=($fileIndex+1)%$MaxSampleFiles;$rowsInFile=0
            $sampleFile=Join-Path $runDirectory ('samples-'+$fileIndex.ToString('00')+'.jsonl')
            [IO.File]::WriteAllText($sampleFile,'',$utf8)
        }
        [IO.File]::AppendAllText($sampleFile,($sample|ConvertTo-Json -Compress -Depth 9)+[Environment]::NewLine,$utf8);$rowsInFile++
        Save-Summary $false
        if($clock.Elapsed.TotalSeconds -ge $DurationSeconds){$reachedDuration=$true;break}
        # Polling sleeps are capped so exits/version changes are noticed within
        # one sampling interval (30 seconds by default), without a busy loop.
        $remaining=[Math]::Min($IntervalSeconds,$DurationSeconds-$clock.Elapsed.TotalSeconds)
        Start-Sleep -Milliseconds ([int][Math]::Max(1,[Math]::Ceiling(1000*$remaining)))
    }
}catch{$stopReason='sampling_error';$samplingFailure=$_.Exception.Message}
finally{if($stopReason -eq 'duration_reached' -and -not $reachedDuration){$stopReason='interrupted'};Save-Summary $true}
$finalContinuity=Get-RuntimeObservationContinuity $observation ([datetime]::UtcNow) ($reachedDuration -and $stopReason -eq 'duration_reached') $partialSamples
[pscustomobject]@{RunDirectory=$runDirectory;SummaryPath=$summaryPath;StopReason=$stopReason;
    SampleCount=$sampleCount;EightHoursCompleted=($stopReason -eq 'duration_reached' -and $clock.Elapsed.TotalSeconds -ge 28800);
    ContinuousEightHoursObserved=$finalContinuity.ContinuousEightHoursObserved;
    CompleteTreeEightHoursObserved=$finalContinuity.CompleteTreeEightHoursObserved}
if($stopReason -eq 'sampling_error'){throw $samplingFailure}
