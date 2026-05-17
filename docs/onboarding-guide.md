# Onboarding Guide — Polyphony SDLC Configuration

A step-by-step guide for onboarding a new repository to the
`apex-driver@polyphony` conductor workflow (and the wider polyphony
sub-workflow library). This guide uses **kyber** (a fictitious post-quantum
cryptography library on a custom `KyberAgile` ADO process template) as a worked
example throughout. After following this guide, you will have a working
`.polyphony-config/` configuration for any ADO process template — standard or custom.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Repository Layout (Bare Repo + Per-Run Worktrees)](#2-repository-layout-bare-repo--per-run-worktrees)
3. [Process Template Selection](#3-process-template-selection)
4. [Bootstrap](#4-bootstrap)
5. [Type Definitions](#5-type-definitions)
6. [Templates](#6-templates)
7. [Agent Guidance](#7-agent-guidance)
8. [Profile](#8-profile)
9. [Config Validation](#9-config-validation)
10. [First Run](#10-first-run)
11. [Kyber Worked Example](#11-kyber-worked-example)

---

## 1. Prerequisites

Before starting, ensure the following are in place:

- **Phases 1–4 complete** — The Polyphony core engine, generic workflow scripts,
  workflow YAML refactoring, and validation/testing phases must all be merged.
- **twig CLI installed** — The AOT-compiled `twig` binary is available on your PATH
  (typically at `~/.polyphony/bin/twig`).
- **Polyphony CLI installed** — The AOT-compiled `polyphony` binary is available on
  your PATH (typically at `~/.polyphony/bin/polyphony`). Build it with `publish-local.ps1`
  from the polyphony repo if not already installed.
- **Type facets configured** — Every type in `.polyphony-config/process-config.yaml` must declare a `facets` field with at least one of `plannable` or `implementable` (case-insensitive). Only these two values are valid.
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

- That `.polyphony-config/process-config.yaml` exists and is valid
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
  - Fix or restore `.polyphony-config/process-config.yaml`
  - Ensure your PATH includes required binaries
  - Re-run `polyphony health` after making changes
- If you are unable to resolve an issue, copy the full output and seek help in the project support channel.

---

## 2. Repository Layout (Bare Repo + Per-Run Worktrees)

> **Status:** the bare-repo + per-run-worktree layout is required by the SDLC orchestrator (epic AB#3085). The legacy "single non-bare clone" layout is unsupported as of the launcher rework — see [`docs/per-run-worktree-layout.md`](per-run-worktree-layout.md) for the full rationale.

### Why this layout

The SDLC orchestrator dispatches agents into worktrees. If the only worktree available is the operator's main clone, two production bugs become structural:

1. **Launcher hijack** — the operator's main worktree gets yanked off `main` onto a feature branch mid-conversation.
2. **`worktree_dirty` cross-contamination** — sibling apex runs (or ad-hoc operator state) race each other on the shared HEAD.

The bare-repo layout eliminates both classes by giving every apex run its own worktree subtree, sibling to the operator's main worktree.

### Target on-disk layout

```
~/projects/<repo>.git/                bare repo (objects + refs only)
~/projects/<repo>/                    operator's main worktree, ALWAYS on `main`
~/projects/<repo>-runs/
  apex-{N}/                           container directory, NOT a worktree
    feature-{N}/                        worktree on feature/{N}
    plan-{N}/                           worktree on plan/{N}
    plan-{N}-{item}/                    worktree on plan/{N}-{item}
    impl-{N}-{item}/                    worktree on impl/{N}-{item}
    mg-{N}_pg-{group}/                  worktree on mg/{N}_pg-{group}
    evidence-{N}-{item}/                worktree on evidence/{N}-{item}
```

For polyphony itself, `<repo>` is `polyphony`. For your repo, substitute the repo basename.

Properties:

- All worktrees share the bare's `objects` and `refs` directories — the on-disk cost of an extra worktree is just the working tree itself.
- Branch invariants from the **polyphony-branch-model** skill are unchanged. The model changes *where* worktrees live, not how branches relate.
- The SDLC launcher (`scripts/Invoke-PolyphonySdlc.ps1`) refuses to dispatch into the operator's main worktree; the bare-repo guard plus per-apex-run path derivation makes the hijack bug structurally impossible.

### One-time migration

If you already have a non-bare clone at `~/projects/<repo>/`, migrate with the supplied script (works for any repo, not just polyphony):

```powershell
# Step 1 — clone bare alongside the existing checkout (no destructive changes):
./scripts/Migrate-ToBareRepo.ps1 -Commit

# The script prints next-step instructions. Move your existing clone aside:
Rename-Item ~/projects/<repo> ~/projects/<repo>.legacy

# Step 2 — populate the new main worktree from the bare clone:
./scripts/Migrate-ToBareRepo.ps1 -Phase2 -LegacyPath ~/projects/<repo>.legacy

# Verify:
git -C ~/projects/<repo> status   # → clean, on main
```

The script is idempotent, dry-run by default, and never touches `~/projects/<repo>/` until you've explicitly moved it aside in step 1's printed instructions. See [`docs/per-run-worktree-layout.md`](per-run-worktree-layout.md) for the manual procedure if you need to migrate without the script.

### Fresh clone (no existing local checkout)

If you don't have the repo locally yet, use `Bootstrap-BareRepo.ps1` instead — it goes straight to the canonical bare-repo + worktree layout without any migration plumbing:

```powershell
# Dry-run (default): prints the plan, creates nothing.
~/.polyphony/bin/Bootstrap-BareRepo.ps1 -RemoteUrl https://github.com/Org/<repo>.git

# Execute:
~/.polyphony/bin/Bootstrap-BareRepo.ps1 -RemoteUrl https://github.com/Org/<repo>.git -Commit

# Optional overrides:
#   -ParentDir D:\repos          (default: ~/projects)
#   -RepoName custom-name        (default: derived from URL)
#   -MainBranch develop          (default: remote's HEAD branch)
```

`Bootstrap-BareRepo.ps1` is idempotent: re-running on an existing canonical layout is a no-op. Partial-state recovery (bare present + main worktree missing) is supported as long as the bare's `origin` URL matches the `-RemoteUrl` you pass — identity mismatches refuse rather than overwrite.

On Windows the script always probes via `git --git-dir=<bare>` so it works under `git config --global safe.bareRepository explicit`.

### Verifying the layout

Two preflight probes confirm the layout is wired correctly:

```powershell
# Run from anywhere inside ~/projects/<repo> (the main worktree):
polyphony state preflight --work-item <ID>
```

Look for the `bare_repo` advisory check — `PASSED` means the common-dir resolved to `~/projects/<repo>.git/` and `git rev-parse --is-bare-repository` returned `true`.

```powershell
# Quick scan of stale per-run worktrees (default is dry-run):
polyphony worktree gc

# Scoped to a single apex:
polyphony worktree gc --apex <ID>

# Actually prune (after dry-run looks right):
polyphony worktree gc --commit
```

`worktree gc` only ever considers worktrees under `~/projects/<repo>-runs/` — your main worktree and any sibling worktrees outside the runs root are never touched.

### After merging a PR — reconcile main + prune stale branches

When a PR merges on the remote (especially an ADO squash-merge), the operator's main worktree ends up 1-ahead/N-behind `origin/main` with identical content and the just-merged local branch is left orphaned. The `Sync-BareRepo.ps1` launcher (installed to `~/.polyphony/bin/`) reconciles both:

```powershell
# Dry-run from anywhere inside the main worktree (safe default — prints the plan):
Sync-BareRepo.ps1

# Execute:
Sync-BareRepo.ps1 -Commit
```

It fast-forwards a behind-only `main`, hard-resets a squash-divergent `main` (only when the content trees are identical), and deletes local branches whose upstream is `[gone]` AND whose tip is merged into reconciled `main`. Refuses on dirty worktree, multiple worktrees on `main`, or any case where data could be lost. Pass `-NoPrune` to skip the cleanup phase.

### What this means for day-to-day work

- **Edit code in `~/projects/<repo>/`.** That's your main worktree, always on `main`. The SDLC orchestrator never dispatches into it.
- **Apex runs land in `~/projects/<repo>-runs/apex-{N}/`.** Each apex run gets its own subtree; nothing leaks across runs.
- **Agents run in per-item worktrees** (e.g. `impl-{N}-{item}/`) created on demand by `polyphony worktree create` and torn down by `polyphony worktree gc`.
- **The launcher (`Invoke-PolyphonySdlc.ps1`) auto-derives the right worktree path** from the apex id; you never need to pass `-WorktreeRoot` unless you're testing.

---

## 3. Process Template Selection

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
> Epic → **Primitive** → Task (plus Bug → Task), where `Primitive` is a
> custom mid-level type that replaces `User Story` for a crypto-primitives
> domain. We'll cover how to declare custom types in
> [Section 5](#5-type-definitions).

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
of `transitions:` actually exist for each type in your process template, and that every type has a valid `facets` field. The `facets` field is required for every type and must contain at least one of `plannable` or `implementable` (case-insensitive). Any other value will fail validation (see V-3, V-4).
Polyphony will not catch a mismatch — `polyphony validate-config` does not
cross-reference state names against the template's state set. The failure
surfaces later, when twig tries to write the state and `StateResolver.ResolveByName`
rejects it with `Unknown state '<X>'. Valid states: ...`.

> **Kyber example:** `KyberAgile` is derived from Agile and uses the standard
> Agile state names (`New`, `Active`, `Resolved`, `Closed`, `Removed`) per
> `twig2/tests/Twig.TestKit/ProcessConfigBuilder.cs:48-96`. The only
> customisations are the addition of the `Primitive` type and the
> `Primitive` → `Task` decomposition rule. No state names are renamed.

---

## 4. Bootstrap

The `bootstrap-conductor.ps1` script generates a complete set of stub `.polyphony-config/`
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
> transitions. You then edit the file to add the custom `Primitive` type and
> drop `User Story`.

### What Gets Generated

The bootstrap creates the following directory structure (canonical layout per
the `.polyphony-config/` directory reference):

```
.polyphony-config/
├── process-config.yaml                 # Type facets, transitions, branch strategy
├── profile.yaml                        # Project metadata, tech stack, build commands
├── agent-guidance/
│   ├── architect.md                    # Per-role guidance: planning & decomposition
│   ├── coder.md                        # Per-role guidance: implementation
│   └── reviewer.md                     # Per-role guidance: review
└── work-item-types/
    ├── epic.md                         # Epic type definition
    ├── user-story.md                   # Mid-level type definition (Agile stub)
    ├── task.md                         # Task type definition
    └── templates/
        ├── epic-template.md            # Epic description template
        ├── user-story-template.md      # User Story description template
        └── task-template.md            # Task description template
```

For kyber you will rename `user-story.md` → `primitive.md` and
`user-story-template.md` → `primitive-template.md` after bootstrap, then update
`process-config.yaml` to reference the renamed type. The bootstrap can't infer
custom type names; it always generates the standard mid-level type for the
parent template.

> **Note:** Agent-guidance files are generated **per role**
> (`architect.md`, `coder.md`, `reviewer.md`), not per type — V-11/12/13
> warnings reference those role files. Optional per-role-per-type
> refinements live at `agent-guidance/<role>/<typeslug>.md` (e.g.
> `agent-guidance/coder/primitive.md`); the bootstrap does not scaffold
> them. See § 7 of this guide for the per-role vs all-agents authoring
> contract.

### Review and Customize

The generated files contain `<!-- TODO -->` placeholders. The next sections walk
through customizing each file type. Don't skip customization — the stubs are
functional but generic.

---

## 5. Type Definitions

Type definitions live in `.polyphony-config/work-item-types/` as markdown files. They tell
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
| Primitive | `primitive.md` |
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
- **Hierarchy Rules** — Parent/child constraints (e.g., "Epics contain only Primitives").
- **Language Guidelines** — Tone and detail expectations per description section.
- **Relationship to Plan Documents** — Whether this type gets a plan doc.

### Kyber Example: Primitive Type Definition

Kyber uses a custom `Primitive` type that acts as a mid-level dual-facet
container — analogous to `User Story` in Agile or `Requirement` in CMMI, but
named for the unit of work that's natural in a crypto-primitives codebase.
A Primitive is a **focused crypto primitive or operation**: small enough that
a single contributor can hold it in their head, large enough that it usually
decomposes into a handful of Tasks (spec read, reference-vector port,
constant-time implementation, fuzz harness).

```markdown
# Primitive — Work Item Type Definition (KyberAgile Process)

## Definition

A Primitive represents a single self-contained crypto primitive or operation
inside the kyber library. Primitives are the unit at which we plan,
review, and ship cryptographic functionality. They sit between strategic
Epics (e.g. "ML-KEM-768 reference implementation") and tactical Tasks
(e.g. "wire NTT into Encaps loop").

## Purpose

A Primitive answers: **"What primitive or operation are we delivering, and
how will we verify it matches the NIST PQC reference implementation?"**

## Audience

| Role | How They Use Primitives |
|------|---------------------|
| **Project Owner** | Creates Primitives, defines acceptance vectors and constant-time requirements. |
| **Contributor** | Implements Primitives or their child Tasks. |
| **AI Agent** | Plans decomposition into Tasks; implements directly when scope is small (e.g. single primitive with reference vectors already in tree). |

## Naming Conventions

- Lead with the primitive name: "Encaps", "Decaps", "NTT", "CBD sampler"
- Spell out abbreviations on first use in the description
- Keep under 80 characters
- Good: "Key encapsulation (Encaps) for ML-KEM-768"
- Good: "Constant-time conditional move (cmov) helper"
- Bad: "Implement crypto" — no primitive named, not verifiable

## In Scope for a Primitive

- A single primitive (Encaps, Decaps, NTT, CBD sampler, cmov, ...)
- Constant-time guarantees and the test that verifies them
- Test vectors imported from the NIST PQC reference implementation
- Performance budget (cycles per operation) when relevant

## Out of Scope for a Primitive

- Multi-primitive workflows — those are Epics
- Build-system or CI changes — those are Tasks under an "Infrastructure" Epic
- API surface decisions — captured in plan documents under `docs/projects/`

## Description Template

See: `templates/primitive-template.md`

## Hierarchy Rules

- Primitives live under Epics
- Primitives decompose into Tasks
- Primitives are NOT self-referential — no nested Primitives
- Bugs filed against a shipped Primitive live as siblings of it under the
  same Epic, and decompose into Tasks the same way
```

This single file is the V-9 fix for the `Primitive` type
(`ConfigValidator.cs:105-110`). One V-9 warning is emitted per type declared
in `process-config.yaml` that is missing its `<slug>.md`.

---

## 6. Templates

Templates live in `.polyphony-config/work-item-types/templates/` and define the expected
structure of a work item's **Description** field. Agents use these when creating
new work items to ensure consistent formatting.

### File Naming

Follow the pattern `{type-slug}-template.md`. This matches
`ConfigValidator.cs:113-114` — the V-10 warning fires per type defined in
`process-config.yaml` whose template file is missing.

| Type | Template File |
|------|--------------|
| Epic | `epic-template.md` |
| Primitive | `primitive-template.md` |
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

**For dual-facet types** (Issue, User Story, Requirement, Primitive):

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

### Kyber Example: Primitive Template

`Primitive` is a dual-facet type (`plannable, implementable`), so its
template follows the dual-facet pattern but with crypto-specific
acceptance criteria pre-baked. From `templates/primitive-template.md`:

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
<Populated during decomposition — omit for directly-implemented Primitives>

## Context (optional)
**Plan:** `docs/projects/<slug>.plan.md`
**NIST reference:** <link to relevant section of FIPS 203>
```

The acceptance criteria block is the part most worth customising per
codebase — it's where domain invariants get enforced by default.

---

## 7. Agent Guidance

Agent guidance files live in `.polyphony-config/agent-guidance/` and provide project-specific
instructions for each AI agent role. These are injected into agent prompts via Jinja2
templating during workflow execution.

> See also: [`.polyphony-config/agent-guidance/README.md`](../.polyphony-config/agent-guidance/README.md) for the directory layout, lookup contract, and authoring rules in one place.

### Two surfaces for guidance: per-role vs all-agents

There are two distinct mechanisms for getting context into agent prompts.
They are complementary, not parallel — pick by **scope**:

| | Per-role (`.polyphony-config/agent-guidance/`) | All-agents (conductor convention) |
|---|---|---|
| **Source** | `<role>.md` in this directory | `.github/instructions/<name>.instructions.md` (with `applyTo: "**"` frontmatter), or root-level `AGENTS.md` / `CLAUDE.md` / `.github/copilot-instructions.md` |
| **Loaded by** | `polyphony plan load-agent-guidance` at workflow runtime | conductor's workspace preamble (every agent invocation) |
| **Authoring rule** | "the architect / coder / reviewer specifically needs this" | "every agent that runs anywhere in this repo needs this" |
| **Examples** | architect: "don't decompose typed-only items into children"; coder: "rebase before push, don't merge"; reviewer: "the three zero-commit-MG cases" | "use the typed `KyberTag` DU"; "spell out `KyberPrimitive` in code, not `Kp`"; "no underscore prefix on members" |
| **Filter** | role + work-item-type + optional agent-name lookup | conductor applies the `applyTo: "**"` predicate; scoped Copilot instructions (`applyTo: "src/**"`) are not loaded |

**Rule of thumb.** Start in this directory (per-role). Promote a rule to
`.github/instructions/` only when you observe the *same* rule is needed
for two or more roles, OR the rule is genuinely about the codebase itself
(style, naming, cross-cutting invariants) rather than about how a
particular role does its job.

The all-agents surface arrived in conductor via
[microsoft/conductor#169](https://github.com/microsoft/conductor/pull/169)
(May 2026), which refactored `CONVENTION_FILES` into a polymorphic
`Convention = ConventionFile | ConventionDirectory` discriminated union
and added auto-discovery for `.github/instructions/**/*.instructions.md`
with `applyTo` filtering. Files with `applyTo: "**"` are loaded into
every agent's preamble; scoped files (`applyTo: "src/**/*.ts"`) and files
without `applyTo` are skipped — that's the right default until conductor
learns to evaluate the glob against the agent's working set.

#### Authoring an all-agents instruction file

Create `.github/instructions/<name>.instructions.md` (any filename ending
in `.instructions.md`) with YAML frontmatter:

```markdown
---
applyTo: "**"
---

# Polyphony — naming and types

- Use the typed `PolyphonyTag` discriminated union for all polyphony-emitted
  ADO tags. Never construct raw `polyphony:*` strings outside the DU.
- Spell out abbreviations in C# type and member names: `MergeGroup` not
  `Mg`, `PullRequest` not `Pr`. Serialized identifiers (branch prefixes
  like `mg/`, JSON keys like `merge_group_id`) keep the short form.
- No underscore prefix on members. Use `this.field` with camelCase.
```

That's it — no polyphony-side wiring needed. Conductor picks it up
automatically the next time any workflow runs.

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

- Decompose Primitives into Tasks
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
- Primitives touching `kyber-asm/` need extra review time — assembly
  changes are security-sensitive.
```

The same shape works for `coder.md` and `reviewer.md`, with role-specific
focus (coder: code style + test patterns; reviewer: blocker vs. nit
calibration + areas where strictness matters most).

---

## 8. Profile

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

## 9. Config Validation

After creating your `.polyphony-config/` configuration, validate it with Polyphony before
running the workflow.

### Running Validation

```bash
# JSON output (default) — for programmatic consumption
polyphony validate-config --config .polyphony-config

# Human-readable output — for interactive use
polyphony validate-config --config .polyphony-config --output human
```

### Interpreting Results

**Valid configuration:**

```
Configuration is valid.
```

**Valid with warnings:**

```
Warnings (2):
  [V-9] Type definition file missing: .polyphony-config/work-item-types/task.md
  [V-11] Agent guidance file missing: .polyphony-config/agent-guidance/architect.md
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
| V-3 | `ConfigValidator.cs:56` | Type has no facets | Add `facets: [plannable]` or `[implementable]` (or both) — this field is required for every type. |
| V-4 | `ConfigValidator.cs:60-67` | Type has invalid facet value | **Use only `plannable` or `implementable`** — these are the only two values in the whitelist. Any other value will fail validation. |
| V-5 | `ConfigValidator.cs:71-74` | Type has no transitions | Add transition mappings under `transitions:` for the type |
| V-6 | `ConfigValidator.cs:88-95` | Transition references undefined type | Ensure all keys in `transitions:` exist in `types:` |
| V-7 | `ConfigValidator.cs:42-49` | Duplicate type name (case-insensitive) | Type names must be unique regardless of case |
| V-8 | `ConfigValidator.cs:77-84` | `allowed_child_types` references undefined type | Ensure all `allowed_child_types` values exist in `types:` |

> **Primitive whitelist callout (V-4):** The valid facet values are
> *exactly* `plannable` and `implementable`
> (`ConfigValidator.cs:60-67`, where the `ValidFacets` HashSet is
> `{ "plannable", "implementable" }`). Anything else — `actionable`,
> `reviewable`, `coordinatable`, etc. — fails V-4 with `Type '<name>' has
> invalid facet '<x>'. Valid values: plannable, implementable.` If you
> need a parent that only groups children without being implemented directly,
> use `facets: [plannable]` and rely on the parent type being
> inherently a grouping construct.

### Common Validation Warnings

| Rule ID | Source line | Meaning | Recommendation |
|---------|-------------|---------|---------------|
| V-9 | `ConfigValidator.cs:105-110` | Type definition file missing | Create `.polyphony-config/work-item-types/{slug}.md` per type |
| V-10 | `ConfigValidator.cs:113-119` | Template file missing | Create `.polyphony-config/work-item-types/templates/{slug}-template.md` per type |
| V-11 | `ConfigValidator.cs:122-127` | Architect guidance file missing | Create `.polyphony-config/agent-guidance/architect.md` |
| V-12 | `ConfigValidator.cs:130-134` | Coder guidance file missing | Create `.polyphony-config/agent-guidance/coder.md` |
| V-13 | `ConfigValidator.cs:137-141` | Reviewer guidance file missing | Create `.polyphony-config/agent-guidance/reviewer.md` |
| V-14 | `ConfigValidator.cs:144-148` | Profile file missing | Create `.polyphony-config/profile.yaml` |

The slug used by V-9 and V-10 is `ConfigValidator.ToSlug` — lowercase plus
spaces-to-hyphens (`ConfigValidator.cs:162-163`).

---

## 10. First Run

Once validation passes, you're ready to drive an apex through the polyphony
SDLC pipeline.

### Invoking the Workflow

The canonical SDLC entry point is `apex-driver@polyphony` — a tree-walking
dispatcher built on the EdgeGraph + `state next-ready` model that drives an
apex (run-root) work item end-to-end. The only required input is `apex_id`;
`intent` (`new` / `resume` / `replan`, default `new`), `platform` (default
`ado`), and `organization` / `project` / `repository` are optional and
threaded through to lifecycle sub-workflows. See
`.github/skills/polyphony-sdlc/SKILL.md` *Invocation* and the ADR
[`docs/decisions/apex-driver.md`](decisions/apex-driver.md) for the full
input contract and per-outcome examples.

```powershell
# Drive a fresh apex through a full SDLC pass
conductor run apex-driver@polyphony `
  --input apex_id=<ID> `
  --web

# Resume an in-flight apex after a human gate or interruption
conductor run apex-driver@polyphony `
  --input apex_id=<ID> `
  --input intent=resume `
  --web

# Full invocation with all inputs explicit (recommended for non-default platform / org)
conductor run apex-driver@polyphony `
  --input apex_id=<ID> `
  --input intent=new `
  --input platform=ado `
  --input organization=<org> `
  --input project=<project> `
  --input repository=<repo> `
  --web
```

> **Always launch detached** — wrap in `Start-Process -WindowStyle Hidden` so
> conductor survives if the parent session drops. Always use `--web`, not
> `--web-bg`.

### Recommended: `Invoke-PolyphonySdlc.ps1`

For day-to-day operator use, the launcher script `scripts/Invoke-PolyphonySdlc.ps1` wraps the `conductor run` invocation with the per-run worktree machinery from § 2. It auto-derives the worktree path under `~/projects/<repo>-runs/apex-{N}/`, refuses to dispatch into the operator's main worktree, refuses if the target is dirty, and threads the right twig org/project + git metadata through.

```powershell
# Dry-run (default) — print the resolved worktree path and the conductor command line, do NOT launch:
./scripts/Invoke-PolyphonySdlc.ps1 -ApexId <ID>

# Launch detached after dry-run looks right:
./scripts/Invoke-PolyphonySdlc.ps1 -ApexId <ID> -Commit

# Resume an in-flight apex:
./scripts/Invoke-PolyphonySdlc.ps1 -ApexId <ID> -Intent resume -Commit

# Override the platform / org / project (defaults are read from .twig/config):
./scripts/Invoke-PolyphonySdlc.ps1 -ApexId <ID> -Platform github -Commit
```

Key parameters:

| Parameter      | Purpose |
|----------------|---------|
| `-ApexId`      | Apex root work item id (required). |
| `-Intent`      | `new` (default), `resume`, or `replan`. |
| `-Platform`    | `ado` (default) or `github`. |
| `-Commit`      | Actually launch. Without `-Commit`, the script is a dry-run that prints what it would do. |
| `-NoDetach`    | Don't background the conductor process — useful for live debugging. |
| `-WorktreeRoot`| Override the auto-derived path. The script REFUSES if this resolves inside the operator's main worktree. |

The launcher's preflight chain (11 phases) catches every common failure mode before conductor starts: stale binary, dirty target worktree, wrong branch, hijack attempt, missing `.twig/config`, etc. See `scripts/Invoke-PolyphonySdlc.ps1` for the full list and the corresponding refusal messages.

The apex-driver re-derives the right leg per item per wave from observable
state, so individual sub-workflows (`plan-level`, `actionable`,
`implement-merge-group`, `feature-pr`, …) should rarely be invoked directly. Reach
for a sub-workflow invocation only when you want to *replay* or *override* a
single leg of an in-flight apex — see `workflows/README.md` for the per-leg
contracts.

### What to Expect

1. **Phase Detection** — Polyphony reads the work item tree and determines the
   current SDLC phase (planning, implementation, review, etc.).
2. **Routing** — Based on type facets and current state, Polyphony decides
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
polyphony state next-ready --work-item <id> --config .polyphony-config/process-config.yaml
```

This outputs a JSON routing decision showing the dispatchable requirements,
their dispositions, and hierarchy analysis. Look for:

- `requirements` — Which kinds came back as `ready` vs `blocked`?
- `type_facets` — Are your types correctly configured?

**2. Verify state transitions:**

```bash
polyphony validate --work-item <id> --event <target-state>
```

This checks whether the proposed transition is valid for the work item's type.

**3. Inspect the hierarchy:**

```bash
polyphony hierarchy --work-item <id> --depth 3
```

This shows the work item tree with type facets at each level.

**4. Common routing problems:**

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| Work item skipped | Type missing `implementable` facet | Add `implementable` to the type's facets |
| Infinite recursion | `max_nesting_depth` too high or self-referential without bound | Set a reasonable `max_nesting_depth` (1–3) |
| Wrong agent runs | Type facets misconfigured | Check that plannable types get architect, implementable get coder |
| PR not auto-merging | Review policy requires human review | Update `review_policies` to set `auto_merge: true` for PG PRs |
| Branch name wrong | Branch strategy pattern incorrect | Fix patterns in `branch_strategy` section |
| `Unknown state '<X>'` from twig | State name in `transitions:` doesn't exist in the template's state set for that type | Cross-check state names against `twig2/tests/Twig.TestKit/ProcessConfigBuilder.cs:48-96` |

---

## 11. Kyber Worked Example

This section shows a complete, annotated `.polyphony-config/` configuration for kyber,
a `KyberAgile` (custom) process repository on GitHub. Use this as a reference
when building your own config.

### Directory Structure

```
kyber/
└── .polyphony-config/
    ├── process-config.yaml
    ├── profile.yaml
    ├── agent-guidance/
    │   ├── architect.md
    │   ├── coder.md
    │   └── reviewer.md
    └── work-item-types/
        ├── epic.md
        ├── primitive.md
        ├── bug.md
        ├── task.md
        └── templates/
            ├── epic-template.md
            ├── primitive-template.md
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

# Type definitions with facets.
# Primitives — the ONLY two valid values:
# - plannable: gets architect/decomposition
# - implementable: leaf-level, directly coded
# (Source of truth: ConfigValidator.cs:60-67 — the ValidFacets HashSet.)
types:
  Epic:
    facets: [plannable]
    filing_eligible: false           # Epics don't receive closeout observations
    max_nesting_depth: 1
    allowed_child_types: [Primitive, Bug]
    decomposition_guidance: |
      Always decompose into Primitives (or Bugs, when fixing a regression in
      a shipped Primitive). Epics are never implemented directly.
      Each Primitive should represent a single crypto primitive or operation.

  Primitive:
    facets: [plannable, implementable]
    filing_eligible: true
    self_referential: false          # No nested Primitives
    max_nesting_depth: 1
    allowed_child_types: [Task]
    decomposition_guidance: |
      Decompose into Tasks for any Primitive touching `kyber-asm/`,
      requiring a new dudect harness, or estimated > 1 day.
      Implement directly when the change is a single primitive with reference
      vectors already present in `kyber-test-vectors/`.

  Bug:
    facets: [plannable, implementable]
    filing_eligible: true
    allowed_child_types: [Task]

  Task:
    facets: [implementable]
    filing_eligible: true            # Tasks receive closeout observations

# State transitions — maps workflow events to ADO state names.
# These use the standard Agile state set
# (twig2/tests/Twig.TestKit/ProcessConfigBuilder.cs:48-96), since KyberAgile
# is derived from Agile and does not rename any states. Note that User Story
# in standard Agile uses `Resolved` for implementation_complete; we mirror
# that for Primitive since the meanings are equivalent.
transitions:
  Epic:
    begin_planning: Active
    all_children_complete: Closed
    scope_removed: Removed
  Primitive:
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
    merge_group_pr:
      agent_review: true
      human_review: false            # PG PRs auto-merge after agent review
      auto_merge: true
    feature_pr:
      agent_review: true
      human_review: true             # Feature PRs require human review (crypto)
      auto_merge: false
  remediation:
    merge_group_pr:
      agent_review: true
      human_review: false
      auto_merge: true

# Branch naming patterns.
# Placeholders: {root_id}, {slug}, {n}
branch_strategy:
  feature_branch: "feature/{root_id}-{slug}"
  planning_branch: "planning/{root_id}"
  merge_group_branch: "pg-{n}/{root_id}-{slug}"
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
| Mid-level type | Issue | **Primitive** (custom name; same `[plannable, implementable]` facet set) |
| Active state | `Doing` | `Active` |
| Done state | `Done` | `Closed` (Tasks) / `Resolved` (Primitive, Bug) |
| `Removed` state available? | No | Yes (Agile state set) |
| Self-referential mid-level | No | No (Primitive does not nest) |
| Bug type | — | Yes; sibling of Primitive under Epic |
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
- [ ] Customize `process-config.yaml` — types, facets (only `plannable`
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
- [ ] Run `polyphony validate-config --config .polyphony-config --output human`
- [ ] Fix any errors, review warnings
- [ ] Run `conductor run apex-driver@polyphony --input apex_id=<id> --web` on a test apex work item
- [ ] Verify routing, agent behavior, and PR lifecycle work correctly



