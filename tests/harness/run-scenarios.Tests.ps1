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

    $script:Python = if ($env:HARNESS_PYTHON) {
        $env:HARNESS_PYTHON
    } elseif (Test-Path 'C:\Users\dangreen\projects\conductor\.venv\Scripts\python.exe') {
        'C:\Users\dangreen\projects\conductor\.venv\Scripts\python.exe'
    } else {
        'python'
    }
}

Describe 'polyphony harness scenarios' {
    It 'has at least one discoverable scenario' -ForEach @(@{ Count = $scenarioDirs.Count }) {
        $Count | Should -BeGreaterThan 0
    }

    It 'passes scenario <_.Name>' -ForEach $scenarioDirs {
        $scenarioDir = $_.FullName
        $scenarioName = $_.Name
        $resultPath  = Join-Path ([System.IO.Path]::GetTempPath()) ("harness-{0}-{1}.json" -f $scenarioName, ([guid]::NewGuid().ToString('N')))

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
