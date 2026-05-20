<#
.SYNOPSIS
    Resolve `policy.unattended.cap_mode` for cap-hit gate policy routers (AB#3186).
.DESCRIPTION
    Invoked by the `<gate>_policy_router` step that fronts every cap-hit
    `human_gate` in the workflows (revise_cap_gate, remediation_cap_gate,
    scope_revise_cap_gate, depth_exceeded_gate, …). Calls
    `polyphony policy load`, extracts `unattended.cap_mode`, validates it
    against the allowed enum, and emits a JSON envelope the router can
    branch on.

    Per the polyphony-workflow-author skill conventions:
      - Always exits 0; routing is condition-based, not exit-code-based.
      - Malformed policy / CLI errors degrade to `cap_mode = 'manual'` so
        the workflow falls through to the human gate (today's behavior)
        rather than tripping conductor's StrictUndefined and failing the
        entire run. Same defensive posture as `renegotiation_policy` in
        apex-driver.yaml.
      - The `source` and `policy_error` fields let the manual gate's
        prompt surface "policy resolution failed; falling back to
        manual" so a typo in policy.yaml doesn't hide silently behind
        the default fallback.

    Allowed `cap_mode` values (from src/Polyphony/Policy/PolicyConfig.cs
    `UnattendedCapMode`):
      - `manual`       → surface the human gate (default; also the
                         fallback on policy error).
      - `auto_proceed` → bypass the gate via its "continue" route
                         (e.g. force one more revision, restart
                         remediation cycle, acknowledge depth).
      - `auto_fail`    → terminate the run with a clear "policy
                         auto_fail" reason via the cap-mode auto-fail
                         terminal (NOT the operator-abort terminal —
                         honesty about who decided to halt).

    Output JSON envelope:
        {
          "cap_mode":     "manual" | "auto_proceed" | "auto_fail",
          "source":       "policy" | "default" | "error",
          "policy_error": "<message>"            # empty unless source=='error'
        }

    `source` semantics:
      - `policy`  → `polyphony policy load` returned a valid cap_mode value.
      - `default` → CLI succeeded but `unattended.cap_mode` was missing /
                    null. Should not happen in practice since
                    PolicyLoader.cs:194 stamps `manual` as the default,
                    but defended against here so a stripped policy can't
                    silently change behavior.
      - `error`   → CLI failed, JSON was malformed, or the cap_mode value
                    was not one of the allowed enum constants. `policy_error`
                    carries the message for the manual-gate prompt to
                    render.
.NOTES
    Companion to every `<gate>_policy_router` script step that consumes
    `unattended.cap_mode`. The output schema is the workflow's input
    schema for those routers; the helper Pester tests pin both shapes.
.PARAMETER PolyphonyExe
    Path or command to invoke for the polyphony CLI. Defaults to
    'polyphony' (PATH lookup). Tests override this to point at a
    deterministic stub script that emits scripted policy-load envelopes.
#>
[CmdletBinding()]
param(
    [string]$PolyphonyExe = 'polyphony'
)

$ErrorActionPreference = 'Stop'

$allowed = @('manual', 'auto_proceed', 'auto_fail')

$capMode = 'manual'
$source = 'default'
$policyError = ''

try {
    $raw = & $PolyphonyExe policy load 2>$null
    if ($LASTEXITCODE -ne 0) {
        $source = 'error'
        $policyError = "polyphony policy load exited $LASTEXITCODE"
    }
    elseif ([string]::IsNullOrWhiteSpace($raw)) {
        $source = 'error'
        $policyError = 'polyphony policy load returned empty output'
    }
    else {
        try {
            $parsed = $raw | ConvertFrom-Json -ErrorAction Stop
        } catch {
            $parsed = $null
            $source = 'error'
            $policyError = "policy JSON parse failed: $($_.Exception.Message)"
        }

        if ($parsed -and ($parsed.PSObject.Properties.Name -contains 'unattended')) {
            $unattended = $parsed.unattended
            if ($unattended -and ($unattended.PSObject.Properties.Name -contains 'cap_mode')) {
                $value = $unattended.cap_mode
                if ($value -in $allowed) {
                    $capMode = $value
                    $source = 'policy'
                }
                elseif ($null -ne $value -and $value -ne '') {
                    $source = 'error'
                    $policyError = "unattended.cap_mode='$value' is not one of [$($allowed -join ', ')]"
                }
                # else: cap_mode present but null/empty → keep default fallback
            }
        }
    }
} catch {
    $source = 'error'
    $policyError = "polyphony policy load threw: $($_.Exception.Message)"
}

[ordered]@{
    cap_mode     = $capMode
    source       = $source
    policy_error = $policyError
} | ConvertTo-Json -Compress
