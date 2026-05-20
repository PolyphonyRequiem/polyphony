<#
.SYNOPSIS
    CI lint — validates plan-level.yaml open_questions policy wiring, scope-renegotiation handler wiring, and structural requirements.
.DESCRIPTION
    Parses workflows/plan-level.yaml and verifies:
    1. open_questions_policy script node exists and calls `polyphony policy resolve --domain open_questions`
    2. open_questions_counter script node exists (single counter tracks
       both initial questions and answer-loop iterations — the prior
       separate open_questions_answer_counter has been folded in)
    3. Policy-aware routes exist (mode==auto, mode==manual, mode==warning)
    4. No hardcoded severity list remains in architect→gate routing
    5. open_questions_gate references policy mode and loop counter in prompt
    6. cap_reached route exists
    7. Phase 3 P8c handler — validate_scope script node exists,
       positioned post-review and pre-merge (after plan_reviewer, before
       merge_plan_pr) and gated on workflow.input.child_scope_globs.
    8. Phase 3 P8c handler — scope_violation_gate human_gate exists and
       is gated on validate_scope verdict == 'block'.
    9. Phase 3 P8c handler — extract_renegotiation_flag script node
       exists and runs post-merge (referenced from merge_plan_pr).
    10. Phase 3 P8c handler — workflow exports the four bubble-up
        outputs at workflow scope (renegotiation_pending,
        renegotiation_request, validate_scope_verdict,
        scope_violation_files).
    11. Stuck-review timeout MVP — pending_poll_counter exists, both
        poll_status and poll_status_ado route their 'pending' case
        through it, stuck_review_gate exposes continue_waiting /
        override_approved / abort options, stuck_review_reset and
        stuck_review_override_router both exist.
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

# ── Check 4: Policy-aware routes — mode==auto skips gate ─────────────────
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
# Rev 4 (2026-05-08): the workflow now references the precomputed
# `open_questions_policy.output.severities_at_or_above` list emitted by
# `polyphony policy resolve --domain open_questions`. The legacy form
# `severities_at_or_above(open_questions_policy.output.min_severity)` was a
# Jinja function call that conductor never honored — surfaced live in the
# #3043 dogfood as `'severities_at_or_above' is undefined`. Lint now enforces
# the field reference and rejects the legacy function form.
if ($content -notmatch 'open_questions_policy\.output\.severities_at_or_above') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-warning-mode-route'
        Detail = "No route condition referencing open_questions_policy.output.severities_at_or_above (precomputed severity list emitted by `polyphony policy resolve --domain open_questions`)."
    }
}

if ($content -match 'severities_at_or_above\s*\(') {
    $violations += [PSCustomObject]@{
        Rule   = 'severities-at-or-above-as-function'
        Detail = "Found legacy `severities_at_or_above(...)` function-call form. Conductor has no such Jinja extension; reference the precomputed `open_questions_policy.output.severities_at_or_above` list field instead."
    }
}

# ── Check 8: No hardcoded severity list in architect routes ──────────────
# The old pattern had selectattr('severity', 'in', ['moderate', 'major', 'critical'])
# directly in the architect's routes block. This should no longer exist.
if ($content -match "architect\.output\.open_questions\s*\|\s*selectattr\('severity',\s*'in',\s*\['") {
    $violations += [PSCustomObject]@{
        Rule   = 'hardcoded-severity-filter'
        Detail = "Hardcoded severity list found in architect routing — should reference policy-driven open_questions_policy.output.severities_at_or_above"
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

# ── Check 18: Stuck-review timeout MVP — pending_poll_counter exists ─────
if ($content -notmatch 'name:\s*pending_poll_counter\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-pending-poll-counter'
        Detail = "No pending_poll_counter script node found (stuck-review timeout MVP)"
    }
}

# ── Check 19: poll_status routes 'none' through pr_feedback_analyzer ────
# Under the sentiment-driven model both poll_status and poll_status_ado
# must defer the ambiguous-middle case (route == 'none') to the
# pr_feedback_analyzer agent — that node is what decides whether the
# pending_poll_counter throttle path runs vs sending the architect to
# revise.
if ($content -notmatch "to:\s*pr_feedback_analyzer[\s\S]{0,200}?poll_status\.output\.route\s*==\s*'none'") {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-poll-status-none-route'
        Detail = "poll_status does not route 'none' through pr_feedback_analyzer"
    }
}
if ($content -notmatch "to:\s*pr_feedback_analyzer[\s\S]{0,200}?poll_status_ado\.output\.route\s*==\s*'none'") {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-poll-status-ado-none-route'
        Detail = "poll_status_ado does not route 'none' through pr_feedback_analyzer"
    }
}

