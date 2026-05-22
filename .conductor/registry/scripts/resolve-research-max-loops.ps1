<#
.SYNOPSIS
    Resolve `policy.research.defaults.max_research_loops` for the
    `research_loop_counter` step in plan-level.yaml.
.DESCRIPTION
    Invoked by `research_loops_policy` (the pre-counter resolver in
    plan-level.yaml). Calls `polyphony policy resolve --domain research
    --scope <scope>`, extracts `max_research_loops`, and emits a JSON
    envelope the counter can consume.

    This is intentionally SEPARATE from `resolve-research-policy.ps1`:
    the latter resolves `escalation_cap` + `mode` for the research
    leg and runs AFTER the counter; this script resolves the plan-level
    loop cap and runs BEFORE the counter. Splitting them keeps a malformed
    `max_research_loops` from poisoning escalation behavior and vice
    versa (field-specific diagnostics), and avoids the force-route
    regression that would arise from moving the existing resolver upstream
    of the counter.

    Per the polyphony-workflow-author skill conventions:
      - Always exits 0; routing is condition-based, not exit-code-based.
      - Malformed policy / CLI errors degrade to the deterministic
        fallback (`max_research_loops = 3`, the historical hard-coded
        MVP value). The `source` and `policy_error` fields let the
        cap_reached gate's prompt surface the failure when it fires.
      - `max_research_loops` is non-negative (0 is legal and disables
        research entirely — the first request routes straight to
        `research_cap_gate`). Negative values are rejected as 'error'.

    Output JSON envelope:
        {
          "max_research_loops": <int>,        # 0..N; default 3
          "source":             "policy" | "default" | "error",
          "policy_error":       "<message>"   # empty unless source=='error'
        }

    `source` semantics:
      - `policy`  → `polyphony policy resolve` returned a non-null
                    `max_research_loops` and the value parsed as a
                    non-negative int.
      - `default` → CLI succeeded but the field was missing / null.
                    Should not happen in practice since PolicyLoader
                    stamps the default of 3 at load time, but defended
                    here so a stripped policy can't silently change
                    behavior.
      - `error`   → CLI failed, JSON was malformed, exited non-zero,
                    or the value was outside its allowed domain.
                    `policy_error` carries the message for downstream
                    gate prompts to render.
.NOTES
    Companion to `research_loops_policy` in plan-level.yaml. The output
    schema is the counter's input schema for `$maxLoops`; the helper
    Pester tests pin both shapes.
.PARAMETER PolyphonyExe
    Path or command to invoke for the polyphony CLI. Defaults to
    'polyphony' (PATH lookup). Tests override this to point at a
    deterministic stub script that emits scripted `policy resolve`
    envelopes.
.PARAMETER Scope
    Scope to pass to `policy resolve --scope`. Defaults to 'default'.
    Plan-level.yaml passes `type:<type>` when the work item type has
    been resolved (type_loader is upstream of research_loops_policy).
#>
[CmdletBinding()]
param(
    [string]$PolyphonyExe = 'polyphony',
    [string]$Scope = 'default'
)

$ErrorActionPreference = 'Stop'

# Historical hard-coded MVP value. Preserved as fallback so any failure
# in the policy layer degrades to the pre-policy behavior rather than
# changing semantics silently.
$maxResearchLoops = 3
$source = 'default'
$policyError = ''

try {
    $raw = & $PolyphonyExe policy resolve --domain research --scope $Scope 2>$null
    if ($LASTEXITCODE -ne 0) {
        $source = 'error'
        $policyError = "polyphony policy resolve exited $LASTEXITCODE"
    }
    elseif ([string]::IsNullOrWhiteSpace($raw)) {
        $source = 'error'
        $policyError = 'polyphony policy resolve returned empty output'
    }
    else {
        try {
            $parsed = $raw | ConvertFrom-Json -ErrorAction Stop
        } catch {
            $parsed = $null
            $source = 'error'
            $policyError = "policy JSON parse failed: $($_.Exception.Message)"
        }

        if ($parsed -and $parsed.PSObject.Properties.Name -contains 'max_research_loops') {
            $value = $parsed.max_research_loops
            if ($value -is [int] -or $value -is [long]) {
                if ($value -ge 0) {
                    $maxResearchLoops = [int]$value
                    $source = 'policy'
                }
                else {
                    $source = 'error'
                    $policyError = "max_research_loops=$value is negative"
                }
            }
            elseif ($null -ne $value -and $value -ne '') {
                # Surface as error so the policy author notices a non-int
                # value (e.g. a quoted "3" YAML literal that survived
                # deserialization).
                $source = 'error'
                $policyError = "max_research_loops='$value' is not a non-negative integer"
            }
            # else: present-but-null → keep default fallback (source stays 'default')
        }
    }
} catch {
    $source = 'error'
    $policyError = "polyphony policy resolve threw: $($_.Exception.Message)"
}

[ordered]@{
    max_research_loops = $maxResearchLoops
    source             = $source
    policy_error       = $policyError
} | ConvertTo-Json -Compress
