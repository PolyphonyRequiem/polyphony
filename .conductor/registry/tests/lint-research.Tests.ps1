BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-research.ps1'
    $script:ResearchYaml = Join-Path $PSScriptRoot '..' 'workflows' 'research.yaml'
    $script:PromptFile = Join-Path $PSScriptRoot '..' 'prompts' 'research-assistant.md'
}

Describe 'lint-research.ps1' {

    Context 'Production research.yaml validation' {

        It 'Passes on the real research.yaml' {
            $script:ResearchYaml | Should -Exist
            $output = pwsh -NoProfile -File $script:LintScript 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Prompt file exists' {
            $script:PromptFile | Should -Exist
        }

        It 'research.yaml is registered in index.yaml' {
            $indexPath = Join-Path $PSScriptRoot '..' 'index.yaml'
            $indexPath | Should -Exist
            $indexContent = Get-Content $indexPath -Raw
            $indexContent | Should -Match 'research:'
            $indexContent | Should -Match 'workflows/research\.yaml'
        }
    }

    Context 'Structural requirements' {

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-research-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            $script:PromptsDir = Join-Path $script:TempRoot 'prompts'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            New-Item $script:PromptsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-research.ps1')
            # Create dummy prompt file so prompt-file check passes
            Set-Content (Join-Path $script:PromptsDir 'research-assistant.md') 'prompt placeholder'
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Fails when workflow name is wrong' {
            $yaml = @'
workflow:
  name: wrong-name
  entry_point: validate_input
  input:
    topic:
      type: string
    work_item_id:
      type: number
    scratch_slug:
      type: string
    archive_path:
      type: string

output:
  findings_path: "{% if write_findings is defined %}x{% endif %}"
  citation_count: "{% if research_assistant is defined %}0{% endif %}"
  topic: "{{ workflow.input.topic }}"

agents:
  - name: validate_input
    type: script
    command: pwsh
    routes:
      - to: scan_archive
  - name: scan_archive
    type: script
    command: pwsh
    routes:
      - to: research_assistant
  - name: research_assistant
    type: agent
    model: claude-haiku-4.5
    output:
      summary:
        type: string
      findings:
        type: array
    prompt: !file prompts/research-assistant.md
    routes:
      - to: write_findings
  - name: write_findings
    type: script
    command: pwsh
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'research.yaml') $yaml
            $lintCopy = Join-Path $script:TestsDir 'lint-research.ps1'
            $output = pwsh -NoProfile -File $lintCopy 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'wrong-workflow-name'
        }

        It 'Fails when entry_point is wrong' {
            $yaml = @'
workflow:
  name: research
  entry_point: wrong_entry
  input:
    topic:
      type: string
    work_item_id:
      type: number
    scratch_slug:
      type: string
    archive_path:
      type: string

output:
  findings_path: "{% if write_findings is defined %}x{% endif %}"
  citation_count: "{% if research_assistant is defined %}0{% endif %}"
  topic: "{{ workflow.input.topic }}"

agents:
  - name: validate_input
    type: script
    command: pwsh
    routes:
      - to: scan_archive
  - name: scan_archive
    type: script
    command: pwsh
    routes:
      - to: research_assistant
  - name: research_assistant
    type: agent
    model: claude-haiku-4.5
    output:
      summary:
        type: string
      findings:
        type: array
    prompt: !file prompts/research-assistant.md
    routes:
      - to: write_findings
  - name: write_findings
    type: script
    command: pwsh
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'research.yaml') $yaml
            $lintCopy = Join-Path $script:TestsDir 'lint-research.ps1'
            $output = pwsh -NoProfile -File $lintCopy 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'wrong-entry-point'
        }

        It 'Fails when required input is missing' {
            $yaml = @'
workflow:
  name: research
  entry_point: validate_input
  input:
    topic:
      type: string
    work_item_id:
      type: number

output:
  findings_path: "{% if write_findings is defined %}x{% endif %}"
  citation_count: "{% if research_assistant is defined %}0{% endif %}"
  topic: "{{ workflow.input.topic }}"

agents:
  - name: validate_input
    type: script
    command: pwsh
    routes:
      - to: scan_archive
  - name: scan_archive
    type: script
    command: pwsh
    routes:
      - to: research_assistant
  - name: research_assistant
    type: agent
    model: claude-haiku-4.5
    output:
      summary:
        type: string
      findings:
        type: array
    prompt: !file prompts/research-assistant.md
    routes:
      - to: write_findings
  - name: write_findings
    type: script
    command: pwsh
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'research.yaml') $yaml
            $lintCopy = Join-Path $script:TestsDir 'lint-research.ps1'
            $output = pwsh -NoProfile -File $lintCopy 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-input-scratch_slug'
        }

        It 'Fails when required node is missing' {
            $yaml = @'
workflow:
  name: research
  entry_point: validate_input
  input:
    topic:
      type: string
    work_item_id:
      type: number
    scratch_slug:
      type: string
    archive_path:
      type: string

output:
  findings_path: "{% if write_findings is defined %}x{% endif %}"
  citation_count: "{% if research_assistant is defined %}0{% endif %}"
  topic: "{{ workflow.input.topic }}"

agents:
  - name: validate_input
    type: script
    command: pwsh
    routes:
      - to: research_assistant
  - name: research_assistant
    type: agent
    model: claude-haiku-4.5
    output:
      summary:
        type: string
      findings:
        type: array
    prompt: !file prompts/research-assistant.md
    routes:
      - to: write_findings
  - name: write_findings
    type: script
    command: pwsh
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'research.yaml') $yaml
            $lintCopy = Join-Path $script:TestsDir 'lint-research.ps1'
            $output = pwsh -NoProfile -File $lintCopy 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-node-scan_archive'
        }

        It 'Fails when research_assistant uses a non-lightweight model' {
            $yaml = @'
workflow:
  name: research
  entry_point: validate_input
  input:
    topic:
      type: string
    work_item_id:
      type: number
    scratch_slug:
      type: string
    archive_path:
      type: string

output:
  findings_path: "{% if write_findings is defined %}x{% endif %}"
  citation_count: "{% if research_assistant is defined %}0{% endif %}"
  topic: "{{ workflow.input.topic }}"

agents:
  - name: validate_input
    type: script
    command: pwsh
    routes:
      - to: scan_archive
  - name: scan_archive
    type: script
    command: pwsh
    routes:
      - to: research_assistant
  - name: research_assistant
    type: agent
    model: claude-opus-4.6
    output:
      summary:
        type: string
      findings:
        type: array
    prompt: !file prompts/research-assistant.md
    routes:
      - to: write_findings
  - name: write_findings
    type: script
    command: pwsh
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'research.yaml') $yaml
            $lintCopy = Join-Path $script:TestsDir 'lint-research.ps1'
            $output = pwsh -NoProfile -File $lintCopy 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'research-assistant-not-lightweight'
        }

        It 'Fails when route target is invalid' {
            $yaml = @'
workflow:
  name: research
  entry_point: validate_input
  input:
    topic:
      type: string
    work_item_id:
      type: number
    scratch_slug:
      type: string
    archive_path:
      type: string

output:
  findings_path: "{% if write_findings is defined %}x{% endif %}"
  citation_count: "{% if research_assistant is defined %}0{% endif %}"
  topic: "{{ workflow.input.topic }}"

agents:
  - name: validate_input
    type: script
    command: pwsh
    routes:
      - to: nonexistent_node
  - name: scan_archive
    type: script
    command: pwsh
    routes:
      - to: research_assistant
  - name: research_assistant
    type: agent
    model: claude-haiku-4.5
    output:
      summary:
        type: string
      findings:
        type: array
    prompt: !file prompts/research-assistant.md
    routes:
      - to: write_findings
  - name: write_findings
    type: script
    command: pwsh
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'research.yaml') $yaml
            $lintCopy = Join-Path $script:TestsDir 'lint-research.ps1'
            $output = pwsh -NoProfile -File $lintCopy 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'invalid-route-target'
        }
    }
}
