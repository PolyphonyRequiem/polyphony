<#
.SYNOPSIS
    CI lint — validates plan-level.yaml open_questions policy wiring, scope-renegotiation handler wiring, and structural requirements.
.DESCRIPTION
    Parses workflows/plan-level.yaml and verifies:
    1. open_questions_policy script node exists and calls `polyphony policy resolve --domain open_questions`
    2. open_questions_counter script node exists
    3. open_questions_answer_counter script node exists
    4. Policy-aware routes exist (mode==auto, mode==manual, mode==warning)
    5. No hardcoded severity list remains in architect→gate routing
    6. open_questions_gate references policy mode and loop counter in prompt
    7. cap_reached route exists
    8. Phase 3 P8c handler — validate_scope script node exists,
       positioned post-review and pre-merge (after plan_reviewer, before
       merge_plan_pr) and gated on workflow.input.child_scope_globs.
    9. Phase 3 P8c handler — scope_violation_gate human_gate exists and
       is gated on validate_scope verdict == 'block'.
    10. Phase 3 P8c handler — extract_renegotiation_flag script node
        exists and runs post-merge (referenced from merge_plan_pr).
    11. Phase 3 P8c handler — workflow exports the four bubble-up
        outputs at workflow scope (renegotiation_pending,
        renegotiation_request, validate_scope_verdict,
        scope_violation_files).
    Exits 0 if clean, 1 if violations found.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$repoRoot = Join-Path $PSScriptRoot '..'
$yamlPath = Join-Path $repoRoot 'workflows' 'plan-level.yaml'

if (-not (Test-Path $yamlPath)) {
    Write-Host "SKIP: $yamlPath not found" -ForegroundColor Yellow
    exit 0
}

$content = Get-Content $yamlPath -Raw
$lines = @(Get-Content $yamlPath)

$violations = @()

# ── Check 1: open_questions_policy script node exists ────────────────────
if ($content -notmatch 'name:\s*open_questions_policy') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-oq-policy-node'
        Detail = "No open_questions_policy script node found"
    }
}

# ── Check 2: Policy node calls polyphony policy resolve --domain open_questions
# Args are on separate lines in YAML, so check for key components individually
$hasPolicyResolve = ($content -match '"policy"') -and ($content -match '"resolve"') -and ($content -match '"--domain"') -and ($content -match '"open_questions"')
if (-not $hasPolicyResolve) {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-policy-resolve-call'
        Detail = "open_questions_policy does not call 'polyphony policy resolve --domain open_questions'"
    }
}

# ── Check 3: open_questions_counter script node exists ───────────────────
if ($content -notmatch 'name:\s*open_questions_counter') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-oq-counter-node'
        Detail = "No open_questions_counter script node found"
    }
}

# ── Check 4: open_questions_answer_counter script node exists ────────────
if ($content -notmatch 'name:\s*open_questions_answer_counter') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-oq-answer-counter-node'
        Detail = "No open_questions_answer_counter script node found"
    }
}

# ── Check 5: Policy-aware routes — mode==auto skips gate ─────────────────
if ($content -notmatch "open_questions_policy\.output\.mode\s*==\s*'auto'") {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-auto-mode-route'
        Detail = "No route condition for mode=='auto' (should skip gate)"
    }
}

# ── Check 6: Policy-aware routes — mode==manual gates on any question ────
if ($content -notmatch "open_questions_policy\.output\.mode\s*==\s*'manual'") {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-manual-mode-route'
        Detail = "No route condition for mode=='manual' (should gate on any question)"
    }
}

# ── Check 7: Policy-aware routes — mode==warning uses severities_at_or_above
if ($content -notmatch 'severities_at_or_above\(open_questions_policy\.output\.min_severity\)') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-warning-mode-route'
        Detail = "No route condition using severities_at_or_above(open_questions_policy.output.min_severity)"
    }
}

# ── Check 8: No hardcoded severity list in architect routes ──────────────
# The old pattern had selectattr('severity', 'in', ['moderate', 'major', 'critical'])
# directly in the architect's routes block. This should no longer exist.
if ($content -match "architect\.output\.open_questions\s*\|\s*selectattr\('severity',\s*'in',\s*\['") {
    $violations += [PSCustomObject]@{
        Rule   = 'hardcoded-severity-filter'
        Detail = "Hardcoded severity list found in architect routing — should use policy-driven severities_at_or_above()"
    }
}

# ── Check 9: Gate prompt references policy mode ──────────────────────────
if ($content -notmatch 'open_questions_policy\.output\.mode.*open_questions_counter\.output') {
    # Looser check: both should appear in the gate prompt section
    $hasMode = $content -match 'open_questions_policy\.output\.mode'
    $hasCounter = $content -match 'open_questions_counter\.output\.(iteration|max_loops)'
    if (-not ($hasMode -and $hasCounter)) {
        $violations += [PSCustomObject]@{
            Rule   = 'gate-missing-policy-context'
            Detail = "open_questions_gate prompt should surface policy mode and loop counter"
        }
    }
}

# ── Check 10: Cap reached route exists ───────────────────────────────────
if ($content -notmatch 'open_questions_counter\.output\.cap_reached\s*==\s*true') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-cap-reached-route'
        Detail = "No route condition for cap_reached==true (should auto-proceed to review)"
    }
}

# ── Check 11: Phase 3 P8c — validate_scope script node exists ────────────
if ($content -notmatch 'name:\s*validate_scope\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-validate-scope-node'
        Detail = "No validate_scope script node found (Phase 3 P8c handler)"
    }
}

