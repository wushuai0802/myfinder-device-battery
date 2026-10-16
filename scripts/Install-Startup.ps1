[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$trayExe = Join-Path $projectRoot 'outputs\AtkMouseBattery.exe'
$workingDirectory = Split-Path -Parent $trayExe
if (-not (Test-Path -LiteralPath $trayExe -PathType Leaf)) {
    throw 'Build AtkMouseBattery.exe before enabling startup.'
}

# Direct GUI-executable shortcuts avoid launching PowerShell or a console at
# login. These are per-user entries and do not require administrator privileges.
$shortcutName = 'MyFinder 设备电量.lnk'
$startupDirectory = [Environment]::GetFolderPath('Startup')
$programsDirectory = [Environment]::GetFolderPath('Programs')
$startupShortcut = Join-Path $startupDirectory $shortcutName
$manualShortcut = Join-Path $programsDirectory $shortcutName
$approvedPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder'
$approval = Get-ItemProperty -LiteralPath $approvedPath -Name $shortcutName -ErrorAction SilentlyContinue
if ($null -ne $approval) {
    $approvalBytes = [byte[]]$approval.$shortcutName
    if ($approvalBytes.Length -gt 0 -and $approvalBytes[0] -notin @(2, 6)) {
        throw 'Windows has previously disabled this startup item; inspect that decision before changing it.'
    }
}

$shell = New-Object -ComObject WScript.Shell
try {
    foreach ($entry in @(
        @{ Path = $startupShortcut; Arguments = ''; Description = 'Start the MyFinder mouse and headset battery indicator quietly at user login.' },
        @{ Path = $manualShortcut; Arguments = '--show'; Description = 'Open the MyFinder mouse and headset battery indicator. If it is already running, click its menu-bar icon.' }
    )) {
        $shortcut = $shell.CreateShortcut($entry.Path)
        if (Test-Path -LiteralPath $entry.Path) {
            if ($shortcut.TargetPath -ne $trayExe -or $shortcut.Arguments -ne $entry.Arguments) {
                throw ('A different existing shortcut will not be overwritten: ' + $entry.Path)
            }
        }
        $shortcut.TargetPath = $trayExe
        $shortcut.Arguments = $entry.Arguments
        $shortcut.WorkingDirectory = $workingDirectory
        $shortcut.Description = $entry.Description
        $shortcut.IconLocation = $trayExe + ',0'
        $shortcut.WindowStyle = 1
        $shortcut.Save()
        [pscustomobject]@{
            Shortcut = $entry.Path
            Target = $shortcut.TargetPath
            Arguments = $shortcut.Arguments
            WorkingDirectory = $shortcut.WorkingDirectory
        }
    }
} finally {
    if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
