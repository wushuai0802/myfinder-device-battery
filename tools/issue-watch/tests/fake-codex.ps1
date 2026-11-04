$ErrorActionPreference = 'Stop'
$root = $env:ISSUEWATCH_TEST_DIR
if (-not $root) { throw 'Test directory required.' }
$utf8 = New-Object Text.UTF8Encoding($false)
$mode = Get-Content -LiteralPath (Join-Path $root 'mode.txt') -Raw -Encoding UTF8
$record = @{ arguments = @($args); directory = [Environment]::CurrentDirectory;
    files = @(Get-ChildItem -LiteralPath ([Environment]::CurrentDirectory) -Force | ForEach-Object { $_.Name });
    forbiddenNames = @([Environment]::GetEnvironmentVariables().Keys | Where-Object { $_ -match '(?i)TOKEN|KEY|SECRET' }) }
if ($args -contains '--help') {
    $record.kind = 'help'
    [IO.File]::AppendAllText((Join-Path $root 'codex.log'), (($record | ConvertTo-Json -Depth 20 -Compress) + "`n"), $utf8)
    switch ($mode.Trim()) {
        'modern' { '--output-schema <FILE>'; '--output-last-message <FILE>' }
        'short' { '-o <FILE>' }
        default { 'Usage: codex exec [OPTIONS] [PROMPT]' }
    }
    exit 0
}
$record.kind = 'model'
$record.prompt = [Console]::In.ReadToEnd()
[IO.File]::AppendAllText((Join-Path $root 'codex.log'), (($record | ConvertTo-Json -Depth 20 -Compress) + "`n"), $utf8)
if ($mode.Trim() -eq 'timeout') {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = (Get-Process -Id $PID).Path
    $start.Arguments = '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 20"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $child = [Diagnostics.Process]::Start($start)
    [IO.File]::WriteAllText((Join-Path $root 'timeout-child.txt'), [string]$child.Id, $utf8)
    Start-Sleep -Seconds 20
}
$result = [IO.File]::ReadAllText((Join-Path $root 'result.json'), $utf8)
$outputIndex = [Array]::IndexOf($args, '--output-last-message')
if ($outputIndex -lt 0) { $outputIndex = [Array]::IndexOf($args, '-o') }
if ($outputIndex -ge 0) {
    [IO.File]::WriteAllText($args[$outputIndex + 1], $result, $utf8)
    'Ignored progress output, not final JSON.'
} else { [Console]::Write($result) }
exit 0
