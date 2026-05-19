BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-sentiment-loop-consistency.ps1'

    # Build a synthetic minimal workflow set in $TestDrive so production
    # YAMLs are never mutated. Each call to New-FixtureSet returns a fresh
    # directory containing valid plan-level.yaml, github-pr.yaml, ado-pr.yaml.
    function script:New-AnalyzerBlock {
        param([string]$NextNode = 'revise_counter')
        @"
  - name: pr_feedback_analyzer
    type: agent
    model: claude-sonnet-4.6
    tools:
      - filesystem
    prompt: |
      Analyze PR feedback. Decide has_negative_feedback.
    output:
      has_negative_feedback:
        type: boolean
      feedback_summary:
        type: string
      feedback_digest:
        type: string
      reasoning:
        type: string
    routes:
      - to: $NextNode
        when: "{{ pr_feedback_analyzer.output.has_negative_feedback == true }}"
      - to: terminal_emitter
"@
    }

    function script:New-CounterNodes {
        param([string]$Prefix)
        @"
  - name: revise_counter
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-Command"
      - "echo conductor-$Prefix-revise-{{ workflow.input.work_item_id }}.json"
    routes:
      - to: terminal_emitter

  - name: pending_poll_counter
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-Command"
      - "echo conductor-$Prefix-pending-poll-{{ workflow.input.work_item_id }}.json"
    routes:
      - to: terminal_emitter
"@
    }

    function script:New-FixtureSet {
        $dir = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $dir -Force | Out-Null

        $planAnalyzer   = (New-AnalyzerBlock)
        $githubAnalyzer = (New-AnalyzerBlock)
        $adoAnalyzer    = (New-AnalyzerBlock)
        $planCounters   = (New-CounterNodes -Prefix 'plan')
        $githubCounters = (New-CounterNodes -Prefix 'github-pr')
        $adoCounters    = (New-CounterNodes -Prefix 'ado-pr')

        # plan-level — no feedback_summary in workflow.output, no
        # closed_unmerged_emitter (exempt by design).
        $plan = @"
workflow:
  name: plan-level
  entry_point: pr_feedback_analyzer
  input:
    work_item_id:
      type: number

output:
  reasoning: "{{ pr_feedback_analyzer.output.reasoning | default('') }}"

agents:
$planAnalyzer

$planCounters

  - name: terminal_emitter
    type: script
    command: echo
    args: ["done"]
    routes: []
"@
        $plan | Set-Content -Path (Join-Path $dir 'plan-level.yaml') -Encoding utf8

        # github-pr — has feedback_summary in workflow.output AND
        # closed_unmerged_emitter.
        $github = @"
workflow:
  name: github-pr
  entry_point: pr_feedback_analyzer
  input:
    work_item_id:
      type: number
    pr_number:
      type: number

output:
  merged: "{{ false }}"
  feedback_summary: "{{ pr_feedback_analyzer.output.feedback_summary | default('') }}"

agents:
$githubAnalyzer

$githubCounters

  - name: closed_unmerged_emitter
    type: script
    command: echo
    args: ["closed-unmerged"]
    routes: []

  - name: terminal_emitter
    type: script
    command: echo
    args: ["done"]
    routes: []
"@
        $github | Set-Content -Path (Join-Path $dir 'github-pr.yaml') -Encoding utf8

        # ado-pr — same shape as github-pr.
        $ado = @"
workflow:
  name: ado-pr
  entry_point: pr_feedback_analyzer
  input:
    work_item_id:
      type: number
    pr_number:
      type: number

output:
  merged: "{{ false }}"
  feedback_summary: "{{ pr_feedback_analyzer.output.feedback_summary | default('') }}"

agents:
$adoAnalyzer

$adoCounters

  - name: closed_unmerged_emitter
    type: script
    command: echo
    args: ["closed-unmerged"]
    routes: []

  - name: terminal_emitter
    type: script
    command: echo
    args: ["done"]
    routes: []
"@
        $ado | Set-Content -Path (Join-Path $dir 'ado-pr.yaml') -Encoding utf8

        return $dir
    }

    function script:Invoke-Lint {
        param([string]$Dir)
        $output = pwsh -NoProfile -File $script:LintScript -WorkflowsDir $Dir 2>&1
        return @{ Output = ($output -join "`n"); ExitCode = $LASTEXITCODE }
    }
}

