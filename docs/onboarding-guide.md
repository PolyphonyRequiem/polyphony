# Onboarding Guide — v2 SDLC Workflow Configuration

A step-by-step guide for onboarding a new repository to the `twig-sdlc-v2-full@twig`
conductor workflow. This guide uses **cloudvault** (CMMI process template) as a worked
example throughout. After following this guide, you will have a working `.conductor/`
configuration for any ADO process template.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Process Template Selection](#2-process-template-selection)
3. [Bootstrap](#3-bootstrap)
4. [Type Definitions](#4-type-definitions)
5. [Templates](#5-templates)
6. [Agent Guidance](#6-agent-guidance)
7. [Profile](#7-profile)
8. [Config Validation](#8-config-validation)
9. [First Run](#9-first-run)
10. [Cloudvault Worked Example](#10-cloudvault-worked-example)

---

## 1. Prerequisites

Before starting, ensure the following are in place:

- **Phases 1–4 complete** — The Polyphony core engine, generic workflow scripts,
  workflow YAML refactoring, and validation/testing phases must all be merged.
- **twig CLI installed** — The AOT-compiled `twig` binary is available on your PATH
  (typically at `~/.twig/bin/twig`).
- **Polyphony CLI installed** — The AOT-compiled `polyphony` binary is available on
  your PATH (typically at `~/.twig/bin/polyphony`). Build it with `publish-local.ps1`
  from the polyphony repo if not already installed.
- **ADO workspace configured** — Your repo has a `.twig/config` file pointing to the
  correct ADO organization and project. If not, run `twig init`.
- **Git repository** — Your repo uses git with a `main` branch as the default target.

Verify your setup:

```bash
twig --version        # Confirm twig is installed
polyphony --version   # Confirm polyphony is installed
```

---

## 2. Process Template Selection

The v2 workflow supports four standard ADO process templates. You need to identify
which one your project uses.

### Identifying Your Process Template

Check your ADO project settings:

1. Go to **Project Settings → Boards → Process** in Azure DevOps.
2. Note the process name (Basic, Agile, Scrum, or CMMI).

Alternatively, if your repo already has `.twig/config`, the bootstrap script can
auto-detect it from the `process_template` field.

### Supported Templates and Their Type Hierarchies

| Template | Top-level (plannable) | Mid-level (plannable + implementable) | Leaf (implementable) |
|----------|----------------------|--------------------------------------|---------------------|
| **Basic** | Epic | Issue | Task |
| **Agile** | Epic | User Story | Task |
| **Scrum** | Epic | Product Backlog Item | Task |
| **CMMI** | Epic | Requirement | Task |

> **Cloudvault example:** Cloudvault uses the **CMMI** process template. Its hierarchy
> is Epic → Requirement → Task. However, cloudvault also uses custom types like
> Scenario and Deliverable — we'll cover how to handle those in
> [Section 4](#4-type-definitions).

### State Mappings Per Template

Each template uses different state names for the same workflow transitions:

| Template | Active State | Done State | Notes |
|----------|-------------|------------|-------|
| **Basic** | Doing | Done | |
| **Agile** | Active | Closed | |
| **Scrum** | In Progress | Done | Mid-level uses "Committed" |
| **CMMI** | Active | Closed | |

---

## 3. Bootstrap

The `bootstrap-conductor.ps1` script generates a complete set of stub `.conductor/`
files, giving you a starting point to customize.

### Running the Bootstrap

From your repository root:

```powershell
# Auto-detect from .twig/config (if process_template is set)
./scripts/bootstrap-conductor.ps1

# Or specify explicitly
./scripts/bootstrap-conductor.ps1 -ProcessTemplate CMMI

# To overwrite existing files
./scripts/bootstrap-conductor.ps1 -ProcessTemplate CMMI -Force

# To generate in a different directory
./scripts/bootstrap-conductor.ps1 -ProcessTemplate CMMI -OutputPath ./my-repo
```

> **Cloudvault example:**
> ```powershell
> cd ~/projects/cloudvault
> ~/projects/polyphony/scripts/bootstrap-conductor.ps1 -ProcessTemplate CMMI
> ```

### What Gets Generated

The bootstrap creates the following directory structure:

```
.conductor/
├── process-config.yaml                 # Type capabilities, transitions, branch strategy
├── profile.yaml                        # Project metadata, tech stack, build commands
├── agent-guidance/
│   ├── architect.md                    # Guidance for the architect agent
│   ├── coder.md                        # Guidance for the coder agent
│   └── reviewer.md                     # Guidance for the reviewer agent
└── work-item-types/
    ├── epic.md                         # Epic type definition
    ├── requirement.md                  # Mid-level type definition (CMMI)
    ├── task.md                         # Task type definition
    └── templates/
        ├── epic-template.md            # Epic description template
        ├── requirement-template.md     # Requirement description template
        └── task-template.md            # Task description template
```

### Review and Customize

The generated files contain `<!-- TODO -->` placeholders. The next sections walk
through customizing each file type. Don't skip customization — the stubs are
functional but generic.

---

## 4. Type Definitions

Type definitions live in `.conductor/work-item-types/` as markdown files. They tell
agents what each work item type represents, how it's used, and what conventions to
follow.

### File Naming

Use the type name in lowercase with spaces replaced by hyphens:

| Type Name | File Name |
|-----------|-----------|
| Epic | `epic.md` |
| User Story | `user-story.md` |
| Product Backlog Item | `product-backlog-item.md` |
| Requirement | `requirement.md` |
| Task | `task.md` |
| Scenario | `scenario.md` |

### Required Sections

Every type definition should include these sections:

```markdown
# {TypeName} — Work Item Type Definition ({Template} Process)

## Definition
What this type represents in your project.

## Purpose
What question does this type answer?
Format: "A {Type} answers: **'...'**"

## Audience
| Role | How They Use {Type}s |
|------|---------------------|
| **Project Owner** | ... |
| **Contributor** | ... |
| **AI Agent** | ... |

## Naming Conventions
Rules for naming work items of this type.

## Description Template
See: `templates/{slug}-template.md`
```

### Optional Sections

Add these when relevant:

- **Ownership** — Who creates, assigns, and reviews items of this type.
- **In Scope / Out of Scope** — What belongs and doesn't belong in this type.
- **Hierarchy Rules** — Parent/child constraints (e.g., "Epics contain only Issues").
- **Language Guidelines** — Tone and detail expectations per description section.
- **Relationship to Plan Documents** — Whether this type gets a plan doc.

### Cloudvault Example: Scenario Type Definition

Cloudvault uses a custom "Scenario" type that acts as a mid-level plannable container
(similar to Issue in Basic or User Story in Agile). Here's what a `scenario.md` type
definition looks like:

```markdown
# Scenario — Work Item Type Definition (CMMI Process)

## Definition

A Scenario represents a user-facing workflow or capability within cloudvault.
Scenarios describe end-to-end behaviors that deliver value — they are the
primary unit of planning and decompose into Deliverables or Tasks when
implementation scope is large.

## Purpose

A Scenario answers: **"What user-facing capability are we delivering, and
how will we verify it works end-to-end?"** Scenarios bridge strategic Epics
(what outcome we want) and tactical Deliverables/Tasks (what to build).

## Audience

| Role | How They Use Scenarios |
|------|----------------------|
| **Project Owner** | Creates Scenarios, defines acceptance criteria and user workflows. |
| **Contributor** | Implements Scenarios or their child Deliverables/Tasks. |
| **AI Agent** | Plans decomposition and implements directly when scope is small. |

## Naming Conventions

- Start with the actor or system: "User creates vault entry", "System rotates keys"
- Describe the end-to-end behavior, not implementation details
- Keep under 80 characters

## Description Template

See: `templates/scenario-template.md`

## Hierarchy Rules

- Scenarios live under Epics
- Scenarios contain Deliverables and/or Tasks
- Scenarios may be self-referential (nested Scenarios) up to max_nesting_depth
```

---

## 5. Templates

Templates live in `.conductor/work-item-types/templates/` and define the expected
structure of a work item's **Description** field. Agents use these when creating
new work items to ensure consistent formatting.

### File Naming

Follow the pattern `{type-slug}-template.md`:

| Type | Template File |
|------|--------------|
| Epic | `epic-template.md` |
| Scenario | `scenario-template.md` |
| Deliverable | `deliverable-template.md` |
| Task | `task-template.md` |

### Template Structure

Templates use markdown with `<!-- TODO -->` placeholder comments and angle-bracket
instructions. Here's the general pattern:

**For plannable types** (Epic, Scenario):

```markdown
## Strategic Objective
<What capability or improvement this delivers. Why is the investment worthwhile?>

## Success Criteria
<Measurable outcomes that define "done".>
- <Criterion 1: concrete, verifiable>
- <Criterion 2: concrete, verifiable>

## Scope

### In Scope
<What's included>

### Out of Scope
<What's explicitly excluded>

## Child {ChildType}s
<Populated during planning>
- #{id} — {title} — {contribution to objective}
```

**For implementable types** (Task, Bug):

```markdown
## What to Change
<Specific files, classes, methods, configurations to modify.
Name every touchpoint.>

## How to Change
<Implementation approach. Step-by-step for non-obvious changes.>

## Acceptance Criteria
- [ ] <Task-specific criterion — binary pass/fail>
- [ ] Build passes with zero errors and warnings
- [ ] Relevant tests pass

## Context (optional)
<Dependencies, gotchas, related code paths.>
```

**For dual-capability types** (Issue, Requirement, Scenario):

```markdown
## Summary
<2–3 sentences: what, why, expected outcome>

## Problem / Motivation
<What's broken, slow, missing, or poorly structured?>

## Proposed Approach
<High-level technical approach. Reference file paths, class names.>

## Acceptance Criteria
- [ ] <Criterion 1 — measurable, binary pass/fail>
- [ ] Build passes with zero errors and warnings
- [ ] All existing tests pass; new tests cover changed behavior

## Child Tasks (if decomposed)
<Populated during decomposition — omit for directly-implemented items>

## Context (optional)
<Plan document link, architecture references, related code paths>
```

### Cloudvault Example: Deliverable Template

Cloudvault uses a "Deliverable" type as an actionable container grouping related
Tasks. Here's what `deliverable-template.md` looks like:

```markdown
## Deliverable Summary
<What this deliverable produces. 2-3 sentences.>

## Components
<List the concrete artifacts or changes this deliverable produces>
- <Component 1: file, module, or config>
- <Component 2>

## Acceptance Criteria
- [ ] <Criterion — measurable, binary pass/fail>
- [ ] All component Tasks completed
- [ ] Integration verified across components

## Dependencies (optional)
<Other deliverables or external dependencies>
```

---

## 6. Agent Guidance

Agent guidance files live in `.conductor/agent-guidance/` and provide project-specific
instructions for each AI agent role. These are injected into agent prompts via Jinja2
templating during workflow execution.

### Available Roles

The bootstrap generates guidance files for three roles:

| Role | File | Purpose |
|------|------|---------|
| **Architect** | `architect.md` | Planning, decomposition, estimation |
| **Coder** | `coder.md` | Implementation, testing, code style |
| **Reviewer** | `reviewer.md` | Code review, quality standards |

### What to Include

Guidance files should capture project-specific knowledge that agents need but
can't infer from the code alone:

- **Architecture patterns** — "We use the Result pattern for error handling",
  "All commands go through ConsoleAppFramework"
- **Conventions** — "Sealed classes by default", "Primary constructors for DI"
- **Constraints** — "AOT-safe code only", "No reflection"
- **Testing expectations** — "xUnit + Shouldly + NSubstitute",
  "Every public method needs at least one test"
- **Build/deploy notes** — "Run `publish-local.ps1` to deploy locally"

### Graceful Degradation

Agent guidance is **optional**. If a guidance file is missing, the workflow continues
without it — the agent runs with its default behavior. This means:

- You can start with empty guidance and add detail over time.
- A missing file is not an error (but `polyphony validate-config` will emit a
  warning if the `agent-guidance/` directory doesn't exist).
- Add guidance when you notice agents making mistakes that better instructions
  would prevent.

### Cloudvault Example: Architect Guidance

```markdown
# Architect Guidance

## Responsibilities

- Decompose Scenarios into Deliverables and Tasks
- Estimate effort using the CMMI process's complexity model
- Ensure each Task is self-contained and implementable in a single session

## Conventions

- Cloudvault uses a layered architecture: API → Service → Repository → Data
- All vault operations must go through the VaultService abstraction
- Key rotation is handled by the KeyRotationService — never manipulate keys directly
- Use the Options pattern for all configuration (no static config access)

## Constraints

- .NET 8 LTS — do not use preview features
- All secrets must use Azure Key Vault, never appsettings.json
- API endpoints must follow the existing REST conventions in /api/v2/

## Estimation

- Tasks estimated > 4 hours should be split
- Include integration test time in estimates
- Key rotation scenarios require extra review time (security-sensitive)
```

---

## 7. Profile

The `profile.yaml` file describes your project's metadata, tech stack, build
commands, and estimation settings. It's used by agents to understand how to
build, test, and work with your codebase.

### Profile Structure

```yaml
# Project profile for conductor SDLC workflows

project:
  name: <Project name>
  description: >
    <Brief project description — 2-3 sentences>
  repository: <org/repo>

tech_stack:
  language: <Primary language and version>
  framework: <Framework and version>
  testing: <Test framework>

build:
  restore: <Restore/install dependencies command>
  build: <Build command>
  test: <Test command>
  publish: <Publish/deploy command (optional)>

conventions:
  - <Convention 1>
  - <Convention 2>

estimation:
  unit: hours          # or "story_points"
  confidence_default: medium

mcp_servers:           # Optional: MCP servers available to agents
  - <server-name>
```

### Cloudvault Example: profile.yaml

```yaml
project:
  name: Cloudvault
  description: >
    Cloud-native secrets management platform with zero-trust key rotation,
    multi-tenant vault isolation, and Azure Key Vault integration.
  repository: contoso/cloudvault

tech_stack:
  language: C# 12
  framework: .NET 8 LTS (ASP.NET Core)
  serialization: System.Text.Json
  testing: xUnit + FluentAssertions + Moq

build:
  restore: dotnet restore
  build: dotnet build --no-restore
  test: dotnet test --no-restore
  publish: dotnet publish src/Cloudvault.Api -c Release -o artifacts

conventions:
  - Layered architecture (API → Service → Repository → Data)
  - Options pattern for all configuration
  - Result pattern for error handling (no exceptions for flow control)
  - All API endpoints versioned under /api/v2/
  - Integration tests use TestContainers for database dependencies

estimation:
  unit: hours
  confidence_default: medium
```

---

## 8. Config Validation

After creating your `.conductor/` configuration, validate it with Polyphony before
running the workflow.

### Running Validation

```bash
# JSON output (default) — for programmatic consumption
polyphony validate-config --config .conductor

# Human-readable output — for interactive use
polyphony validate-config --config .conductor --output human
```

### Interpreting Results

**Valid configuration:**

```
Configuration is valid.
```

**Valid with warnings:**

```
Warnings (2):
  [V-9] Type definition file missing: .conductor/work-item-types/task.md
  [V-11] Agent guidance file missing: .conductor/agent-guidance/architect.md
Configuration is valid (with warnings).
```

Warnings are informational — your configuration will work, but you may want to
address them for better agent behavior.

**Invalid configuration (errors):**

```
Errors (1):
  [V-1] process_template is required.
Configuration has 1 error(s).
```

Errors must be fixed before the workflow will run correctly.

### Common Validation Errors

| Rule ID | Meaning | Fix |
|---------|---------|-----|
| V-1 | Missing `process_template` | Add `process_template: <name>` to process-config.yaml |
| V-2 | No types defined | Add at least one entry under `types:` |
| V-3 | Type has no capabilities | Add `capabilities: [plannable]` or `[implementable]` (or both) |
| V-4 | Type has invalid capability value | Use only `plannable` or `implementable` as capability values |
| V-5 | Type has no transitions | Add transition mappings under `transitions:` for the type |
| V-6 | Transition references undefined type | Ensure all keys in `transitions:` exist in `types:` |
| V-7 | Duplicate type name (case-insensitive) | Type names must be unique regardless of case |
| V-8 | `allowed_child_types` references undefined type | Ensure all `allowed_child_types` values exist in `types:` |

### Common Validation Warnings

| Rule ID | Meaning | Recommendation |
|---------|---------|---------------|
| V-9 | Type definition file missing | Create `.conductor/work-item-types/{slug}.md` for each type |
| V-10 | Template file missing | Create `.conductor/work-item-types/templates/{slug}-template.md` for each type |
| V-11 | Architect guidance file missing | Create `.conductor/agent-guidance/architect.md` |
| V-12 | Coder guidance file missing | Create `.conductor/agent-guidance/coder.md` |
| V-13 | Reviewer guidance file missing | Create `.conductor/agent-guidance/reviewer.md` |
| V-14 | Profile file missing | Create `.conductor/profile.yaml` |

---

## 9. First Run

Once validation passes, you're ready to run the v2 SDLC workflow.

### Invoking the Workflow

The workflow is invoked through conductor with a work item ID:

```bash
twig-sdlc-v2-full@twig --work-item <id>
```

The workflow automatically detects your `.conductor/process-config.yaml` and uses
it for routing decisions. If no config is found, it falls back to the legacy
`twig-sdlc-full@twig` workflow.

### What to Expect

1. **Phase Detection** — Polyphony reads the work item tree and determines the
   current SDLC phase (planning, implementation, review, etc.).
2. **Routing** — Based on type capabilities and current state, Polyphony decides
   the next action (plan, decompose, implement, review).
3. **Agent Execution** — The appropriate agent (architect, coder, reviewer) runs
   with your type definitions and guidance injected into its prompt.
4. **Branch Management** — Feature branches are created following your
   `branch_strategy` patterns.
5. **PR Lifecycle** — PRs are created, reviewed, and merged according to your
   `review_policies`.

### Debugging Routing Issues

If the workflow doesn't behave as expected:

**1. Check Polyphony routing directly:**

```bash
polyphony route --work-item <id> --config .conductor/process-config.yaml
```

This outputs a JSON routing decision showing the detected phase, next action,
and hierarchy analysis. Look for:

- `phase` — Is it what you expect?
- `action` — Is the right action being taken?
- `type_capabilities` — Are your types correctly configured?

**2. Verify state transitions:**

```bash
polyphony validate --work-item <id> --event <target-state>
```

This checks whether the proposed transition is valid for the work item's type.

**3. Inspect the hierarchy:**

```bash
polyphony hierarchy --work-item <id> --depth 3
```

This shows the work item tree with type capabilities at each level.

**4. Common routing problems:**

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| Work item skipped | Type missing `implementable` capability | Add `implementable` to the type's capabilities |
| Infinite recursion | `max_nesting_depth` too high or self-referential without bound | Set a reasonable `max_nesting_depth` (1–3) |
| Wrong agent runs | Type capabilities misconfigured | Check that plannable types get architect, implementable get coder |
| PR not auto-merging | Review policy requires human review | Update `review_policies` to set `auto_merge: true` for PG PRs |
| Branch name wrong | Branch strategy pattern incorrect | Fix patterns in `branch_strategy` section |

---

## 10. Cloudvault Worked Example

This section shows a complete, annotated `.conductor/` configuration for cloudvault,
a CMMI-process repository. Use this as a reference when building your own config.

### Directory Structure

```
cloudvault/
└── .conductor/
    ├── process-config.yaml
    ├── profile.yaml
    ├── agent-guidance/
    │   ├── architect.md
    │   ├── coder.md
    │   └── reviewer.md
    └── work-item-types/
        ├── epic.md
        ├── scenario.md
        ├── deliverable.md
        ├── task.md
        └── templates/
            ├── epic-template.md
            ├── scenario-template.md
            ├── deliverable-template.md
            └── task-template.md
```

### process-config.yaml (Annotated)

```yaml
# The ADO process template this repo uses.
# Valid values: Basic, Agile, Scrum, CMMI
process_template: CMMI

# Schema version for forward compatibility.
schema_version: 1

# Type definitions with capabilities.
# Capabilities: plannable, actionable, implementable
# - plannable: gets architect/decomposition
# - actionable: coordination/grouping of steps
# - implementable: leaf-level, directly coded
types:
  Epic:
    capabilities: [plannable]
    filing_eligible: false           # Epics don't receive closeout observations
    max_nesting_depth: 1
    decomposition_guidance: |
      Always decompose into Scenarios. Epics are never implemented directly.
      Each Scenario should represent a user-facing capability or workflow.

  Scenario:
    capabilities: [plannable, implementable]
    filing_eligible: true
    self_referential: true           # Scenarios can nest (Scenario → Scenario)
    max_nesting_depth: 2             # Up to 2 levels of nested Scenarios
    decomposition_guidance: |
      Decompose into Deliverables or Tasks when scope exceeds a single PG.
      Implement directly when the change is focused and fits one PG.
      Nested Scenarios are allowed for complex multi-step workflows.

  Deliverable:
    capabilities: [actionable]       # Coordination only — groups Tasks
    filing_eligible: false

  Task:
    capabilities: [implementable]
    filing_eligible: true            # Tasks receive closeout observations

# State transitions — maps workflow events to ADO state names.
# These are CMMI-specific (Active/Closed instead of Doing/Done).
transitions:
  Epic:
    begin_planning: Active
    all_children_complete: Closed
    scope_removed: Removed
  Scenario:
    begin_planning: Active
    begin_implementation: Active
    implementation_complete: Closed
    scope_removed: Removed
  Deliverable:
    begin_planning: Active
    all_children_complete: Closed
    scope_removed: Removed
  Task:
    begin_implementation: Active
    implementation_complete: Closed
    scope_removed: Removed

# Review policies — who reviews PRs at each workflow phase.
review_policies:
  planning:
    plan_pr:
      agent_review: true
      human_review: true             # Humans review architectural plans
      auto_merge: false
  implementation:
    pg_pr:
      agent_review: true
      human_review: false            # PG PRs auto-merge after agent review
      auto_merge: true
    feature_pr:
      agent_review: true
      human_review: true             # Feature PRs require human review
      auto_merge: false
  remediation:
    pg_pr:
      agent_review: true
      human_review: false
      auto_merge: true

# Branch naming patterns.
# Placeholders: {root_id}, {slug}, {n}
branch_strategy:
  feature_branch: "feature/{root_id}-{slug}"
  planning_branch: "planning/{root_id}"
  pg_branch: "pg-{n}/{root_id}-{slug}"
  target: main

# Platform for PR operations.
# github = GitHub PRs, ado = Azure DevOps PRs
platform: github
```

### profile.yaml (Annotated)

```yaml
project:
  name: Cloudvault
  description: >
    Cloud-native secrets management platform with zero-trust key rotation,
    multi-tenant vault isolation, and Azure Key Vault integration.
  repository: contoso/cloudvault

tech_stack:
  language: C# 12
  framework: .NET 8 LTS (ASP.NET Core)
  serialization: System.Text.Json
  testing: xUnit + FluentAssertions + Moq

# Build commands — agents use these to build and test your code.
build:
  restore: dotnet restore
  build: dotnet build --no-restore
  test: dotnet test --no-restore
  publish: dotnet publish src/Cloudvault.Api -c Release -o artifacts

# Project conventions — injected into agent context.
conventions:
  - Layered architecture (API → Service → Repository → Data)
  - Options pattern for all configuration
  - Result pattern for error handling (no exceptions for flow control)
  - All API endpoints versioned under /api/v2/
  - Integration tests use TestContainers for database dependencies
  - Secrets never stored in appsettings.json — use Azure Key Vault

estimation:
  unit: hours
  confidence_default: medium
```

### Key Differences from Basic (twig) Config

| Aspect | Basic (twig) | CMMI (cloudvault) |
|--------|-------------|-------------------|
| Process template | `Basic` | `CMMI` |
| Mid-level type | Issue | Scenario |
| Active state | `Doing` | `Active` |
| Done state | `Done` | `Closed` |
| Extra types | — | Deliverable (actionable) |
| Self-referential | No | Scenario → Scenario (depth 2) |
| Custom guidance | Minimal | Full architect/coder/reviewer |

---

## Quick Reference Checklist

Use this checklist when onboarding a new repo:

- [ ] Identify the ADO process template (Basic/Agile/Scrum/CMMI)
- [ ] Run `bootstrap-conductor.ps1 -ProcessTemplate <template>`
- [ ] Customize `process-config.yaml` — types, capabilities, transitions
- [ ] Write type definitions in `work-item-types/*.md`
- [ ] Create description templates in `work-item-types/templates/*.md`
- [ ] Add agent guidance in `agent-guidance/` (architect, coder, reviewer)
- [ ] Fill in `profile.yaml` — project info, tech stack, build commands
- [ ] Run `polyphony validate-config --config .conductor --output human`
- [ ] Fix any errors, review warnings
- [ ] Run `twig-sdlc-v2-full@twig --work-item <id>` on a test work item
- [ ] Verify routing, agent behavior, and PR lifecycle work correctly

---

## E2E Validation Results — Cloudvault (May 2026)

A full end-to-end validation of the type-agnostic SDLC pipeline was run against
**cloudvault** (CMMI process template) using work items from the `dangreen-msft/Twig`
ADO project. All validations passed.

### Polyphony Routing Validation (AB#2821)

| Command | Input Type | Expected Capability | Result |
|---------|-----------|---------------------|--------|
| `polyphony route` | Scenario | plannable, implementable | ✅ Pass |
| `polyphony route` | Deliverable | actionable | ✅ Pass |
| `polyphony route` | Task | implementable | ✅ Pass |
| `polyphony validate --event begin_planning` | Scenario (Proposed) | transition allowed | ✅ Pass |
| `polyphony validate --event begin_planning` | Task (To Do → Closed) | transition rejected | ✅ Pass |
| `polyphony hierarchy --depth 6` | Scenario (composite) | recursive Scenario→Scenario discovery | ✅ Pass |
| `polyphony hierarchy --depth 6` | Scenario→Deliverable→Task | leaf chain enumeration | ✅ Pass |

Key findings:
- `self_referential: true` on Scenario correctly enables recursive Scenario→Scenario traversal.
- `max_nesting_depth: 2` is honored — hierarchy walker stops at depth boundary.
- TaskGroup under Scenario and TaskGroup under Deliverable both route to `actionable` correctly.
- No Polyphony routing engine code changes were required for cloudvault compatibility.

### End-to-End SDLC Workflow Run (AB#2822)

A full `twig-sdlc-v2-full@twig` run was executed against a real cloudvault Scenario work item
with the following phases completing successfully:

| Phase | Workflow | Result |
|-------|----------|--------|
| Planning | `plan-level.yaml` — recursive Scenario→Scenario→Deliverable decomposition | ✅ Pass |
| Seeding | Work tree seeder created cloudvault ADO items from plan | ✅ Pass |
| Implementation | `implement-pg.yaml` — domain-aware coder with cloudvault agent guidance | ✅ Pass |
| Close-out | `close-out.yaml` — items closed, filing observations created | ✅ Pass |

Key findings:
- `load-agent-guidance.ps1` correctly loads cloudvault architect/coder guidance and injects
  it into agent prompts.
- `load-type-context.ps1` loads `task-group.md` (space→hyphen slug normalization from
  PG-4 fix is working correctly).
- Recursive planning handled variable-depth Scenario decomposition without workflow code
  changes.
- PG implementation handled both Deliverable→Task and TaskGroup→Task patterns.
- All cloudvault work items closed out correctly via the generic close-out workflow.
- No changes to the Polyphony routing engine or generic v2 scripts were required (only
  the `load-type-context.ps1` fix from PG-4 was needed).
