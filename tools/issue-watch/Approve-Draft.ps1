[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][ValidateRange(1, 2147483647)][int]$Issue,
    [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$Sha256,
    [switch]$Dismiss,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository = 'wushuai0802/myfinder-device-battery',
    [string]$DraftDirectory = 'E:\AIArchive\issue-drafts',
    [string]$GhPath = 'gh', [string]$PythonPath
)
. (Join-Path $PSScriptRoot 'IssueWatch.Common.ps1')
$DraftDirectory = [IO.Path]::GetFullPath($DraftDirectory)
$lock = Open-WatchStore $DraftDirectory
$run = $null
try {
    $state = Read-WatchState $DraftDirectory $Repository
    $entry = $state.issues[[string]$Issue]
    if (-not $entry -or $entry.status -notin @('ready', 'commented')) { throw 'No approvable draft (posting requires manual reconciliation).' }
    $python = Resolve-WatchPython $PythonPath
    $run = New-WatchTemp $DraftDirectory
    $expected = Join-Path $run 'expected.json'
    Write-Utf8 $expected ($entry | ConvertTo-Json -Depth 50)
    $draft = Join-Path $DraftDirectory ($Issue.ToString() + '.md')
    $verified = Invoke-DraftHelper $python @('inspect', '--draft', $draft, '--expected', $expected) | ConvertFrom-Json
    Write-Host (Get-Content -LiteralPath $draft -Raw -Encoding UTF8)
    Write-Host ('SHA-256: ' + $verified.sha256)
    if ($Sha256) {
        if ($Sha256 -ine $verified.sha256) { throw 'Approval digest mismatch; nothing sent.' }
    } elseif ((Read-Host $(if ($Dismiss) { 'Dismiss draft and label issue? Enter y' } else { 'Post this draft? Enter y' })) -cne 'y') {
        Write-Output 'Cancelled; nothing sent.'
        return
    }
    # Re-read after the interactive pause. Never approve bytes edited meanwhile.
    $verified = Invoke-DraftHelper $python @('inspect', '--draft', $draft, '--expected', $expected) | ConvertFrom-Json
    if (-not $Dismiss -and $entry.status -ne 'commented') {
        $bodyFile = Join-Path $run 'reply.txt'
        Write-Utf8 $bodyFile $verified.reply
        # Journal BEFORE sending: an interrupted/ambiguous POST must not be
        # automatically repeated. A label-only retry is safe after confirmed POST.
        $entry.status = 'posting'
        $state.issues[[string]$Issue] = $entry
        Save-WatchState $state $DraftDirectory
        [void](Invoke-WatchCommand $GhPath @('issue', 'comment', $Issue.ToString(), '--repo', $Repository, '--body-file', $bodyFile))
        $entry.status = 'commented'
        Save-WatchState $state $DraftDirectory
    }
    [void](Invoke-WatchCommand $GhPath @('issue', 'edit', $Issue.ToString(), '--repo', $Repository, '--add-label', 'triage:drafted'))
    $entry.status = if ($Dismiss) { 'dismissed' } else { 'drafted' }
    Save-WatchState $state $DraftDirectory
    Write-Output ('Issue #{0}: {1}.' -f $Issue, $entry.status)
} finally {
    if ($run) { Remove-WatchTemp $run $DraftDirectory }
    $lock.Dispose()
}
