# Onboarding Guide — v2 SDLC Workflow Configuration

A step-by-step guide for onboarding a new repository to the `twig-sdlc-v2-full@twig`
conductor workflow. This guide uses **kyber** (a fictitious post-quantum
cryptography library on a custom `KyberAgile` ADO process template) as a worked
example throughout. After following this guide, you will have a working
`.conductor/` configuration for any ADO process template — standard or custom.

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
10. [Kyber Worked Example](#10-kyber-worked-example)

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
- **Type capabilities configured** — Every type in `.conductor/process-config.yaml` must declare a `capabilities` field with at least one of `plannable` or `implementable` (case-insensitive). Only these two values are valid.
- **ADO workspace configured** — Your repo has a `.twig/config` file pointing to the
  correct ADO organization and project. If not, run `twig init`.
- **Git repository** — Your repo uses git with a `main` branch as the default target.

Verify your setup:

```bash
twig --version        # Confirm twig is installed
polyphony --version   # Confirm polyphony is installed
polyphony health      # Run environment and config diagnostics
```

### polyphony health

After installing Polyphony, run `polyphony health` to verify your environment and configuration. This command checks:

- That `.conductor/process-config.yaml` exists and is valid
- That required tools (`twig`, `git`) are available on your PATH
- OS, architecture, .NET, and Polyphony version

**Example output:**

```json
{
  "checks": [
    { "name": "process-config", "success": true, "message": "Loaded successfully" },
    { "name": "twig", "success": true, "message": "Found on PATH: /usr/local/bin/twig" },
    { "name": "git", "success": true, "message": "Found on PATH: /usr/bin/git" }
  ],
  "os": "Windows_NT",
  "architecture": "x64",
  "dotnetVersion": "10.0.0",
  "polyphonyVersion": "1.2.3"
}
```

#### Interpreting Results

- If any check fails, the `message` field will explain what needs to be fixed (e.g., missing config, tool not found, invalid YAML).
- All output fields are always present (never null).
- Exit code 0 means all checks passed; exit code 4 means one or more critical health checks failed.
- Use this command after setup or when troubleshooting environment issues.
- For failed checks, follow the remediation steps in the `message` field. Common actions:
  - Reinstall missing tools (`twig`, `git`)
  - Fix or restore `.conductor/process-config.yaml`
  - Ensure your PATH includes required binaries
  - Re-run `polyphony health` after making changes
- If you are unable to resolve an issue, copy the full output and seek help in the project support channel.

---

## 2. Process Template Selection

The v2 workflow supports the four standard ADO process templates plus any
**custom** template derived from one of them. You need to identify which one
your project uses.

### Identifying Your Process Template

Check your ADO project settings:

1. Go to **Project Settings → Boards → Process** in Azure DevOps.
2. Note the process name (Basic, Agile, Scrum, CMMI, or a custom name).

Alternatively, if your repo already has `.twig/config`, the bootstrap script can
auto-detect it from the `process_template` field.

### Supported Templates and Their Type Hierarchies

| Template | Top-level (plannable) | Mid-level (plannable + implementable) | Leaf (implementable) |
|----------|----------------------|--------------------------------------|---------------------|
| **Basic** | Epic | Issue | Task |
| **Agile** | Epic | User Story | Task |
| **Scrum** | Epic | Product Backlog Item | Task |
| **CMMI** | Epic | Requirement | Task |

> **Kyber example:** Kyber uses a **custom** process template called
> `KyberAgile`, derived from the standard Agile template. Its hierarchy is
> Epic → **Capability** → Task (plus Bug → Task), where `Capability` is a
> custom mid-level type that replaces `User Story` for a crypto-primitives
> domain. We'll cover how to declare custom types in
> [Section 4](#4-type-definitions).

### State Mappings Per Template

Each template uses different state names for the same workflow transitions
(state sets per template are at
`twig2/tests/Twig.TestKit/ProcessConfigBuilder.cs:48-96`):

| Template | Active State | Done State | Notes |
|----------|-------------|------------|-------|
| **Basic** | Doing | Done | No `Removed` state |
| **Agile** | Active | Closed | User Story also has `Resolved` |
| **Scrum** | In Progress | Done | Mid-level uses `Committed` |
| **CMMI** | Active | Closed | Mid-level uses `Resolved` for `implementation_complete` |

### Custom process templates

A custom process template is one your ADO project administrator has created by
inheriting from a standard template (Basic, Agile, Scrum, or CMMI) and then
renaming types, renaming states, or adding new types.

Polyphony does **not** maintain a whitelist of valid `process_template` names.
Validation rule V-1 (`src/Polyphony/Configuration/ConfigValidator.cs:27`) only
checks that the field is non-empty. Any string is accepted; nothing checks it
against ADO's actual catalog. So `process_template: KyberAgile`, or
`process_template: ContosoCMMI-v3`, are all valid as far as Polyphony is
concerned.

What *does* matter is that the **state names** you use on the right-hand side
of `transitions:` actually exist for each type in your process template, and that every type has a valid `capabilities` field. The `capabilities` field is required for every type and must contain at least one of `plannable` or `implementable` (case-insensitive). Any other value will fail validation (see V-3, V-4).
Polyphony will not catch a mismatch — `polyphony validate-config` does not
cross-reference state names against the template's state set. The failure
surfaces later, when twig tries to write the state and `StateResolver.ResolveByName`
rejects it with `Unknown state '<X>'. Valid states: ...`.

> **Kyber example:** `KyberAgile` is derived from Agile and uses the standard
> Agile state names (`New`, `Active`, `Resolved`, `Closed`, `Removed`) per
> `twig2/tests/Twig.TestKit/ProcessConfigBuilder.cs:48-96`. The only
> customisations are the addition of the `Capability` type and the
> `Capability` → `Task` decomposition rule. No state names are renamed.

---

## 3. Bootstrap

The `bootstrap-conductor.ps1` script generates a complete set of stub `.conductor/`
files, giving you a starting point to customize.

### Running the Bootstrap

From your repository root:

```powershell
# Auto-detect from .twig/config (if process_template is set)
./scripts/bootstrap-conductor.ps1

# Or specify explicitly — works for standard templates...
./scripts/bootstrap-conductor.ps1 -ProcessTemplate Agile

# ...or for a custom template (uses the closest standard template as the stub source)
./scripts/bootstrap-conductor.ps1 -ProcessTemplate Agile -CustomName KyberAgile

# To overwrite existing files
./scripts/bootstrap-conductor.ps1 -ProcessTemplate Agile -Force

# To generate in a different directory
./scripts/bootstrap-conductor.ps1 -ProcessTemplate Agile -OutputPath ./my-repo
```

> **Kyber example:**
> ```powershell
> cd ~/projects/kyber
> ~/projects/polyphony/scripts/bootstrap-conductor.ps1 `
>     -ProcessTemplate Agile -CustomName KyberAgile
> ```
> The bootstrap stamps `process_template: KyberAgile` into
> `process-config.yaml` and uses the Agile state set for the generated
> transitions. You then edit the file to add the custom `Capability` type and
> drop `User Story`.

### What Gets Generated

The bootstrap creates the following directory structure (canonical layout per
the `.conductor/` directory reference):

```
.conductor/
├── process-config.yaml                 # Type capabilities, transitions, branch strategy
├── profile.yaml                        # Project metadata, tech stack, build commands
├── agent-guidance/
│   ├── epic.md                         # Guidance for Epic type
│   ├── user-story.md                   # Guidance for User Story type
│   ├── task.md                         # Guidance for Task type
└── work-item-types/
    ├── epic.md                         # Epic type definition
    ├── user-story.md                   # Mid-level type definition (Agile stub)
    ├── task.md                         # Task type definition
    └── templates/
        ├── epic-template.md            # Epic description template
        ├── user-story-template.md      # User Story description template
        └── task-template.md            # Task description template
```

For kyber you will rename `user-story.md` → `capability.md` and
`user-story-template.md` → `capability-template.md` after bootstrap, then update
`process-config.yaml` to reference the renamed type. The bootstrap can't infer
custom type names; it always generates the standard mid-level type for the
parent template. 

> **Note:** Agent guidance files are now generated per type (e.g. `agent-guidance/capability.md`). V-11 warnings will reference the slug for each type. Ensure your agent-guidance directory matches your type names.

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

Use the type name in lowercase with spaces replaced by hyphens. This matches
`ConfigValidator.ToSlug` (`src/Polyphony/Configuration/ConfigValidator.cs:162-163`),
which is what the warnings V-9 and V-10 use to look for the file:

| Type Name | File Name |
|-----------|-----------|
| Epic | `epic.md` |
| User Story | `user-story.md` |
| Product Backlog Item | `product-backlog-item.md` |
| Requirement | `requirement.md` |
| Task | `task.md` |
| Capability | `capability.md` |
| Bug | `bug.md` |

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
- **Hierarchy Rules** — Parent/child constraints (e.g., "Epics contain only Capabilities").
- **Language Guidelines** — Tone and detail expectations per description section.
- **Relationship to Plan Documents** — Whether this type gets a plan doc.

### Kyber Example: Capability Type Definition

Kyber uses a custom `Capability` type that acts as a mid-level dual-capability
container — analogous to `User Story` in Agile or `Requirement` in CMMI, but
named for the unit of work that's natural in a crypto-primitives codebase.
A Capability is a **focused crypto primitive or operation**: small enough that
a single contributor can hold it in their head, large enough that it usually
decomposes into a handful of Tasks (spec read, reference-vector port,
constant-time implementation, fuzz harness).

```markdown
# Capability — Work Item Type Definition (KyberAgile Process)

## Definition

A Capability represents a single self-contained crypto primitive or operation
inside the kyber library. Capabilities are the unit at which we plan,
review, and ship cryptographic functionality. They sit between strategic
Epics (e.g. "ML-KEM-768 reference implementation") and tactical Tasks
(e.g. "wire NTT into Encaps loop").

## Purpose

A Capability answers: **"What primitive or operation are we delivering, and
how will we verify it matches the NIST PQC reference implementation?"**

## Audience

| Role | How They Use Capabilities |
|------|---------------------|
| **Project Owner** | Creates Capabilities, defines acceptance vectors and constant-time requirements. |
| **Contributor** | Implements Capabilities or their child Tasks. |
| **AI Agent** | Plans decomposition into Tasks; implements directly when scope is small (e.g. single primitive with reference vectors already in tree). |

## Naming Conventions

- Lead with the primitive name: "Encaps", "Decaps", "NTT", "CBD sampler"
- Spell out abbreviations on first use in the description
- Keep under 80 characters
- Good: "Key encapsulation (Encaps) for ML-KEM-768"
- Good: "Constant-time conditional move (cmov) helper"
- Bad: "Implement crypto" — no primitive named, not verifiable

## In Scope for a Capability

- A single primitive (Encaps, Decaps, NTT, CBD sampler, cmov, ...)
- Constant-time guarantees and the test that verifies them
- Test vectors imported from the NIST PQC reference implementation
- Performance budget (cycles per operation) when relevant

## Out of Scope for a Capability

- Multi-primitive workflows — those are Epics
- Build-system or CI changes — those are Tasks under an "Infrastructure" Epic
- API surface decisions — captured in plan documents under `docs/projects/`

## Description Template

See: `templates/capability-template.md`

## Hierarchy Rules

- Capabilities live under Epics
- Capabilities decompose into Tasks
- Capabilities are NOT self-referential — no nested Capabilities
- Bugs filed against a shipped Capability live as siblings of it under the
  same Epic, and decompose into Tasks the same way
```

This single file is the V-9 fix for the `Capability` type
(`ConfigValidator.cs:105-110`). One V-9 warning is emitted per type declared
in `process-config.yaml` that is missing its `<slug>.md`.

---

## 5. Templates

Templates live in `.conductor/work-item-types/templates/` and define the expected
structure of a work item's **Description** field. Agents use these when creating
new work items to ensure consistent formatting.

### File Naming

Follow the pattern `{type-slug}-template.md`. This matches
`ConfigValidator.cs:113-114` — the V-10 warning fires per type defined in
`process-config.yaml` whose template file is missing.

| Type | Template File |
|------|--------------|
| Epic | `epic-template.md` |
| Capability | `capability-template.md` |
| Task | `task-template.md` |
| Bug | `bug-template.md` |

### Template Structure

Templates use markdown with `<!-- TODO -->` placeholder comments and angle-bracket
instructions. Here's the general pattern:

**For plannable types** (Epic):

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
<Specific files, modules, functions to modify. Name every touchpoint.>

## How to Change
<Implementation approach. Step-by-step for non-obvious changes.>

## Acceptance Criteria
- [ ] <Task-specific criterion — binary pass/fail>
- [ ] Build passes with zero errors and warnings
- [ ] Relevant tests pass

## Context (optional)
<Dependencies, gotchas, related code paths.>
```

**For dual-capability types** (Issue, User Story, Requirement, Capability):

```markdown
## Summary
<2–3 sentences: what, why, expected outcome>

## Problem / Motivation
<What's broken, slow, missing, or poorly structured?>

## Proposed Approach
<High-level technical approach. Reference file paths, module names.>

## Acceptance Criteria
- [ ] <Criterion 1 — measurable, binary pass/fail>
- [ ] Build passes with zero errors and warnings
- [ ] All existing tests pass; new tests cover changed behavior

## Child Tasks (if decomposed)
<Populated during decomposition — omit for directly-implemented items>

## Context (optional)
<Plan document link, architecture references, related code paths>
```

### Kyber Example: Capability Template

`Capability` is a dual-capability type (`plannable, implementable`), so its
template follows the dual-capability pattern but with crypto-specific
acceptance criteria pre-baked. From `templates/capability-template.md`:

```markdown
## Summary
<2–3 sentences: which primitive, what shape, expected outcome.>

## Problem / Motivation
<Why we need this primitive in kyber. Reference the parent ML-KEM Epic and
the upstream NIST PQC spec section.>

## Proposed Approach
<Module path under `kyber-core/src/`. Trait that the new type implements
(usually `KyberCore`). Whether reference vectors exist in
`kyber-test-vectors/` already or need to be imported.>

## Acceptance Criteria
- [ ] Implementation matches NIST PQC reference vectors for ML-KEM-{512,768,1024}
- [ ] Constant-time test (`dudect` harness) passes with p > 0.05
- [ ] No allocations in the hot path (verified by `cargo bench --no-default-features`)
- [ ] All `unsafe` confined to `kyber-asm/` (if any used)
- [ ] `cargo build` and `cargo test` pass with zero warnings
- [ ] New public API documented with `///` doc comments and one example

## Child Tasks (if decomposed)
<Populated during decomposition — omit for directly-implemented Capabilities>

## Context (optional)
**Plan:** `docs/projects/<slug>.plan.md`
**NIST reference:** <link to relevant section of FIPS 203>
```

The acceptance criteria block is the part most worth customising per
codebase — it's where domain invariants get enforced by default.

---

## 6. Agent Guidance

Agent guidance files live in `.conductor/agent-guidance/` and provide project-specific
instructions for each AI agent role. These are injected into agent prompts via Jinja2
templating during workflow execution.

### Available Roles

The bootstrap generates guidance files for three roles. Polyphony only checks
existence (`ConfigValidator.cs:122-141`); content is free-form Markdown:

| Role | File | V-rule | Purpose |
|------|------|--------|---------|
| **Architect** | `architect.md` | V-11 | Planning, decomposition, estimation |
| **Coder** | `coder.md` | V-12 | Implementation, testing, code style |
| **Reviewer** | `reviewer.md` | V-13 | Code review, quality standards |

### What to Include

Guidance files should capture project-specific knowledge that agents need but
can't infer from the code alone:

- **Architecture patterns** — "We use the `KyberCore` trait for all primitives",
  "All randomness goes through `getrandom` with the OS RNG"
- **Conventions** — "Constant-time only", "No allocations in hot paths"
- **Constraints** — "No `unsafe` outside `kyber-asm/`", "Stable Rust toolchain only"
- **Testing expectations** — "Reference vectors from NIST PQC", "dudect for
  constant-time verification"
- **Build/deploy notes** — "`cargo build --release` for benchmark builds"

### Graceful Degradation

Agent guidance is **optional**. If a guidance file is missing, the workflow
continues without it — the agent runs with its default behavior. Polyphony's
V-11..V-13 are warnings, not errors (`ConfigValidator.cs:122-141`). This means:

- You can start with empty guidance and add detail over time.
- A missing file is not an error (but `polyphony validate-config` will emit a
  warning).
- Add guidance when you notice agents making mistakes that better instructions
  would prevent.

### Kyber Example: Architect Guidance

```markdown
# Architect Guidance — kyber

## Responsibilities

- Decompose Capabilities into Tasks
- Estimate effort using a Task = ½ – 1 day model (kyber Tasks are small)
- Ensure each Task is self-contained and verifiable against a reference vector
  or a constant-time test

## Conventions

- All primitives implement the `KyberCore` trait in `kyber-core/src/lib.rs`.
  Never expose a primitive without going through this abstraction.
- Constant-time operations only. Branching on secret data is a bug, not a
  style preference.
- No allocations in hot paths. Hot paths are: Encaps, Decaps, NTT, sampler
  inner loops. Allocate once at setup, reuse buffers.
- Test vectors live in `kyber-test-vectors/`. They are imported verbatim from
  the NIST PQC reference implementation; do not hand-edit.
- `unsafe` is permitted only inside the `kyber-asm/` crate, which wraps
  hand-written assembly. Anywhere else, `#![forbid(unsafe_code)]`.

## Constraints

- Stable Rust toolchain (currently 1.79). No nightly features.
- `no_std` compatible — kyber must build for embedded targets. Keep `std`
  imports gated behind the `std` feature flag.
- MSRV is documented in `Cargo.toml` and bumped only with team agreement.

## Estimation

- Tasks estimated > 1 day should be split — kyber prefers many small Tasks
  with reference-vector checkpoints.
- Include `dudect` constant-time test runtime in estimates (~5 minutes per
  primitive on CI hardware).
- Capabilities touching `kyber-asm/` need extra review time — assembly
  changes are security-sensitive.
```

The same shape works for `coder.md` and `reviewer.md`, with role-specific
focus (coder: code style + test patterns; reviewer: blocker vs. nit
calibration + areas where strictness matters most).

---

## 7. Profile

The `profile.yaml` file describes your project's metadata, tech stack, build
commands, and estimation settings. Polyphony checks only its existence
(V-14, `ConfigValidator.cs:144-148`); the file is consumed by agents inside
the v2 SDLC workflow suite, not by the Polyphony CLI itself.

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

### Kyber Example: profile.yaml

```yaml
project:
  name: kyber
  description: >
    Pure-Rust implementation of ML-KEM (Kyber), the NIST PQC
    standardised key-encapsulation mechanism. Constant-time, no_std-friendly,
    benchmark-quality.
  repository: kyber-crypto/kyber

tech_stack:
  language: Rust 1.79 (stable)
  framework: no_std core + optional std feature
  testing: cargo test + dudect (constant-time) + criterion (benchmarks)

build:
  restore: cargo fetch
  build: cargo build --workspace --all-features
  test: cargo test --workspace --all-features
  publish: cargo publish -p kyber-core

conventions:
  - All primitives implement the KyberCore trait
  - Constant-time operations only — branch-on-secret is a bug
  - No allocations in hot paths (Encaps, Decaps, NTT, samplers)
  - Reference vectors imported verbatim from NIST PQC
  - unsafe permitted only inside the kyber-asm/ crate

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

The full V-rule table is enforced in
`src/Polyphony/Configuration/ConfigValidator.cs`:

| Rule ID | Source line | Meaning | Fix |
|---------|-------------|---------|-----|
| V-1 | `ConfigValidator.cs:27` | Missing `process_template` | Add `process_template: <name>` to process-config.yaml |
| V-2 | `ConfigValidator.cs:33` | No types defined | Add at least one entry under `types:` |
| V-3 | `ConfigValidator.cs:56` | Type has no capabilities | Add `capabilities: [plannable]` or `[implementable]` (or both) — this field is required for every type. |
| V-4 | `ConfigValidator.cs:60-67` | Type has invalid capability value | **Use only `plannable` or `implementable`** — these are the only two values in the whitelist. Any other value will fail validation. |
| V-5 | `ConfigValidator.cs:71-74` | Type has no transitions | Add transition mappings under `transitions:` for the type |
| V-6 | `ConfigValidator.cs:88-95` | Transition references undefined type | Ensure all keys in `transitions:` exist in `types:` |
| V-7 | `ConfigValidator.cs:42-49` | Duplicate type name (case-insensitive) | Type names must be unique regardless of case |
| V-8 | `ConfigValidator.cs:77-84` | `allowed_child_types` references undefined type | Ensure all `allowed_child_types` values exist in `types:` |

> **Capability whitelist callout (V-4):** The valid capability values are
> *exactly* `plannable` and `implementable`
> (`ConfigValidator.cs:60-67`, where the `ValidCapabilities` HashSet is
> `{ "plannable", "implementable" }`). Anything else — `actionable`,
> `reviewable`, `coordinatable`, etc. — fails V-4 with `Type '<name>' has
> invalid capability '<x>'. Valid values: plannable, implementable.` If you
> need a parent that only groups children without being implemented directly,
> use `capabilities: [plannable]` and rely on the parent type being
> inherently a grouping construct.

### Common Validation Warnings

| Rule ID | Source line | Meaning | Recommendation |
|---------|-------------|---------|---------------|
| V-9 | `ConfigValidator.cs:105-110` | Type definition file missing | Create `.conductor/work-item-types/{slug}.md` per type |
| V-10 | `ConfigValidator.cs:113-119` | Template file missing | Create `.conductor/work-item-types/templates/{slug}-template.md` per type |
| V-11 | `ConfigValidator.cs:122-127` | Architect guidance file missing | Create `.conductor/agent-guidance/architect.md` |
| V-12 | `ConfigValidator.cs:130-134` | Coder guidance file missing | Create `.conductor/agent-guidance/coder.md` |
| V-13 | `ConfigValidator.cs:137-141` | Reviewer guidance file missing | Create `.conductor/agent-guidance/reviewer.md` |
| V-14 | `ConfigValidator.cs:144-148` | Profile file missing | Create `.conductor/profile.yaml` |

The slug used by V-9 and V-10 is `ConfigValidator.ToSlug` — lowercase plus
spaces-to-hyphens (`ConfigValidator.cs:162-163`).

---

## 9. First Run

Once validation passes, you're ready to run the v2 SDLC workflow.

> **Caveat:** This guide describes the *intended* flow. End-to-end validation
> of the polyphony + twig + kyber path is in progress and has not yet been
> completed. Treat the steps below as the configured contract; expect to file
> small fixes as the first run surfaces them.

### Invoking the Workflow

The workflow is invoked through `conductor run` with a work item ID. The root
workflow takes three named inputs (`work_item_id`, `intent`, optional
`user_plan_path`) — see `.github/skills/polyphony-sdlc/SKILL.md:330-355` for
the full input contract:

```powershell
# Resume an existing work item (default intent)
conductor run twig-sdlc-v2-full@twig `
  --input work_item_id=<ID> `
  --web

# Launch with a user-authored plan (intent=new)
conductor run twig-sdlc-v2-full@twig `
  --input work_item_id=<ID> `
  --input intent=new `
  --input user_plan_path=path/to/plan.md `
  --web

# Wipe existing children/branches and start over (intent=redo)
conductor run twig-sdlc-v2-full@twig `
  --input work_item_id=<ID> `
  --input intent=redo `
  --web
```

> **Always launch detached** — wrap in `Start-Process -WindowStyle Hidden` so
> conductor survives if the parent session drops. Always use `--web`, not
> `--web-bg`.

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
| `Unknown state '<X>'` from twig | State name in `transitions:` doesn't exist in the template's state set for that type | Cross-check state names against `twig2/tests/Twig.TestKit/ProcessConfigBuilder.cs:48-96` |

---

## 10. Kyber Worked Example

This section shows a complete, annotated `.conductor/` configuration for kyber,
a `KyberAgile` (custom) process repository on GitHub. Use this as a reference
when building your own config.

### Directory Structure

```
kyber/
└── .conductor/
    ├── process-config.yaml
    ├── profile.yaml
    ├── agent-guidance/
    │   ├── architect.md
    │   ├── coder.md
    │   └── reviewer.md
    └── work-item-types/
        ├── epic.md
        ├── capability.md
        ├── bug.md
        ├── task.md
        └── templates/
            ├── epic-template.md
            ├── capability-template.md
            ├── bug-template.md
            └── task-template.md
```

### process-config.yaml (Annotated)

```yaml
# The ADO process template this repo uses.
# For standard templates: Basic | Agile | Scrum | CMMI.
# For custom templates: any string. Polyphony does not validate the value
# against an ADO catalog (V-1 only checks non-empty —
# src/Polyphony/Configuration/ConfigValidator.cs:27).
process_template: KyberAgile

# Schema version for forward compatibility.
schema_version: 1

# Type definitions with capabilities.
# Capabilities — the ONLY two valid values:
# - plannable: gets architect/decomposition
# - implementable: leaf-level, directly coded
# (Source of truth: ConfigValidator.cs:60-67 — the ValidCapabilities HashSet.)
types:
  Epic:
    capabilities: [plannable]
    filing_eligible: false           # Epics don't receive closeout observations
    max_nesting_depth: 1
    allowed_child_types: [Capability, Bug]
    decomposition_guidance: |
      Always decompose into Capabilities (or Bugs, when fixing a regression in
      a shipped Capability). Epics are never implemented directly.
      Each Capability should represent a single crypto primitive or operation.

  Capability:
    capabilities: [plannable, implementable]
    filing_eligible: true
    self_referential: false          # No nested Capabilities
    max_nesting_depth: 1
    allowed_child_types: [Task]
    decomposition_guidance: |
      Decompose into Tasks for any Capability touching `kyber-asm/`,
      requiring a new dudect harness, or estimated > 1 day.
      Implement directly when the change is a single primitive with reference
      vectors already present in `kyber-test-vectors/`.

  Bug:
    capabilities: [plannable, implementable]
    filing_eligible: true
    allowed_child_types: [Task]

  Task:
    capabilities: [implementable]
    filing_eligible: true            # Tasks receive closeout observations

# State transitions — maps workflow events to ADO state names.
# These use the standard Agile state set
# (twig2/tests/Twig.TestKit/ProcessConfigBuilder.cs:48-96), since KyberAgile
# is derived from Agile and does not rename any states. Note that User Story
# in standard Agile uses `Resolved` for implementation_complete; we mirror
# that for Capability since the meanings are equivalent.
transitions:
  Epic:
    begin_planning: Active
    all_children_complete: Closed
    scope_removed: Removed
  Capability:
    begin_planning: Active
    begin_implementation: Active
    implementation_complete: Resolved
    scope_removed: Removed
  Bug:
    begin_implementation: Active
    implementation_complete: Resolved
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
      human_review: true             # Feature PRs require human review (crypto)
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
  name: kyber
  description: >
    Pure-Rust implementation of ML-KEM (Kyber), the NIST PQC standardised
    key-encapsulation mechanism. Constant-time, no_std-friendly,
    benchmark-quality.
  repository: kyber-crypto/kyber

tech_stack:
  language: Rust 1.79 (stable)
  framework: no_std core + optional std feature
  testing: cargo test + dudect (constant-time) + criterion (benchmarks)

# Build commands — agents use these to build and test your code.
build:
  restore: cargo fetch
  build: cargo build --workspace --all-features
  test: cargo test --workspace --all-features
  publish: cargo publish -p kyber-core

# Project conventions — injected into agent context.
conventions:
  - All primitives implement the KyberCore trait
  - Constant-time operations only — branch-on-secret is a bug
  - No allocations in hot paths (Encaps, Decaps, NTT, samplers)
  - Reference vectors imported verbatim from NIST PQC
  - unsafe permitted only inside the kyber-asm/ crate
  - no_std compatible; std imports gated behind the `std` feature flag

estimation:
  unit: hours
  confidence_default: medium
```

### Key Differences from Basic (twig) Config

| Aspect | Basic (twig) | KyberAgile (kyber) |
|--------|-------------|---------------------|
| Process template | `Basic` (standard) | `KyberAgile` (custom, derived from Agile) |
| Mid-level type | Issue | **Capability** (custom name; same `[plannable, implementable]` capability set) |
| Active state | `Doing` | `Active` |
| Done state | `Done` | `Closed` (Tasks) / `Resolved` (Capability, Bug) |
| `Removed` state available? | No | Yes (Agile state set) |
| Self-referential mid-level | No | No (Capability does not nest) |
| Bug type | — | Yes; sibling of Capability under Epic |
| Platform | github | github |
| Custom guidance | Minimal | Full architect/coder/reviewer (crypto-specific) |

---

## Quick Reference Checklist

Use this checklist when onboarding a new repo:

- [ ] Identify the ADO process template (Basic/Agile/Scrum/CMMI, or a custom name)
- [ ] If custom: identify which standard template it derives from (this is the
      bootstrap stub source)
- [ ] Run `bootstrap-conductor.ps1 -ProcessTemplate <template>` (add
      `-CustomName <name>` for a custom template)
- [ ] Customize `process-config.yaml` — types, capabilities (only `plannable`
      and `implementable` are valid per V-4), transitions, branch strategy
- [ ] Cross-check every state name in `transitions:` against the template's
      state set per `ProcessConfigBuilder.cs:48-96` (Polyphony will not catch
      mismatches)
- [ ] Write type definitions in `work-item-types/*.md` (one per type — V-9)
- [ ] Create description templates in `work-item-types/templates/*.md` (one per
      type — V-10)
- [ ] Add agent guidance in `agent-guidance/` (architect, coder, reviewer —
      V-11..V-13)
- [ ] Fill in `profile.yaml` — project info, tech stack, build commands (V-14)
- [ ] Run `polyphony validate-config --config .conductor --output human`
- [ ] Fix any errors, review warnings
- [ ] Run `conductor run twig-sdlc-v2-full@twig --input work_item_id=<id> --web` on a test work item
- [ ] Verify routing, agent behavior, and PR lifecycle work correctly

