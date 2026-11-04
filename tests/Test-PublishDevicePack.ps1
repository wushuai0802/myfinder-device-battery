param()
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $env:TEMP ('device-pack-tests-'+[Guid]::NewGuid().ToString('N'))
$publisherTemp=Join-Path $fixture 'temp'
$oldPath=$env:PATH
$utf8=New-Object Text.UTF8Encoding($false)
$global:checks=0
function Assert([bool]$Value,[string]$Name){if(-not $Value){throw ('FAIL '+$Name)};$global:checks++}
function Write-Json([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 20),$utf8)}
function Reset-Remote {
    $global:release=$null;$global:assets=@()
    $global:calls=New-Object 'Collections.Generic.List[object]'
    $global:downloads=New-Object 'Collections.Generic.List[string]'
    $global:collision=$false;$global:assetReads=0;$global:tamper='';$global:stale=$false;$global:apiFail=$false
    Write-Json $source ([ordered]@{entries=@();disabled=@()})
}
# Shadow all network access; unexpected operations fail closed.
function gh {
    $a=@($args);$global:calls.Add($a);$global:LASTEXITCODE=0
    if($a[0] -eq 'api'){
        if($global:apiFail){$global:LASTEXITCODE=1;return}
        if($a[1] -like '*/releases?per_page=*'){
            if($global:release){return ('[['+($global:release | ConvertTo-Json -Compress)+']]')};return '[[]]'
        }
        if($a[1] -like '*/assets?per_page=*'){
            $global:assetReads++;$items=@($global:assets)
            if($global:collision -and $global:assetReads -gt 1){$items+=@{name=('device-pack-v1-'+$today+'01.json.sig')}}
            return ('['+(ConvertTo-Json -InputObject @($items) -Compress)+']')
        }
        if($a[1] -like '*/releases/tags/device-pack'){return ($global:release | ConvertTo-Json -Compress)}
        throw 'Unexpected fake gh API operation.'
    }
    if($a[0] -eq 'release' -and $a[1] -eq 'create'){
        Assert ($a -contains '--prerelease' -and $a -contains '--latest=false') 'first release is prerelease and never latest'
        $noteIndex=$a.IndexOf('--notes')
        Assert ($noteIndex -ge 0 -and $noteIndex+1 -lt $a.Count -and -not [string]::IsNullOrWhiteSpace([string]$a[$noteIndex+1])) 'release notes distinguish data from app release'
        $global:release=@{id=42;tag_name='device-pack';draft=$false;prerelease=$true};return
    }
    if($a[0] -eq 'release' -and $a[1] -eq 'upload'){
        $files=@($a[3..($a.IndexOf('--repo')-1)])
        foreach($file in $files){
            $name=[IO.Path]::GetFileName($file)
            Assert (($a -contains '--clobber') -eq ($name -eq 'device-pack-v1-latest.json')) 'only pointer may use clobber'
            if(@($global:assets | Where-Object {$_.name -eq $name}).Count -and $a -notcontains '--clobber'){$global:LASTEXITCODE=1;return}
            Copy-Item -LiteralPath $file -Destination (Join-Path $global:remoteRoot $name) -Force
            $global:assets=@($global:assets | Where-Object {$_.name -ne $name})+@(@{name=$name})
        };return
    }
    throw 'Unexpected fake gh operation.'
}
function Invoke-WebRequest {
    param([switch]$UseBasicParsing,[string]$Uri,[string]$OutFile,[int]$TimeoutSec)
    Assert ($Uri.StartsWith('https://github.com/test-owner/test-repo/releases/download/device-pack/') -or $Uri.StartsWith('https://myfinder-feedback-relay.wushuai0802.deno.net/device-pack/v1/')) 'only expected source URLs downloaded'
    $global:downloads.Add($Uri);$name=$Uri.Split('/')[-1]
    Copy-Item -LiteralPath (Join-Path $global:remoteRoot $name) -Destination $OutFile
    if($global:stale -and $Uri -like '*deno.net*latest.json'){
        $value=Get-Content -Raw -LiteralPath $OutFile | ConvertFrom-Json
        $value.packVersion--;$value.file='device-pack-v1-'+$value.packVersion+'.json';Write-Json $OutFile $value
    }
    if($global:tamper -and $Uri -like '*deno.net*' -and $name.EndsWith($global:tamper)){[IO.File]::WriteAllText($OutFile,'corrupt',$utf8)}
}
function Publish([switch]$DryRun){
    $oldTemp=$env:TEMP;$oldTmp=$env:TMP
    try{
        $env:TEMP=$publisherTemp;$env:TMP=$publisherTemp
        & $publisher -PrivateKeyPath $key -Repository test-owner/test-repo -DryRun:$DryRun
    }finally{
        $env:TEMP=$oldTemp;$env:TMP=$oldTmp
        Assert (@(Get-ChildItem -LiteralPath $publisherTemp -Directory -Filter 'device-pack-publish-*').Count -eq 0) 'publisher cleans temporary files after each invocation'
    }
}
function Must-Fail([scriptblock]$Action,[string]$Message){$failed=$false;try{& $Action | Out-Null}catch{$failed=$true};Assert $failed $Message}
function Uploads {return @($global:calls | Where-Object {$_[0] -eq 'release' -and $_[1] -eq 'upload'})}
function Mutations {return @($global:calls | Where-Object {$_[0] -eq 'release'})}
try {
    New-Item -ItemType Directory -Path $fixture,$publisherTemp,(Join-Path $fixture 'scripts/device-pack'),(Join-Path $fixture 'engine/providers'),(Join-Path $fixture 'devices/pack'),(Join-Path $fixture 'remote') -Force | Out-Null
    $global:remoteRoot=Join-Path $fixture 'remote';$source=Join-Path $fixture 'devices/pack/source.json'
    $publisher=Join-Path $fixture 'scripts/Publish-DevicePack.ps1';$key=Join-Path $fixture 'test-private.xml'
    foreach($name in @('Publish-DevicePack.ps1','Sign-DevicePack.ps1','validate_device_pack.py','DevicePackTrust.cs','DevicePackValidation.cs','DevicePackUnicode.cs','device-pack/PublishCheck.cs')){
        Copy-Item -LiteralPath (Join-Path $project ('scripts/'+$name)) -Destination (Join-Path $fixture ('scripts/'+$name))
    }
    foreach($name in @('device_pack.py','device_pack_schema.json','providers/__init__.py','providers/base.py','providers/profiles.py','providers/razer_profiles.py')){
        Copy-Item -LiteralPath (Join-Path $project ('engine/'+$name)) -Destination (Join-Path $fixture ('engine/'+$name))
    }
    if(Test-Path -LiteralPath (Join-Path $project 'engine/runtime/python.exe')){$env:PATH=(Join-Path $project 'engine/runtime')+';'+$env:PATH}
    $rsa=New-Object Security.Cryptography.RSACryptoServiceProvider(2048);$rsa.PersistKeyInCsp=$false
    try{
        [IO.File]::WriteAllText($key,$rsa.ToXmlString($true),$utf8)
        $trust=Join-Path $fixture 'scripts/DevicePackTrust.cs'
        # Replace only the public key in the disposable source copy; production
        # signer and trust have no runtime override and are never edited here.
        $text=[IO.File]::ReadAllText($trust)
        [IO.File]::WriteAllText($trust,[regex]::Replace($text,'<RSAKeyValue>.*?</RSAKeyValue>',$rsa.ToXmlString($false)),$utf8)
    }finally{$rsa.Dispose()}
    $keyHash=(Get-FileHash -LiteralPath $key).Hash;$today=[DateTime]::UtcNow.ToString('yyyyMMdd')
    Reset-Remote;Publish | Out-Null
    Assert (@(Uploads).Count -eq 2) 'first publish uploads pair then pointer'
    Assert ($global:calls[1][0] -eq 'release' -and $global:calls[1][1] -eq 'create') 'missing release created before uploads'
    $uploads=@(Uploads)
    Assert ($uploads[0][3].EndsWith('.json') -and $uploads[0][4].EndsWith('.json.sig') -and $uploads[1][3].EndsWith('latest.json')) 'immutable pair precedes pointer'
    Assert ($global:downloads.Count -eq 6) 'both origins download pointer, pack and signature'
    $first=Get-Content -Raw -LiteralPath (Join-Path $remoteRoot 'device-pack-v1-latest.json') | ConvertFrom-Json
    Assert ($first.packVersion -eq [long]($today+'01')) 'first publication starts at UTC day 01'
    $global:calls.Clear();Publish | Out-Null
    $second=Get-Content -Raw -LiteralPath (Join-Path $remoteRoot 'device-pack-v1-latest.json') | ConvertFrom-Json
    Assert ($second.packVersion -eq $first.packVersion+1) 'version increments from published pointer'
    Assert (@($global:calls | Where-Object {$_[1] -eq 'create'}).Count -eq 0) 'existing release not recreated'
    $global:assets+=@{name=('device-pack-v1-'+$today+'07.json.sig')};Publish | Out-Null
    $latest=Get-Content -Raw -LiteralPath (Join-Path $remoteRoot 'device-pack-v1-latest.json') | ConvertFrom-Json
    Assert ($latest.packVersion -eq [long]($today+'08')) 'version exceeds orphaned immutable assets'
    Reset-Remote
    $global:release=@{id=42;tag_name='device-pack';draft=$false;prerelease=$true};$global:collision=$true
    Must-Fail {Publish} 'same-name immutable signature collision refused'
    Assert (@(Uploads).Count -eq 0) 'collision causes no upload'
    Reset-Remote;Publish -DryRun | Out-Null
    Assert (@(Mutations).Count -eq 0) 'DryRun never creates release or uploads'
    $badSources=@(
        '{"entries":[],"disabled":[],"packVersion":1}',
        '{"entries":[],"entries":[],"disabled":[]}',
        '{"entries":[{"provider":"razer","profile":"unknown","profileRev":1,"match":{"vendorId":"1532","productId":"FE01"},"name":"Test","kind":"mouse"}],"disabled":[]}',
        '{"entries":[],"disabled":[{"provider":"native_atk","vendorId":"373B","productId":"1278","reason":"test"}]}'
    )
    foreach($bad in $badSources){
        Reset-Remote;[IO.File]::WriteAllText($source,$bad,$utf8)
        Must-Fail {Publish} 'invalid source rejected'
        Assert ($global:calls.Count -eq 0) 'validation failure makes no gh calls'
    }
    Reset-Remote;$global:apiFail=$true
    Must-Fail {Publish} 'API error is not mistaken for absent release'
    Assert (@(Mutations).Count -eq 0) 'API failure causes no mutation'
    Reset-Remote;$global:release=@{id=42;tag_name='device-pack';draft=$false;prerelease=$false}
    Must-Fail {Publish} 'ordinary release cannot be used'
    Assert (@(Mutations).Count -eq 0) 'ordinary release unchanged'
    Reset-Remote;$global:stale=$true
    Publish | Out-Null
    Assert ($true) 'stale mirror pointer only warns'
    Reset-Remote;$global:tamper='.sig';Must-Fail {Publish} 'downloaded mirror signature is checked'
    Reset-Remote;$global:tamper='.json';Must-Fail {Publish} 'downloaded mirror digest is checked'
    Assert ((Get-FileHash -LiteralPath $key).Hash -eq $keyHash) 'private key remains unchanged'
    Assert (@(Get-ChildItem -LiteralPath $publisherTemp -Directory -Filter 'device-pack-publish-*').Count -eq 0) 'publisher cleans temporary files on success and failure'
    Write-Output ("PASS $global:checks device-pack publication checks; fake gh/HTTP and ephemeral key only.")
} finally {
    $env:PATH=$oldPath
    if(Test-Path -LiteralPath $fixture){Remove-Item -LiteralPath $fixture -Recurse -Force}
}


