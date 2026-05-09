#requires -Version 7.0

<#
.SYNOPSIS
    Pins the AB#3066 fix to apex-item-dispatch.yaml's `terminal_satisfied` step.

.DESCRIPTION
    `terminal_satisfied` wraps a twig+polyphony block in try/catch so that
    transient ADO/twig failures don't fail the entire wave (the apex-driver
    outer loop re-evaluates next-ready on the next pass). Before AB#3066 the
    catch was silent: no stderr write, no field surfaced in the structured
    output. The fix preserves the don't-fail-the-wave behavior while making
    every caught exception observable in two channels:

      1. stderr  — `[Console]::Error.WriteLine(...)` so the conductor event
                   log shows the failure.
      2. output  — conditional `error` + `error_code` fields so wave
                   aggregators can act on it.

    These tests are structural assertions against the YAML text. They prevent
    regression to the silent-swallow shape.
#>

BeforeAll {
    $script:WorkflowPath = Join-Path $PSScriptRoot '..' '.conductor' 'registry' 'workflows' 'apex-item-dispatch.yaml'
    $script:WorkflowYaml = Get-Content -Raw -LiteralPath $script:WorkflowPath

    # Slice out just the terminal_satisfied block (everything from the step
    # name to the next top-level step).
    $pattern = '(?ms)^  - name: terminal_satisfied\b.*?(?=^  - name: |\Z)'
    $match = [regex]::Match($script:WorkflowYaml, $pattern)
    if (-not $match.Success) {
        throw "Could not locate terminal_satisfied block in $script:WorkflowPath"
    }
    $script:Block = $match.Value
}

Describe 'apex-item-dispatch.yaml > terminal_satisfied — AB#3066 observable-error fix' {

    It 'has a try/catch wrapper (preserves "do not fail the wave" semantics)' {
        $script:Block | Should -Match 'try\s*\{'
        $script:Block | Should -Match '\}\s*catch\s*\{'
    }

    It 'captures the caught exception message into a variable' {
        # Must be `$_.Exception.Message`, not `$_` (which is the ErrorRecord
        # object) — the latter would serialize poorly into the output payload.
        $script:Block | Should -Match '\$errMsg\s*=\s*\$_\.Exception\.Message'
    }

    It 'writes the caught exception to stderr (visible in conductor event log)' {
        # Must be `[Console]::Error.WriteLine` — `Write-Error` would terminate
        # the script (defeating the don't-fail-the-wave intent), and a plain
        # `Write-Host` would not land on stderr.
        $script:Block | Should -Match '\[Console\]::Error\.WriteLine'
        $script:Block | Should -Not -Match 'Write-Error\b'
    }

    It 'surfaces the error in structured output via `error` and `error_code` fields' {
        # Match the existing terminal-error convention (e.g. terminal_classify_error,
        # terminal_spawn_error): a string `error` + a string `error_code`.
        $script:Block | Should -Match '\$out\.error\s*='
        $script:Block | Should -Match "\`$out\.error_code\s*=\s*'terminal_satisfied_caught'"
    }

    It 'only adds the error fields when an exception was actually caught' {
        # Conditional on `$errMsg -ne ''` so the happy path output stays clean
        # (no spurious empty error fields when nothing went wrong).
        $script:Block | Should -Match "if\s*\(\s*\`$errMsg\s+-ne\s+''\s*\)"
    }

    It 'does NOT rethrow inside the catch block (would fail the wave)' {
        # If a future edit adds `throw` inside the catch, the wave fails and
        # the outer loop re-evaluation is bypassed — that would be a regression
        # of the AB#3066 design, not just the observability fix.
        $catchPattern = '(?ms)\}\s*catch\s*\{(.*?)\}\s*\$out\s*='
        $catchMatch = [regex]::Match($script:Block, $catchPattern)
        $catchMatch.Success | Should -BeTrue
        $catchBody = $catchMatch.Groups[1].Value
        $catchBody | Should -Not -Match '^\s*throw\b'
    }
}
