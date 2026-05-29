#!/usr/bin/env pwsh
#requires -Version 7.0

<#
.SYNOPSIS
  Polls a PR (GitHub or ADO) for state changes since a recorded watermark.
  Returns when a reaction is detected OR when the timeout expires.

.PARAMETER Platform
  Required. One of: github, ado.

.PARAMETER PrUrl
  Required. Full URL to the PR. Used for both API access and rendering CTA links.

.PARAMETER WatermarkPath
  Optional. File path. Read at start to load last-seen state; written on completion
  with new state. If the file does not exist, treat as "first observation" — record
  current state and return immediately with reaction_kind: "initial_observation".
  When omitted or empty, a temp-file path is derived automatically from the platform
  and PR coordinates (suitable for most workflows).

.PARAMETER TimeoutSeconds
  Optional. Default 86400 (24h). Max time to wait for a reaction.

.PARAMETER PollIntervalSeconds
  Optional. Default 30. Time between polls. Jitter of ±10% is applied to avoid
  thundering-herd against the PR API.

.OUTPUTS
  JSON on stdout (single object):
  {
    "reaction_kind": "initial_observation" | "pr_merged" | "pr_closed" |
                     "new_review_approved" | "new_review_changes_requested" |
                     "new_review_commented" | "new_commit" | "new_comment" |
                     "ci_status_changed" | "timeout",
    "new_watermark": { ...platform-specific state snapshot... },
    "delta_details": { ...kind-specific payload... },
    "pr_url":         "<echoed>",
    "platform":       "<echoed>",
    "observed_at_utc": "2026-05-29T16:22:12Z"
  }

  Stderr: human-readable progress/warnings (one line per poll).

.EXIT CODES
  0  reaction detected OR timeout reached cleanly (route on reaction_kind)
  2  invalid arguments (missing required, unparseable URL)
  3  auth/permission failure (gh / az auth lapsed)
  4  PR not found at URL
  5  network or API rate-limit failure after retries
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('github', 'ado')][string]$Platform,
    [Parameter(Mandatory)][string]$PrUrl,
    [string]$WatermarkPath    = '',  # Optional; auto-derived from PR coords when empty
    [int]$TimeoutSeconds      = 86400,
    [int]$PollIntervalSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Exit code constants ──────────────────────────────────────────────────────
$EXIT_SUCCESS   = 0
$EXIT_BAD_ARGS  = 2
$EXIT_AUTH      = 3
$EXIT_NOT_FOUND = 4
$EXIT_NETWORK   = 5

# ── Reaction precedence (lower index = higher priority) ─────────────────────
$REACTION_PRECEDENCE = [string[]]@(
    'pr_merged', 'pr_closed',
    'new_commit',
    'new_review_changes_requested',
    'new_review_approved',
    'new_review_commented',
    'new_comment',
    'ci_status_changed'
)

# ── Helpers ──────────────────────────────────────────────────────────────────

function Write-Stderr {
    param([string]$Message)
    [Console]::Error.WriteLine($Message)
}

function Invoke-CliCaptured {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$TimeoutSeconds = 60
    )

    $cmd = Get-Command $Exe -ErrorAction SilentlyContinue
    if (-not $cmd) {
        return [pscustomobject]@{
            Ok       = $false
            ExitCode = -1
            Stdout   = ''
            Stderr   = "$Exe not found on PATH"
            TimedOut = $false
        }
    }

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $cmd.Source
    foreach ($a in $Arguments) { [void]$psi.ArgumentList.Add($a) }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.UseShellExecute        = $false
    $psi.CreateNoWindow         = $true

    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    [void]$proc.Start()

    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()

    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
        try { $proc.Kill($true) } catch { }
        return [pscustomobject]@{
            Ok       = $false
            ExitCode = -1
            Stdout   = ''
            Stderr   = "$Exe $($Arguments -join ' ') timed out after ${TimeoutSeconds}s"
            TimedOut = $true
        }
    }

    return [pscustomobject]@{
        Ok       = ($proc.ExitCode -eq 0)
        ExitCode = $proc.ExitCode
        Stdout   = $stdoutTask.GetAwaiter().GetResult().Trim()
        Stderr   = $stderrTask.GetAwaiter().GetResult().Trim()
        TimedOut = $false
    }
}

