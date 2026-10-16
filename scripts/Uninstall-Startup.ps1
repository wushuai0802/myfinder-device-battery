[CmdletBinding()]
param([switch]$RemoveStartMenu)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Install.Common.ps1')
Uninstall-DeviceBatteryStartup -SourceRoot (Split-Path -Parent $PSScriptRoot) `
    -DataDirectory (Get-DeviceBatteryDataDirectory) -StartupDirectory ([Environment]::GetFolderPath('Startup')) `
    -ProgramsDirectory ([Environment]::GetFolderPath('Programs')) -RemoveStartMenu:$RemoveStartMenu
