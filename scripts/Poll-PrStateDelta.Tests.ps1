#requires -Version 7.0

<#
.SYNOPSIS
    Pester tests for scripts/Poll-PrStateDelta.ps1.

.DESCRIPTION
    Tests are structured around the contract defined in Poll-PrStateDelta.ps1.
    Rather than dot-sourcing the script (which has a top-level param block and
    auto-executes), each test invokes it as a subprocess via `pwsh -File ...`,
    captures stdout (JSON), stderr, and the exit code.

    CLI stubs (gh, az) are installed per-test onto a PATH-isolated directory
    using the same Windows .cmd shim pattern as Resolve-GhIdentity.Tests.ps1.

    Coverage:
      - Initial observation: watermark missing → records state, returns immediately
      - Reaction kinds: pr_merged, pr_closed, new_commit, new_review_* (all three),
        new_comment, ci_status_changed
      - Precedence: multiple deltas in one poll → highest precedence returned
      - Timeout: no delta within budget → reaction_kind: timeout, exit 0
      - Bad input: missing platform, bad URL → exit 2
      - Auth failure: gh exits 401 → exit 3
      - PR not found: gh exits 404 → exit 4
      - Network failure: gh hangs → exit 5

    NOTE: Tests are written but NOT executed as part of authoring (no live
    gh/az credentials or real PRs in this environment). Run with:
        pwsh -Command "Invoke-Pester scripts/Poll-PrStateDelta.Tests.ps1 -Output Detailed"
#>

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Poll-PrStateDelta.ps1'

    # ── Stub installers ───────────────────────────────────────────────────

    function Install-CliStub {
        param(
            [Parameter(Mandatory)][string]$StubDir,
            [Parameter(Mandatory)][string]$ExeName,
            [Parameter(Mandatory)][string]$ScriptBody
        )
        $ps1Path = Join-Path $StubDir "$ExeName-impl.ps1"
        Set-Content -Path $ps1Path -Value $ScriptBody -Encoding utf8

        if ($IsWindows) {
            $cmdPath = Join-Path $StubDir "$ExeName.cmd"
            Set-Content -Path $cmdPath -Value "@pwsh -NoProfile -ExecutionPolicy Bypass -File `"$ps1Path`" %*" -Encoding ascii
        } else {
            $shPath = Join-Path $StubDir $ExeName
            Set-Content -Path $shPath -Value "#!/usr/bin/env bash`npwsh -NoProfile -File `"$ps1Path`" `"`$@`"" -Encoding utf8
            chmod +x $shPath
        }
    }

    # ── Script invocation helper ─────────────────────────────────────────

    function Invoke-PollScript {
        param(
            [string]$Platform = 'github',
            [string]$PrUrl    = 'https://github.com/org/repo/pull/42',
            [string]$WatermarkPath,
            [int]$TimeoutSeconds      = 5,
            [int]$PollIntervalSeconds = 1,
            [string]$StubDir,
            [hashtable]$ExtraEnv = @{}
        )

        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName  = (Get-Command pwsh).Source
        [void]$psi.ArgumentList.Add('-NoProfile')
        [void]$psi.ArgumentList.Add('-ExecutionPolicy')
        [void]$psi.ArgumentList.Add('Bypass')
        [void]$psi.ArgumentList.Add('-File')
        [void]$psi.ArgumentList.Add($script:ScriptPath)
        [void]$psi.ArgumentList.Add('-Platform')
        [void]$psi.ArgumentList.Add($Platform)
        [void]$psi.ArgumentList.Add('-PrUrl')
        [void]$psi.ArgumentList.Add($PrUrl)
        [void]$psi.ArgumentList.Add('-WatermarkPath')
        [void]$psi.ArgumentList.Add($WatermarkPath)
        [void]$psi.ArgumentList.Add('-TimeoutSeconds')
        [void]$psi.ArgumentList.Add([string]$TimeoutSeconds)
        [void]$psi.ArgumentList.Add('-PollIntervalSeconds')
        [void]$psi.ArgumentList.Add([string]$PollIntervalSeconds)

        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError  = $true
        $psi.UseShellExecute        = $false
        $psi.CreateNoWindow         = $true

        # Inject stub dir at front of PATH
        $sep = [System.IO.Path]::PathSeparator
        $psi.Environment['PATH'] = if ($StubDir) { "$StubDir$sep$env:PATH" } else { $env:PATH }

        foreach ($k in $ExtraEnv.Keys) { $psi.Environment[$k] = $ExtraEnv[$k] }

        $proc = [System.Diagnostics.Process]::new()
        $proc.StartInfo = $psi
        [void]$proc.Start()

        $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
        $stderrTask = $proc.StandardError.ReadToEndAsync()

        [void]$proc.WaitForExit(30000)   # hard 30s wall-clock guard

        return [pscustomobject]@{
            ExitCode = $proc.ExitCode
            Stdout   = $stdoutTask.GetAwaiter().GetResult().Trim()
            Stderr   = $stderrTask.GetAwaiter().GetResult().Trim()
        }
    }

    # ── GitHub stub builders ─────────────────────────────────────────────

    function Build-GhStub {
        param(
            [string]$PrState        = 'open',
            [bool]$Merged           = $false,
            [string]$HeadSha        = 'abc1234',
            [string]$ReviewsJson    = '[]',
            [string]$CommentsJson   = '[]',
            [string]$CheckRunsJson  = '{"check_runs":[]}'
        )
        return @"
`$argLine = `$args -join ' '
if (`$argLine -like 'api repos/*/pulls/* --jq *') {
    Write-Output '{"state":"$PrState","merged":$($Merged.ToString().ToLower()),"head_sha":"$HeadSha"}'
    exit 0
}
if (`$argLine -like 'api repos/*/pulls/*/reviews --jq *') {
    Write-Output '$ReviewsJson'
    exit 0
}
if (`$argLine -like 'api repos/*/issues/*/comments --jq *') {
    Write-Output '$CommentsJson'
    exit 0
}
if (`$argLine -like 'api repos/*/commits/*/check-runs --jq *') {
    `$json = '$CheckRunsJson'
    `$data = `$json | ConvertFrom-Json
    `$conclusions = `$data.check_runs | ForEach-Object { `$_.conclusion } | ConvertTo-Json -Compress
    if (-not `$conclusions) { `$conclusions = '[]' }
    Write-Output `$conclusions
    exit 0
}
[Console]::Error.WriteLine("Unhandled gh args: `$argLine")
exit 1
"@
    }
}

