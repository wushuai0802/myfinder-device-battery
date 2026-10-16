$script:DeviceBatteryAppId = 'myfinder-device-battery'

function Get-DeviceBatteryDataDirectory {
    Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MyFinderDeviceBattery'
}

function Get-DeviceBatteryShortcutName {
    'MyFinder ' + ([string][char]0x8bbe) + [char]0x5907 + [char]0x7535 + [char]0x91cf + '.lnk'
}

function Get-DeviceBatteryShortcutNames {
    $name = Get-DeviceBatteryShortcutName
    $names = @($name)
    foreach ($page in @(936, 1252, [Text.Encoding]::Default.CodePage)) {
        $names += [Text.Encoding]::GetEncoding($page).GetString([Text.Encoding]::UTF8.GetBytes($name))
    }
    @($names | Select-Object -Unique)
}

function Read-DeviceBatteryReceipt([string]$DataDirectory) {
    $path = Join-Path $DataDirectory 'install.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    $receipt = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($receipt.AppId -ne $script:DeviceBatteryAppId -or [string]::IsNullOrWhiteSpace($receipt.InstallDirectory)) {
        throw 'The existing installation receipt is not valid.'
    }
    return $receipt
}

function Test-DeviceBatteryShortcut($Shortcut, [string[]]$KnownExecutables) {
    if ([IO.Path]::GetFileName($Shortcut.TargetPath) -ine 'AtkMouseBattery.exe') { return $false }
    if ([string]$Shortcut.Arguments -notin @('', '--show')) { return $false }
    if ($Shortcut.TargetPath -in $KnownExecutables) { return $true }
    return $Shortcut.Description -like 'Start the MyFinder mouse and headset battery indicator*' -or
        $Shortcut.Description -like 'Open the MyFinder mouse and headset battery indicator*' -or
        $Shortcut.Description -like 'MyFinder Device Battery*'
}

function Get-DeviceBatteryStartupApproval([string]$Name) {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder'
    $value = Get-ItemProperty -LiteralPath $key -Name $Name -ErrorAction SilentlyContinue
    if ($null -ne $value) { return ,([byte[]]$value.$Name) }
    return $null
}

function Assert-DeviceBatteryInstallDirectory([string]$Path, [string]$SourceRoot, [string]$DataDirectory) {
    if (-not [IO.Path]::IsPathRooted($Path)) { throw 'The installation directory must be an absolute path.' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd([char]92)
    $forbidden = @([IO.Path]::GetPathRoot($full), $DataDirectory,
        [Environment]::GetFolderPath('UserProfile'), [Environment]::GetFolderPath('LocalApplicationData'),
        [Environment]::GetFolderPath('Windows'), [Environment]::GetFolderPath('ProgramFiles'),
        [Environment]::GetFolderPath('ProgramFilesX86'))
    foreach ($item in $forbidden) {
        if (-not [string]::IsNullOrWhiteSpace($item) -and $full -ieq $item.TrimEnd([char]92)) {
            throw 'Choose a separate application directory, not a system or data directory.'
        }
    }
    if (Test-Path -LiteralPath $full) {
        $directory = Get-Item -LiteralPath $full
        if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'The installation target must be a regular directory.'
        }
        if ($full -ieq $SourceRoot.TrimEnd([char]92) -or @(Get-ChildItem -LiteralPath $full -Force).Count -gt 0) {
            $marker = Join-Path $full '.myfinder-device-battery-install.json'
            if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'The target contains unrelated files; nothing was overwritten.' }
            $identity = Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($identity.AppId -ne $script:DeviceBatteryAppId) { throw 'The target belongs to another application.' }
        }
    }
    return $full
}

