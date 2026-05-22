<#
.SYNOPSIS
    Per-root sentinel store that lets `root-batch-dispatch.yaml` short-circuit
    when an earlier batch under the same root run already failed.

.DESCRIPTION
    Companion to .conductor/registry/workflows/polyphony.yaml +
    .conductor/registry/workflows/root-batch-dispatch.yaml.

    polyphony's outer `batch_dispatch_loop` runs `failure_mode:
    continue_on_error` (M8) so that a failed batch still flows through
    `batch_loop_summary` instead of crashing the parent workflow. Conductor's
    native `failure_mode: fail_fast` raises an ExecutionError and would
    bypass the polyphony gate routing entirely, so we cannot use it.

    Without an explicit short-circuit, every subsequent batch still runs
    after batch[N] fails — wasting compute and producing cascading
    sub-workflow failures whose terminal state still rolls up correctly
    via `outer_loop_evaluator → root_dispatch_failures` (PR #265),
    but only after every later batch's items have been dispatched.

    This guard introduces a per-root filesystem sentinel:

      * `clear`  — wipe the sentinel directory. Invoked at the start of
                   every dispatch pass by polyphony's
                   `reset_batch_failure_flags` step (between
                   `check_conflicts` and `batch_dispatch_loop`).
      * `check`  — report whether any flag exists for this root. Invoked
                   as the entry step of root-batch-dispatch
                   (`check_prior_batch_status`); routes to a no-op
                   terminal when blocked.
      * `record` — write a flag for this batch. Invoked by
                   root-batch-dispatch's `record_batch_failure_flag` step
                   when `aggregate_renegotiation.output.items_failed_count`
                   is non-zero.

    Renegotiation is intentionally NOT short-circuited. The existing
    `renegotiation_gate.override` route flips straight to
    `root_completion_gate`; skipping later waves on a renegotiation
    request would let `override` declare root completion having silently
    skipped real work. Failure short-circuit is safe — `outer_loop_evaluator`
    routes failures to `root_dispatch_failures` regardless of
    how many waves actually executed.

    Per the polyphony-workflow-author skill conventions:
      * ALWAYS exits 0 (routing-style envelope).
      * Single-line JSON envelope to stdout.

.PARAMETER Op
    One of `clear`, `check`, `record`.

.PARAMETER RootId
    The root root work item id. Sentinel directory is namespaced by this
    id so concurrent runs against different roots do not interfere.
    (The same-root run-lock prevents concurrent runs against the SAME
    root; see polyphony-branch-model skill.)

.PARAMETER BatchIndex
    0-based batch index. Used by `record` to label the flag file for
    diagnostics. Ignored by `clear` and `check`.

.PARAMETER Reason
    Short reason label for the flag filename (used by `record` only).
    Sanitized to `[A-Za-z0-9._-]+`; non-matching characters are
    replaced with `_`.

.NOTES
    Companion to polyphony.yaml + root-batch-dispatch.yaml. The output
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

    [int]$BatchIndex = -1,

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
    return (Join-Path $gitCommonDir "polyphony/$rootId/batch-failures")
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
            $flagName = if ($BatchIndex -ge 0) { "batch-$BatchIndex-$safeReason.flag" } else { "$safeReason.flag" }
            $flagPath = Join-Path $flagDir $flagName
            $payload = [ordered]@{
                batch_index = $BatchIndex
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
