[CmdletBinding()]
param([switch]$RemoveStartMenu,[switch]$RemoveProgram,[switch]$RemoveData)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Install.Common.ps1')
Uninstall-DeviceBatteryStartup -SourceRoot (Split-Path -Parent $PSScriptRoot) `
    -DataDirectory (Get-DeviceBatteryDataDirectory) -StartupDirectory ([Environment]::GetFolderPath('Startup')) `
    -ProgramsDirectory ([Environment]::GetFolderPath('Programs')) -RemoveStartMenu:$RemoveStartMenu -RemoveProgram:$RemoveProgram -RemoveData:$RemoveData