# ── Check 12: validate_scope invokes the polyphony plan validate-scope verb
$hasValidateScopeVerb = ($content -match '"validate-scope"') -and ($content -match '"--child-scope"')
if (-not $hasValidateScopeVerb) {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-validate-scope-verb'
        Detail = "validate_scope does not call 'polyphony plan validate-scope --child-scope'"
    }
}

# ── Check 13: poll_status routes to validate_scope when child_scope_globs supplied
# The route condition is gated on workflow.input.child_scope_globs being
# non-empty so today's callers (which never set the input) keep routing
# straight to merge_plan_pr.
if ($content -notmatch "to:\s*validate_scope[\s\S]{0,300}?workflow\.input\.child_scope_globs") {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-validate-scope-route'
        Detail = "poll_status does not route to validate_scope gated on workflow.input.child_scope_globs"
    }
}

# ── Check 14: validate_scope is positioned post-review, pre-merge ────────
# Indices in $content of the literal `name: validate_scope` declaration
# vs the merge_plan_pr / plan_reviewer declarations. validate_scope must
# appear AFTER plan_reviewer and BEFORE merge_plan_pr in the github leg.
$idxPlanReviewer = $content.IndexOf("name: plan_reviewer`n")
if ($idxPlanReviewer -lt 0) { $idxPlanReviewer = $content.IndexOf("- name: plan_reviewer") }
$idxValidateScope = $content.IndexOf("name: validate_scope")
$idxMergePlanPr = $content.IndexOf("name: merge_plan_pr`n")
if ($idxMergePlanPr -lt 0) { $idxMergePlanPr = $content.IndexOf("- name: merge_plan_pr`r") }
if ($idxMergePlanPr -lt 0) {
    # Fallback: locate the bare `merge_plan_pr` agent declaration (avoid
    # matching merge_plan_pr_ado / merge_plan_pr_ado_*).
    $matches = [regex]::Matches($content, '- name: merge_plan_pr\b(?!_)')
    if ($matches.Count -gt 0) { $idxMergePlanPr = $matches[0].Index }
}
if ($idxValidateScope -ge 0 -and $idxPlanReviewer -ge 0 -and $idxMergePlanPr -ge 0) {
    if ($idxValidateScope -lt $idxPlanReviewer) {
        $violations += [PSCustomObject]@{
            Rule   = 'validate-scope-before-review'
            Detail = "validate_scope is declared BEFORE plan_reviewer (must be post-review, pre-merge)"
        }
    }
    if ($idxValidateScope -gt $idxMergePlanPr) {
        $violations += [PSCustomObject]@{
            Rule   = 'validate-scope-after-merge'
            Detail = "validate_scope is declared AFTER merge_plan_pr (must be post-review, pre-merge)"
        }
    }
}

# ── Check 15: scope_violation_gate exists and is gated on 'block' ────────
if ($content -notmatch 'name:\s*scope_violation_gate') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-scope-violation-gate'
        Detail = "No scope_violation_gate human_gate found (Phase 3 P8c handler)"
    }
}
if ($content -notmatch "validate_scope\.output\.verdict\s*==\s*'block'") {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-block-verdict-route'
        Detail = "No route condition for validate_scope.output.verdict == 'block'"
    }
}

# ── Check 16: extract_renegotiation_flag exists and runs post-merge ─────
if ($content -notmatch 'name:\s*extract_renegotiation_flag') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-extract-renegotiation-flag-node'
        Detail = "No extract_renegotiation_flag script node found (Phase 3 P8c handler)"
    }
}
if ($content -notmatch '"extract-renegotiation-flag"') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-extract-renegotiation-flag-verb'
        Detail = "extract_renegotiation_flag does not call 'polyphony plan extract-renegotiation-flag'"
    }
}
# extract_renegotiation_flag must be reached from merge_plan_pr (the
# successful-merge route target) so the flag is harvested post-merge.
if ($content -notmatch "merge_plan_pr[\s\S]{0,2000}?to:\s*extract_renegotiation_flag") {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-post-merge-renegotiation-route'
        Detail = "merge_plan_pr does not route to extract_renegotiation_flag (post-merge handler not wired)"
    }
}

# ── Check 17: Workflow exports the four bubble-up outputs ────────────────
# Per M7 a sub-workflow's outward-facing surface is its top-level
# `output:` map. The handler exposes four keys for the (forthcoming)
# apex-driver consumer. Match on key names (the value templates can
# vary in formatting).
$bubbleKeys = @(
    @{ Key = 'renegotiation_pending';    Rule = 'missing-output-renegotiation-pending' },
    @{ Key = 'renegotiation_request';    Rule = 'missing-output-renegotiation-request' },
    @{ Key = 'validate_scope_verdict';   Rule = 'missing-output-validate-scope-verdict' },
    @{ Key = 'scope_violation_files';    Rule = 'missing-output-scope-violation-files' }
)
foreach ($b in $bubbleKeys) {
    if ($content -notmatch ('(?m)^\s+' + [regex]::Escape($b.Key) + ':')) {
        $violations += [PSCustomObject]@{
            Rule   = $b.Rule
            Detail = "Workflow output map does not export '$($b.Key)' (Phase 3 P8c bubble-up)"
        }
    }
}

# ── Report ───────────────────────────────────────────────────────────────
if ($violations.Count -gt 0) {
    Write-Host "`n❌ plan-level.yaml open_questions policy lint FAILED ($($violations.Count) violations):`n" -ForegroundColor Red
    foreach ($v in $violations) {
        Write-Host "  [$($v.Rule)] $($v.Detail)" -ForegroundColor Red
    }
    Write-Host ""
    exit 1
} else {
    Write-Host "✅ plan-level.yaml lint passed (open_questions policy + Phase 3 P8c scope-renegotiation handler)" -ForegroundColor Green
    exit 0
}
