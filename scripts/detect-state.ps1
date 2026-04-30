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

    # ── Stub output ──────────────────────────────────────────────────────────
    # Minimal valid JSON with all required schema keys set to defaults.
    # Subsequent tasks will replace stubs with real polyphony-backed logic:
    #   - #2632: polyphony route integration (phase, action, workspace_hint)
    #   - #2633: plan discovery (has_plan, plan_status, plan_path, plan_source)
    #   - #2634: intent validation and state transitions

    $childrenSummary = @{
        total = 0
        done  = 0
        doing = 0
        todo  = 0
    } | ConvertTo-Json -Compress

    $output = [ordered]@{
        work_item_id            = $WorkItemId
        work_item_type          = ''
        work_item_state         = ''
        work_item_title         = ''
        intent                  = $Intent
        phase                   = 'needs_planning'
        has_plan                = $false
        plan_status             = 'none'
        plan_path               = ''
        plan_source             = 'none'
        has_seeded_children     = $false
        any_child_missing_tasks = $false
        seed_status             = 'unseeded'
        children_summary        = $childrenSummary
        implementation_status   = 'not_started'
        intent_conflict         = $false
        needs_cleanup           = $false
        error                   = ''
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
