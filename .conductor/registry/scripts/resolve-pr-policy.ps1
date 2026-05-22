<#
.SYNOPSIS
    Resolve `policy.pr.defaults.mode` for the pre-merge policy router in
    github-pr.yaml and ado-pr.yaml (AB#3184).
.DESCRIPTION
    Invoked by the `pr_pre_merge_policy_router` step that fronts every
    auto-merge path into `pr_merger`. Calls `polyphony policy resolve
    --domain pr --scope <scope>`, extracts `mode`, and emits a JSON
    envelope the workflow can consume.

    Same defensive posture as `resolve-research-policy.ps1` and
    `resolve-unattended-cap-mode.ps1`:
      - Always exits 0; routing is condition-based, not exit-code-based.
      - On error, `mode` flips to `'manual'` so the failure surfaces at
        the next human gate rather than silently auto-merging with a
        broken policy.
      - `source` and `policy_error` fields let `pr_pre_merge_gate`
        surface "policy resolution failed; falling back to manual" so a
        typo in policy.yaml doesn't hide silently behind the default.

    Allowed `mode` values (from src/Polyphony/Policy/PolicyMode.cs):
      - `auto`    → never gate; auto-merge as soon as reviewer is green
                    (silent / current behavior).
      - `warning` → auto-merge, but interpose `pr_warning_stamp` first
                    so a single advisory comment is posted to the PR
                    recording that the merge fired under
                    `policy.pr.defaults.mode == 'warning'`. Gives
                    operators an audit footprint without blocking the
                    merge.
      - `manual`  → always gate at `pr_pre_merge_gate` before invoking
                    `pr_merger` on the auto-merge path.

    AB#3184 originally wired ONLY mode here with `warning` aliasing
    `auto`. The warning/auto split landed under AB#3217, which also
    tracks the still-deferred reviewer-output schema work for
    `quality_threshold`. `max_fix_loops` is dead by architecture
    (tracked separately under #3272). `max_remediation_cycles` is
    already consumed elsewhere by `pr_remediation_policy` (PR #405).

    Output JSON envelope:
        {
          "mode":         "auto" | "warning" | "manual",
          "source":       "policy" | "default" | "error",
          "policy_error": "<message>"               # empty unless source=='error'
        }

    `source` semantics:
      - `policy`  → `polyphony policy resolve` returned a valid envelope
                    with non-null `mode`.
      - `default` → CLI succeeded but `mode` was missing / null. Should
                    not happen in practice since PolicyLoader stamps
                    defaults at load time (`pr.defaults.mode = warning`)
                    but defended here so a stripped policy can't
                    silently change behavior.
      - `error`   → CLI failed, JSON was malformed, exited non-zero, or
                    the value was outside the allowed domain.
                    `policy_error` carries the message for the gate
                    prompt to render.
.NOTES
    Companion to `pr_pre_merge_policy_router` in github-pr.yaml and
    ado-pr.yaml. The output schema is the workflow's input schema for
    the pre-merge mode-routing leg; the helper Pester tests pin both
    shapes.
.PARAMETER PolyphonyExe
    Path or command to invoke for the polyphony CLI. Defaults to
    'polyphony' (PATH lookup). Tests override this to point at a
    deterministic stub script that emits scripted `policy resolve`
    envelopes.
.PARAMETER Scope
    Scope to pass to `policy resolve --scope`. Defaults to 'default'.
    Workflows pass `type:<type>` when the work item type has been
    resolved upstream.
#>
[CmdletBinding()]
param(
    [string]$PolyphonyExe = 'polyphony',
    [string]$Scope = 'default'
)

$ErrorActionPreference = 'Stop'

$allowedModes = @('auto', 'warning', 'manual')

# Default applied when policy resolution succeeds-but-missing or fails.
# On error, mode flips to 'manual' to surface the failure at the gate
# rather than silently auto-merging.
$mode = 'warning'
$source = 'default'
$policyError = ''

try {
    $raw = & $PolyphonyExe policy resolve --domain pr --scope $Scope 2>$null
    if ($LASTEXITCODE -ne 0) {
        $source = 'error'
        $mode = 'manual'
        $policyError = "polyphony policy resolve exited $LASTEXITCODE"
    }
    elseif ([string]::IsNullOrWhiteSpace($raw)) {
        $source = 'error'
        $mode = 'manual'
        $policyError = 'polyphony policy resolve returned empty output'
    }
    else {
        try {
            $parsed = $raw | ConvertFrom-Json -ErrorAction Stop
        } catch {
            $parsed = $null
            $source = 'error'
            $mode = 'manual'
            $policyError = "policy JSON parse failed: $($_.Exception.Message)"
        }

        if ($parsed) {
            if ($parsed.PSObject.Properties.Name -contains 'mode') {
                $modeValue = $parsed.mode
                if ($modeValue -in $allowedModes) {
                    $mode = [string]$modeValue
                    $source = 'policy'
                }
                elseif ($null -ne $modeValue -and $modeValue -ne '') {
                    # Surface as error so the policy author notices a bad value.
                    $source = 'error'
                    $mode = 'manual'
                    $policyError = "mode='$modeValue' is not one of [$($allowedModes -join ', ')]"
                }
                # else: present-but-null → keep default fallback (source stays 'default')
            }
        }
    }
} catch {
    $source = 'error'
    $mode = 'manual'
    $policyError = "polyphony policy resolve threw: $($_.Exception.Message)"
}

[ordered]@{
    mode         = $mode
    source       = $source
    policy_error = $policyError
} | ConvertTo-Json -Compress
