<#
.SYNOPSIS
    Per-item lifecycle classifier for the apex-driver dispatch loop.

.DESCRIPTION
    Companion to .conductor/registry/workflows/apex-driver.yaml.

    The apex-driver iterates `polyphony worklist build` waves and
    dispatches each work item into the correct lifecycle sub-workflow.
    This script is the deterministic classifier: it calls
    `polyphony state next-ready --work-item <id>`, inspects the
    requirement-kind dispositions, and emits the lifecycle workflow
    name the apex-driver should invoke.

    The six lifecycle outcomes:

      plan-level     — Item has a planning facet and the next ready
                       requirement is one of plan_authored,
                       plan_reviewed, plan_promoted, or
                       children_seeded.

      actionable     — Item has an actionable facet and the next
                       ready requirement is action_satisfied or
                       evidence_accepted.

      implement-merge-group   — Item has an implementable facet, the next
                       ready requirement is implementation_merged,
                       AND either (a) the item is NOT the apex root,
                       or (b) the item IS the apex root but has zero
                       ADO children (an indivisible apex — the apex
                       itself is the unit of implementation work; the
                       PG branch is `impl/{root}-{root}`).

      feature-pr     — Item has implementation_merged ready, IS the
                       apex root, AND has at least one ADO child. The
                       children's PG branches have already been merged
                       in earlier waves; feature-pr aggregates them
                       into a single feature PR.

      fast-path      — Item has no facets (pure organizational
                       container) OR all requirements already
                       satisfied (status='satisfied' or 'empty'). The
                       apex-driver marks this item as item_satisfied
                       without spawning a sub-workflow.

      terminal-satisfied
                     — Item is dispatchable but the only ready kind
                       is `item_satisfied` — every facet-driven
                       requirement is Satisfied and the only thing
                       left is the act of declaring the item done.
                       The apex-item-dispatch workflow performs the
                       ADO state transition (validate +
                       twig state) without spawning a worktree or a
                       lifecycle sub-workflow. Distinct from
                       fast-path so the wave aggregator can count
                       items as DONE rather than DISPATCHED. Per
                       PR #5 cross-item rollup, this only fires for
                       items whose children's item_satisfied is also
                       Satisfied (or for items with no children).

    Plus three non-dispatch outcomes the apex-driver routes specially:
      monitoring — at least one requirement is fulfilling; defer.
      blocked    — nothing ready, nothing fulfilling, work remains.
      error      — `polyphony state next-ready` returned an error.

    Multi-facet items honor the `facet_sequence` planner declaration:
    actionable evidence is satisfied first, then implementable. For the
    MVP the sequence is implicit (action_satisfied gates
    implementation_merged), so the classifier returns whichever
    requirement is currently dispatchable.

    Per the polyphony-workflow-author skill conventions:
      * ALWAYS exits 0 (routing-style envelope).
      * `polyphony state next-ready` non-zero exits surface as
        `lifecycle_workflow=error` with `error_code` populated.

    Output JSON envelope:
        {
          success: <bool>,
          work_item_id: <int>,
          work_item_type: '<string>',
          status: 'dispatchable' | 'monitoring' | 'satisfied' | 'empty' | 'blocked' | 'error',
          lifecycle_workflow: 'plan-level' | 'actionable' | 'implement-merge-group' | 'feature-pr' | 'fast-path' | 'terminal-satisfied' | 'monitoring' | 'blocked' | 'error',
          next_kinds: [<string>...],
          fulfilling_kinds: [<string>...],
          is_root: <bool>,
          error_code: '<code>' | '',
          error_message: '<msg>' | ''
        }

    Error codes:
      polyphony_unavailable       — polyphony not on PATH.
      next_ready_failed           — `polyphony state next-ready` exited non-zero.
      next_ready_invalid_json     — non-JSON output from polyphony.
      classification_indeterminate — dispatchable but no kind matched a lifecycle.
      hierarchy_failed            — `polyphony hierarchy` exited non-zero or returned non-JSON.
                                    Surfaces only on the apex-root + implementable
                                    branch, where children-count drives the choice
                                    between `implement-merge-group` and `feature-pr`. We FAIL
                                    LOUDLY here rather than silently defaulting,
                                    because either default is wrong roughly half the
                                    time (decomposed apex → wrong-default skips real
                                    aggregation; indivisible apex → wrong-default
                                    creates the empty-MG bug).

.PARAMETER WorkItemId
    ADO work item id of the item to classify.

.PARAMETER ApexId
    Apex root work item id (the value the apex-driver was invoked with).
    Used to disambiguate implement-merge-group vs feature-pr.

.PARAMETER PolyphonyExe
    Override for the polyphony executable path. Defaults to `polyphony`.

.NOTES
    Companion to .conductor/registry/workflows/apex-driver.yaml. The
    output schema is the workflow's input schema for the
    `lifecycle_router` step; tests pin both shapes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$WorkItemId,

    [Parameter(Mandatory)]
    [int]$ApexId,

    [string]$PolyphonyExe = 'polyphony'
)

