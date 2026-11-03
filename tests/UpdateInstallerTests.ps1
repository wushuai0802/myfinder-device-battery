param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
. (Join-Path $project 'scripts\Install.Common.ps1')
$updateCommon=Join-Path $project 'scripts\Update.Common.ps1'
if(-not (Test-Path -LiteralPath $updateCommon)){throw 'FAIL signed update helper and stable-install rules are missing.'}
. $updateCommon
$script:realUpdateInstaller=${function:Invoke-DeviceBatteryUpdateInstaller}

$script:passed=0
function Assert([bool]$Value,[string]$Name){if(-not $Value){throw ('FAIL '+$Name)};$script:passed++}
$script:startupApproval=$null;$script:approvalUnreadable=$false
function Get-DeviceBatteryStartupApproval([string]$Name){
    if($script:approvalUnreadable){throw 'fixture unreadable startup decision'}
    if($null -ne $script:startupApproval){return ,$script:startupApproval}
    return $null
}
function Update-DeviceBatteryShellIcons([string]$Executable,[string]$StartupShortcut,[string]$ManualShortcut){}
$fixture=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('update-installer-'+$PSVersionTable.PSVersion.Major+'-'+[Guid]::NewGuid().ToString('N'))
$old=Join-Path $fixture 'old package';$candidate=Join-Path $fixture 'new package'
$target=Join-Path $fixture (([string][char]0x4e2d)+[char]0x6587+' installed program')
$data=Join-Path $fixture 'fake data';$startup=Join-Path $fixture 'fake Startup';$programs=Join-Path $fixture 'fake Programs'
function New-Package([string]$Directory,[string]$Version){
    New-Item -ItemType Directory -Path (Join-Path $Directory 'outputs'),(Join-Path $Directory 'scripts'),(Join-Path $Directory 'engine\runtime'),$startup,$programs -Force|Out-Null
    foreach($name in @('Install.Common.ps1','Manifest.Common.ps1','Install-Startup.ps1')){Copy-Item -LiteralPath (Join-Path $project ('scripts\'+$name)) -Destination (Join-Path $Directory ('scripts\'+$name))}
    foreach($name in @('Uninstall-Startup.ps1')){[IO.File]::WriteAllText((Join-Path $Directory ('scripts\'+$name)),'# fixture')}
    foreach($name in @('LICENSE','THIRD_PARTY_NOTICES.md')){[IO.File]::WriteAllText((Join-Path $Directory $name),'fixture')}
    $release=[ordered]@{schema=1;appId='myfinder-device-battery';version=$Version;assemblyVersion='1.3.6.0';configSchema=1;engineSchema=2}
    [IO.File]::WriteAllText((Join-Path $Directory 'release.json'),($release|ConvertTo-Json))
    [IO.File]::WriteAllText((Join-Path $Directory 'engine\server.py'),('# payload '+$Version))
    $cs=Join-Path $fixture ('host-'+$Version+'.cs')
    [IO.File]::WriteAllText($cs,('[assembly:System.Reflection.AssemblyInformationalVersion("'+$Version+'")] class Fixture {static void Main(string[] args){if(args.Length==0 || args.Length==1 && args[0]=="--show-updated") System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Fixture).Assembly.Location),"started.txt"),"'+$Version+'|"+string.Join(" ",args));}}'))
    & (Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /target:winexe /platform:x64 ('/out:'+(Join-Path $Directory 'outputs\AtkMouseBattery.exe')) $cs
    if($LASTEXITCODE -ne 0){throw 'Fixture build failed.'}
    Copy-Item -LiteralPath (Join-Path $Directory 'outputs\AtkMouseBattery.exe') -Destination (Join-Path $Directory 'engine\runtime\python.exe')
    $null=New-DeviceBatteryPackageManifest $Directory
}
function Test-ModuleIndependentHashes {
    Assert ([bool](Get-Command Get-DeviceBatteryFileHash -ErrorAction SilentlyContinue)) 'shared module-independent SHA256 function exists'
    foreach($name in @('empty.txt','ordinary.txt',(([string][char]0x4e2d)+[char]0x6587+'.txt'),'hash with spaces.txt')) {
        $path=Join-Path $fixture $name
        $bytes=[byte[]]@()
        if($name -ne 'empty.txt'){$bytes=[byte[]]@(0,1,2,127,128,255,13,10)}
        [IO.File]::WriteAllBytes($path,$bytes)
        $expected=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        Assert ((Get-DeviceBatteryFileHash $path) -ceq $expected) ('shared SHA256 matches uppercase Get-FileHash for '+$name)
        $handle=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $handle.Dispose()
    }
}
function Test-IsolatedHelperEnvironment([switch]$ValidationFailure) {
    $null=Install-DeviceBattery @install
    $started=Join-Path $target 'outputs\started.txt'
    if(Test-Path -LiteralPath $started){Remove-Item -LiteralPath $started -Force}
    $package=Join-Path $fixture ('isolated helper package-'+[Guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath $candidate -Destination $package -Recurse
    # Keep real transaction/hash/rollback code, but route shortcuts and registry probes to fixtures.
    [IO.File]::WriteAllText((Join-Path $package 'scripts\Install-Startup.ps1'),@'
param([string]$InstallDirectory,[string]$DataDirectory,[switch]$PreserveStartup)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Install.Common.ps1')
function Get-DeviceBatteryStartupApproval([string]$Name){return $null}
function Update-DeviceBatteryShellIcons([string]$Executable,[string]$StartupShortcut,[string]$ManualShortcut){}
$fixture=Split-Path -Parent $DataDirectory
Install-DeviceBattery -SourceRoot (Split-Path -Parent $PSScriptRoot) -InstallDirectory $InstallDirectory -DataDirectory $DataDirectory -StartupDirectory (Join-Path $fixture 'fake Startup') -ProgramsDirectory (Join-Path $fixture 'fake Programs') -PreserveStartup:$PreserveStartup
'@)
    $null=New-DeviceBatteryPackageManifest $package
    $work=Work
    # The harmless fixture executable accepts the independent verifier invocation. Its C# verifier
    # is covered in UpdateTests; seed that boundary's output before running the real helper.
    Copy-Item -LiteralPath $package -Destination (Join-Path $work 'verified') -Recurse
    if($ValidationFailure){[IO.File]::WriteAllText((Join-Path $work 'verified\engine\server.py'),'# fixture validation failure')}
    $modules=Join-Path $fixture 'PowerShell 7 modules only'
    if(-not (Test-Path -LiteralPath $modules)){
        New-Item -ItemType Directory -Path $modules|Out-Null
        $pwsh=Get-Command pwsh.exe -ErrorAction Stop
        Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $pwsh.Source) 'Modules\Microsoft.PowerShell.Utility') -Destination $modules -Recurse
    }
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    $arguments=@((Join-Path $project 'scripts\Apply-Update.ps1'),'-ExecutablePath',$exe,'-DataDirectory',$data,'-Version','1.3.6','-WorkDirectory',$work,'-ParentId','2147483647','-ParentStartUtc','2026-01-01T00:00:00Z')
    $info.Arguments='-NoProfile -ExecutionPolicy Bypass -File '+(@($arguments|ForEach-Object {ConvertTo-DeviceBatteryUpdateArgument $_}) -join ' ')
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden'
    $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$info.EnvironmentVariables['PSModulePath']=$modules
    $process=[Diagnostics.Process]::Start($info)
    try {
        $output=$process.StandardOutput.ReadToEndAsync();$errors=$process.StandardError.ReadToEndAsync()
        $finished=$process.WaitForExit(30000)
        if(-not $finished){$process.Kill();$process.WaitForExit()}
        [IO.File]::WriteAllText((Join-Path $fixture 'helper-stdout.txt'),$output.Result)
        [IO.File]::WriteAllText((Join-Path $fixture 'helper-stderr.txt'),$errors.Result)
        Assert ($finished -and $process.ExitCode -eq 0) ('isolated Windows PowerShell helper exits normally: '+$errors.Result)
    }finally{$process.Dispose()}
    $result=Get-Content -LiteralPath (Join-Path $data 'update-result.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $expectedStatus=if($ValidationFailure){'failed'}else{'success'}
    $expectedVersion=if($ValidationFailure){'1.3.4'}else{'1.3.6'}
    Assert ($result.status -ceq $expectedStatus) ('Windows PowerShell helper handles PowerShell 7-only PSModulePath: '+$result.reason)
    if($ValidationFailure){Assert ($result.reason -like '*Package file is missing or changed: engine/server.py*' -and $result.reason -notlike '*Recovery restart failed*') 'isolated helper records validation failure without misreporting recovery'}
    Assert ((Read-DeviceBatteryReceipt $data).Version -ceq $expectedVersion) 'isolated helper records the expected installed version'
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Test-Path -LiteralPath $started) -and $clock.ElapsedMilliseconds -lt 5000){Start-Sleep -Milliseconds 25}
    $expectedArguments=if($ValidationFailure){''}else{'--show-updated'}
    Assert ((Test-Path -LiteralPath $started) -and [IO.File]::ReadAllText($started) -ceq ($expectedVersion+'|'+$expectedArguments)) ('isolated helper launches fixture version '+$expectedVersion+' with the correct success/recovery arguments')
    Assert (-not (Test-Path -LiteralPath $work)) 'isolated helper cleans its staging directory'
}
function Test-RecoveryRestart([string]$Fault,[switch]$LaunchFailure,[switch]$ResultFailure) {
    $null=Install-DeviceBattery @install
    $manifestReader=${function:Read-DeviceBatteryPackageManifest};$receiptReader=${function:Read-DeviceBatteryReceipt}
    $hashReader=${function:Get-DeviceBatteryFileHash};$resultWriter=${function:Write-DeviceBatteryUpdateResult}
    $script:validationFault=$Fault;$script:validationFaultActive=$false;$script:recovering=$false;$script:recoveryReads=0
    $script:restarts=@();$script:restartFailure=$LaunchFailure.IsPresent
    $previousWaitResult=$script:waitResult;$script:waitResult=$true
    function Read-DeviceBatteryPackageManifest([string]$Root) {
        if($script:recovering){$script:recoveryReads++;throw 'fixture recovery manifest unavailable'}
        if($script:validationFaultActive -and $script:validationFault -eq 'manifest'){throw 'fixture manifest validation failure'}
        & $manifestReader $Root
    }
    function Read-DeviceBatteryReceipt([string]$DataDirectory) {
        if($script:recovering){$script:recoveryReads++;throw 'fixture recovery receipt unavailable'}
        if($script:validationFaultActive -and $script:validationFault -eq 'receipt'){throw 'fixture receipt validation failure'}
        & $receiptReader $DataDirectory
    }
    function Get-FileHash {
        param([string]$LiteralPath,[string]$Algorithm)
        if($script:recovering){$script:recoveryReads++;throw 'fixture recovery hash unavailable'}
        if($script:validationFaultActive -and $script:validationFault -eq 'hash'){throw 'fixture hash validation failure'}
        Microsoft.PowerShell.Utility\Get-FileHash @PSBoundParameters
    }
    function Get-DeviceBatteryFileHash([string]$LiteralPath) {
        if($script:recovering){$script:recoveryReads++;throw 'fixture recovery hash unavailable'}
        if($script:validationFaultActive -and $script:validationFault -eq 'hash'){throw 'fixture hash validation failure'}
        & $hashReader $LiteralPath
    }
    function Invoke-DeviceBatteryUpdateInstaller([string]$Package,[string]$InstallDirectory,[string]$DataDirectory) {
        $script:validationFaultActive=$true
        try {Install-DeviceBattery -SourceRoot $Package -InstallDirectory $InstallDirectory -DataDirectory $DataDirectory -StartupDirectory $startup -ProgramsDirectory $programs -PreserveStartup}
        catch {$script:recovering=$true;throw}
    }
    function Write-DeviceBatteryUpdateResult([string]$DataDirectory,[string]$Status,[string]$Version,[string]$Reason) {
        if($ResultFailure){throw 'fixture result write failure'}
        & $resultWriter $DataDirectory $Status $Version $Reason
    }
    try {
        $parameters.WorkDirectory=Work;$thrown=$null
        try {$null=Invoke-DeviceBatteryUpdate @parameters}catch{$thrown=$_.Exception.Message}
        Assert ($script:restarts.Count -eq 1 -and $script:restarts[0] -ceq $exe) ('recovery attempts the known executable after '+$Fault+' failure; launchFailure='+$LaunchFailure+'; resultFailure='+$ResultFailure)
        Assert (-not $script:lastRestartHasWindowStyle) ('recovery restart omits WindowStyle after '+$Fault+' failure')
        Assert (-not $script:lastRestartHasArguments) ('recovery restart has no show arguments after '+$Fault+' failure')
        Assert ($script:recoveryReads -eq 0) 'recovery performs no hash, manifest or receipt checks'
        if($ResultFailure){Assert ($thrown -ceq 'fixture result write failure') 'result-write failure still attempts recovery'}
        else {
            Assert ($null -eq $thrown) 'installer failure is recorded without escaping the helper'
            $result=Get-Content -LiteralPath (Join-Path $data 'update-result.json') -Raw -Encoding UTF8|ConvertFrom-Json
            Assert ($result.status -ceq 'failed' -and $result.reason -like ('*fixture '+$Fault+' validation failure*')) 'result preserves the original validation failure'
            if($LaunchFailure){Assert ($result.reason -like '*Recovery restart failed: fixture launch failure*') 'only a launch failure records Recovery restart failed'}
            else {Assert ($result.reason -notlike '*Recovery restart failed*') 'unavailable validation never becomes a recovery launch failure'}
        }
        Assert (-not (Test-Path -LiteralPath $parameters.WorkDirectory)) 'validation-failure staging is cleaned'
    }finally{$script:validationFaultActive=$false;$script:recovering=$false;$script:restartFailure=$false;$script:waitResult=$previousWaitResult}
}
New-Item -ItemType Directory -Path $fixture -Force|Out-Null
try {
    New-Package $old '1.3.4';New-Package $candidate '1.3.6'
    $install=@{SourceRoot=$old;InstallDirectory=$target;DataDirectory=$data;StartupDirectory=$startup;ProgramsDirectory=$programs}
    $null=Install-DeviceBattery @install
    $exe=Join-Path $target 'outputs\AtkMouseBattery.exe'
    $shell=New-DeviceBatteryShortcutShell;$shortcutName=Get-DeviceBatteryShortcutName
    $startupPath=Join-Path $startup $shortcutName;$manualPath=Join-Path $programs $shortcutName
    Assert (Test-DeviceBatteryUpdateInstallation $exe $data) 'stable qualification reuses receipt and ownership rules'
    $probeInfo=New-Object Diagnostics.ProcessStartInfo
    $probeInfo.FileName=Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    $probeInfo.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $project 'scripts\Apply-Update.ps1')+'" "-Probe" "-ExecutablePath" "'+$exe+'" "-DataDirectory" "'+$data+'"'
    $probeInfo.UseShellExecute=$false;$probeInfo.CreateNoWindow=$true;$probeInfo.WindowStyle='Hidden';$probeInfo.RedirectStandardOutput=$true
    $probe=[Diagnostics.Process]::Start($probeInfo)
    try{
        $probeText=$probe.StandardOutput.ReadToEndAsync()
        Assert ($probe.WaitForExit(30000) -and $probeText.Wait(1000) -and $probe.ExitCode -eq 0 -and $probeText.Result -ceq 'stable') 'hidden PowerShell parses quoted switches, spaces and Chinese paths correctly'
    }finally{$probe.Dispose()}
    Assert (-not (Test-DeviceBatteryUpdateInstallation (Join-Path $old 'outputs\AtkMouseBattery.exe') $data)) 'portable copy cannot update recorded installation'
    $marker=Join-Path $target '.myfinder-device-battery-install.json';$saved=[IO.File]::ReadAllText($marker)
    [IO.File]::WriteAllText($marker,$saved.Replace('myfinder-device-battery','another-app'))
    Assert (-not (Test-DeviceBatteryUpdateInstallation $exe $data)) 'foreign ownership is refused'
    [IO.File]::WriteAllText($marker,$saved)
    $receiptPath=Join-Path $data 'install.json';$receiptText=[IO.File]::ReadAllText($receiptPath)
    [IO.File]::WriteAllText($receiptPath,'{"AppId":"another-app","Schema":1,"InstallDirectory":"D:\\invalid"}')
    Assert (-not (Test-DeviceBatteryUpdateInstallation $exe $data)) 'invalid receipt cannot qualify as stable'
    [IO.File]::WriteAllText($receiptPath,$receiptText)
    $settings=Join-Path $data 'audio-settings.json';[IO.File]::WriteAllText($settings,'{"Theme":"dark","AutoCheckUpdates":false,"private":"unchanged"}')
    $settingsHash=(Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash
    $self=[Diagnostics.Process]::GetCurrentProcess()
    try {
        Assert (-not (Wait-DeviceBatteryUpdateParent $self.Id $self.StartTime.ToUniversalTime().ToString('o') $self.MainModule.FileName 5)) 'real parent wait times out without terminating the process'
        $wrongIdentity=$false
        try{$null=Wait-DeviceBatteryUpdateParent $self.Id $self.StartTime.ToUniversalTime().AddTicks(1).ToString('o') $self.MainModule.FileName 5}catch{$wrongIdentity=$true}
        Assert $wrongIdentity 'PID reuse with a different start time is refused'
        Assert (-not $self.HasExited) 'waiting never kills the parent'
    }finally{$self.Dispose()}
    $script:waitResult=$true;$script:restarts=@();$script:installCalls=0
    function Wait-DeviceBatteryUpdateParent([int]$ParentId,[string]$ParentStartUtc,[string]$Executable){return $script:waitResult}
    # Signature, ZIP and directory rejection are tested by UpdateTests. This fixture only substitutes
    # the already-verified package boundary, never the transactional installer or rollback code.
    function Confirm-DeviceBatteryUpdatePackage([string]$Executable,[string]$WorkDirectory,[string]$Version){return $candidate}
    $script:restartFailure=$false
    function Start-Process {
        [CmdletBinding()]param([string]$FilePath,[string]$ArgumentList,[string]$WorkingDirectory,[string]$WindowStyle,[switch]$PassThru)
        $script:restarts+=,$FilePath
        $script:lastRestartHasWindowStyle=$PSBoundParameters.ContainsKey('WindowStyle')
        $script:lastRestartHasArguments=$PSBoundParameters.ContainsKey('ArgumentList')
        $script:lastRestartArguments=$ArgumentList
        Assert ($WorkingDirectory -ceq (Split-Path -Parent $FilePath)) 'restart directly uses the exact executable directory'
        if($script:restartFailure){throw 'fixture launch failure'}
    }
    function Invoke-DeviceBatteryUpdateInstaller([string]$Package,[string]$InstallDirectory,[string]$DataDirectory){
        $script:installCalls++
        # Invoke the same transaction in this scope so the fake registry/icon functions remain
        # effective. Launching a separate PowerShell entry point would lose those overrides.
        Install-DeviceBattery -SourceRoot $Package -InstallDirectory $InstallDirectory -DataDirectory $DataDirectory -StartupDirectory $startup -ProgramsDirectory $programs -PreserveStartup
    }
    function Work {
        $directory=Join-Path (Join-Path (Join-Path $data 'updates') '1.3.6') ('run-'+[Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $directory -Force|Out-Null
        return $directory
    }
    $work=Work
    $parameters=@{Executable=$exe;DataDirectory=$data;Version='1.3.6';WorkDirectory=$work;ParentId=123;ParentStartUtc='2026-01-01T00:00:00Z'}
    $link=$shell.CreateShortcut($startupPath);$link.TargetPath=Join-Path $old 'outputs\AtkMouseBattery.exe';$link.Save()
    $null=Invoke-DeviceBatteryUpdate @parameters
    Assert ((Read-DeviceBatteryReceipt $data).Version -ceq '1.3.6') 'old version updates transactionally'
    Assert ((Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash -ceq $settingsHash) 'settings preserved on success'
    Assert ($script:restarts.Count -eq 1 -and $script:restarts[0] -ceq $exe) 'restart uses exact Unicode install path'
    Assert (-not $script:lastRestartHasWindowStyle) 'successful restart omits WindowStyle so the first dialog can appear'
    Assert ($script:lastRestartArguments -ceq '--show-updated') 'successful restart requests the tray-anchored updated panel'
    Assert (-not (Test-Path -LiteralPath $work)) 'successful handoff staging cleaned'
    $result=Get-Content -LiteralPath (Join-Path $data 'update-result.json') -Raw|ConvertFrom-Json
    Assert ($result.status -ceq 'success') 'success result persisted'
    Assert ($shell.CreateShortcut($startupPath).TargetPath -ceq $exe -and $shell.CreateShortcut($startupPath).Arguments -ceq '') 'update refreshes an existing quiet startup target'
    $null=Install-DeviceBattery @install
    $null=Uninstall-DeviceBatteryStartup -SourceRoot $old -DataDirectory $data -StartupDirectory $startup -ProgramsDirectory $programs
    $parameters.WorkDirectory=Work
    $null=Invoke-DeviceBatteryUpdate @parameters
    Assert (-not (Test-Path -LiteralPath $startupPath)) 'update preserves the users removal of autostart'
    Assert ((Read-DeviceBatteryReceipt $data).Version -ceq '1.3.6') 'update without autostart succeeds'
    Assert ($shell.CreateShortcut($manualPath).TargetPath -ceq $exe -and $shell.CreateShortcut($manualPath).Arguments -ceq '--show') 'update without autostart maintains the Start menu launcher'
    foreach($decision in @(3,7,255,-1,-2)) {
        $null=Install-DeviceBattery @install
        $script:startupApproval=if($decision -lt 0){,([byte[]]@())}else{,([byte[]]@($decision,0,0,0))}
        $script:approvalUnreadable=$decision -eq -2
        $approvalBytes=[Convert]::ToBase64String($script:startupApproval)
        $parameters.WorkDirectory=Work
        $null=Invoke-DeviceBatteryUpdate @parameters
        $result=Get-Content -LiteralPath (Join-Path $data 'update-result.json') -Raw|ConvertFrom-Json
        Assert ($result.status -ceq 'success' -and (Read-DeviceBatteryReceipt $data).Version -ceq '1.3.6') ('update accepts disabled or unknown startup decision '+$decision)
        Assert ([Convert]::ToBase64String($script:startupApproval) -ceq $approvalBytes) ('update preserves Windows startup decision bytes '+$decision)
        Assert ($shell.CreateShortcut($startupPath).TargetPath -ceq $exe) ('startup target maintained for decision '+$decision)
        if($decision -in @(3,7)){Assert ((Get-DeviceBatteryStartupState $startup $exe).Status -ceq 'disabled') ('startup stays disabled '+$decision)}
        $script:startupApproval=$null;$script:approvalUnreadable=$false
    }
    # StartupApproved is keyed by shortcut filename. Keep legacy startup names in place.
    $null=Install-DeviceBattery @install
    $legacyName=@(Get-DeviceBatteryShortcutNames | Where-Object {$_ -cne $shortcutName})[0]
    $legacyPath=Join-Path $startup $legacyName
    Move-Item -LiteralPath $startupPath -Destination $legacyPath
    $script:startupApproval=[byte[]]@(3,0,0,0);$parameters.WorkDirectory=Work
    $null=Invoke-DeviceBatteryUpdate @parameters
    Assert ((Test-Path -LiteralPath $legacyPath) -and -not (Test-Path -LiteralPath $startupPath)) 'update preserves a legacy startup filename and its Windows decision'
    Assert ($shell.CreateShortcut($legacyPath).TargetPath -ceq $exe -and (Get-DeviceBatteryStartupState $startup $exe).Status -ceq 'disabled') 'legacy startup target is refreshed without enabling it'
    $script:startupApproval=$null
    # Return only the isolated fixture to its old version, then fail during a payload copy.
    $null=Install-DeviceBattery @install
    $script:restarts=@();$script:copyFailure=$false
    function Copy-Item {
        [CmdletBinding()]param([string]$LiteralPath,[string]$Destination,[switch]$Force)
        if(-not $script:copyFailure -and $Destination -ieq (Join-Path $target 'engine\server.py')){$script:copyFailure=$true;throw 'fixture mid-install failure'}
        Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
    }
    $parameters.WorkDirectory=Work
    $null=Invoke-DeviceBatteryUpdate @parameters
    Remove-Item Function:\Copy-Item
    Assert ($script:copyFailure -and (Read-DeviceBatteryReceipt $data).Version -ceq '1.3.4') 'mid-install failure rolls back receipt'
    Assert ((Read-DeviceBatteryPackageManifest $target).version -ceq '1.3.4') 'all old payload hashes restored'
    Assert ((Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash -ceq $settingsHash) 'settings preserved on rollback'
    Assert ($script:restarts.Count -eq 1 -and $script:restarts[0] -ceq $exe) 'old version restarts after rollback'
    Assert (-not $script:lastRestartHasWindowStyle) 'rollback restart omits WindowStyle so the first dialog can appear'
    Assert (-not $script:lastRestartHasArguments) 'rollback restart is quiet without either show argument'
    $result=Get-Content -LiteralPath (Join-Path $data 'update-result.json') -Raw|ConvertFrom-Json
    Assert ($result.status -ceq 'failed' -and $result.reason -like '*fixture mid-install failure*') 'failure reason available next launch'
    Assert (-not (Test-Path -LiteralPath $parameters.WorkDirectory)) 'failed handoff staging cleaned'
    # Fail after the startup link would have been saved, exercising shortcut undo as well as payload undo.
    $script:originalUndo=${function:Add-DeviceBatteryUndo}
    foreach($hasStartup in @($true,$false)) {
        $null=Install-DeviceBattery @install
        if(-not $hasStartup){$null=Uninstall-DeviceBatteryStartup -SourceRoot $old -DataDirectory $data -StartupDirectory $startup -ProgramsDirectory $programs}
        $startupHash=if($hasStartup){(Get-FileHash -LiteralPath $startupPath -Algorithm SHA256).Hash}else{$null}
        $manualHash=(Get-FileHash -LiteralPath $manualPath -Algorithm SHA256).Hash
        $script:startupApproval=[byte[]]@(3,0,0,0)
        function Add-DeviceBatteryUndo($Undo,[string]$Path,[string]$Temporary) {
            & $script:originalUndo $Undo $Path $Temporary
            if($Path -ieq $manualPath){throw 'fixture late shortcut failure'}
        }
        try{$parameters.WorkDirectory=Work;$null=Invoke-DeviceBatteryUpdate @parameters}
        finally{Set-Item Function:\Add-DeviceBatteryUndo $script:originalUndo}
        $result=Get-Content -LiteralPath (Join-Path $data 'update-result.json') -Raw|ConvertFrom-Json
        Assert ($result.status -ceq 'failed' -and $result.reason -like '*fixture late shortcut failure*') ('late shortcut failure reaches rollback; startup='+$hasStartup)
        Assert ((Read-DeviceBatteryPackageManifest $target).version -ceq '1.3.4') ('late shortcut failure restores old payload; startup='+$hasStartup)
        Assert ((Test-Path -LiteralPath $startupPath) -eq $hasStartup) ('rollback restores startup presence; startup='+$hasStartup)
        if($hasStartup){Assert ((Get-FileHash -LiteralPath $startupPath -Algorithm SHA256).Hash -ceq $startupHash) 'rollback restores exact original startup bytes'}
        Assert ((Get-FileHash -LiteralPath $manualPath -Algorithm SHA256).Hash -ceq $manualHash) ('rollback restores exact Start menu bytes; startup='+$hasStartup)
        Assert ([Convert]::ToBase64String($script:startupApproval) -ceq 'AwAAAA==') ('rollback preserves Windows startup decision; startup='+$hasStartup)
        $script:startupApproval=$null
    }
    $null=Install-DeviceBattery @install
    Assert (Test-Path -LiteralPath $startupPath) 'ordinary installation still creates a missing startup link'
    foreach($decision in @(3,255)) {
        $script:startupApproval=[byte[]]@($decision,0,0,0);$rejected=$false
        try{$null=Install-DeviceBattery @install}catch{$rejected=$true}
        Assert $rejected ('ordinary installation still rejects disabled or unknown Windows decision '+$decision)
        $script:startupApproval=$null
    }
    $script:waitResult=$false;$script:restarts=@();$before=$script:installCalls;$parameters.WorkDirectory=Work
    $null=Invoke-DeviceBatteryUpdate @parameters
    Assert ($script:installCalls -eq $before -and $script:restarts.Count -eq 0) 'parent timeout never installs or starts another app'
    Assert ((Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash -ceq $settingsHash) 'timeout preserves settings'
    # A temporary unrelated key must be rejected by the real signing command without replacing
    # an existing signature or leaking private XML into command output.
    $signing=Join-Path $fixture 'signing';New-Item -ItemType Directory -Path $signing|Out-Null
    [IO.File]::WriteAllText((Join-Path $signing 'SHA256SUMS.txt'),('0'*64+'  fixture.zip'+[Environment]::NewLine))
    $signaturePath=Join-Path $signing 'SHA256SUMS.txt.sig';[IO.File]::WriteAllText($signaturePath,'keep existing signature')
    $key=New-Object Security.Cryptography.RSACryptoServiceProvider -ArgumentList 2048;$key.PersistKeyInCsp=$false
    $privatePath=Join-Path $signing 'temporary-test-key.xml'
    try {
        [IO.File]::WriteAllText($privatePath,$key.ToXmlString($true));$rejected=$false
        try{& (Join-Path $project 'scripts\Sign-Release.ps1') -ReleaseDirectory $signing -PrivateKeyPath $privatePath|Out-Null}catch{$rejected=$true}
        Assert $rejected 'signing tool rejects a key other than the built-in release key'
        Assert ([IO.File]::ReadAllText($signaturePath) -ceq 'keep existing signature') 'failed signing preserves existing signature'
    }finally{$key.Dispose();if(Test-Path -LiteralPath $privatePath){Remove-Item -LiteralPath $privatePath -Force}}
    # Exercise both real forwarding layers with a harmless installer boundary in fake folders.
    $forwardPackage=Join-Path $fixture 'forward package';$forwardScripts=Join-Path $forwardPackage 'scripts'
    New-Item -ItemType Directory -Path $forwardScripts -Force|Out-Null
    Copy-Item -LiteralPath (Join-Path $project 'scripts\Install-Startup.ps1') -Destination (Join-Path $forwardScripts 'Install-Startup.ps1')
    [IO.File]::WriteAllText((Join-Path $forwardScripts 'Install.Common.ps1'),@'
function Install-DeviceBattery {
    param($SourceRoot,$InstallDirectory,$DataDirectory,$StartupDirectory,$ProgramsDirectory,[switch]$PreserveStartup)
    [pscustomobject]@{InstallDirectory=$InstallDirectory;DataDirectory=$DataDirectory;PreserveStartup=$PreserveStartup.IsPresent}
}
'@)
    $forwarded=& $script:realUpdateInstaller $forwardPackage $target $data
    Assert ($forwarded.PreserveStartup -and $forwarded.InstallDirectory -ceq $target -and $forwarded.DataDirectory -ceq $data) 'real update helper and entry point forward preserve mode and exact paths'
    $ordinary=& (Join-Path $forwardScripts 'Install-Startup.ps1') -InstallDirectory $target -DataDirectory $data -StartupDirectory $startup -ProgramsDirectory $programs
    Assert (-not $ordinary.PreserveStartup) 'ordinary entry point defaults to the original install mode'
    $regressionFailures=@()
    foreach($regression in @(
        {Test-ModuleIndependentHashes},
        {Test-IsolatedHelperEnvironment},
        {Test-IsolatedHelperEnvironment -ValidationFailure},
        {Test-RecoveryRestart 'hash'},
        {Test-RecoveryRestart 'manifest'},
        {Test-RecoveryRestart 'receipt'},
        {Test-RecoveryRestart 'manifest' -LaunchFailure},
        {Test-RecoveryRestart 'manifest' -ResultFailure}
    )) {
        try {& $regression}catch{$regressionFailures+=,$_.Exception.Message;Write-Output $_.Exception.Message}
    }
    if($regressionFailures.Count -gt 0){throw ($regressionFailures -join [Environment]::NewLine)}
    Write-Output ('PASS '+$script:passed+' update installer checks on PowerShell '+$PSVersionTable.PSVersion+'; fake folders only')
}finally {
    if(Test-Path Function:\Copy-Item){Remove-Item Function:\Copy-Item}
}
