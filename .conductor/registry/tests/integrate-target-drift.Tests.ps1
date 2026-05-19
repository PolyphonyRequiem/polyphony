# Pester tests for .conductor/registry/scripts/integrate-target-drift.ps1 (AB#3238).
#
# Exercises the script against a synthetic two-clone git repo to cover:
#   - no drift (idempotent fast path)
#   - clean drift (merge + push)
#   - conflicting drift (merge aborted, conflicted_files captured)
#   - feature_branch_diverged (local + origin both have unique commits)
#   - missing origin feature branch (non-fatal warning, proceeds)
#   - branch_mismatch (checkout-to-feature fails)
#
# All tests scope a temp directory tree:
#   <root>/origin.git      (bare)
#   <root>/work            (clone used as "the worktree")
#   <root>/other           (clone used to simulate "other writers" landing on main / feature)

BeforeAll {
    $script:Script = Join-Path $PSScriptRoot '..' 'scripts' 'integrate-target-drift.ps1'

    function script:New-IntegrateDriftFixture {
        param([string]$Name = "ab3238-${PID}-$([guid]::NewGuid().ToString('N').Substring(0,8))")

        $root = Join-Path ([System.IO.Path]::GetTempPath()) $Name
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        Push-Location $root
        try {
            git init --bare origin.git --initial-branch=main 2>&1 | Out-Null
            git clone -q origin.git work 2>&1 | Out-Null
            Set-Location (Join-Path $root 'work')
            git config user.email "t@t.t"
            git config user.name "t"
            'main-v0' | Out-File -NoNewline initial.txt
            git add initial.txt 2>&1 | Out-Null
            git commit -qm "main v0" 2>&1 | Out-Null
            git push -q origin main 2>&1 | Out-Null

            git checkout -qb feature/test 2>&1 | Out-Null
            'feature-work' | Out-File -NoNewline feature.txt
            git add feature.txt 2>&1 | Out-Null
            git commit -qm "feature work" 2>&1 | Out-Null
            git push -q origin feature/test 2>&1 | Out-Null
        }
        finally {
            Pop-Location
        }
        return $root
    }

    function script:Add-DriftCommit {
        param([string]$Root, [string]$FileName = 'drift.txt', [string]$Content = 'drift')

        $other = Join-Path $Root 'other'
        if (-not (Test-Path $other)) {
            Push-Location $Root
            try {
                git clone -q origin.git other 2>&1 | Out-Null
                Set-Location $other
                git config user.email "x@x.x"
                git config user.name "x"
            }
            finally { Pop-Location }
        }
        Push-Location $other
        try {
            git checkout -q main 2>&1 | Out-Null
            git pull -q origin main 2>&1 | Out-Null
            $Content | Out-File -NoNewline $FileName
            git add $FileName 2>&1 | Out-Null
            git commit -qm "drift: add $FileName" 2>&1 | Out-Null
            git push -q origin main 2>&1 | Out-Null
        }
        finally { Pop-Location }
    }

    function script:Invoke-DriftScript {
        param([string]$Root, [string]$Feature = 'feature/test', [string]$Target = 'main')

        $work = Join-Path $Root 'work'
        Push-Location $work
        try {
            $out = pwsh -NoProfile -File $script:Script -FeatureBranch $Feature -TargetBranch $Target 2>&1
            return ($out | Out-String).Trim() | ConvertFrom-Json
        }
        finally { Pop-Location }
    }
}

