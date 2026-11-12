function Install-DeviceBattery {
    param($SourceRoot,$InstallDirectory,$DataDirectory,$StartupDirectory,$ProgramsDirectory,[switch]$PreserveStartup)
    [pscustomobject]@{InstallDirectory=$InstallDirectory;DataDirectory=$DataDirectory;PreserveStartup=$PreserveStartup.IsPresent}
}