param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$project=Split-Path -Parent $PSScriptRoot
. (Join-Path $project 'scripts\Install.Common.ps1')
$script:passed=0
$script:disabledForTest=$false
$script:unknownForTest=$false
$script:iconRefreshPaths=@();$script:iconRefreshCount=0;$script:failIconRefresh=$false
# All folders and launched children belong to this fixture. No real registry or desktop notification access.
function Get-DeviceBatteryStartupApproval([string]$Name) {
    if($script:disabledForTest){return ,([byte[]]@(3,0,0,0))}
    if($script:unknownForTest){return ,([byte[]]@(255,0,0,0))}
    return $null
}
function Update-DeviceBatteryShellIcons([string]$Executable,[string]$StartupShortcut,[string]$ManualShortcut) {
    $script:iconRefreshPaths=@($Executable,$StartupShortcut,$ManualShortcut);$script:iconRefreshCount++
    if($script:failIconRefresh){throw 'Isolated shell notification failure.'}
}
function Assert([bool]$Condition,[string]$Name){if(-not $Condition){throw ('FAIL '+$Name)};$script:passed++}
function Assert-Rejected([scriptblock]$Operation,[string]$Name){$failed=$false;try{& $Operation|Out-Null}catch{$failed=$true};Assert $failed $Name}
function Stop-Fixture($Process) {if($Process -and -not $Process.HasExited){$Process.Kill();$Process.WaitForExit(5000)|Out-Null}}
function Refresh-FixtureManifest {$null=New-DeviceBatteryPackageManifest $source}
function Test-ManualInstallerStop {
    $manualRoot=Join-Path $fixtureRoot 'manual stop fixtures'
    $manualPackage=Join-Path $manualRoot 'package'
    New-Item -ItemType Directory -Path $manualRoot -Force|Out-Null
    Copy-Item -LiteralPath $source -Destination $manualPackage -Recurse
    $manualTarget=Join-Path $manualRoot 'installed program'
    $manualData=Join-Path $manualRoot 'data'
    $manualStartup=Join-Path $manualRoot 'Startup';$manualPrograms=Join-Path $manualRoot 'Programs'
    $manualState=[pscustomobject]@{InstallDirectory=$manualTarget;Running=$false;Interactive=$true;Answer='Y';PromptCount=0;
        InteractiveChecks=0;ExitCalls=0;ExitRequested=$false;ExitSucceeds=$false;ExitPolls=0;ForcedStops=0;ExitPath='';ExitArguments='';Prompt=''}
    # Only this copied package sees fake processes, registry probes and shell notifications.
    # Even a regressed force-stop attempt is trapped without touching a real process.
    [IO.File]::AppendAllText((Join-Path $manualPackage 'scripts\Install.Common.ps1'),@'

function Get-DeviceBatteryStartupApproval([string]$Name){return $null}
function Update-DeviceBatteryShellIcons([string]$Executable,[string]$StartupShortcut,[string]$ManualShortcut){}
function Get-DeviceBatteryOwnedProcesses([string]$InstallDirectory) {
    if($InstallDirectory -ine $manualState.InstallDirectory){throw 'FAIL process discovery used a different installation root'}
    if($manualState.ExitRequested -and $manualState.ExitSucceeds) {
        $manualState.ExitPolls++
        if($manualState.ExitPolls -ge 2){$manualState.Running=$false}
    }
    if($manualState.Running) {
        foreach($relative in @('outputs\AtkMouseBattery.exe','outputs\AtkMouseBattery.exe','engine\runtime\python.exe')) {
            $fake=[pscustomobject]@{Id=2147483647;Path=(Join-Path $InstallDirectory $relative);State=$manualState}
            $fake|Add-Member -MemberType ScriptMethod -Name Kill -Value {$this.State.ForcedStops++;throw 'FAIL process Kill is forbidden'}
            $fake
        }
    }
}
function Start-Process {
    param([string]$FilePath,[string]$ArgumentList,[string]$WorkingDirectory,[string]$WindowStyle)
    $manualState.ExitCalls++;$manualState.ExitPath=$FilePath;$manualState.ExitArguments=$ArgumentList
    if($FilePath -ine (Join-Path $manualState.InstallDirectory 'outputs\AtkMouseBattery.exe') -or
        $ArgumentList -cne '--exit' -or $WorkingDirectory -ine (Split-Path -Parent $FilePath) -or $WindowStyle -cne 'Hidden'){
        throw 'FAIL exit request must use only the installed executable with --exit'
    }
    $manualState.ExitRequested=$true
}
function Stop-Process {
    param($InputObject,$Id,$Name,[switch]$Force)
    $manualState.ForcedStops++;throw 'FAIL Stop-Process is forbidden'
}
'@,(New-Object Text.UTF8Encoding($false)))
    $null=New-DeviceBatteryPackageManifest $manualPackage
    $entry=Join-Path $manualPackage 'scripts\Install-Startup.ps1'
    $entryPaths=@{InstallDirectory=$manualTarget;DataDirectory=$manualData;StartupDirectory=$manualStartup;ProgramsDirectory=$manualPrograms}
    $hooks=@{
        CanPrompt={$manualState.InteractiveChecks++;return $manualState.Interactive}
        ConfirmStopRunning={param($Message) $manualState.PromptCount++;$manualState.Prompt=$Message;return $manualState.Answer}
        StopTimeoutMilliseconds=2000
    }
    function Get-ManualInstallSnapshot {
        return (@(foreach($root in @($manualTarget,$manualData,$manualStartup,$manualPrograms)) {
            foreach($file in @(Get-ChildItem -LiteralPath $root -Recurse -File -Force|Sort-Object FullName)) {
                $file.FullName+'|'+(Get-DeviceBatteryFileHash $file.FullName)
            }
        }) -join "`n")
    }
    function Assert-ManualInstallRejected([scriptblock]$Operation,[string]$Expected,[string]$Name) {
        $before=Get-ManualInstallSnapshot;$failure=$null
        try {& $Operation|Out-Null}catch{$failure=$_.Exception.Message}
        Assert ($failure -ceq $Expected) ($Name+' reports the exact Chinese failure')
        Assert ((Get-ManualInstallSnapshot) -ceq $before) ($Name+' preserves all installed files, receipts, settings and shortcuts')
        Assert ($manualState.Running -and $manualState.ForcedStops -eq 0) ($Name+' never force-stops any fixture process')
    }
    $result=& $entry @entryPaths @hooks -StopRunning
    Assert ((Test-Path -LiteralPath $result.InstalledExecutable) -and $result.PayloadChanged) 'manual install with no running process succeeds'
    Assert ($manualState.PromptCount -eq 0 -and $manualState.InteractiveChecks -eq 0 -and $manualState.ExitCalls -eq 0) 'stopped manual install never asks for confirmation or requests exit'
    [IO.File]::WriteAllText((Join-Path $manualData 'audio-settings.json'),'{"keep":"manual fixture settings"}')
    [IO.File]::WriteAllText((Join-Path $manualPackage 'engine\providers\base.py'),'# manual candidate two')
    $null=New-DeviceBatteryPackageManifest $manualPackage
    $cancelled="已取消。请先在托盘图标上右键选择‘退出’，再重新运行安装。"
    $manualState.Running=$true
    foreach($answer in @('N','other','')) {
        $manualState.Answer=$answer;$manualState.PromptCount=0
        Assert-ManualInstallRejected {& $entry @entryPaths @hooks -StopRunning} $cancelled ('manual answer ['+$answer+']')
        Assert ($manualState.PromptCount -eq 1 -and $manualState.ExitCalls -eq 0) 'declining or invalid confirmation never sends an exit request'
    }
    $manualState.Interactive=$false;$manualState.PromptCount=0
    Assert-ManualInstallRejected {& $entry @entryPaths @hooks -StopRunning} $cancelled 'non-interactive input'
    Assert ($manualState.PromptCount -eq 0 -and $manualState.ExitCalls -eq 0) 'non-interactive input never prompts or requests exit'
    $manualState.Interactive=$true;$manualState.Answer='Y';$hooks.StopTimeoutMilliseconds=25
    $timeoutClock=[Diagnostics.Stopwatch]::StartNew()
    Assert-ManualInstallRejected {& $entry @entryPaths @hooks -StopRunning} "程序没有在 20 秒内退出。请在任务栏托盘图标上右键选择‘退出’，然后重新运行安装。" 'normal-exit timeout'
    Assert ($manualState.ExitCalls -eq 1 -and $manualState.ForcedStops -eq 0 -and $timeoutClock.ElapsedMilliseconds -lt 5000) 'injected short timeout sends exactly one normal-exit request without killing'
    $manualState.PromptCount=0;$manualState.ExitCalls=0;$manualState.InteractiveChecks=0;$manualState.ExitRequested=$false
    Assert-ManualInstallRejected {& $entry @entryPaths @hooks} "MyFinder 设备电量仍在运行。请在任务栏托盘图标上右键选择‘退出’（读取组件会一并关闭），然后重新运行安装。" 'entry point without StopRunning'
    Assert ($manualState.PromptCount -eq 0 -and $manualState.ExitCalls -eq 0 -and $manualState.InteractiveChecks -eq 0) 'programmatic update entry preserves its non-interactive running-process rejection'
    # Verify the default input guard in a real redirected host, with and without -NonInteractive.
    $probePath=Join-Path $manualRoot 'noninteractive.ps1'
    [IO.File]::WriteAllText($probePath,@'
param($Package,$InstallDirectory,$DataDirectory,$StartupDirectory,$ProgramsDirectory)
$ErrorActionPreference='Stop'
$manualState=[pscustomobject]@{InstallDirectory=$InstallDirectory;Running=$true;ExitRequested=$false;ExitSucceeds=$false;PromptCount=0;ExitCalls=0;ForcedStops=0}
$failure=$null
try {
    & (Join-Path $Package 'scripts\Install-Startup.ps1') -InstallDirectory $InstallDirectory -DataDirectory $DataDirectory -StartupDirectory $StartupDirectory -ProgramsDirectory $ProgramsDirectory -StopRunning -ConfirmStopRunning {$manualState.PromptCount++;return 'Y'}|Out-Null
}catch{$failure=$_.Exception.Message}
if($failure -cne "已取消。请先在托盘图标上右键选择‘退出’，再重新运行安装。" -or
    $manualState.PromptCount -ne 0 -or $manualState.ExitCalls -ne 0 -or $manualState.ForcedStops -ne 0){throw ('FAIL non-interactive host: '+$failure)}
'@,(New-Object Text.UTF8Encoding($true)))
    foreach($nonInteractive in @($false,$true)) {
        $before=Get-ManualInstallSnapshot
        $info=New-Object Diagnostics.ProcessStartInfo
        $info.FileName=(Get-Process -Id $PID).Path
        $probeArguments=@('-NoProfile','-ExecutionPolicy','Bypass')
        if($nonInteractive){$probeArguments+='-NonInteractive'}
        $probeArguments+=@('-File',$probePath,$manualPackage,$manualTarget,$manualData,$manualStartup,$manualPrograms)
        $info.Arguments=(@($probeArguments|ForEach-Object {'"'+$_+'"'}) -join ' ')
        $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden'
        $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
        $probe=[Diagnostics.Process]::Start($info)
        try {
            $probe.StandardInput.Close();$output=$probe.StandardOutput.ReadToEndAsync();$errors=$probe.StandardError.ReadToEndAsync()
            $finished=$probe.WaitForExit(30000)
            Assert ($finished -and $probe.ExitCode -eq 0) ('default input guard cancels without prompting; NonInteractive='+$nonInteractive+'; '+$errors.Result)
        }finally{$probe.Dispose()}
        Assert ((Get-ManualInstallSnapshot) -ceq $before) 'default redirected-host rejection leaves all installed files unchanged'
    }
    $hooks.StopTimeoutMilliseconds=2000
    foreach($answer in @('Y','y')) {
        [IO.File]::WriteAllText((Join-Path $manualPackage 'engine\providers\base.py'),('# confirmed candidate '+$answer))
        $null=New-DeviceBatteryPackageManifest $manualPackage
        $manualState.Running=$true;$manualState.Answer=$answer;$manualState.ExitSucceeds=$true;$manualState.ExitRequested=$false
        $manualState.PromptCount=0;$manualState.ExitCalls=0;$manualState.ExitPolls=0
        # A canonical alias and a receipt-selected root must request exit from the same owned installation.
        if($answer -ceq 'Y'){$entryPaths.InstallDirectory=$manualTarget+'\.'}else{$entryPaths.Remove('InstallDirectory')}
        $result=& $entry @entryPaths @hooks -StopRunning
        Assert ($result.PayloadChanged -and -not $manualState.Running -and $manualState.ExitPolls -ge 2) ('answer '+$answer+' waits for both fake UIs and the portable reader before installing')
        Assert ($manualState.PromptCount -eq 1 -and $manualState.ExitCalls -eq 1 -and $manualState.ExitPath -ceq (Join-Path $manualTarget 'outputs\AtkMouseBattery.exe') -and $manualState.ExitArguments -ceq '--exit') 'normal exit runs only the canonical installed executable once'
        Assert ($manualState.Prompt -ceq '检测到 MyFinder 设备电量正在运行，安装需要先关闭它。是否自动关闭并继续安装？[Y/N]') 'manual confirmation uses the requested Chinese prompt'
        Assert ((Get-DeviceBatteryFileHash (Join-Path $manualTarget 'engine\providers\base.py')) -ceq (Get-DeviceBatteryFileHash (Join-Path $manualPackage 'engine\providers\base.py'))) 'confirmed normal exit allows the changed payload to install'
        Assert ($manualState.ForcedStops -eq 0) 'confirmed normal exit never force-stops a process'
    }
}
$fixtureRoot=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('installer-'+$PSVersionTable.PSVersion.Major+'-'+[Guid]::NewGuid().ToString('N'))
# Exercise Unicode and spaces without making script decoding depend on host encoding.
$unicode=([string][char]0x4e2d)+[char]0x6587+' package'
$source=Join-Path $fixtureRoot $unicode
$target=Join-Path $fixtureRoot (([string][char]0x7a33)+[char]0x5b9a+' installed program')
$data=Join-Path $fixtureRoot 'user data'
$startup=Join-Path $fixtureRoot 'fake Startup'
$programs=Join-Path $fixtureRoot 'fake Programs'
New-Item -ItemType Directory -Path (Join-Path $source 'scripts'),(Join-Path $source 'outputs'),$startup,$programs -Force|Out-Null
foreach($name in @('Install.Common.ps1','Manifest.Common.ps1','Install-Startup.ps1','Uninstall-Startup.ps1')){
    Copy-Item -LiteralPath (Join-Path $project ('scripts\'+$name)) -Destination (Join-Path $source ('scripts\'+$name))
}
foreach($name in @('release.json','LICENSE','THIRD_PARTY_NOTICES.md')){
    Copy-Item -LiteralPath (Join-Path $project $name) -Destination (Join-Path $source $name)
}
$fixtureSource=Join-Path $fixtureRoot 'FixtureHost.cs'
[IO.File]::WriteAllText($fixtureSource,('[assembly:System.Reflection.AssemblyInformationalVersion("'+(Read-DeviceBatteryRelease $source).version+'")] class FixtureHost { [System.STAThread] static void Main() { System.Threading.Thread.Sleep(120000); } }'))
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:x64 ("/out:"+(Join-Path $source 'outputs\AtkMouseBattery.exe')) $fixtureSource
if($LASTEXITCODE -ne 0){throw 'Isolated installer process fixture failed to build.'}
$arguments=@{SourceRoot=$source;InstallDirectory=$target;DataDirectory=$data;StartupDirectory=$startup;ProgramsDirectory=$programs}
$correct=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('TXlGaW5kZXIg6K6+5aSH55S16YePLmxuaw=='))
$shell=New-DeviceBatteryShortcutShell
$uiProcess=$null;$pythonProcess=$null
try {
    Assert ((Get-DeviceBatteryShortcutName) -ceq $correct) 'shortcut names survive Windows PowerShell and PowerShell 7 encoding'
    Assert-Rejected {Install-DeviceBattery @arguments} 'missing manifest rejects an incomplete package before mutation'
    Assert (-not (Test-Path -LiteralPath $target)) 'incomplete package creates no installation directory'
    $engine=Join-Path $source 'engine'
    New-Item -ItemType Directory -Path (Join-Path $engine 'providers'),(Join-Path $engine 'runtime') -Force|Out-Null
    [IO.File]::WriteAllText((Join-Path $engine 'server.py'),'# isolated fixture; never used as package smoke')
    [IO.File]::WriteAllText((Join-Path $engine 'providers\base.py'),'# version one')
    [IO.File]::WriteAllText((Join-Path $engine 'providers\retired.py'),'# obsolete owned file')
    [IO.File]::WriteAllText((Join-Path $engine 'providers\retired-user-edit.py'),'# obsolete later edited by user')
    Assert-Rejected {Refresh-FixtureManifest} 'manifest validation requires the portable runtime'
    Copy-Item -LiteralPath (Join-Path $source 'outputs\AtkMouseBattery.exe') -Destination (Join-Path $engine 'runtime\python.exe')
    Refresh-FixtureManifest
    # Unlisted development files are not installed even if present next to the package.
    New-Item -ItemType Directory -Path (Join-Path $engine 'tests')|Out-Null
    [IO.File]::WriteAllText((Join-Path $engine 'tests\private-fixture.py'),'private fixture')
    $bad=[Text.Encoding]::GetEncoding(936).GetString([Text.Encoding]::UTF8.GetBytes($correct))
    $legacy=$shell.CreateShortcut((Join-Path $startup $bad))
    $legacy.TargetPath=Join-Path $source 'outputs\AtkMouseBattery.exe'
    $legacy.Description='Start the MyFinder mouse and headset battery indicator quietly at user login.';$legacy.Save()
    $installed=Install-DeviceBattery @arguments
    Assert (Test-Path -LiteralPath $installed.InstalledExecutable) 'Unicode and spaces package installs successfully'
    Assert (Test-Path -LiteralPath (Join-Path $target 'engine\runtime\python.exe')) 'the package-owned portable runtime is copied'
    Assert (-not (Test-Path -LiteralPath (Join-Path $target 'engine\tests\private-fixture.py'))) 'unlisted files never become installed payload'
    Assert (-not (Test-Path -LiteralPath (Join-Path $startup $bad))) 'owned legacy garbled shortcut is migrated'
    Assert (@(Get-ChildItem -LiteralPath $startup -Filter '*.lnk').Count -eq 1) 'exactly one startup entry remains'
    Assert ($shell.CreateShortcut((Join-Path $startup $correct)).TargetPath -ieq $installed.InstalledExecutable) 'startup points to the installed executable'
    Assert ($shell.CreateShortcut((Join-Path $programs $correct)).Arguments -ceq '--show') 'manual launcher opens the panel'
    Assert ($shell.CreateShortcut((Join-Path $startup $correct)).IconLocation -ceq ($installed.InstalledExecutable+',0')) 'startup uses the installed executable icon'
    Assert ($shell.CreateShortcut((Join-Path $programs $correct)).IconLocation -ceq ($installed.InstalledExecutable+',0')) 'manual launcher uses the installed executable icon'
    Assert ($script:iconRefreshPaths.Count -eq 3 -and $script:iconRefreshPaths[0] -ceq $installed.InstalledExecutable -and $script:iconRefreshPaths[1] -ceq $installed.StartupShortcut -and $script:iconRefreshPaths[2] -ceq $installed.ManualShortcut) 'shell refresh targets only the installed executable and its two shortcuts'
    $receipt=Read-DeviceBatteryReceipt $data
    Assert ($receipt.Schema -eq 2 -and $receipt.Version -ceq (Read-DeviceBatteryRelease $source).version) 'receipt records version and schema'
    Assert (@($receipt.Files | Where-Object {$_.path -eq 'engine/providers/base.py' -and $_.sha256.Length -eq 64}).Count -eq 1) 'receipt owns each file with SHA256'
    Assert ((Get-DeviceBatteryStartupState $startup $installed.InstalledExecutable).Status -ceq 'enabled') 'startup diagnostics inspect target and arguments'
    $testLink=$shell.CreateShortcut((Join-Path $startup $correct));$testLink.Arguments='--show';$testLink.Save()
    Assert ((Get-DeviceBatteryStartupState $startup $installed.InstalledExecutable).Status -ceq 'invalid-target') 'startup diagnostics reject non-quiet arguments'
    $testLink.Arguments='';$testLink.Save()
    $script:disabledForTest=$true
    Assert ((Get-DeviceBatteryStartupState $startup $installed.InstalledExecutable).Status -ceq 'disabled') 'startup diagnostics report Windows disabled state'
    $script:disabledForTest=$false;$script:unknownForTest=$true
    Assert ((Get-DeviceBatteryStartupState $startup $installed.InstalledExecutable).Status -ceq 'unknown') 'unrecognized StartupApproved state stays unknown'
    $script:unknownForTest=$false
    Test-ManualInstallerStop
    $uiProcess=Start-Process -FilePath $installed.InstalledExecutable -WindowStyle Hidden -PassThru
    Assert (-not $uiProcess.HasExited) 'isolated real process fixture is running'
    $script:failIconRefresh=$true
    $result=Install-DeviceBattery @arguments
    $script:failIconRefresh=$false
    Assert (-not $result.PayloadChanged) 'identical payload may safely re-register shortcuts while active'
    Assert ($result.InstalledExecutable -ceq $installed.InstalledExecutable) 'shell refresh failure does not roll back a successful registration'
    $oldReceipt=[IO.File]::ReadAllText((Join-Path $data 'install.json'))
    $oldEngineHash=(Get-FileHash -LiteralPath (Join-Path $target 'engine\providers\base.py')).Hash
    [IO.File]::WriteAllText((Join-Path $engine 'providers\base.py'),'# version two')
    Remove-Item -LiteralPath (Join-Path $engine 'providers\retired.py'),(Join-Path $engine 'providers\retired-user-edit.py') -Force
    [IO.File]::WriteAllText((Join-Path $target 'engine\providers\retired-user-edit.py'),'user content must survive')
    # Keep development fixture out of the newly generated test manifest.
    Remove-Item -LiteralPath (Join-Path $engine 'tests\private-fixture.py') -Force
    Refresh-FixtureManifest
    Assert ((Get-FileHash -LiteralPath (Join-Path $source 'outputs\AtkMouseBattery.exe')).Hash -eq (Get-FileHash -LiteralPath $installed.InstalledExecutable).Hash) 'engine-only regression uses an identical real executable'
    Assert-Rejected {Install-DeviceBattery @arguments} 'same-exe changed-engine update rejects a running installed UI before copying'
    Assert ((Get-FileHash -LiteralPath (Join-Path $target 'engine\providers\base.py')).Hash -eq $oldEngineHash) 'running-UI rejection preserves the entire old engine'
    Assert ([IO.File]::ReadAllText((Join-Path $data 'install.json')) -ceq $oldReceipt) 'running-UI rejection preserves the receipt'
    Stop-Fixture $uiProcess;$uiProcess=$null
    $pythonProcess=Start-Process -FilePath (Join-Path $target 'engine\runtime\python.exe') -WindowStyle Hidden -PassThru
    Assert-Rejected {Install-DeviceBattery @arguments} 'orphan portable worker blocks an engine-only update before copying'
    Assert ((Get-FileHash -LiteralPath (Join-Path $target 'engine\providers\base.py')).Hash -eq $oldEngineHash) 'worker rejection preserves installed bytes'
    Stop-Fixture $pythonProcess;$pythonProcess=$null
    # Model a short-lived independent reader, without changing the installer's
    # process discovery or its file-open probe. The original one-shot check fails.
    if(-not ('InstallerTests.TimedPayloadReader' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
namespace InstallerTests {
    public sealed class TimedPayloadReader : IDisposable {
        readonly FileStream stream;
        readonly Thread worker;
        public TimedPayloadReader(string path) {
            stream=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            worker=new Thread(delegate(){Thread.Sleep(500);stream.Dispose();});worker.Start();
        }
        public void Dispose(){worker.Join();stream.Dispose();}
    }
}
'@
    }
    $probePath=Join-Path $target 'engine\providers\base.py'
    $transientReader=New-Object InstallerTests.TimedPayloadReader $probePath
    try {Assert-DeviceBatteryStopped $target @($probePath)}finally{$transientReader.Dispose()}
    Assert ((Get-FileHash -LiteralPath $probePath).Hash -eq $oldEngineHash) 'transient reader releases within bounded preflight without changing payload bytes'
    $held=[IO.File]::Open((Join-Path $target 'engine\providers\base.py'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {Assert-Rejected {Install-DeviceBattery @arguments} 'a locked changed engine file is rejected before target mutation'}finally{$held.Dispose()}
    $originalAttributes=[IO.File]::GetAttributes($probePath)
    try {
        [IO.File]::SetAttributes($probePath,($originalAttributes -bor [IO.FileAttributes]::ReadOnly))
        $denied=$null
        try {Assert-DeviceBatteryStopped $target @($probePath)}catch{$denied=$_.Exception}
        Assert ($null -ne $denied -and ($denied.GetBaseException().HResult -band 0xffff) -eq 5 -and $denied.Message.Contains($probePath)) 'read-only payload still fails with its exact path and original access-denied cause'
    }finally{[IO.File]::SetAttributes($probePath,$originalAttributes)}
    $settings=Join-Path $data 'audio-settings.json'
    [IO.File]::WriteAllText($settings,'{"keep":"user audio settings"}')
    $oldExeHash=(Get-FileHash -LiteralPath $installed.InstalledExecutable).Hash
    $oldMarker=[IO.File]::ReadAllText((Join-Path $target '.myfinder-device-battery-install.json'))
    $oldStartupHash=(Get-FileHash -LiteralPath (Join-Path $startup $correct)).Hash
    $candidateExe=Join-Path $source 'outputs\AtkMouseBattery.exe'
    [IO.File]::WriteAllBytes($candidateExe,([IO.File]::ReadAllBytes($candidateExe)+[byte[]]@(9,8,7)))
    Refresh-FixtureManifest
    # A late shortcut failure occurs after the candidate has copied. Roll back both binaries and metadata.
    $block=Join-Path $fixtureRoot 'not a directory';[IO.File]::WriteAllText($block,'block')
    $failing=@{};foreach($key in $arguments.Keys){$failing[$key]=$arguments[$key]};$failing.ProgramsDirectory=$block
    $refreshCountBeforeFailure=$script:iconRefreshCount
    Assert-Rejected {Install-DeviceBattery @failing} 'late shortcut failure aborts the transaction'
    Assert ($script:iconRefreshCount -eq $refreshCountBeforeFailure) 'failed transactions never notify the shell about candidate icons'
    Assert ((Get-FileHash -LiteralPath (Join-Path $target 'engine\providers\base.py')).Hash -eq $oldEngineHash) 'late failure restores the old engine exactly'
    Assert ((Get-FileHash -LiteralPath $installed.InstalledExecutable).Hash -eq $oldExeHash) 'late failure preserves the old executable exactly'
    Assert ([IO.File]::ReadAllText((Join-Path $data 'install.json')) -ceq $oldReceipt) 'late failure restores user receipt bytes'
    Assert ([IO.File]::ReadAllText((Join-Path $target '.myfinder-device-battery-install.json')) -ceq $oldMarker) 'late failure restores ownership marker bytes'
    Assert (Test-Path -LiteralPath (Join-Path $target 'engine\providers\retired.py')) 'late failure restores removed obsolete owned files'
    Assert ((Get-FileHash -LiteralPath (Join-Path $startup $correct)).Hash -eq $oldStartupHash) 'late failure restores the exact prior startup shortcut'
    Assert ([IO.File]::ReadAllText($settings) -ceq '{"keep":"user audio settings"}') 'update rollback does not migrate or change user settings'
    Install-DeviceBattery @arguments|Out-Null
    Assert ((Get-FileHash -LiteralPath (Join-Path $target 'engine\providers\base.py')).Hash -ne $oldEngineHash) 'stopped engine-only update succeeds'
    Assert (-not (Test-Path -LiteralPath (Join-Path $target 'engine\providers\retired.py'))) 'successful update removes only unchanged obsolete owned files'
    Assert ([IO.File]::ReadAllText((Join-Path $target 'engine\providers\retired-user-edit.py')) -ceq 'user content must survive') 'successful update preserves edited obsolete files'
    $registered=@{};foreach($key in $arguments.Keys){$registered[$key]=$arguments[$key]};$registered.SourceRoot=$target
    Install-DeviceBattery @registered|Out-Null
    Assert (@(Get-ChildItem -LiteralPath $startup -Filter '*.lnk').Count -eq 1) 'owned installation can re-register from its own manifest'
    [IO.File]::WriteAllText((Join-Path $source 'engine\providers\base.py'),'# tampered after manifest')
    Assert-Rejected {Install-DeviceBattery @arguments} 'modified package content fails SHA256 before copying'
    Refresh-FixtureManifest
    $releasePath=Join-Path $source 'release.json';$savedRelease=[IO.File]::ReadAllText($releasePath)
    $wrongVersion=$savedRelease | ConvertFrom-Json;$wrongVersion.version='1.3.0-beta.999'
    [IO.File]::WriteAllText($releasePath,($wrongVersion|ConvertTo-Json))
    Assert-Rejected {Refresh-FixtureManifest} 'a package cannot relabel an unchanged binary with another release version'
    [IO.File]::WriteAllText($releasePath,$savedRelease);Refresh-FixtureManifest
    Uninstall-DeviceBatteryStartup -SourceRoot $source -DataDirectory $data -StartupDirectory $startup -ProgramsDirectory $programs|Out-Null
    Assert ((Get-DeviceBatteryStartupState $startup $installed.InstalledExecutable).Status -ceq 'missing') 'default uninstall only disables startup'
    Assert (Test-Path -LiteralPath (Join-Path $programs $correct)) 'default uninstall preserves manual launcher'
    Assert (Test-Path -LiteralPath $installed.InstalledExecutable) 'default uninstall preserves installed program'
    Assert ([IO.File]::ReadAllText($settings) -ceq '{"keep":"user audio settings"}') 'default uninstall preserves user settings'
    $script:disabledForTest=$true
    Assert-Rejected {Install-DeviceBattery @arguments} 'installer respects Windows startup disable decision'
    $script:disabledForTest=$false
    $foreign=$shell.CreateShortcut((Join-Path $startup $correct));$foreign.TargetPath=Join-Path $env:SystemRoot 'notepad.exe';$foreign.Description='Unrelated shortcut';$foreign.Save()
    Assert-Rejected {Install-DeviceBattery @arguments} 'foreign shortcut blocks installation without overwrite'
    Assert-Rejected {Uninstall-DeviceBatteryStartup -SourceRoot $source -DataDirectory $data -StartupDirectory $startup -ProgramsDirectory $programs} 'foreign shortcut is not removed'
    Assert ($shell.CreateShortcut((Join-Path $startup $correct)).TargetPath -like '*notepad.exe') 'unrelated shortcut bytes remain usable'
    Remove-Item -LiteralPath (Join-Path $startup $correct) -Force
    Assert-Rejected {Assert-DeviceBatteryInstallDirectory ([IO.Path]::GetPathRoot($fixtureRoot)) $source $data} 'drive roots are never install targets'
    Assert-Rejected {Assert-DeviceBatteryInstallDirectory $source $source $data} 'unowned source is never an install target'
    Assert-Rejected {Resolve-DeviceBatteryPayloadPath $target '../outside.txt'} 'traversal paths are rejected'
    Assert-Rejected {Resolve-DeviceBatteryPayloadPath $target 'engine\..\outside.txt'} 'alternate separators are rejected'
    Assert-Rejected {Resolve-DeviceBatteryPayloadPath $target 'engine/NUL.txt'} 'Windows reserved device paths are rejected'
    Assert-Rejected {Resolve-DeviceBatteryPayloadPath $target 'engine/reader.py.'} 'Windows trailing-dot aliases are rejected'
    Assert-Rejected {Resolve-DeviceBatteryPayloadPath $target 'engine /reader.py'} 'Windows trailing-space directory aliases are rejected'
    $foreignDirectory=Join-Path $fixtureRoot 'unrelated files';New-Item -ItemType Directory -Path $foreignDirectory|Out-Null
    [IO.File]::WriteAllText((Join-Path $foreignDirectory 'keep.txt'),'keep')
    Assert-Rejected {Assert-DeviceBatteryInstallDirectory $foreignDirectory $source $data} 'unrelated nonempty directory is preserved'
    Install-DeviceBattery @arguments|Out-Null
    $unknown=Join-Path $target 'user note.txt';[IO.File]::WriteAllText($unknown,'keep unknown')
    $unknownData=Join-Path $data 'personal-note.txt';[IO.File]::WriteAllText($unknownData,'keep personal')
    $modified=Join-Path $target 'engine\providers\base.py';[IO.File]::WriteAllText($modified,'user modified installed file')
    Uninstall-DeviceBatteryStartup -SourceRoot $source -DataDirectory $data -StartupDirectory $startup -ProgramsDirectory $programs -RemoveProgram|Out-Null
    Assert (-not (Test-Path -LiteralPath $installed.InstalledExecutable)) 'explicit program removal deletes unmodified owned files'
    Assert (-not (Test-Path -LiteralPath (Join-Path $target 'engine\runtime\python.exe'))) 'program removal includes the matching portable engine'
    Assert (Test-Path -LiteralPath $unknown) 'program removal preserves unknown files in the install directory'
    Assert ([IO.File]::ReadAllText($modified) -ceq 'user modified installed file') 'program removal preserves user-modified owned files'
    Assert (Test-Path -LiteralPath $settings) 'program removal keeps personal settings by default'
    Uninstall-DeviceBatteryStartup -SourceRoot $source -DataDirectory $data -StartupDirectory $startup -ProgramsDirectory $programs -RemoveData|Out-Null
    Assert (-not (Test-Path -LiteralPath $settings)) 'explicit data removal deletes known app setting files'
    Assert (Test-Path -LiteralPath $unknownData) 'explicit data removal never recursively clears user files'
    Write-Output ('PASS '+$script:passed+' installer tests on PowerShell '+$PSVersionTable.PSVersion+'; real known folders and registry unchanged')
} finally {
    Stop-Fixture $uiProcess
    Stop-Fixture $pythonProcess
}
