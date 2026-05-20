BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-implement-merge-group.ps1'
    $script:ImplementMgYaml = Join-Path $PSScriptRoot '..' 'workflows' 'implement-merge-group.yaml'
}

Describe 'lint-implement-merge-group.ps1' {

    Context 'Production implement-merge-group.yaml validation' {

        It 'Passes on the real implement-merge-group.yaml' {
            $script:ImplementMgYaml | Should -Exist
            $output = pwsh -NoProfile -File $script:LintScript 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }

    # AB#3169 — primary_completer must skip implementation_complete event
    # when primary_id == root_id (indivisible apex root case). Otherwise the
    # apex Issue is marked Done at MG → feature merge time, before
    # feature → main has been promoted.
    Context 'primary_completer apex-root carve-out (AB#3169)' {

        BeforeAll {
            $script:ImplementMgYamlText = Get-Content $script:ImplementMgYaml -Raw
        }

        It 'Captures the rootId from workflow.input.root_id alongside the taskId' {
            $script:ImplementMgYamlText | Should -Match '\$rootId\s*=\s*\[int\]''\{\{\s*workflow\.input\.root_id\s*\}\}'''
        }

        It 'Computes $isApexRoot = $taskId -eq $rootId discriminator' {
            $script:ImplementMgYamlText | Should -Match '\$isApexRoot\s*=\s*\(\$taskId\s*-eq\s*\$rootId\)'
        }

        It 'Branches on $isApexRoot so the implementation_complete event is only fired in the else (non-apex-root) arm' {
            $primary = [regex]::Match($script:ImplementMgYamlText, '(?s)- name: primary_completer.*?routes:')
            $primary.Success | Should -BeTrue
            $body = $primary.Value
            # Split the inner if-arm from the else-arm at the literal `} else {`
            # introduced by the AB#3169 carve-out. This avoids fragile balanced-brace
            # regex parsing when the inner `if (-not $validate.is_valid) { throw ... }`
            # block confuses non-greedy matchers.
            $body | Should -Match '\}\s*else\s*\{'
            $split = $body -split '\}\s*else\s*\{', 2
            $split.Count | Should -Be 2
            $ifSide   = $split[0]
            $elseSide = $split[1]
            # Trim the if-side back to start at `if ($isApexRoot) {` so we don't
            # confuse pre-if setup lines with the apex-root body.
            $ifSplit = $ifSide -split 'if\s*\(\$isApexRoot\)\s*\{', 2
            $ifSplit.Count | Should -Be 2
            $ifBody = $ifSplit[1]
            $ifBody   | Should -Not -Match 'polyphony validate.*--event implementation_complete' -Because 'AB#3169 — apex root MUST skip implementation_complete; close_mark_satisfied fires the terminal item_satisfied event AFTER feature → main promotion'
            $ifBody   | Should -Not -Match 'twig state'                                          -Because 'AB#3169 — apex root MUST NOT change ADO state here'
            $elseSide | Should -Match 'polyphony validate.*--event implementation_complete'      -Because 'Child tasks still get their normal implementation_complete transition'
            $elseSide | Should -Match 'twig state \$validate\.target_state'                      -Because 'Child tasks still transition via twig state'
        }

        It 'Emits a deferred_apex_root flag on the JSON output so callers / dashboards can distinguish the two arms' {
            $script:ImplementMgYamlText | Should -Match 'deferred_apex_root\s*=\s*\$true'
            $script:ImplementMgYamlText | Should -Match 'deferred_apex_root\s*=\s*\$false'
        }

        It 'Apex-root note text references AB#3169 so the audit trail in twig comments points to the rationale' {
            $script:ImplementMgYamlText | Should -Match 'twig note --text ''Apex root implementation merged into MG branch.*AB#3169'
        }
    }

    Context 'Structural requirements' {

        BeforeAll {
            # Helper: minimal valid YAML with all required structure for
            # implement-merge-group. Must include every Rev 4 grammar verb the lint
            # checks for (ensure-mg, ensure-impl, open-mg-pr, open-impl-pr,
            # merge-mg-pr, merge-impl-pr) plus all required agents and
            # output schemas. max_iterations is set to 300 to satisfy the
            # task-loop-budget check.
            $script:ValidYaml = @'
workflow:
  name: implement-merge-group
  entry_point: branch_ensure_mg
  limits:
    max_iterations: 300
  input:
    work_item_id:
      type: number
    root_id:
      type: number
    pg_number:
      type: number
    mg_path:
      type: string
    work_item_ids:
      type: array
    feature_branch:
      type: string

output:
  merged: "{{ scope_closer.exit_code == 0 }}"
  pr_url: "{{ mg_pr_open.output.pr_url }}"
  pr_number: "{{ mg_pr_open.output.pr_number }}"
  mg_path: "{{ workflow.input.mg_path }}"

agents:
  - name: branch_ensure_mg
    type: script
    command: polyphony
    args: ["branch", "ensure-mg", "--root-id", "1", "--mg-path", "data-layer"]
    routes:
      - to: guidance_loader
  - name: guidance_loader
    type: script
    command: pwsh
    args: ["-Command", "@{} | ConvertTo-Json"]
    routes:
      - to: primary_router
  - name: primary_router
    type: script
    command: pwsh
    args: ["-Command", "@{} | ConvertTo-Json"]
    routes:
      - to: impl_branch_ensure
        when: "{{ primary_router.output.action == 'implement_item' }}"
      - to: dependency_check
        when: "{{ primary_router.output.action == 'all_items_done' }}"
  - name: impl_branch_ensure
    type: script
    command: polyphony
    args: ["branch", "ensure-impl", "--root-id", "1", "--item-id", "2", "--mg-path", "data-layer"]
    routes:
      - to: coder
  - name: coder
    type: agent
    model: claude-opus-4.6
    description: Implement a single task
    prompt: "Implement the task"
    routes:
      - to: primary_reviewer
  - name: primary_reviewer
    type: agent
    model: claude-opus-4.6
    description: Review per-item task implementation
    output:
      verdict:
        type: string
      feedback:
        type: string
      issues:
        type: array
        items:
          type: string
    prompt: "Review the implementation"
    routes:
      - to: impl_pr_open
        when: "{{ primary_reviewer.output.verdict == 'approved' }}"
      - to: coder
        when: "{{ primary_reviewer.output.verdict == 'changes_requested' }}"
  - name: impl_pr_open
    type: script
    command: polyphony
    args: ["pr", "open-impl-pr", "--root-id", "1", "--item-id", "2", "--mg-path", "data-layer"]
    routes:
      - to: impl_pr_merge
  - name: impl_pr_merge
    type: script
    command: polyphony
    args: ["pr", "merge-impl-pr", "--root-id", "1", "--item-id", "2", "--mg-path", "data-layer"]
    routes:
      - to: primary_completer
  - name: primary_completer
    type: script
    command: pwsh
    args: ["-Command", "@{} | ConvertTo-Json"]
    routes:
      - to: primary_router
  - name: dependency_check
    type: script
    command: pwsh
    args: ["-Command", "@{} | ConvertTo-Json"]
    routes:
      - to: dependency_gate
        when: "{{ dependency_check.output.status == 'blocked' }}"
      - to: scope_empty_mg_triage
        when: "{{ dependency_check.output.status == 'not_blocked' }}"
  - name: dependency_gate
    type: human_gate
    prompt: "Dependencies blocked"
    options:
      - label: "Wait"
        value: wait
        route: dependency_check
      - label: "Override"
        value: override
        route: scope_empty_mg_triage
      - label: "Reassign"
        value: reassign
        route: $end
  - name: scope_guidance_loader
    type: script
    command: pwsh
    args: ["-Command", "@{} | ConvertTo-Json"]
    routes:
      - to: scope_empty_mg_triage
  - name: scope_empty_mg_triage
    type: script
    command: pwsh
    args: ["-Command", "@{ disposition = 'has_commits' } | ConvertTo-Json"]
    routes:
      - to: scope_auto_approve
        when: "{{ scope_empty_mg_triage.output.disposition == 'already_satisfied' }}"
      - to: scope_reviewer
        when: "{{ scope_empty_mg_triage.output.disposition == 'has_commits' }}"
      - to: scope_reviewer
  - name: scope_auto_approve
    type: script
    command: pwsh
    args: ["-Command", "@{ verdict = 'approved'; feedback = 'auto'; issues = @() } | ConvertTo-Json"]
    routes:
      - to: mg_pr_open
  - name: scope_reviewer
    type: agent
    model: claude-opus-4.7
    description: Review MG-level work
    output:
      verdict:
        type: string
      feedback:
        type: string
      issues:
        type: array
        items:
          type: string
    prompt: "Review the MG"
    routes:
      - to: mg_pr_open
        when: "{{ scope_reviewer.output.verdict == 'approved' }}"
      - to: scope_revise_counter
        when: "{{ scope_reviewer.output.verdict == 'changes_requested' }}"
      - to: scope_revise_counter
  - name: scope_revise_counter
    type: script
    command: pwsh
    args: ["-Command", "@{ cap_reached = $false } | ConvertTo-Json"]
    routes:
      - to: scope_revise_cap_gate
        when: "{{ scope_revise_counter.output.cap_reached == true }}"
      - to: primary_router
        when: "{{ scope_revise_counter.output.cap_reached == false }}"
      - to: scope_revise_cap_gate
  - name: scope_revise_cap_gate
    type: human_gate
    prompt: "Scope revise cap hit"
    options:
      - label: "Re-loop"
        value: re_loop
        route: scope_revise_reset
      - label: "Force-accept"
        value: force_accept
        route: mg_pr_open
      - label: "Abort"
        value: abort
        route: $end
  - name: scope_revise_reset
    type: script
    command: pwsh
    args: ["-Command", "@{} | ConvertTo-Json"]
    routes:
      - to: primary_router
  - name: mg_pr_open
    type: script
    command: polyphony
    args: ["pr", "open-mg-pr", "--root-id", "1", "--mg-path", "data-layer"]
    routes:
      - to: mg_pr_merge
  - name: mg_pr_merge
    type: script
    command: polyphony
    args: ["pr", "merge-mg-pr", "--root-id", "1", "--mg-path", "data-layer"]
    routes:
      - to: scope_closer
  - name: scope_closer
    type: script
    command: pwsh
    args: ["-Command", "@{} | ConvertTo-Json"]
    routes:
      - to: $end
  - name: scope_revise_cap_gate_policy_router
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-File"
      - "../scripts/resolve-unattended-cap-mode.ps1"
    routes:
      - to: terminal_cap_auto_fail
        when: "{{ scope_revise_cap_gate_policy_router.output.cap_mode == 'auto_fail' }}"
      - to: scope_revise_cap_gate
  - name: terminal_cap_auto_fail
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-Command"
      - "echo done"
      - "-Reason"
      - "cap-auto-fail"
    routes:
      - to: $end
'@
        }

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-implement-merge-group-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Passes when all structural requirements are met' {
            $yaml = $script:ValidYaml
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Fails when work_item_id input is missing' {
            $yaml = ($script:ValidYaml) -replace 'work_item_id:', 'parent_item_id:'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-input'
        }

        It 'Fails when root_id input is missing' {
            $yaml = ($script:ValidYaml) -replace 'root_id:', 'apex_id:'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-input'
        }

        It 'Fails when mg_path input is missing' {
            # mg_path appears in both input and output sections. Rename
            # both so the lint regex finds neither, then assert the
            # specific input-missing violation. (We only assert on input;
            # the output check will also fire, which is fine.)
            $yaml = ($script:ValidYaml) -replace '(?m)^\s+mg_path:', '    bogus_input_name:'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-input.*mg_path'
        }

        It 'Fails when merged output is missing' {
            $yaml = ($script:ValidYaml) -replace '(?m)^\s+merged:.*\n', ''
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-output'
        }

        It 'Fails when pr_number output is missing' {
            $yaml = ($script:ValidYaml) -replace '(?m)^\s+pr_number:.*\n', ''
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-output'
        }

        It 'Fails when primary_router agent is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: primary_router', 'name: task_dispatcher'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-primary-loop-agent'
        }

        It 'Fails when impl_branch_ensure agent is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: impl_branch_ensure', 'name: task_branch_make'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-primary-loop-agent'
        }

        It 'Fails when impl_pr_open agent is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: impl_pr_open', 'name: task_pr_create'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-primary-loop-agent'
        }

        It 'Fails when coder uses a non-opus model' {
            # Coder must be opus-class. Sonnet is rejected.
            $yaml = ($script:ValidYaml) -replace '(name: coder[\s\S]*?model: )claude-opus-4.6', '$1claude-sonnet-4.6'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'wrong-coder-model'
        }

        It 'Accepts the coder using a different opus revision' {
            # The "contains opus" rule is intentionally flexible — bumping
            # opus revisions should not require a lint update.
            $yaml = ($script:ValidYaml) -replace '(name: coder[\s\S]*?model: )claude-opus-4.6', '$1claude-opus-4.7-1m-internal'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Fails when scope_reviewer agent is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: scope_reviewer', 'name: mg_reviewer'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-scope-review-agent'
        }

        It 'Fails when scope_reviewer uses a non-opus model' {
            $yaml = ($script:ValidYaml) -replace '(name: scope_reviewer[\s\S]*?model: )claude-opus-4.7', '$1claude-sonnet-4.6'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'wrong-scope-reviewer-model'
        }

        It 'Fails when mg_pr_open agent is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: mg_pr_open', 'name: pr_open'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-pr-agent'
        }

        It 'Fails when ensure-mg verb is not invoked' {
            # Replacing the verb string drops Rev 4 grammar usage; the
            # lint must catch this since the workflow loses its Rev 4
            # branch-model commitment.
            $yaml = ($script:ValidYaml) -replace '"ensure-mg"', '"create-mg-branch"'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-rev4-grammar-verb'
        }

        It 'Fails when merge-mg-pr verb is not invoked' {
            $yaml = ($script:ValidYaml) -replace '"merge-mg-pr"', '"merge-merge-group"'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-rev4-grammar-verb'
        }

        It 'Fails when merge-impl-pr verb is not invoked' {
            $yaml = ($script:ValidYaml) -replace '"merge-impl-pr"', '"finalize-task"'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-rev4-grammar-verb'
        }

        It 'Fails when dependency_check is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: dependency_check', 'name: dep_checker'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-dependency-check'
        }

        It 'Fails when dependency_gate is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: dependency_gate', 'name: dep_gate'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-dependency-gate'
        }

        It 'Fails when dependency gate wait option is missing' {
            $yaml = ($script:ValidYaml) -replace 'value: wait', 'value: pause'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-gate-option'
        }

        It 'Fails when scope_closer is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: scope_closer', 'name: mg_closer'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-scope-closer'
        }

        It 'Fails when entry point is wrong' {
            $yaml = ($script:ValidYaml) -replace 'entry_point: branch_ensure_mg', 'entry_point: primary_router'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'wrong-entry-point'
        }

        It 'Fails when primary_reviewer lacks an output schema' {
            # Per conductor-mechanics M2, agents whose output is read by
            # routes MUST declare an output schema. Without it the
            # response packs into output.result and the routes break.
            $yaml = ($script:ValidYaml) -replace '(?ms)(name: primary_reviewer.*?)\n\s+output:\s*\n\s+verdict:\s*\n\s+type: string\s*\n\s+feedback:\s*\n\s+type: string\s*\n\s+issues:\s*\n\s+type: array\s*\n\s+items:\s*\n\s+type: string', '$1'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-output-schema'
        }

        It 'Fails when scope_reviewer lacks an output schema' {
            $yaml = ($script:ValidYaml) -replace '(?ms)(name: scope_reviewer.*?)\n\s+output:\s*\n\s+verdict:\s*\n\s+type: string\s*\n\s+feedback:\s*\n\s+type: string\s*\n\s+issues:\s*\n\s+type: array\s*\n\s+items:\s*\n\s+type: string', '$1'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-output-schema'
        }

        It 'Fails when max_iterations is too low' {
            $yaml = ($script:ValidYaml) -replace 'max_iterations: 300', 'max_iterations: 100'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'max-iterations-too-low'
        }

        It 'Fails when route target references nonexistent agent' {
            $yaml = ($script:ValidYaml) -replace 'to: scope_closer', 'to: nonexistent_closer'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'invalid-route-target'
        }

        It 'Fails when scope_revise_counter agent is missing (AB#3125)' {
            $yaml = ($script:ValidYaml) -replace '(?ms)  - name: scope_revise_counter\r?\n.*?\r?\n  - name: scope_revise_cap_gate', '  - name: scope_revise_cap_gate'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-scope-revise-node'
        }

        It 'Fails when scope_revise_cap_gate human_gate is missing (AB#3125)' {
            $yaml = ($script:ValidYaml) -replace '(?ms)  - name: scope_revise_cap_gate\r?\n.*?\r?\n  - name: scope_revise_reset', '  - name: scope_revise_reset'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-scope-revise-node'
        }

        It 'Fails when scope_revise_reset script is missing (AB#3125)' {
            $yaml = ($script:ValidYaml) -replace '(?ms)  - name: scope_revise_reset\r?\n.*?\r?\n  - name: mg_pr_open', '  - name: mg_pr_open'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-scope-revise-node'
        }

        It 'Fails when scope_revise_cap_gate is missing the re_loop option (AB#3125)' {
            $yaml = ($script:ValidYaml) -replace 'value: re_loop', 'value: continue_revising'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-scope-revise-gate-option'
        }

        It 'Fails when scope_reviewer routes changes_requested directly to primary_router (AB#3125)' {
            # Strip the cap-gate trio AND rewire scope_reviewer to bypass it.
            # The "scope-reviewer-bypasses-cap" check fires when no route in
            # the scope_reviewer block targets scope_revise_counter.
            $yaml = ($script:ValidYaml) `
                -replace 'to: scope_revise_counter', 'to: primary_router'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-merge-group.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-merge-group.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'scope-reviewer-bypasses-cap'
        }
    }
}
