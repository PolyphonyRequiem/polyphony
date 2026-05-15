# abort-run.ps1 — Operator-initiated full-run abort.
#
# Invoked by error-gate "Abort" routes that mean "halt the entire conductor
# run, not just the current sub-workflow". Walks the parent-process chain
# from the current PID up to the nearest conductor process and Stop-Processes
# it. Conductor's children (python workers) cascade-die.
#
# Why this exists: conductor's `route: $end` only ends the CURRENT workflow.
# Sub-workflows (apex-driver → apex-wave-dispatch → for_each → apex-item-
# dispatch → plan-level) cannot signal "halt everything" via routing alone
# — the parent's next route is unconditional. Without an explicit kill, the
# parent assumes success and proceeds to teardown_worktree → next item →
# next wave, producing confusing downstream errors.
#
# Surgical (parent-chain walk) instead of `conductor stop --all` so concurrent
# polyphony runs on the same host are unaffected.
#
# Emits a JSON envelope on stdout BEFORE the kill so any observer (transcript
# log, dashboard) sees the abort signal before conductor dies. The script
# itself almost certainly does not return cleanly — that's the point.

[CmdletBinding()]
param(
    [string]$Reason = 'operator-abort',
    [int]$WorkItemId = 0,
    [string]$Stage = ''
)

$ErrorActionPreference = 'Continue'

$envelope = @{
    aborted     = $true
    reason      = $Reason
    work_item_id = $WorkItemId
    stage       = $Stage
    pid         = $PID
    timestamp   = (Get-Date -Format 'o')
}

# Try to find the conductor process by walking parents.
$conductorPid = $null
$walked = @()
try {
    $current = Get-CimInstance Win32_Process -Filter "ProcessId = $PID" -ErrorAction Stop
    $maxDepth = 16
    while ($current -and $current.ParentProcessId -gt 0 -and $maxDepth -gt 0) {
        $walked += "PID=$($current.ProcessId) Name=$($current.Name)"
        $parent = Get-CimInstance Win32_Process -Filter "ProcessId = $($current.ParentProcessId)" -ErrorAction SilentlyContinue
        if (-not $parent) { break }
        if ($parent.Name -in @('conductor.exe', 'conductor')) {
            $conductorPid = [int]$parent.ProcessId
            $walked += "PID=$($parent.ProcessId) Name=$($parent.Name)  ← TARGET"
            break
        }
        $current = $parent
        $maxDepth--
    }
} catch {
    # Fall through to fallback below.
    $envelope.walk_error = $_.Exception.Message
}

$envelope.parent_chain = $walked

if ($conductorPid) {
    $envelope.target_pid = $conductorPid
    $envelope.target_strategy = 'parent-chain-walk'

    # Emit the envelope BEFORE killing so transcript/dashboard sees it.
    $envelope | ConvertTo-Json -Compress -Depth 5

    Write-Host "[polyphony-abort] Stopping conductor (PID $conductorPid) — operator chose Abort at stage '$Stage'." -ForegroundColor Red
    try {
        Stop-Process -Id $conductorPid -Force -ErrorAction Stop
    } catch {
        Write-Host "[polyphony-abort] Stop-Process failed: $($_.Exception.Message)" -ForegroundColor Yellow
        # Belt-and-braces: try `conductor stop --all` as a fallback.
        & conductor stop --all 2>&1 | Write-Host
    }
    # We do not expect to return — conductor's death will SIGTERM this process.
    exit 0
}

# Fallback: parent-chain walk failed (e.g. conductor invoked via an unusual
# wrapper). Fall back to the documented `conductor stop --all` mechanism.
$envelope.target_strategy = 'conductor-stop-all-fallback'
$envelope | ConvertTo-Json -Compress -Depth 5

Write-Host "[polyphony-abort] conductor.exe not found in parent chain; falling back to 'conductor stop --all'." -ForegroundColor Yellow
& conductor stop --all 2>&1 | Write-Host
exit 0
