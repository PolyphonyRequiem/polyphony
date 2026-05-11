#requires -Version 7.0

<#
.SYNOPSIS
    Pins the open-questions re-entry guard in architect-plan-level.md.

.DESCRIPTION
    The plan-level workflow routes from open_questions_gate (when the user
    picks "Answer") through the open_questions_answer_counter step before
    re-entering the architect. That means on re-entry,
    `context.history[-1]` is the counter step, not the gate.

    A previous version of the prompt guarded the answers branch on
    `last == "open_questions_gate"`, which silently never matched after
    the counter was added. Result: the architect re-ran the default
    first-iteration prompt, ignored the user's answers, and re-asked the
    same questions. This was reported during the AB#3071 dogfood on
    2026-05-11.

    These tests are structural assertions against the prompt text. They
    prevent regression to the brittle history-position guard.
#>

BeforeAll {
    $script:PromptPath = Join-Path $PSScriptRoot '..' '.conductor' 'registry' 'prompts' 'architect-plan-level.md'
    $script:PromptText = Get-Content -Raw -LiteralPath $script:PromptPath
}

Describe 'architect-plan-level.md open-questions re-entry guard' {

    It 'has exactly one open-questions re-entry block' {
        $matches = [regex]::Matches($script:PromptText, 'You Are Being Re-Invoked With User Answers')
        $matches.Count | Should -Be 1
    }

    It 'guards on open_questions_gate.output.answers existence' {
        $script:PromptText | Should -Match 'open_questions_gate\.output\.answers is defined'
        $script:PromptText | Should -Match 'open_questions_gate\.output\.selected == "answer"'
    }

    It 'does NOT use the brittle "last == open_questions_gate" only guard' {
        # The naked single-step guard is what regressed AB#3071. If the
        # guard accepts only `last == "open_questions_gate"` with NO
        # alternative for the interposed counter step, fail.
        $headerIdx = $script:PromptText.IndexOf('You Are Being Re-Invoked With User Answers')
        $headerIdx | Should -BeGreaterThan -1

        # Find the LAST {% elif ... %} block before the header.
        $slice = $script:PromptText.Substring(0, $headerIdx)
        $elifMatches = [regex]::Matches($slice, '(?s)\{%\s*elif\b.*?%\}')
        $elifMatches.Count | Should -BeGreaterThan 0 -Because 'the answers branch must be introduced by an {% elif %} guard'

        $guard = $elifMatches[$elifMatches.Count - 1].Value

        if ($guard -match 'last\s*==\s*"open_questions_gate"') {
            # If the guard mentions the gate name, it must ALSO mention
            # the counter (or use `last in (...)`) — otherwise the
            # interposed counter step will defeat it.
            ($guard -match 'open_questions_answer_counter') | Should -BeTrue -Because 'guard must accept the interposed counter step as last-step name, OR drop the last-step check entirely'
        }
    }

    It 'accepts open_questions_answer_counter as a valid prior step on re-entry' {
        $headerIdx = $script:PromptText.IndexOf('You Are Being Re-Invoked With User Answers')
        $slice = $script:PromptText.Substring(0, $headerIdx)
        $elifMatches = [regex]::Matches($slice, '(?s)\{%\s*elif\b.*?%\}')
        $elifMatches.Count | Should -BeGreaterThan 0
        $elifMatches[$elifMatches.Count - 1].Value | Should -Match 'open_questions_answer_counter'
    }
}
