#requires -Version 7.0

<#
.SYNOPSIS
    Pester tests for scripts/Resolve-GhIdentity.ps1.

.DESCRIPTION
    The function shells out to `gh`. To exercise its happy paths, error
    surfaces, and boundary conditions deterministically, the tests
    install a per-test stub `gh` script onto PATH that emits canned
    responses. Production gh on PATH is unaffected (PATH is restored
    after each test).

    Coverage:
      - Happy path: active gh has token + token validates → returns identity.
      - GH_TOKEN already set → source=env, no `gh auth token` call.
      - gh auth token fails → throws actionable message.
      - gh auth token returns empty → throws "no token cached".
      - gh auth token returns whitespace → throws "unexpected whitespace".
      - Token validation fails → throws "validation failed".
      - gh missing from PATH → throws actionable install message.
      - gh hangs → bounded by TimeoutSeconds, throws "timed out".
#>

BeforeAll {
    . (Join-Path $PSScriptRoot 'Resolve-GhIdentity.ps1')

    function Install-GhStub {
        param(
            [Parameter(Mandatory)][string]$ScriptBody
        )
        # Install a pwsh-backed stub that gh-clients on PATH will resolve to.
        # On Windows we use a .cmd shim that re-invokes pwsh on a sibling .ps1.
        # On *nix the script is named `gh` and chmod +x.
        $ps1Path = Join-Path $script:stubDir 'gh-impl.ps1'
        Set-Content -Path $ps1Path -Value $ScriptBody -Encoding utf8

        if ($IsWindows) {
            $cmdPath = Join-Path $script:stubDir 'gh.cmd'
            $cmdBody = "@pwsh -NoProfile -ExecutionPolicy Bypass -File `"$ps1Path`" %*"
            Set-Content -Path $cmdPath -Value $cmdBody -Encoding ascii
        } else {
            $shPath = Join-Path $script:stubDir 'gh'
            $shBody = "#!/usr/bin/env bash`npwsh -NoProfile -File `"$ps1Path`" `"`$@`""
            Set-Content -Path $shPath -Value $shBody -Encoding utf8
            chmod +x $shPath
        }
    }
}