# Throws with a typed prefix so the top-level catch can map to exit codes.
function Invoke-AssertCli {
    param(
        [Parameter(Mandatory)]$Result,
        [Parameter(Mandatory)][string]$Context
    )
    if ($Result.Ok) { return }

    $combined = "$($Result.Stderr) $($Result.Stdout)".Trim()

    if ($Result.TimedOut) {
        throw "NETWORK:$Context timed out. $combined"
    }
    if ($Result.ExitCode -eq -1 -and $combined -match 'not found on PATH') {
        throw "NETWORK:$Context — $combined"
    }
    if ($combined -match '(?i)(401|unauthorized|authentication|credentials|auth\s+fail|token.*invalid|invalid.*token)') {
        throw "AUTH:$Context failed (exit $($Result.ExitCode)): $combined"
    }
    if ($combined -match '(?i)(404|not\s+found|does\s+not\s+exist|no\s+pull\s+request)') {
        throw "NOTFOUND:$Context failed (exit $($Result.ExitCode)): $combined"
    }
    if ($combined -match '(?i)(429|rate.?limit|too\s+many\s+requests)') {
        throw "NETWORK:$Context rate-limited (exit $($Result.ExitCode)): $combined"
    }
    throw "NETWORK:$Context failed (exit $($Result.ExitCode)): $combined"
}

function Resolve-GitHubCoords {
    param([Parameter(Mandatory)][string]$Url)
    if ($Url -match '^https://github\.com/([^/]+)/([^/]+)/pull/(\d+)') {
        return [pscustomobject]@{
            Owner  = $Matches[1]
            Repo   = $Matches[2]
            Number = [int]$Matches[3]
        }
    }
    throw "Cannot parse GitHub PR URL (expected https://github.com/{owner}/{repo}/pull/{n}): $Url"
}

function Resolve-AdoCoords {
    param([Parameter(Mandatory)][string]$Url)
    # https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{id}
    if ($Url -match '^https://dev\.azure\.com/([^/]+)/([^/?]+)/_git/([^/]+)/pullrequest/(\d+)') {
        return [pscustomobject]@{
            Org     = $Matches[1]
            Project = [System.Uri]::UnescapeDataString($Matches[2])
            Repo    = $Matches[3]
            Id      = [int]$Matches[4]
        }
    }
    # https://{org}.visualstudio.com/{project}/_git/{repo}/pullrequest/{id}
    if ($Url -match '^https://([^.]+)\.visualstudio\.com/([^/?]+)/_git/([^/]+)/pullrequest/(\d+)') {
        return [pscustomobject]@{
            Org     = $Matches[1]
            Project = [System.Uri]::UnescapeDataString($Matches[2])
            Repo    = $Matches[3]
            Id      = [int]$Matches[4]
        }
    }
    throw "Cannot parse ADO PR URL (expected https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{n}): $Url"
}