$ErrorActionPreference = 'Stop'

$envelope = [ordered]@{
    success            = $false
    work_item_id       = $WorkItemId
    work_item_type     = ''
    status             = 'error'
    lifecycle_workflow = 'error'
    next_kinds         = @()
    fulfilling_kinds   = @()
    is_root            = ($WorkItemId -eq $ApexId)
    error_code         = ''
    error_message      = ''
}

function Write-Envelope($e) {
    $e | ConvertTo-Json -Compress -Depth 6
}

# Returns the count of direct ADO children for $WorkItemId by invoking
# `polyphony hierarchy --work-item <id>` and parsing the `children`
# array. Throws on any failure (caller catches and routes via
# error_code='hierarchy_failed').
function Get-DirectChildCount {
    param(
        [Parameter(Mandatory)][int]$WorkItemId,
        [Parameter(Mandatory)][string]$PolyphonyExe
    )
    $stderrFile = [System.IO.Path]::GetTempFileName()
    try {
        $stdout = & $PolyphonyExe hierarchy --work-item $WorkItemId 2>$stderrFile
        $exit = $LASTEXITCODE
        $stderr = Get-Content -Raw $stderrFile -ErrorAction SilentlyContinue
    }
    finally {
        Remove-Item $stderrFile -ErrorAction SilentlyContinue
    }
    if ($exit -ne 0) {
        throw "polyphony hierarchy --work-item $WorkItemId exited $exit. stderr: $stderr"
    }
    $hier = $stdout | ConvertFrom-Json -ErrorAction Stop
    if ($null -eq $hier.children) { return 0 }
    return @($hier.children).Count
}