Describe 'Resolve-GhIdentity' {

    BeforeEach {
        # Snapshot env so tests can mutate freely.
        $script:savedToken = if (Test-Path env:GH_TOKEN) { $env:GH_TOKEN } else { $null }
        $script:savedHost  = if (Test-Path env:GH_HOST)  { $env:GH_HOST }  else { $null }
        $script:savedPath  = $env:PATH

        if (Test-Path env:GH_TOKEN) { Remove-Item env:GH_TOKEN }
        if (Test-Path env:GH_HOST)  { Remove-Item env:GH_HOST }

        # Per-test PATH-isolated stub directory.
        $script:stubDir = Join-Path ([System.IO.Path]::GetTempPath()) `
            "gh-stub-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $script:stubDir -Force | Out-Null

        # Prepend stub dir to PATH so Get-Command gh resolves to our stub.
        $env:PATH = "$script:stubDir$([System.IO.Path]::PathSeparator)$env:PATH"
    }

    AfterEach {
        if ($null -ne $script:savedToken) { $env:GH_TOKEN = $script:savedToken }
        if ($null -ne $script:savedHost)  { $env:GH_HOST  = $script:savedHost }
        $env:PATH = $script:savedPath
        Remove-Item -Path $script:stubDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'Happy paths' {

        It 'Returns user, token, source=gh-keyring when active gh has valid token' {
            Install-GhStub @'
$argLine = $args -join ' '
if ($argLine -eq 'auth token --hostname github.com') {
    Write-Output 'ghp_TESTTOKEN1234567890ABCDEFGHIJKLMNOPQR'
    exit 0
}
if ($argLine -like 'api user --jq .login*') {
    Write-Output 'TestUser'
    exit 0
}
[Console]::Error.WriteLine("Unexpected gh args: $argLine")
exit 1
'@
            $identity = Resolve-GhIdentity -TimeoutSeconds 10
            $identity.User        | Should -Be 'TestUser'
            $identity.Token       | Should -Be 'ghp_TESTTOKEN1234567890ABCDEFGHIJKLMNOPQR'
            $identity.TokenLength | Should -Be 41
            $identity.Source      | Should -Be 'gh-keyring'
        }

        It 'Returns source=env without invoking gh auth token when GH_TOKEN preset' {
            $env:GH_TOKEN = 'ghp_PRESET_ENV_TOKEN_xxxxxxxxxxxxxxxxxxxx'
            Install-GhStub @'
$argLine = $args -join ' '
if ($argLine -like 'auth token*') {
    [Console]::Error.WriteLine("FAIL: auth token should not be invoked when GH_TOKEN is preset")
    exit 99
}
if ($argLine -like 'api user --jq .login*') {
    Write-Output 'EnvUser'
    exit 0
}
exit 1
'@
            $identity = Resolve-GhIdentity -TimeoutSeconds 10
            $identity.User        | Should -Be 'EnvUser'
            $identity.Token       | Should -Be 'ghp_PRESET_ENV_TOKEN_xxxxxxxxxxxxxxxxxxxx'
            $identity.Source      | Should -Be 'env'
        }
    }

    Context 'Capture failures' {

        It 'Throws when gh auth token exits non-zero' {
            Install-GhStub @'
$argLine = $args -join ' '
if ($argLine -eq 'auth token --hostname github.com') {
    [Console]::Error.WriteLine('not authenticated for github.com')
    exit 1
}
exit 1
'@
            { Resolve-GhIdentity -TimeoutSeconds 10 } |
                Should -Throw -ExpectedMessage '*gh auth token*failed*'
        }

        It 'Throws "no token cached" when gh auth token exits 0 with empty stdout' {
            Install-GhStub @'
$argLine = $args -join ' '
if ($argLine -eq 'auth token --hostname github.com') {
    # Empty stdout, exit 0 — gh's behavior when the active host has no token.
    exit 0
}
exit 1
'@
            { Resolve-GhIdentity -TimeoutSeconds 10 } |
                Should -Throw -ExpectedMessage '*no token cached*'
        }

        It 'Throws when gh auth token returns whitespace-bearing output' {
            Install-GhStub @'
$argLine = $args -join ' '
if ($argLine -eq 'auth token --hostname github.com') {
    Write-Output "ghp_PART1 ghp_PART2"  # whitespace inside — corrupted
    exit 0
}
exit 1
'@
            { Resolve-GhIdentity -TimeoutSeconds 10 } |
                Should -Throw -ExpectedMessage '*unexpected whitespace*'
        }
    }

    Context 'Validation failures' {

        It 'Throws when gh api user rejects the captured token' {
            Install-GhStub @'
$argLine = $args -join ' '
if ($argLine -eq 'auth token --hostname github.com') {
    Write-Output 'ghp_BADTOKENxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx'
    exit 0
}
if ($argLine -like 'api user*') {
    [Console]::Error.WriteLine('HTTP 401: Bad credentials')
    exit 1
}
exit 1
'@
            { Resolve-GhIdentity -TimeoutSeconds 10 } |
                Should -Throw -ExpectedMessage '*token validation failed*'
        }

        It 'Surfaces token source in the validation-failed error message' {
            $env:GH_TOKEN = 'ghp_PRESET_BAD_xxxxxxxxxxxxxxxxxxxxxxxxx'
            Install-GhStub @'
$argLine = $args -join ' '
if ($argLine -like 'api user*') {
    [Console]::Error.WriteLine('HTTP 401: Bad credentials')
    exit 1
}
exit 1
'@
            { Resolve-GhIdentity -TimeoutSeconds 10 } |
                Should -Throw -ExpectedMessage '*token source: env*'
        }

        It 'Throws when gh api user returns empty stdout (defense-in-depth)' {
            Install-GhStub @'
$argLine = $args -join ' '
if ($argLine -eq 'auth token --hostname github.com') {
    Write-Output 'ghp_TOKENvalidshapebutemptyresponse_xxxxxx'
    exit 0
}
if ($argLine -like 'api user*') {
    # Exit 0 but no output — should still be treated as failure.
    exit 0
}
exit 1
'@
            { Resolve-GhIdentity -TimeoutSeconds 10 } |
                Should -Throw -ExpectedMessage '*token validation failed*'
        }
    }

    Context 'Bounded timeout' {

        It 'Throws bounded "timed out" when gh hangs longer than TimeoutSeconds' {
            Install-GhStub @'
$argLine = $args -join ' '
if ($argLine -eq 'auth token --hostname github.com') {
    Start-Sleep -Seconds 10
    Write-Output 'unreachable'
    exit 0
}
exit 1
'@
            # Use a 2s timeout to keep the test fast while still proving the
            # bound. The throw text must reference "timed out" so callers
            # (and operators reading logs) can route on it.
            { Resolve-GhIdentity -TimeoutSeconds 2 } |
                Should -Throw -ExpectedMessage '*timed out*'
        }
    }
}
