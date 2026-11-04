param(
    [Parameter(Mandatory=$true)][string]$PrivateKeyPath,
    [switch]$DryRun,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository='wushuai0802/myfinder-device-battery'
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$tag='device-pack'
$pointerName='device-pack-v1-latest.json'
$work=Join-Path $env:TEMP ('device-pack-publish-'+[Guid]::NewGuid().ToString('N'))
$utf8=New-Object Text.UTF8Encoding($false)

function Invoke-PackGh([string[]]$Arguments) {
    $output=& gh @Arguments
    if($LASTEXITCODE -ne 0){throw ('gh failed: '+($Arguments[0..1] -join ' '))}
    return ($output -join "`n")
}
function Get-PackAssets($Release) {
    if($null -eq $Release){return @()}
    $pages=Invoke-PackGh @('api',"repos/$Repository/releases/$($Release.id)/assets?per_page=100",'--paginate','--slurp') | ConvertFrom-Json
    return @($pages | ForEach-Object {$_} | ForEach-Object {$_})
}
function Invoke-PackCheck([string[]]$Arguments) {
    $output=& $script:checker @Arguments
    if($LASTEXITCODE -ne 0){throw 'Client device-pack validation failed.'}
    return $output
}
function Get-PackFile([string]$Base,[string]$Name,[string]$Directory,[int]$Limit) {
    $path=Join-Path $Directory $Name
    Invoke-WebRequest -UseBasicParsing -Uri ($Base+$Name) -OutFile $path -TimeoutSec 30 | Out-Null
    if((Get-Item -LiteralPath $path).Length -gt $Limit){throw 'Downloaded device-pack file exceeds its limit.'}
    return $path
}

New-Item -ItemType Directory -Path $work | Out-Null
try {
    $script:checker=Join-Path $work 'PublishCheck.exe'
    $compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $sources=@('DevicePackTrust.cs','DevicePackValidation.cs','DevicePackUnicode.cs','device-pack/PublishCheck.cs') | ForEach-Object {Join-Path $PSScriptRoot $_}
    & $compiler /nologo /target:exe /platform:x64 ("/out:"+$script:checker) ("/resource:"+(Join-Path $root 'engine/device_pack_schema.json')+',DevicePackSchema') $sources
    if($LASTEXITCODE -ne 0){throw 'Publish validator build failed.'}
    $urls=@(Invoke-PackCheck @('sources'))
    $github="https://github.com/$Repository/releases/download/$tag/"
    $mirror=$urls[1]
    $python=Join-Path $root 'engine/runtime/python.exe'
    if(-not (Test-Path -LiteralPath $python)){$python=(Get-Command python -ErrorAction Stop).Source}
    # Reject malformed source before any network operation, including row errors.
    & $python -I -B (Join-Path $PSScriptRoot 'validate_device_pack.py')
    if($LASTEXITCODE -ne 0){throw 'Device pack source validation failed.'}
    $pages=Invoke-PackGh @('api',"repos/$Repository/releases?per_page=100",'--paginate','--slurp') | ConvertFrom-Json
    $releases=@($pages | ForEach-Object {$_} | ForEach-Object {$_} | Where-Object {$_.tag_name -ceq $tag})
    if($releases.Count -gt 1){throw 'Ambiguous device-pack release.'}
    $release=if($releases.Count){$releases[0]}else{$null}
    if($release -and ($release.draft -or -not $release.prerelease)){throw 'Existing device-pack release must be a public prerelease.'}
    $assets=@(Get-PackAssets $release)
    [long]$maximum=0
    if(@($assets | Where-Object {$_.name -ceq $pointerName}).Count){
        $previous=Get-PackFile $github $pointerName $work 4096
        $maximum=[long](Invoke-PackCheck @('pointer',$previous))
    }
    # Include orphaned immutable uploads from an interrupted prior publication.
    foreach($asset in $assets){
        if($asset.name -cmatch '^device-pack-v1-([0-9]{10})\.json(?:\.sig)?$'){$maximum=[Math]::Max($maximum,[long]$Matches[1])}
    }
    [long]$day=[long]([DateTime]::UtcNow.ToString('yyyyMMdd',[Globalization.CultureInfo]::InvariantCulture)+'00')
    [long]$version=[Math]::Max($day+1,$maximum+1)
    if($version -gt $day+99){throw 'No yyyyMMddNN version remains today (or remote version is in the future). Check clock / retry on a later UTC date.'}
    $name='device-pack-v1-'+$version+'.json'
    $pack=Join-Path $work $name
    & $python -I -B (Join-Path $PSScriptRoot 'validate_device_pack.py') --version $version --output $pack
    if($LASTEXITCODE -ne 0){throw 'Complete device pack validation failed.'}
    $null=Invoke-PackCheck @('validate',$pack)
    & (Join-Path $PSScriptRoot 'Sign-DevicePack.ps1') -PackPath $pack -PrivateKeyPath $PrivateKeyPath
    $hash=(Get-FileHash -LiteralPath $pack -Algorithm SHA256).Hash.ToLowerInvariant()
    $pointer=Join-Path $work $pointerName
    $metadata=[ordered]@{format='myfinder-device-pack-pointer';loader=1;packVersion=$version;file=$name;sha256=$hash;size=(Get-Item -LiteralPath $pack).Length}
    [IO.File]::WriteAllText($pointer,($metadata | ConvertTo-Json -Compress)+"`n",$utf8)
    $null=Invoke-PackCheck @('verify',$pointer,$pack,($pack+'.sig'))
    if($DryRun){Write-Output "DryRun validated version $version; sha256=$hash; no uploads. Temporary files will be deleted.";return}

    if(-not $release){
        $null=Invoke-PackGh @('release','create',$tag,'--repo',$Repository,'--prerelease','--latest=false','--title','Device packs','--notes',"型号数据包，不是程序版本")
        # Re-read metadata; never upload into a draft or ordinary app release.
        $release=Invoke-PackGh @('api',"repos/$Repository/releases/tags/$tag") | ConvertFrom-Json
        if($release.draft -or -not $release.prerelease){throw 'Created release is not a public prerelease.'}
    }
    $currentAssets=@(Get-PackAssets $release)
    if(@($currentAssets | Where-Object {$_.name -ceq $name -or $_.name -ceq ($name+'.sig')}).Count){throw 'Immutable device-pack asset already exists; refusing overwrite.'}
    # No --clobber here. GitHub rejects same-name assets even if a publisher races us.
    $null=Invoke-PackGh @('release','upload',$tag,$pack,($pack+'.sig'),'--repo',$Repository)
    $null=Invoke-PackGh @('release','upload',$tag,$pointer,'--repo',$Repository,'--clobber')
    foreach($base in @($github,$mirror)){
        $directory=Join-Path $work ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $directory | Out-Null
        $remotePointer=Get-PackFile $base $pointerName $directory 4096
        $remotePack=Get-PackFile $base $name $directory 262144
        $remoteSignature=Get-PackFile $base ($name+'.sig') $directory 4096
        # Always verify the new immutable pair, including when the mirror pointer is stale.
        $null=Invoke-PackCheck @('verify',$pointer,$remotePack,$remoteSignature)
        $null=Invoke-PackCheck @('pointer',$remotePointer)
        $remote=Get-Content -Raw -LiteralPath $remotePointer | ConvertFrom-Json
        $same=$remote.packVersion -eq $version -and $remote.file -ceq $name -and $remote.sha256 -ieq $hash -and $remote.size -eq $metadata.size
        if(-not $same){
            if($base -eq $mirror){Write-Warning 'Mirror pointer differs (cache may last 5 minutes); new immutable pack and signature verified.'}
            else{throw 'GitHub pointer does not match the published pack.'}
        }
    }
    Write-Output "Published and downloaded/verified device pack $version; sha256=$hash"
} finally {
    # Only this invocation's uniquely named TEMP directory is removed.
    if(Test-Path -LiteralPath $work){Remove-Item -LiteralPath $work -Recurse -Force}
}