try {
    $polyphonyCmd = Get-Command $PolyphonyExe -ErrorAction SilentlyContinue
    if (-not $polyphonyCmd) {
        $envelope.error_code = 'polyphony_unavailable'
        $envelope.error_message = "polyphony executable '$PolyphonyExe' not found on PATH"
        Write-Envelope $envelope
        exit 0
    }

    # `polyphony state next-ready` writes JSON to stdout. Capture stderr
    # separately so we can surface it on failure.
    $stderrFile = [System.IO.Path]::GetTempFileName()
    try {
        $stdout = & $PolyphonyExe state next-ready --work-item $WorkItemId 2>$stderrFile
        $exit = $LASTEXITCODE
        $stderr = Get-Content -Raw $stderrFile -ErrorAction SilentlyContinue
    }
    finally {
        Remove-Item $stderrFile -ErrorAction SilentlyContinue
    }

    if ($exit -ne 0) {
        $envelope.error_code = 'next_ready_failed'
        $envelope.error_message = "polyphony state next-ready exited $exit. stderr: $stderr"
        Write-Envelope $envelope
        exit 0
    }

    try {
        $result = $stdout | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        $envelope.error_code = 'next_ready_invalid_json'
        $envelope.error_message = "could not parse polyphony state next-ready JSON: $($_.Exception.Message)"
        Write-Envelope $envelope
        exit 0
    }

    $envelope.work_item_type = [string]$result.work_item_type
    $envelope.status = [string]$result.status
    $envelope.next_kinds = @($result.next | ForEach-Object { [string]$_ })
    $envelope.fulfilling_kinds = @($result.fulfilling | ForEach-Object { [string]$_ })

    switch ($envelope.status) {
        'satisfied' {
            $envelope.success = $true
            $envelope.lifecycle_workflow = 'fast-path'
            Write-Envelope $envelope
            exit 0
        }
        'empty' {
            $envelope.success = $true
            $envelope.lifecycle_workflow = 'fast-path'
            Write-Envelope $envelope
            exit 0
        }
        'monitoring' {
            # 'monitoring' originally short-circuited to the no-op terminal,
            # but the plan/action/impl lifecycle sub-workflows are designed to
            # be idempotent and resumable on existing in-flight artifacts
            # (e.g. plan-level can resume on an OPEN plan PR and drive it
            # through its review+merge phases, especially under unattended
            # policy with review_wait_mode=skip). When the fulfilling kinds
            # are ones a lifecycle workflow CAN drive forward, fall through
            # to classification and re-enter the right lifecycle. Only stay
            # in monitoring when fulfilling kinds are purely external
            # (item_satisfied rolled up from children, or unrecognized).
            $dispatchableFulfilling = @($envelope.fulfilling_kinds | Where-Object {
                $_ -in @('plan_authored','plan_reviewed','plan_promoted','children_seeded',
                         'action_satisfied','evidence_accepted','implementation_merged')
            })
            if ($dispatchableFulfilling.Count -eq 0) {
                $envelope.success = $true
                $envelope.lifecycle_workflow = 'monitoring'
                Write-Envelope $envelope
                exit 0
            }
            # Fall through to classification, treating the fulfilling kinds
            # as the dispatch source. This is the "drive an in-flight PR
            # through merge" path — without it, an open plan PR stalls the
            # apex driver indefinitely (decision=blocked, iterations=1).
        }
        'blocked' {
            $envelope.success = $true
            $envelope.lifecycle_workflow = 'blocked'
            Write-Envelope $envelope
            exit 0
        }
        'error' {
            $envelope.error_code = 'next_ready_failed'
            $envelope.error_message = "polyphony state next-ready returned status=error. detail: $($result.error)"
            Write-Envelope $envelope
            exit 0
        }
        'dispatchable' {
            # fall through to classification
        }
        default {
            $envelope.error_code = 'classification_indeterminate'
            $envelope.error_message = "unrecognized status '$($envelope.status)' from polyphony state next-ready"
            Write-Envelope $envelope
            exit 0
        }
    }

    # Classification: pick the lifecycle workflow from the ready kinds.
    # When status='monitoring' fell through (above), next_kinds is empty
    # and the classification source is fulfilling_kinds — the kinds that
    # are observably in-flight and that a lifecycle workflow can drive
    # forward via its idempotent resume path (e.g. plan-level on an OPEN
    # plan PR).
    $classifySource = if ($envelope.next_kinds.Count -gt 0) {
        $envelope.next_kinds
    } else {
        $envelope.fulfilling_kinds
    }

    # Order matters when a multi-facet item has more than one ready kind:
    #   1. planning kinds win (plan-level always sequences before action/impl)
    #   2. actionable kinds next (action-evidence before implementation)
    #   3. implementation kinds last (routed root -> feature-pr, else implement-merge-group)
    #   4. terminal kinds win ONLY when no other dispatchable kind is ready
    #      — item_satisfied is the close-out act once every facet-driven
    #      requirement has settled.
    $planKinds = @('plan_authored', 'plan_reviewed', 'plan_promoted', 'children_seeded')
    $actionKinds = @('action_satisfied', 'evidence_accepted')
    $implKinds = @('implementation_merged')
    $terminalKinds = @('item_satisfied')

    $hasPlanReady = @($classifySource | Where-Object { $planKinds -contains $_ }).Count -gt 0
    $hasActionReady = @($classifySource | Where-Object { $actionKinds -contains $_ }).Count -gt 0
    $hasImplReady = @($classifySource | Where-Object { $implKinds -contains $_ }).Count -gt 0
    $hasTerminalReady = @($classifySource | Where-Object { $terminalKinds -contains $_ }).Count -gt 0

    if ($hasPlanReady) {
        $envelope.success = $true
        $envelope.lifecycle_workflow = 'plan-level'
    }
    elseif ($hasActionReady) {
        $envelope.success = $true
        $envelope.lifecycle_workflow = 'actionable'
    }
    elseif ($hasImplReady) {
        $envelope.success = $true
        if ($envelope.is_root) {
            # Apex-root + implementable splits on decomposition state:
            #   • zero children → indivisible apex; the apex IS the
            #     PG; route to implement-merge-group so an actual impl PR gets
            #     opened against `impl/{root}-{root}`.
            #   • >=1 child   → decomposed apex; children's PGs were
            #     merged in earlier waves; route to feature-pr to
            #     aggregate them into the feature PR.
            #
            # Defaulting either way silently is wrong roughly half the
            # time and was the surface of the empty-MG bug observed in
            # the AB#3064 dogfood (2026-05-09). Hierarchy failure
            # surfaces as a hard error_code rather than a guess.
            try {
                $childCount = Get-DirectChildCount -WorkItemId $WorkItemId -PolyphonyExe $PolyphonyExe
            }
            catch {
                $envelope.success = $false
                $envelope.lifecycle_workflow = 'error'
                $envelope.error_code = 'hierarchy_failed'
                $envelope.error_message = "could not determine apex-root child count: $($_.Exception.Message)"
                Write-Envelope $envelope
                exit 0
            }
            $envelope.lifecycle_workflow = if ($childCount -gt 0) { 'feature-pr' } else { 'implement-merge-group' }
        }
        else {
            $envelope.lifecycle_workflow = 'implement-merge-group'
        }
    }
    elseif ($hasTerminalReady) {
        # Only terminal kinds are ready -> the item is ready to be
        # declared satisfied. Routes to a worktree-less terminal that
        # performs the ADO state transition (see apex-item-dispatch.yaml
        # `terminal_satisfied` node).
        $envelope.success = $true
        $envelope.lifecycle_workflow = 'terminal-satisfied'
    }
    else {
        $envelope.error_code = 'classification_indeterminate'
        $envelope.error_message = "dispatchable item with no recognized ready kinds (next=$($envelope.next_kinds -join ','); fulfilling=$($envelope.fulfilling_kinds -join ','))"
    }

    Write-Envelope $envelope
    exit 0
}
catch {
    $envelope.error_code = 'unexpected'
    $envelope.error_message = $_.Exception.Message
    Write-Envelope $envelope
    exit 0
}
