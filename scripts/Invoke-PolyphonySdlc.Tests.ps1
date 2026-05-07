BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Invoke-PolyphonySdlc.ps1'

    function New-TestWorktree {
        param([hashtable]$Config = @{}, [string]$RemoteUrl)
        $merged = @{
            organization    = 'test-org'
            project         = 'TestProj'
            team            = ''
            processTemplate = 'Basic'
        }
        foreach ($k in $Config.Keys) { $merged[$k] = $Config[$k] }
        $path = Join-Path ([System.IO.Path]::GetTempPath()) "invoke-polyphony-test-$(Get-Random)"
        $twigDir = Join-Path $path '.twig'
        New-Item -ItemType Directory -Path $twigDir -Force | Out-Null
        Set-Content -Path (Join-Path $twigDir 'config') -Value ($merged | ConvertTo-Json -Compress)
        if ($RemoteUrl) {
            # `git remote get-url` reads from .git/config; init a real repo so the
            # wrapper's detection path is exercised end-to-end (no mocking).
            & git -C $path init -q
            & git -C $path remote add origin $RemoteUrl
        }
        return $path
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# Preflight validation
# ══════════════════════════════════════════════════════════════════════════════

Describe 'Invoke-PolyphonySdlc — preflight validation' {

    It 'Throws when WorktreeRoot does not exist' {
        $missing = Join-Path ([System.IO.Path]::GetTempPath()) "does-not-exist-$(Get-Random)"
        { & $script:ScriptPath -ApexId 1234 -WorktreeRoot $missing -DryRun } |
            Should -Throw -ExpectedMessage '*does not exist*'
    }

    It 'Throws when worktree has no .twig/ directory' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "invoke-no-twig-$(Get-Random)"
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        try {
            { & $script:ScriptPath -ApexId 1234 -WorktreeRoot $tmp -DryRun } |
                Should -Throw -ExpectedMessage '*Worktree preflight FAILED*'
        } finally {
            Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Throws when .twig/ exists but config is missing' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "invoke-no-config-$(Get-Random)"
        New-Item -ItemType Directory -Path (Join-Path $tmp '.twig') -Force | Out-Null
        try {
            { & $script:ScriptPath -ApexId 1234 -WorktreeRoot $tmp -DryRun } |
                Should -Throw -ExpectedMessage '*config file missing*'
        } finally {
            Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Throws when twig config is missing organization' {
        $tmp = New-TestWorktree -Config @{ organization = ''; project = 'TestProj' }
        try {
            { & $script:ScriptPath -ApexId 1234 -WorktreeRoot $tmp -DryRun } |
                Should -Throw -ExpectedMessage "*missing 'organization'*"
        } finally {
            Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# Command construction
# ══════════════════════════════════════════════════════════════════════════════

Describe 'Invoke-PolyphonySdlc — command construction (dry run)' {

    BeforeEach {
        $script:TempDir = New-TestWorktree
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Targets the apex-driver workflow' {
        $r = & $script:ScriptPath -ApexId 9999 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.workflow | Should -Be 'apex-driver@polyphony'
        $r.args[0] | Should -Be 'run'
        $r.args[1] | Should -Be 'apex-driver@polyphony'
    }

    It 'Always includes --web' {
        $r = & $script:ScriptPath -ApexId 9999 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.args | Should -Contain '--web'
    }

    It 'Passes apex_id input' {
        $r = & $script:ScriptPath -ApexId 9999 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.args | Should -Contain 'apex_id=9999'
    }

    It 'Defaults intent to "new"' {
        $r = & $script:ScriptPath -ApexId 9999 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.intent | Should -Be 'new'
        $r.args | Should -Contain 'intent=new'
    }

    It 'Honors -Intent override' {
        $r = & $script:ScriptPath -ApexId 9999 -WorktreeRoot $script:TempDir -Intent resume -DryRun | ConvertFrom-Json
        $r.intent | Should -Be 'resume'
        $r.args | Should -Contain 'intent=resume'
    }

    It 'Rejects invalid intent values via ValidateSet' {
        { & $script:ScriptPath -ApexId 9999 -WorktreeRoot $script:TempDir -Intent garbage -DryRun } |
            Should -Throw
    }

    It 'Defaults platform to "ado" when worktree has no git remote' {
        $r = & $script:ScriptPath -ApexId 9999 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.platform | Should -Be 'ado'
        $r.args | Should -Contain 'platform=ado'
    }

    It 'Honors -Platform override' {
        $r = & $script:ScriptPath -ApexId 9999 -WorktreeRoot $script:TempDir -Platform github -DryRun | ConvertFrom-Json
        $r.platform | Should -Be 'github'
        $r.args | Should -Contain 'platform=github'
    }

    It 'Rejects invalid -Platform values via ValidateSet' {
        { & $script:ScriptPath -ApexId 9999 -WorktreeRoot $script:TempDir -Platform gitlab -DryRun } |
            Should -Throw
    }

    It 'Defaults repository to "" when worktree has no git remote' {
        $r = & $script:ScriptPath -ApexId 9999 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.repository | Should -Be ''
        $r.args | Should -Contain 'repository='
    }

    It 'Honors -Repository override' {
        $r = & $script:ScriptPath -ApexId 9999 -WorktreeRoot $script:TempDir -Repository 'custom/repo' -DryRun | ConvertFrom-Json
        $r.repository | Should -Be 'custom/repo'
        $r.args | Should -Contain 'repository=custom/repo'
    }

    It 'Resolves organization + project from .twig/config' {
        $r = & $script:ScriptPath -ApexId 9999 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.args | Should -Contain 'organization=test-org'
        $r.args | Should -Contain 'project=TestProj'
        $r.project_url | Should -Be 'https://dev.azure.com/test-org/TestProj'
    }

    It 'Includes all six -m metadata flags' {
        $r = & $script:ScriptPath -ApexId 9999 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $mFlags = @()
        for ($i = 0; $i -lt $r.args.Count; $i++) {
            if ($r.args[$i] -eq '-m') { $mFlags += $r.args[$i + 1] }
        }
        $mFlags.Count | Should -Be 6
        ($mFlags | Where-Object { $_ -eq 'tracker=ado' }).Count | Should -Be 1
        ($mFlags | Where-Object { $_ -like 'project_url=https://dev.azure.com/test-org/TestProj' }).Count | Should -Be 1
        ($mFlags | Where-Object { $_ -like 'git_repo=*' }).Count | Should -Be 1
        ($mFlags | Where-Object { $_ -eq 'workitem_id=9999' }).Count | Should -Be 1
        ($mFlags | Where-Object { $_ -like 'worktree_name=*' }).Count | Should -Be 1
        ($mFlags | Where-Object { $_ -like 'cwd=*' }).Count | Should -Be 1
    }

    It 'Renders an executable command string' {
        $r = & $script:ScriptPath -ApexId 9999 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.command | Should -Match '^conductor run apex-driver@polyphony --web '
        $r.command | Should -Match 'apex_id=9999'
        $r.command | Should -Match 'workitem_id=9999'
    }

    It 'Returns dry_run=true and does not attempt to launch' {
        $r = & $script:ScriptPath -ApexId 9999 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.dry_run | Should -BeTrue
        $r.PSObject.Properties.Name | Should -Not -Contain 'pid'
    }

    It 'Resolves git_repo from <repo>-<id> worktree convention' {
        $parent = Join-Path ([System.IO.Path]::GetTempPath()) "wrapper-conv-$(Get-Random)"
        $repo   = Join-Path $parent 'polyphony'
        $wt     = Join-Path $parent 'polyphony-9999'
        New-Item -ItemType Directory -Path (Join-Path $repo '.git') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $wt '.twig') -Force | Out-Null
        Set-Content -Path (Join-Path $wt '.twig/config') `
            -Value '{"organization":"o","project":"p"}'
        try {
            $r = & $script:ScriptPath -ApexId 9999 -WorktreeRoot $wt -DryRun | ConvertFrom-Json
            $r.git_repo | Should -Be (Resolve-Path $repo).Path
        } finally {
            Remove-Item -Path $parent -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# Platform + repository auto-detection from git remote
# ══════════════════════════════════════════════════════════════════════════════

Describe 'Invoke-PolyphonySdlc — git remote detection' {

    AfterEach {
        if ($script:TempDir -and (Test-Path $script:TempDir)) {
            Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Detects platform=github + repository=<owner>/<name> from https github URL' {
        $script:TempDir = New-TestWorktree -RemoteUrl 'https://github.com/PolyphonyRequiem/polyphony.git'
        $r = & $script:ScriptPath -ApexId 1 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.platform | Should -Be 'github'
        $r.repository | Should -Be 'PolyphonyRequiem/polyphony'
        $r.args | Should -Contain 'platform=github'
        $r.args | Should -Contain 'repository=PolyphonyRequiem/polyphony'
    }

    It 'Detects platform=github from https URL without .git suffix' {
        $script:TempDir = New-TestWorktree -RemoteUrl 'https://github.com/owner/name'
        $r = & $script:ScriptPath -ApexId 1 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.platform | Should -Be 'github'
        $r.repository | Should -Be 'owner/name'
    }

    It 'Detects platform=github from ssh URL' {
        $script:TempDir = New-TestWorktree -RemoteUrl 'git@github.com:owner/name.git'
        $r = & $script:ScriptPath -ApexId 1 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.platform | Should -Be 'github'
        $r.repository | Should -Be 'owner/name'
    }

    It 'Detects platform=ado + repository=<repo> from dev.azure.com URL' {
        $script:TempDir = New-TestWorktree -RemoteUrl 'https://dev.azure.com/dangreen-msft/Polyphony/_git/polyphony'
        $r = & $script:ScriptPath -ApexId 1 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.platform | Should -Be 'ado'
        $r.repository | Should -Be 'polyphony'
    }

    It 'Detects platform=ado from dev.azure.com URL with embedded user' {
        $script:TempDir = New-TestWorktree -RemoteUrl 'https://user@dev.azure.com/org/proj/_git/myrepo'
        $r = & $script:ScriptPath -ApexId 1 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.platform | Should -Be 'ado'
        $r.repository | Should -Be 'myrepo'
    }

    It 'Detects platform=ado from legacy visualstudio.com URL' {
        $script:TempDir = New-TestWorktree -RemoteUrl 'https://contoso.visualstudio.com/MyProject/_git/MyRepo'
        $r = & $script:ScriptPath -ApexId 1 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.platform | Should -Be 'ado'
        $r.repository | Should -Be 'MyRepo'
    }

    It 'Detects platform=ado from ssh ADO URL' {
        $script:TempDir = New-TestWorktree -RemoteUrl 'git@ssh.dev.azure.com:v3/org/proj/myrepo'
        $r = & $script:ScriptPath -ApexId 1 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.platform | Should -Be 'ado'
        $r.repository | Should -Be 'myrepo'
    }

    It '-Platform override beats auto-detection' {
        $script:TempDir = New-TestWorktree -RemoteUrl 'https://github.com/owner/name'
        $r = & $script:ScriptPath -ApexId 1 -WorktreeRoot $script:TempDir -Platform ado -DryRun | ConvertFrom-Json
        $r.platform | Should -Be 'ado'
        # Repository auto-detection still happens (best-effort) even when platform
        # is overridden — the user is explicitly choosing the leg, not disabling
        # detection.
        $r.repository | Should -Be 'owner/name'
    }

    It '-Repository override beats auto-detection' {
        $script:TempDir = New-TestWorktree -RemoteUrl 'https://github.com/owner/name'
        $r = & $script:ScriptPath -ApexId 1 -WorktreeRoot $script:TempDir -Repository 'override/repo' -DryRun | ConvertFrom-Json
        $r.platform | Should -Be 'github'
        $r.repository | Should -Be 'override/repo'
    }

    It 'Falls back to platform=ado + repository="" when remote URL is unrecognized' {
        $script:TempDir = New-TestWorktree -RemoteUrl 'https://gitlab.example.com/owner/name.git'
        $r = & $script:ScriptPath -ApexId 1 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.platform | Should -Be 'ado'
        $r.repository | Should -Be ''
    }

    It 'Reports remote_url in the resolved envelope when present' {
        $script:TempDir = New-TestWorktree -RemoteUrl 'https://github.com/owner/name.git'
        $r = & $script:ScriptPath -ApexId 1 -WorktreeRoot $script:TempDir -DryRun | ConvertFrom-Json
        $r.remote_url | Should -Be 'https://github.com/owner/name.git'
    }
}