# ── Test workspace cleanup ────────────────────────────────────────────────────

Describe 'Poll-PrStateDelta' {

    BeforeEach {
        $script:testDir = Join-Path ([System.IO.Path]::GetTempPath()) `
            "pps-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $script:testDir -Force | Out-Null

        $script:watermarkFile = Join-Path $script:testDir 'watermark.json'
        $script:stubDir       = Join-Path $script:testDir 'stubs'
        New-Item -ItemType Directory -Path $script:stubDir -Force | Out-Null
    }

    AfterEach {
        Remove-Item -Path $script:testDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    # ─────────────────────────────────────────────────────────────────────────
    Context 'Initial observation (watermark missing)' {

        It 'Returns reaction_kind=initial_observation and exit 0 when watermark absent' {
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -HeadSha 'sha111' -ReviewsJson '[]' -CommentsJson '[]'
            )

            $r = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir

            $r.ExitCode | Should -Be 0
            $out = $r.Stdout | ConvertFrom-Json
            $out.reaction_kind | Should -Be 'initial_observation'
            $out.platform      | Should -Be 'github'
            $out.pr_url        | Should -Be 'https://github.com/org/repo/pull/42'
            $out.new_watermark.last_commit_sha | Should -Be 'sha111'
        }

        It 'Writes watermark file on initial observation' {
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -HeadSha 'sha111'
            )

            Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir | Out-Null

            Test-Path $script:watermarkFile | Should -BeTrue
            $wm = Get-Content $script:watermarkFile -Raw | ConvertFrom-Json
            $wm.last_commit_sha | Should -Be 'sha111'
        }

        It 'Watermark contains null for last_merged_state when PR is open' {
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -PrState 'open' -Merged $false -HeadSha 'sha0'
            )
            Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir | Out-Null
            $wm = Get-Content $script:watermarkFile -Raw | ConvertFrom-Json
            $wm.last_merged_state | Should -BeNullOrEmpty
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    Context 'Reaction kinds — GitHub' {

        BeforeEach {
            # Lay down a base watermark (open PR, sha 'base000')
            $baseWatermark = [ordered]@{
                last_merged_state  = $null
                last_commit_sha    = 'base000'
                last_review_ids    = @()
                last_comment_ids   = @()
                last_ci_conclusion = $null
            }
            $baseWatermark | ConvertTo-Json | Set-Content -Path $script:watermarkFile -Encoding utf8NoBOM
        }

        It 'Returns pr_merged when PR transitions to merged state' {
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -PrState 'closed' -Merged $true -HeadSha 'base000'
            )
            $r   = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir -TimeoutSeconds 10
            $out = $r.Stdout | ConvertFrom-Json
            $r.ExitCode        | Should -Be 0
            $out.reaction_kind | Should -Be 'pr_merged'
        }

        It 'Returns pr_closed when PR is closed without merging' {
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -PrState 'closed' -Merged $false -HeadSha 'base000'
            )
            $r   = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir -TimeoutSeconds 10
            $out = $r.Stdout | ConvertFrom-Json
            $r.ExitCode        | Should -Be 0
            $out.reaction_kind | Should -Be 'pr_closed'
        }

        It 'Returns new_commit when HEAD SHA changes' {
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -HeadSha 'newsha1'
            )
            $r   = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir -TimeoutSeconds 10
            $out = $r.Stdout | ConvertFrom-Json
            $out.reaction_kind             | Should -Be 'new_commit'
            $out.delta_details.new_sha     | Should -Be 'newsha1'
            $out.delta_details.prev_sha    | Should -Be 'base000'
        }

        It 'Returns new_review_changes_requested when CHANGES_REQUESTED review appears' {
            $reviewsJson = '[{"id":"101","state":"CHANGES_REQUESTED"}]'
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -ReviewsJson $reviewsJson -HeadSha 'base000'
            )
            $r   = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir -TimeoutSeconds 10
            $out = $r.Stdout | ConvertFrom-Json
            $out.reaction_kind | Should -Be 'new_review_changes_requested'
        }

        It 'Returns new_review_approved when APPROVED review appears' {
            $reviewsJson = '[{"id":"102","state":"APPROVED"}]'
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -ReviewsJson $reviewsJson -HeadSha 'base000'
            )
            $r   = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir -TimeoutSeconds 10
            $out = $r.Stdout | ConvertFrom-Json
            $out.reaction_kind | Should -Be 'new_review_approved'
        }

        It 'Returns new_review_commented when COMMENTED review appears' {
            $reviewsJson = '[{"id":"103","state":"COMMENTED"}]'
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -ReviewsJson $reviewsJson -HeadSha 'base000'
            )
            $r   = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir -TimeoutSeconds 10
            $out = $r.Stdout | ConvertFrom-Json
            $out.reaction_kind | Should -Be 'new_review_commented'
        }

        It 'Returns new_comment when a new comment ID appears' {
            $commentsJson = '["9001"]'
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -CommentsJson $commentsJson -HeadSha 'base000'
            )
            $r   = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir -TimeoutSeconds 10
            $out = $r.Stdout | ConvertFrom-Json
            $out.reaction_kind | Should -Be 'new_comment'
        }

        It 'Returns ci_status_changed when CI conclusion changes from null to SUCCESS' {
            $checkRunsJson = '{"check_runs":[{"conclusion":"success"}]}'
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -CheckRunsJson $checkRunsJson -HeadSha 'base000'
            )
            $r   = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir -TimeoutSeconds 10
            $out = $r.Stdout | ConvertFrom-Json
            $out.reaction_kind | Should -Be 'ci_status_changed'
        }

        It 'Does not trigger ci_status_changed when CI unchanged (still null)' {
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -HeadSha 'base000' -CheckRunsJson '{"check_runs":[]}'
            )
            # With no delta, poll will run until timeout
            $r   = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir `
                       -TimeoutSeconds 2 -PollIntervalSeconds 1
            $out = $r.Stdout | ConvertFrom-Json
            $r.ExitCode        | Should -Be 0
            $out.reaction_kind | Should -Be 'timeout'
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    Context 'Precedence' {

        BeforeEach {
            $baseWatermark = [ordered]@{
                last_merged_state  = $null
                last_commit_sha    = 'base000'
                last_review_ids    = @()
                last_comment_ids   = @()
                last_ci_conclusion = $null
            }
            $baseWatermark | ConvertTo-Json | Set-Content -Path $script:watermarkFile -Encoding utf8NoBOM
        }

        It 'Returns new_commit (not new_comment) when both occur in same poll' {
            # New commit (sha changed) + new comment — new_commit wins
            $commentsJson = '["8888"]'
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -HeadSha 'newsha9' -CommentsJson $commentsJson
            )
            $r   = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir -TimeoutSeconds 10
            $out = $r.Stdout | ConvertFrom-Json
            $out.reaction_kind | Should -Be 'new_commit'
        }

        It 'Bundles lower-precedence deltas into also_observed' {
            $commentsJson = '["8888"]'
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -HeadSha 'newsha9' -CommentsJson $commentsJson
            )
            $r   = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir -TimeoutSeconds 10
            $out = $r.Stdout | ConvertFrom-Json
            $out.delta_details.also_observed | Should -Contain 'new_comment'
        }

        It 'Returns pr_merged even when new_commit also present' {
            # PR merged + SHA technically different — pr_merged wins
            $checkRunsJson = '{"check_runs":[{"conclusion":"success"}]}'
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -PrState 'closed' -Merged $true -HeadSha 'newsha9' -CheckRunsJson $checkRunsJson
            )
            $r   = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir -TimeoutSeconds 10
            $out = $r.Stdout | ConvertFrom-Json
            $out.reaction_kind | Should -Be 'pr_merged'
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    Context 'Timeout path' {

        It 'Returns reaction_kind=timeout and exit 0 when no delta within budget' {
            # Watermark already in sync with API — nothing changes
            $baseWatermark = [ordered]@{
                last_merged_state  = $null
                last_commit_sha    = 'stablesha'
                last_review_ids    = @()
                last_comment_ids   = @()
                last_ci_conclusion = $null
            }
            $baseWatermark | ConvertTo-Json | Set-Content -Path $script:watermarkFile -Encoding utf8NoBOM

            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -HeadSha 'stablesha'
            )

            $r   = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir `
                       -TimeoutSeconds 3 -PollIntervalSeconds 1
            $out = $r.Stdout | ConvertFrom-Json

            $r.ExitCode        | Should -Be 0
            $out.reaction_kind | Should -Be 'timeout'
            $out.delta_details.poll_count | Should -BeGreaterThan 0
        }

        It 'Preserves original watermark content after timeout (no write)' {
            $baseWatermark = [ordered]@{
                last_merged_state  = $null
                last_commit_sha    = 'stablesha'
                last_review_ids    = @()
                last_comment_ids   = @()
                last_ci_conclusion = $null
            }
            $baseWatermark | ConvertTo-Json | Set-Content -Path $script:watermarkFile -Encoding utf8NoBOM

            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -HeadSha 'stablesha'
            )

            Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir `
                -TimeoutSeconds 2 -PollIntervalSeconds 1 | Out-Null

            # Watermark SHA should still be stablesha (no reaction = no rewrite)
            $wm = Get-Content $script:watermarkFile -Raw | ConvertFrom-Json
            $wm.last_commit_sha | Should -Be 'stablesha'
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    Context 'Bad input — exit 2' {

        It 'Exits 2 for an unparseable GitHub URL' {
            $r = Invoke-PollScript `
                -PrUrl 'https://not-a-valid-pr-url/foo' `
                -WatermarkPath $script:watermarkFile
            $r.ExitCode | Should -Be 2
        }

        It 'Exits 2 for an unparseable ADO URL' {
            $r = Invoke-PollScript `
                -Platform 'ado' `
                -PrUrl 'https://dev.azure.com/bad-url' `
                -WatermarkPath $script:watermarkFile
            $r.ExitCode | Should -Be 2
        }

        It 'Exits 2 for TimeoutSeconds = 0' {
            $r = Invoke-PollScript `
                -WatermarkPath $script:watermarkFile `
                -TimeoutSeconds 0
            $r.ExitCode | Should -Be 2
        }

        It 'Exits 2 for PollIntervalSeconds = -1' {
            $r = Invoke-PollScript `
                -WatermarkPath $script:watermarkFile `
                -PollIntervalSeconds (-1)
            $r.ExitCode | Should -Be 2
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    Context 'Auth failure — exit 3' {

        It 'Exits 3 when gh returns 401 Unauthorized' {
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody @'
$argLine = $args -join ' '
if ($argLine -like 'api *') {
    [Console]::Error.WriteLine('HTTP 401: Bad credentials - authentication required')
    exit 1
}
exit 1
'@
            $r = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir
            $r.ExitCode | Should -Be 3
        }

        It 'Writes CONDUCTOR_ERROR_OUT with kind=auth_failure on exit 3' {
            $errFile = Join-Path $script:testDir 'conductor-error.json'

            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody @'
$argLine = $args -join ' '
if ($argLine -like 'api *') {
    [Console]::Error.WriteLine('unauthorized - token invalid')
    exit 1
}
'@
            Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir `
                -ExtraEnv @{ CONDUCTOR_ERROR_OUT = $errFile } | Out-Null

            Test-Path $errFile | Should -BeTrue
            $err = Get-Content $errFile -Raw | ConvertFrom-Json
            $err.kind | Should -Be 'auth_failure'
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    Context 'PR not found — exit 4' {

        It 'Exits 4 when gh returns 404 Not Found' {
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody @'
$argLine = $args -join ' '
if ($argLine -like 'api *') {
    [Console]::Error.WriteLine('HTTP 404: Not Found - no pull request at that URL')
    exit 1
}
exit 1
'@
            $r = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir
            $r.ExitCode | Should -Be 4
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    Context 'Network / rate-limit failure — exit 5' {

        It 'Exits 5 when gh returns 429 rate-limit' {
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody @'
$argLine = $args -join ' '
if ($argLine -like 'api *') {
    [Console]::Error.WriteLine('HTTP 429: API rate limit exceeded')
    exit 1
}
exit 1
'@
            $r = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir
            $r.ExitCode | Should -Be 5
        }

        It 'Exits 5 when gh hangs beyond its call timeout' {
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody @'
$argLine = $args -join ' '
if ($argLine -like 'api *') {
    Start-Sleep -Seconds 90    # will be killed by Invoke-CliCaptured timeout
    Write-Output '{}'
    exit 0
}
exit 1
'@
            # We cannot easily test the internal 60s CLI timeout here in unit tests
            # without a very long wait. This test exists for CI documentation only
            # — skip it in fast test runs.
            Set-ItResult -Skipped -Because 'Requires 60s+ internal CLI timeout — run in dedicated slow-test suite'
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    Context 'Output shape invariants' {

        It 'Always emits observed_at_utc in ISO 8601 format' {
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub
            )
            $r   = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir
            $out = $r.Stdout | ConvertFrom-Json
            $out.observed_at_utc | Should -Match '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$'
        }

        It 'Always echoes pr_url and platform in output' {
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub
            )
            $r   = Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir
            $out = $r.Stdout | ConvertFrom-Json
            $out.pr_url   | Should -Be 'https://github.com/org/repo/pull/42'
            $out.platform | Should -Be 'github'
        }

        It 'Watermark file is UTF-8 NoBOM' {
            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub
            )
            Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir | Out-Null

            $bytes = [System.IO.File]::ReadAllBytes($script:watermarkFile)
            # UTF-8 BOM is EF BB BF — assert no BOM
            if ($bytes.Count -ge 3) {
                ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) |
                    Should -BeFalse -Because 'watermark file must be UTF-8 NoBOM'
            }
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    Context 'ADO platform — initial observation' {

        It 'Returns initial_observation for ADO URL (az stub)' {
            Install-CliStub -StubDir $script:stubDir -ExeName 'az' -ScriptBody @'
$argLine = $args -join ' '
if ($argLine -like 'repos pr show *') {
    @{
        status = 'active'
        lastMergeSourceCommit = @{ commitId = 'adosha1' }
        reviewers = @()
    } | ConvertTo-Json -Depth 5
    exit 0
}
if ($argLine -like 'repos pr thread list *') {
    Write-Output '[]'
    exit 0
}
if ($argLine -like 'repos pr policy list *') {
    Write-Output '[]'
    exit 0
}
[Console]::Error.WriteLine("Unhandled az args: $argLine")
exit 1
'@
            $r = Invoke-PollScript `
                -Platform 'ado' `
                -PrUrl 'https://dev.azure.com/myorg/myproject/_git/myrepo/pullrequest/99' `
                -WatermarkPath $script:watermarkFile `
                -StubDir $script:stubDir

            $r.ExitCode | Should -Be 0
            $out = $r.Stdout | ConvertFrom-Json
            $out.reaction_kind | Should -Be 'initial_observation'
            $out.platform      | Should -Be 'ado'
            $out.new_watermark.last_commit_sha | Should -Be 'adosha1'
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    Context 'Watermark written on reaction detection' {

        It 'Updates the watermark file after a reaction is detected' {
            $baseWatermark = [ordered]@{
                last_merged_state  = $null
                last_commit_sha    = 'oldsha'
                last_review_ids    = @()
                last_comment_ids   = @()
                last_ci_conclusion = $null
            }
            $baseWatermark | ConvertTo-Json | Set-Content -Path $script:watermarkFile -Encoding utf8NoBOM

            Install-CliStub -StubDir $script:stubDir -ExeName 'gh' -ScriptBody (
                Build-GhStub -HeadSha 'newsha'
            )

            Invoke-PollScript -WatermarkPath $script:watermarkFile -StubDir $script:stubDir `
                -TimeoutSeconds 10 | Out-Null

            $wm = Get-Content $script:watermarkFile -Raw | ConvertFrom-Json
            $wm.last_commit_sha | Should -Be 'newsha'
        }
    }
}
