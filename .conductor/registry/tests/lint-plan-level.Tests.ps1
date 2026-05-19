BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-plan-level.ps1'
    $script:PlanLevelYaml = Join-Path $PSScriptRoot '..' 'workflows' 'plan-level.yaml'
}

Describe 'lint-plan-level.ps1' {

    Context 'Production plan-level.yaml validation' {

        It 'Passes on the real plan-level.yaml' {
            $script:PlanLevelYaml | Should -Exist
            $output = pwsh -NoProfile -File $script:LintScript 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Policy wiring checks' {

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-plan-level-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-plan-level.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Fails when open_questions_policy node is missing' {
            # Minimal valid-looking yaml without the policy node
            $yaml = @'
workflow:
  name: plan-level
  entry_point: depth_guard
  input:
    work_item_id:
      type: number

agents:
  - name: architect
    type: agent
    routes:
      - to: review_group
  - name: open_questions_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "echo test"
  - name: open_questions_answer_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "echo test"
'@
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $yaml
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-oq-policy-node'
        }

        It 'Fails when hardcoded severity list is present' {
            $yaml = @"
workflow:
  name: plan-level
  entry_point: depth_guard
  input:
    work_item_id:
      type: number

agents:
  - name: open_questions_policy
    type: script
    command: polyphony
    args:
      - "policy"
      - "resolve"
      - "--domain"
      - "open_questions"
  - name: open_questions_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - |
        `$capReached = `$count -ge `$maxLoops
        @{ iteration = `$count; cap_reached = `$capReached } | ConvertTo-Json
    routes:
      - to: review_group
        when: "{{ open_questions_policy.output.mode == 'auto' }}"
      - to: review_group
        when: "{{ open_questions_counter.output.cap_reached == true }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'manual' and architect.output.open_questions | length > 0 }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', open_questions_policy.output.severities_at_or_above) | list | length > 0 }}"
      - to: review_group
  - name: open_questions_gate
    type: human_gate
    prompt: |
      Policy mode: {{ open_questions_policy.output.mode }}
      Loop: {{ open_questions_counter.output.iteration }} of {{ open_questions_counter.output.max_loops }}
  - name: open_questions_answer_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "echo test"
  - name: architect
    type: agent
    routes:
      - to: open_questions_gate
        when: "{{ architect.output.open_questions | selectattr('severity', 'in', ['moderate', 'major', 'critical']) | list | length > 0 }}"
      - to: review_group
"@
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $yaml
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'hardcoded-severity-filter'
        }

        It 'Fails when mode=auto route is missing' {
            $yaml = @"
workflow:
  name: plan-level
  entry_point: depth_guard
  input:
    work_item_id:
      type: number

agents:
  - name: open_questions_policy
    type: script
    command: polyphony
    args:
      - "policy"
      - "resolve"
      - "--domain"
      - "open_questions"
  - name: open_questions_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - |
        @{ iteration = 0; cap_reached = `$false } | ConvertTo-Json
    routes:
      - to: review_group
        when: "{{ open_questions_counter.output.cap_reached == true }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'manual' and architect.output.open_questions | length > 0 }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', open_questions_policy.output.severities_at_or_above) | list | length > 0 }}"
      - to: review_group
  - name: open_questions_gate
    type: human_gate
    prompt: |
      Policy mode: {{ open_questions_policy.output.mode }}
      Loop: {{ open_questions_counter.output.iteration }} of {{ open_questions_counter.output.max_loops }}
  - name: open_questions_answer_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "echo test"
  - name: architect
    type: agent
    routes:
      - to: open_questions_policy
"@
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $yaml
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-auto-mode-route'
        }

        It 'Passes with all policy wiring present and no hardcoded severities' {
            $yaml = @"
workflow:
  name: plan-level
  entry_point: depth_guard
  input:
    work_item_id:
      type: number
    child_scope_globs:
      type: string
      default: ""

output:
  renegotiation_pending: >-
    {% if extract_renegotiation_flag is defined %}{{ (extract_renegotiation_flag.output.flag_present | default(false)) | string | lower }}
    {%- else %}false{% endif %}
  renegotiation_request: >-
    {% if extract_renegotiation_flag is defined %}{{ extract_renegotiation_flag.output.renegotiation_request | default('') }}
    {%- else %}{% endif %}
  validate_scope_verdict: >-
    {% if validate_scope is defined %}{{ validate_scope.output.verdict | default('') }}
    {%- else %}{% endif %}
  scope_violation_files: >-
    {% if validate_scope is defined %}{{ validate_scope.output.files_out_of_scope | default([]) | tojson }}
    {%- else %}[]{% endif %}

agents:
  - name: open_questions_policy
    type: script
    command: polyphony
    args:
      - "policy"
      - "resolve"
      - "--domain"
      - "open_questions"
  - name: open_questions_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - |
        @{ iteration = 0; cap_reached = `$false } | ConvertTo-Json
    routes:
      - to: review_group
        when: "{{ open_questions_policy.output.mode == 'auto' }}"
      - to: review_group
        when: "{{ open_questions_counter.output.cap_reached == true }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'manual' and architect.output.open_questions | length > 0 }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', open_questions_policy.output.severities_at_or_above) | list | length > 0 }}"
      - to: review_group
  - name: open_questions_gate
    type: human_gate
    prompt: |
      Policy mode: {{ open_questions_policy.output.mode }}
      Loop: {{ open_questions_counter.output.iteration }} of {{ open_questions_counter.output.max_loops }}
  - name: open_questions_answer_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "echo test"
  - name: architect
    type: agent
    routes:
      - to: open_questions_policy
  - name: state_detector
    type: script
    command: polyphony
    args: ["plan", "detect-state"]
    routes:
      - to: seeder
        when: "{{ state_detector.output.state == 'merged_unseeded' }}"
      - to: architect
  - name: seeder
    type: script
    command: polyphony
    args: ["plan", "seed-children"]
    routes:
      - to: review_group
  - name: plan_reviewer
    type: agent
    routes:
      - to: poll_status
  - name: poll_status
    type: script
    command: polyphony
    args:
      - "pr"
      - "poll-status"
    routes:
      - to: validate_scope
        when: "{{ poll_status.output.state == 'approved' and workflow.input.child_scope_globs != '' }}"
      - to: merge_plan_pr
        when: "{{ poll_status.output.state == 'approved' }}"
      - to: pr_feedback_analyzer
        when: "{{ poll_status.output.route == 'none' }}"
  - name: poll_status_ado
    type: script
    command: polyphony
    args:
      - "pr"
      - "poll-status-ado"
    routes:
      - to: pr_feedback_analyzer
        when: "{{ poll_status_ado.output.route == 'none' }}"
  - name: pr_feedback_analyzer
    type: agent
    model: claude-sonnet-4.6
    description: stub for lint fixture
    prompt: "stub"
    output:
      has_negative_feedback:
        type: boolean
      feedback_summary:
        type: string
      feedback_digest:
        type: string
    routes:
      - to: pending_poll_counter
  - name: pending_poll_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "echo {}"
    routes:
      - to: stuck_review_gate
        when: "{{ pending_poll_counter.output.cap_reached == true }}"
      - to: merge_plan_pr
  - name: stuck_review_gate
    type: human_gate
    prompt: "stuck review"
    options:
      - label: "Continue"
        value: continue_waiting
        route: stuck_review_reset
      - label: "Override"
        value: override_approved
        route: stuck_review_override_router
      - label: "Abort"
        value: abort
        route: `$end
  - name: stuck_review_reset
    type: script
    command: pwsh
    args: ["-Command", "echo {}"]
    routes:
      - to: merge_plan_pr
  - name: stuck_review_override_router
    type: script
    command: pwsh
    args: ["-Command", "echo {}"]
    routes:
      - to: merge_plan_pr
  - name: validate_scope
    type: script
    command: polyphony
    args:
      - "plan"
      - "validate-scope"
      - "{{ poll_status.output.pr_number }}"
      - "--child-scope"
      - "{{ workflow.input.child_scope_globs }}"
    routes:
      - to: scope_violation_gate
        when: "{{ validate_scope.output.verdict == 'block' }}"
      - to: merge_plan_pr
  - name: scope_violation_gate
    type: human_gate
    prompt: |
      Out of scope: {{ validate_scope.output.files_out_of_scope }}
    options:
      - label: "Override"
        value: override
        route: merge_plan_pr
      - label: "Abort"
        value: abort
        route: `$end
  - name: merge_plan_pr
    type: script
    command: polyphony
    args:
      - "pr"
      - "merge-plan-pr"
    routes:
      - to: extract_renegotiation_flag
  - name: extract_renegotiation_flag
    type: script
    command: polyphony
    args:
      - "plan"
      - "extract-renegotiation-flag"
      - "{{ poll_status.output.pr_number }}"
    routes:
      - to: cascade_remedy
"@
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $yaml
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        # ── Phase 3 P8c handler — scope-renegotiation lint coverage ──────

        It 'Fails when validate_scope node is missing' {
            $yaml = @"
workflow:
  name: plan-level
  entry_point: depth_guard
  input:
    work_item_id:
      type: number
    child_scope_globs:
      type: string
      default: ""

output:
  renegotiation_pending: "false"
  renegotiation_request: ""
  validate_scope_verdict: ""
  scope_violation_files: "[]"

agents:
  - name: open_questions_policy
    type: script
    command: polyphony
    args:
      - "policy"
      - "resolve"
      - "--domain"
      - "open_questions"
  - name: open_questions_counter
    type: script
    command: pwsh
    args: ["-Command", "echo {}"]
    routes:
      - to: review_group
        when: "{{ open_questions_policy.output.mode == 'auto' }}"
      - to: review_group
        when: "{{ open_questions_counter.output.cap_reached == true }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'manual' and architect.output.open_questions | length > 0 }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', open_questions_policy.output.severities_at_or_above) | list | length > 0 }}"
  - name: open_questions_answer_counter
    type: script
    command: pwsh
    args: ["-Command", "echo {}"]
  - name: open_questions_gate
    type: human_gate
    prompt: |
      Policy mode: {{ open_questions_policy.output.mode }}
      Loop: {{ open_questions_counter.output.iteration }}
  - name: plan_reviewer
    type: agent
  - name: merge_plan_pr
    type: script
    command: polyphony
    args: ["pr", "merge-plan-pr"]
    routes:
      - to: extract_renegotiation_flag
  - name: extract_renegotiation_flag
    type: script
    command: polyphony
    args: ["plan", "extract-renegotiation-flag", "1"]
  - name: scope_violation_gate
    type: human_gate
    prompt: |
      out: {{ validate_scope.output.verdict }}
"@
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $yaml
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-validate-scope-node'
        }

        It 'Fails when scope_violation_gate is missing' {
            $yaml = @"
workflow:
  name: plan-level
  entry_point: depth_guard
  input:
    work_item_id:
      type: number
    child_scope_globs:
      type: string
      default: ""

output:
  renegotiation_pending: "false"
  renegotiation_request: ""
  validate_scope_verdict: ""
  scope_violation_files: "[]"

agents:
  - name: open_questions_policy
    type: script
    command: polyphony
    args:
      - "policy"
      - "resolve"
      - "--domain"
      - "open_questions"
  - name: open_questions_counter
    type: script
    command: pwsh
    args: ["-Command", "echo {}"]
    routes:
      - to: review_group
        when: "{{ open_questions_policy.output.mode == 'auto' }}"
      - to: review_group
        when: "{{ open_questions_counter.output.cap_reached == true }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'manual' and architect.output.open_questions | length > 0 }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', open_questions_policy.output.severities_at_or_above) | list | length > 0 }}"
  - name: open_questions_answer_counter
    type: script
    command: pwsh
    args: ["-Command", "echo {}"]
  - name: open_questions_gate
    type: human_gate
    prompt: |
      Policy mode: {{ open_questions_policy.output.mode }}
      Loop: {{ open_questions_counter.output.iteration }}
  - name: plan_reviewer
    type: agent
  - name: poll_status
    type: script
    command: polyphony
    args: ["pr", "poll-status"]
    routes:
      - to: validate_scope
        when: "{{ poll_status.output.state == 'approved' and workflow.input.child_scope_globs != '' }}"
      - to: merge_plan_pr
        when: "{{ poll_status.output.state == 'approved' }}"
  - name: validate_scope
    type: script
    command: polyphony
    args:
      - "plan"
      - "validate-scope"
      - "{{ poll_status.output.pr_number }}"
      - "--child-scope"
      - "{{ workflow.input.child_scope_globs }}"
    routes:
      - to: merge_plan_pr
  - name: merge_plan_pr
    type: script
    command: polyphony
    args: ["pr", "merge-plan-pr"]
    routes:
      - to: extract_renegotiation_flag
  - name: extract_renegotiation_flag
    type: script
    command: polyphony
    args: ["plan", "extract-renegotiation-flag", "1"]
"@
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $yaml
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-scope-violation-gate'
        }

        It 'Fails when extract_renegotiation_flag is missing' {
            $yaml = @"
workflow:
  name: plan-level
  entry_point: depth_guard
  input:
    work_item_id:
      type: number
    child_scope_globs:
      type: string
      default: ""

output:
  renegotiation_pending: "false"
  renegotiation_request: ""
  validate_scope_verdict: ""
  scope_violation_files: "[]"

agents:
  - name: open_questions_policy
    type: script
    command: polyphony
    args:
      - "policy"
      - "resolve"
      - "--domain"
      - "open_questions"
  - name: open_questions_counter
    type: script
    command: pwsh
    args: ["-Command", "echo {}"]
    routes:
      - to: review_group
        when: "{{ open_questions_policy.output.mode == 'auto' }}"
      - to: review_group
        when: "{{ open_questions_counter.output.cap_reached == true }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'manual' and architect.output.open_questions | length > 0 }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', open_questions_policy.output.severities_at_or_above) | list | length > 0 }}"
  - name: open_questions_answer_counter
    type: script
    command: pwsh
    args: ["-Command", "echo {}"]
  - name: open_questions_gate
    type: human_gate
    prompt: |
      Policy mode: {{ open_questions_policy.output.mode }}
      Loop: {{ open_questions_counter.output.iteration }}
  - name: plan_reviewer
    type: agent
  - name: poll_status
    type: script
    command: polyphony
    args: ["pr", "poll-status"]
    routes:
      - to: validate_scope
        when: "{{ poll_status.output.state == 'approved' and workflow.input.child_scope_globs != '' }}"
      - to: merge_plan_pr
        when: "{{ poll_status.output.state == 'approved' }}"
  - name: validate_scope
    type: script
    command: polyphony
    args:
      - "plan"
      - "validate-scope"
      - "{{ poll_status.output.pr_number }}"
      - "--child-scope"
      - "{{ workflow.input.child_scope_globs }}"
    routes:
      - to: scope_violation_gate
        when: "{{ validate_scope.output.verdict == 'block' }}"
      - to: merge_plan_pr
  - name: scope_violation_gate
    type: human_gate
    prompt: |
      out: {{ validate_scope.output.verdict }}
  - name: merge_plan_pr
    type: script
    command: polyphony
    args: ["pr", "merge-plan-pr"]
"@
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $yaml
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-extract-renegotiation-flag-node'
        }

        It 'Fails when bubble-up output keys are missing from workflow output map' {
            # Same baseline as the passing test, but the workflow `output:`
            # block is omitted entirely — should flag all four bubble-up
            # outputs as missing.
            $yaml = @"
workflow:
  name: plan-level
  entry_point: depth_guard
  input:
    work_item_id:
      type: number
    child_scope_globs:
      type: string
      default: ""

agents:
  - name: open_questions_policy
    type: script
    command: polyphony
    args:
      - "policy"
      - "resolve"
      - "--domain"
      - "open_questions"
  - name: open_questions_counter
    type: script
    command: pwsh
    args: ["-Command", "echo {}"]
    routes:
      - to: review_group
        when: "{{ open_questions_policy.output.mode == 'auto' }}"
      - to: review_group
        when: "{{ open_questions_counter.output.cap_reached == true }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'manual' and architect.output.open_questions | length > 0 }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', open_questions_policy.output.severities_at_or_above) | list | length > 0 }}"
  - name: open_questions_answer_counter
    type: script
    command: pwsh
    args: ["-Command", "echo {}"]
  - name: open_questions_gate
    type: human_gate
    prompt: |
      Policy mode: {{ open_questions_policy.output.mode }}
      Loop: {{ open_questions_counter.output.iteration }}
  - name: plan_reviewer
    type: agent
  - name: poll_status
    type: script
    command: polyphony
    args: ["pr", "poll-status"]
    routes:
      - to: validate_scope
        when: "{{ poll_status.output.state == 'approved' and workflow.input.child_scope_globs != '' }}"
      - to: merge_plan_pr
        when: "{{ poll_status.output.state == 'approved' }}"
  - name: validate_scope
    type: script
    command: polyphony
    args:
      - "plan"
      - "validate-scope"
      - "{{ poll_status.output.pr_number }}"
      - "--child-scope"
      - "{{ workflow.input.child_scope_globs }}"
    routes:
      - to: scope_violation_gate
        when: "{{ validate_scope.output.verdict == 'block' }}"
      - to: merge_plan_pr
  - name: scope_violation_gate
    type: human_gate
    prompt: |
      out: {{ validate_scope.output.verdict }}
  - name: merge_plan_pr
    type: script
    command: polyphony
    args: ["pr", "merge-plan-pr"]
    routes:
      - to: extract_renegotiation_flag
  - name: extract_renegotiation_flag
    type: script
    command: polyphony
    args: ["plan", "extract-renegotiation-flag", "1"]
"@
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $yaml
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            $joined = $output -join "`n"
            $joined | Should -Match 'missing-output-renegotiation-pending'
            $joined | Should -Match 'missing-output-renegotiation-request'
            $joined | Should -Match 'missing-output-validate-scope-verdict'
            $joined | Should -Match 'missing-output-scope-violation-files'
        }
    }

    Context 'Stuck-review timeout MVP' {

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-plan-level-stuck-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            $script:RealYaml = Join-Path $PSScriptRoot '..' 'workflows' 'plan-level.yaml'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-plan-level.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Real plan-level.yaml carries the pending_poll_counter step' {
            (Get-Content $script:RealYaml -Raw) | Should -Match 'name:\s*pending_poll_counter'
        }

        It 'Real plan-level.yaml has stuck_review_gate with all three required options' {
            $content = Get-Content $script:RealYaml -Raw
            $content | Should -Match 'name:\s*stuck_review_gate'
            $m = [regex]::Match($content, '(?s)- name: stuck_review_gate\b.*?(?=\n  - name: |\Z)')
            $m.Success | Should -BeTrue
            $m.Value | Should -Match 'value:\s*continue_waiting'
            $m.Value | Should -Match 'value:\s*override_approved'
            $m.Value | Should -Match 'value:\s*abort'
        }

        It 'Real plan-level.yaml has stuck_review_reset and stuck_review_override_router' {
            $content = Get-Content $script:RealYaml -Raw
            $content | Should -Match 'name:\s*stuck_review_reset'
            $content | Should -Match 'name:\s*stuck_review_override_router'
        }

        It 'Real plan-level.yaml routes both poll_status legs through pr_feedback_analyzer on route=none' {
            $content = Get-Content $script:RealYaml -Raw
            # poll_status leg (sentiment-driven model — route 'none' defers to analyzer)
            $content | Should -Match "to:\s*pr_feedback_analyzer[\s\S]{0,200}?poll_status\.output\.route\s*==\s*'none'"
            # poll_status_ado leg
            $content | Should -Match "to:\s*pr_feedback_analyzer[\s\S]{0,200}?poll_status_ado\.output\.route\s*==\s*'none'"
        }

        It 'Real plan-level.yaml routes from pending_poll_counter to stuck_review_gate on cap_reached' {
            (Get-Content $script:RealYaml -Raw) | Should -Match 'pending_poll_counter\.output\.cap_reached\s*==\s*true'
        }

        It 'Lint fails when pending_poll_counter is removed from plan-level.yaml' {
            $content = Get-Content $script:RealYaml -Raw
            $stripped = $content -replace '(?s)\n\s*# ── Pending poll counter.*?(?=\n  # ── Pending-review gate)', "`n"
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $stripped
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-pending-poll-counter'
        }

        It 'Lint fails when stuck_review_gate is missing the override_approved option' {
            $content = Get-Content $script:RealYaml -Raw
            $mutated = $content -replace '(?s)(- label: "✅ Override approved"\s*\r?\n\s*value:\s*override_approved\s*\r?\n\s*route:\s*stuck_review_override_router\s*\r?\n)', ''
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $mutated
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match "missing-stuck-review-option"
        }
    }

    Context 'open_questions_policy --scope type field (regression: dogfood apex #3043, 2026-05-07)' {

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-plan-level-typescope-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            $script:RealYaml = Join-Path $PSScriptRoot '..' 'workflows' 'plan-level.yaml'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-plan-level.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Real plan-level.yaml uses type_loader.output.type (canonical field) for the policy scope' {
            (Get-Content $script:RealYaml -Raw) | Should -Match 'type:\{\{\s*type_loader\.output\.type\s*\}\}'
        }

        It 'Real plan-level.yaml does NOT reference the bogus type_loader.output.type_name field' {
            (Get-Content $script:RealYaml -Raw) | Should -Not -Match 'type_loader\.output\.type_name'
        }

        It 'Lint fails when open_questions_policy regresses to type_loader.output.type_name' {
            $content = Get-Content $script:RealYaml -Raw
            $mutated = $content -replace 'type:\{\{\s*type_loader\.output\.type\s*\}\}', 'type:{{ type_loader.output.type_name }}'
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $mutated
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'open-questions-policy-bad-type-field'
        }
    }

    Context 'severities_at_or_above field reference (regression: dogfood apex #3043, 2026-05-08)' {

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-plan-level-severities-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            $script:RealYaml = Join-Path $PSScriptRoot '..' 'workflows' 'plan-level.yaml'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-plan-level.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Real plan-level.yaml references the precomputed severities_at_or_above field' {
            (Get-Content $script:RealYaml -Raw) | Should -Match 'open_questions_policy\.output\.severities_at_or_above'
        }

        It 'Real plan-level.yaml does NOT reference severities_at_or_above as a Jinja function call' {
            (Get-Content $script:RealYaml -Raw) | Should -Not -Match 'severities_at_or_above\s*\('
        }

        It 'Lint fails when plan-level.yaml regresses to the legacy function-call form' {
            $content = Get-Content $script:RealYaml -Raw
            # Mutate every field reference back to the old function-call form
            $mutated = $content -replace 'open_questions_policy\.output\.severities_at_or_above', 'severities_at_or_above(open_questions_policy.output.min_severity)'
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $mutated
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'severities-at-or-above-as-function'
        }

        It 'Lint fails when the severities_at_or_above field reference is removed entirely' {
            $content = Get-Content $script:RealYaml -Raw
            $mutated = $content -replace 'open_questions_policy\.output\.severities_at_or_above', 'open_questions_policy.output.min_severity'
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $mutated
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-warning-mode-route'
        }
    }

    Context 'parent_item_id default-filter form (regression: dogfood apex #3043, 2026-05-08)' {

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-plan-level-pid-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            $script:RealYaml = Join-Path $PSScriptRoot '..' 'workflows' 'plan-level.yaml'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-plan-level.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Real plan-level.yaml uses the bare default(0) form for every parent_item_id reference' {
            # Conductor's custom _default_filter handles BOTH Undefined AND
            # None — bare default(0) is the correct form. The standard Jinja
            # 3-arg default(0, true) form crashes conductor at runtime
            # ("takes from 1 to 2 positional arguments but 3 were given").
            $content = Get-Content $script:RealYaml -Raw
            $bareMatches = [regex]::Matches($content, 'parent_item_id\s*\|\s*default\(\s*0\s*\)')
            $bareMatches.Count | Should -BeGreaterThan 0
            # And the multi-arg form is NOT present (would crash conductor).
            $multiArgMatches = [regex]::Matches($content, 'parent_item_id\s*\|\s*default\(\s*[^)]*,\s*[^)]+\)')
            $multiArgMatches.Count | Should -Be 0
        }

        It 'Lint fails when plan-level.yaml regresses to the multi-arg default(0, true) form' {
            $content = Get-Content $script:RealYaml -Raw
            # Mutate every bare form to the multi-arg form (which would crash conductor).
            $mutated = $content -replace 'parent_item_id\s*\|\s*default\(\s*0\s*\)', 'parent_item_id | default(0, true)'
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $mutated
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'parent-item-id-multi-arg-default'
        }
    }
}
