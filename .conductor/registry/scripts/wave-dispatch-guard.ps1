<#
.SYNOPSIS
    Per-root sentinel store that lets `apex-wave-dispatch.yaml` short-circuit
    when an earlier wave under the same apex run already failed.

.DESCRIPTION
    Companion to .conductor/registry/workflows/apex-driver.yaml +
    .conductor/registry/workflows/apex-wave-dispatch.yaml.

    apex-driver's outer `wave_dispatch_loop` runs `failure_mode:
    continue_on_error` (M8) so that a failed wave still flows through
    `wave_loop_summary` instead of crashing the parent workflow. Conductor's
    native `failure_mode: fail_fast` raises an ExecutionError and would
    bypass the apex-driver gate routing entirely, so we cannot use it.

    Without an explicit short-circuit, every subsequent wave still runs
    after wave[N] fails — wasting compute and producing cascading
    sub-workflow failures whose terminal state still rolls up correctly
    via `outer_loop_evaluator → terminal_apex_dispatch_failures` (PR #265),
    but only after every later wave's items have been dispatched.

    This guard introduces a per-root filesystem sentinel:

      * `clear`  — wipe the sentinel directory. Invoked at the start of
                   every dispatch pass by apex-driver's
                   `reset_wave_failure_flags` step (between
                   `check_conflicts` and `wave_dispatch_loop`).
      * `check`  — report whether any flag exists for this root. Invoked
                   as the entry step of apex-wave-dispatch
                   (`check_prior_wave_status`); routes to a no-op
                   terminal when blocked.
      * `record` — write a flag for this wave. Invoked by
                   apex-wave-dispatch's `record_wave_failure_flag` step
                   when `aggregate_renegotiation.output.items_failed_count`
                   is non-zero.

    Renegotiation is intentionally NOT short-circuited. The existing
    `renegotiation_gate.override` route flips straight to
    `apex_completion_gate`; skipping later waves on a renegotiation
    request would let `override` declare apex completion having silently
    skipped real work. Failure short-circuit is safe — `outer_loop_evaluator`
    routes failures to `terminal_apex_dispatch_failures` regardless of
    how many waves actually executed.

    Per the polyphony-workflow-author skill conventions:
      * ALWAYS exits 0 (routing-style envelope).
      * Single-line JSON envelope to stdout.

.PARAMETER Op
    One of `clear`, `check`, `record`.

.PARAMETER RootId
    The apex root work item id. Sentinel directory is namespaced by this
    id so concurrent runs against different apexes do not interfere.
    (The same-root run-lock prevents concurrent runs against the SAME
    apex; see polyphony-branch-model skill.)

.PARAMETER WaveIndex
    0-based wave index. Used by `record` to label the flag file for
    diagnostics. Ignored by `clear` and `check`.

.PARAMETER Reason
    Short reason label for the flag filename (used by `record` only).
    Sanitized to `[A-Za-z0-9._-]+`; non-matching characters are
    replaced with `_`.

.NOTES
    Companion to apex-driver.yaml + apex-wave-dispatch.yaml. The output
    schema is the workflows' input schema for the guard steps; tests
    pin both shapes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('clear', 'check', 'record')]
    [string]$Op,

    [Parameter(Mandatory)]
    [int]$RootId,

    [int]$WaveIndex = -1,

    [string]$Reason = ''
)

$ErrorActionPreference = 'Stop'

function Write-Envelope($e) {
    $e | ConvertTo-Json -Compress -Depth 4
    exit 0
}

function Get-FlagDir([int]$rootId) {
    # `--path-format=absolute` returns an absolute path even when the
    # current working directory changed under us (per-item worktrees,
    # subprocess cwd shifts). Bare `--git-common-dir` can return a
    # relative path which is brittle inside Conductor's per-step cwd
    # handling.
    $raw = & git rev-parse --path-format=absolute --git-common-dir 2>&1
    if ($LASTEXITCODE -ne 0) {
        return $null
    }
    $gitCommonDir = ($raw | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($gitCommonDir)) {
        return $null
    }
    return (Join-Path $gitCommonDir "polyphony/$rootId/wave-failures")
}

function Get-SafeReason([string]$reason) {
    if ([string]::IsNullOrWhiteSpace($reason)) {
        return 'unspecified'
    }
    # Allow letters, digits, dot, underscore, dash. Replace everything
    # else with `_` to defeat path-traversal and invalid-filename risk.
    return ([regex]::Replace($reason, '[^A-Za-z0-9._-]', '_'))
}

$envelope = [ordered]@{
    op           = $Op
    root_id      = $RootId
    flag_dir     = $null
    blocked      = $false
    first_reason = $null
    cleared      = $false
    recorded     = $false
    flag_path    = $null
    error_code   = ''
    error_message = ''
}

$flagDir = Get-FlagDir -rootId $RootId
if ($null -eq $flagDir) {
    $envelope.error_code = 'git_common_dir_failed'
    $envelope.error_message = 'git rev-parse --git-common-dir failed; sentinel disabled for this step'
    Write-Envelope $envelope
}
$envelope.flag_dir = $flagDir

try {
    switch ($Op) {
        'clear' {
            if (Test-Path $flagDir) {
                Remove-Item -Recurse -Force $flagDir
            }
            $envelope.cleared = $true
        }
        'check' {
            if (Test-Path $flagDir) {
                $flags = @(Get-ChildItem -LiteralPath $flagDir -File -Filter '*.flag' -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime)
                if ($flags.Count -gt 0) {
                    $envelope.blocked = $true
                    $envelope.first_reason = $flags[0].BaseName
                }
            }
        }
        'record' {
            if (-not (Test-Path $flagDir)) {
                New-Item -ItemType Directory -Path $flagDir -Force | Out-Null
            }
            $safeReason = Get-SafeReason -reason $Reason
            $flagName = if ($WaveIndex -ge 0) { "wave-$WaveIndex-$safeReason.flag" } else { "$safeReason.flag" }
            $flagPath = Join-Path $flagDir $flagName
            $payload = [ordered]@{
                wave_index = $WaveIndex
                reason     = $Reason
                recorded_at = ([DateTimeOffset]::UtcNow.ToString('o'))
            } | ConvertTo-Json -Compress
            Set-Content -LiteralPath $flagPath -Value $payload -NoNewline
            $envelope.recorded = $true
            $envelope.flag_path = $flagPath
        }
    }
}
catch {
    $envelope.error_code = 'sentinel_io_failed'
    $envelope.error_message = $_.Exception.Message
}

Write-Envelope $envelope
