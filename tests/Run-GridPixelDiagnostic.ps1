[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$OutputDirectory,
    [ValidateRange(1,180)][int]$TimeoutSeconds=180,
    [ValidateSet('reference','order-control')][string]$CasePlan='reference'
)
$ErrorActionPreference='Stop'
$clock=[Diagnostics.Stopwatch]::StartNew()
$root=Split-Path -Parent $PSScriptRoot
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\','/')
if($output.Length -gt 120){throw 'Use an explicit short OutputDirectory (at most 120 characters).'}
if(Test-Path -LiteralPath $output){
    if(@(Get-ChildItem -LiteralPath $output -Force).Count){throw 'OutputDirectory must be new or empty; prior evidence is never overwritten.'}
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$utf8=New-Object Text.UTF8Encoding($false)
$launched=New-Object Collections.ArrayList
$caseNames=if($CasePlan -eq 'order-control'){@('cold-on','cold-off','cold-off','cold-on')}else{@('cold-off','cold-on','matrix-prefix-off','matrix-prefix-on')}
$cases=@(for($i=0;$i -lt $caseNames.Count;$i++){
    $role=$caseNames[$i];$id=if($CasePlan -eq 'reference'){$role}else{'{0:d2}-{1}' -f ($i+1),$role}
    [ordered]@{id=$id;case=$role;order=($i+1);status='not_run';execution=$null;frames=0;comparisons=0;firstFrame=$null;finalFrameSha256=$null;evidenceError=$null}
})
$summary=[ordered]@{
    schema=1;status='error';outcome='failed';exitCode=2;startedUtc=[DateTime]::UtcNow.ToString('o');elapsedMs=0
    timeoutSeconds=$TimeoutSeconds;maxColdProcesses=4;maxFramesPerProcess=32;maxProcessMilliseconds=35000
    casePlan=$CasePlan;compile=$null;cases=$cases;crossProcess=[ordered]@{status='not_run';path=$null};error=$null
}
function Write-Json([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 20),$utf8)}
function Remaining-Milliseconds {
    # Reserve two seconds for final summaries and process cleanup, inside the overall deadline.
    return [int][Math]::Max(0,[Math]::Floor($TimeoutSeconds*1000-$clock.Elapsed.TotalMilliseconds-2000))
}
function File-Evidence([string]$Path){
    if(Test-Path -LiteralPath $Path -PathType Leaf){
        return [ordered]@{path=$Path;sha256=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash;status='available'}
    }
    return [ordered]@{path=$Path;sha256=$null;status='unavailable'}
}
function Compare-FirstFramePixels([string]$LeftPath,[string]$RightPath,[DateTime]$DeadlineUtc=[DateTime]::MaxValue){
    # Read existing full PNGs only. No rendering, font initialization, cropping or tolerance.
    Add-Type -AssemblyName System.Drawing
    $left=$null;$right=$null;$leftBits=$null;$rightBits=$null;$leftHash=$null;$rightHash=$null
    try{
        $left=[Drawing.Bitmap]::new($LeftPath);$right=[Drawing.Bitmap]::new($RightPath)
        if($left.Width -ne $right.Width -or $left.Height -ne $right.Height){throw 'First-frame PNG dimensions differ.'}
        if(-not (($left.Width -eq 390 -and $left.Height -eq 780) -or ($left.Width -eq 325 -and $left.Height -eq 650))){throw 'First-frame PNG dimensions are outside the two existing diagnostic scales.'}
        $area=[Drawing.Rectangle]::new(0,0,$left.Width,$left.Height)
        $leftBits=$left.LockBits($area,[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $rightBits=$right.LockBits($area,[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $leftHash=[Security.Cryptography.SHA256]::Create();$rightHash=[Security.Cryptography.SHA256]::Create()
        $leftRow=New-Object byte[] ($left.Width*4);$rightRow=New-Object byte[] ($right.Width*4)
        $points=New-Object Collections.ArrayList;$deviceCount=0
        $minX=$left.Width;$minY=$left.Height;$maxX=-1;$maxY=-1
        for($y=0;$y -lt $left.Height;$y++){
            if([DateTime]::UtcNow -ge $DeadlineUtc){throw [TimeoutException]::new('First-frame comparison deadline exhausted.')}
            [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($leftBits.Scan0,$y*$leftBits.Stride),$leftRow,0,$leftRow.Length)
            [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($rightBits.Scan0,$y*$rightBits.Stride),$rightRow,0,$rightRow.Length)
            [void]$leftHash.TransformBlock($leftRow,0,$leftRow.Length,$leftRow,0)
            [void]$rightHash.TransformBlock($rightRow,0,$rightRow.Length,$rightRow,0)
            # Equality of encoded rows is only a fast path; every differing row is inspected pixel by pixel.
            if([Convert]::ToBase64String($leftRow) -ceq [Convert]::ToBase64String($rightRow)){continue}
            for($x=0;$x -lt $left.Width;$x++){
                $offset=$x*4;$a=[BitConverter]::ToUInt32($leftRow,$offset);$b=[BitConverter]::ToUInt32($rightRow,$offset)
                if($a -eq $b){continue}
                [void]$points.Add([ordered]@{x=$x;y=$y;left=$a.ToString('X8');right=$b.ToString('X8')})
                if($x -lt 390 -and $y -lt 150){$deviceCount++}
                $minX=[Math]::Min($minX,$x);$minY=[Math]::Min($minY,$y);$maxX=[Math]::Max($maxX,$x);$maxY=[Math]::Max($maxY,$y)
            }
        }
        [void]$leftHash.TransformFinalBlock([byte[]]@(),0,0);[void]$rightHash.TransformFinalBlock([byte[]]@(),0,0)
        return [ordered]@{
            width=$left.Width;height=$left.Height;fullFrameDifferences=$points.Count;originalDeviceAreaDifferences=$deviceCount
            originalDeviceArea=[ordered]@{x=0;y=0;width=390;height=150}
            originalDeviceAreaFullyCovered=($left.Width -ge 390 -and $left.Height -ge 150)
            bounds=if($points.Count){[ordered]@{minX=$minX;minY=$minY;maxX=$maxX;maxY=$maxY}}else{$null}
            points=@($points.ToArray())
            leftRawHash=[BitConverter]::ToString($leftHash.Hash).Replace('-','');rightRawHash=[BitConverter]::ToString($rightHash.Hash).Replace('-','')
        }
    }finally{
        if($null -ne $leftBits){$left.UnlockBits($leftBits)};if($null -ne $rightBits){$right.UnlockBits($rightBits)}
        if($null -ne $left){$left.Dispose()};if($null -ne $right){$right.Dispose()}
        if($null -ne $leftHash){$leftHash.Dispose()};if($null -ne $rightHash){$rightHash.Dispose()}
    }
}
function Get-FirstFrameInput($Report){
    $frame=$Report.Frames[0]
    if($frame.Number -ne 1 -or $frame.BaselinePng -ne 'first.png' -or -not $Report.Parameters){throw 'Missing first-frame identity or parameters.'}
    $state=[ordered]@{}
    foreach($name in @('Before','After')){
        $raw=[string]$frame.State.$name
        if(-not $raw.StartsWith('utc=') -or $raw.Contains('CAPTURE_FAILED')){throw 'Incomplete first-frame business-state capture.'}
        # Fixtures generate absolute UTCs per process. Keep raw values in result.json;
        # compare every other character, including all recorded freshness/status flags.
        $state[$name]=[regex]::Replace($raw,'\b(utc|fixtureBatteryUtc|lastGoodUtc|fixtureDpi|headsetBatteryUtc|audioUtc|confirmedUtc|batteryConfirmedUtc|batteryMeasuredUtc|dpiUtc)=\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z','$1=<per-process-utc>')
    }
    $businessInput=[ordered]@{parameters=$Report.Parameters}
    foreach($name in @('Scale','Dark','Offset','Width','Height','PixelFormat','DpiX','DpiY','RegionObserver')){
        if($null -eq $frame.$name){throw ('Missing first-frame input: '+$name)}
        $businessInput[$name]=$frame.$name
    }
    $businessInput.state=$state
    # Capture event lists differ by design; the existing core Graphics records must agree.
    $businessInput.graphicsEntry=[regex]::Replace($frame.State.GraphicsEntry,'^\d+ frame-entry ','')
    $businessInput.graphicsExit=[regex]::Replace($frame.State.GraphicsExit,'^\d+ frame-exit ','')
    if(-not $businessInput.graphicsEntry -or -not $businessInput.graphicsExit){throw 'Missing first-frame core Graphics capture.'}
    return $businessInput
}
function Get-CrossProcessOutcome($Pairs){
    if(@($Pairs).Count -ne 6 -or @($Pairs | Where-Object {$_.status -notin @('same','difference','skipped_incompatible_inputs')}).Count -or -not @($Pairs | Where-Object {$_.status -in @('same','difference')}).Count){throw 'Incomplete first-frame pair comparisons.'}
    if(@($Pairs | Where-Object {$_.status -eq 'difference'}).Count){
        return [ordered]@{status='cross_process_difference';outcome='failed';exitCode=1}
    }
    return [ordered]@{status='not_reproduced';outcome='passed';exitCode=0}
}
function Compare-FirstFrames($CompletedCases,[string]$Directory,[string]$Plan,[DateTime]$DeadlineUtc){
    if(@($CompletedCases).Count -ne 4 -or @($CompletedCases | Where-Object {$_.status -ne 'not_reproduced'}).Count){throw 'Cross-process comparison requires four valid completed cases.'}
    $records=@(foreach($item in $CompletedCases){
        $caseDirectory=Join-Path $Directory $item.id
        $report=Get-Content -LiteralPath (Join-Path $caseDirectory 'result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if($report.Case -ne $item.case){throw 'Recorded case role does not match its invocation.'}
        $businessInput=Get-FirstFrameInput $report
        [ordered]@{id=$item.id;role=$item.case;order=$item.order;processId=$report.ProcessId;png=(File-Evidence (Join-Path $caseDirectory 'first.png'));rawSha256=$report.Frames[0].Sha256;input=$businessInput}
    })
    $comparison=[ordered]@{
        schema=1;casePlan=$Plan;status='running';outcome='running';exitCode=2
        inputPolicy='Compare all recorded first-frame business fields and core Graphics; exclude capture/events and their numeric frame-entry/exit prefixes, labels/timings and per-process absolute UTC values only. Raw states remain in each result.json.'
        records=$records;pairs=@();error=$null
    }
    $path=Join-Path $Directory 'first-frame-comparison.json'
    try{
        # Preflight all inputs before reading pixels. Reference includes different scales;
        # those pairs are explicitly ineligible, never treated as visual regressions.
        for($i=0;$i -lt 4;$i++){for($j=$i+1;$j -lt 4;$j++){
            $same=($records[$i].input | ConvertTo-Json -Depth 10 -Compress) -ceq ($records[$j].input | ConvertTo-Json -Depth 10 -Compress)
            $comparison.pairs+=,[ordered]@{id=($records[$i].id+'__'+$records[$j].id);left=$records[$i].id;right=$records[$j].id;inputsMatch=$same;status=if($same){'pending'}else{'skipped_incompatible_inputs'};pixels=$null}
        }}
        if($Plan -eq 'order-control' -and @($comparison.pairs | Where-Object {-not $_.inputsMatch}).Count){throw 'Order-control first-frame business inputs differ; PNGs are not a same-state regression comparison.'}
        $pairIndex=0
        for($i=0;$i -lt 4;$i++){for($j=$i+1;$j -lt 4;$j++){
            $pair=$comparison.pairs[$pairIndex];$pairIndex++
            if(-not $pair.inputsMatch){continue}
            $pair.pixels=Compare-FirstFramePixels $records[$i].png.path $records[$j].png.path $DeadlineUtc
            if($pair.pixels.leftRawHash -cne $records[$i].rawSha256 -or $pair.pixels.rightRawHash -cne $records[$j].rawSha256){throw 'Decoded first-frame pixels do not match the recorded raw hash.'}
            $pair.status=if($pair.pixels.fullFrameDifferences){'difference'}else{'same'}
            Write-Json $path $comparison
        }}
        $outcome=Get-CrossProcessOutcome $comparison.pairs
        foreach($key in @('status','outcome','exitCode')){$comparison[$key]=$outcome[$key]}
    }catch{
        $comparison.status=if($_.Exception -is [TimeoutException]){'timeout'}else{'error'}
        $comparison.outcome='failed';$comparison.exitCode=if($comparison.status -eq 'timeout'){124}else{2};$comparison.error=$_.Exception.Message
        throw
    }finally{Write-Json $path $comparison}
    return $comparison
}
function Run-RecordedProcess([string]$FilePath,[string[]]$NativeArguments,[string]$Directory,[int]$BudgetMs,[string]$Kind){
    if($BudgetMs -lt 1){throw [TimeoutException]::new('Overall diagnostic deadline exhausted before process start.')}
    $quoted=@($NativeArguments | ForEach-Object {
        if($_.Contains('"') -or $_.EndsWith('\')){throw 'Unsupported native argument quoting.'}
        '"'+$_+'"'
    })
    $stdout=Join-Path $Directory 'stdout.txt';$stderr=Join-Path $Directory 'stderr.txt'
    $record=[ordered]@{kind=$Kind;pid=$null;path=$FilePath;budgetMs=$BudgetMs;status='starting';exitCode=$null;startedUtc=[DateTime]::UtcNow.ToString('o');elapsedMs=0;stdout=$stdout;stderr=$stderr}
    $process=$null;$stdoutTask=$null;$stderrTask=$null;$started=$false;$processClock=[Diagnostics.Stopwatch]::StartNew()
    try{
        # Keep the handle returned by Process.Start. Windows PowerShell Start-Process
        # can lose ExitCode for an already-exited fast compiler even after WaitForExit.
        $process=New-Object Diagnostics.Process
        $process.StartInfo.FileName=$FilePath;$process.StartInfo.Arguments=$quoted -join ' '
        $process.StartInfo.WorkingDirectory=$root;$process.StartInfo.UseShellExecute=$false
        $process.StartInfo.CreateNoWindow=$true;$process.StartInfo.WindowStyle='Hidden'
        $process.StartInfo.RedirectStandardOutput=$true;$process.StartInfo.RedirectStandardError=$true
        $started=$process.Start()
        if(-not $started){throw 'Native diagnostic process did not start.'}
        $record.pid=$process.Id;$record.status='running';[void]$launched.Add($record)
        $stdoutTask=$process.StandardOutput.ReadToEndAsync();$stderrTask=$process.StandardError.ReadToEndAsync()
        Write-Json (Join-Path $output 'launched-processes.json') @($launched.ToArray())
        # Only this exact Process object, launched and recorded here, may be terminated.
        if(-not $process.WaitForExit([Math]::Max(1,$BudgetMs-[int]$processClock.ElapsedMilliseconds))){
            $record.status='timeout';$process.Kill();[void]$process.WaitForExit(1000)
            $record.exitCode=124
        }else{
            $record.exitCode=$process.ExitCode;$record.status=if($process.ExitCode -eq 0){'passed'}else{'failed'}
        }
    }finally{
        if($started){
            if(-not $process.HasExited){$process.Kill();[void]$process.WaitForExit(1000)}
            foreach($capture in @(@($stdoutTask,$stdout),@($stderrTask,$stderr))){
                if($null -ne $capture[0] -and $capture[0].Wait(500)){
                    [IO.File]::WriteAllText($capture[1],$capture[0].Result,$utf8)
                }else{
                    [IO.File]::WriteAllText($capture[1],'PROCESS_OUTPUT_INCOMPLETE',$utf8)
                    if($record.status -ne 'timeout'){$record.status='failed';$record.exitCode=2}
                }
            }
        }
        if($null -ne $process){$process.Dispose()}
        $record.elapsedMs=$processClock.Elapsed.TotalMilliseconds
        Write-Json (Join-Path $Directory 'execution.json') $record
        Write-Json (Join-Path $output 'launched-processes.json') @($launched.ToArray())
    }
    return $record
}
try{
    Write-Json (Join-Path $output 'summary.json') $summary
    $compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if(-not (Test-Path -LiteralPath $compiler -PathType Leaf)){throw 'Windows .NET Framework compiler is unavailable.'}
    $sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -Filter '*.cs' -File | Where-Object {$_.Name -notin @('Probe.cs','Tests.cs','AssemblyInfo.cs')} | Sort-Object Name | ForEach-Object {$_.FullName})
    $testSource=Join-Path $PSScriptRoot 'PanelDesignTests.cs'
    $schemaSource=Join-Path $root 'engine\device_pack_schema.json'
    $releasePath=Join-Path $root 'release.json'
    $metadata=Join-Path $output 'AssemblyInfo.diagnostic.cs'
    $release=Get-Content -LiteralPath $releasePath -Raw | ConvertFrom-Json
    if($release.version -notmatch '^[0-9A-Za-z.+-]+$'){throw 'Invalid release version for diagnostic metadata.'}
    [IO.File]::WriteAllText($metadata,('[assembly:System.Reflection.AssemblyInformationalVersion("'+$release.version+'")]'),$utf8)
    $windows=Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $runner=[ordered]@{}
    # Deliberate allowlist: never serialize the whole environment (tokens/credentials).
    foreach($name in @('ImageOS','ImageVersion','RUNNER_OS','RUNNER_ARCH','GITHUB_SHA','GITHUB_REF','GITHUB_EVENT_NAME','GITHUB_RUN_ID','GITHUB_RUN_ATTEMPT')){
        $runner[$name]=[Environment]::GetEnvironmentVariable($name)
    }
    $fonts=[Environment]::GetFolderPath('Fonts')
    $environment=[ordered]@{
        schema=1;windowsBuild=([string]$windows.CurrentBuildNumber+'.'+$windows.UBR);displayVersion=$windows.DisplayVersion;buildLabEx=$windows.BuildLabEx
        reportedOS=[Environment]::OSVersion.ToString();clr=[Environment]::Version.ToString();x64=[Environment]::Is64BitProcess
        runner=$runner;compiler=(File-Evidence $compiler)
        sources=@(@($sources)+@($testSource,$schemaSource,$releasePath,$metadata,$PSCommandPath,(Join-Path $root '.github\workflows\ci.yml')) | ForEach-Object {File-Evidence $_})
        fonts=@('segoeui.ttf','segoeuib.ttf','msyh.ttc','msyhbd.ttc','msyhl.ttc' | ForEach-Object {File-Evidence (Join-Path $fonts $_)})
    }
    Write-Json (Join-Path $output 'environment.json') $environment
    $binary=Join-Path $output 'PanelDesignTests.exe'
    $compileDirectory=Join-Path $output 'compile';New-Item -ItemType Directory -Path $compileDirectory | Out-Null
    $compileArguments=@('/nologo','/target:exe','/platform:x64','/optimize+','/main:PanelDesignTests','/reference:System.Runtime.Serialization.dll','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:Accessibility.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll',('/resource:'+$schemaSource+',DevicePackSchema'),('/out:'+$binary))+$sources+@($metadata,$testSource)
    $compileBudget=[Math]::Min(30000,(Remaining-Milliseconds))
    $summary.compile=Run-RecordedProcess $compiler $compileArguments $compileDirectory $compileBudget 'compile'
    if($summary.compile.status -eq 'timeout'){throw [TimeoutException]::new('Diagnostic compilation timed out.')}
    if($summary.compile.exitCode -ne 0){throw 'Diagnostic compilation failed; see compile/stdout.txt and stderr.txt.'}
    Write-Json (Join-Path $output 'executable.json') (File-Evidence $binary)
    $summary.status='running';$summary.outcome='running';$summary.exitCode=0
    for($index=0;$index -lt $cases.Count;$index++){
        $case=$cases[$index];$directory=Join-Path $output $case.id;New-Item -ItemType Directory -Path $directory | Out-Null
        # Share remaining time across pending cases; no process receives more than 35 s.
        $budget=[Math]::Min(35000,[Math]::Floor((Remaining-Milliseconds)/($cases.Count-$index)))
        if($budget -le 1000){$case.status='timeout';throw [TimeoutException]::new('Insufficient overall diagnostic budget for next cold process.')}
        $arguments=@('--grid-repaint-probe','--case',$case.case,'--output',$directory,'--budget-ms',([string]($budget-1000)))
        $execution=Run-RecordedProcess $binary $arguments $directory $budget $case.id
        $case.execution=$execution
        $resultPath=Join-Path $directory 'result.json'
        $report=$null
        if(Test-Path -LiteralPath $resultPath){
            try{$report=Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json}
            catch{$case.evidenceError=$_.Exception.Message} # A killed writer must still classify as timeout.
        }
        if($null -ne $report){
            $case.frames=@($report.Frames).Count;$case.comparisons=@($report.Frames | ForEach-Object {$_.Comparisons}).Count
            $case.firstFrame=if($case.frames){$report.Frames[0].BaselinePng}else{$null};$case.finalFrameSha256=$report.FinalFrameSha256
        }
        if($execution.status -eq 'timeout' -or $execution.exitCode -eq 124){
            $case.status='timeout';$summary.status='timeout';$summary.outcome='failed';$summary.exitCode=124
        }elseif($null -eq $report){
            $case.status='error';$summary.status='error';$summary.outcome='failed';$summary.exitCode=2
        }elseif($report.Status -eq 'difference'){
            $case.status='difference';$summary.status='difference';$summary.outcome='failed';$summary.exitCode=1
        }elseif($execution.exitCode -ne 0 -or $report.Status -ne 'not_reproduced'){
            $case.status='error';$summary.status='error';$summary.outcome='failed';$summary.exitCode=2
        }else{
            $expected=if($case.case.StartsWith('cold-')){32}else{30}
            $comparisons=@($report.Frames | ForEach-Object {$_.Comparisons})
            $firstCompared=@($comparisons | Where-Object {$_.LeftFrame -eq 1 -or $_.RightFrame -eq 1}).Count -gt 0
            $coreMissing=@($report.Frames | Where-Object {-not $_.State.GraphicsEntry -or -not $_.State.GraphicsExit}).Count -gt 0
            if($case.frames -ne $expected -or $report.DrawsStarted -gt 32 -or -not $firstCompared -or $coreMissing -or @($comparisons | Where-Object {-not $_.Same}).Count -or -not (Test-Path -LiteralPath (Join-Path $directory 'first.png')) -or $report.FinalFrameSha256 -ne $report.Frames[-1].Sha256){
                $case.status='error';throw ('Incomplete or inconsistent successful case evidence: '+$case.case)
            }
            $case.status='not_reproduced'
        }
        Write-Json (Join-Path $directory 'case-summary.json') $case
        Write-Json (Join-Path $output 'summary.json') $summary
        if($summary.exitCode -ne 0){break} # Never retry or replace a differing frame with a later match.
    }
    if($summary.exitCode -eq 0){
        if(@($cases | Where-Object {$_.status -ne 'not_reproduced'}).Count){throw 'Not every diagnostic case completed.'}
        $summary.crossProcess.status='running';$summary.crossProcess.path=Join-Path $output 'first-frame-comparison.json'
        $cross=Compare-FirstFrames $cases $output $CasePlan ([DateTime]::UtcNow.AddMilliseconds((Remaining-Milliseconds)))
        $summary.crossProcess.status=$cross.status
        foreach($key in @('status','outcome','exitCode')){$summary[$key]=$cross[$key]}
    }
    if($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds){throw [TimeoutException]::new('Overall diagnostic deadline exceeded.')}
}catch [TimeoutException]{
    if($summary.crossProcess.status -eq 'running'){$summary.crossProcess.status='timeout'}
    $summary.status='timeout';$summary.outcome='failed';$summary.exitCode=124;$summary.error=$_.Exception.Message
}catch{
    if($summary.crossProcess.status -eq 'running'){$summary.crossProcess.status='error'}
    $summary.status='error';$summary.outcome='failed';$summary.exitCode=2;$summary.error=$_.Exception.Message
}finally{
    # All native processes are already waited/killed in their own finally; no process-tree cleanup.
    $summary.elapsedMs=$clock.Elapsed.TotalMilliseconds
    if($summary.elapsedMs -ge $TimeoutSeconds*1000 -and $summary.status -eq 'not_reproduced'){$summary.status='timeout';$summary.outcome='failed';$summary.exitCode=124}
    foreach($case in $cases){
        $directory=Join-Path $output $case.id
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        Write-Json (Join-Path $directory 'case-summary.json') $case
    }
    Write-Json (Join-Path $output 'summary.json') $summary
}
Write-Output ('GRID_PIXEL_DIAGNOSTIC status='+$summary.status+' outcome='+$summary.outcome+' elapsedMs='+[Math]::Round($summary.elapsedMs)+' summary='+(Join-Path $output 'summary.json'))
exit $summary.exitCode
