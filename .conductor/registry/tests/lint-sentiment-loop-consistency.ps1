<#
.SYNOPSIS
    CI lint — assert structural alignment of the sentiment-driven PR review
    loop across plan-level.yaml, github-pr.yaml, and ado-pr.yaml.

.DESCRIPTION
    Three workflows carry near-identical sentiment-driven review loops:

      - plan-level.yaml      (plan PRs, github + ado legs)
      - github-pr.yaml       (implementation PRs on GitHub)
      - ado-pr.yaml          (implementation PRs on ADO)

    The loop shape is intentionally uniform: poll_status -> analyzer ->
    {merge | counter+gate -> fixer}. Without a lint asserting structural
    alignment, future edits to one workflow will silently diverge from
    the other two (issue #443).

    Checks enforced (failure exits 1):

      1. Each workflow declares `pr_feedback_analyzer` with the canonical
         contract:
            type: agent
            model: claude-sonnet-4.6
            tools: [filesystem]
            output keys (exact set, exact types):
              has_negative_feedback : boolean
              feedback_summary      : string
              feedback_digest       : string
              reasoning             : string

      2. Each workflow's `pr_feedback_analyzer` routes
         `has_negative_feedback == true` somewhere (the negative-feedback
         loop entry point). The exact next-node varies per workflow but
         the route must exist.

      3. Counter file naming uses the canonical per-workflow prefix:
            plan-level.yaml   : conductor-plan-revise-/pending-poll-
            github-pr.yaml    : conductor-github-pr-revise-/pending-poll-
            ado-pr.yaml       : conductor-ado-pr-revise-/pending-poll-
         Both `revise_counter` and `pending_poll_counter` scripts must
         appear in each workflow.

      4. github-pr.yaml and ado-pr.yaml each surface
         `pr_feedback_analyzer.output.feedback_summary` somewhere in
         their `workflow.output` block (so the parent workflow can read
         the last analyzer summary on terminal abort). plan-level is
         exempt — it has no parent that consumes that field.

      5. github-pr.yaml and ado-pr.yaml each declare
         `closed_unmerged_emitter` (script-type terminal emitter for
         abort_unmerged). plan-level is exempt — it uses a different
         shape (`closed_unmerged_gate` human gate) by design.

    Intentionally NOT checked (would false-positive):
      - The analyzer prompt body. plan-level's prompt has Jinja
        platform-conditional logic (ado vs github); ado-pr has +5/-5
        scoring; github-pr uses changes_requested terminology. These
        legitimately differ.
      - Gate option sets (continue/abort vs continue/abort/mark-merged-
        by-hand). ADO has the third option because of its PR-completed-
        elsewhere race; GitHub doesn't. Legitimately differs.

.PARAMETER WorkflowsDir
    Directory of workflow YAMLs to scan. Defaults to
    `<lint-dir>/../workflows`.

.PARAMETER Format
    Output format: `human` (default) or `github` (Actions annotations).

.OUTPUTS
    Per-check PASS / FAIL lines on stdout. Exit 0 if all invariants hold,
    1 otherwise.
#>
[CmdletBinding()]
param(
    [string]$WorkflowsDir = (Join-Path $PSScriptRoot '..' 'workflows'),
    [ValidateSet('human', 'github')]
    [string]$Format = 'human'
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $WorkflowsDir)) {
    Write-Host "SKIP: workflows dir not found: $WorkflowsDir" -ForegroundColor Yellow
    exit 0
}

# Canonical contract — single source of truth. If you intentionally bump
# any of these (e.g. model upgrade), update HERE and the lint will guide
# every workflow into alignment.
$CanonicalAnalyzer = @{
    Type   = 'agent'
    Model  = 'claude-sonnet-4.6'
    Tools  = @('filesystem')
    Output = [ordered]@{
        has_negative_feedback = 'boolean'
        feedback_summary      = 'string'
        feedback_digest       = 'string'
        reasoning             = 'string'
    }
}

