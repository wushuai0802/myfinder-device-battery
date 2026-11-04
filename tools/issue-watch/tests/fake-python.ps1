$ErrorActionPreference = 'Stop'
$root = $env:ISSUEWATCH_TEST_DIR
if (-not $root) { throw 'Test directory required.' }
$utf8 = New-Object Text.UTF8Encoding($false)
$mode = [IO.File]::ReadAllText((Join-Path $root 'mode.txt'), $utf8).Trim()
if (($mode -eq 'fail-index' -and [IO.Path]::GetFileName($args[2]) -eq 'known_ids.py') -or
    ($mode -eq 'fail-prepare' -and $args -contains 'prepare')) {
    [Console]::Error.Write([IO.File]::ReadAllText((Join-Path $root 'stderr.txt'), $utf8))
    exit 22
}
& ([IO.File]::ReadAllText((Join-Path $root 'python.txt'), $utf8)) @args
exit $LASTEXITCODE
