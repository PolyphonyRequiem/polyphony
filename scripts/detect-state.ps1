<#
.SYNOPSIS
    Deterministic state detector for the twig SDLC apex workflow.
    Inspects ADO work item state, plan artifacts, and git state to determine
    the current lifecycle phase and validate user intent.

.DESCRIPTION
    Thin wrapper around Polyphony CLI commands for phase detection and routing.
    Outputs JSON with: work_item_id, work_item_type, work_item_state, intent,
    phase, plan info, seed status, and error/conflict flags.

.PARAMETER WorkItemId
    ADO work item ID to inspect.

.PARAMETER Intent
    User intent: new, redo, or resume (default: resume).

.PARAMETER PlanPath
    Explicit plan file override for debugging/recovery.
#>
param(
    [Parameter(Mandatory = $true)]
    [int]$WorkItemId,

    [ValidateSet('new', 'redo', 'resume')]
    [string]$Intent = 'resume',

    [string]$PlanPath = ''
)

$ErrorActionPreference = 'Stop'

try {
    # ── Step 0: Sync local cache from ADO ────────────────────────────────────
    # The local .twig SQLite cache may be stale. Force a refresh before
    # reading any state to prevent routing on stale data.
    twig sync --output json 2>$null | Out-Null

    # ── Stub variables ───────────────────────────────────────────────────────
    # Defaults for variables populated by prior tasks:
    #   - #2632: polyphony route integration sets $phase, $workItemType,
    #            $workItemState, $workItemTitle
    #   - #2633: plan discovery sets $hasPlan, $planStatus, $planPath,
    #            $planSource, $hasSeededChildren
    $workItemType          = ''
    $workItemState         = ''
    $workItemTitle         = ''
    $phase                 = 'needs_planning'
    $hasPlan               = $false
    $planStatus            = 'none'
    $planPath              = ''
    $planSource            = 'none'
    $hasSeededChildren     = $false
    $anyChildMissingTasks  = $false
    $seedStatus            = 'unseeded'
    $implementationStatus  = 'not_started'
    $errorMsg              = ''

    # ── Step 1: State transition via polyphony validate (#2634) ───────────
    # Replace the former type-name guard with a type-agnostic
    # validation call. polyphony validate checks the process config to
    # determine if begin_planning is a valid event for this work item type.
    $validateJson = polyphony validate --work-item $WorkItemId --event begin_planning 2>$null
    $validateResult = $validateJson | ConvertFrom-Json
    if ($validateResult.is_valid -and $workItemState -eq 'To Do' -and $hasSeededChildren) {
        twig set $WorkItemId --output json 2>$null | Out-Null
        twig state $validateResult.target_state --output json 2>$null | Out-Null
        $workItemState = $validateResult.target_state
    }

    # ── Step 2: Intent conflict detection (#2634) ─────────────────────────
    # Detects conflicts between the user's stated intent and the current
    # work item state. Ported from reference lines 200-210.
    $intentConflict = $false
    $needsCleanup   = $false

    switch ($Intent) {
        'new' {
            if ($hasSeededChildren -or $hasPlan) {
                $intentConflict = $true
            }
        }
        'redo' {
            $needsCleanup = $hasSeededChildren -or $hasPlan
        }
    }

    # ── Step 3: Phase override by intent (#2634) ─────────────────────────
    # When intent conflicts or cleanup is needed, preserve the phase from
    # polyphony route (let conflict/cleanup take precedence over intent).
    # When plan status is ambiguous, surface an error message.
    if (-not $intentConflict -and -not $needsCleanup) {
        if ($planStatus -eq 'ambiguous') {
            $errorMsg = "Plan status is ambiguous: multiple plan sources detected. Resolve before proceeding."
        }
        # Otherwise, use the phase from polyphony route (set by #2632)
    }

    # ── Build output ─────────────────────────────────────────────────────
    $childrenSummary = @{
        total = 0
        done  = 0
        doing = 0
        todo  = 0
    } | ConvertTo-Json -Compress

    $output = [ordered]@{
        work_item_id            = $WorkItemId
        work_item_type          = $workItemType
        work_item_state         = $workItemState
        work_item_title         = $workItemTitle
        intent                  = $Intent
        phase                   = $phase
        has_plan                = $hasPlan
        plan_status             = $planStatus
        plan_path               = $planPath
        plan_source             = $planSource
        has_seeded_children     = $hasSeededChildren
        any_child_missing_tasks = $anyChildMissingTasks
        seed_status             = $seedStatus
        children_summary        = $childrenSummary
        implementation_status   = $implementationStatus
        intent_conflict         = $intentConflict
        needs_cleanup           = $needsCleanup
        error                   = $errorMsg
    }

    $output | ConvertTo-Json -Depth 3
}
catch {
    [ordered]@{
        error        = $_.Exception.Message
        phase        = 'error'
        work_item_id = $WorkItemId
    } | ConvertTo-Json
    exit 1
}