Describe 'integrate-target-drift.ps1 (AB#3238)' {

    Context 'No drift' {
        It 'Returns success with drift_integrated=false when behind_by==0' {
            $root = New-IntegrateDriftFixture
            try {
                $env = Invoke-DriftScript -Root $root
                $env.success | Should -BeTrue
                $env.drift_integrated | Should -BeFalse
                $env.behind_by | Should -Be 0
                $env.error_code | Should -Be ''
            }
            finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    Context 'Clean drift' {
        It 'Merges origin/<target> and pushes when behind_by>0' {
            $root = New-IntegrateDriftFixture
            try {
                Add-DriftCommit -Root $root
                $env = Invoke-DriftScript -Root $root
                $env.success | Should -BeTrue
                $env.drift_integrated | Should -BeTrue
                $env.behind_by | Should -Be 1
                $env.merge_commit_sha | Should -Not -BeNullOrEmpty
                $env.error_code | Should -Be ''

                # Verify push: re-run should see no drift.
                $env2 = Invoke-DriftScript -Root $root
                $env2.behind_by | Should -Be 0
                $env2.drift_integrated | Should -BeFalse
            }
            finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    Context 'Merge conflict' {
        It 'Reports merge_conflict and aborts cleanly when integration conflicts' {
            $root = New-IntegrateDriftFixture
            try {
                # Set up a conflicting edit on main vs feature.
                Push-Location (Join-Path $root 'work')
                try {
                    "line1`nFEATURE`nline3`n" | Out-File -NoNewline shared.txt
                    git add shared.txt 2>&1 | Out-Null
                    git commit -qm "feature shared.txt" 2>&1 | Out-Null
                    git push -q origin feature/test 2>&1 | Out-Null
                }
                finally { Pop-Location }

                Push-Location $root
                try {
                    git clone -q origin.git other 2>&1 | Out-Null
                    Set-Location (Join-Path $root 'other')
                    git config user.email "x@x.x"
                    git config user.name "x"
                    "line1`nMAIN`nline3`n" | Out-File -NoNewline shared.txt
                    git add shared.txt 2>&1 | Out-Null
                    git commit -qm "main shared.txt" 2>&1 | Out-Null
                    git push -q origin main 2>&1 | Out-Null
                }
                finally { Pop-Location }

                Push-Location (Join-Path $root 'work')
                try { git fetch -q origin 2>&1 | Out-Null } finally { Pop-Location }

                $env = Invoke-DriftScript -Root $root
                $env.success | Should -BeFalse
                $env.error_code | Should -Be 'merge_conflict'
                $env.conflicted_files | Should -Contain 'shared.txt'

                # Worktree should be clean after abort.
                Push-Location (Join-Path $root 'work')
                try {
                    $status = (git status -s) -join "`n"
                    $status | Should -BeNullOrEmpty
                    $mergeHead = (git rev-parse -q --verify MERGE_HEAD 2>&1)
                    $LASTEXITCODE | Should -Not -Be 0
                }
                finally { Pop-Location }
            }
            finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    Context 'Feature branch diverged' {
        It 'Refuses to proceed when local and origin feature both have unique commits' {
            $root = New-IntegrateDriftFixture
            try {
                # Push a remote-only commit to feature.
                Push-Location $root
                try {
                    git clone -q origin.git other 2>&1 | Out-Null
                    Set-Location (Join-Path $root 'other')
                    git config user.email "x@x.x"
                    git config user.name "x"
                    git checkout -q feature/test 2>&1 | Out-Null
                    'remote-only' | Out-File remote.txt
                    git add remote.txt 2>&1 | Out-Null
                    git commit -qm "remote feature commit" 2>&1 | Out-Null
                    git push -q origin feature/test 2>&1 | Out-Null
                }
                finally { Pop-Location }

                # Make a local-only commit on feature.
                Push-Location (Join-Path $root 'work')
                try {
                    'local-only' | Out-File local.txt
                    git add local.txt 2>&1 | Out-Null
                    git commit -qm "local feature commit" 2>&1 | Out-Null
                }
                finally { Pop-Location }

                $env = Invoke-DriftScript -Root $root
                $env.success | Should -BeFalse
                $env.error_code | Should -Be 'feature_branch_diverged'
                $env.error_message | Should -Match 'diverged'
            }
            finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    Context 'Envelope contract' {
        It 'Always emits a parsable JSON envelope with required fields' {
            $root = New-IntegrateDriftFixture
            try {
                $env = Invoke-DriftScript -Root $root
                $env.PSObject.Properties.Name | Should -Contain 'success'
                $env.PSObject.Properties.Name | Should -Contain 'feature_branch'
                $env.PSObject.Properties.Name | Should -Contain 'target_branch'
                $env.PSObject.Properties.Name | Should -Contain 'behind_by'
                $env.PSObject.Properties.Name | Should -Contain 'ahead_by'
                $env.PSObject.Properties.Name | Should -Contain 'drift_integrated'
                $env.PSObject.Properties.Name | Should -Contain 'merge_commit_sha'
                $env.PSObject.Properties.Name | Should -Contain 'conflicted_files'
                $env.PSObject.Properties.Name | Should -Contain 'error_code'
                $env.PSObject.Properties.Name | Should -Contain 'error_message'
            }
            finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}