# ── Check 19b: pr_feedback_analyzer node exists ─────────────────────────
if ($content -notmatch 'name:\s*pr_feedback_analyzer\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-pr-feedback-analyzer'
        Detail = "No pr_feedback_analyzer agent node found (sentiment-driven loop)"
    }
}

# ── Check 20: pending_poll_counter routes to stuck_review_gate on cap ───
if ($content -notmatch "pending_poll_counter\.output\.cap_reached\s*==\s*true") {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-pending-poll-cap-route'
        Detail = "pending_poll_counter does not route on cap_reached==true"
    }
}

# ── Check 21: stuck_review_gate exists with all three required options ──
if ($content -notmatch 'name:\s*stuck_review_gate\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-stuck-review-gate'
        Detail = "No stuck_review_gate human_gate found (stuck-review timeout MVP)"
    }
}
$stuckOptions = @('continue_waiting', 'override_approved', 'abort')
foreach ($opt in $stuckOptions) {
    # Locate the stuck_review_gate block then check option presence.
    $gateBlock = ''
    $m = [regex]::Match($content, '(?s)- name: stuck_review_gate\b.*?(?=\n  - name: |\Z)')
    if ($m.Success) { $gateBlock = $m.Value }
    if ($gateBlock -notmatch "value:\s*$opt\b") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-stuck-review-option'
            Detail = "stuck_review_gate missing option value: '$opt'"
        }
    }
}

# ── Check 22: stuck_review_reset script exists ──────────────────────────
if ($content -notmatch 'name:\s*stuck_review_reset\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-stuck-review-reset'
        Detail = "No stuck_review_reset script node found (stuck-review timeout MVP)"
    }
}

# ── Check 23: stuck_review_override_router routes per platform ──────────
if ($content -notmatch 'name:\s*stuck_review_override_router\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-stuck-review-override-router'
        Detail = "No stuck_review_override_router script node found (stuck-review timeout MVP)"
    }
}

# ── Check 24: open_questions_policy --scope arg uses canonical type_loader output ──
# `polyphony plan load-type` emits {"type": "..."} — historically a typo
# read `type_loader.output.type_name` here, which silently rendered as the
# string "type:" at lint time and then exploded with strict_undefined at
# runtime ("'dict object' has no attribute 'type_name'"). Pin the field
# name so the typo can never come back. We do NOT require the --scope
# arg to be present at all — the verb falls back to the default scope
# when omitted, which is valid; we only block the typo when present.
$policyBlock = ''
$m = [regex]::Match($content, '(?s)- name: open_questions_policy\b.*?(?=\n  - name: |\Z)')
if ($m.Success) { $policyBlock = $m.Value }
if ($policyBlock -match 'type_loader\.output\.type_name\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'open-questions-policy-bad-type-field'
        Detail = "open_questions_policy --scope references type_loader.output.type_name; the verb emits 'type', not 'type_name' (caused dogfood failure on apex #3043, 2026-05-07)"
    }
}# ── Check 25: ancestor_chain.output.parent_item_id default-filter form ──
# Bug #8 (dogfood apex #3043, 2026-05-08, two iterations).
#
# Wire shape: `polyphony plan derive-ancestor-chain` returns int? for
# `parent_item_id`, always emitted (per-property [JsonIgnore(Never)] in
# PlanDeriveAncestorChainResult.cs). On the root path the value is JSON
# null; on direct children of root, also null; on deeper descendants,
# an integer.
#
# Filter: conductor's custom Jinja `default` filter
# (conductor.executor.template.TemplateRenderer._default_filter) is
# `_default_filter(value, default="")` — only TWO positional args. It
# already returns `default` when `value is None` (handles BOTH
# Undefined AND None — better than standard Jinja, which only handles
# Undefined unless given the boolean=True second arg).
#
# Therefore the correct form is bare `default(0)`. Standard Jinja's
# 3-arg `default(0, true)` form CRASHES conductor at runtime with
# "TemplateRenderer._default_filter() takes from 1 to 2 positional
# arguments but 3 were given" (iter 5 of the apex #3043 dogfood).
#
# This check refuses the 3-arg form to prevent the regression.
$threeArgMatches = [regex]::Matches(
    $content,
    'parent_item_id\s*\|\s*default\(\s*[^)]*,\s*[^)]+\)')
