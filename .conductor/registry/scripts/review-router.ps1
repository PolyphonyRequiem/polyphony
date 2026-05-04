#requires -Version 7
<#
.SYNOPSIS
  Aggregate technical + readability reviewer outputs and decide whether the
  plan-level workflow should loop back to the architect or proceed to the
  human plan_approval gate.

.DESCRIPTION
  Emits a single JSON object on stdout. Conductor merges the parsed JSON
  into the script-agent's `output` dict, so downstream templates and routes
  can reference fields like `review_router.output.passed`,
  `review_router.output.combined_feedback`, etc.

  Pass criteria (any one wins):
    - average_score >= 90   (passByScore)
    - blocking_issue_count == 0   (passByNoBlocking)
    - prior cycle count >= 5   (capHit — escapes oscillation)

  When forced_by_cap == true, the score thresholds were never met but the
  workflow is bailing out so a human can decide. The plan_approval prompt
  surfaces this prominently.

.PARAMETER TechReviewerJson
  Full JSON of the technical_reviewer output (score, feedback, blocking_issues, suggestions, …).

.PARAMETER ReadabilityReviewerJson
  Same shape for the readability_reviewer.

.PARAMETER PriorCycleCount
  Number of times review_router has already executed in this workflow run.
  Computed by the caller from `context.history`. The current execution does
  not count.
#>
param(
    [Parameter(Mandatory)] [string] $TechReviewerJson,
    [Parameter(Mandatory)] [string] $ReadabilityReviewerJson,
    [Parameter(Mandatory)] [int]    $PriorCycleCount
)

$ErrorActionPreference = 'Stop'

function Get-Field($obj, [string]$name) {
    if ($null -eq $obj) { return $null }
    if ($obj.PSObject.Properties.Name -contains $name) { return $obj.$name }
    return $null
}

function To-Array($value) {
    if ($null -eq $value) { return @() }
    if ($value -is [System.Array]) { return $value }
    return @($value)
}

$tech = $TechReviewerJson | ConvertFrom-Json
$read = $ReadabilityReviewerJson | ConvertFrom-Json

$techScore = [int](Get-Field $tech 'score')
$readScore = [int](Get-Field $read 'score')

$techBlocking = To-Array (Get-Field $tech 'blocking_issues')
$readBlocking = To-Array (Get-Field $read 'blocking_issues')

$blockingCount = $techBlocking.Count + $readBlocking.Count
$avg           = [math]::Floor(($techScore + $readScore) / 2)

$sb = [System.Text.StringBuilder]::new()
if ($techBlocking.Count -gt 0) {
    [void]$sb.AppendLine("### From technical reviewer (score: $techScore)")
    foreach ($it in $techBlocking) { [void]$sb.AppendLine("- $($it.ToString().Trim())") }
    [void]$sb.AppendLine('')
}
if ($readBlocking.Count -gt 0) {
    [void]$sb.AppendLine("### From readability reviewer (score: $readScore)")
    foreach ($it in $readBlocking) { [void]$sb.AppendLine("- $($it.ToString().Trim())") }
}
$combined = $sb.ToString().TrimEnd()

$passByScore      = $avg -ge 90
$passByNoBlocking = $blockingCount -eq 0
$capHit           = $PriorCycleCount -ge 5
$pass             = $passByScore -or $passByNoBlocking -or $capHit
$forcedByCap      = (-not ($passByScore -or $passByNoBlocking)) -and $capHit

$result = [ordered]@{
    average_score             = $avg
    technical_score           = $techScore
    readability_score         = $readScore
    revision_cycles_completed = $PriorCycleCount
    blocking_issue_count      = $blockingCount
    combined_feedback         = $combined
    passed                    = $pass
    forced_by_cap             = $forcedByCap
}

# `-Compress` keeps stdout to a single JSON line so the conductor parser
# doesn't have to deal with multi-line output.
$result | ConvertTo-Json -Compress -Depth 10
