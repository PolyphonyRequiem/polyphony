<#
.SYNOPSIS
    IO helpers for conductor-invoked PowerShell scripts.
.DESCRIPTION
    Conductor invokes scripts via `pwsh -NoProfile -File <script>` and parses
    stdout as JSON to merge structured fields into the agent's output dict.
    Anything other than valid JSON on stdout silently breaks downstream Jinja
    routing (the dict ends up as just `{stdout, stderr, exit_code}` and any
    `output.<field>` lookup raises an Undefined error in route conditions).

    `Write-Warning` is the trap: under `pwsh -File` it writes to STDOUT with a
    `WARNING:` prefix, not to stderr. `Write-StderrWarning` routes the same
    operator-facing notice to stderr via [Console]::Error.WriteLine, keeping
    stdout clean for JSON parsing while preserving the `WARNING:` prefix for
    log scanners.

    Tests should `Mock Write-StderrWarning {}` and assert invocation rather
    than capturing console stderr (which is process-global and flaky under
    parallel test runs).
#>

if (-not (Get-Command Write-StderrWarning -ErrorAction SilentlyContinue)) {
    function Write-StderrWarning {
        [CmdletBinding()]
        param(
            [Parameter(Mandatory, Position = 0)]
            [string]$Message
        )

        [Console]::Error.WriteLine("WARNING: $Message")
    }
}