function Install-DeviceBattery {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$SourceRoot,
        [Parameter(Mandatory=$true)][string]$InstallDirectory,
        [Parameter(Mandatory=$true)][string]$DataDirectory,
        [Parameter(Mandatory=$true)][string]$StartupDirectory,
        [Parameter(Mandatory=$true)][string]$ProgramsDirectory
    )
    $source = [IO.Path]::GetFullPath($SourceRoot).TrimEnd([char]92)
    $target = Assert-DeviceBatteryInstallDirectory $InstallDirectory $source $DataDirectory
    $exe = Join-Path $target 'outputs\AtkMouseBattery.exe'
    $sourceExe = Join-Path $source 'outputs\AtkMouseBattery.exe'
    $payload = @('outputs\AtkMouseBattery.exe', 'scripts\Install.Common.ps1',
        'scripts\Install-Startup.ps1', 'scripts\Uninstall-Startup.ps1')
    foreach ($relative in $payload) {
        if (-not (Test-Path -LiteralPath (Join-Path $source $relative) -PathType Leaf)) { throw ('Package file missing: ' + $relative) }
    }
    foreach ($relative in @('outputs\AtkBatteryProbe.exe', 'README.md', 'scripts\Start.ps1',
        'docs\images\cards-light.png', 'docs\images\cards-dark.png')) {
        if (Test-Path -LiteralPath (Join-Path $source $relative) -PathType Leaf) { $payload += $relative }
    }
    foreach ($launcher in @(Get-ChildItem -LiteralPath $source -Filter '*.bat' -File)) { $payload += $launcher.Name }
    $receipt = Read-DeviceBatteryReceipt $DataDirectory
    $known = @($sourceExe, $exe)
    if ($receipt) { $known += Join-Path $receipt.InstallDirectory 'outputs\AtkMouseBattery.exe' }
    $name = Get-DeviceBatteryShortcutName
    $shell = New-Object -ComObject WScript.Shell
    $undo = New-Object 'System.Collections.Generic.List[object]'
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ('myfinder-install-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    try {
        $links = @(
            @{ Path=(Join-Path $StartupDirectory $name); Arguments=''; Description='MyFinder Device Battery - start quietly at login.' },
            @{ Path=(Join-Path $ProgramsDirectory $name); Arguments='--show'; Description='MyFinder Device Battery - open the device panel.' }
        )
        foreach ($link in $links) {
            if ((Test-Path -LiteralPath $link.Path) -and -not (Test-DeviceBatteryShortcut ($shell.CreateShortcut($link.Path)) $known)) {
                throw ('An unrelated shortcut will not be overwritten: ' + $link.Path)
            }
        }
        foreach ($candidate in (Get-DeviceBatteryShortcutNames)) {
            $approval = Get-DeviceBatteryStartupApproval $candidate
            if ($null -ne $approval -and $approval.Length -gt 0 -and $approval[0] -notin @(2,6)) {
                throw 'Windows previously disabled this startup item. Enable it in Startup Apps before installing autostart.'
            }
        }
        $exeChanged = -not (Test-Path -LiteralPath $exe)
        if (-not $exeChanged) { $exeChanged = (Get-FileHash $sourceExe).Hash -ne (Get-FileHash $exe).Hash }
        if ($exeChanged) {
            foreach ($process in @(Get-Process -Name AtkMouseBattery -ErrorAction SilentlyContinue)) {
                if ($process.Path -ieq $exe) { throw 'Exit Device Battery from its tray menu before updating the installed program.' }
            }
            if (Test-Path -LiteralPath $exe) {
                try { $writeCheck=[IO.File]::Open($exe,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::Read);$writeCheck.Dispose() }
                catch { throw 'The installed executable is in use by another process; close it before updating.' }
            }
        }
        foreach ($relative in $payload) {
            $from = Join-Path $source $relative; $to = Join-Path $target $relative
            if ((Test-Path -LiteralPath $to) -and (Get-FileHash $from).Hash -eq (Get-FileHash $to).Hash) { continue }
            $backup = $null
            if (Test-Path -LiteralPath $to) { $backup=Join-Path $temporary ([Guid]::NewGuid().ToString('N')); Copy-Item -LiteralPath $to -Destination $backup }
            $undo.Add(@{ Path=$to; Backup=$backup })
            New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
            Copy-Item -LiteralPath $from -Destination $to -Force
            if ((Get-FileHash $from).Hash -ne (Get-FileHash $to).Hash) { throw ('Copy verification failed: ' + $relative) }
        }
        $manifest = [ordered]@{ AppId=$script:DeviceBatteryAppId; Schema=1; InstallDirectory=$target;
            ExecutablePath=$exe; InstalledAtUtc=(Get-Date).ToUniversalTime().ToString('o') }
        foreach ($destination in @((Join-Path $target '.myfinder-device-battery-install.json'), (Join-Path $DataDirectory 'install.json'))) {
            $backup = $null
            if (Test-Path -LiteralPath $destination) { $backup=Join-Path $temporary ([Guid]::NewGuid().ToString('N')); Copy-Item -LiteralPath $destination -Destination $backup }
            $undo.Add(@{ Path=$destination; Backup=$backup })
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            [IO.File]::WriteAllText($destination,($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        }
        foreach ($entry in $links) {
            $backup = $null
            if (Test-Path -LiteralPath $entry.Path) { $backup=Join-Path $temporary ([Guid]::NewGuid().ToString('N')); Copy-Item -LiteralPath $entry.Path -Destination $backup }
            $undo.Add(@{ Path=$entry.Path; Backup=$backup })
            New-Item -ItemType Directory -Path (Split-Path -Parent $entry.Path) -Force | Out-Null
            $shortcut=$shell.CreateShortcut($entry.Path)
            $shortcut.TargetPath=$exe; $shortcut.Arguments=$entry.Arguments
            $shortcut.WorkingDirectory=Split-Path -Parent $exe
            $shortcut.Description=$entry.Description; $shortcut.IconLocation=$exe+',0'; $shortcut.WindowStyle=1
            $shortcut.Save()
            $verified=$shell.CreateShortcut($entry.Path)
            if ($verified.TargetPath -ine $exe -or $verified.Arguments -ne $entry.Arguments) { throw 'Shortcut verification failed.' }
        }
        foreach ($oldName in (Get-DeviceBatteryShortcutNames | Where-Object { $_ -cne $name })) {
            foreach ($folder in @($StartupDirectory,$ProgramsDirectory)) {
                $old=Join-Path $folder $oldName
                if ((Test-Path -LiteralPath $old) -and (Test-DeviceBatteryShortcut ($shell.CreateShortcut($old)) $known)) {
                    $backup=Join-Path $temporary ([Guid]::NewGuid().ToString('N')); Copy-Item -LiteralPath $old -Destination $backup
                    $undo.Add(@{ Path=$old; Backup=$backup }); Remove-Item -LiteralPath $old -Force
                }
            }
        }
        [pscustomobject]@{ InstalledExecutable=$exe; StartupShortcut=$links[0].Path; ManualShortcut=$links[1].Path; SettingsDirectory=$DataDirectory }
    } catch {
        for ($i=$undo.Count-1; $i -ge 0; $i--) {
            $item=$undo[$i]
            if ($item.Backup) { Copy-Item -LiteralPath $item.Backup -Destination $item.Path -Force }
            elseif (Test-Path -LiteralPath $item.Path -PathType Leaf) { Remove-Item -LiteralPath $item.Path -Force }
        }
        throw
    } finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        $resolved=[IO.Path]::GetFullPath($temporary)
        if ((Split-Path -Parent $resolved) -ieq ([IO.Path]::GetTempPath()).TrimEnd([char]92) -and (Split-Path -Leaf $resolved) -match '^myfinder-install-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}

function Uninstall-DeviceBatteryStartup {
    [CmdletBinding()]
    param([string]$SourceRoot,[string]$DataDirectory,[string]$StartupDirectory,[string]$ProgramsDirectory,[switch]$RemoveStartMenu)
    $receipt=Read-DeviceBatteryReceipt $DataDirectory
    $known=@((Join-Path $SourceRoot 'outputs\AtkMouseBattery.exe'))
    if ($receipt) { $known+=Join-Path $receipt.InstallDirectory 'outputs\AtkMouseBattery.exe' }
    $folders=@($StartupDirectory)
    if ($RemoveStartMenu) { $folders+=$ProgramsDirectory }
    $shell=New-Object -ComObject WScript.Shell
    try {
        foreach ($folder in $folders) {
            foreach ($name in (Get-DeviceBatteryShortcutNames)) {
                $path=Join-Path $folder $name
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
                if (-not (Test-DeviceBatteryShortcut ($shell.CreateShortcut($path)) $known)) { throw ('Unrelated shortcut was preserved: '+$path) }
                Remove-Item -LiteralPath $path -Force -ErrorAction Stop
                if (Test-Path -LiteralPath $path) { throw ('Shortcut was not removed: '+$path) }
            }
        }
        [pscustomobject]@{ AutostartDisabled=$true; ManualLauncherPreserved=(-not $RemoveStartMenu.IsPresent); SettingsPreserved=$true }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