function Get-GitHubSnapshot {
    param([Parameter(Mandatory)]$Coords)

    $base = "repos/$($Coords.Owner)/$($Coords.Repo)"
    $n    = $Coords.Number

    # PR core state
    $prResult = Invoke-CliCaptured -Exe 'gh' -Arguments @(
        'api', "$base/pulls/$n",
        '--jq', '{state:.state, merged:.merged, head_sha:.head.sha}'
    )
    Invoke-AssertCli -Result $prResult -Context "gh api $base/pulls/$n"
    $prData = $prResult.Stdout | ConvertFrom-Json

    # Reviews
    $revResult = Invoke-CliCaptured -Exe 'gh' -Arguments @(
        'api', "$base/pulls/$n/reviews",
        '--jq', '[.[] | {id:(.id|tostring), state:.state}]'
    )
    Invoke-AssertCli -Result $revResult -Context "gh api $base/pulls/$n/reviews"
    $reviews = @($revResult.Stdout | ConvertFrom-Json)

    # Issue comments (general PR comments, not review comments)
    $cmtResult = Invoke-CliCaptured -Exe 'gh' -Arguments @(
        'api', "$base/issues/$n/comments",
        '--jq', '[.[] | .id | tostring]'
    )
    Invoke-AssertCli -Result $cmtResult -Context "gh api $base/issues/$n/comments"
    $commentIds = @($cmtResult.Stdout | ConvertFrom-Json)

    # Check runs on HEAD commit (best-effort — no error if CI not configured)
    $ciConclusion = $null
    if ($prData.head_sha) {
        $ciResult = Invoke-CliCaptured -Exe 'gh' -Arguments @(
            'api', "$base/commits/$($prData.head_sha)/check-runs",
            '--jq', '[.check_runs[] | .conclusion]'
        )
        if ($ciResult.Ok -and $ciResult.Stdout -ne '[]' -and $ciResult.Stdout -ne '') {
            $conclusions = @($ciResult.Stdout | ConvertFrom-Json)
            $ciConclusion = if ($conclusions -contains 'failure' -or $conclusions -contains 'timed_out') {
                'FAILURE'
            } elseif ($conclusions | Where-Object { $_ -eq $null }) {
                'PENDING'
            } else {
                'SUCCESS'
            }
        }
    }

    return [pscustomobject]@{
        State        = $prData.state
        Merged       = [bool]$prData.merged
        HeadSha      = $prData.head_sha
        Reviews      = $reviews
        CommentIds   = $commentIds
        CiConclusion = $ciConclusion
    }
}

function Get-AdoSnapshot {
    param([Parameter(Mandatory)]$Coords)

    $orgUrl     = "https://dev.azure.com/$($Coords.Org)"
    $commonArgs = @(
        '--id', [string]$Coords.Id,
        '--org', $orgUrl,
        '--project', $Coords.Project,
        '--detect', 'false',
        '--output', 'json'
    )

    # PR core state + reviewers
    $prResult = Invoke-CliCaptured -Exe 'az' -Arguments (@('repos', 'pr', 'show') + $commonArgs)
    Invoke-AssertCli -Result $prResult -Context "az repos pr show --id $($Coords.Id)"
    $prData = $prResult.Stdout | ConvertFrom-Json

    # Discussion threads
    $threadIds = @()
    $threadResult = Invoke-CliCaptured -Exe 'az' -Arguments (@('repos', 'pr', 'thread', 'list') + $commonArgs)
    if ($threadResult.Ok -and $threadResult.Stdout -ne '' -and $threadResult.Stdout -ne '[]') {
        $threads   = @($threadResult.Stdout | ConvertFrom-Json)
        $threadIds = @($threads | ForEach-Object { [string]$_.id })
    }

    # Build status via policy evaluations (best-effort)
    $buildStatus = $null
    $policyResult = Invoke-CliCaptured -Exe 'az' -Arguments (@('repos', 'pr', 'policy', 'list') + $commonArgs)
    if ($policyResult.Ok -and $policyResult.Stdout -ne '' -and $policyResult.Stdout -ne '[]') {
        $policies     = @($policyResult.Stdout | ConvertFrom-Json)
        $buildPols    = @($policies | Where-Object { $_.configuration.type.displayName -like '*Build*' })
        if ($buildPols.Count -gt 0) {
            $policyStatuses = @($buildPols | ForEach-Object { $_.status })
            $buildStatus = if ($policyStatuses -contains 'rejected') { 'failed' }
                           elseif ($policyStatuses -contains 'running' -or $policyStatuses -contains 'queued') { 'inProgress' }
                           elseif ($policyStatuses -contains 'approved') { 'succeeded' }
                           else { $null }
        }
    }

    return [pscustomobject]@{
        Status      = $prData.status               # active / completed / abandoned
        HeadSha     = $prData.lastMergeSourceCommit.commitId
        Reviewers   = @($prData.reviewers)
        ThreadIds   = $threadIds
        BuildStatus = $buildStatus
    }
}

