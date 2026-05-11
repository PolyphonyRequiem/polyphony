#requires -Version 7.0

<#
.SYNOPSIS
    Pins the open-questions re-entry guard in architect-plan-level.md
    AND the structural invariant that no step interposes between
    open_questions_gate and architect on the answer route.

.DESCRIPTION
    The plan-level workflow's `open_questions_gate` routes its "Answer"
    option directly to `architect`. With nothing between the two,
    `context.history[-1]` on architect re-entry is reliably
    `"open_questions_gate"`, and the architect's re-entry guard can use
    that simple equality check.

    Earlier versions of the workflow interposed an
    `open_questions_answer_counter` step that incremented a temp-file
    counter. That step (a) defeated the simple `last == "open_questions_gate"`
    guard so the architect ignored the user's answers, and (b) wrote a
    temp file that survived across runs of the same work item,
    silently capping the loop on the next planning attempt. Both
    pathologies were observed in the AB#3071 dogfood on 2026-05-11.

    The fix: count gate appearances directly from `context.history` in
    `open_questions_counter` (run-scoped, no temp file), and route Answer
    straight to architect (no interposed step). These tests pin both
    halves so neither half can regress without the other being noticed.
#>

BeforeAll {
    $script:PromptPath = Join-Path $PSScriptRoot '..' '.conductor' 'registry' 'prompts' 'architect-plan-level.md'
    $script:WorkflowPath = Join-Path $PSScriptRoot '..' '.conductor' 'registry' 'workflows' 'plan-level.yaml'
    $script:PromptText = Get-Content -Raw -LiteralPath $script:PromptPath
    $script:WorkflowText = Get-Content -Raw -LiteralPath $script:WorkflowPath
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

    It 'guards on last == "open_questions_gate" (paired with the workflow invariant tested below)' {
        $headerIdx = $script:PromptText.IndexOf('You Are Being Re-Invoked With User Answers')
        $headerIdx | Should -BeGreaterThan -1
        $slice = $script:PromptText.Substring(0, $headerIdx)
        $elifMatches = [regex]::Matches($slice, '(?s)\{%\s*elif\b.*?%\}')
        $elifMatches.Count | Should -BeGreaterThan 0
        $guard = $elifMatches[$elifMatches.Count - 1].Value
        $guard | Should -Match 'last\s*==\s*"open_questions_gate"' -Because 'workflow routes Answer directly to architect, so the simple last-step check is correct (and the structural test below pins that)'
    }
}

Describe 'plan-level.yaml open_questions_gate Answer route' {

    It 'routes Answer directly to architect (no interposed step)' {
        # Slice the open_questions_gate step (anchored to its `- name:`
        # at the same indent as other top-level agents).
        $gatePattern = '(?ms)^  - name: open_questions_gate\b.*?(?=^  - name: )'
        $gateMatch = [regex]::Match($script:WorkflowText, $gatePattern)
        $gateMatch.Success | Should -BeTrue -Because 'open_questions_gate must be defined as a top-level agent'

        $gateBlock = $gateMatch.Value

        # The Answer option's route line.
        $answerRoute = [regex]::Match(
            $gateBlock,
            '(?ms)^\s+- label:\s*"💬 Answer".*?route:\s*(\S+)')
        $answerRoute.Success | Should -BeTrue -Because 'gate must define an Answer option with a route'
        $answerRoute.Groups[1].Value | Should -Be 'architect' -Because 'Answer must go straight to architect; an interposed step (e.g. a counter) would defeat the architect re-entry guard and silently lose user answers'
    }

    It 'open_questions_answer_counter step does NOT exist' {
        # The historical counter step was a temp-file mechanism that
        # survived across runs and stale-capped the loop. Any
        # reintroduction must come with a new design that does not have
        # both pathologies — and must update this test.
        $script:WorkflowText | Should -Not -Match '(?m)^  - name: open_questions_answer_counter\b' -Because 'history-derived counter (open_questions_counter via context.history) replaces the temp-file approach; the answer_counter step is no longer needed and was a stale-state hazard'
    }

    It 'open_questions_counter derives count from context.history (no temp file)' {
        $counterPattern = '(?ms)^  - name: open_questions_counter\b.*?(?=^  - name: )'
        $counterMatch = [regex]::Match($script:WorkflowText, $counterPattern)
        $counterMatch.Success | Should -BeTrue
        $counterBlock = $counterMatch.Value

        # Must use context.history-based counting.
        $counterBlock | Should -Match "context\.history\s*\|\s*select\(\s*'eq'\s*,\s*'open_questions_gate'\s*\)" -Because 'counter must derive iteration from context.history (run-scoped), not from a temp file (which survives across runs of the same work item and stale-caps the loop)'

        # Must NOT use the temp-file path that caused AB#3071.
        $counterBlock | Should -Not -Match 'conductor-oq-loops' -Because 'temp-file counter survived across runs; replaced by history-derived count'
    }
}
