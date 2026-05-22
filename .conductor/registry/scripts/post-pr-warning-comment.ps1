<#
.SYNOPSIS
    Post a single advisory "warning-mode auto-merge" stamp comment to a PR
    just before the auto-merge fires, when
    `policy.pr.defaults.mode == 'warning'` (AB#3217).

.DESCRIPTION
    Invoked by the `pr_warning_stamp` step that sits between
    `pr_pre_merge_policy_router` and `pr_merger` on the auto-merge path
    when policy mode is `warning`. Posts one short comment so operators
    have a visible audit trail that the merge was policy-driven rather
    than human-approved.

    Always exits 0. A comment-post failure must NEVER block the merge --
    the warning stamp is advisory, not gating. Errors surface in the
    `error` field of the JSON envelope so downstream gates / journal
    consumers can render the failure if they care.

    Routes downstream to `pr_merger` unconditionally; the workflow YAML
    encodes that hop rather than this script.

.PARAMETER Platform
    'github' or 'ado'. Determines the comment-post mechanism.

.PARAMETER PrNumber
    Pull request number (positive integer).

.PARAMETER Organization
    ADO organization name (e.g. 'contoso'). Required when Platform='ado',
    ignored for github.

.PARAMETER Project
    ADO project name. Required when Platform='ado', ignored for github.

.PARAMETER Repository
    ADO repository identifier (GUID or name). Required when Platform='ado',
    ignored for github. (github case infers repo from cwd via gh CLI.)

.PARAMETER WorkItemId
    Optional work item ID to mention in the comment body for cross-link
    context. Empty string skips the mention.

.PARAMETER PolyphonyExe
    Path or command for the polyphony CLI (used by the ADO branch via
    `polyphony pr post-comment-ado`). Defaults to 'polyphony'. Tests
    override.

.PARAMETER GhExe
    Path or command for the GitHub CLI (used by the github branch via
    `gh pr comment`). Defaults to 'gh'. Tests override.

.OUTPUTS
    JSON envelope on stdout:
        {
          "stamped":  true | false,
          "platform": "github" | "ado",
          "error":    "<message>"   # empty unless stamped=false
        }
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('github', 'ado')]
    [string]$Platform,

    [Parameter(Mandatory = $true)]
    [int]$PrNumber,

    [string]$Organization = '',
    [string]$Project = '',
    [string]$Repository = '',
    [string]$WorkItemId = '',
    [string]$PolyphonyExe = 'polyphony',
    [string]$GhExe = 'gh'
)

# NOTE: We intentionally do NOT $ErrorActionPreference = 'Stop' here.
# Comment-post failure must surface as a non-stamped envelope, not a
# script-level termination that would break the workflow.

$bodyLines = @(
    "⚠️ **Auto-merged under `policy.pr.defaults.mode == ``warning```**"
    ""
    "This PR was auto-merged by polyphony without manual operator approval, per the configured PR policy mode."
)
if (-not [string]::IsNullOrWhiteSpace($WorkItemId)) {
    $bodyLines += ""
    $bodyLines += "Work item: AB#$WorkItemId"
}
$body = [string]::Join("`n", $bodyLines)

$stamped = $false
$errMsg = ''

try {
    if ($Platform -eq 'github') {
        # gh pr comment infers repository from cwd. Workflow runs in the
        # feature worktree, so cwd is the right repo.
        $stdout = & $GhExe pr comment $PrNumber --body $body 2>&1
        if ($LASTEXITCODE -eq 0) {
            $stamped = $true
        } else {
            $errMsg = "gh pr comment exited $LASTEXITCODE`: $stdout"
        }
    }
    else {
        # ADO: route via the in-process polyphony verb so PAT auth + thread
        # creation match the rest of the workflow.
        $missing = @()
        if ([string]::IsNullOrWhiteSpace($Organization)) { $missing += '--organization' }
        if ([string]::IsNullOrWhiteSpace($Project))      { $missing += '--project' }
        if ([string]::IsNullOrWhiteSpace($Repository))   { $missing += '--repository' }
        if ($missing.Count -gt 0) {
            $errMsg = "ADO platform requires: $($missing -join ', ')"
        }
        else {
            $stdout = & $PolyphonyExe pr post-comment-ado `
                --organization $Organization `
                --project $Project `
                --repository $Repository `
                --pr-number $PrNumber `
                --body $body 2>&1
            if ($LASTEXITCODE -eq 0) {
                # post-comment-ado is a routing-style verb (always exits 0); inspect envelope.
                try {
                    $parsed = $stdout | ConvertFrom-Json -ErrorAction Stop
                    if ($parsed.PSObject.Properties.Name -contains 'error_code' -and -not [string]::IsNullOrEmpty($parsed.error_code)) {
                        $errMsg = "post-comment-ado error_code='$($parsed.error_code)': $($parsed.error)"
                    } else {
                        $stamped = $true
                    }
                } catch {
                    $errMsg = "post-comment-ado envelope parse failed: $($_.Exception.Message)"
                }
            } else {
                $errMsg = "polyphony pr post-comment-ado exited $LASTEXITCODE`: $stdout"
            }
        }
    }
} catch {
    $errMsg = "exception: $($_.Exception.Message)"
}

[ordered]@{
    stamped  = $stamped
    platform = $Platform
    error    = $errMsg
} | ConvertTo-Json -Compress
