# abort-run.ps1 — Operator-initiated full-run abort via conductor's `/api/stop`.
#
# Invoked by error-gate "Abort" routes that mean "halt the entire conductor
# run, not just the current sub-workflow". POSTs to the dashboard's
# `/api/stop` endpoint, which sets the engine's interrupt_event. Every
# nested sub-workflow's between-agent check (workflow.py:_check_interrupt
# at workflow.py:2309) raises InterruptError, propagating cleanly up
# through the entire run. No SIGKILL, no zombie processes, no inflight
# PR/branch corruption.
#
# Why /api/stop and not `route: $end`:
#   `$end` only ends the CURRENT workflow. Sub-workflows (apex-driver →
#   apex-wave-dispatch → for_each → apex-item-dispatch → plan-level)
#   cannot signal "halt everything" via routing alone — the parent's next
#   route is unconditional and forward-progressing. /api/stop fires
#   conductor's native cooperative-stop primitive, which IS designed to
#   unwind all engines.
#
# Why /api/stop and not parent-process-kill (pre-PR ##; superseded):
#   Process-killing is messy: the script's own pwsh process gets SIGTERM'd
#   too, race conditions on PR/branch state, harder to surface a clean
#   workflow_failed event, dependent on Win32 process tree semantics. The
#   HTTP path is one round-trip and conductor handles the unwind.
#
# Discovery: $env:CONDUCTOR_WEB_PORT is set by polyphony's launcher
# (Invoke-PolyphonySdlc.ps1) BEFORE conductor is spawned, so the script
# step subprocess inherits it. If absent (e.g., conductor invoked outside
# the launcher), the script emits a clear envelope and exits non-zero —
# which still halts forward progress because the script step fails.
#
# Emits a single JSON envelope on stdout (success or failure) so workflow
# transcripts and the dashboard see the abort signal regardless of outcome.

[CmdletBinding()]
param(
    [string]$Reason = 'operator-abort',
    [int]$WorkItemId = 0,
    [string]$Stage = ''
)

$ErrorActionPreference = 'Continue'

$envelope = [ordered]@{
    aborted      = $true
    reason       = $Reason
    work_item_id = $WorkItemId
    stage        = $Stage
    pid          = $PID
    timestamp    = (Get-Date -Format 'o')
}

$port = $env:CONDUCTOR_WEB_PORT
if (-not $port) {
    $envelope.error_code = 'no_web_port'
    $envelope.error      = 'CONDUCTOR_WEB_PORT not set; cannot reach conductor /api/stop.'
    $envelope.suggestion = "Run via Invoke-PolyphonySdlc.ps1 (which pins CONDUCTOR_WEB_PORT) or set it manually before `conductor run --web-port <port>`."
    $envelope | ConvertTo-Json -Compress -Depth 5
    Write-Host "[polyphony-abort] CONDUCTOR_WEB_PORT not set — cannot signal abort." -ForegroundColor Red
    exit 1
}

$url = "http://127.0.0.1:$port/api/stop"
$envelope.target_url = $url

Write-Host "[polyphony-abort] POST $url (Reason='$Reason', Stage='$Stage')" -ForegroundColor Yellow

try {
    # 5-second timeout: conductor responds in milliseconds when alive; if
    # it's already torn down, fail fast rather than block the run.
    $resp = Invoke-RestMethod -Method Post -Uri $url -TimeoutSec 5 -ErrorAction Stop
    $envelope.api_response = $resp
    $envelope | ConvertTo-Json -Compress -Depth 5
    Write-Host "[polyphony-abort] /api/stop signaled — conductor will raise InterruptError at the next between-agent check, unwinding all sub-workflows." -ForegroundColor Yellow
    exit 0
}
catch {
    $envelope.error_code = 'api_stop_failed'
    $envelope.error      = $_.Exception.Message
    $envelope | ConvertTo-Json -Compress -Depth 5
    Write-Host "[polyphony-abort] POST $url failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