function New-GitHubWatermark {
    param([Parameter(Mandatory)]$Snapshot)
    return [ordered]@{
        last_merged_state  = if ($Snapshot.Merged) { 'MERGED' }
                             elseif ($Snapshot.State -eq 'closed') { 'CLOSED' }
                             else { $null }
        last_commit_sha    = $Snapshot.HeadSha
        last_review_ids    = @($Snapshot.Reviews | ForEach-Object { $_.id })
        last_comment_ids   = @($Snapshot.CommentIds)
        last_ci_conclusion = $Snapshot.CiConclusion
    }
}

function New-AdoWatermark {
    param([Parameter(Mandatory)]$Snapshot)
    $revMap = [ordered]@{}
    foreach ($r in $Snapshot.Reviewers) {
        $revMap[$r.uniqueName] = [int]$r.vote
    }
    return [ordered]@{
        last_status       = $Snapshot.Status
        last_commit_sha   = $Snapshot.HeadSha
        last_review_revs  = $revMap
        last_thread_ids   = @($Snapshot.ThreadIds)
        last_build_status = $Snapshot.BuildStatus
    }
}

function Find-GitHubDeltas {
    param(
        [Parameter(Mandatory)]$Watermark,
        [Parameter(Mandatory)]$Snapshot
    )

    $deltas = [System.Collections.Generic.List[hashtable]]::new()

    # pr_merged / pr_closed (terminal — check first)
    if ($Snapshot.Merged -and $Watermark.last_merged_state -ne 'MERGED') {
        $deltas.Add(@{ kind = 'pr_merged'; details = @{ state = 'MERGED' } })
    } elseif ($Snapshot.State -eq 'closed' -and -not $Snapshot.Merged -and $Watermark.last_merged_state -ne 'CLOSED') {
        $deltas.Add(@{ kind = 'pr_closed'; details = @{ state = 'CLOSED' } })
    }

    # new_commit
    if ($Snapshot.HeadSha -and $Snapshot.HeadSha -ne $Watermark.last_commit_sha) {
        $deltas.Add(@{ kind = 'new_commit'; details = @{ new_sha = $Snapshot.HeadSha; prev_sha = $Watermark.last_commit_sha } })
    }

    # new reviews (by ID)
    $seenIds    = @($Watermark.last_review_ids | ForEach-Object { [string]$_ })
    $newReviews = @($Snapshot.Reviews | Where-Object { [string]$_.id -notin $seenIds })

    $crReviews = @($newReviews | Where-Object { $_.state -eq 'CHANGES_REQUESTED' })
    $apReviews = @($newReviews | Where-Object { $_.state -eq 'APPROVED' })
    $cmReviews = @($newReviews | Where-Object { $_.state -eq 'COMMENTED' })

    if ($crReviews.Count -gt 0) {
        $deltas.Add(@{ kind = 'new_review_changes_requested'; details = @{ review_ids = @($crReviews | ForEach-Object { $_.id }) } })
    }
    if ($apReviews.Count -gt 0) {
        $deltas.Add(@{ kind = 'new_review_approved'; details = @{ review_ids = @($apReviews | ForEach-Object { $_.id }) } })
    }
    if ($cmReviews.Count -gt 0) {
        $deltas.Add(@{ kind = 'new_review_commented'; details = @{ review_ids = @($cmReviews | ForEach-Object { $_.id }) } })
    }

    # new_comment
    $seenCommentIds = @($Watermark.last_comment_ids | ForEach-Object { [string]$_ })
    $newComments    = @($Snapshot.CommentIds | Where-Object { $_ -notin $seenCommentIds })
    if ($newComments.Count -gt 0) {
        $deltas.Add(@{ kind = 'new_comment'; details = @{ comment_ids = $newComments } })
    }

    # ci_status_changed
    if ($Snapshot.CiConclusion -and $Snapshot.CiConclusion -ne $Watermark.last_ci_conclusion) {
        $deltas.Add(@{ kind = 'ci_status_changed'; details = @{ prev = $Watermark.last_ci_conclusion; new = $Snapshot.CiConclusion } })
    }

    return $deltas
}

