#requires -Modules Pester, powershell-yaml

# F8 regression: the scope_reviewer agent in implement-merge-group.yaml MUST
# instruct the LLM to fail loudly on an empty merge group instead of
# rationalizing the void and punting to the user_acceptance gate.
#
# AB#3166 update: zero-commit MGs are now triaged by a deterministic
# `scope_empty_mg_triage` node upstream. Case C (already_satisfied) is
# auto-approved via `scope_auto_approve`. The scope_reviewer prompt
# retains structural checks (a) branch-missing and (b) zero-tasks,
# plus a new zero-commit disambiguation section for Cases A/B.
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

Describe 'implement-merge-group.yaml — scope_reviewer empty-MG structural check (F8)' {

    BeforeAll {
        $script:WorkflowPath = Join-Path $PSScriptRoot '..' 'workflows' 'implement-merge-group.yaml'
        $script:WorkflowPath | Should -Exist
        $raw = Get-Content -Raw $script:WorkflowPath
        $script:Yaml = ConvertFrom-Yaml $raw
        # `agents:` is a TOP-LEVEL key in this workflow YAML (not nested
        # under `workflow:` like name/version/entry_point).
        $script:ScopeReviewer = $script:Yaml.agents | Where-Object { $_.name -eq 'scope_reviewer' } | Select-Object -First 1
        $script:ScopeReviewer | Should -Not -BeNullOrEmpty -Because 'scope_reviewer agent must exist in implement-merge-group.yaml'
        $script:Prompt = [string]$script:ScopeReviewer.prompt
    }

    It 'scope_reviewer prompt names the empty-MG structural check explicitly' {
        $script:Prompt | Should -Match 'empty[- ]?MG' -Because (
            'reviewer must be told to look for an empty merge group as a structural condition, not just a soft scope concern')
        $script:Prompt | Should -Match 'empty_merge_group_structural_violation' -Because (
            'the violation tag is the wire-level handle the gate downstream uses to identify this failure mode')
    }

    It 'scope_reviewer prompt directs verdict=changes_requested on structural violations' {
        $script:Prompt | Should -Match 'changes_requested' -Because (
            'the reviewer must request changes on structurally broken MGs; defaulting to approved (with a hopeful note) green-washes the upstream bug')
    }

    It 'scope_reviewer prompt forbids punting to user_acceptance on empty MG' {
        # The protective language must explicitly discourage the rationalization
        # observed in the AB#3064 dogfood ("the human reviewer should confirm at
        # user_acceptance that the apex requirements really are covered elsewhere").
        # Match across-line wrapping (YAML indent breaks "covered\n  elsewhere").
        $script:Prompt | Should -Match 'covered\s+elsewhere' -Because (
            'the prompt should call out and refuse this exact rationalization pattern that produced the AB#3064 false-approval')
    }

    It 'scope_reviewer prompt instructs an actual git rev-parse check (not just LLM judgment)' {
        $script:Prompt | Should -Match 'git rev-parse' -Because (
            'an objective branch-existence check is the floor; the reviewer must run it before reasoning, not after')
    }

    It 'scope_reviewer prompt includes zero-commit disambiguation (AB#3166)' {
        $script:Prompt | Should -Match 'zero.commit\s+disambiguation' -Because (
            'the AB#3166 fix adds a zero-commit disambiguation section to distinguish Cases A/B after Case C is triaged upstream')
        $script:Prompt | Should -Match 'no.op' -Because (
            'Case B (nothing-to-do MG) must be explicitly named so the LLM can approve legitimate no-ops')
    }
}

