<#
.SYNOPSIS
    Integrate completed wave branches into the apex feature branch in
    edge-correct topological order.

.DESCRIPTION
    Companion to .conductor/registry/workflows/apex-driver.yaml.

    After the apex-driver fans a wave of work-items out into per-item
    worktrees and each lifecycle sub-workflow has produced a child
    branch (sdlc/apex/<id>), this script merges those child branches
    back into the apex feature branch in the order dictated by the
    cross-item edge graph.

    Topological ordering is consumed from
    `polyphony edges check <ApexId> --render json`. Items
    whose `item_satisfied` requirement depends on other items are merged
    AFTER their prerequisites, so the resulting commit graph mirrors the
    declared dependency structure.

    Per the polyphony-workflow-author skill conventions:
      * ALWAYS exits 0 (routing-style envelope).
      * Conflicts surface via the `conflicts` array; the workflow's
        wave_failed_gate routes on `success` and `conflicts.length`.
      * `git merge` failures (conflicts, missing branch, etc.) are
        captured per-branch and apex-driver decides whether to halt
        the wave or continue based on policy.

    Output JSON envelope:
        {
          success: <bool>,
          wave_index: <int>,
          apex_id: <int>,
          feature_branch: '<branch>',
          merge_strategy: 'no-ff' | 'ff-only' | 'ff',
          branches_integrated: [{ work_item_id, branch, merge_commit }],
          skipped: [{ work_item_id, branch, reason }],
          conflicts: [{ work_item_id, branch, conflict_files }],
          error_code: '<code>' | '',
          error_message: '<msg>' | ''
        }

    Error codes:
      git_unavailable         — git not on PATH.
      polyphony_unavailable   — polyphony not on PATH.
      edges_check_failed      — `polyphony edges check` exited non-zero.
      edges_check_invalid_json — non-JSON output from polyphony.
      checkout_failed         — could not check out feature branch.
      missing_apex_id         — required apex id was not provided.

.PARAMETER ApexId
    The apex root work item id. Used for `polyphony edges check` and
    to derive the feature branch name when -FeatureBranch is omitted.

.PARAMETER WaveIndex
    The wave number being integrated (0-based; threaded through into
    the envelope so apex-driver can correlate gate decisions).

.PARAMETER WorkItemIds
    Comma-separated list of work-item ids in the wave whose branches
    should be considered for integration. The script will only merge
    branches for items in this list AND in the topological order
    derived from `polyphony edges check`. Items absent from edge data
    are merged in the order supplied.

.PARAMETER FeatureBranch
    Override for the apex feature branch. Defaults to
    `feature/apex-<ApexId>`.

.PARAMETER MergeStrategy
    One of `no-ff` (default — preserve merge commits per child),
    `ff-only` (refuse on non-fast-forward), `ff` (allow fast-forward).

.PARAMETER PolyphonyExe
    Override for the polyphony executable. Defaults to `polyphony`.

.NOTES
    Companion to .conductor/registry/workflows/apex-driver.yaml. The
    output schema is the workflow's input schema for the
    `wave_integrator` step; tests pin both shapes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$ApexId,

    [Parameter(Mandatory)]
    [int]$WaveIndex,

    [string]$WorkItemIds = '',

    [string]$FeatureBranch = '',

    [ValidateSet('no-ff', 'ff-only', 'ff')]
    [string]$MergeStrategy = 'no-ff',

    [string]$PolyphonyExe = 'polyphony'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($FeatureBranch)) {
    $FeatureBranch = "feature/apex-$ApexId"
}

$envelope = [ordered]@{
    success              = $false
    wave_index           = $WaveIndex
    apex_id              = $ApexId
    feature_branch       = $FeatureBranch
    merge_strategy       = $MergeStrategy
    branches_integrated  = @()
    skipped              = @()
    conflicts            = @()
    error_code           = ''
    error_message        = ''
}

