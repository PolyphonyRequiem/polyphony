<#
.SYNOPSIS
    Strip polyphony's planning artifacts from the apex feature branch
    before the feature PR is opened against the target branch.

.DESCRIPTION
    Companion to .conductor/registry/workflows/apex-driver.yaml.

    The polyphony planning lifecycle writes two kinds of working files
    under `plans/` on each plannable item's plan branch (which all
    eventually land on the apex `feature/<root>` integration trunk via
    the plan PR cascade):

      * `plans/plan-<id>.children.json` — unconditionally vestigial; only
        consumed at plan-execution time by `polyphony plan seed-children`.
        Always stripped.
      * `plans/plan-<id>.md`            — the human-readable plan. May
        contain useful context for the PR reviewer, but is conceptually
        a *means*, not the deliverable. Default: strip. Future:
        opt-in keep when `policy.plan_retention` is wired (TBD).

    Files outside the polyphony naming convention (e.g.
    `plans/strategy.md` or `plans/team-notes.md` authored by humans)
    are PRESERVED — the glob is intentionally conservative
    (`plan-*.md` / `plan-*.children.json`).

    Idempotent: if no matching files exist (e.g. fast-pathed apex with
    no planning artifacts, or a remediation re-entry after the strip
    already ran), emits `already_clean: true` and skips the commit/push.

    Routing-style envelope — ALWAYS exits 0. Failures surface via
    `error_code` / `error_message`; the workflow gate routes on those
    fields, not on the exit code.

    Output JSON envelope:
        {
          success: <bool>,
          feature_branch: '<branch>',
          stripped_files: [<rel-path>, ...],
          stripped_count: <int>,
          commit_sha: '<sha>' | '',
          pushed: <bool>,
          already_clean: <bool>,
          error_code: '<code>' | '',
          error_message: '<msg>' | ''
        }

    Error codes:
      git_unavailable       — git not on PATH.
      branch_mismatch       — current branch is not the apex feature branch
                              and `git checkout` to it failed.
      rm_failed             — `git rm` of the matched files failed.
      commit_failed         — `git commit` failed.
      push_failed           — `git push origin <feature-branch>` failed
                              (local commit succeeded; envelope reports
                              the local SHA + stripped files so the
                              operator can recover by hand).
      unexpected_error      — uncaught exception in the script body.

.PARAMETER ApexId
    The apex root work item id. Used to derive the feature branch name
    when -FeatureBranch is omitted, and surfaced in the envelope for
    correlation with apex-driver events.

.PARAMETER FeatureBranch
    Override for the apex feature branch. Defaults to `feature/<ApexId>`
    per the branch-model spec.

.NOTES
    Companion to .conductor/registry/workflows/apex-driver.yaml. Inserted
    between `promote_feature_to_main` (the script that detects the
    feature branch is ahead of main) and `promote_feature_pr_dispatch`
    (the sub-workflow that actually opens the feature PR). Runs in the
    apex-driver's cwd, which is the feature-branch worktree spawned by
    the launcher.

    The plan-retention decision is hardcoded to "strip all polyphony
    planning files" today. Future work: gate the .md strip on
    `policy.plan_retention` so docs-deliverable / transparency runs can
    opt into keeping plans in the final PR. The `plans/plan-*.children.json`
    half is permanently vestigial and will always be stripped.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$ApexId,

    [string]$FeatureBranch = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($FeatureBranch)) {
    $FeatureBranch = "feature/$ApexId"
}

$envelope = [ordered]@{
    success         = $false
    feature_branch  = $FeatureBranch
    stripped_files  = @()
    stripped_count  = 0
    commit_sha      = ''
    pushed          = $false
    already_clean   = $false
    error_code      = ''
    error_message   = ''
}

function Write-Envelope($e) {
    $e | ConvertTo-Json -Compress -Depth 4
}

