[CmdletBinding()]
param([string]$InstallDirectory,[string]$DataDirectory,[string]$StartupDirectory,[string]$ProgramsDirectory)
$ErrorActionPreference='Stop'
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
Install-DeviceBattery -SourceRoot $source -InstallDirectory $InstallDirectory -DataDirectory $data `
    -StartupDirectory $StartupDirectory -ProgramsDirectory $ProgramsDirectory
