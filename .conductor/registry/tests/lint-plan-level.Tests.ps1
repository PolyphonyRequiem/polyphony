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
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', severities_at_or_above(open_questions_policy.output.min_severity)) | list | length > 0 }}"
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
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', severities_at_or_above(open_questions_policy.output.min_severity)) | list | length > 0 }}"
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
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', severities_at_or_above(open_questions_policy.output.min_severity)) | list | length > 0 }}"
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
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', severities_at_or_above(open_questions_policy.output.min_severity)) | list | length > 0 }}"
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
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', severities_at_or_above(open_questions_policy.output.min_severity)) | list | length > 0 }}"
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
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', severities_at_or_above(open_questions_policy.output.min_severity)) | list | length > 0 }}"
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
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', severities_at_or_above(open_questions_policy.output.min_severity)) | list | length > 0 }}"
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
}