function Write-Envelope($e) {
    $e | ConvertTo-Json -Compress -Depth 6
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
    if (-not (Test-CommandAvailable $PolyphonyExe)) {
        $envelope.error_code = 'polyphony_unavailable'
        $envelope.error_message = "polyphony executable '$PolyphonyExe' not found on PATH"
        Write-Envelope $envelope
        exit 0
    }

    $waveIds = @()
    if (-not [string]::IsNullOrWhiteSpace($WorkItemIds)) {
        $waveIds = $WorkItemIds.Split(',') | ForEach-Object {
            [int]($_.Trim())
        } | Where-Object { $_ -gt 0 }
    }

    # Pull the edge graph; we use it for topological ordering only.
    # Routing-style: polyphony edges check exits 0 even on error and
    # surfaces failure via `Error`/`ErrorCode` in the JSON.
    $stderrFile = [System.IO.Path]::GetTempFileName()
    try {
        $stdout = & $PolyphonyExe edges check $ApexId --render json 2>$stderrFile
        $exit = $LASTEXITCODE
        $stderr = Get-Content -Raw $stderrFile -ErrorAction SilentlyContinue
    }
    finally {
        Remove-Item $stderrFile -ErrorAction SilentlyContinue
    }

    if ($exit -ne 0) {
        $envelope.error_code = 'edges_check_failed'
        $envelope.error_message = "polyphony edges check exited $exit. stderr: $stderr"
        Write-Envelope $envelope
        exit 0
    }

    $edges = $null
    try {
        $edges = $stdout | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        $envelope.error_code = 'edges_check_invalid_json'
        $envelope.error_message = "could not parse polyphony edges check JSON: $($_.Exception.Message)"
        Write-Envelope $envelope
        exit 0
    }

    if ($edges.error) {
        $envelope.error_code = 'edges_check_failed'
        $envelope.error_message = "polyphony edges check returned error: $($edges.error)"
        Write-Envelope $envelope
        exit 0
    }

    # Compute integration order: stable preorder over WorkItemIds.
    # If edges data exposes a topological field, prefer it. Otherwise
    # fall back to the input order, which conductor's worklist build
    # already returns in topological wave order.
    $ordered = @()
    $orderedIds = @()
    if ($edges.PSObject.Properties.Name -contains 'topological_order') {
        foreach ($id in $edges.topological_order) {
            $intId = [int]$id
            if ($waveIds.Count -eq 0 -or $waveIds -contains $intId) {
                $ordered += $intId
                $orderedIds += $intId
            }
        }
        # Append any wave ids missing from topological order at the end
        foreach ($id in $waveIds) {
            if ($orderedIds -notcontains $id) {
                $ordered += $id
            }
        }
    }
    else {
        $ordered = $waveIds
    }

    # Check out the feature branch in the current working tree before merging.
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
        $envelope.error_code = 'checkout_failed'
        $envelope.error_message = "git checkout $FeatureBranch failed: $coErr"
        Write-Envelope $envelope
        exit 0
    }

    $mergeFlag = switch ($MergeStrategy) {
        'no-ff'   { '--no-ff' }
        'ff-only' { '--ff-only' }
        'ff'      { '--ff' }
    }

    $integrated = @()
    $skipped = @()
    $conflicts = @()

    foreach ($id in $ordered) {
        $branch = "sdlc/apex/$id"

        # Verify branch exists locally; if not, skip with a reason.
        $null = & git rev-parse --verify --quiet $branch 2>$null
        if ($LASTEXITCODE -ne 0) {
            $skipped += [ordered]@{
                work_item_id = $id
                branch       = $branch
                reason       = 'branch_not_found'
            }
            continue
        }

        $mergeStderr = [System.IO.Path]::GetTempFileName()
        try {
            $mergeOut = & git merge $mergeFlag --no-edit -m "Integrate $branch into $FeatureBranch (apex $ApexId, wave $WaveIndex)" $branch 2>$mergeStderr
            $mergeExit = $LASTEXITCODE
            $mergeErr = Get-Content -Raw $mergeStderr -ErrorAction SilentlyContinue
        }
        finally {
            Remove-Item $mergeStderr -ErrorAction SilentlyContinue
        }

        if ($mergeExit -ne 0) {
            # Detect merge conflicts via `git diff --name-only --diff-filter=U`.
            $conflictFiles = @()
            try {
                $conflictOut = & git diff --name-only --diff-filter=U 2>$null
                if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($conflictOut)) {
                    $conflictFiles = @($conflictOut.Split("`n") | Where-Object { $_ })
                }
            }
            catch { }

            # Abort the merge so the working tree returns to a clean state
            # for any subsequent integration attempts.
            $null = & git merge --abort 2>$null

            $conflicts += [ordered]@{
                work_item_id    = $id
                branch          = $branch
                conflict_files  = $conflictFiles
                merge_error     = $mergeErr.Trim()
            }
            continue
        }

        $mergeCommit = (& git rev-parse HEAD 2>$null).Trim()
        $integrated += [ordered]@{
            work_item_id  = $id
            branch        = $branch
            merge_commit  = $mergeCommit
        }
    }

    $envelope.branches_integrated = $integrated
    $envelope.skipped = $skipped
    $envelope.conflicts = $conflicts
    $envelope.success = ($conflicts.Count -eq 0)

    Write-Envelope $envelope
    exit 0
}
catch {
    $envelope.error_code = 'unexpected'
    $envelope.error_message = $_.Exception.Message
    Write-Envelope $envelope
    exit 0
}
