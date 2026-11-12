[CmdletBinding()]
param(
    [string]$InstallDirectory,[string]$DataDirectory,[string]$StartupDirectory,[string]$ProgramsDirectory,
    [switch]$PreserveStartup,[switch]$StopRunning,
    # Replace only these interaction boundaries in isolated installer tests.
    [scriptblock]$CanPrompt={
        [Environment]::UserInteractive -and $Host.Name -eq 'ConsoleHost' -and
            -not [Console]::IsInputRedirected -and -not ([Environment]::GetCommandLineArgs() -match '^-NonI')
    },
    [scriptblock]$ConfirmStopRunning={param($Message) Read-Host $Message},
    [scriptblock]$RequestExit={
        param($Executable)
        Start-Process -FilePath $Executable -ArgumentList '--exit' -WorkingDirectory (Split-Path -Parent $Executable) -WindowStyle Hidden
    },
    [ValidateRange(1,20000)][int]$StopTimeoutMilliseconds=20000
)
$ErrorActionPreference='Stop'
if($StopRunning){[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)}
. (Join-Path $PSScriptRoot 'Install.Common.ps1')
$source=Split-Path -Parent $PSScriptRoot
$data=if($DataDirectory){$DataDirectory}else{Get-DeviceBatteryDataDirectory}
if(-not $StartupDirectory){$StartupDirectory=[Environment]::GetFolderPath('Startup')}
if(-not $ProgramsDirectory){$ProgramsDirectory=[Environment]::GetFolderPath('Programs')}
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $previous=Read-DeviceBatteryReceipt $data
    $InstallDirectory=if($previous){$previous.InstallDirectory}else{
        Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\MyFinderDeviceBattery'
    }
}
if($StopRunning) {
    # Use the same canonical root and executable ownership rules as the transaction.
    $stopDirectory=[IO.Path]::GetFullPath($InstallDirectory).TrimEnd([char]92)
    if(@(Get-DeviceBatteryOwnedProcesses $stopDirectory).Count -gt 0) {
        $null=Assert-DeviceBatteryInstallDirectory $stopDirectory $source $data
        $cancelled="已取消。请先在托盘图标上右键选择‘退出’，再重新运行安装。"
        if(-not (& $CanPrompt)){throw $cancelled}
        try {$answer=& $ConfirmStopRunning '检测到 MyFinder 设备电量正在运行，安装需要先关闭它。是否自动关闭并继续安装？[Y/N]'}
        catch {throw $cancelled}
        if($answer -cnotmatch '^[Yy]$'){throw $cancelled}
        $executable=Resolve-DeviceBatteryPayloadPath $stopDirectory 'outputs/AtkMouseBattery.exe'
        $clock=[Diagnostics.Stopwatch]::StartNew()
        $null=& $RequestExit $executable
        while(@(Get-DeviceBatteryOwnedProcesses $stopDirectory).Count -gt 0) {
            $remaining=$StopTimeoutMilliseconds-$clock.ElapsedMilliseconds
            if($remaining -le 0){throw "程序没有在 20 秒内退出。请在任务栏托盘图标上右键选择‘退出’，然后重新运行安装。"}
            Start-Sleep -Milliseconds ([int][Math]::Min(250,$remaining))
        }
    }
}
Install-DeviceBattery -SourceRoot $source -InstallDirectory $InstallDirectory -DataDirectory $data `
    -StartupDirectory $StartupDirectory -ProgramsDirectory $ProgramsDirectory -PreserveStartup:$PreserveStartup
