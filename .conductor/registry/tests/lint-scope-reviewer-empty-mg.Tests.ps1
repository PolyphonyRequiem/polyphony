#requires -Modules Pester, powershell-yaml

# F8 regression: the scope_reviewer agent in implement-mg.yaml MUST
# instruct the LLM to fail loudly on an empty merge group instead of
# rationalizing the void and punting to the user_acceptance gate.
#
# Surface that motivated this check: AB#3064 dogfood 2026-05-09 — the
# scope_reviewer observed `0 tasks, no mg/3064_pg-0 branch exists in the
# repo` accurately, then approved with `the human reviewer should
# confirm at user_acceptance that the apex requirements really are
# covered elsewhere`. That's the wrong layer for that confirmation; the
# MG layer is responsible for the work it was dispatched with, and an
# empty MG is a bug somewhere upstream that the reviewer must surface
# as `changes_requested`, not green-wash.
#
# These assertions intentionally tie the lint to specific phrases in the
# prompt so the protective language can't drift away under future edits
# without an explicit lint update.

Describe 'implement-mg.yaml — scope_reviewer empty-MG structural check (F8)' {

    BeforeAll {
        $script:WorkflowPath = Join-Path $PSScriptRoot '..' 'workflows' 'implement-mg.yaml'
        $script:WorkflowPath | Should -Exist
        $raw = Get-Content -Raw $script:WorkflowPath
        $script:Yaml = ConvertFrom-Yaml $raw
        # `agents:` is a TOP-LEVEL key in this workflow YAML (not nested
        # under `workflow:` like name/version/entry_point).
        $script:ScopeReviewer = $script:Yaml.agents | Where-Object { $_.name -eq 'scope_reviewer' } | Select-Object -First 1
        $script:ScopeReviewer | Should -Not -BeNullOrEmpty -Because 'scope_reviewer agent must exist in implement-mg.yaml'
        $script:Prompt = [string]$script:ScopeReviewer.prompt
    }

    It 'scope_reviewer prompt names the empty-MG structural check explicitly' {
        $script:Prompt | Should -Match 'empty[- ]?MG' -Because (
            'reviewer must be told to look for an empty merge group as a structural condition, not just a soft scope concern')
        $script:Prompt | Should -Match 'empty_merge_group_structural_violation' -Because (
            'the violation tag is the wire-level handle the gate downstream uses to identify this failure mode')
    }

    It 'scope_reviewer prompt directs verdict=changes_requested on empty MG' {
        $script:Prompt | Should -Match 'changes_requested' -Because (
            'the reviewer must request changes on empty MGs; defaulting to approved (with a hopeful note) green-washes the upstream bug')
    }

    It 'scope_reviewer prompt forbids punting to user_acceptance on empty MG' {
        # The protective language must explicitly discourage the rationalization
        # observed in the AB#3064 dogfood ("the human reviewer should confirm at
        # user_acceptance that the apex requirements really are covered elsewhere").
        # Match across-line wrapping (YAML indent breaks "covered\n  elsewhere").
        $script:Prompt | Should -Match 'covered\s+elsewhere' -Because (
            'the prompt should call out and refuse this exact rationalization pattern that produced the AB#3064 false-approval')
    }

    It 'scope_reviewer prompt instructs an actual git rev-parse / git log check (not just LLM judgment)' {
        $script:Prompt | Should -Match 'git rev-parse' -Because (
            'an objective branch-existence check is the floor; the reviewer must run it before reasoning, not after')
        $script:Prompt | Should -Match 'zero commits' -Because (
            'the empty-history check (git log .. shows 0 commits) is the second deterministic check')
    }
}
