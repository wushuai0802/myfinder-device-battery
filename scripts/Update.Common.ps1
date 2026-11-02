# Update policy delegates directory/receipt ownership to the existing installer.
. (Join-Path $PSScriptRoot 'Install.Common.ps1')

function Test-DeviceBatteryUpdateInstallation([string]$Executable,[string]$DataDirectory) {
    try {
        $exe=[IO.Path]::GetFullPath($Executable)
        if([IO.Path]::GetFileName($exe) -cne 'AtkMouseBattery.exe' -or -not (Test-Path -LiteralPath $exe -PathType Leaf)){return $false}
        $outputs=Split-Path -Parent $exe
        if((Split-Path -Leaf $outputs) -ine 'outputs'){return $false}
        $root=Split-Path -Parent $outputs
        $receipt=Read-DeviceBatteryReceipt $DataDirectory
        if(-not $receipt -or [IO.Path]::GetFullPath($receipt.InstallDirectory).TrimEnd([char]92) -ine $root.TrimEnd([char]92)){return $false}
        if(-not (Test-Path -LiteralPath (Join-Path $root '.myfinder-device-battery-install.json') -PathType Leaf)){return $false}
        $null=Assert-DeviceBatteryInstallDirectory $root $root $DataDirectory
        return $true
    }catch{return $false}
}

