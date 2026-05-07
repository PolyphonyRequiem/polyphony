<#
.SYNOPSIS
    Spawn or tear down a per-item git worktree for the apex-driver dispatch loop.

.DESCRIPTION
    Companion to .conductor/registry/workflows/apex-driver.yaml.

    The apex-driver fans work-items out across waves and dispatches
    each item into a per-item git worktree so multiple lifecycle
    sub-workflows (plan-level, actionable, implement-pg, feature-pr)
    can run in parallel without racing branch checkouts in the
    primary working tree.

    Operations:
      spawn    — Creates a new worktree at <root>-item-<work_item_id>
                 (relative to the current repo root), checked out on a
                 fresh branch sdlc/apex/<work_item_id> branched from
                 the supplied -BaseBranch (default 'origin/main').
                 The branch is created with `git worktree add -b`.
                 If the worktree directory already exists, the
                 operation is a no-op (idempotent re-entry on resume).

      teardown — Removes the worktree directory and prunes the entry
                 from the parent repo. Forced (--force) so a
                 partial/dirty checkout still gets cleaned up — apex
                 owns the worktree lifecycle and a leftover dirty tree
                 would block the next dispatch.

    Per the polyphony-workflow-author skill conventions:
      * ALWAYS exits 0 (routing-style envelope).
      * Failures populate `success=false` and `error_code` so the
        workflow's wave_failed_gate can surface them via the human
        gate without halting the conductor run.

    Output JSON envelope:
        {
          success: <bool>,
          operation: 'spawn' | 'teardown',
          work_item_id: <int>,
          worktree_path: '<absolute-path>',
          branch: '<branch-name>',
          error_code: '<code>' | '',
          error_message: '<msg>' | ''
        }

    Error codes:
      git_unavailable         — git not on PATH.
      worktree_add_failed     — `git worktree add` returned non-zero.
      worktree_remove_failed  — `git worktree remove` returned non-zero.
      invalid_operation       — Operation not one of spawn|teardown.
      missing_base_branch     — spawn called without -BaseBranch and no remote default.

.PARAMETER Operation
    One of `spawn` or `teardown`. Required.

.PARAMETER WorkItemId
    ADO work item id of the lifecycle item the worktree is for.
    Used to derive the worktree directory name and branch name.

.PARAMETER BaseBranch
    For spawn only: the branch the new sdlc/apex/<id> branch is forked
    from. Defaults to `origin/main`.

.PARAMETER WorktreeRoot
    For both operations: the parent directory under which per-item
    worktrees are placed. Defaults to the parent of the current
    repo root (so a repo at C:\repos\polyphony spawns worktrees at
    C:\repos\polyphony-item-<id>).

.NOTES
    Companion to .conductor/registry/workflows/apex-driver.yaml.
    The output schema is the workflow's input schema for the
    `worktree_router` step; tests pin both shapes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('spawn', 'teardown')]
    [string]$Operation,

    [Parameter(Mandatory)]
    [int]$WorkItemId,

    [string]$BaseBranch = 'origin/main',

    [string]$WorktreeRoot = ''
)

$ErrorActionPreference = 'Stop'

function Test-GitAvailable {
    try {
        $null = & git --version 2>&1
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
}

function Get-RepoRoot {
    try {
        $repoRoot = (& git rev-parse --show-toplevel 2>&1).Trim()
        if ($LASTEXITCODE -ne 0) { return $null }
        return $repoRoot
    }
    catch {
        return $null
    }
}

function Get-WorktreePath([string]$repoRoot, [string]$worktreeRoot, [int]$workItemId) {
    if ([string]::IsNullOrWhiteSpace($worktreeRoot)) {
        $parent = Split-Path -Parent $repoRoot
        $repoName = Split-Path -Leaf $repoRoot
        return Join-Path $parent "$repoName-item-$workItemId"
    }
    return Join-Path $worktreeRoot "item-$workItemId"
}

$envelope = [ordered]@{
    success       = $false
    operation     = $Operation
    work_item_id  = $WorkItemId
    worktree_path = ''
    branch        = ''
    error_code    = ''
    error_message = ''
}

try {
    if (-not (Test-GitAvailable)) {
        $envelope.error_code = 'git_unavailable'
        $envelope.error_message = 'git is not available on PATH'
        $envelope | ConvertTo-Json -Compress
        exit 0
    }

    $repoRoot = Get-RepoRoot
    if (-not $repoRoot) {
        $envelope.error_code = 'git_unavailable'
        $envelope.error_message = 'not inside a git repository (git rev-parse --show-toplevel failed)'
        $envelope | ConvertTo-Json -Compress
        exit 0
    }

    $worktreePath = Get-WorktreePath $repoRoot $WorktreeRoot $WorkItemId
    $branchName = "sdlc/apex/$WorkItemId"
    $envelope.worktree_path = $worktreePath
    $envelope.branch = $branchName

    if ($Operation -eq 'spawn') {
        if ([string]::IsNullOrWhiteSpace($BaseBranch)) {
            $envelope.error_code = 'missing_base_branch'
            $envelope.error_message = 'spawn requires -BaseBranch (or default origin/main)'
            $envelope | ConvertTo-Json -Compress
            exit 0
        }

        if (Test-Path $worktreePath) {
            # Idempotent re-entry: a prior dispatch already spawned this worktree.
            $envelope.success = $true
            $envelope | ConvertTo-Json -Compress
            exit 0
        }

        $output = & git worktree add -b $branchName $worktreePath $BaseBranch 2>&1
        if ($LASTEXITCODE -ne 0) {
            $envelope.error_code = 'worktree_add_failed'
            $envelope.error_message = "git worktree add failed: $($output -join "`n")"
            $envelope | ConvertTo-Json -Compress
            exit 0
        }

        $envelope.success = $true
        $envelope | ConvertTo-Json -Compress
        exit 0
    }

    # teardown
    if (-not (Test-Path $worktreePath)) {
        # Idempotent: nothing to clean up.
        $envelope.success = $true
        $envelope | ConvertTo-Json -Compress
        exit 0
    }

    $output = & git worktree remove --force $worktreePath 2>&1
    if ($LASTEXITCODE -ne 0) {
        $envelope.error_code = 'worktree_remove_failed'
        $envelope.error_message = "git worktree remove failed: $($output -join "`n")"
        $envelope | ConvertTo-Json -Compress
        exit 0
    }

    $envelope.success = $true
    $envelope | ConvertTo-Json -Compress
    exit 0
}
catch {
    $envelope.error_code = 'unexpected'
    $envelope.error_message = $_.Exception.Message
    $envelope | ConvertTo-Json -Compress
    exit 0
}