if ($threeArgMatches.Count -gt 0) {
    $violations += [PSCustomObject]@{
        Rule   = 'parent-item-id-multi-arg-default'
        Detail = "Found $($threeArgMatches.Count) reference(s) to ancestor_chain.output.parent_item_id with the multi-arg `default(...)` form (e.g. `default(0, true)`); conductor's custom _default_filter only accepts 2 positional args (value, default) and crashes on 3. Use bare `default(0)` — conductor's filter substitutes on both Undefined AND None (caused dogfood failure on apex #3043 iter 5, 2026-05-08)"
    }
}

# ── Check 26: merged_unseeded resume path invokes seeder ────────────────
# Closed-loop PR #8. The state_detector step must route the
# 'merged_unseeded' state to the seeder step (so `polyphony plan
# seed-children` is invoked on the resume path), and the seeder's
# `--children-json` Jinja template must wrap the architect.output
# reference with an OUTER `(architect is defined and ...)` guard. An
# inner-only guard like `architect.output.children is defined` raises
# TemplateError on the `.output` access when architect itself is
# undefined (which is exactly the merged_unseeded case — architect
# never ran in the resume conductor process), causing the step to
# crash before seed-children is invoked. Without the outer guard the
# workflow short-circuits past seeder and the polyphony:planned tag is
# never set, so next-ready keeps reporting children_seeded:Needed and
# the closed-loop iterator can never make progress.
$detectorBlock = ''
$m = [regex]::Match($content, '(?s)- name: state_detector\b.*?(?=\n  - name: |\Z)')
if ($m.Success) { $detectorBlock = $m.Value }
if ($detectorBlock -notmatch "(?s)to:\s*seeder\s*\n\s*when:\s*[`"']\{\{\s*state_detector\.output\.state\s*==\s*'merged_unseeded'\s*\}\}") {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-merged-unseeded-seeder-route'
        Detail = "state_detector must route state == 'merged_unseeded' → seeder so the workflow invokes 'polyphony plan seed-children' on the resume path (closed-loop PR #8). Without this route the polyphony:planned tag is never set on the parent and the closed-loop iterator stalls."
    }
}

$seederBlock = ''
$m = [regex]::Match($content, '(?s)- name: seeder\b.*?(?=\n  - name: |\Z)')
if ($m.Success) { $seederBlock = $m.Value }
if ($seederBlock -match 'architect\.output\.children\s*\|\s*tojson') {
    if ($seederBlock -notmatch '\(\s*architect\s+is\s+defined\s+and\s+architect\.output\.children\s+is\s+defined\s*\)') {
        $violations += [PSCustomObject]@{
            Rule   = 'seeder-children-json-missing-outer-architect-guard'
            Detail = "seeder's --children-json template must wrap 'architect.output.children is defined' with the OUTER guard '(architect is defined and architect.output.children is defined)' so the merged_unseeded resume path renders cleanly to '[]' instead of raising TemplateError on the .output access of an undefined architect step (closed-loop PR #8 — fixes the resume gap that PR #210 partially addressed)."
        }
    }
}

