<#
.SYNOPSIS
    Executor router for the actionable.yaml conductor workflow.
.DESCRIPTION
    Reads `-WorkItemId` and `-Executor` and emits a JSON envelope the
    workflow's `executor_router` step uses to route between the
    polyphony leg (agent + evidence PR) and the human leg (satisfaction
    gate only).

    Per the polyphony-workflow-author skill conventions:
      - Always exits 0; routing is condition-based, not exit-code-based.
      - On unknown executor values, populates `error` so the workflow's
        catch-all route surfaces it via the workflow_error_gate.

    Output JSON:
        { executor: 'polyphony' | 'human', work_item_id: <int>, error: '<msg>' }

    The `error` field is empty on the happy path; populated when the
    `-Executor` value is not one of the allowed enum values.
.PARAMETER WorkItemId
    ADO work item id of the actionable item being satisfied. Threaded
    through into the envelope so the workflow can reference it
    consistently across nodes (templates already resolve
    `executor_router.output.work_item_id`).
.PARAMETER Executor
    Who performs the action. One of `polyphony` (default — polyphony
    invokes the agent and produces an evidence PR) or `human` (the
    action is outside polyphony's authority). Defaults to `polyphony`
    so callers that haven't threaded the executor through yet match
    the workflow input default.
.NOTES
    Companion to .conductor/registry/workflows/actionable.yaml. The
    output schema below is the workflow's input schema for the
    `executor_router` node; tests pin both shapes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$WorkItemId,

    [string]$Executor = 'polyphony'
)

$ErrorActionPreference = 'Stop'

try {
    $allowed = @('polyphony', 'human')
    $errorMsg = ''
    $resolved = $Executor

    if (-not $allowed.Contains($resolved)) {
        $errorMsg = "executor must be one of [$($allowed -join ', ')] (got '$Executor')"
    }

    [ordered]@{
        executor     = $resolved
        work_item_id = $WorkItemId
        error        = $errorMsg
    } | ConvertTo-Json -Compress
}
catch {
    [ordered]@{
        executor     = ''
        work_item_id = $WorkItemId
        error        = $_.Exception.Message
    } | ConvertTo-Json -Compress
}
