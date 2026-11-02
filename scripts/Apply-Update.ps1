[CmdletBinding()]
param([switch]$Probe,[string]$ExecutablePath,[string]$DataDirectory,[string]$Version,[string]$WorkDirectory,[int]$ParentId,[string]$ParentStartUtc)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Update.Common.ps1')
if($Probe){
    if(Test-DeviceBatteryUpdateInstallation $ExecutablePath $DataDirectory){[Console]::Write('stable')}else{[Console]::Write('portable')}
    exit 0
}
Invoke-DeviceBatteryUpdate -Executable $ExecutablePath -DataDirectory $DataDirectory -Version $Version -WorkDirectory $WorkDirectory -ParentId $ParentId -ParentStartUtc $ParentStartUtc
