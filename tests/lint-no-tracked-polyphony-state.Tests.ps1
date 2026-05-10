BeforeAll {
    $script:LintScriptPath = Join-Path $PSScriptRoot 'lint-no-tracked-polyphony-state.ps1'

    function New-TempRepo {
        param([scriptblock] $Setup)
        $repo = Join-Path ([System.IO.Path]::GetTempPath()) "no-tracked-polyphony-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $repo -Force | Out-Null
        Push-Location -LiteralPath $repo
        try {
            git init --quiet 2>&1 | Out-Null
            git config user.email "test@example.com"
            git config user.name "test"
            git config commit.gpgsign false
            & $Setup
        } finally {
            Pop-Location
        }
        return $repo
    }

    function Invoke-Lint {
        param([string] $RepoRoot)
        $output = pwsh -NoProfile -File $script:LintScriptPath -RepoRoot $RepoRoot 2>&1
        return @{ Output = ($output -join "`n"); ExitCode = $global:LASTEXITCODE }
    }
}

Describe 'lint-no-tracked-polyphony-state.ps1' {

    It 'PASSes when no .polyphony/ paths are tracked' {
        $repo = New-TempRepo {
            Set-Content -LiteralPath 'README.md' -Value '# test'
            git add README.md 2>&1 | Out-Null
            git commit --quiet -m 'init' 2>&1 | Out-Null
        }
        try {
            $r = Invoke-Lint -RepoRoot $repo
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'PASS'
        } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'PASSes when .polyphony/ exists on disk but is not tracked' {
        $repo = New-TempRepo {
            Set-Content -LiteralPath '.gitignore' -Value '.polyphony/'
            New-Item -ItemType Directory -Path '.polyphony' | Out-Null
            Set-Content -LiteralPath '.polyphony/run.yaml' -Value 'schema: 1'
            git add .gitignore 2>&1 | Out-Null
            git commit --quiet -m 'add gitignore' 2>&1 | Out-Null
        }
        try {
            $r = Invoke-Lint -RepoRoot $repo
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'PASS'
        } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'FAILs when .polyphony/run.yaml is tracked' {
        $repo = New-TempRepo {
            New-Item -ItemType Directory -Path '.polyphony' | Out-Null
            Set-Content -LiteralPath '.polyphony/run.yaml' -Value 'schema: 1'
            git add -f .polyphony/run.yaml 2>&1 | Out-Null
            git commit --quiet -m 'leak' 2>&1 | Out-Null
        }
        try {
            $r = Invoke-Lint -RepoRoot $repo
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'FAIL'
            $r.Output | Should -Match '.polyphony/run.yaml'
            $r.Output | Should -Match 'Rev 4.2'
        } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'FAILs when .polyphony/run.lock is tracked' {
        $repo = New-TempRepo {
            New-Item -ItemType Directory -Path '.polyphony' | Out-Null
            Set-Content -LiteralPath '.polyphony/run.lock' -Value 'pid: 1234'
            git add -f .polyphony/run.lock 2>&1 | Out-Null
            git commit --quiet -m 'leak' 2>&1 | Out-Null
        }
        try {
            $r = Invoke-Lint -RepoRoot $repo
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'FAIL'
            $r.Output | Should -Match '.polyphony/run.lock'
        } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'FAILs and lists every offending path when multiple are tracked' {
        $repo = New-TempRepo {
            New-Item -ItemType Directory -Path '.polyphony' | Out-Null
            Set-Content -LiteralPath '.polyphony/run.yaml' -Value 'schema: 1'
            Set-Content -LiteralPath '.polyphony/run.lock' -Value 'pid: 1'
            New-Item -ItemType Directory -Path '.polyphony/sub' | Out-Null
            Set-Content -LiteralPath '.polyphony/sub/extra.txt' -Value 'x'
            git add -f .polyphony 2>&1 | Out-Null
            git commit --quiet -m 'leak' 2>&1 | Out-Null
        }
        try {
            $r = Invoke-Lint -RepoRoot $repo
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match '.polyphony/run.yaml'
            $r.Output | Should -Match '.polyphony/run.lock'
            $r.Output | Should -Match '.polyphony/sub/extra.txt'
        } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'EXITs 2 when run outside a git repo' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "no-git-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        try {
            $r = Invoke-Lint -RepoRoot $tmp
            $r.ExitCode | Should -Be 2
            $r.Output | Should -Match 'FATAL'
        } finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
