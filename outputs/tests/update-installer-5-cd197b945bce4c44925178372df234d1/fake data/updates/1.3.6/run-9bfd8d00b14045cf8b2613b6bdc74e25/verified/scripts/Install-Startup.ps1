param([string]$InstallDirectory,[string]$DataDirectory,[switch]$PreserveStartup)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Install.Common.ps1')
function Get-DeviceBatteryStartupApproval([string]$Name){return $null}
function Update-DeviceBatteryShellIcons([string]$Executable,[string]$StartupShortcut,[string]$ManualShortcut){}
$fixture=Split-Path -Parent $DataDirectory
Install-DeviceBattery -SourceRoot (Split-Path -Parent $PSScriptRoot) -InstallDirectory $InstallDirectory -DataDirectory $DataDirectory -StartupDirectory (Join-Path $fixture 'fake Startup') -ProgramsDirectory (Join-Path $fixture 'fake Programs') -PreserveStartup:$PreserveStartup