Describe 'implement-merge-group.yaml — scope_empty_mg_triage deterministic node (AB#3166)' {

    BeforeAll {
        $script:WorkflowPath = Join-Path $PSScriptRoot '..' 'workflows' 'implement-merge-group.yaml'
        $script:WorkflowPath | Should -Exist
        $raw = Get-Content -Raw $script:WorkflowPath
        $script:Yaml = ConvertFrom-Yaml $raw
        $script:Triage = $script:Yaml.agents | Where-Object { $_.name -eq 'scope_empty_mg_triage' } | Select-Object -First 1
        $script:AutoApprove = $script:Yaml.agents | Where-Object { $_.name -eq 'scope_auto_approve' } | Select-Object -First 1
    }

    It 'scope_empty_mg_triage node exists and is a script type' {
        $script:Triage | Should -Not -BeNullOrEmpty -Because (
            'the deterministic triage must exist to disambiguate zero-commit MGs before the LLM fires')
        $script:Triage.type | Should -Be 'script' -Because (
            'the triage must be deterministic (script), not LLM-driven')
    }

    It 'scope_empty_mg_triage routes already_satisfied to scope_auto_approve' {
        $alreadySatisfiedRoute = $script:Triage.routes | Where-Object {
            $_.when -and $_.when -match 'already_satisfied'
        }
        $alreadySatisfiedRoute | Should -Not -BeNullOrEmpty -Because (
            'Case C (already_satisfied) must bypass the LLM scope_reviewer')
        $alreadySatisfiedRoute.to | Should -Be 'scope_auto_approve' -Because (
            'already_satisfied dispositions route to the auto-approve node')
    }

    It 'scope_empty_mg_triage routes has_commits and structural_violation to scope_reviewer' {
        $hasCommitsRoute = $script:Triage.routes | Where-Object {
            $_.when -and $_.when -match 'has_commits'
        }
        $hasCommitsRoute | Should -Not -BeNullOrEmpty
        $hasCommitsRoute.to | Should -Be 'scope_reviewer'

        $structuralRoute = $script:Triage.routes | Where-Object {
            $_.when -and $_.when -match 'structural_violation'
        }
        $structuralRoute | Should -Not -BeNullOrEmpty
        $structuralRoute.to | Should -Be 'scope_reviewer'
    }

    It 'scope_empty_mg_triage has a catch-all route (M4 safety)' {
        $catchAll = $script:Triage.routes | Where-Object { -not $_.when }
        $catchAll | Should -Not -BeNullOrEmpty -Because (
            'per conductor-mechanics M4, every routed node must have a catch-all to prevent ValueError on unknown dispositions')
    }

    It 'scope_empty_mg_triage script checks git merge-base --is-ancestor for Case C' {
        $script:Triage.args | Should -Not -BeNullOrEmpty
        $scriptBody = $script:Triage.args[-1]
        $scriptBody | Should -Match 'merge-base\s+--is-ancestor' -Because (
            'Case C detection requires checking if main is an ancestor of feature — the correct direction per AB#3166 rubber-duck finding')
    }

    It 'scope_empty_mg_triage script fetches origin/main before ancestry check' {
        $scriptBody = $script:Triage.args[-1]
        $scriptBody | Should -Match 'fetch.*origin\s+main' -Because (
            'origin/main must be fresh before the ancestry check — fetch_for_scope_review only fetches feature + MG')
    }

    It 'scope_auto_approve node exists and routes to user_acceptance_policy_router' {
        $script:AutoApprove | Should -Not -BeNullOrEmpty -Because (
            'scope_auto_approve must exist to emit an approved verdict for auto-approved zero-commit MGs')
        $script:AutoApprove.type | Should -Be 'script'
        $route = $script:AutoApprove.routes | Select-Object -First 1
        $route.to | Should -Be 'user_acceptance_policy_router' -Because (
            'auto-approved MGs follow the same acceptance path as LLM-approved MGs')
    }
}

Describe 'implement-merge-group.yaml — user_acceptance handles both scope_reviewer and scope_auto_approve (AB#3166)' {

    BeforeAll {
        $script:WorkflowPath = Join-Path $PSScriptRoot '..' 'workflows' 'implement-merge-group.yaml'
        $script:WorkflowPath | Should -Exist
        $raw = Get-Content -Raw $script:WorkflowPath
        $script:Yaml = ConvertFrom-Yaml $raw
        $script:UserAcceptance = $script:Yaml.agents | Where-Object { $_.name -eq 'user_acceptance' } | Select-Object -First 1
        $script:UserAcceptance | Should -Not -BeNullOrEmpty
    }

    It 'user_acceptance prompt renders feedback from scope_auto_approve when scope_reviewer did not run' {
        $prompt = [string]$script:UserAcceptance.prompt
        $prompt | Should -Match 'scope_auto_approve' -Because (
            'the user_acceptance gate must handle feedback from the auto-approve path (AB#3166 Case C bypass)')
    }

    It 'user_acceptance prompt still renders feedback from scope_reviewer when it ran' {
        $prompt = [string]$script:UserAcceptance.prompt
        $prompt | Should -Match 'scope_reviewer' -Because (
            'the user_acceptance gate must still render scope_reviewer feedback on the normal review path')
    }
}
