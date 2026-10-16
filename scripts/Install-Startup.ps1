[CmdletBinding()]
param([string]$InstallDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Install.Common.ps1')
$source=Split-Path -Parent $PSScriptRoot
$data=Get-DeviceBatteryDataDirectory
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $previous=Read-DeviceBatteryReceipt $data
    $InstallDirectory=if($previous){$previous.InstallDirectory}else{
        Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\MyFinderDeviceBattery'
    }
}
Install-DeviceBattery -SourceRoot $source -InstallDirectory $InstallDirectory -DataDirectory $data `
    -StartupDirectory ([Environment]::GetFolderPath('Startup')) -ProgramsDirectory ([Environment]::GetFolderPath('Programs'))