function Find-AdoDeltas {
    param(
        [Parameter(Mandatory)]$Watermark,
        [Parameter(Mandatory)]$Snapshot
    )

    $deltas = [System.Collections.Generic.List[hashtable]]::new()

    # pr_merged / pr_closed
    if ($Snapshot.Status -eq 'completed' -and $Watermark.last_status -ne 'completed') {
        $deltas.Add(@{ kind = 'pr_merged'; details = @{ status = 'completed' } })
    } elseif ($Snapshot.Status -eq 'abandoned' -and $Watermark.last_status -ne 'abandoned') {
        $deltas.Add(@{ kind = 'pr_closed'; details = @{ status = 'abandoned' } })
    }

    # new_commit
    if ($Snapshot.HeadSha -and $Snapshot.HeadSha -ne $Watermark.last_commit_sha) {
        $deltas.Add(@{ kind = 'new_commit'; details = @{ new_sha = $Snapshot.HeadSha; prev_sha = $Watermark.last_commit_sha } })
    }

    # review vote changes
    $prevRevs     = $Watermark.last_review_revs
    $crReviewers  = [System.Collections.Generic.List[string]]::new()
    $apReviewers  = [System.Collections.Generic.List[string]]::new()
    $cmReviewers  = [System.Collections.Generic.List[string]]::new()

    foreach ($reviewer in $Snapshot.Reviewers) {
        $email = $reviewer.uniqueName
        $vote  = [int]$reviewer.vote
        $prevVote = 0
        if ($prevRevs) {
            $prevProp = $prevRevs.PSObject.Properties[$email]
            if ($prevProp) { $prevVote = [int]$prevProp.Value }
        }
        if ($vote -ne $prevVote) {
            if ($vote -le -10)     { $crReviewers.Add($email) }
            elseif ($vote -ge 10)  { $apReviewers.Add($email) }
            elseif ($vote -ne 0)   { $cmReviewers.Add($email) }
        }
    }

    if ($crReviewers.Count -gt 0) {
        $deltas.Add(@{ kind = 'new_review_changes_requested'; details = @{ reviewers = @($crReviewers) } })
    }
    if ($apReviewers.Count -gt 0) {
        $deltas.Add(@{ kind = 'new_review_approved'; details = @{ reviewers = @($apReviewers) } })
    }
    if ($cmReviewers.Count -gt 0) {
        $deltas.Add(@{ kind = 'new_review_commented'; details = @{ reviewers = @($cmReviewers) } })
    }

    # new_comment (new thread IDs)
    $seenThreadIds = @($Watermark.last_thread_ids | ForEach-Object { [string]$_ })
    $newThreadIds  = @($Snapshot.ThreadIds | Where-Object { $_ -notin $seenThreadIds })
    if ($newThreadIds.Count -gt 0) {
        $deltas.Add(@{ kind = 'new_comment'; details = @{ thread_ids = $newThreadIds } })
    }

    # ci_status_changed
    if ($Snapshot.BuildStatus -and $Snapshot.BuildStatus -ne $Watermark.last_build_status) {
        $deltas.Add(@{ kind = 'ci_status_changed'; details = @{ prev = $Watermark.last_build_status; new = $Snapshot.BuildStatus } })
    }

    return $deltas
}

function Select-HighestPrecedenceDelta {
    param([Parameter(Mandatory)]$Deltas)

    $sorted = @($Deltas | Sort-Object -Property {
        $idx = [Array]::IndexOf($REACTION_PRECEDENCE, $_.kind)
        if ($idx -lt 0) { 999 } else { $idx }
    })

    $primary  = $sorted[0]
    $alsoKinds = @($sorted | Select-Object -Skip 1 | ForEach-Object { $_.kind })

    if ($alsoKinds.Count -gt 0) {
        $primary.details['also_observed'] = $alsoKinds
    }

    return $primary
}

