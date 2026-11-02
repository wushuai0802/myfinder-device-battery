param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
. (Join-Path $project 'scripts\Install.Common.ps1')
$updateCommon=Join-Path $project 'scripts\Update.Common.ps1'
if(-not (Test-Path -LiteralPath $updateCommon)){throw 'FAIL signed update helper and stable-install rules are missing.'}
. $updateCommon

$script:passed=0
function Assert([bool]$Value,[string]$Name){if(-not $Value){throw ('FAIL '+$Name)};$script:passed++}
function Get-DeviceBatteryStartupApproval([string]$Name){return $null}
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
    [IO.File]::WriteAllText($cs,('[assembly:System.Reflection.AssemblyInformationalVersion("'+$Version+'")] class Fixture {static void Main(){}}'))
    & (Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /target:winexe /platform:x64 ('/out:'+(Join-Path $Directory 'outputs\AtkMouseBattery.exe')) $cs
    if($LASTEXITCODE -ne 0){throw 'Fixture build failed.'}
    Copy-Item -LiteralPath (Join-Path $Directory 'outputs\AtkMouseBattery.exe') -Destination (Join-Path $Directory 'engine\runtime\python.exe')
    $null=New-DeviceBatteryPackageManifest $Directory
}
New-Item -ItemType Directory -Path $fixture -Force|Out-Null
try {
    New-Package $old '1.3.4';New-Package $candidate '1.3.6'
    $install=@{SourceRoot=$old;InstallDirectory=$target;DataDirectory=$data;StartupDirectory=$startup;ProgramsDirectory=$programs}
    $null=Install-DeviceBattery @install
    $exe=Join-Path $target 'outputs\AtkMouseBattery.exe'
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
    function Start-DeviceBatteryUpdatedApp([string]$Executable){$script:restarts+=,$Executable}
    function Invoke-DeviceBatteryUpdateInstaller([string]$Package,[string]$InstallDirectory,[string]$DataDirectory){
        $script:installCalls++
        # Invoke the same transaction in this scope so the fake registry/icon functions remain
        # effective. Launching a separate PowerShell entry point would lose those overrides.
        Install-DeviceBattery -SourceRoot $Package -InstallDirectory $InstallDirectory -DataDirectory $DataDirectory -StartupDirectory $startup -ProgramsDirectory $programs
    }
    function Work {
        $directory=Join-Path (Join-Path (Join-Path $data 'updates') '1.3.6') ('run-'+[Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $directory -Force|Out-Null
        return $directory
    }
    $work=Work
    $parameters=@{Executable=$exe;DataDirectory=$data;Version='1.3.6';WorkDirectory=$work;ParentId=123;ParentStartUtc='2026-01-01T00:00:00Z'}
    $null=Invoke-DeviceBatteryUpdate @parameters
    Assert ((Read-DeviceBatteryReceipt $data).Version -ceq '1.3.6') 'old version updates transactionally'
    Assert ((Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash -ceq $settingsHash) 'settings preserved on success'
    Assert ($script:restarts.Count -eq 1 -and $script:restarts[0] -ceq $exe) 'restart uses exact Unicode install path'
    Assert (-not (Test-Path -LiteralPath $work)) 'successful handoff staging cleaned'
    $result=Get-Content -LiteralPath (Join-Path $data 'update-result.json') -Raw|ConvertFrom-Json
    Assert ($result.status -ceq 'success') 'success result persisted'
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
    Assert ($script:restarts.Count -eq 1 -and $script:restarts[0] -ceq $exe) 'verified old version restarts after rollback'
    $result=Get-Content -LiteralPath (Join-Path $data 'update-result.json') -Raw|ConvertFrom-Json
    Assert ($result.status -ceq 'failed' -and $result.reason -like '*fixture mid-install failure*') 'failure reason available next launch'
    Assert (-not (Test-Path -LiteralPath $parameters.WorkDirectory)) 'failed handoff staging cleaned'
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
    Write-Output ('PASS '+$script:passed+' update installer checks on PowerShell '+$PSVersionTable.PSVersion+'; fake folders only')
}finally {
    if(Test-Path Function:\Copy-Item){Remove-Item Function:\Copy-Item}
}