# Per-workflow expectations (counter prefix, scope of secondary checks).
$WorkflowSpecs = @(
    @{
        File             = 'plan-level.yaml'
        CounterPrefix    = 'plan'
        RequireFeedbackInOutput = $false
        RequireClosedUnmergedEmitter = $false
    }
    @{
        File             = 'github-pr.yaml'
        CounterPrefix    = 'github-pr'
        RequireFeedbackInOutput = $true
        RequireClosedUnmergedEmitter = $true
    }
    @{
        File             = 'ado-pr.yaml'
        CounterPrefix    = 'ado-pr'
        RequireFeedbackInOutput = $true
        RequireClosedUnmergedEmitter = $true
    }
)

$violations = @()

# Extract the text of a `- name: <node>` block — from the line starting
# `- name: <node>` up to (but not including) the next sibling `- name:`
# line at the same indent (or EOF). Returns $null if not found.
function Get-NodeBlock {
    param(
        [string]$Content,
        [string]$NodeName
    )
    $lines = $Content -split "`n"
    $startIdx = -1
    $startIndent = -1
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -match "^(\s*)-\s+name:\s+$([regex]::Escape($NodeName))\s*$") {
            $startIdx = $i
            $startIndent = $matches[1].Length
            break
        }
    }
    if ($startIdx -lt 0) { return $null }

    $endIdx = $lines.Length - 1
    for ($i = $startIdx + 1; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -match "^(\s*)-\s+name:\s+") {
            if ($matches[1].Length -eq $startIndent) {
                $endIdx = $i - 1
                break
            }
        }
    }
    return ($lines[$startIdx..$endIdx] -join "`n")
}

