[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository = 'wushuai0802/myfinder-device-battery',
    [string]$DraftDirectory = 'E:\AIArchive\issue-drafts',
    [string]$GhPath = 'gh', [string]$CodexPath = 'codex', [string]$PythonPath,
    [ValidateRange(1, 600)][int]$ModelTimeoutSeconds = 600,
    [switch]$NoNotification
)
. (Join-Path $PSScriptRoot 'IssueWatch.Common.ps1')
$DraftDirectory = [IO.Path]::GetFullPath($DraftDirectory)
$lock = Open-WatchStore $DraftDirectory
$transcribing = $false
$run = $null
try {
    $logs = Join-Path $DraftDirectory 'logs'
    [void][IO.Directory]::CreateDirectory($logs)
    Get-ChildItem -LiteralPath $logs -File -Filter 'watch-*.log' |
        Where-Object { $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddDays(-30) } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    Start-Transcript -Path (Join-Path $logs ('watch-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N') + '.log')) | Out-Null
    $transcribing = $true
    $state = Read-WatchState $DraftDirectory $Repository
    # Avoid GitHub search's 1,000-result cap: list all open issues, filter locally.
    $listed = Invoke-WatchCommand $GhPath @('issue', 'list', '--repo', $Repository, '--state', 'open', '--limit', '2147483647', '--json', 'number,labels,updatedAt')
    $allIssues = ConvertFrom-Json -InputObject $listed
    $candidates = @($allIssues | Where-Object {
        $labels = @($_.labels | ForEach-Object { $_.name })
        ($labels -notcontains 'triage:drafted') -and ($labels -notcontains 'triage:locked') -and
        (@($labels | Where-Object { $_ -in @('triage:pack-candidate', 'triage:insufficient-evidence', 'triage:new-vendor', 'triage:bug') }).Count -gt 0)
    } | Sort-Object number)
    if ($candidates.Count -eq 0) { Write-Output 'No pending issues; model not invoked.'; return }
    $python = Resolve-WatchPython $PythonPath
    $run = New-WatchTemp $DraftDirectory
    $knownFile = Join-Path $run 'known.json'
    $help = $null
    $processed = 0
    foreach ($candidate in $candidates) {
        if ($processed -ge 5) { break }
        $number = [int]$candidate.number
        if ($number -le 0) { throw 'Invalid issue number.' }
        $key = [string]$number
        $previous = $state.issues[$key]
        if ($previous -and $previous.status -in @('manual', 'posting', 'commented', 'drafted', 'dismissed')) { continue }
        $issueJson = Invoke-WatchCommand $GhPath @('issue', 'view', $key, '--repo', $Repository, '--json', 'number,title,body,comments,updatedAt,labels,state')
        $issue = $issueJson | ConvertFrom-Json
        $labels = @($issue.labels | ForEach-Object { $_.name })
        if ([int]$issue.number -ne $number) { throw 'Issue identity mismatch.' }
        if ($issue.state -ne 'OPEN' -or 'triage:locked' -in $labels -or 'triage:drafted' -in $labels -or
            @($labels | Where-Object { $_ -in @('triage:pack-candidate', 'triage:insufficient-evidence', 'triage:new-vendor', 'triage:bug') }).Count -eq 0) { continue }
        $issueFile = Join-Path $run 'issue.json'
        Write-Utf8 $issueFile $issueJson
        $revision = Invoke-DraftHelper $python @('source', '--issue', $issueFile) | ConvertFrom-Json
        if ($previous -and $previous.status -eq 'ready' -and $previous.sourceHash -eq $revision.sourceHash) { continue }
        $processed++
        $attempt = New-WatchTemp $DraftDirectory
        $cwd = Join-Path $attempt 'empty'
        [void][IO.Directory]::CreateDirectory($cwd)
        $entry = [ordered]@{ sourceHash = $revision.sourceHash; issueUpdatedAt = $revision.issueUpdatedAt;
            commentUpdates = @($revision.commentUpdates); status = 'error'; failures = 0;
            sha256 = $null; fileSha256 = $null; lastAttempt = [DateTime]::UtcNow.ToString('o'); error = $null }
        try {
            if ($null -eq $help) {
                $help = Invoke-WatchCommand -Command $CodexPath -Arguments @('exec', '--help') -Directory $cwd -CleanEnvironment -TimeoutSeconds 30
                $schemaFlag = $help -match '(?m)(?<!\S)--output-schema(?=[\s,=]|$)'
                $outputFlag = if ($help -match '(?m)(?<!\S)--output-last-message(?=[\s,=]|$)') { '--output-last-message' }
                              elseif ($help -match '(?m)(?<!\S)-o(?=[\s,]|$)') { '-o' } else { $null }
                Write-Output ('Codex help capabilities: output-schema={0}; final-output={1}' -f $schemaFlag, $outputFlag)
                Write-Utf8 $knownFile (Invoke-WatchCommand $python @('-I', '-B', (Join-Path $script:ProjectRoot 'engine\known_ids.py'), '--json'))
            }
            $prompt = Invoke-DraftHelper $python @('prepare', '--issue', $issueFile, '--known', $knownFile, '--template', (Join-Path $PSScriptRoot 'prompt.md'))
            $resultFile = Join-Path $attempt 'result.json'
            $arguments = @('exec', '-m', 'gpt-6.1-sol', '-c', 'model_reasoning_effort=max', '--sandbox', 'read-only', '--skip-git-repo-check', '-C', $cwd)
            if ($schemaFlag) { $arguments += @('--output-schema', (Join-Path $PSScriptRoot 'response.schema.json')) }
            if ($outputFlag) { $arguments += @($outputFlag, $resultFile) }
            $arguments += '-'
            $raw = Invoke-WatchCommand -Command $CodexPath -Arguments $arguments -Directory $cwd -InputText $prompt -CleanEnvironment -TimeoutSeconds $ModelTimeoutSeconds
            if (-not $outputFlag) { Write-Utf8 $resultFile $raw }
            if (-not (Test-Path -LiteralPath $resultFile)) { throw 'Model final output missing.' }
            $stagedDraft = Join-Path $attempt 'draft.md'
            $metadata = Invoke-DraftHelper $python @('render', '--issue', $issueFile, '--result', $resultFile, '--draft', $stagedDraft) | ConvertFrom-Json
            $target = Join-Path $DraftDirectory ($key + '.md')
            if (Test-Path -LiteralPath $target) { [IO.File]::Replace($stagedDraft, $target, [NullString]::Value) }
            else { [IO.File]::Move($stagedDraft, $target) }
            $entry.status = 'ready'
            $entry.sha256 = $metadata.sha256
            $entry.fileSha256 = $metadata.fileSha256
            $state.issues[$key] = [pscustomobject]$entry
            Save-WatchState $state $DraftDirectory
            Write-Output ('Issue #{0}: draft ready; SHA-256 {1}' -f $number, $entry.sha256)
            if (-not $NoNotification) { Show-DraftNotification $number }
        } catch {
            $entry.failures = if ($previous) { [int]$previous.failures + 1 } else { 1 }
            $entry.status = if ($entry.failures -ge 3) { 'manual' } else { 'error' }
            $entry.error = 'Drafting/validation failed; inspect locally and retry.'
            $state.issues[$key] = [pscustomobject]$entry
            Save-WatchState $state $DraftDirectory
            Write-Warning ('Issue #{0}: {1}; consecutive failures={2}' -f $number, $entry.status, $entry.failures)
        } finally { Remove-WatchTemp $attempt $DraftDirectory }
    }
    Write-Output ('Attempted {0} issues (maximum 5).' -f $processed)
} finally {
    if ($run) { Remove-WatchTemp $run $DraftDirectory }
    if ($transcribing) { Stop-Transcript | Out-Null }
    $lock.Dispose()
}
