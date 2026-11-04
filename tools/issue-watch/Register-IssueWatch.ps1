[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Unregister,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository = 'wushuai0802/myfinder-device-battery',
    [string]$DraftDirectory = 'E:\AIArchive\issue-drafts',
    [string]$GhPath = 'gh', [string]$CodexPath = 'codex', [string]$PythonPath
)
. (Join-Path $PSScriptRoot 'IssueWatch.Common.ps1')
$name = 'MyFinder Issue Watch'
if ($Unregister) {
    if ($PSCmdlet.ShouldProcess($name, 'Unregister current-user scheduled task')) {
        Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction Stop
    }
    return
}
if (-not $PSCmdlet.ShouldProcess($name, 'Register current-user task at 09:00 and 21:00')) { return }
$DraftDirectory = [IO.Path]::GetFullPath($DraftDirectory)
$python = Resolve-WatchPython $PythonPath
$gh = Resolve-WatchCommand $GhPath
$codex = Resolve-WatchCommand $CodexPath
$hostPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$arguments = @('-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass',
    '-File', (Join-Path $PSScriptRoot 'Issue-Watch.ps1'), '-DraftDirectory', $DraftDirectory,
    '-Repository', $Repository, '-GhPath', $gh, '-CodexPath', $codex, '-PythonPath', $python)
$action = New-ScheduledTaskAction -Execute $hostPath -Argument (@($arguments | ForEach-Object { Quote-NativeArgument $_ }) -join ' ') -WorkingDirectory $script:ProjectRoot
$triggers = @(New-ScheduledTaskTrigger -Daily -At '09:00'; New-ScheduledTaskTrigger -Daily -At '21:00')
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -Hidden -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $name -Action $action -Trigger $triggers -Principal $principal -Settings $settings -Description 'Draft only; explicit digest-bound approval required to post.' -Force