function ConvertTo-DeviceBatteryUpdateArgument([string]$Value) {
    # All callers supply filesystem paths, decimal IDs, ISO timestamps or strict versions.
    if($Value.Contains('"') -or $Value.Contains([char]0) -or $Value.EndsWith('\')){throw 'Invalid update process argument.'}
    return '"'+$Value+'"'
}

function Wait-DeviceBatteryUpdateParent([int]$ParentId,[string]$ParentStartUtc,[string]$Executable,[int]$TimeoutMilliseconds=60000) {
    $parent=$null
    try {
        try{$parent=[Diagnostics.Process]::GetProcessById($ParentId)}catch [ArgumentException]{return $true}
        $expected=[DateTime]::Parse($ParentStartUtc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        if($parent.StartTime.ToUniversalTime().Ticks -ne $expected.Ticks -or $parent.MainModule.FileName -ine $Executable){throw 'The waiting process identity changed.'}
        # Never terminate the application or its readers. Normal --exit must finish settings writes.
        return $parent.WaitForExit($TimeoutMilliseconds)
    }catch {
        # The normal exit may race process identity inspection; a confirmed exited parent is safe.
        if($parent){try{if($parent.HasExited){return $true}}catch{}}
        throw
    }finally{if($parent){$parent.Dispose()}}
}

function Confirm-DeviceBatteryUpdatePackage([string]$Executable,[string]$WorkDirectory,[string]$Version) {
    $package=Join-Path $WorkDirectory 'verified'
    $arguments=@('--verify-update-package',(Join-Path $WorkDirectory ('myfinder-device-battery-v'+$Version+'-windows-x64.zip')),
        (Join-Path $WorkDirectory 'SHA256SUMS.txt'),(Join-Path $WorkDirectory 'SHA256SUMS.txt.sig'),$Version,$package)
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$Executable;$info.Arguments=(@($arguments|ForEach-Object {ConvertTo-DeviceBatteryUpdateArgument $_}) -join ' ')
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $process=[Diagnostics.Process]::Start($info)
    try {
        if(-not $process.WaitForExit(120000)){throw 'The independent signature/package verification timed out.'}
        if($process.ExitCode -ne 0){throw 'The independent signature/package verification failed.'}
    }finally{$process.Dispose()}
    return $package
}

function Invoke-DeviceBatteryUpdateInstaller([string]$Package,[string]$InstallDirectory,[string]$DataDirectory) {
    # Only this verified package's normal entry point may mutate the installation.
    & (Join-Path $Package 'scripts\Install-Startup.ps1') -InstallDirectory $InstallDirectory -DataDirectory $DataDirectory -PreserveStartup
}

function Start-DeviceBatteryUpdatedApp([string]$Executable) {
    $null=Start-Process -FilePath $Executable -ArgumentList '--show' -WorkingDirectory (Split-Path -Parent $Executable) -WindowStyle Hidden -PassThru
}

function Write-DeviceBatteryUpdateResult([string]$DataDirectory,[string]$Status,[string]$Version,[string]$Reason) {
    New-Item -ItemType Directory -Path $DataDirectory -Force|Out-Null
    $result=[ordered]@{schema=1;status=$Status;version=$Version;reason=$Reason.Substring(0,[Math]::Min(1500,$Reason.Length));timeUtc=[DateTime]::UtcNow.ToString('o');notified=$false}
    $temporary=Join-Path $DataDirectory ('update-result-'+[Guid]::NewGuid().ToString('N')+'.tmp')
    $path=Join-Path $DataDirectory 'update-result.json'
    try {
        [IO.File]::WriteAllText($temporary,($result|ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
        if(Test-Path -LiteralPath $path){[IO.File]::Replace($temporary,$path,[NullString]::Value)}else{[IO.File]::Move($temporary,$path)}
        $log=Join-Path $DataDirectory 'update.log'
        if((Test-Path -LiteralPath $log) -and (Get-Item -LiteralPath $log).Length -gt 1048576){[IO.File]::WriteAllText($log,'')}
        [IO.File]::AppendAllText($log,([DateTime]::UtcNow.ToString('o')+' '+$Status+' '+$Version+' '+($Reason -replace '[\r\n]',' ')+[Environment]::NewLine),(New-Object Text.UTF8Encoding($false)))
    }finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}
}

function Assert-DeviceBatteryUpdateWork([string]$DataDirectory,[string]$WorkDirectory,[string]$Version) {
    if($Version -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-beta\.(0|[1-9][0-9]*))?\z'){throw 'Invalid update version.'}
    $updates=[IO.Path]::GetFullPath((Join-Path $DataDirectory 'updates')).TrimEnd([char]92)
    $work=[IO.Path]::GetFullPath($WorkDirectory).TrimEnd([char]92)
    if((Split-Path -Parent $work) -ine (Join-Path $updates $Version) -or (Split-Path -Leaf $work) -cnotmatch '\Arun-[0-9a-f]{32}\z'){throw 'The update work directory is outside the application data area.'}
    $null=Resolve-DeviceBatteryPayloadPath $updates ($Version+'/'+(Split-Path -Leaf $work)+'/sentinel')
    return $work
}

function Remove-DeviceBatteryUpdateWork([string]$WorkDirectory) {
    # Inspect every descendant before the recursive delete; never follow a reparse point.
    $pending=New-Object 'System.Collections.Generic.Stack[string]';$pending.Push($WorkDirectory)
    while($pending.Count -gt 0){
        $directory=$pending.Pop()
        if(-not (Test-Path -LiteralPath $directory)){continue}
        if(((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Linked update cleanup refused.'}
        foreach($item in (Get-ChildItem -LiteralPath $directory -Force)){
            if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Linked update cleanup refused.'}
            if($item.PSIsContainer){$pending.Push($item.FullName)}
        }
    }
    if(Test-Path -LiteralPath $WorkDirectory){Remove-Item -LiteralPath $WorkDirectory -Recurse -Force}
    $versionDirectory=Split-Path -Parent $WorkDirectory
    if((Test-Path -LiteralPath $versionDirectory) -and @(Get-ChildItem -LiteralPath $versionDirectory -Force).Count -eq 0){Remove-Item -LiteralPath $versionDirectory -Force}
}

function Invoke-DeviceBatteryUpdate {
    param([string]$Executable,[string]$DataDirectory,[string]$Version,[string]$WorkDirectory,[int]$ParentId,[string]$ParentStartUtc)
    $work=Assert-DeviceBatteryUpdateWork $DataDirectory $WorkDirectory $Version
    $installDirectory=Split-Path -Parent (Split-Path -Parent ([IO.Path]::GetFullPath($Executable)))
    $stopped=$false;$oldVersion=$null
    try {
        if(-not (Test-DeviceBatteryUpdateInstallation $Executable $DataDirectory)){throw 'The running copy is not the recorded stable installation.'}
        $oldVersion=(Get-Item -LiteralPath $Executable).VersionInfo.ProductVersion
        if(-not (Wait-DeviceBatteryUpdateParent $ParentId $ParentStartUtc $Executable)){throw 'Timed out waiting for the application to exit normally; no installation was attempted.'}
        $stopped=$true
        if(-not (Test-DeviceBatteryUpdateInstallation $Executable $DataDirectory)){throw 'The stable installation changed while waiting.'}
        $package=Confirm-DeviceBatteryUpdatePackage $Executable $work $Version
        $null=Invoke-DeviceBatteryUpdateInstaller $package $installDirectory $DataDirectory
        if((Read-DeviceBatteryReceipt $DataDirectory).Version -cne $Version){throw 'The installer did not record the expected version.'}
        Write-DeviceBatteryUpdateResult $DataDirectory 'success' $Version ''
        Start-DeviceBatteryUpdatedApp $Executable
    }catch {
        $reason=$_.Exception.Message
        Write-DeviceBatteryUpdateResult $DataDirectory 'failed' $Version $reason
        if($stopped){
            # An incomplete rollback must not launch an unverified mixture of versions.
            try {
                $restored=Read-DeviceBatteryPackageManifest $installDirectory
                if($restored.version -cne $oldVersion){throw 'Old-version recovery verification failed.'}
                Start-DeviceBatteryUpdatedApp $Executable
            }catch {Write-DeviceBatteryUpdateResult $DataDirectory 'failed' $Version ($reason+' Recovery restart failed: '+$_.Exception.Message)}
        }
    }finally{Remove-DeviceBatteryUpdateWork $work}
}
