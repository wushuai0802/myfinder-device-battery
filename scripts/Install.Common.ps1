. (Join-Path $PSScriptRoot 'Manifest.Common.ps1')

function New-DeviceBatteryShortcutShell {
    if (-not ('DeviceBatterySetup.ShortcutShell' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
namespace DeviceBatterySetup {
    [ComImport,Guid("00021401-0000-0000-C000-000000000046")] class ShellLink {}
    [ComImport,Guid("000214F9-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW {
        void GetPath([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,int length,IntPtr findData,uint flags);
        void GetIDList(out IntPtr list); void SetIDList(IntPtr list);
        void GetDescription([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder value,int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetWorkingDirectory([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder value,int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetArguments([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder value,int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetHotkey(out short value); void SetHotkey(short value);
        void GetShowCmd(out int value); void SetShowCmd(int value);
        void GetIconLocation([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder value,int length,out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string value,int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string value,uint reserved);
        void Resolve(IntPtr window,uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string value);
    }
    public sealed class UnicodeShortcut {
        readonly string path;
        public string TargetPath {get;set;}
        public string Arguments {get;set;}
        public string WorkingDirectory {get;set;}
        public string Description {get;set;}
        public string IconLocation {get;set;}
        public int WindowStyle {get;set;}
        public UnicodeShortcut(string file) {
            path=Path.GetFullPath(file);TargetPath="";Arguments="";WorkingDirectory="";Description="";IconLocation="";WindowStyle=1;
            if(!File.Exists(path))return;
            object native=new ShellLink();
            try {
                ((IPersistFile)native).Load(path,0);var link=(IShellLinkW)native;var value=new StringBuilder(32768);
                link.GetPath(value,value.Capacity,IntPtr.Zero,4);TargetPath=value.ToString();value.Length=0;
                link.GetArguments(value,value.Capacity);Arguments=value.ToString();value.Length=0;
                link.GetWorkingDirectory(value,value.Capacity);WorkingDirectory=value.ToString();value.Length=0;
                link.GetDescription(value,value.Capacity);Description=value.ToString();value.Length=0;
                int index;link.GetIconLocation(value,value.Capacity,out index);IconLocation=value.ToString()+","+index;
                int show;link.GetShowCmd(out show);WindowStyle=show;
            } finally {Marshal.FinalReleaseComObject(native);}
        }
        public void Save() {
            object native=new ShellLink();
            try {
                var link=(IShellLinkW)native;link.SetPath(TargetPath??"");link.SetArguments(Arguments??"");
                link.SetWorkingDirectory(WorkingDirectory??"");link.SetDescription(Description??"");link.SetShowCmd(WindowStyle);
                string icon=IconLocation??"";int index=0,comma=icon.LastIndexOf(',');
                if(comma>=0&&Int32.TryParse(icon.Substring(comma+1),out index))icon=icon.Substring(0,comma);
                link.SetIconLocation(icon,index);((IPersistFile)native).Save(path,true);
            } finally {Marshal.FinalReleaseComObject(native);}
        }
    }
    public sealed class ShortcutShell {
        public UnicodeShortcut CreateShortcut(string path){return new UnicodeShortcut(path);}
    }
}
'@
    }
    New-Object DeviceBatterySetup.ShortcutShell
}

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
    $path=Join-Path $DataDirectory 'install.json'
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)){return $null}
    if((Get-Item -LiteralPath $path).Length -gt 2097152){throw 'The installation receipt exceeds the size limit.'}
    $receipt=Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if($receipt.AppId -cne $script:DeviceBatteryAppId -or $receipt.Schema -notin @(1,2) -or
        [string]::IsNullOrWhiteSpace($receipt.InstallDirectory) -or -not [IO.Path]::IsPathRooted($receipt.InstallDirectory)) {
        throw 'The existing installation receipt is not valid.'
    }
    if($receipt.Schema -eq 2) {
        if(@($receipt.Files).Count -eq 0 -or @($receipt.Files).Count -gt 5001){throw 'Invalid installation file receipt.'}
        $seen=@{}
        foreach($entry in @($receipt.Files)) {
            $null=Resolve-DeviceBatteryPayloadPath $receipt.InstallDirectory ([string]$entry.path)
            if($seen.ContainsKey([string]$entry.path) -or [string]$entry.sha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Invalid installation ownership entry.'}
            $seen[[string]$entry.path]=$true
        }
    }
    return $receipt
}

function Test-DeviceBatteryShortcut($Shortcut,[string[]]$KnownExecutables) {
    if([IO.Path]::GetFileName($Shortcut.TargetPath) -ine 'AtkMouseBattery.exe'){return $false}
    if([string]$Shortcut.Arguments -notin @('','--show')){return $false}
    if($Shortcut.TargetPath -in $KnownExecutables){return $true}
    return $Shortcut.Description -like 'Start the MyFinder mouse and headset battery indicator*' -or
        $Shortcut.Description -like 'Open the MyFinder mouse and headset battery indicator*' -or
        $Shortcut.Description -like 'MyFinder Device Battery*'
}

function Get-DeviceBatteryStartupApproval([string]$Name) {
    $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder')
    if($null -eq $key){return $null}
    try {
        $value=$key.GetValue($Name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if($null -eq $value){return $null}
        if($value -isnot [byte[]]){throw 'Windows startup approval has an unexpected type.'}
        return ,([byte[]]$value)
    } finally {$key.Dispose()}
}

function Get-DeviceBatteryStartupState([string]$StartupDirectory,[string]$ExpectedExecutable) {
    try {
        $shell=New-DeviceBatteryShortcutShell
        $found=$null
        foreach($name in (Get-DeviceBatteryShortcutNames)) {
            $path=Join-Path $StartupDirectory $name
            if(Test-Path -LiteralPath $path -PathType Leaf){$found=@{Path=$path;Name=$name};break}
        }
        if(-not $found){return [pscustomobject]@{Status='missing';TargetExists=$false;ArgumentsValid=$false;Reason='Startup shortcut is missing.'}}
        $shortcut=$shell.CreateShortcut($found.Path)
        $exists=Test-Path -LiteralPath $shortcut.TargetPath -PathType Leaf
        $arguments=[string]$shortcut.Arguments -eq ''
        if(-not $exists -or -not $arguments -or ($ExpectedExecutable -and $shortcut.TargetPath -ine $ExpectedExecutable)) {
            return [pscustomobject]@{Status='invalid-target';TargetExists=$exists;ArgumentsValid=$arguments;Reason='Startup target or arguments do not match this installation.'}
        }
        $approval=Get-DeviceBatteryStartupApproval $found.Name
        if($null -ne $approval -and ($approval.Length -lt 1 -or $approval[0] -notin @(2,3,6,7))) {
            return [pscustomobject]@{Status='unknown';TargetExists=$true;ArgumentsValid=$true;Reason='Windows startup approval is not recognized.'}
        }
        $disabled=$null -ne $approval -and $approval[0] -in @(3,7)
        return [pscustomobject]@{Status=$(if($disabled){'disabled'}else{'enabled'});TargetExists=$true;ArgumentsValid=$true;Reason=$(if($disabled){'Windows disabled this startup entry.'}else{'Shortcut target and Windows startup approval are valid.'})}
    } catch {return [pscustomobject]@{Status='unknown';TargetExists=$false;ArgumentsValid=$false;Reason='Startup state could not be inspected.'}}
}

function Assert-DeviceBatteryInstallDirectory([string]$Path,[string]$SourceRoot,[string]$DataDirectory) {
    if(-not [IO.Path]::IsPathRooted($Path)){throw 'The installation directory must be an absolute path.'}
    $full=[IO.Path]::GetFullPath($Path).TrimEnd([char]92)
    $forbidden=@([IO.Path]::GetPathRoot($full),$DataDirectory,
        [Environment]::GetFolderPath('UserProfile'),[Environment]::GetFolderPath('LocalApplicationData'),
        [Environment]::GetFolderPath('Windows'),[Environment]::GetFolderPath('ProgramFiles'),
        [Environment]::GetFolderPath('ProgramFilesX86'))
    foreach($item in $forbidden) {
        if(-not [string]::IsNullOrWhiteSpace($item) -and $full -ieq $item.TrimEnd([char]92)){
            throw 'Choose a separate application directory, not a system or data directory.'
        }
    }
    $null=Resolve-DeviceBatteryPayloadPath $full 'outputs/AtkMouseBattery.exe'
    if(Test-Path -LiteralPath $full) {
        if(-not (Get-Item -LiteralPath $full).PSIsContainer){throw 'The installation target must be a regular directory.'}
        if($full -ieq $SourceRoot.TrimEnd([char]92) -or @(Get-ChildItem -LiteralPath $full -Force).Count -gt 0) {
            $marker=Join-Path $full '.myfinder-device-battery-install.json'
            if(-not (Test-Path -LiteralPath $marker -PathType Leaf)){throw 'The target contains unrelated files; nothing was overwritten.'}
            $identity=Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
            if($identity.AppId -cne $script:DeviceBatteryAppId -or $identity.Schema -notin @(1,2) -or
                [IO.Path]::GetFullPath($identity.InstallDirectory).TrimEnd([char]92) -ine $full){throw 'The target ownership marker is invalid.'}
        }
    }
    return $full
}

function Get-DeviceBatteryOwnedProcesses([string]$InstallDirectory) {
    $owned=@((Join-Path $InstallDirectory 'outputs\AtkMouseBattery.exe'),
        (Join-Path $InstallDirectory 'engine\runtime\python.exe'),(Join-Path $InstallDirectory 'engine\runtime\pythonw.exe'))
    foreach($process in @(Get-Process -Name AtkMouseBattery,python,pythonw -ErrorAction SilentlyContinue)) {
        try {if($process.Path -in $owned){$process}}catch{}
    }
}

function Assert-DeviceBatteryStopped([string]$InstallDirectory,[object[]]$ChangedPaths) {
    if(@(Get-DeviceBatteryOwnedProcesses $InstallDirectory).Count -gt 0) {
        throw 'Exit Device Battery and its portable reader before updating any installed payload file.'
    }
    foreach($path in @($ChangedPaths)) {
        if(-not (Test-Path -LiteralPath $path -PathType Leaf)){continue}
        try {$handle=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::Read);$handle.Dispose()}
        catch {throw 'An installed payload file is in use or not writable. Close the application before updating.'}
    }
}

function Add-DeviceBatteryUndo($Undo,[string]$Path,[string]$Temporary) {
    $backup=$null
    if(Test-Path -LiteralPath $Path) {
        if(-not (Test-Path -LiteralPath $Path -PathType Leaf)){throw 'An installation file path is occupied by a directory.'}
        $backup=Join-Path $Temporary ([Guid]::NewGuid().ToString('N'))
        Copy-Item -LiteralPath $Path -Destination $backup -ErrorAction Stop
    }
    $Undo.Add(@{Path=$Path;Backup=$backup})
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
    if([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or [Environment]::OSVersion.Version.Major -lt 10 -or -not [Environment]::Is64BitOperatingSystem){throw 'Windows 10/11 x64 is required.'}
    $framework=[Microsoft.Win32.Registry]::GetValue('HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full','Release',$null)
    if($null -eq $framework -or [int]$framework -lt 461808){throw 'The Windows .NET Framework 4.7.2 or newer runtime is required.'}
    $source=[IO.Path]::GetFullPath($SourceRoot).TrimEnd([char]92)
    $package=Read-DeviceBatteryPackageManifest $source
    $target=Assert-DeviceBatteryInstallDirectory $InstallDirectory $source $DataDirectory
    $exe=Join-Path $target 'outputs\AtkMouseBattery.exe'
    $receipt=Read-DeviceBatteryReceipt $DataDirectory
    $marker=Join-Path $target '.myfinder-device-battery-install.json'
    $previous=$null
    if(Test-Path -LiteralPath $marker -PathType Leaf) {
        $previous=Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
        if($previous.Schema -eq 2) {
            if(@($previous.Files).Count -eq 0 -or @($previous.Files).Count -gt 5001){throw 'Invalid previous ownership file count.'}
            $seen=@{}
            foreach($entry in @($previous.Files)) {
                $null=Resolve-DeviceBatteryPayloadPath $target ([string]$entry.path)
                if($seen.ContainsKey([string]$entry.path) -or [string]$entry.sha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Invalid previous ownership list.'}
                $seen[[string]$entry.path]=$true
            }
        }
    }
    $files=@($package.files)+@([pscustomobject]@{path='package-manifest.json';length=(Get-Item -LiteralPath (Join-Path $source 'package-manifest.json')).Length;sha256=(Get-FileHash -LiteralPath (Join-Path $source 'package-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()})
    $newPaths=@{};$oldPaths=@{};$changed=New-Object 'System.Collections.Generic.List[object]';$obsolete=New-Object 'System.Collections.Generic.List[object]'
    if($previous -and $previous.Schema -eq 2){foreach($entry in @($previous.Files)){$oldPaths[[string]$entry.path]=$entry}}
    foreach($entry in $files) {
        $relative=[string]$entry.path;$newPaths[$relative]=$true
        $to=Resolve-DeviceBatteryPayloadPath $target $relative
        if(Test-Path -LiteralPath $to) {
            if(-not (Test-Path -LiteralPath $to -PathType Leaf)){throw 'An incoming file is occupied by an existing directory.'}
            if($previous -and $previous.Schema -eq 2 -and -not $oldPaths.ContainsKey($relative)){throw ('An unowned target file was preserved: '+$relative)}
            if((Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -ieq $entry.sha256){continue}
        }
        $changed.Add(@{Relative=$relative;Destination=$to;Entry=$entry})
    }
    foreach($relative in @($oldPaths.Keys)) {
        if($newPaths.ContainsKey($relative)){continue}
        $to=Resolve-DeviceBatteryPayloadPath $target $relative
        if(Test-Path -LiteralPath $to -PathType Leaf) {
            # A user-modified obsolete file is no longer ours to delete.
            if((Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -ieq $oldPaths[$relative].sha256){$obsolete.Add($to)}
        }
    }
    $payloadChanged=$changed.Count -gt 0 -or $obsolete.Count -gt 0
    if($payloadChanged){Assert-DeviceBatteryStopped $target (@($changed | ForEach-Object {$_.Destination})+$obsolete.ToArray())}
    $known=@((Join-Path $source 'outputs\AtkMouseBattery.exe'),$exe)
    if($receipt){$known+=Join-Path $receipt.InstallDirectory 'outputs\AtkMouseBattery.exe'}
    $name=Get-DeviceBatteryShortcutName;$shell=New-DeviceBatteryShortcutShell
    $links=@(
        @{Path=(Join-Path $StartupDirectory $name);Arguments='';Description='MyFinder Device Battery - start quietly at login.'},
        @{Path=(Join-Path $ProgramsDirectory $name);Arguments='--show';Description='MyFinder Device Battery - open the device panel.'}
    )
    foreach($link in $links) {
        if((Test-Path -LiteralPath $link.Path) -and -not (Test-DeviceBatteryShortcut ($shell.CreateShortcut($link.Path)) $known)){
            throw ('An unrelated shortcut will not be overwritten: '+$link.Path)
        }
    }
    foreach($candidate in (Get-DeviceBatteryShortcutNames)) {
        $approval=Get-DeviceBatteryStartupApproval $candidate
        if($null -ne $approval -and ($approval.Length -lt 1 -or $approval[0] -notin @(2,6))){
            throw 'Windows previously disabled or has an unknown decision for this startup item. Review Startup Apps before installing autostart.'
        }
    }
    $temporary=Join-Path ([IO.Path]::GetTempPath()) ('myfinder-install-'+[Guid]::NewGuid().ToString('N'))
    $stage=Join-Path $temporary 'candidate';$undo=New-Object 'System.Collections.Generic.List[object]'
    $rollbackErrors=$null
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    try {
        # Verify a complete independent candidate before the first target mutation.
        foreach($entry in $files) {
            $to=Resolve-DeviceBatteryPayloadPath $stage ([string]$entry.path)
            New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
            Copy-Item -LiteralPath (Resolve-DeviceBatteryPayloadPath $source ([string]$entry.path)) -Destination $to -ErrorAction Stop
        }
        $null=Read-DeviceBatteryPackageManifest $stage
        if($payloadChanged){Assert-DeviceBatteryStopped $target (@($changed | ForEach-Object {$_.Destination})+$obsolete.ToArray())}
        foreach($item in $changed) {
            Add-DeviceBatteryUndo $undo $item.Destination $temporary
            New-Item -ItemType Directory -Path (Split-Path -Parent $item.Destination) -Force | Out-Null
            Copy-Item -LiteralPath (Resolve-DeviceBatteryPayloadPath $stage $item.Relative) -Destination $item.Destination -Force -ErrorAction Stop
            if((Get-FileHash -LiteralPath $item.Destination -Algorithm SHA256).Hash -ine $item.Entry.sha256){throw ('Copy verification failed: '+$item.Relative)}
        }
        foreach($path in $obsolete){Add-DeviceBatteryUndo $undo $path $temporary;Remove-Item -LiteralPath $path -Force -ErrorAction Stop}
        $installed=[ordered]@{AppId=$script:DeviceBatteryAppId;Schema=2;Version=$package.version;ConfigSchema=$package.configSchema;EngineSchema=$package.engineSchema;
            InstallDirectory=$target;ExecutablePath=$exe;InstalledAtUtc=(Get-Date).ToUniversalTime().ToString('o');Files=$files}
        foreach($path in @($marker,(Join-Path $DataDirectory 'install.json'))) {
            Add-DeviceBatteryUndo $undo $path $temporary
            New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
            [IO.File]::WriteAllText($path,($installed | ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
        }
        foreach($entry in $links) {
            Add-DeviceBatteryUndo $undo $entry.Path $temporary
            New-Item -ItemType Directory -Path (Split-Path -Parent $entry.Path) -Force | Out-Null
            $shortcut=$shell.CreateShortcut($entry.Path);$shortcut.TargetPath=$exe;$shortcut.Arguments=$entry.Arguments
            $shortcut.WorkingDirectory=Split-Path -Parent $exe;$shortcut.Description=$entry.Description;$shortcut.IconLocation=$exe+',0';$shortcut.WindowStyle=1;$shortcut.Save()
            $verified=$shell.CreateShortcut($entry.Path)
            if($verified.TargetPath -ine $exe -or $verified.Arguments -cne $entry.Arguments){throw 'Shortcut verification failed.'}
        }
        foreach($oldName in (Get-DeviceBatteryShortcutNames | Where-Object {$_ -cne $name})) {
            foreach($folder in @($StartupDirectory,$ProgramsDirectory)) {
                $old=Join-Path $folder $oldName
                if((Test-Path -LiteralPath $old) -and (Test-DeviceBatteryShortcut ($shell.CreateShortcut($old)) $known)) {
                    Add-DeviceBatteryUndo $undo $old $temporary;Remove-Item -LiteralPath $old -Force -ErrorAction Stop
                }
            }
        }
        [pscustomobject]@{Version=$package.version;PayloadChanged=$payloadChanged;InstalledExecutable=$exe;StartupShortcut=$links[0].Path;ManualShortcut=$links[1].Path;SettingsDirectory=$DataDirectory}
    } catch {
        $failure=$_
        $rollbackErrors=New-Object 'System.Collections.Generic.List[string]'
        for($i=$undo.Count-1;$i -ge 0;$i--) {
            $item=$undo[$i]
            try {
                if($item.Backup){Copy-Item -LiteralPath $item.Backup -Destination $item.Path -Force -ErrorAction Stop}
                elseif(Test-Path -LiteralPath $item.Path -PathType Leaf){Remove-Item -LiteralPath $item.Path -Force -ErrorAction Stop}
            } catch {$rollbackErrors.Add($item.Path)}
        }
        if($rollbackErrors.Count -gt 0){throw ('Installation failed and automatic rollback is incomplete. Backup retained at '+$temporary+'. Restore from your pre-update backup.')}
        throw $failure
    } finally {
        # All paths removed recursively here were created by this invocation.
        if($rollbackErrors -and $rollbackErrors.Count -gt 0){}else{
            $resolved=[IO.Path]::GetFullPath($temporary)
            if((Split-Path -Parent $resolved) -ieq ([IO.Path]::GetTempPath()).TrimEnd([char]92) -and (Split-Path -Leaf $resolved) -match '^myfinder-install-[0-9a-f]{32}$'){
                Remove-Item -LiteralPath $resolved -Recurse -Force
            }
        }
    }
}

function Uninstall-DeviceBatteryStartup {
    [CmdletBinding()]
    param([string]$SourceRoot,[string]$DataDirectory,[string]$StartupDirectory,[string]$ProgramsDirectory,
        [switch]$RemoveStartMenu,[switch]$RemoveProgram,[switch]$RemoveData)
    $receipt=Read-DeviceBatteryReceipt $DataDirectory
    $known=@((Join-Path $SourceRoot 'outputs\AtkMouseBattery.exe'))
    if($receipt){$known+=Join-Path $receipt.InstallDirectory 'outputs\AtkMouseBattery.exe'}
    $folders=@($StartupDirectory)
    if($RemoveStartMenu -or $RemoveProgram){$folders+=$ProgramsDirectory}
    $shell=New-DeviceBatteryShortcutShell;$links=New-Object 'System.Collections.Generic.List[string]'
    foreach($folder in $folders) {
        foreach($name in (Get-DeviceBatteryShortcutNames)) {
            $path=Join-Path $folder $name
            if(-not (Test-Path -LiteralPath $path -PathType Leaf)){continue}
            if(-not (Test-DeviceBatteryShortcut ($shell.CreateShortcut($path)) $known)){throw ('Unrelated shortcut was preserved: '+$path)}
            $links.Add($path)
        }
    }
    $owned=New-Object 'System.Collections.Generic.List[string]'
    if($RemoveData) {
        $runningRoot=if($receipt){$receipt.InstallDirectory}else{$SourceRoot}
        Assert-DeviceBatteryStopped $runningRoot @()
    }
    if($RemoveProgram) {
        if(-not $receipt -or $receipt.Schema -ne 2){throw 'Program removal requires a versioned ownership receipt; reinstall first. Nothing was removed.'}
        $target=Assert-DeviceBatteryInstallDirectory $receipt.InstallDirectory $SourceRoot $DataDirectory
        Assert-DeviceBatteryStopped $target @()
        $marker=Join-Path $target '.myfinder-device-battery-install.json'
        $identity=Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
        if($identity.Schema -ne 2 -or $identity.Version -cne $receipt.Version){throw 'Installation ownership receipts do not agree.'}
        foreach($entry in @($receipt.Files)) {
            $path=Resolve-DeviceBatteryPayloadPath $target ([string]$entry.path)
            if(Test-Path -LiteralPath $path -PathType Leaf) {
                if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ieq $entry.sha256){$owned.Add($path)}
            }
        }
        $owned.Add($marker)
    }
    foreach($path in $links){Remove-Item -LiteralPath $path -Force -ErrorAction Stop}
    foreach($path in $owned){Remove-Item -LiteralPath $path -Force -ErrorAction Stop}
    if($RemoveProgram -and (Test-Path -LiteralPath (Join-Path $DataDirectory 'install.json'))){Remove-Item -LiteralPath (Join-Path $DataDirectory 'install.json') -Force}
    if($RemoveData) {
        # Delete only application-known leaf files, never recurse over user data.
        foreach($relative in @('audio-settings.json','audio-settings.json.unread.bak','readings-cache.json','wireless-cache.json','status.json','low-battery-state.txt',
            'active-ui-render.png','visual-status.json','diagnostics.json','diagnostics.log','diagnostics.log.1','diagnostics.log.2')) {
            $path=Resolve-DeviceBatteryPayloadPath $DataDirectory $relative
            if(Test-Path -LiteralPath $path -PathType Leaf){Remove-Item -LiteralPath $path -Force -ErrorAction Stop}
        }
    }
    [pscustomobject]@{AutostartDisabled=$true;ManualLauncherPreserved=(-not ($RemoveStartMenu -or $RemoveProgram));ProgramRemoved=$RemoveProgram.IsPresent;SettingsPreserved=(-not $RemoveData.IsPresent);UnknownOrModifiedFilesPreserved=$true}
}