function Test-CommandAvailable([string]$name) {
    return [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

try {
    if (-not (Test-CommandAvailable 'git')) {
        $envelope.error_code = 'git_unavailable'
        $envelope.error_message = 'git is not available on PATH'
        Write-Envelope $envelope
        exit 0
    }

    # Defensive: make sure we're on the feature branch. The apex-driver's
    # cwd should already be the feature-branch worktree, but a resume or
    # an operator-initiated detached-HEAD state could leave us elsewhere.
    # If we're already on the right branch, this is a fast no-op; if
    # not, switch to it. Honour worktree cleanliness — if there are
    # unstaged changes (e.g. twig rewriting .twig/config mid-run), the
    # checkout will refuse and we route to the gate with branch_mismatch.
    $currentBranch = (& git rev-parse --abbrev-ref HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { $currentBranch = '' }
    $currentBranch = ($currentBranch | Out-String).Trim()

    if ($currentBranch -ne $FeatureBranch) {
        $coStderr = [System.IO.Path]::GetTempFileName()
        try {
            $null = & git checkout $FeatureBranch 2>$coStderr
            $coExit = $LASTEXITCODE
            $coErr = Get-Content -Raw $coStderr -ErrorAction SilentlyContinue
        }
        finally {
            Remove-Item $coStderr -ErrorAction SilentlyContinue
        }
        if ($coExit -ne 0) {
            $envelope.error_code = 'branch_mismatch'
            $envelope.error_message = "expected to be on $FeatureBranch, was on '$currentBranch'; checkout failed: $coErr"
            Write-Envelope $envelope
            exit 0
        }
    }

    # Discover candidate files. Conservative glob: only files matching
    # polyphony's own naming convention. Anything else in `plans/` is
    # operator-authored and preserved.
    $candidates = @()
    if (Test-Path 'plans') {
        $candidates = @(
            Get-ChildItem 'plans' -File -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.Name -like 'plan-*.md' -or $_.Name -like 'plan-*.children.json'
                } |
                ForEach-Object { "plans/$($_.Name)" }
        )
    }

    if ($candidates.Count -eq 0) {
        $envelope.success = $true
        $envelope.already_clean = $true
        Write-Envelope $envelope
        exit 0
    }

    # Strip via `git rm` so the commit is clean (no orphan working-tree
    # files). Skip files that aren't tracked (e.g. uncommitted), since
    # `git rm` would otherwise fail the whole batch.
    $tracked = @()
    foreach ($f in $candidates) {
        $null = & git ls-files --error-unmatch $f 2>$null
        if ($LASTEXITCODE -eq 0) { $tracked += $f }
    }

    if ($tracked.Count -eq 0) {
        # All matches were untracked; nothing to commit.
        $envelope.success = $true
        $envelope.already_clean = $true
        Write-Envelope $envelope
        exit 0
    }

    $rmStderr = [System.IO.Path]::GetTempFileName()
    try {
        $null = & git rm --quiet @tracked 2>$rmStderr
        $rmExit = $LASTEXITCODE
        $rmErr = Get-Content -Raw $rmStderr -ErrorAction SilentlyContinue
    }
    finally {
        Remove-Item $rmStderr -ErrorAction SilentlyContinue
    }
    if ($rmExit -ne 0) {
        $envelope.error_code = 'rm_failed'
        $envelope.error_message = "git rm failed: $rmErr"
        Write-Envelope $envelope
        exit 0
    }

    $msg = "chore: strip polyphony planning artifacts before feature PR ($($tracked.Count) file(s))"
    $commitStderr = [System.IO.Path]::GetTempFileName()
    try {
        $null = & git commit --quiet -m $msg 2>$commitStderr
        $commitExit = $LASTEXITCODE
        $commitErr = Get-Content -Raw $commitStderr -ErrorAction SilentlyContinue
    }
    finally {
        Remove-Item $commitStderr -ErrorAction SilentlyContinue
    }
    if ($commitExit -ne 0) {
        $envelope.error_code = 'commit_failed'
        $envelope.error_message = "git commit failed: $commitErr"
        Write-Envelope $envelope
        exit 0
    }

    $sha = (& git rev-parse HEAD 2>$null)
    $sha = ($sha | Out-String).Trim()

    $pushStderr = [System.IO.Path]::GetTempFileName()
    try {
        $null = & git push origin $FeatureBranch 2>$pushStderr
        $pushExit = $LASTEXITCODE
        $pushErr = Get-Content -Raw $pushStderr -ErrorAction SilentlyContinue
    }
    finally {
        Remove-Item $pushStderr -ErrorAction SilentlyContinue
    }
    if ($pushExit -ne 0) {
        # Local commit succeeded — record what was stripped so the
        # operator can recover (force-push or revert) deterministically.
        $envelope.error_code = 'push_failed'
        $envelope.error_message = "git push origin $FeatureBranch failed: $pushErr"
        $envelope.stripped_files = $tracked
        $envelope.stripped_count = $tracked.Count
        $envelope.commit_sha = $sha
        Write-Envelope $envelope
        exit 0
    }

    $envelope.success = $true
    $envelope.stripped_files = $tracked
    $envelope.stripped_count = $tracked.Count
    $envelope.commit_sha = $sha
    $envelope.pushed = $true
    Write-Envelope $envelope
    exit 0
}
catch {
    $envelope.error_code = 'unexpected_error'
    $envelope.error_message = $_.Exception.Message
    Write-Envelope $envelope
    exit 0
}