foreach ($spec in $WorkflowSpecs) {
    $path = Join-Path $WorkflowsDir $spec.File
    if (-not (Test-Path $path)) {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-workflow'
            File   = $spec.File
            Detail = "Workflow YAML not found at $path"
        }
        continue
    }

    $content = Get-Content $path -Raw

    # ── Check 1: pr_feedback_analyzer contract ───────────────────────────
    $analyzer = Get-NodeBlock -Content $content -NodeName 'pr_feedback_analyzer'
    if ($null -eq $analyzer) {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-analyzer'
            File   = $spec.File
            Detail = "No `pr_feedback_analyzer` agent block found"
        }
    } else {
        if ($analyzer -notmatch "(?m)^\s*type:\s*$($CanonicalAnalyzer.Type)\s*$") {
            $violations += [PSCustomObject]@{
                Rule   = 'analyzer-wrong-type'
                File   = $spec.File
                Detail = "pr_feedback_analyzer.type != '$($CanonicalAnalyzer.Type)'"
            }
        }
        if ($analyzer -notmatch "(?m)^\s*model:\s*$([regex]::Escape($CanonicalAnalyzer.Model))\s*$") {
            $violations += [PSCustomObject]@{
                Rule   = 'analyzer-wrong-model'
                File   = $spec.File
                Detail = "pr_feedback_analyzer.model != '$($CanonicalAnalyzer.Model)' (sentiment loop must use one model across all 3 workflows for cost + behavior parity)"
            }
        }
        foreach ($tool in $CanonicalAnalyzer.Tools) {
            if ($analyzer -notmatch "(?m)^\s*-\s*$([regex]::Escape($tool))\s*$") {
                $violations += [PSCustomObject]@{
                    Rule   = 'analyzer-missing-tool'
                    File   = $spec.File
                    Detail = "pr_feedback_analyzer.tools does not include '$tool'"
                }
            }
        }
        foreach ($key in $CanonicalAnalyzer.Output.Keys) {
            $expectedType = $CanonicalAnalyzer.Output[$key]
            # Match `<key>:` then within its sub-block find `type: <expected>`.
            # The analyzer block is extracted with absolute indentation preserved.
            # An agent at top-level (`  - name: ...`) puts its body at 4-space indent,
            # `output:` keys at 6-space indent, and `type:` at 8-space indent.
            $keyPattern = "(?ms)^\s{6}$([regex]::Escape($key)):\s*$.*?^\s{8}type:\s*$([regex]::Escape($expectedType))\s*$"
            if ($analyzer -notmatch $keyPattern) {
                $violations += [PSCustomObject]@{
                    Rule   = 'analyzer-output-drift'
                    File   = $spec.File
                    Detail = "pr_feedback_analyzer.output.$key missing or not declared as type '$expectedType'"
                }
            }
        }

        # ── Check 2: has_negative_feedback == true route exists ──────────
        # The exact target varies (approvals_policy, revise_counter, etc.)
        # but the route must exist to drive the negative-feedback loop.
        if ($analyzer -notmatch "(?ms)^\s*-\s*to:\s*\S+\s*$\s+when:\s*[`"']\{\{\s*pr_feedback_analyzer\.output\.has_negative_feedback\s*==\s*true\s*\}\}[`"']") {
            $violations += [PSCustomObject]@{
                Rule   = 'analyzer-missing-negative-route'
                File   = $spec.File
                Detail = "pr_feedback_analyzer has no route guarded by `pr_feedback_analyzer.output.has_negative_feedback == true` — sentiment-loop negative branch is unwired"
            }
        }
    }

    # ── Check 3: counter file naming ─────────────────────────────────────
    $reviseRegex   = "conductor-$([regex]::Escape($spec.CounterPrefix))-revise-\{\{\s*workflow\.input\.work_item_id"
    $pendingRegex  = "conductor-$([regex]::Escape($spec.CounterPrefix))-pending-poll-\{\{\s*workflow\.input\.work_item_id"
    if ($content -notmatch $reviseRegex) {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-revise-counter-key'
            File   = $spec.File
            Detail = "No `conductor-$($spec.CounterPrefix)-revise-{{ workflow.input.work_item_id }}` counter-file path found"
        }
    }
    if ($content -notmatch $pendingRegex) {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-pending-poll-counter-key'
            File   = $spec.File
            Detail = "No `conductor-$($spec.CounterPrefix)-pending-poll-{{ workflow.input.work_item_id }}` counter-file path found"
        }
    }
    if ($content -notmatch '(?m)^\s*-\s*name:\s*revise_counter\s*$') {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-revise-counter-node'
            File   = $spec.File
            Detail = "No `revise_counter` script node found"
        }
    }
    if ($content -notmatch '(?m)^\s*-\s*name:\s*pending_poll_counter\s*$') {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-pending-poll-counter-node'
            File   = $spec.File
            Detail = "No `pending_poll_counter` script node found"
        }
    }

    # ── Check 4: feedback_summary surfaced in workflow.output ────────────
    if ($spec.RequireFeedbackInOutput) {
        if ($content -notmatch 'pr_feedback_analyzer\.output\.feedback_summary') {
            $violations += [PSCustomObject]@{
                Rule   = 'missing-feedback-summary-output'
                File   = $spec.File
                Detail = "workflow.output does not reference `pr_feedback_analyzer.output.feedback_summary` — parent workflow cannot read the last analyzer summary on terminal abort"
            }
        }
    }

    # ── Check 5: closed_unmerged_emitter present ─────────────────────────
    if ($spec.RequireClosedUnmergedEmitter) {
        if ($content -notmatch '(?m)^\s*-\s*name:\s*closed_unmerged_emitter\s*$') {
            $violations += [PSCustomObject]@{
                Rule   = 'missing-closed-unmerged-emitter'
                File   = $spec.File
                Detail = "No `closed_unmerged_emitter` terminal node found (required for abort_unmerged routing)"
            }
        }
    }
}

if ($violations.Count -gt 0) {
    foreach ($v in $violations) {
        if ($Format -eq 'github') {
            $msg = "$($v.Rule) [$($v.File)] — $($v.Detail)"
            Write-Host "::error file=.conductor/registry/workflows/$($v.File)::$msg"
        } else {
            Write-Host "FAIL [$($v.File)] $($v.Rule): $($v.Detail)" -ForegroundColor Red
        }
    }
    Write-Host ''
    Write-Host "FAIL: $($violations.Count) sentiment-loop consistency violation(s) across plan-level/github-pr/ado-pr" -ForegroundColor Red
    exit 1
}

Write-Host "PASS: sentiment-loop structurally aligned across plan-level.yaml, github-pr.yaml, and ado-pr.yaml" -ForegroundColor Green
exit 0