function Write-WatermarkAtomic {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object]$Watermark
    )
    $absPath = [System.IO.Path]::GetFullPath($Path)
    $json    = $Watermark | ConvertTo-Json -Depth 10
    $tmpPath = $absPath + '.tmp.' + [System.IO.Path]::GetRandomFileName().Replace('.', '')
    [System.IO.File]::WriteAllText($tmpPath, $json, [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $tmpPath -Destination $absPath -Force
}

function Read-WatermarkFile {
    param([Parameter(Mandatory)][string]$Path)
    $absPath = [System.IO.Path]::GetFullPath($Path)
    $json    = [System.IO.File]::ReadAllText($absPath, [System.Text.UTF8Encoding]::new($false))
    return $json | ConvertFrom-Json
}

function Get-JitteredIntervalMs {
    param(
        [Parameter(Mandatory)][int]$Seconds,
        [double]$JitterFraction = 0.1
    )
    $baseMs   = $Seconds * 1000
    $jitterMs = [int]($baseMs * $JitterFraction)
    $rng      = [System.Random]::new()
    return $baseMs + $rng.Next(-$jitterMs, $jitterMs + 1)
}

function Write-ErrorAndEnvelope {
    param(
        [Parameter(Mandatory)][int]$Code,
        [Parameter(Mandatory)][string]$Message
    )
    $kindMap = @{
        $EXIT_BAD_ARGS  = 'invalid_args'
        $EXIT_AUTH      = 'auth_failure'
        $EXIT_NOT_FOUND = 'pr_not_found'
        $EXIT_NETWORK   = 'network_failure'
    }
    $kind = $kindMap[$Code]
    if (-not $kind) { $kind = 'unknown_failure' }

    Write-Stderr "[Poll-PrStateDelta] ERROR ($kind): $Message"

    $errOut = $env:CONDUCTOR_ERROR_OUT
    if ($errOut) {
        $envelope = [ordered]@{
            kind    = $kind
            message = $Message
            details = @{ exit_code = $Code }
        }
        $envelope | ConvertTo-Json -Compress |
            Set-Content -LiteralPath $errOut -Encoding utf8NoBOM
    }
}

# ── Argument validation ──────────────────────────────────────────────────────

if ([string]::IsNullOrWhiteSpace($PrUrl)) {
    Write-ErrorAndEnvelope -Code $EXIT_BAD_ARGS -Message "PrUrl is required and must be non-empty."
    exit $EXIT_BAD_ARGS
}
if ($TimeoutSeconds -le 0) {
    Write-ErrorAndEnvelope -Code $EXIT_BAD_ARGS -Message "TimeoutSeconds must be a positive integer."
    exit $EXIT_BAD_ARGS
}
if ($PollIntervalSeconds -le 0) {
    Write-ErrorAndEnvelope -Code $EXIT_BAD_ARGS -Message "PollIntervalSeconds must be a positive integer."
    exit $EXIT_BAD_ARGS
}

$coords = $null
try {
    $coords = if ($Platform -eq 'github') {
        Resolve-GitHubCoords -Url $PrUrl
    } else {
        Resolve-AdoCoords -Url $PrUrl
    }
} catch {
    Write-ErrorAndEnvelope -Code $EXIT_BAD_ARGS -Message "Invalid PrUrl: $($_.Exception.Message)"
    exit $EXIT_BAD_ARGS
}

# Derive WatermarkPath from PR coords when not supplied by caller.
if ([string]::IsNullOrWhiteSpace($WatermarkPath)) {
    $pathKey = if ($Platform -eq 'github') {
        "github-$($coords.Owner)-$($coords.Repo)-$($coords.Number)"
    } else {
        "ado-$($coords.Org)-$($coords.Id)"
    }
    $pathKey     = $pathKey -replace '[/\\:*?"<>|]', '_'
    $WatermarkPath = Join-Path ([System.IO.Path]::GetTempPath()) "conductor-pr-delta-$pathKey.json"
    Write-Stderr "[Poll-PrStateDelta] WatermarkPath not supplied; using '$WatermarkPath'"
}

# ── Main polling logic ───────────────────────────────────────────────────────

$startTime = [System.DateTimeOffset]::UtcNow

try {

    # ── Initial observation ───────────────────────────────────────────────────
    if (-not (Test-Path -LiteralPath $WatermarkPath)) {
        Write-Stderr "[Poll-PrStateDelta] No watermark at '$WatermarkPath' — recording initial state."

        $snapshot = if ($Platform -eq 'github') {
            Get-GitHubSnapshot -Coords $coords
        } else {
            Get-AdoSnapshot -Coords $coords
        }

        $watermark = if ($Platform -eq 'github') {
            New-GitHubWatermark -Snapshot $snapshot
        } else {
            New-AdoWatermark -Snapshot $snapshot
        }

        Write-WatermarkAtomic -Path $WatermarkPath -Watermark $watermark

        [ordered]@{
            reaction_kind   = 'initial_observation'
            new_watermark   = $watermark
            delta_details   = @{}
            pr_url          = $PrUrl
            platform        = $Platform
            observed_at_utc = [System.DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        } | ConvertTo-Json -Depth 10
        exit $EXIT_SUCCESS
    }

    # ── Load existing watermark ───────────────────────────────────────────────
    $watermark = Read-WatermarkFile -Path $WatermarkPath

    # ── Poll loop ─────────────────────────────────────────────────────────────
    $pollCount = 0

    while ($true) {
        $elapsed = ([System.DateTimeOffset]::UtcNow - $startTime).TotalSeconds

        if ($elapsed -ge $TimeoutSeconds) {
            Write-Stderr "[Poll-PrStateDelta] Timeout after $([int]$elapsed)s — no reaction detected (polls: $pollCount)."

            [ordered]@{
                reaction_kind   = 'timeout'
                new_watermark   = $watermark
                delta_details   = @{ elapsed_seconds = [int]$elapsed; poll_count = $pollCount }
                pr_url          = $PrUrl
                platform        = $Platform
                observed_at_utc = [System.DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
            } | ConvertTo-Json -Depth 10
            exit $EXIT_SUCCESS
        }

        $pollCount++
        Write-Stderr "[Poll-PrStateDelta] Poll #$pollCount at $([int]$elapsed)s elapsed (budget: ${TimeoutSeconds}s)."

        $snapshot = if ($Platform -eq 'github') {
            Get-GitHubSnapshot -Coords $coords
        } else {
            Get-AdoSnapshot -Coords $coords
        }

        $deltas = @(if ($Platform -eq 'github') {
            Find-GitHubDeltas -Watermark $watermark -Snapshot $snapshot
        } else {
            Find-AdoDeltas -Watermark $watermark -Snapshot $snapshot
        })

        if ($deltas.Count -gt 0) {
            $primary = Select-HighestPrecedenceDelta -Deltas $deltas

            $newWatermark = if ($Platform -eq 'github') {
                New-GitHubWatermark -Snapshot $snapshot
            } else {
                New-AdoWatermark -Snapshot $snapshot
            }

            Write-WatermarkAtomic -Path $WatermarkPath -Watermark $newWatermark
            Write-Stderr "[Poll-PrStateDelta] Reaction detected: $($primary.kind)"

            [ordered]@{
                reaction_kind   = $primary.kind
                new_watermark   = $newWatermark
                delta_details   = $primary.details
                pr_url          = $PrUrl
                platform        = $Platform
                observed_at_utc = [System.DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
            } | ConvertTo-Json -Depth 10
            exit $EXIT_SUCCESS
        }

        $remaining = $TimeoutSeconds - $elapsed
        $sleepMs   = [long][Math]::Min(
            (Get-JitteredIntervalMs -Seconds $PollIntervalSeconds),
            $remaining * 1000
        )

        Write-Stderr "[Poll-PrStateDelta] No delta. Sleeping ${sleepMs}ms ($([int]$remaining)s remaining)."
        Start-Sleep -Milliseconds $sleepMs
    }

} catch {
    $msg = $_.Exception.Message

    $exitCode = if ($msg -match '^AUTH:')     { $EXIT_AUTH }
                elseif ($msg -match '^NOTFOUND:') { $EXIT_NOT_FOUND }
                elseif ($msg -match '^NETWORK:')  { $EXIT_NETWORK }
                else                              { $EXIT_NETWORK }

    Write-ErrorAndEnvelope -Code $exitCode -Message $msg
    exit $exitCode
}
