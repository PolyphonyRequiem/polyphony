<#
.SYNOPSIS
    Integrate any drift on `origin/<target>` into the feature branch
    before a feature PR is opened — fixes AB#3238 (un-rebased feature
    PRs include drift as false-positive review feedback).

.DESCRIPTION
    Companion to .conductor/registry/workflows/feature-pr.yaml.

    A feature branch that's been alive for any nontrivial duration
    accumulates "drift" from its target as the target advances. Without
    integrating that drift before opening the feature PR, the PR diff
    will appear to include / remove changes that landed on the target
    while the feature branch was alive — fooling LLM reviewers into
    flagging unrelated files (and, with AB#3236's revise_counter loop,
    burning unbounded LLM tokens on phantom fixes).

    This script runs at the entry of feature-pr.yaml, immediately
    before the platform-specific PR creator. It:

      1. Fetches `origin/<target>` and `origin/<feature>`.
      2. Switches to <feature> if not already there.
      3. Refuses to proceed if local <feature> has diverged from
         `origin/<feature>` (both sides have unique commits — that's
         an out-of-band write situation that should be inspected).
      4. Computes `behind_by` from `origin/<target>...HEAD`.
      5. If behind_by == 0, exits clean (already in sync).
      6. Otherwise, `git merge --no-ff origin/<target>` and pushes.
         On conflict: safely aborts the merge (only if MERGE_HEAD is
         present), captures the conflicted file list, and surfaces
         `merge_conflict` for the gate to handle.

    Merge (not rebase) is intentional:
      * Preserves SHAs of upstream MG → feature merge commits.
      * Avoids `--force-with-lease` (vanilla push works after a merge
        commit is layered on top — fast-forward against origin/feature).
      * Makes the drift-integration commit obvious in PR review.

    Routing-style envelope — ALWAYS exits 0. Failures surface via
    `error_code` / `error_message`; the workflow gate routes on those
    fields, not on the exit code.

    Output JSON envelope:
        {
          success:           <bool>,
          feature_branch:    '<branch>',
          target_branch:     '<branch>',
          behind_by:         <int>,
          ahead_by:          <int>,
          drift_integrated:  <bool>,
          merge_commit_sha:  '<sha>' | '',
          conflicted_files:  [<rel-path>, ...],
          error_code:        '<code>' | '',
          error_message:     '<msg>' | ''
        }

    Error codes:
      git_unavailable          — git not on PATH.
      fetch_failed             — `git fetch origin <target>` (or <feature>) failed.
      branch_mismatch          — current branch is not <feature> and checkout failed.
      feature_branch_diverged  — local <feature> and origin/<feature> have
                                 unique commits each — refuse to proceed.
      merge_conflict           — `git merge --no-ff origin/<target>` hit
                                 conflicts; merge aborted; conflicted_files
                                 lists the affected paths.
      push_failed              — drift was integrated locally but
                                 `git push origin <feature>` failed.
      unexpected_error         — uncaught exception in the script body.

.PARAMETER FeatureBranch
    The feature branch being prepared for PR. Required.

.PARAMETER TargetBranch
    The target branch (typically `main` for apex feature PRs, or the
    parent feature branch for child item feature PRs). Required.

.NOTES
    Companion to feature-pr.yaml. Inserted as the new entry point of
    that workflow — runs before pr_platform_router so drift is
    integrated regardless of which platform leg (github / ado) we
    take next.

    Scope: covers both apex-driver.yaml's apex→main promotion and
    apex-item-dispatch.yaml's child→feature/<apex> promotion (both
    flow through feature-pr.yaml).

    Local cwd is the feature-branch worktree spawned by the launcher.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FeatureBranch,

    [Parameter(Mandatory)]
    [string]$TargetBranch
)

$ErrorActionPreference = 'Stop'

$envelope = [ordered]@{
    success           = $false
    feature_branch    = $FeatureBranch
    target_branch     = $TargetBranch
    behind_by         = 0
    ahead_by          = 0
    drift_integrated  = $false
    merge_commit_sha  = ''
    conflicted_files  = @()
    error_code        = ''
    error_message     = ''
}

function Write-Envelope($e) {
    $e | ConvertTo-Json -Compress -Depth 4
}

function Test-CommandAvailable([string]$name) {
    return [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments)] [string[]]$Args)
    $stderrFile = [System.IO.Path]::GetTempFileName()
    try {
        $stdout = & git @Args 2>$stderrFile
        $exit = $LASTEXITCODE
        $stderr = Get-Content -Raw $stderrFile -ErrorAction SilentlyContinue
        $stderrTrim = if ($null -eq $stderr) { '' } else { $stderr.Trim() }
        [pscustomobject]@{
            ExitCode = $exit
            Stdout   = ($stdout | Out-String).Trim()
            Stderr   = $stderrTrim
        }
    }
    finally {
        Remove-Item $stderrFile -ErrorAction SilentlyContinue
    }
}

function Test-MergeInProgress {
    $r = Invoke-Git 'rev-parse' '-q' '--verify' 'MERGE_HEAD'
    return ($r.ExitCode -eq 0)
}

