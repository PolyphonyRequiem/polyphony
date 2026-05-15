<#
.SYNOPSIS
    CI lint — `primary_completer` in implement-merge-group.yaml MUST be
    reachable only via the squash-coverage trust chain (AB#3214).

.DESCRIPTION
    Encodes the AB#3214 trust-chain invariant as a structural lint.

    Background. AB#3175 incident (2026-05-14, apex run 3165): the
    `primary_completer` step transitioned a child task to its template's
    done state in ADO while the implementing commit was stranded on a
    sibling task's impl branch and the squash-merge to the MG carried
    zero diff. Net result: ADO reported the work as Done while
    `feature/{root}` lacked any of its commits. The scope reviewer
    caught the discrepancy four cycles later.

    PRs #401 (AB#3210, impl-branch routing assertion) and #402 (AB#3211,
    `pr assert-impl-pr-coverage`) plug the upstream root causes. After
    they ship, the AB#3175 failure mode is structurally impossible —
    PROVIDED `primary_completer` is reachable ONLY via the
    coverage-asserted path. AB#3214 tracks the contract: prove that
    invariant structurally, so future routing edits cannot reintroduce
    a path that bypasses coverage.

    Invariants enforced by this lint (against
    `.conductor/registry/workflows/implement-merge-group.yaml`):

      I1. Every route whose `to:` is `primary_completer` must originate
          from a step named `delete_impl_branch`.

      I2. Every route whose `to:` is `delete_impl_branch` must
          originate from one of the two coverage-trust steps:
            - `assert_impl_pr_coverage` (the verb-level assertion), or
            - `squash_coverage_mismatch_gate` (the human-gate
              `force_accept` route, which is a deliberate operator
              override after manual inspection).

    These two invariants together guarantee that `primary_completer`
    fires only after `assert_impl_pr_coverage` returned `ok` (or the
    operator explicitly acknowledged a mismatch), making the AB#3175
    green-wash structurally impossible.

.PARAMETER WorkflowPath
    Path to the workflow YAML to scan. Defaults to the canonical
    `.conductor/registry/workflows/implement-merge-group.yaml` under
    the repo root.

.PARAMETER RepoRoot
    Repository root. Default: parent of this script's directory.

.PARAMETER Format
    `plain` (default) — human-readable. `github` — `::error` annotations
    consumed by GitHub Actions.

.OUTPUTS
    Exit 0 — invariants hold.
    Exit 1 — at least one violation. Prints the offending step and
             remediation guidance.
    Exit 2 — configuration error (missing workflow file, parse failure).
#>

