param(
    [Parameter(Mandatory=$true)][string]$PackPath,
    [Parameter(Mandatory=$true)][string]$PrivateKeyPath
)
$ErrorActionPreference='Stop'
# Explicit manual invocation only. Never searches for a private key.
function Assert-NoPackLink([string]$Path) {
    for($part=[IO.Path]::GetFullPath($Path);$part;$part=[IO.Path]::GetDirectoryName($part)) {
        try{$attributes=[IO.File]::GetAttributes($part)}catch [IO.FileNotFoundException]{continue}catch [IO.DirectoryNotFoundException]{continue}
        if(($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Linked signing paths are not supported.'}
    }
}
$pack=[IO.Path]::GetFullPath($PackPath)
$private=[IO.Path]::GetFullPath($PrivateKeyPath)
$signature=$pack+'.sig'
if($private -ieq $pack -or $private -ieq $signature){throw 'Private-key input must be separate from pack and signature output.'}
foreach($path in @($pack,$private,$signature)){Assert-NoPackLink $path}
if(-not ('AtkBattery.DevicePackSigning' -as [type])) {
    Add-Type -Path @((Join-Path $PSScriptRoot 'DevicePackTrust.cs'),(Join-Path $PSScriptRoot 'DevicePackValidation.cs'),(Join-Path $PSScriptRoot 'DevicePackUnicode.cs'))
}
$rsa=New-Object Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp=$false
$temporary=$signature+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
try {
    if((Get-Item -LiteralPath $pack).Length -gt 262144){throw 'Pack exceeds 256 KB.'}
    $raw=[IO.File]::ReadAllBytes($pack)
    $rsa.FromXmlString([IO.File]::ReadAllText($private))
    $signed=$rsa.SignData([AtkBattery.DevicePackSigning]::SignedBytes($raw),[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
    $bytes=[Text.Encoding]::ASCII.GetBytes([Convert]::ToBase64String($signed)+"`n")
    # Verification always uses the compiled pack keyring, never the private
    # key's public half, an environment variable, or a command-line override.
    [AtkBattery.DevicePackSigning]::Verify($raw,$bytes)
    $stream=New-Object IO.FileStream($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
    Assert-NoPackLink $signature
    if([IO.File]::Exists($signature)){[IO.File]::Replace($temporary,$signature,$null)}else{[IO.File]::Move($temporary,$signature)}
    Write-Output ('Signed and verified: '+$signature)
} catch {
    Assert-NoPackLink $signature
    if([IO.File]::Exists($signature)){[IO.File]::Delete($signature)}
    throw 'Device pack signing or built-in public-key verification failed; no signature was retained.'
} finally {
    $rsa.Dispose()
    Assert-NoPackLink $temporary
    if([IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)}
}
