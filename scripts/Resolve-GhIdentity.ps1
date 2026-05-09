#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Auto-detect, capture, and validate the gh CLI identity for a polyphony SDLC run.

.DESCRIPTION
    Captures whichever gh user is "active" at the moment of the call, then
    validates the captured token by calling the GitHub API. The validated
    identity is returned to the caller, which is expected to pin it into the
    process environment (GH_TOKEN + GH_HOST) for the lifetime of the run.

    Why pin: the conductor + polyphony subprocess tree is long-running. A
    competing worker (e.g. another agent in a sibling worktree) calling
    `gh auth switch` mid-run can flip the "active" gh user out from under us.
    Without pinning, every subsequent `gh api …` invocation re-resolves the
    active user — and if that user has no token cached for github.com, gh
    falls through to the DPAPI keyring, finds nothing in a non-TTY context,
    and hangs the per-attempt timeout (60s × 3 retries per call).

    By pinning GH_TOKEN at launcher startup and validating it once, the
    subprocess tree is immune to that drift.

.PARAMETER TimeoutSeconds
    Per-gh-call hard timeout. Default 30s.

.OUTPUTS
    A PSCustomObject with these properties:
      - User        : the GitHub login the captured token authenticates as.
      - Token       : the captured token (treat as secret).
      - TokenLength : length of the token (for safe diagnostic emission).
      - Source      : 'env' if GH_TOKEN was already set at entry,
                      'gh-keyring' if captured fresh from gh's auth store.

.NOTES
    Throws with an actionable error message on any failure — gh missing,
    no token cached, validation rejected by GitHub, or per-call timeout.
    Callers should treat the throw as a launch-blocker and surface the
    error message to the operator (it includes the remediation command).
#>

function Invoke-GhWithTimeout {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$TimeoutSeconds = 30,
        [hashtable]$ExtraEnv = @{}
    )

    $ghCmd = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $ghCmd) {
        return [pscustomobject]@{
            Succeeded = $false
            ExitCode  = -1
            Stdout    = ''
            Stderr    = 'gh CLI not found on PATH'
        }
    }

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $ghCmd.Source
    foreach ($arg in $Arguments) { [void]$psi.ArgumentList.Add($arg) }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.UseShellExecute        = $false
    $psi.CreateNoWindow         = $true
    foreach ($k in $ExtraEnv.Keys) {
        # ProcessStartInfo.Environment starts seeded with the parent's env.
        # Setting a key overrides for the child; null removes.
        if ($null -eq $ExtraEnv[$k]) {
            [void]$psi.Environment.Remove($k)
        } else {
            $psi.Environment[$k] = [string]$ExtraEnv[$k]
        }
    }

    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    [void]$proc.Start()

    # Drain output asynchronously to avoid pipe deadlock when stdout/stderr
    # exceed the OS pipe buffer (rare for our calls — token + login text only —
    # but cheap insurance against future caller adding bigger calls).
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()

    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
        try { $proc.Kill($true) } catch { }
        return [pscustomobject]@{
            Succeeded = $false
            ExitCode  = -1
            Stdout    = ''
            Stderr    = "gh $($Arguments -join ' ') timed out after ${TimeoutSeconds}s"
        }
    }

    return [pscustomobject]@{
        Succeeded = ($proc.ExitCode -eq 0)
        ExitCode  = $proc.ExitCode
        Stdout    = $stdoutTask.GetAwaiter().GetResult().Trim()
        Stderr    = $stderrTask.GetAwaiter().GetResult().Trim()
    }
}

function Resolve-GhIdentity {
    [CmdletBinding()]
    param(
        [int]$TimeoutSeconds = 30
    )

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw @"
gh CLI not found on PATH.

The polyphony SDLC launcher needs the gh CLI to authenticate against GitHub.
Either install it (https://cli.github.com/) or relaunch with -Platform ado
if this run targets Azure DevOps.
"@
    }

    # ─── Capture the token ──────────────────────────────────────────────────
    # If the caller already set GH_TOKEN, treat that as authoritative — they
    # know their environment (CI workflows do this; local dev usually doesn't).
    # Either way, the next step VALIDATES the token, so we can't silently
    # accept a bogus one.
    $token = $null
    $tokenSource = $null

    if ($env:GH_TOKEN) {
        $token = $env:GH_TOKEN
        $tokenSource = 'env'
    } else {
        $tokenResult = Invoke-GhWithTimeout `
            -Arguments @('auth', 'token', '--hostname', 'github.com') `
            -TimeoutSeconds $TimeoutSeconds

        if (-not $tokenResult.Succeeded) {
            throw @"
gh auth token --hostname github.com failed (exit $($tokenResult.ExitCode)).

Stderr: $($tokenResult.Stderr)

Run: gh auth login --hostname github.com
"@
        }

        if ([string]::IsNullOrWhiteSpace($tokenResult.Stdout)) {
            throw @"
gh has no token cached for github.com.

Run: gh auth login --hostname github.com
"@
        }

        if ($tokenResult.Stdout -match '\s') {
            throw "gh auth token returned unexpected whitespace; refusing to use it."
        }

        $token = $tokenResult.Stdout
        $tokenSource = 'gh-keyring'
    }

    # ─── Validate the token ─────────────────────────────────────────────────
    # Single live API call with ONLY the captured token in env. If GitHub
    # accepts it and returns a login, that login is the authoritative identity
    # for the run (and we surface it via the diagnostic emit upstream).
    # We pass GH_HOST=github.com so gh can't fall through to a different host.
    $validation = Invoke-GhWithTimeout `
        -Arguments @('api', 'user', '--jq', '.login') `
        -TimeoutSeconds $TimeoutSeconds `
        -ExtraEnv @{
            GH_TOKEN = $token
            GH_HOST  = 'github.com'
        }

    if (-not $validation.Succeeded -or [string]::IsNullOrWhiteSpace($validation.Stdout)) {
        $tokenLen = $token.Length
        throw @"
gh token validation failed (exit $($validation.ExitCode), token source: $tokenSource, token length: $tokenLen).

The captured token was rejected by the GitHub API. Common causes:
  - token expired
  - insufficient scopes (need 'repo' for PR operations)
  - wrong account is active

Stderr: $($validation.Stderr)

Run: gh auth status
Then: gh auth refresh --hostname github.com -s repo
"@
    }

    return [pscustomobject]@{
        User        = $validation.Stdout.Trim()
        Token       = $token
        TokenLength = $token.Length
        Source      = $tokenSource
    }
}