try {
    if (-not (Test-CommandAvailable 'git')) {
        $envelope.error_code = 'git_unavailable'
        $envelope.error_message = 'git is not available on PATH'
        Write-Envelope $envelope
        exit 0
    }

    $fetchTarget = Invoke-Git 'fetch' 'origin' $TargetBranch
    if ($fetchTarget.ExitCode -ne 0) {
        $envelope.error_code = 'fetch_failed'
        $envelope.error_message = "git fetch origin $TargetBranch failed: $($fetchTarget.Stderr)"
        Write-Envelope $envelope
        exit 0
    }

    $fetchFeature = Invoke-Git 'fetch' 'origin' $FeatureBranch
    if ($fetchFeature.ExitCode -ne 0) {
        # Feature branch may not exist on origin yet (very first push from
        # a fresh apex). Treat as non-fatal and proceed; the divergence
        # check below will handle the "no origin/<feature>" case.
        $envelope.error_message = "non-fatal: git fetch origin $FeatureBranch failed: $($fetchFeature.Stderr)"
    }

    $current = Invoke-Git 'rev-parse' '--abbrev-ref' 'HEAD'
    $currentBranch = $current.Stdout
    if ($currentBranch -ne $FeatureBranch) {
        $co = Invoke-Git 'checkout' $FeatureBranch
        if ($co.ExitCode -ne 0) {
            $envelope.error_code = 'branch_mismatch'
            $envelope.error_message = "expected to be on $FeatureBranch, was on '$currentBranch'; checkout failed: $($co.Stderr)"
            Write-Envelope $envelope
            exit 0
        }
    }

    # Local-vs-origin divergence check on the feature branch.
    # `git rev-list --left-right --count origin/<feature>...HEAD` returns
    # "<behind>\t<ahead>". If both > 0, local and origin have unique
    # commits each — refuse to merge target into a divergent view.
    $originFeatureExists = (Invoke-Git 'rev-parse' '--verify' "refs/remotes/origin/$FeatureBranch").ExitCode -eq 0
    if ($originFeatureExists) {
        $cmp = Invoke-Git 'rev-list' '--left-right' '--count' "origin/$FeatureBranch...HEAD"
        if ($cmp.ExitCode -eq 0 -and $cmp.Stdout -match '^(\d+)\s+(\d+)$') {
            $behindFeature = [int]$Matches[1]
            $aheadFeature  = [int]$Matches[2]
            if ($behindFeature -gt 0 -and $aheadFeature -gt 0) {
                $envelope.error_code = 'feature_branch_diverged'
                $envelope.error_message = "local $FeatureBranch has diverged from origin/$FeatureBranch (local ahead by $aheadFeature, behind by $behindFeature)"
                Write-Envelope $envelope
                exit 0
            }
        }
    }

    $cmp2 = Invoke-Git 'rev-list' '--left-right' '--count' "origin/$TargetBranch...HEAD"
    if ($cmp2.ExitCode -eq 0 -and $cmp2.Stdout -match '^(\d+)\s+(\d+)$') {
        $envelope.behind_by = [int]$Matches[1]
        $envelope.ahead_by  = [int]$Matches[2]
    }

    if ($envelope.behind_by -eq 0) {
        $envelope.success = $true
        $envelope.drift_integrated = $false
        $envelope.error_message = ''  # clear non-fatal fetch warning if no drift
        Write-Envelope $envelope
        exit 0
    }

    $mergeMsg = "chore: integrate origin/$TargetBranch drift into $FeatureBranch ($($envelope.behind_by) commit(s)) [AB#3238]"
    $merge = Invoke-Git 'merge' '--no-ff' '-m' $mergeMsg "origin/$TargetBranch"
    if ($merge.ExitCode -ne 0) {
        $conflicted = @()
        if (Test-MergeInProgress) {
            $unmerged = Invoke-Git 'diff' '--name-only' '--diff-filter=U'
            if ($unmerged.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($unmerged.Stdout)) {
                $conflicted = @($unmerged.Stdout -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            }
            $abort = Invoke-Git 'merge' '--abort'
            if ($abort.ExitCode -ne 0) {
                $envelope.error_code = 'merge_conflict'
                $envelope.error_message = "merge of origin/$TargetBranch into $FeatureBranch failed ($($merge.Stderr)) AND `git merge --abort` failed ($($abort.Stderr)); worktree may be left in a conflicted state"
                $envelope.conflicted_files = $conflicted
                Write-Envelope $envelope
                exit 0
            }
        }
        $envelope.error_code = 'merge_conflict'
        $envelope.error_message = "merge of origin/$TargetBranch into $FeatureBranch hit conflicts; merge aborted cleanly: $($merge.Stderr)"
        $envelope.conflicted_files = $conflicted
        Write-Envelope $envelope
        exit 0
    }

    $sha = (Invoke-Git 'rev-parse' 'HEAD').Stdout
    $envelope.merge_commit_sha = $sha

    $push = Invoke-Git 'push' 'origin' $FeatureBranch
    if ($push.ExitCode -ne 0) {
        $envelope.error_code = 'push_failed'
        $envelope.error_message = "git push origin $FeatureBranch failed: $($push.Stderr)"
        Write-Envelope $envelope
        exit 0
    }

    $envelope.success = $true
    $envelope.drift_integrated = $true
    $envelope.error_message = ''
    Write-Envelope $envelope
    exit 0
}
catch {
    $envelope.error_code = 'unexpected_error'
    $envelope.error_message = $_.Exception.Message
    Write-Envelope $envelope
    exit 0
}
