$ErrorActionPreference = 'Stop'
$root = $env:ISSUEWATCH_TEST_DIR
if (-not $root) { throw 'Test directory required.' }
$utf8 = New-Object Text.UTF8Encoding($false)
$record = @{ arguments = @($args) }
if ($args[0] -ne 'issue') { throw 'Unexpected gh command.' }
if ($args[1] -eq 'comment') {
    $index = [Array]::IndexOf($args, '--body-file')
    if ($index -lt 0) { throw 'Expected --body-file.' }
    $record.body = [IO.File]::ReadAllText($args[$index + 1], $utf8)
}
[IO.File]::AppendAllText((Join-Path $root 'gh.log'), (($record | ConvertTo-Json -Depth 30 -Compress) + "`n"), $utf8)
$issues = Get-Content -LiteralPath (Join-Path $root 'issues.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($null -eq $issues) { $issues = @() }
$issueNumber = if ($args[1] -eq 'view') { [int]$args[2] } else { 0 }
switch ($args[1]) {
    'list' { ConvertTo-Json -InputObject @($issues) -Depth 50 -Compress }
    'view' {
        $issue = $issues | Where-Object { $_.number -eq $issueNumber } | Select-Object -First 1
        if (-not $issue) { exit 2 }
        $issue | ConvertTo-Json -Depth 50 -Compress
    }
    'comment' {
        if (Test-Path -LiteralPath (Join-Path $root 'fail-comment')) { exit 7 }
        'https://github.com/test/repo/issues/1#issuecomment-1'
    }
    'edit' { if (Test-Path -LiteralPath (Join-Path $root 'fail-label')) { exit 8 } }
    default { throw 'Unexpected gh issue command.' }
}
exit 0
