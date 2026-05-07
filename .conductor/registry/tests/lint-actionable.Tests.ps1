BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-actionable.ps1'
    $script:ActionableYaml = Join-Path $PSScriptRoot '..' 'workflows' 'actionable.yaml'
    $script:RouterScript = Join-Path $PSScriptRoot '..' 'scripts' 'route-actionable-executor.ps1'
}

Describe 'lint-actionable.ps1' {

    Context 'Production actionable.yaml validation' {

        It 'Passes on the real actionable.yaml' {
            $script:ActionableYaml | Should -Exist
            $output = pwsh -NoProfile -File $script:LintScript 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Real actionable.yaml passes the strict-undefined lint' {
            # Per M3 — every reference to a verb's output across the
            # executor router (only ONE leg runs in any execution) must
            # be guarded with `is defined`. Reuse the existing lint to
            # catch StrictUndefined trap regressions in the new YAML.
            $strict = Join-Path $PSScriptRoot 'lint-strict-undefined.ps1'
            $output = pwsh -NoProfile -File $strict 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Companion router script is present and parses' {
            $script:RouterScript | Should -Exist
            $tokens = $null
            $errors = $null
            [System.Management.Automation.Language.Parser]::ParseFile(
                $script:RouterScript, [ref]$tokens, [ref]$errors) | Out-Null
            $errors | Should -BeNullOrEmpty
        }
    }

    Context 'Structural requirements' {

        BeforeAll {
            # Helper: minimal valid YAML with all required structure for
            # actionable.yaml. Mirrors the production shape just enough
            # to satisfy every check in lint-actionable.ps1.
            $script:ValidYaml = @'
workflow:
  name: actionable
  entry_point: executor_router
  metadata:
    min_polyphony_version: "1.0.1"
  limits:
    max_iterations: 50
  input:
    work_item_id:
      type: number
    apex_id:
      type: number
    executor:
      type: string
    platform:
      type: string
    organization:
      type: string
    project:
      type: string
    repository:
      type: string
    from_ref:
      type: string

output:
  satisfied: "{% if workflow_completed is defined %}true{% else %}false{% endif %}"
  executor: "{{ workflow.input.executor }}"
  pr_url: "{% if open_evidence_pr is defined %}{{ open_evidence_pr.output.pr_url }}{% else %}{% endif %}"
  pr_number: "{% if open_evidence_pr is defined %}{{ open_evidence_pr.output.pr_number }}{% else %}0{% endif %}"
  evidence_branch: "{% if ensure_evidence_branch is defined %}{{ ensure_evidence_branch.output.branch }}{% else %}{% endif %}"

agents:
  - name: executor_router
    type: script
    command: pwsh
    args: ["-NoProfile", "-File", "route.ps1"]
    routes:
      - to: ensure_evidence_branch
        when: "{{ executor_router.output.executor == 'polyphony' }}"
      - to: human_satisfaction_gate
        when: "{{ executor_router.output.executor == 'human' }}"
      - to: workflow_error_gate

  - name: ensure_evidence_branch
    type: script
    command: polyphony
    args: ["branch", "ensure-evidence-branch", "1"]
    routes:
      - to: workflow_error_gate
        when: "{{ ensure_evidence_branch.output.error is defined and ensure_evidence_branch.output.error != '' }}"
      - to: compose_addendum

  # Phase 6 PR #5 — composes the addendum (skills + MCPs + per-item
  # guidance) the actionable_agent prompt template injects.
  - name: compose_addendum
    type: script
    command: polyphony
    args: ["agent", "compose-addendum", "1"]
    routes:
      - to: workflow_error_gate
        when: "{{ compose_addendum.output.error is defined and compose_addendum.output.error != '' }}"
      - to: actionable_agent

  - name: actionable_agent
    type: agent
    model: claude-opus-4.6
    description: Perform the actionable work
    output:
      summary:
        type: string
    prompt: |
      Do the work for #{{ workflow.input.work_item_id }}.
      {% if compose_addendum is defined %}{{ compose_addendum.output.skills | default([]) | join(', ') }}{% endif %}
    routes:
      - to: open_evidence_pr

  - name: open_evidence_pr
    type: script
    command: polyphony
    args: ["pr", "open-evidence-pr", "1"]
    routes:
      - to: workflow_error_gate
        when: "{{ open_evidence_pr.output.error is defined and open_evidence_pr.output.error != '' }}"
      - to: evidence_floor_check

  - name: evidence_floor_check
    type: script
    command: polyphony
    args: ["pr", "check-evidence-floor", "1"]
    routes:
      - to: workflow_error_gate
        when: "{{ evidence_floor_check.output.error_code is defined and evidence_floor_check.output.error_code != null }}"
      - to: floor_failed_gate
        when: "{{ evidence_floor_check.output.passes_floor == false }}"
      - to: evidence_reviewer

  - name: floor_failed_gate
    type: human_gate
    prompt: "Floor failed"
    options:
      - label: "Abort"
        value: abort
        route: workflow_abandoned
      - label: "Retry"
        value: retry
        route: actionable_agent
      - label: "Manual complete"
        value: manual_complete
        route: workflow_completed

  # TODO(p6-pr8): replace this stub with the full evidence-judgment rubric.
  - name: evidence_reviewer
    type: agent
    model: claude-opus-4.7
    description: Judge evidence sufficiency
    output:
      decision:
        type: string
      comment:
        type: string
    prompt: "Review the evidence"
    routes:
      - to: merge_evidence_pr
        when: "{{ evidence_reviewer.output.decision == 'approve' }}"
      - to: revise_loop_gate
        when: "{{ evidence_reviewer.output.decision == 'request_changes' }}"
      - to: workflow_error_gate
        when: "{{ evidence_reviewer.output.decision == 'block' }}"
      - to: workflow_error_gate

  - name: revise_loop_gate
    type: human_gate
    prompt: "Reviewer requested changes"
    options:
      - label: "Retry"
        value: retry
        route: actionable_agent
      - label: "Abandon"
        value: abandon
        route: workflow_abandoned

  - name: merge_evidence_pr
    type: script
    command: pwsh
    args: ["-NoProfile", "-Command", "@{ merged = $true } | ConvertTo-Json"]
    routes:
      - to: workflow_completed
        when: "{{ merge_evidence_pr.output.merged == true }}"
      - to: workflow_error_gate

  - name: human_satisfaction_gate
    type: human_gate
    prompt: "Has the action been performed?"
    options:
      - label: "Satisfied"
        value: satisfied
        route: workflow_completed
      - label: "Not yet"
        value: not_yet
        route: human_satisfaction_gate
      - label: "Abandoned"
        value: abandoned
        route: workflow_abandoned

  - name: workflow_error_gate
    type: human_gate
    prompt: "Error - retry or abandon?"
    options:
      - label: "Retry"
        value: retry
        route: executor_router
      - label: "Abandon"
        value: abandon
        route: workflow_abandoned

  - name: workflow_completed
    type: script
    command: pwsh
    args: ["-NoProfile", "-Command", "@{ satisfied = $true } | ConvertTo-Json"]
    routes:
      - to: $end

  - name: workflow_abandoned
    type: script
    command: pwsh
    args: ["-NoProfile", "-Command", "@{ satisfied = $false } | ConvertTo-Json"]
    routes:
      - to: $end
'@
        }

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-actionable-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-actionable.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Passes when all structural requirements are met' {
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $script:ValidYaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Fails when work_item_id input is missing' {
            $yaml = ($script:ValidYaml) -replace '(?m)^\s+work_item_id:\s*\n\s+type: number\s*\n', ''
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-input.*work_item_id'
        }

        It 'Fails when executor input is missing' {
            $yaml = ($script:ValidYaml) -replace '(?m)^\s+executor:\s*\n\s+type: string\s*\n', ''
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-input.*executor'
        }

        It 'Fails when satisfied output is missing' {
            $yaml = ($script:ValidYaml) -replace '(?m)^\s+satisfied:.*\n', ''
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-output.*satisfied'
        }

        It 'Fails when executor_router node is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: executor_router', 'name: executor_decider'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-node.*executor_router'
        }

        It 'Fails when human_satisfaction_gate is missing (human leg deleted)' {
            $yaml = ($script:ValidYaml) -replace 'name: human_satisfaction_gate', 'name: human_check_gate'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-node.*human_satisfaction_gate'
        }

        It 'Fails when ensure-evidence-branch verb is not invoked' {
            $yaml = ($script:ValidYaml) -replace '"ensure-evidence-branch"', '"create-evidence"'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-evidence-verb'
        }

        It 'Fails when open-evidence-pr verb is not invoked' {
            $yaml = ($script:ValidYaml) -replace '"open-evidence-pr"', '"create-evidence-pr"'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-evidence-verb'
        }

        It 'Fails when actionable_agent uses a non-opus model' {
            $yaml = ($script:ValidYaml) -replace '(name: actionable_agent[\s\S]*?model: )claude-opus-4.6', '$1claude-sonnet-4.6'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'wrong-agent-model.*actionable_agent'
        }

        It 'Fails when evidence_reviewer uses a non-opus model' {
            $yaml = ($script:ValidYaml) -replace '(name: evidence_reviewer[\s\S]*?model: )claude-opus-4.7', '$1claude-sonnet-4.6'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'wrong-agent-model.*evidence_reviewer'
        }

        It 'Accepts a different opus revision (model pin must not break on opus bump)' {
            $yaml = ($script:ValidYaml) -replace '(name: actionable_agent[\s\S]*?model: )claude-opus-4.6', '$1claude-opus-4.7-1m-internal'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Fails when evidence_reviewer lacks an output schema (M2)' {
            $yaml = ($script:ValidYaml) -replace '(?ms)(name: evidence_reviewer[\s\S]*?)\n\s+output:\s*\n\s+decision:\s*\n\s+type: string\s*\n\s+comment:\s*\n\s+type: string', '$1'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-output-schema.*evidence_reviewer'
        }

        It 'Fails when a route target references a non-existent agent' {
            $yaml = ($script:ValidYaml) -replace 'to: workflow_completed', 'to: not_a_real_node'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'invalid-route-target'
        }

        It 'Fails when entry point is wrong' {
            $yaml = ($script:ValidYaml) -replace 'entry_point: executor_router', 'entry_point: actionable_agent'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'wrong-entry-point'
        }

        It 'Fails on type-agnostic violation (P5 — Epic / Issue / Task forbidden)' {
            $yaml = ($script:ValidYaml) -replace '"open-evidence-pr"', '"open-evidence-pr"
        # Epic-only path'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            # The injected `# Epic-only path` is a comment, so it must NOT
            # trigger the violation (the lint strips comment lines first).
            $LASTEXITCODE | Should -Be 0
        }

        It 'Fails when an Epic literal is in non-comment YAML' {
            $yaml = ($script:ValidYaml) -replace 'description: Perform the actionable work', 'description: Perform the actionable work for the Epic'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'type-agnostic-violation'
        }

        It 'Fails when min_polyphony_version is missing' {
            $yaml = ($script:ValidYaml) -replace '(?m)^\s+min_polyphony_version:.*\n', ''
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-min-polyphony-version'
        }

        It 'Fails when a deferred-wiring TODO marker is dropped' {
            # Replace TODO(p6-pr8) with a generic marker — that should fail.
            $yaml = ($script:ValidYaml) -replace 'TODO\(p6-pr8\)', 'TODO(generic)'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-deferred-wiring-todo'
        }

        It 'Fails when the shipped TODO(p6-pr7) marker is still present' {
            # Phase 6 PR #7 ships the floor — the marker must be ABSENT
            # from production YAML now. Re-introducing it is the
            # symptom of a botched revert / merge.
            $yaml = ($script:ValidYaml) -replace '(?ms)(- name: evidence_floor_check)', "      # TODO(p6-pr7): insert evidence_floor_check`n`$1"
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'shipped-todo-still-present'
        }

        It 'Fails when evidence_floor_check node is missing (PR #7 wiring reverted)' {
            $yaml = ($script:ValidYaml) -replace 'name: evidence_floor_check', 'name: evidence_floor_disabled'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-node.*evidence_floor_check'
        }

        It 'Fails when floor_failed_gate node is missing (PR #7 gate reverted)' {
            $yaml = ($script:ValidYaml) -replace 'name: floor_failed_gate', 'name: floor_failed_disabled'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-node.*floor_failed_gate'
        }

        It 'Fails when check-evidence-floor verb is not invoked' {
            $yaml = ($script:ValidYaml) -replace '"check-evidence-floor"', '"check-something-else"'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-evidence-verb'
        }

        It 'Fails when the compose_addendum step is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: compose_addendum', 'name: compose_skipped'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-node.*compose_addendum'
        }

        It 'Fails when the agent compose-addendum verb is not invoked' {
            $yaml = ($script:ValidYaml) -replace '"compose-addendum"', '"compose-context"'
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-evidence-verb'
        }

        It 'Fails when actionable_agent does not consume compose_addendum.output' {
            # Strip the {% if compose_addendum %} block from the agent prompt.
            $yaml = ($script:ValidYaml) -replace "(?ms)\s+\{% if compose_addendum is defined %\}.*?\{% endif %\}", ''
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'compose-addendum-envelope-not-consumed'
        }

        It 'Fails when the stale TODO(p6-pr5) marker reappears' {
            # PR #5 removed it; reappearance is a partial-revert smell.
            $yaml = ($script:ValidYaml) -replace 'name: compose_addendum', "name: compose_addendum`n  # TODO(p6-pr5): forgotten marker"
            Set-Content (Join-Path $script:WorkflowsDir 'actionable.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-actionable.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'shipped-todo-still-present'
        }
    }
}