Describe 'lint-sentiment-loop-consistency.ps1' {

    Context 'Production workflows' {
        It 'Passes on the real workflows under .conductor/registry/workflows' {
            $output = pwsh -NoProfile -File $script:LintScript 2>&1
            $LASTEXITCODE | Should -Be 0 -Because ($output -join "`n")
        }
    }

    Context 'Synthetic baseline' {
        It 'Passes on a freshly generated valid fixture set' {
            $dir = New-FixtureSet
            $r = Invoke-Lint -Dir $dir
            $r.ExitCode | Should -Be 0 -Because $r.Output
        }
    }

    Context 'Missing pr_feedback_analyzer' {
        It 'Fails when github-pr.yaml has no analyzer block' {
            $dir = New-FixtureSet
            $path = Join-Path $dir 'github-pr.yaml'
            (Get-Content $path -Raw) -replace '(?ms)^  - name: pr_feedback_analyzer.*?(?=^  - name: revise_counter)', '' |
                Set-Content -Path $path -Encoding utf8
            $r = Invoke-Lint -Dir $dir
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'missing-analyzer'
            $r.Output | Should -Match 'github-pr.yaml'
        }
    }

    Context 'Analyzer model drift' {
        It 'Fails when ado-pr.yaml uses a different model' {
            $dir = New-FixtureSet
            $path = Join-Path $dir 'ado-pr.yaml'
            (Get-Content $path -Raw) -replace 'model: claude-sonnet-4\.6', 'model: claude-opus-4.7' |
                Set-Content -Path $path -Encoding utf8
            $r = Invoke-Lint -Dir $dir
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'analyzer-wrong-model'
        }
    }

    Context 'Analyzer output schema drift' {
        It 'Fails when an output key is renamed' {
            $dir = New-FixtureSet
            $path = Join-Path $dir 'plan-level.yaml'
            (Get-Content $path -Raw) -replace 'has_negative_feedback', 'negative_feedback_present' |
                Set-Content -Path $path -Encoding utf8
            $r = Invoke-Lint -Dir $dir
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'analyzer-output-drift'
            $r.Output | Should -Match 'has_negative_feedback'
        }

        It 'Fails when an output type is wrong' {
            $dir = New-FixtureSet
            $path = Join-Path $dir 'github-pr.yaml'
            $content = Get-Content $path -Raw
            # Flip has_negative_feedback from boolean to string
            $content = $content -replace '(?ms)(has_negative_feedback:\s*\r?\n\s*type:\s*)boolean', '${1}string'
            $content | Set-Content -Path $path -Encoding utf8
            $r = Invoke-Lint -Dir $dir
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'analyzer-output-drift'
        }
    }

    Context 'Negative-feedback route' {
        It 'Fails when the has_negative_feedback==true route is removed' {
            $dir = New-FixtureSet
            $path = Join-Path $dir 'plan-level.yaml'
            (Get-Content $path -Raw) -replace '(?ms)\s*- to: revise_counter\s*\r?\n\s*when:.*?has_negative_feedback == true \}\}"', '' |
                Set-Content -Path $path -Encoding utf8
            $r = Invoke-Lint -Dir $dir
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'analyzer-missing-negative-route'
        }
    }

    Context 'Counter file naming' {
        It 'Fails when github-pr.yaml uses the wrong counter prefix' {
            $dir = New-FixtureSet
            $path = Join-Path $dir 'github-pr.yaml'
            (Get-Content $path -Raw) -replace 'conductor-github-pr-revise', 'conductor-pr-revise' |
                Set-Content -Path $path -Encoding utf8
            $r = Invoke-Lint -Dir $dir
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'missing-revise-counter-key'
        }

        It 'Fails when revise_counter node is absent' {
            $dir = New-FixtureSet
            $path = Join-Path $dir 'ado-pr.yaml'
            (Get-Content $path -Raw) -replace '(?ms)^  - name: revise_counter.*?(?=^  - name: pending_poll_counter)', '' |
                Set-Content -Path $path -Encoding utf8
            $r = Invoke-Lint -Dir $dir
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'missing-revise-counter-node'
        }
    }

    Context 'feedback_summary workflow output' {
        It 'Fails when github-pr.yaml drops feedback_summary from workflow.output' {
            $dir = New-FixtureSet
            $path = Join-Path $dir 'github-pr.yaml'
            (Get-Content $path -Raw) -replace '(?m)^  feedback_summary:.*$', '' |
                Set-Content -Path $path -Encoding utf8
            $r = Invoke-Lint -Dir $dir
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'missing-feedback-summary-output'
        }

        It 'Does not require feedback_summary on plan-level.yaml' {
            $dir = New-FixtureSet
            # Baseline plan-level fixture already lacks feedback_summary.
            $r = Invoke-Lint -Dir $dir
            $r.ExitCode | Should -Be 0 -Because $r.Output
        }
    }

    Context 'closed_unmerged_emitter terminal' {
        It 'Fails when ado-pr.yaml drops closed_unmerged_emitter' {
            $dir = New-FixtureSet
            $path = Join-Path $dir 'ado-pr.yaml'
            (Get-Content $path -Raw) -replace '(?ms)^  - name: closed_unmerged_emitter.*?(?=^  - name: terminal_emitter)', '' |
                Set-Content -Path $path -Encoding utf8
            $r = Invoke-Lint -Dir $dir
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'missing-closed-unmerged-emitter'
        }

        It 'Does not require closed_unmerged_emitter on plan-level.yaml' {
            $dir = New-FixtureSet
            # Baseline plan-level fixture already lacks the emitter (uses gate).
            $r = Invoke-Lint -Dir $dir
            $r.ExitCode | Should -Be 0 -Because $r.Output
        }
    }
}