# ── AB#3186 — Unattended cap_mode policy-router wiring ──────────────────
#
# Each cap-hit gate (depth_exceeded_gate, revise_cap_gate) must be
# preceded by a `<gate>_policy_router` script node that invokes
# resolve-unattended-cap-mode.ps1 and offers three routes:
#   auto_proceed → workflow-specific target (the "force one more / accept"
#                  semantic for this site; not checked here — site-specific)
#   auto_fail    → terminal_cap_auto_fail
#   (fallthrough)→ the cap-hit gate itself (manual + catch-all)
#
# These checks enumerate the concrete cap-hit gates known to this
# workflow rather than suffix-matching `*_cap_gate` so that an
# accidentally-deleted router (vs. a renamed/removed gate) shows up
# as a violation rather than vanishing silently.
foreach ($gate in @('depth_exceeded_gate', 'revise_cap_gate')) {
    $routerName = "${gate}_policy_router"
    if ($content -notmatch "name:\s*$([regex]::Escape($routerName))\b") {
        $violations += [PSCustomObject]@{
            Rule   = "missing-cap-mode-policy-router-$gate"
            Detail = "AB#3186: '$gate' must be preceded by a '$routerName' script node that calls resolve-unattended-cap-mode.ps1 and routes cap_mode=auto_proceed/auto_fail/manual."
        }
        continue
    }
    $routerBlock = ''
    $routerMatch = [regex]::Match($content, "(?s)- name:\s*$([regex]::Escape($routerName))\b.*?(?=\n  - name: |\Z)")
    if ($routerMatch.Success) { $routerBlock = $routerMatch.Value }
    if ($routerBlock -notmatch 'resolve-unattended-cap-mode\.ps1') {
        $violations += [PSCustomObject]@{
            Rule   = "cap-mode-router-wrong-helper-$gate"
            Detail = "AB#3186: '$routerName' must invoke the shared 'resolve-unattended-cap-mode.ps1' helper, not inline policy lookup."
        }
    }
    if ($routerBlock -notmatch 'to:\s*terminal_cap_auto_fail\b') {
        $violations += [PSCustomObject]@{
            Rule   = "cap-mode-router-missing-auto-fail-route-$gate"
            Detail = "AB#3186: '$routerName' must include a 'to: terminal_cap_auto_fail' route guarded by cap_mode == 'auto_fail'."
        }
    }
    if ($routerBlock -notmatch "to:\s*$([regex]::Escape($gate))\b") {
        $violations += [PSCustomObject]@{
            Rule   = "cap-mode-router-missing-manual-fallthrough-$gate"
            Detail = "AB#3186: '$routerName' must include a final unconditional 'to: $gate' route as the manual + catch-all fallthrough (per conductor-mechanics M4)."
        }
    }
}

if ($content -notmatch 'name:\s*terminal_cap_auto_fail\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-terminal-cap-auto-fail'
        Detail = "AB#3186: 'terminal_cap_auto_fail' terminal node missing. Required as the auto_fail target for cap-mode policy routers; must invoke abort-run.ps1 with -Reason 'cap-auto-fail'."
    }
} else {
    $terminalMatch = [regex]::Match($content, '(?s)- name:\s*terminal_cap_auto_fail\b.*?(?=\n  - name: |\Z)')
    if ($terminalMatch.Success -and $terminalMatch.Value -notmatch '"cap-auto-fail"') {
        $violations += [PSCustomObject]@{
            Rule   = 'terminal-cap-auto-fail-wrong-reason'
            Detail = "AB#3186: 'terminal_cap_auto_fail' must invoke abort-run.ps1 with -Reason 'cap-auto-fail' (the discriminator vs 'operator-abort' for post-mortem diagnostics)."
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