[CmdletBinding()]
param(
    [string] $WorkflowPath,
    [string] $RepoRoot,
    [ValidateSet('plain', 'github')]
    [string] $Format = 'plain'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

if (-not $WorkflowPath) {
    $WorkflowPath = Join-Path $RepoRoot '.conductor/registry/workflows/implement-merge-group.yaml'
}

if (-not (Test-Path -LiteralPath $WorkflowPath)) {
    Write-Error "Workflow YAML not found: $WorkflowPath"
    exit 2
}

if (-not (Get-Module -ListAvailable -Name 'powershell-yaml')) {
    Write-Error "FATAL: the powershell-yaml module is required by lint-primary-completer-trust-chain.ps1.`nInstall with: Install-Module -Name powershell-yaml -Force -SkipPublisherCheck -Scope CurrentUser"
    exit 2
}

Import-Module powershell-yaml -ErrorAction Stop

try {
    $yaml = Get-Content -LiteralPath $WorkflowPath -Raw | ConvertFrom-Yaml
} catch {
    Write-Error "Failed to parse $WorkflowPath as YAML: $_"
    exit 2
}

$agents = $yaml.agents
if (-not $agents) {
    Write-Error "Workflow $WorkflowPath does not declare an 'agents:' block."
    exit 2
}

# Gather: for each step name S, the set of step names that route TO S.
$predecessors = @{}
foreach ($agent in $agents) {
    if (-not $agent.routes) { continue }
    foreach ($route in $agent.routes) {
        $target = $route.to
        if (-not $target) { continue }
        if (-not $predecessors.ContainsKey($target)) {
            $predecessors[$target] = New-Object 'System.Collections.Generic.HashSet[string]'
        }
        [void]$predecessors[$target].Add($agent.name)
    }
}

# Also gather human_gate option routes.
foreach ($agent in $agents) {
    if (-not $agent.options) { continue }
    foreach ($opt in $agent.options) {
        $target = $opt.route
        if (-not $target) { continue }
        if (-not $predecessors.ContainsKey($target)) {
            $predecessors[$target] = New-Object 'System.Collections.Generic.HashSet[string]'
        }
        [void]$predecessors[$target].Add($agent.name)
    }
}

$violations = New-Object System.Collections.Generic.List[hashtable]

# I1: primary_completer's only predecessor must be delete_impl_branch.
$expectedPrimaryCompleterPreds = @('delete_impl_branch')
$actualPrimaryCompleterPreds = @()
if ($predecessors.ContainsKey('primary_completer')) {
    $actualPrimaryCompleterPreds = @($predecessors['primary_completer']) | Sort-Object
}
$unexpectedPCP = $actualPrimaryCompleterPreds | Where-Object { $_ -notin $expectedPrimaryCompleterPreds }
$missingPCP = $expectedPrimaryCompleterPreds | Where-Object { $_ -notin $actualPrimaryCompleterPreds }
if ($unexpectedPCP -or $missingPCP) {
    $violations.Add(@{
        invariant = 'I1'
        step = 'primary_completer'
        actual = $actualPrimaryCompleterPreds
        expected = $expectedPrimaryCompleterPreds
        unexpected = $unexpectedPCP
        missing = $missingPCP
    })
}

# I2: delete_impl_branch's predecessors must be exactly the two coverage-trust steps.
$expectedDIBPreds = @('assert_impl_pr_coverage', 'squash_coverage_mismatch_gate') | Sort-Object
$actualDIBPreds = @()
if ($predecessors.ContainsKey('delete_impl_branch')) {
    $actualDIBPreds = @($predecessors['delete_impl_branch']) | Sort-Object
}
$unexpectedDIB = $actualDIBPreds | Where-Object { $_ -notin $expectedDIBPreds }
$missingDIB = $expectedDIBPreds | Where-Object { $_ -notin $actualDIBPreds }
if ($unexpectedDIB -or $missingDIB) {
    $violations.Add(@{
        invariant = 'I2'
        step = 'delete_impl_branch'
        actual = $actualDIBPreds
        expected = $expectedDIBPreds
        unexpected = $unexpectedDIB
        missing = $missingDIB
    })
}

if ($violations.Count -eq 0) {
    Write-Host "PASS: primary_completer trust chain is intact ($WorkflowPath)" -ForegroundColor Green
    exit 0
}

foreach ($v in $violations) {
    $msg = "[$($v.invariant)] $($v.step): expected predecessors {$($v.expected -join ', ')}; actual {$(($v.actual -join ', ') | ForEach-Object { if ($_) { $_ } else { '(none)' } })}"
    if ($v.unexpected) { $msg += "; unexpected predecessor(s): $($v.unexpected -join ', ')" }
    if ($v.missing) { $msg += "; missing predecessor(s): $($v.missing -join ', ')" }
    if ($Format -eq 'github') {
        Write-Host "::error file=$WorkflowPath::$msg"
    } else {
        Write-Host "FAIL: $msg" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Fix: route into primary_completer must come only from delete_impl_branch," -ForegroundColor Yellow
Write-Host "and route into delete_impl_branch must come only from assert_impl_pr_coverage" -ForegroundColor Yellow
Write-Host "(action='ok') or squash_coverage_mismatch_gate (force_accept). See AB#3214" -ForegroundColor Yellow
Write-Host "and the trust-chain comment block above primary_completer in the workflow." -ForegroundColor Yellow
exit 1
