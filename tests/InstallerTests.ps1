param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$project=Split-Path -Parent $PSScriptRoot
. (Join-Path $project 'scripts\Install.Common.ps1')
$script:passed=0
$script:disabledForTest=$false
# Do not consult or modify the real user's Startup Apps decisions in these tests.
function Get-DeviceBatteryStartupApproval([string]$Name) {
    if($script:disabledForTest){return ,([byte[]]@(3,0,0,0))}
    return $null
}
function Assert([bool]$Condition,[string]$Name){if(-not $Condition){throw ('FAIL '+$Name)};$script:passed++}
function Assert-Rejected([scriptblock]$Operation,[string]$Name){$failed=$false;try{& $Operation|Out-Null}catch{$failed=$true};Assert $failed $Name}
$root=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('installer-'+$PSVersionTable.PSVersion.Major+'-'+[Guid]::NewGuid().ToString('N'))
$source=Join-Path $root 'portable-source'
$target=Join-Path $root 'stable-program'
$data=Join-Path $root 'user-data'
$startup=Join-Path $root 'Startup'
$programs=Join-Path $root 'Programs'
New-Item -ItemType Directory -Path (Join-Path $source 'scripts'),(Join-Path $source 'outputs'),$startup,$programs -Force|Out-Null
foreach($name in @('Install.Common.ps1','Install-Startup.ps1','Uninstall-Startup.ps1')){Copy-Item -LiteralPath (Join-Path $project ('scripts\'+$name)) -Destination (Join-Path $source ('scripts\'+$name))}
[IO.File]::WriteAllBytes((Join-Path $source 'outputs\AtkMouseBattery.exe'),[byte[]]@(77,90,1,2))
$arguments=@{SourceRoot=$source;InstallDirectory=$target;DataDirectory=$data;StartupDirectory=$startup;ProgramsDirectory=$programs}
$correct=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('TXlGaW5kZXIg6K6+5aSH55S16YePLmxuaw=='))
Assert ((Get-DeviceBatteryShortcutName) -ceq $correct) 'shortcut name is Unicode-correct regardless of PowerShell default encoding'
$shell=New-DeviceBatteryShortcutShell
try {
    $bad=[Text.Encoding]::GetEncoding(936).GetString([Text.Encoding]::UTF8.GetBytes($correct))
    $legacy=$shell.CreateShortcut((Join-Path $startup $bad))
    $legacy.TargetPath=Join-Path $source 'outputs\AtkMouseBattery.exe'
    $legacy.Description='Start the MyFinder mouse and headset battery indicator quietly at user login.'
    $legacy.Save()
    $installed=Install-DeviceBattery @arguments
    Assert (Test-Path -LiteralPath $installed.InstalledExecutable) 'payload is copied to the stable directory'
    Assert (-not (Test-Path -LiteralPath (Join-Path $startup $bad))) 'owned garbled v1 shortcut is migrated'
    Assert ((Get-ChildItem $startup -Filter '*.lnk').Count -eq 1) 'exactly one autostart shortcut remains'
    Assert ($shell.CreateShortcut((Join-Path $startup $correct)).TargetPath -ieq (Join-Path $target 'outputs\AtkMouseBattery.exe')) 'autostart points to the stable program'
    Assert ($shell.CreateShortcut((Join-Path $programs $correct)).Arguments -eq '--show') 'manual launcher arguments are preserved'
    Install-DeviceBattery @arguments|Out-Null
    Assert ((Get-ChildItem $startup -Filter '*.lnk').Count -eq 1) 'reinstallation does not duplicate autostart'
    $registered=@{};foreach($key in $arguments.Keys){$registered[$key]=$arguments[$key]};$registered.SourceRoot=$target
    Install-DeviceBattery @registered|Out-Null
    Assert ((Get-ChildItem $startup -Filter '*.lnk').Count -eq 1) 'an owned installed directory can re-register autostart without the original package'
    $settings=Join-Path $data 'audio-settings.json'
    [IO.File]::WriteAllText($settings,'{"keep":"user audio settings"}')
    Uninstall-DeviceBatteryStartup -SourceRoot $source -DataDirectory $data -StartupDirectory $startup -ProgramsDirectory $programs|Out-Null
    Assert (-not (Test-Path -LiteralPath (Join-Path $startup $correct))) 'uninstall removes the installed autostart shortcut'
    Assert (Test-Path -LiteralPath (Join-Path $programs $correct)) 'disabling autostart preserves the manual launcher'
    Assert ([IO.File]::ReadAllText($settings) -ceq '{"keep":"user audio settings"}') 'uninstall preserves audio settings'
    Uninstall-DeviceBatteryStartup -SourceRoot $source -DataDirectory $data -StartupDirectory $startup -ProgramsDirectory $programs -RemoveStartMenu|Out-Null
    Assert (-not (Test-Path -LiteralPath (Join-Path $programs $correct))) 'explicit full shortcut removal removes the manual launcher'
    $script:disabledForTest=$true
    Assert-Rejected {Install-DeviceBattery @arguments} 'disabled startup choices are respected'
    Assert (-not (Test-Path -LiteralPath (Join-Path $startup $correct))) 'rejected installation does not create autostart'
    $script:disabledForTest=$false
    $foreign=$shell.CreateShortcut((Join-Path $startup $correct));$foreign.TargetPath=Join-Path $env:SystemRoot 'notepad.exe';$foreign.Description='Unrelated shortcut';$foreign.Save()
    Assert-Rejected {Install-DeviceBattery @arguments} 'foreign shortcut blocks installation before any overwrite'
    Assert-Rejected {Uninstall-DeviceBatteryStartup -SourceRoot $source -DataDirectory $data -StartupDirectory $startup -ProgramsDirectory $programs} 'foreign shortcut is not deleted by uninstall'
    Assert ($shell.CreateShortcut((Join-Path $startup $correct)).TargetPath -like '*notepad.exe') 'foreign shortcut is preserved'
    Remove-Item -LiteralPath (Join-Path $startup $correct)
    Assert-Rejected {Assert-DeviceBatteryInstallDirectory ([IO.Path]::GetPathRoot($root)) $source $data} 'drive roots are never installation targets'
    Assert-Rejected {Assert-DeviceBatteryInstallDirectory $source $source $data} 'source directory is not overwritten as an installation'
    $foreignDirectory=Join-Path $root 'unrelated-files';New-Item -ItemType Directory -Path $foreignDirectory|Out-Null
    [IO.File]::WriteAllText((Join-Path $foreignDirectory 'keep.txt'),'keep')
    Assert-Rejected {Assert-DeviceBatteryInstallDirectory $foreignDirectory $source $data} 'nonempty unrelated install directories are preserved'
    $oldHash=(Get-FileHash (Join-Path $target 'outputs\AtkMouseBattery.exe')).Hash
    $oldReceipt=[IO.File]::ReadAllText((Join-Path $data 'install.json'))
    [IO.File]::WriteAllBytes((Join-Path $source 'outputs\AtkMouseBattery.exe'),[byte[]]@(77,90,3,4))
    $held=[IO.File]::Open((Join-Path $target 'outputs\AtkMouseBattery.exe'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        Assert-Rejected {Install-DeviceBattery @arguments} 'a non-app process holding the executable blocks update before mutation'
        Assert ([IO.File]::ReadAllText((Join-Path $data 'install.json')) -ceq $oldReceipt) 'locked executable preflight preserves the receipt'
    } finally {$held.Dispose()}
    $block=Join-Path $root 'not-a-directory';[IO.File]::WriteAllText($block,'block')
    $failing=@{};foreach($key in $arguments.Keys){$failing[$key]=$arguments[$key]};$failing.ProgramsDirectory=$block
    Assert-Rejected {Install-DeviceBattery @failing} 'a shortcut failure aborts installation'
    Assert ((Get-FileHash (Join-Path $target 'outputs\AtkMouseBattery.exe')).Hash -eq $oldHash) 'failed update restores the previous program'
    Assert ([IO.File]::ReadAllText((Join-Path $data 'install.json')) -ceq $oldReceipt) 'failed update restores the installation receipt'
    Assert (-not (Test-Path -LiteralPath (Join-Path $startup $correct))) 'failed update removes its newly created autostart'
    Assert ([IO.File]::ReadAllText($settings) -ceq '{"keep":"user audio settings"}') 'failed update preserves independent user settings'
    Write-Output ('PASS '+$script:passed+' installer tests on PowerShell '+$PSVersionTable.PSVersion+'; real known folders and registry unchanged')
} finally { }
