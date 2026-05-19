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
      6. Otherwise, `git rebase origin/<target>` and force-pushes
         with `--force-with-lease=<feature>:<expected-sha>` (safe
         force: refuses if origin advanced since fetch).
         On conflict: safely aborts the rebase (only if a rebase is
         in progress), captures the conflicted file list, and surfaces
         `merge_conflict` for the gate to handle.

    Rebase (not merge) is intentional:
      * Feature PR shows clean linear history — no extra integration
        merge commit polluting the PR commits view.
      * Reviewer sees only the work the feature actually did, with
        upstream drift folded in as ancestor commits (not as a sibling
        merge).
      * Force-with-lease is safe because polyphony's SDLC run is the
        sole writer of the feature branch within a run; the
        local-vs-origin divergence gate (below) guarantees we never
        force-push over remote-only commits we don't know about.

    Routing-style envelope — ALWAYS exits 0. Failures surface via
    `error_code` / `error_message`; the workflow gate routes on those
    fields, not on the exit code.

    Output JSON envelope:
        {
          success:           <bool>,
          strategy:          'rebase',
          feature_branch:    '<branch>',
          target_branch:     '<branch>',
          behind_by:         <int>,
          ahead_by:          <int>,
          drift_integrated:  <bool>,
          old_head_sha:      '<sha>' | '',
          new_head_sha:      '<sha>' | '',
          conflicted_files:  [<rel-path>, ...],
          error_code:        '<code>' | '',
          error_message:     '<msg>' | ''
        }

    Error codes:
      git_unavailable          — git not on PATH.
      worktree_dirty           — uncommitted changes / in-progress merge
                                 or rebase / cherry-pick / revert detected
                                 before we touch anything.
      fetch_failed             — `git fetch origin <target>` (or <feature>) failed.
      branch_mismatch          — current branch is not <feature> and checkout failed.
      feature_branch_diverged  — local <feature> and origin/<feature> have
                                 unique commits each — refuse to proceed.
      merge_conflict           — `git rebase origin/<target>` hit
                                 conflicts; rebase aborted; conflicted_files
                                 lists the affected paths. (Name retained
                                 for workflow-route compatibility with the
                                 merge-strategy version — the semantic is
                                 still "drift integration conflicted".)
      push_failed              — drift was integrated locally but
                                 `git push --force-with-lease ...` failed
                                 (typically: origin advanced since fetch).
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
    strategy          = 'rebase'
    feature_branch    = $FeatureBranch
    target_branch     = $TargetBranch
    behind_by         = 0
    ahead_by          = 0
    drift_integrated  = $false
    old_head_sha      = ''
    new_head_sha      = ''
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

function Test-RebaseInProgress {
    # `git rev-parse --git-path <name>` echoes the resolved path even if
    # the path does not exist. Existence on disk is the actual signal.
    $rm = (Invoke-Git 'rev-parse' '--git-path' 'rebase-merge').Stdout
    $ra = (Invoke-Git 'rev-parse' '--git-path' 'rebase-apply').Stdout
    return ((-not [string]::IsNullOrWhiteSpace($rm) -and (Test-Path $rm)) -or
            (-not [string]::IsNullOrWhiteSpace($ra) -and (Test-Path $ra)))
}

function Test-WorktreeBlocked {
    # Returns ($true, '<reason>') if any pre-existing in-progress git
    # operation or uncommitted change should block us from touching the
    # branch. Returns ($false, '') if safe to proceed.
    if (Test-RebaseInProgress) {
        return @($true, 'a rebase is already in progress')
    }
    $r = Invoke-Git 'rev-parse' '-q' '--verify' 'MERGE_HEAD'
    if ($r.ExitCode -eq 0) {
        return @($true, 'a merge is already in progress')
    }
    $r = Invoke-Git 'rev-parse' '-q' '--verify' 'CHERRY_PICK_HEAD'
    if ($r.ExitCode -eq 0) {
        return @($true, 'a cherry-pick is already in progress')
    }
    $r = Invoke-Git 'rev-parse' '-q' '--verify' 'REVERT_HEAD'
    if ($r.ExitCode -eq 0) {
        return @($true, 'a revert is already in progress')
    }
    $status = Invoke-Git 'status' '--porcelain'
    if ($status.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($status.Stdout)) {
        return @($true, "worktree has uncommitted changes:`n$($status.Stdout)")
    }
    return @($false, '')
}

