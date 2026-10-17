param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root 'tmp\backend-runtime'}
$lock=Get-Content -LiteralPath (Join-Path $root 'engine\dependencies.lock.json') -Raw | ConvertFrom-Json
$downloads=Join-Path $root 'tmp\backend-downloads'
New-Item -ItemType Directory -Path $downloads,$OutputDirectory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach($entry in @($lock.python,$lock.hidapi)) {
    $archive=Join-Path $downloads ($entry.sha256+'.zip')
    if(-not (Test-Path -LiteralPath $archive)){Invoke-WebRequest -Uri $entry.url -OutFile $archive -UseBasicParsing}
    if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $entry.sha256){throw 'Backend download checksum mismatch.'}
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $destinationRoot=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd([char]92)+[char]92
        foreach($item in $zip.Entries) {
            $target=[IO.Path]::GetFullPath((Join-Path $destinationRoot $item.FullName))
            if(-not $target.StartsWith($destinationRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid archive path.'}
            if($item.FullName.EndsWith('/')){New-Item -ItemType Directory -Path $target -Force | Out-Null;continue}
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($item,$target,$true)
        }
    }finally{$zip.Dispose()}
}
Copy-Item -LiteralPath (Join-Path $root 'engine\python314._pth.in') -Destination (Join-Path $OutputDirectory 'python314._pth') -Force
& (Join-Path $OutputDirectory 'python.exe') -I -B -c "import hid,ctypes,json; print('Portable battery backend dependencies ready')"
if($LASTEXITCODE -ne 0){throw 'Portable backend import check failed.'}
[pscustomobject]@{Directory=[IO.Path]::GetFullPath($OutputDirectory);Python=$lock.python.version;Hidapi=$lock.hidapi.version}
