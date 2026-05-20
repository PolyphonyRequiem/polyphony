<#
.SYNOPSIS
    Resolve `policy.research.defaults.{escalation_cap, mode}` for the
    research_dispatch router in plan-level.yaml (AB#3188).
.DESCRIPTION
    Invoked by the `research_policy_resolver` step that fronts every path
    into `research_dispatch`. Calls `polyphony policy resolve --domain
    research --scope <scope>`, extracts `escalation_cap` and `mode`, and
    emits a JSON envelope the workflow can consume.

    Per the polyphony-workflow-author skill conventions:
      - Always exits 0; routing is condition-based, not exit-code-based.
      - Malformed policy / CLI errors degrade to a deterministic
        fallback (`escalation_cap = 1`, `mode = 'manual'`) so the
        workflow surfaces the failure at the next human gate rather
        than tripping conductor's StrictUndefined and failing the
        entire run. Same defensive posture as
        `resolve-unattended-cap-mode.ps1`.
      - The `source` and `policy_error` fields let the manual gate's
        prompt surface "policy resolution failed; falling back to
        manual" so a typo in policy.yaml doesn't hide silently behind
        the default fallback.
      - On error, `mode` deliberately falls back to `'manual'` (not
        the per-policy `'warning'` default) so the operator is forced
        to acknowledge the failure at `escalation_decision_gate`
        rather than the workflow silently auto-escalating with a
        broken policy.

    Allowed `mode` values (from src/Polyphony/Policy/PolicyConfig.cs
    `ResearchMode`):
      - `auto`    → never gate; always honor escalation_cap.
      - `warning` → currently aliases `auto` (no post-deep
                    sufficiency check exists yet). Documented in
                    research.yaml. TODO once a second sufficiency
                    check lands.
      - `manual`  → gate before deep_researcher escalation when
                    `researcher.output.sufficient == false`.

    `escalation_cap`: non-negative integer. 0 forces the researcher
    sufficiency judge to emit `sufficient=true` (no escalation); 1
    allows a single deep_researcher pass (the default); >1 is accepted
    by config but the workflow today honors only one pass (see
    research.yaml line ~247-248).

    Output JSON envelope:
        {
          "escalation_cap": <int>,                    # 0..N; default 1
          "mode":           "auto" | "warning" | "manual",
          "source":         "policy" | "default" | "error",
          "policy_error":   "<message>"               # empty unless source=='error'
        }

    `source` semantics:
      - `policy`  → `polyphony policy resolve` returned a valid envelope
                    with non-null escalation_cap + mode.
      - `default` → CLI succeeded but one or both fields were missing /
                    null. Should not happen in practice since
                    PolicyLoader stamps defaults at load time, but
                    defended here so a stripped policy can't silently
                    change behavior.
      - `error`   → CLI failed, JSON was malformed, exited non-zero,
                    or one of the field values was outside its allowed
                    domain. `policy_error` carries the message for the
                    manual-gate prompt to render.

    Followup: profile.research.escalation_cap (ResearchConfig.cs:59)
    is read by C# but not exposed via any CLI verb today. AB#3188
    intentionally wires only the policy-side fix; profile plumbing is
    deferred to a follow-up. The resolver returns whatever
    `polyphony policy resolve` reports; once profile precedence is
    added to the C# resolver, this script needs no changes.
.NOTES
    Companion to `research_policy_resolver` in plan-level.yaml. The
    output schema is the workflow's input schema for the research
    leg; the helper Pester tests pin both shapes.
.PARAMETER PolyphonyExe
    Path or command to invoke for the polyphony CLI. Defaults to
    'polyphony' (PATH lookup). Tests override this to point at a
    deterministic stub script that emits scripted `policy resolve`
    envelopes.
.PARAMETER Scope
    Scope to pass to `policy resolve --scope`. Defaults to 'default'.
    Plan-level.yaml passes `type:<type>` when the work item type has
    been resolved (type_loader is upstream of research_dispatch).
#>
[CmdletBinding()]
param(
    [string]$PolyphonyExe = 'polyphony',
    [string]$Scope = 'default'
)

$ErrorActionPreference = 'Stop'

$allowedModes = @('auto', 'warning', 'manual')

# Defaults applied when policy resolution succeeds-but-missing or fails.
# On error, mode flips to 'manual' to surface the failure at the gate
# rather than silently auto-escalating.
$escalationCap = 1
$mode = 'warning'
$source = 'default'
$policyError = ''

try {
    $raw = & $PolyphonyExe policy resolve --domain research --scope $Scope 2>$null
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
            # Two-phase validation: first pass records findings; second pass
            # applies them only if no error fired. This keeps error semantics
            # order-independent — a valid `mode` after a bad `escalation_cap`
            # must NOT overwrite the error-fallback `mode='manual'`.
            $capCandidate = $null
            $modeCandidate = $null
            $haveCap = $false
            $haveMode = $false

            if ($parsed.PSObject.Properties.Name -contains 'escalation_cap') {
                $capValue = $parsed.escalation_cap
                if ($capValue -is [int] -or $capValue -is [long]) {
                    if ($capValue -ge 0) {
                        $capCandidate = [int]$capValue
                        $haveCap = $true
                    }
                    else {
                        $source = 'error'
                        $policyError = "escalation_cap=$capValue is negative"
                    }
                }
                elseif ($null -ne $capValue -and $capValue -ne '') {
                    # Surface as error so the policy author notices a non-int value
                    # (e.g. a quoted "1" YAML literal that survived deserialization).
                    $source = 'error'
                    $policyError = "escalation_cap='$capValue' is not a non-negative integer"
                }
                # else: present-but-null → keep default fallback (source stays 'default')
            }

            if ($parsed.PSObject.Properties.Name -contains 'mode') {
                $modeValue = $parsed.mode
                if ($modeValue -in $allowedModes) {
                    $modeCandidate = [string]$modeValue
                    $haveMode = $true
                }
                elseif ($null -ne $modeValue -and $modeValue -ne '') {
                    if ($source -ne 'error') {
                        # First error wins; don't clobber an earlier escalation_cap error.
                        $source = 'error'
                        $policyError = "mode='$modeValue' is not one of [$($allowedModes -join ', ')]"
                    }
                }
                # else: present-but-null → keep default fallback (source stays 'default')
            }

            if ($source -eq 'error') {
                # Force the manual fallback so the failure surfaces at the gate.
                $mode = 'manual'
            }
            else {
                if ($haveCap) { $escalationCap = $capCandidate }
                if ($haveMode) { $mode = $modeCandidate }
                if ($haveCap -and $haveMode) { $source = 'policy' }
            }
        }
    }
} catch {
    $source = 'error'
    $mode = 'manual'
    $policyError = "polyphony policy resolve threw: $($_.Exception.Message)"
}

[ordered]@{
    escalation_cap = $escalationCap
    mode           = $mode
    source         = $source
    policy_error   = $policyError
} | ConvertTo-Json -Compress