try {
    if (-not (Test-CommandAvailable 'git')) {
        $envelope.error_code = 'git_unavailable'
        $envelope.error_message = 'git is not available on PATH'
        Write-Envelope $envelope
        exit 0
    }

    $blockedResult = Test-WorktreeBlocked
    if ($blockedResult[0]) {
        $envelope.error_code = 'worktree_dirty'
        $envelope.error_message = "refusing to integrate drift: $($blockedResult[1])"
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

    $oldHead = (Invoke-Git 'rev-parse' 'HEAD').Stdout
    $envelope.old_head_sha = $oldHead

    # Capture the expected origin/<feature> SHA BEFORE rebase, to use as
    # the explicit lease in --force-with-lease. This guards against
    # background fetches that could otherwise weaken the lease.
    $expectedRemote = ''
    if ($originFeatureExists) {
        $expectedRemote = (Invoke-Git 'rev-parse' "refs/remotes/origin/$FeatureBranch").Stdout
    }

    $rebase = Invoke-Git 'rebase' "origin/$TargetBranch"
    if ($rebase.ExitCode -ne 0) {
        $conflicted = @()
        if (Test-RebaseInProgress) {
            $unmerged = Invoke-Git 'diff' '--name-only' '--diff-filter=U'
            if ($unmerged.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($unmerged.Stdout)) {
                $conflicted = @($unmerged.Stdout -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            }
            $abort = Invoke-Git 'rebase' '--abort'
            if ($abort.ExitCode -ne 0) {
                $envelope.error_code = 'merge_conflict'
                $envelope.error_message = "rebase of HEAD onto origin/$TargetBranch failed ($($rebase.Stderr)) AND ``git rebase --abort`` failed ($($abort.Stderr)); worktree may be left in a conflicted state"
                $envelope.conflicted_files = $conflicted
                Write-Envelope $envelope
                exit 0
            }
        }
        $envelope.error_code = 'merge_conflict'
        $envelope.error_message = "rebase of HEAD onto origin/$TargetBranch hit conflicts; rebase aborted cleanly: $($rebase.Stderr)"
        $envelope.conflicted_files = $conflicted
        Write-Envelope $envelope
        exit 0
    }

    $newHead = (Invoke-Git 'rev-parse' 'HEAD').Stdout
    $envelope.new_head_sha = $newHead

    # Edge case: rebase ran but HEAD didn't move (target was already an
    # ancestor of HEAD, no commits to replay). Behind_by said otherwise,
    # but git can still no-op in odd-history scenarios. Treat as success
    # and skip the force-push.
    if ($newHead -eq $oldHead) {
        $envelope.success = $true
        $envelope.drift_integrated = $false
        $envelope.error_message = ''
        Write-Envelope $envelope
        exit 0
    }

    # Force-push with explicit lease (origin/<feature> must match the SHA
    # we observed at fetch time). If the feature branch does not exist on
    # origin yet, a plain `git push -u` is appropriate (no lease needed —
    # nothing to overwrite).
    if ([string]::IsNullOrWhiteSpace($expectedRemote)) {
        $push = Invoke-Git 'push' '--set-upstream' 'origin' $FeatureBranch
    }
    else {
        $leaseArg = "--force-with-lease=refs/heads/${FeatureBranch}:$expectedRemote"
        $push = Invoke-Git 'push' $leaseArg 'origin' "HEAD:refs/heads/$FeatureBranch"
    }
    if ($push.ExitCode -ne 0) {
        $envelope.error_code = 'push_failed'
        $envelope.error_message = "git push --force-with-lease ... origin $FeatureBranch failed (most likely: origin advanced since fetch): $($push.Stderr)"
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
