Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:WatchRoot = $PSScriptRoot
$script:ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:Utf8 = New-Object Text.UTF8Encoding($false)

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, $script:Utf8)
}

function Resolve-WatchCommand([string]$Command) {
    if (Test-Path -LiteralPath $Command -PathType Leaf) { return (Resolve-Path -LiteralPath $Command).Path }
    $found = Get-Command -Name $Command -CommandType Application,ExternalScript -ErrorAction Stop | Select-Object -First 1
    return $found.Source
}

function Resolve-WatchPython([string]$PythonPath) {
    if ($PythonPath) { return Resolve-WatchCommand $PythonPath }
    $bundled = Join-Path $script:ProjectRoot 'engine\runtime\python.exe'
    if (Test-Path -LiteralPath $bundled) { return $bundled }
    return Resolve-WatchCommand 'python'
}

function Quote-NativeArgument([string]$Value) {
    # CommandLineToArgvW quoting, including trailing backslashes.
    return '"' + ([regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1')) + '"'
}

function Protect-WatchDiagnostic([string]$Text, [int]$TailLength = 0) {
    $pattern = '(?i)(?:gh[pousr]_|github_pat_|sk-)[A-Za-z0-9_+/-]+={0,2}|[A-Za-z0-9+/_-]{32,}={0,2}|[A-Za-z0-9+/_-]{31}=|[A-Za-z0-9+/_-]{30}=='
    if ($TailLength -gt 0 -and $Text.Length -gt $TailLength) {
        $offset = $Text.Length - $TailLength
        # Include a whole token crossing the boundary so no suffix can leak.
        foreach ($match in [regex]::Matches($Text, $pattern)) {
            if ($match.Index -lt $offset -and $match.Index + $match.Length -gt $offset) { $offset = $match.Index; break }
        }
        $Text = $Text.Substring($offset)
    }
    $safe = [regex]::Replace($Text, $pattern, '[redacted]')
    if ($TailLength -gt 0 -and $safe.Length -gt $TailLength) { return $safe.Substring($safe.Length - $TailLength) }
    return $safe
}

function Invoke-WatchCommand {
    param([string]$Command, [string[]]$Arguments, [string]$Directory = $script:ProjectRoot,
          [string]$InputText = '', [switch]$CleanEnvironment, [int]$TimeoutSeconds = 60,
          [string]$OutputPath)
    $resolved = Resolve-WatchCommand $Command
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $Directory
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $script:Utf8
    $start.StandardErrorEncoding = $script:Utf8
    # Framework 4.x lacks StandardInputEncoding; the redirected writer uses
    # Console.InputEncoding. Set it only while creating this child process.
    if ([IO.Path]::GetExtension($resolved) -in @('.ps1', '.cmd', '.bat')) {
        # A trusted shim is invoked with literal PowerShell arguments, never
        # with interpolated issue text or cmd.exe command concatenation.
        $literalArgs = @($Arguments | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ' '
        $literalPath = "'" + $resolved.Replace("'", "''") + "'"
        $wrapper = '$ProgressPreference = ''SilentlyContinue''; [Console]::InputEncoding = [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false); $OutputEncoding = [Console]::OutputEncoding; & ' + $literalPath + ' ' + $literalArgs + '; if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }'
        $start.FileName = (Get-Process -Id $PID).Path
        $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($wrapper))
    } else {
        $start.FileName = $resolved
        $start.Arguments = (@($Arguments | ForEach-Object { Quote-NativeArgument $_ }) -join ' ')
    }
    if ($CleanEnvironment) {
        foreach ($name in @($start.EnvironmentVariables.Keys)) {
            if ($name -match '(?i)TOKEN|KEY|SECRET') { $start.EnvironmentVariables.Remove($name) }
        }
    }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    if (-not ('IssueWatch.ProcessJob' -as [type])) { Add-Type -Path (Join-Path $script:WatchRoot 'ProcessJob.cs') }
    $job = New-Object IssueWatch.ProcessJob
    $oldEncoding = [Console]::InputEncoding
    $started = $false
    try {
        [Console]::InputEncoding = $script:Utf8
        [void]$process.Start()
        $started = $true
        $job.Assign($process.Handle)
    } catch {
        $job.Dispose()
        if ($started -and -not $process.HasExited) { $process.Kill() }
        $process.Dispose()
        throw
    } finally { [Console]::InputEncoding = $oldEncoding }
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $writing = $process.StandardInput.WriteAsync($InputText)
        $timer = [Diagnostics.Stopwatch]::StartNew()
        if (-not $writing.Wait($TimeoutSeconds * 1000)) { throw 'Command timed out while writing stdin.' }
        $process.StandardInput.Close()
        $remaining = [Math]::Max(1, ($TimeoutSeconds * 1000) - [int]$timer.ElapsedMilliseconds)
        if (-not $process.WaitForExit($remaining)) { throw 'Command timed out.' }
        if (-not $stdout.Wait(5000) -or -not $stderr.Wait(5000)) { throw 'Command output did not close.' }
        if ($OutputPath) { Write-Utf8 $OutputPath $stdout.Result }
        if ($process.ExitCode -ne 0) { throw ('Command failed (exit {0}).' -f $process.ExitCode) }
        return $stdout.Result
    } catch {
        $failure = $_.Exception
        # Close the job first so timeout diagnostics also include flushed stderr.
        $job.Dispose()
        if (-not $process.HasExited) { [void]$process.WaitForExit(5000) }
        $failure.Data['WatchExitCode'] = if ($process.HasExited) { $process.ExitCode } else { 'unavailable' }
        $diagnostic = ''
        $stderrText = ''
        try {
            if ($stderr.Wait(5000)) {
                $stderrText = $stderr.Result
                $diagnostic = Protect-WatchDiagnostic $stderrText
            }
            if ($OutputPath -and $stdout.Wait(5000)) { Write-Utf8 $OutputPath $stdout.Result }
        } catch { } # Keep the original error if a pipe or output file also failed.
        if ($diagnostic -match '(?m)^Draft validation/helper failed: \w+: ([^\r\n]+)') {
            $failure.Data['WatchReason'] = $Matches[1]
        } elseif ($failure.Message -like 'Command failed*' -and $diagnostic.Trim()) {
            $lastLine = ([regex]::Split($diagnostic.Trim(), '\r?\n'))[-1]
            $failure.Data['WatchReason'] = $lastLine.Substring(0, [Math]::Min(200, $lastLine.Length))
        }
        $failure.Data['WatchStderr'] = Protect-WatchDiagnostic $stderrText 2000
        throw
    } finally {
        $job.Dispose()
        if (-not $process.HasExited) { [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

function Invoke-DraftHelper([string]$Python, [string[]]$Arguments) {
    return Invoke-WatchCommand -Command $Python -Arguments (@('-I', '-B', (Join-Path $script:WatchRoot 'draft_json.py')) + $Arguments)
}

function Open-WatchStore([string]$Directory) {
    $full = [IO.Path]::GetFullPath($Directory)
    [void][IO.Directory]::CreateDirectory($full)
    return [IO.File]::Open((Join-Path $full '.issue-watch.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
}

function Read-WatchState([string]$Directory, [string]$Repository) {
    $path = Join-Path $Directory 'state.json'
    $state = @{ version = 1; repository = $Repository; issues = @{} }
    if (Test-Path -LiteralPath $path) {
        $saved = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($saved.version -ne 1 -or $saved.repository -cne $Repository) { throw 'State version/repository mismatch.' }
        foreach ($property in $saved.issues.PSObject.Properties) { $state.issues[$property.Name] = $property.Value }
    }
    return $state
}

function Save-WatchState($State, [string]$Directory) {
    $target = Join-Path $Directory 'state.json'
    $staging = Join-Path $Directory ('state-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Write-Utf8 $staging ($State | ConvertTo-Json -Depth 50)
        if (Test-Path -LiteralPath $target) { [IO.File]::Replace($staging, $target, [NullString]::Value) }
        else { [IO.File]::Move($staging, $target) }
    } finally { if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Force } }
}

function Save-FailedWatchResult([string]$Path, [string]$Directory, [int]$Issue) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    $failed = Join-Path ([IO.Path]::GetFullPath($Directory)) 'failed'
    [void][IO.Directory]::CreateDirectory($failed)
    $name = '{0}-{1}.json' -f $Issue, [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfffffff')
    [IO.File]::Copy($Path, (Join-Path $failed $name))
    # Prune only this helper's named artifacts, never unrelated local files.
    Get-ChildItem -LiteralPath $failed -File -Filter '*.json' |
        Where-Object { $_.Name -match '^\d+-\d{8}-\d{13}\.json$' } |
        Sort-Object LastWriteTimeUtc, Name -Descending | Select-Object -Skip 20 |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
}

function New-WatchTemp([string]$Directory) {
    $root = Join-Path ([IO.Path]::GetFullPath($Directory)) 'tmp'
    [void][IO.Directory]::CreateDirectory($root)
    $path = Join-Path $root ('run-' + [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($path)
    return $path
}

function Remove-WatchTemp([string]$Path, [string]$Directory) {
    $root = [IO.Path]::GetFullPath((Join-Path $Directory 'tmp')).TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($full) -notmatch '^run-[a-f0-9]{32}$') { throw 'Unsafe temporary path.' }
    # Windows may release a terminated process's cwd handle just after its exit
    # event. Retry that short cleanup race without relaxing the path boundary.
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
            return
        } catch [IO.IOException] {
            if ($attempt -eq 19) { throw }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Show-DraftNotification([int]$Issue) {
    try {
        [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        [void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
        $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
        $xml.LoadXml('<toast><visual><binding template="ToastGeneric"><text>MyFinder Issue Watch</text><text>Issue #' + $Issue + ': draft ready for review.</text></binding></visual></toast>')
        $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Microsoft.Windows.PowerShell').Show($toast)
    } catch { } # Notifications must never invalidate a saved draft.
}
