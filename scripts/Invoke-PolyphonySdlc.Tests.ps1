<#
Tests for scripts/Invoke-PolyphonySdlc.ps1 — the AB#3085 launcher rework.

Strategy: real bare repo + real worktree fixture per test. No conductor or
init-apex mocking — we run the actual polyphony binary published locally
(~/.twig/bin/polyphony.exe). All tests use -DryRun so no conductor process
is ever spawned.

The fixture creates:
  <tmp>/polyphony.git/             bare repo (cloned from a seed remote)
  <tmp>/polyphony/                  main worktree on `main`, with .twig/
  <tmp>/polyphony-runs/             empty per-run root (init-apex creates apex-N/)

The launcher's cwd contract is satisfied by Push-Location $main before each
test and Pop-Location after. This mirrors how an operator actually invokes
the launcher.
#>

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Invoke-PolyphonySdlc.ps1'

    # Verify polyphony is installed locally — required for init-apex/assert-clean.
    $script:PolyphonyExe = (Get-Command polyphony -ErrorAction SilentlyContinue)
    if (-not $script:PolyphonyExe) {
        throw "polyphony is not on PATH. Run ./publish-local.ps1 before running these tests."
    }

    function New-BareRepoFixture {
        param([string]$RemoteUrl = 'https://github.com/PolyphonyRequiem/polyphony.git')
        $root = Join-Path ([System.IO.Path]::GetTempPath()) "launcher-test-$([System.Guid]::NewGuid().ToString('N').Substring(0, 12))"
        $bare = Join-Path $root 'polyphony.git'
        $main = Join-Path $root 'polyphony'
        $runs = Join-Path $root 'polyphony-runs'

        # 1. Seed remote: a real git repo with a `main` branch + one commit so
        #    the bare clone has a `main` ref and the launcher's main-worktree
        #    detection works.
        $seedRemote = Join-Path $root 'seed.git'
        & git init --bare $seedRemote --quiet
        $seedWt = Join-Path $root 'seed-wt'
        & git clone --quiet $seedRemote $seedWt 2>&1 | Out-Null
        Push-Location $seedWt
        try {
            & git config user.email 'test@test.invalid' 2>&1 | Out-Null
            & git config user.name 'Test' 2>&1 | Out-Null
            & git checkout -b main --quiet 2>&1 | Out-Null
            'seed' | Set-Content -Path 'README.md'
            & git add README.md 2>&1 | Out-Null
            & git -c commit.gpgsign=false commit -m 'seed' --quiet 2>&1 | Out-Null
            & git push --set-upstream origin main --quiet 2>&1 | Out-Null
        } finally { Pop-Location }
        Remove-Item -Recurse -Force $seedWt

        # 2. Bare clone from seed.
        & git clone --bare --quiet $seedRemote $bare 2>&1 | Out-Null

        # 3. Re-point to user-supplied remote URL so the launcher's git remote
        #    detection sees github/ado URLs as it would in production.
        & git --git-dir $bare remote set-url origin $RemoteUrl 2>&1 | Out-Null

        # 4. Main worktree at <root>/polyphony, on main.
        & git --git-dir $bare worktree add --quiet $main main 2>&1 | Out-Null

        # 5. .twig/ in main with a config the launcher can read.
        $twigDir = Join-Path $main '.twig'
        New-Item -ItemType Directory -Path $twigDir -Force | Out-Null
        Set-Content -Path (Join-Path $twigDir 'config') `
            -Value (@{ organization = 'test-org'; project = 'TestProj'; team = ''; processTemplate = 'Basic' } | ConvertTo-Json -Compress)

        # 6. Empty runs root.
        New-Item -ItemType Directory -Path $runs -Force | Out-Null

        # 7. Twig shim (AB#3165 Item 2). The launcher's Phase 2.5 calls
        #    `twig show $ApexId --output json` and refuses on terminal state.
        #    Real twig requires ADO connectivity that the test fixture has
        #    no business needing, so we shim it here. Default state is
        #    "To Do" (non-terminal) so existing tests pass transparently;
        #    set $env:TWIG_FAKE_STATE before the launcher call to override.
        $bin = Join-Path $root 'bin'
        New-Item -ItemType Directory -Path $bin -Force | Out-Null
        $shim = @'
@echo off
if defined TWIG_FAKE_STATE (
  echo {"state": "%TWIG_FAKE_STATE%"}
) else (
  echo {"state": "To Do"}
)
'@
        Set-Content -Path (Join-Path $bin 'twig.cmd') -Value $shim -Encoding ASCII
        $origPath = $env:PATH
        $env:PATH = "$bin$([System.IO.Path]::PathSeparator)$origPath"

        return [pscustomobject]@{
            Root       = $root
            Bare       = $bare
            Main       = $main
            Runs       = $runs
            SeedRemote = $seedRemote
            Bin        = $bin
            OrigPath   = $origPath
        }
    }

    function Remove-BareRepoFixture {
        param([Parameter(Mandatory)][pscustomobject]$Fixture)
        if ($Fixture.OrigPath) {
            $env:PATH = $Fixture.OrigPath
        }
        $env:TWIG_FAKE_STATE = $null
        if (Test-Path $Fixture.Root) {
            Remove-Item -Path $Fixture.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# ════════════════════════════════════════════════════════════════════════════
# Phase 1-2: cwd + bare-repo preflight
# ════════════════════════════════════════════════════════════════════════════

Describe 'Invoke-PolyphonySdlc — repo-layout preflight' {

    It 'Throws when cwd is not inside a git repository' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "not-git-$([System.Guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        Push-Location $tmp
        try {
            { & $script:ScriptPath -ApexId 1234 -DryRun } |
                Should -Throw -ExpectedMessage '*Cwd is not inside a git repository*'
        } finally {
            Pop-Location
            Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Throws with bare-repo migration guidance when cwd is a non-bare clone' {
        # Set up a non-bare clone (legacy layout): no separate bare gitdir.
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "non-bare-$([System.Guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        & git init --quiet $tmp 2>&1 | Out-Null
        Push-Location $tmp
        try {
            { & $script:ScriptPath -ApexId 1234 -DryRun } |
                Should -Throw -ExpectedMessage '*Repo-layout preflight FAILED*'
        } finally {
            Pop-Location
            Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Error message links to migration script and layout doc' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "non-bare-msg-$([System.Guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        & git init --quiet $tmp 2>&1 | Out-Null
        Push-Location $tmp
        try {
            $err = $null
            try { & $script:ScriptPath -ApexId 1234 -DryRun } catch { $err = $_.Exception.Message }
            $err | Should -Match 'Migrate-ToBareRepo\.ps1'
            $err | Should -Match 'per-run-worktree-layout\.md'
        } finally {
            Pop-Location
            Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It '-SkipLayoutCheck bypasses the bare-repo gate (advanced escape hatch)' {
        # Non-bare clone with SkipLayoutCheck should NOT throw the layout
        # error — it'll fail later (init-apex needs a sane common-dir), but
        # the layout gate itself is bypassed.
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "skip-$([System.Guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        & git init --quiet $tmp 2>&1 | Out-Null
        Push-Location $tmp
        try {
            $err = $null
            try { & $script:ScriptPath -ApexId 1234 -DryRun -SkipLayoutCheck } catch { $err = $_.Exception.Message }
            # If layout check ran, we'd see "Repo-layout preflight FAILED".
            # Bypassed → some downstream error from init-apex (we accept any
            # other failure mode here).
            $err | Should -Not -Match 'Repo-layout preflight FAILED'
        } finally {
            Pop-Location
            Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# ════════════════════════════════════════════════════════════════════════════
# Phase 3-5: init-apex integration + WorktreeRoot derivation
# ════════════════════════════════════════════════════════════════════════════

Describe 'Invoke-PolyphonySdlc — init-apex + WorktreeRoot derivation' {

    BeforeEach { $script:fx = New-BareRepoFixture }
    AfterEach  { Remove-BareRepoFixture $script:fx }

    It 'Self-derives WorktreeRoot from init-apex (no -WorktreeRoot supplied)' {
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -DryRun | ConvertFrom-Json
            $r.success | Should -BeTrue
            $r.dry_run | Should -BeTrue
            $expected = [System.IO.Path]::GetFullPath((Join-Path $script:fx.Runs 'apex-9999/feature-9999')).TrimEnd('\','/')
            [System.IO.Path]::GetFullPath($r.worktree_root).TrimEnd('\','/') | Should -Be $expected
        } finally { Pop-Location }
    }

    It 'Surfaces main_worktree_path, runs_root, branch in the resolved envelope' {
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 7777 -DryRun | ConvertFrom-Json
            [System.IO.Path]::GetFullPath($r.main_worktree_path).TrimEnd('\','/') |
                Should -Be ([System.IO.Path]::GetFullPath($script:fx.Main).TrimEnd('\','/'))
            [System.IO.Path]::GetFullPath($r.runs_root).TrimEnd('\','/') |
                Should -Be ([System.IO.Path]::GetFullPath($script:fx.Runs).TrimEnd('\','/'))
            $r.branch | Should -Be 'feature/7777'
        } finally { Pop-Location }
    }

    It 'Reports init_apex_outcome=dry_run on -DryRun' {
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 5555 -DryRun | ConvertFrom-Json
            $r.init_apex_outcome | Should -Be 'dry_run'
        } finally { Pop-Location }
    }

    It 'Does not create the apex worktree on -DryRun' {
        Push-Location $script:fx.Main
        try {
            & $script:ScriptPath -ApexId 4444 -DryRun | Out-Null
            (Test-Path (Join-Path $script:fx.Runs 'apex-4444')) | Should -BeFalse
        } finally { Pop-Location }
    }

    It 'Accepts -WorktreeRoot override that matches the canonical apex path' {
        Push-Location $script:fx.Main
        try {
            $expected = Join-Path $script:fx.Runs 'apex-3333/feature-3333'
            $r = & $script:ScriptPath -ApexId 3333 -WorktreeRoot $expected -DryRun | ConvertFrom-Json
            $r.success | Should -BeTrue
        } finally { Pop-Location }
    }

    It 'Refuses -WorktreeRoot override that does not match the canonical apex path' {
        Push-Location $script:fx.Main
        try {
            $bogus = Join-Path $script:fx.Runs 'apex-9999/feature-9999'
            { & $script:ScriptPath -ApexId 3333 -WorktreeRoot $bogus -DryRun } |
                Should -Throw -ExpectedMessage '*does not match the canonical apex worktree*'
        } finally { Pop-Location }
    }
}

# ════════════════════════════════════════════════════════════════════════════
# Phase 4: hijack-refusal (-WorktreeRoot pointing at main)
# ════════════════════════════════════════════════════════════════════════════

Describe 'Invoke-PolyphonySdlc — hijack-refusal' {

    BeforeEach { $script:fx = New-BareRepoFixture }
    AfterEach  { Remove-BareRepoFixture $script:fx }

    It 'Refuses -WorktreeRoot pointing at the main worktree (would hijack)' {
        Push-Location $script:fx.Main
        try {
            { & $script:ScriptPath -ApexId 1111 -WorktreeRoot $script:fx.Main -DryRun } |
                Should -Throw -ExpectedMessage '*does not match the canonical apex worktree*'
        } finally { Pop-Location }
    }

    It 'Refuses -WorktreeRoot pointing inside the main worktree' {
        Push-Location $script:fx.Main
        try {
            $insideMain = Join-Path $script:fx.Main 'subdir'
            { & $script:ScriptPath -ApexId 1111 -WorktreeRoot $insideMain -DryRun } |
                Should -Throw -ExpectedMessage '*does not match the canonical apex worktree*'
        } finally { Pop-Location }
    }
}

# ════════════════════════════════════════════════════════════════════════════
# Phase 6: intent semantics
# ════════════════════════════════════════════════════════════════════════════

Describe 'Invoke-PolyphonySdlc — intent semantics' {

    BeforeEach { $script:fx = New-BareRepoFixture }
    AfterEach  { Remove-BareRepoFixture $script:fx }

    It 'Rejects invalid intent values via ValidateSet' {
        Push-Location $script:fx.Main
        try {
            { & $script:ScriptPath -ApexId 9999 -Intent garbage -DryRun } |
                Should -Throw
        } finally { Pop-Location }
    }

    It 'Defaults intent to "new"' {
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -DryRun | ConvertFrom-Json
            $r.intent | Should -Be 'new'
            $r.args | Should -Contain 'intent=new'
        } finally { Pop-Location }
    }

    It 'Honors -Intent replan' {
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -Intent replan -DryRun | ConvertFrom-Json
            $r.intent | Should -Be 'replan'
            $r.args | Should -Contain 'intent=replan'
        } finally { Pop-Location }
    }

    # Note: -Intent resume + outcome=created refusal is not testable in dry-run
    # because dry-run reports outcome=dry_run, not 'created'. The refusal logic
    # is unit-tested by a wet test (live init-apex creating the worktree, then
    # a follow-up resume that would fail). Skipped here to keep the suite fast
    # and side-effect-free; covered manually during dogfood.
}

# ════════════════════════════════════════════════════════════════════════════
# Command construction (preserved from prior tests)
# ════════════════════════════════════════════════════════════════════════════

Describe 'Invoke-PolyphonySdlc — command construction' {

    BeforeEach { $script:fx = New-BareRepoFixture }
    AfterEach  { Remove-BareRepoFixture $script:fx }

    It 'Targets the apex-driver workflow' {
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -DryRun | ConvertFrom-Json
            $r.workflow | Should -Be 'apex-driver@polyphony'
            $r.args[0] | Should -Be 'run'
            $r.args[1] | Should -Be 'apex-driver@polyphony'
        } finally { Pop-Location }
    }

    It 'Always includes --web' {
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -DryRun | ConvertFrom-Json
            $r.args | Should -Contain '--web'
        } finally { Pop-Location }
    }

    It 'Passes apex_id input' {
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -DryRun | ConvertFrom-Json
            $r.args | Should -Contain 'apex_id=9999'
        } finally { Pop-Location }
    }

    It 'Resolves organization + project from .twig/config in main worktree' {
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -DryRun | ConvertFrom-Json
            $r.args | Should -Contain 'organization=test-org'
            $r.args | Should -Contain 'project=TestProj'
            $r.project_url | Should -Be 'https://dev.azure.com/test-org/TestProj'
        } finally { Pop-Location }
    }

    It 'Includes all six -m metadata flags' {
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -DryRun | ConvertFrom-Json
            $mFlags = @()
            for ($i = 0; $i -lt $r.args.Count; $i++) {
                if ($r.args[$i] -eq '-m') { $mFlags += $r.args[$i + 1] }
            }
            $mFlags.Count | Should -Be 6
            ($mFlags | Where-Object { $_ -eq 'tracker=ado' }).Count | Should -Be 1
            ($mFlags | Where-Object { $_ -like 'project_url=https://dev.azure.com/test-org/TestProj' }).Count | Should -Be 1
            ($mFlags | Where-Object { $_ -like 'git_repo=*' }).Count | Should -Be 1
            ($mFlags | Where-Object { $_ -eq 'workitem_id=9999' }).Count | Should -Be 1
            ($mFlags | Where-Object { $_ -like 'worktree_name=feature-9999*' }).Count | Should -Be 1
            ($mFlags | Where-Object { $_ -like 'cwd=*feature-9999*' }).Count | Should -Be 1
        } finally { Pop-Location }
    }

    It 'Defaults git_repo to the canonical main worktree (NOT old sibling-name heuristic)' {
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -DryRun | ConvertFrom-Json
            [System.IO.Path]::GetFullPath($r.git_repo).TrimEnd('\','/') |
                Should -Be ([System.IO.Path]::GetFullPath($script:fx.Main).TrimEnd('\','/'))
        } finally { Pop-Location }
    }

    It '-GitRepo override beats default' {
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -GitRepo 'C:\custom\path' -DryRun | ConvertFrom-Json
            $r.git_repo | Should -Be 'C:\custom\path'
        } finally { Pop-Location }
    }

    It 'Returns dry_run=true and does not attempt to launch' {
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -DryRun | ConvertFrom-Json
            $r.dry_run | Should -BeTrue
            $r.PSObject.Properties.Name | Should -Not -Contain 'pid'
        } finally { Pop-Location }
    }

    It 'Renders an executable command string' {
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -DryRun | ConvertFrom-Json
            $r.command | Should -Match '^conductor run apex-driver@polyphony --web '
            $r.command | Should -Match 'apex_id=9999'
            $r.command | Should -Match 'workitem_id=9999'
        } finally { Pop-Location }
    }
}

# ════════════════════════════════════════════════════════════════════════════
# Platform + repository auto-detection from MAIN WORKTREE remote
# ════════════════════════════════════════════════════════════════════════════

Describe 'Invoke-PolyphonySdlc — git remote detection (from main worktree)' {

    AfterEach {
        if ($script:fx) { Remove-BareRepoFixture $script:fx }
    }

    It 'Detects platform=github from https github URL' {
        $script:fx = New-BareRepoFixture -RemoteUrl 'https://github.com/PolyphonyRequiem/polyphony.git'
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 1 -DryRun | ConvertFrom-Json
            $r.platform | Should -Be 'github'
            $r.repository | Should -Be 'PolyphonyRequiem/polyphony'
        } finally { Pop-Location }
    }

    It 'Detects platform=ado from dev.azure.com URL' {
        $script:fx = New-BareRepoFixture -RemoteUrl 'https://dev.azure.com/dangreen-msft/Polyphony/_git/polyphony'
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 1 -DryRun | ConvertFrom-Json
            $r.platform | Should -Be 'ado'
            $r.repository | Should -Be 'polyphony'
        } finally { Pop-Location }
    }

    It 'Detects platform=ado from legacy visualstudio.com URL' {
        $script:fx = New-BareRepoFixture -RemoteUrl 'https://contoso.visualstudio.com/MyProject/_git/MyRepo'
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 1 -DryRun | ConvertFrom-Json
            $r.platform | Should -Be 'ado'
            $r.repository | Should -Be 'MyRepo'
        } finally { Pop-Location }
    }

    It '-Platform override beats auto-detection' {
        $script:fx = New-BareRepoFixture -RemoteUrl 'https://github.com/owner/name'
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 1 -Platform ado -DryRun | ConvertFrom-Json
            $r.platform | Should -Be 'ado'
            $r.repository | Should -Be 'owner/name'
        } finally { Pop-Location }
    }

    It '-Repository override beats auto-detection' {
        $script:fx = New-BareRepoFixture -RemoteUrl 'https://github.com/owner/name'
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 1 -Repository 'override/repo' -DryRun | ConvertFrom-Json
            $r.platform | Should -Be 'github'
            $r.repository | Should -Be 'override/repo'
        } finally { Pop-Location }
    }
}

# ════════════════════════════════════════════════════════════════════════════
# .twig/ propagation
# ════════════════════════════════════════════════════════════════════════════

Describe 'Invoke-PolyphonySdlc — .twig/ propagation from main' {

    BeforeEach { $script:fx = New-BareRepoFixture }
    AfterEach  { Remove-BareRepoFixture $script:fx }

    It 'Throws clear error when main worktree has no .twig/ directory' {
        # Remove the .twig/ that the fixture created.
        Remove-Item -Path (Join-Path $script:fx.Main '.twig') -Recurse -Force
        Push-Location $script:fx.Main
        try {
            { & $script:ScriptPath -ApexId 9999 -DryRun } |
                Should -Throw -ExpectedMessage '*No .twig/ directory found in main worktree*'
        } finally { Pop-Location }
    }

    It 'Throws when .twig/ exists but config is missing' {
        Remove-Item -Path (Join-Path $script:fx.Main '.twig/config') -Force
        Push-Location $script:fx.Main
        try {
            { & $script:ScriptPath -ApexId 9999 -DryRun } |
                Should -Throw -ExpectedMessage '*config file is missing*'
        } finally { Pop-Location }
    }

    It 'Throws when twig config is missing organization' {
        $bad = @{ organization = ''; project = 'TestProj' } | ConvertTo-Json -Compress
        Set-Content -Path (Join-Path $script:fx.Main '.twig/config') -Value $bad
        Push-Location $script:fx.Main
        try {
            { & $script:ScriptPath -ApexId 9999 -DryRun } |
                Should -Throw -ExpectedMessage "*missing 'organization'*"
        } finally { Pop-Location }
    }
}

# ════════════════════════════════════════════════════════════════════════════
# Phase 2.5: terminal-state pre-flight refusal (AB#3165 Item 2)
# ════════════════════════════════════════════════════════════════════════════

Describe 'Invoke-PolyphonySdlc — terminal-state pre-flight (AB#3165)' {

    BeforeEach {
        $script:fx = New-BareRepoFixture
        $env:TWIG_FAKE_STATE = $null
    }
    AfterEach {
        $env:TWIG_FAKE_STATE = $null
        Remove-BareRepoFixture $script:fx
    }

    It 'Refuses to dispatch when work item is in Done state' {
        $env:TWIG_FAKE_STATE = 'Done'
        Push-Location $script:fx.Main
        try {
            { & $script:ScriptPath -ApexId 9999 -DryRun } |
                Should -Throw -ExpectedMessage "*terminal state 'Done'*"
        } finally { Pop-Location }
    }

    It 'Refuses to dispatch when work item is Closed' {
        $env:TWIG_FAKE_STATE = 'Closed'
        Push-Location $script:fx.Main
        try {
            { & $script:ScriptPath -ApexId 9999 -DryRun } |
                Should -Throw -ExpectedMessage "*terminal state 'Closed'*"
        } finally { Pop-Location }
    }

    It 'Refuses to dispatch when work item is Removed' {
        $env:TWIG_FAKE_STATE = 'Removed'
        Push-Location $script:fx.Main
        try {
            { & $script:ScriptPath -ApexId 9999 -DryRun } |
                Should -Throw -ExpectedMessage "*terminal state 'Removed'*"
        } finally { Pop-Location }
    }

    It 'Refuses to dispatch when work item is Resolved' {
        $env:TWIG_FAKE_STATE = 'Resolved'
        Push-Location $script:fx.Main
        try {
            { & $script:ScriptPath -ApexId 9999 -DryRun } |
                Should -Throw -ExpectedMessage "*terminal state 'Resolved'*"
        } finally { Pop-Location }
    }

    It 'Allows dispatch when work item is in non-terminal state (To Do)' {
        $env:TWIG_FAKE_STATE = 'To Do'
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -DryRun | ConvertFrom-Json
            $r.dry_run | Should -BeTrue
        } finally { Pop-Location }
    }

    It 'Allows dispatch when work item is Active' {
        $env:TWIG_FAKE_STATE = 'Active'
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -DryRun | ConvertFrom-Json
            $r.dry_run | Should -BeTrue
        } finally { Pop-Location }
    }

    It 'Refusal message points to twig state remediation' {
        $env:TWIG_FAKE_STATE = 'Done'
        Push-Location $script:fx.Main
        try {
            { & $script:ScriptPath -ApexId 9999 -DryRun } |
                Should -Throw -ExpectedMessage "*twig state 9999 'To Do'*"
        } finally { Pop-Location }
    }

    It 'Refusal message points to polyphony reset --root-id remediation' {
        $env:TWIG_FAKE_STATE = 'Done'
        Push-Location $script:fx.Main
        try {
            { & $script:ScriptPath -ApexId 9999 -DryRun } |
                Should -Throw -ExpectedMessage "*polyphony reset --root-id 9999*"
        } finally { Pop-Location }
    }

    It 'Refusal message points to AB#3165 epic' {
        $env:TWIG_FAKE_STATE = 'Done'
        Push-Location $script:fx.Main
        try {
            { & $script:ScriptPath -ApexId 9999 -DryRun } |
                Should -Throw -ExpectedMessage '*AB#3165*'
        } finally { Pop-Location }
    }

    It '-Intent resume bypasses the terminal-state check' {
        $env:TWIG_FAKE_STATE = 'Done'
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -Intent resume -DryRun | ConvertFrom-Json
            $r.dry_run | Should -BeTrue
        } finally { Pop-Location }
    }

    It '-Intent replan bypasses the terminal-state check' {
        $env:TWIG_FAKE_STATE = 'Done'
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -Intent replan -DryRun | ConvertFrom-Json
            $r.dry_run | Should -BeTrue
        } finally { Pop-Location }
    }

    It '-SkipStateCheck bypasses the terminal-state check' {
        $env:TWIG_FAKE_STATE = 'Done'
        Push-Location $script:fx.Main
        try {
            $r = & $script:ScriptPath -ApexId 9999 -SkipStateCheck -DryRun | ConvertFrom-Json
            $r.dry_run | Should -BeTrue
        } finally { Pop-Location }
    }
}
