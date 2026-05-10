# run-scenarios.Tests.ps1 — Pester wrapper for polyphony harness scenarios.
#
# Discovers every tests/harness/scenarios/<name>/scenario.yaml, invokes
# the Python driver against it, parses the resulting JSON, and asserts
# the scenario's `passed` flag.
#
# Required environment:
#   $env:HARNESS_PYTHON — absolute path to a Python interpreter that has
#                         conductor + ruamel.yaml importable.
#                         Falls back to `python` on PATH.

#Requires -Version 7

# Discovery-time setup: -ForEach runs at discovery, BeforeAll at run.
$harnessRoot   = $PSScriptRoot
$scenarioRoot  = Join-Path $harnessRoot 'scenarios'
$scenarioDirs  = @(Get-ChildItem -Path $scenarioRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path (Join-Path $_.FullName 'scenario.yaml') })

BeforeAll {
    $script:HarnessRoot = $PSScriptRoot
    $script:RepoRoot    = Resolve-Path (Join-Path $PSScriptRoot '..\..')

    # Resolve the python interpreter to use. CI sets HARNESS_PYTHON
    # explicitly; developer machines fall back to a local conductor venv;
    # otherwise we try `python` on PATH and skip if it can't import.
    $script:Python = if ($env:HARNESS_PYTHON) {
        $env:HARNESS_PYTHON
    } elseif (Test-Path 'C:\Users\dangreen\projects\conductor\.venv\Scripts\python.exe') {
        'C:\Users\dangreen\projects\conductor\.venv\Scripts\python.exe'
    } else {
        'python'
    }

    # Probe prereqs. The harness needs the `conductor` Python package and
    # `ruamel.yaml`. CI installs both via setup-python + pip; developer
    # machines either set HARNESS_PYTHON to a conductor-equipped venv or
    # rely on the well-known dev path below. When neither produces a
    # working interpreter, scenario tests skip with a diagnostic so a
    # missing local setup doesn't masquerade as a code failure.
    $cmd = Get-Command $script:Python -ErrorAction SilentlyContinue
    if (-not $cmd) {
        $script:PrereqReady  = $false
        $script:PrereqReason = "python interpreter '$($script:Python)' not found on PATH (set `$env:HARNESS_PYTHON to point at a conductor-equipped venv)"
    } else {
        $probe = & $script:Python -c "import conductor, ruamel.yaml" 2>&1
        if ($LASTEXITCODE -ne 0) {
            $script:PrereqReady  = $false
            $script:PrereqReason = "interpreter '$($script:Python)' missing imports: $probe"
        } else {
            $script:PrereqReady  = $true
            $script:PrereqReason = $null
        }
    }
}

Describe 'polyphony harness scenarios' {
    It 'has at least one discoverable scenario' -ForEach @(@{ Count = $scenarioDirs.Count }) {
        $Count | Should -BeGreaterThan 0
    }

    It 'passes scenario <_.Name>' -ForEach $scenarioDirs {
        if (-not $script:PrereqReady) {
            Set-ItResult -Skipped -Because $script:PrereqReason
            return
        }

        $scenarioDir  = $_.FullName
        $scenarioName = $_.Name
        $resultPath   = Join-Path ([System.IO.Path]::GetTempPath()) ("harness-{0}-{1}.json" -f $scenarioName, ([guid]::NewGuid().ToString('N')))

        Push-Location $script:RepoRoot
        try {
            $env:PYTHONPATH = $script:HarnessRoot
            & $script:Python -m driver.run $scenarioDir --output-json $resultPath *> $null
            $exit = $LASTEXITCODE
        }
        finally {
            Pop-Location
            Remove-Item Env:PYTHONPATH -ErrorAction SilentlyContinue
        }

        Test-Path $resultPath | Should -BeTrue -Because "driver should always emit a result file (exit was $exit)"

        $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
        Remove-Item -LiteralPath $resultPath -ErrorAction SilentlyContinue

        if (-not $result.passed) {
            $reason = "Scenario $scenarioName failed: driver_error='$($result.driver_error)'; failures=$($result.failures -join '; ')"
            $result.passed | Should -BeTrue -Because $reason
        }
        $exit | Should -Be 0
    }
}
