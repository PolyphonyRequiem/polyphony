# Polyphony

**Type-agnostic SDLC routing engine and conductor workflow suite.**

Polyphony takes any Azure DevOps work item — Epic, Issue, Bug, custom-process
type, at any hierarchy depth — and drives it through a full plan → implement →
review → merge → close-out lifecycle, using deterministic routing logic instead
of hardcoded type names.

It is the *replacement* for the original `twig-sdlc-full@twig` workflow, which
was wired specifically to the Basic process template (Epic → Issue → Task).
Polyphony reads `.conductor/process-config.yaml` and adapts to **Basic, Agile,
Scrum, CMMI, and custom process templates** without changes to the workflow
YAML itself.

---

## What this repo ships

This repo is **two artifacts in one tree**:

### 1. The polyphony CLI binary (`src/Polyphony/`)

A .NET 11 CLI exposing ~24 verbs across 9 command groups. It does
deterministic things — phase detection, transition validation, branch-name
resolution, hierarchy walking, policy resolution — and emits structured JSON.
**It writes nothing to ADO**; writes are always delegated to the
[`twig`](https://github.com/PolyphonyRequiem/twig) CLI.

The CLI is what makes "type-agnostic" actually work: it consumes the process
config and the work item, and tells the orchestrator what state the work is
in and what to do next.

### 2. The conductor workflow suite (`.conductor/registry/workflows/`)

Nine YAML files registered as `polyphony-full@polyphony` that drive
`conductor` (the multi-agent orchestrator) through the full SDLC. The workflows
shell out to the polyphony CLI for every routing decision and every
configuration query, so the YAML itself contains zero type-name conditionals.

The two share a name (and ship from the same repo so they version together),
but they are independent artifacts. You install the CLI as a binary; you
register the workflow suite with `conductor`.

### Plus: agent skills and per-target config

- **`.github/skills/`** — eight skills (CLI dev, workflow author, SDLC
  operator, conductor mechanics, bootstrap onboarding, twig CLI / SDLC, and
  the design/mechanics design pair) loaded by Copilot CLI / Claude Code when
  working in this codebase.
- **`.conductor/`** — the configuration consumed by both the CLI and the
  workflow suite at runtime. Polyphony's *own* `.conductor/` directory is
  the dogfood example: this repo runs itself through `polyphony-full@polyphony`.

---

## Why

The previous SDLC engine baked Basic-process assumptions into every workflow
script: `if (type == "Epic") plan, elif "Issue" implement, elif "Task" leaf`.
Migrating to Agile or CMMI or a custom process meant rewriting the workflows.

Polyphony pulls those decisions out of the workflows and into a config-driven
engine:

- **Phase** — what SDLC phase is this work item in? (`needs_planning`,
  `ready_for_implementation`, `ready_for_completion`, …)
- **Action** — what should happen next? (plan, seed children, implement,
  review, close)
- **Validation** — is this state transition legal given the configured
  process rules and SDLC preconditions?

The workflow YAML routes on the *answers*, not on type names. This means the
same conductor workflow runs against any ADO process template that declares
its types in `.conductor/process-config.yaml`.

For the deeper "why split it this way?" — see
[`docs/polyphony-architecture.md`](docs/polyphony-architecture.md) and §12 of
[`docs/polyphony-cli-reference.md`](docs/polyphony-cli-reference.md)
(*"How much value is the CLI actually adding?"*).

---

## Install

You will need:

- **.NET 11 SDK** — to build the CLI.
- **PowerShell 7+** — workflow scripts are PowerShell.
- **`twig` CLI** — Polyphony's write-side companion. Install from
  [`PolyphonyRequiem/twig`](https://github.com/PolyphonyRequiem/twig) and put
  it on PATH (typically at `~/.twig/bin/twig`).
- **`conductor` CLI** — multi-agent workflow orchestrator. Required only if
  you want to *run* the workflow suite, not if you only want to use the CLI.
- **`gh` CLI** — used by the GitHub PR sub-workflow.
- **`git` CLI** — git worktrees are how we run multiple SDLC instances in
  parallel.

### Build and install the CLI

```powershell
git clone https://github.com/PolyphonyRequiem/polyphony.git
cd polyphony
dotnet restore
./publish-local.ps1     # publishes to ~/.twig/bin/polyphony(.exe)
polyphony --version
polyphony health        # Validates env + config; should show all green
```

`publish-local.ps1` deploys to `~/.twig/bin/` so the same install location
serves both `twig` and `polyphony`.

### Register the workflow suite with conductor

```powershell
# From the polyphony repo root — point conductor at this repo's registry path
conductor registry add polyphony .

conductor registry list polyphony       # Lists polyphony-full and friends
```

The `add` source can be either a local path (above) or a GitHub `owner/repo`:

```powershell
conductor registry add polyphony PolyphonyRequiem/polyphony
```

### Verify

```powershell
polyphony health                                # CLI + env diagnostics
polyphony validate-config                       # process-config.yaml schema
polyphony policy validate                       # policy.yaml schema (if used)
conductor validate .conductor/registry/workflows/polyphony-full.yaml
```

If all four pass, you're ready to run a workflow.

---

## Quick start

The fastest way to see polyphony work is to drive an existing ADO work item
through it. From a repo that already has its own `.conductor/` configured
(see *Configure your repo*, below):

```powershell
# Set up an isolated worktree for this work item
$ID = 1234
git worktree add -b sdlc/$ID ../$(Split-Path $pwd -Leaf)-$ID main
cd ../$(Split-Path $pwd -Leaf)-$ID
dotnet restore
twig set $ID
twig sync

# Launch the workflow detached, with the conductor web UI for live status
Start-Process -WindowStyle Hidden -FilePath conductor -ArgumentList @(
  "run", "polyphony-full@polyphony",
  "--input", "work_item_id=$ID",
  "--input", "intent=resume",
  "-m", "tracker=ado",
  "-m", "project_url=https://dev.azure.com/<org>/<project>",
  "-m", "git_repo=$((Resolve-Path ..).Path)\<repo>",
  "-m", "workitem_id=$ID",
  "-m", "worktree_name=<repo>-$ID",
  "-m", "cwd=$(Resolve-Path .)",
  "--web"
)
```

The full set of metadata fields (and what each does in the dashboard) is
documented in the
[`polyphony-sdlc` skill](.github/skills/polyphony-sdlc/SKILL.md).

For a no-workflow smoke test that exercises only the CLI:

```powershell
polyphony route       --work-item 1234       # JSON: phase + action + reason
polyphony hierarchy   --work-item 1234       # Tree with role annotations
polyphony validate    --work-item 1234 --event begin_planning
polyphony state preflight --work-item 1234   # Full preflight (12 checks)
```

---

## Configure your repo

To onboard a *different* repo to polyphony — i.e. a target codebase you want
the workflow suite to operate on — you create a `.conductor/` directory with
the following layout. Polyphony's own `.conductor/` is the dogfood example.

```
.conductor/
├── process-config.yaml      # types, facets, transitions, review policy
├── policy.yaml              # (optional) implementation modes + per-scope caps
├── profile.yaml             # default agent + workflow tuning per repo
├── work-item-types/         # one .md per type — definition + template
│   ├── epic.md
│   ├── issue.md
│   └── task.md
├── agent-guidance/          # markdown guidance injected into agent prompts
│   ├── architect.md
│   ├── coder.md
│   └── reviewer.md
└── registry/                # only present if you ship workflows from this repo
    ├── workflows/*.yaml
    └── scripts/*.ps1
```

The full step-by-step walkthrough — including a fictitious **kyber** worked
example using a custom `KyberAgile` process template — is in
[`docs/onboarding-guide.md`](docs/onboarding-guide.md). Activate the
`polyphony-bootstrap` skill in your agent for an interactive bootstrap.

A short tour of each file:

- **`process-config.yaml`** — the heart of type-agnosticism. Declares your
  ADO process template name, every work-item type with its `facets`
  (`plannable` / `implementable`), nesting depth, decomposition guidance, and
  the state transitions for SDLC events (`begin_planning`,
  `implementation_complete`, etc.). Schema lives at
  [`docs/polyphony-process-config-schema.md`](docs/polyphony-process-config-schema.md).
- **`policy.yaml`** — optional. Declares implementation **modes**
  (e.g. `loose` / `strict`) and per-scope caps (review thresholds, dependency
  rules). Resolved by `polyphony policy resolve`. Surface documented in
  the deep-dive's §7.
- **`profile.yaml`** — per-repo agent/workflow tuning (default agent model
  preferences, workflow toggles).
- **`work-item-types/<slug>.md`** — one markdown file per type. The plan
  agent reads this to learn the type's purpose and template before planning.
- **`agent-guidance/*.md`** — supplemental prompts injected into specific
  agent roles. Use this to steer the architect, coder, reviewer, etc., with
  repo-specific conventions.

For everything that lives in `.conductor/` *outside* `process-config.yaml`,
see [`docs/polyphony-conductor-directory.md`](docs/polyphony-conductor-directory.md).

---

## CLI verbs at a glance

For per-verb depth — synopsis, flags, JSON shape, exit codes, when-to-use,
when-NOT-to-use, and source-of-truth pointers — read
[**`docs/polyphony-cli-reference.md`**](docs/polyphony-cli-reference.md).
The tables below are the quick-reference index.

### Top-level

| Command                       | Purpose                                                  |
|-------------------------------|----------------------------------------------------------|
| `polyphony health`            | Environment + configuration diagnostics.                 |
| `polyphony route`             | Phase + action routing for a work item.                  |
| `polyphony validate`          | Validate a state transition against ADO + SDLC rules.    |
| `polyphony validate-config`   | Schema-check `.conductor/process-config.yaml`.           |
| `polyphony hierarchy`         | Display work item hierarchy with role annotations.       |

### `polyphony state <verb>`

| Verb                              | Purpose                                                          |
|-----------------------------------|------------------------------------------------------------------|
| `polyphony state preflight`       | Verify config, tools, and work item readiness before a run.      |
| `polyphony state preflight-lite`  | Lightweight subset for nested workflow entry points.             |
| `polyphony state detect`          | Root-workflow phase detection (`state_detector` JSON shape).     |

### `polyphony plan <verb>`

| Verb                            | Purpose                                              |
|---------------------------------|------------------------------------------------------|
| `polyphony plan depth-guard`    | Enforce the recursion-depth budget.                  |
| `polyphony plan next-child`     | Pick the next plannable child for recursive planning.|
| `polyphony plan load-type`      | Inject type definition + template into prompts.      |
| `polyphony plan load-guidance`  | Inject `.conductor/agent-guidance/*.md` into prompts.|
| `polyphony plan review`         | Aggregate planner reviews and gate revision cycles.  |
| `polyphony plan seed-children`  | Marker-based child seeding with idempotent re-entry. |

### `polyphony branch <verb>`

| Verb                            | Purpose                                                       |
|---------------------------------|---------------------------------------------------------------|
| `polyphony branch route`        | PG lifecycle — pick the next PG action.                       |
| `polyphony branch load-tree`    | Hierarchy → PG-grouped tree with completion + branch state.   |
| `polyphony branch ensure-feature` | Idempotently ensure the feature branch exists.              |
| `polyphony branch next-impl`    | Within-PG task selection via facet filtering.            |
| `polyphony branch check-deps`   | ADO predecessor link check.                                   |
| `polyphony branch close-scope`  | Validate then transition leaf items at scope close.           |

### `polyphony pr <verb>`

| Verb                              | Purpose                                              |
|-----------------------------------|------------------------------------------------------|
| `polyphony pr create-feature-pr`  | Create a feature PR via `gh`, against `workspace_hint`. |

### `polyphony policy <verb>`

| Verb                       | Purpose                                                                       |
|----------------------------|-------------------------------------------------------------------------------|
| `polyphony policy load`    | Load and validate `policy.yaml`, emit resolved JSON for the run.              |
| `polyphony policy validate`| Schema-only validation of a `policy.yaml` file.                               |
| `polyphony policy resolve` | Resolve effective mode + caps for a given scope (`root`, `type:Foo`, `default`). |

---

## The workflow suite at a glance

The nine YAMLs in `.conductor/registry/workflows/`:

| File                       | Role                                                                |
|----------------------------|---------------------------------------------------------------------|
| `polyphony-full.yaml`      | Root entry. Preflight + phase detection + dispatch.                 |
| `polyphony-planning.yaml`  | Planning entry. Lite preflight + recursive plan + work-tree seed.   |
| `plan-level.yaml`          | Recursive planning core. Self-recurses for nested plannable levels. |
| `polyphony-implement.yaml` | Implementation entry. Loads work tree, dispatches PGs in parallel.  |
| `implement-pg.yaml`        | Single PG lifecycle: tasks → review → PR → merge → scope close.     |
| `github-pr.yaml`           | GitHub PR lifecycle (review + fix loop, max 10 iterations).         |
| `ado-pr.yaml`              | ADO PR lifecycle (currently a manual-gate stub).                    |
| `feature-pr.yaml`          | Feature PR + remediation cycles (max 3, then human gate).           |
| `close-out.yaml`           | Post-mortem + structured-observation filing.                        |

For agent rosters, recursion budgets, and the platform-abstraction model
(GitHub vs. ADO), read the
[`polyphony-sdlc`](.github/skills/polyphony-sdlc/SKILL.md) skill.

---

## Documentation

| Doc                                                                                       | Topic                                                                  |
|-------------------------------------------------------------------------------------------|------------------------------------------------------------------------|
| [`docs/glossary.md`](docs/glossary.md)                                                    | Ubiquitous-language reference. Start here when in doubt about a term.  |
| [`docs/polyphony-cli-reference.md`](docs/polyphony-cli-reference.md)                      | Per-verb deep-dive, conceptual primers, and value assessment.          |
| [`docs/polyphony-architecture.md`](docs/polyphony-architecture.md)                        | Layering diagram, three-vocabularies rule, Polyphony-vs-twig boundary. |
| [`docs/polyphony-process-config-schema.md`](docs/polyphony-process-config-schema.md)      | Full schema for `process-config.yaml`.                                 |
| [`docs/polyphony-conductor-directory.md`](docs/polyphony-conductor-directory.md)          | Everything in `.conductor/` outside `process-config.yaml`.             |
| [`docs/onboarding-guide.md`](docs/onboarding-guide.md)                                    | Step-by-step new-repo onboarding, with worked example.                 |
| [`docs/polyphony-skills-index.md`](docs/polyphony-skills-index.md)                        | Index of the agent skills shipped under `.github/skills/`.             |
| [`docs/polyphony-agent-failure-modes.md`](docs/polyphony-agent-failure-modes.md)          | Known failure modes and remediation patterns.                          |
| [`docs/decisions/`](docs/decisions/)                                                      | ADRs (verb-migration rationale, DU adoption, etc.).                    |

Agent skills (loaded by Copilot CLI and Claude Code when in this repo):

| Skill                       | When it activates                                                       |
|-----------------------------|-------------------------------------------------------------------------|
| `polyphony-bootstrap`       | Onboarding a new repo to the polyphony engine.                          |
| `polyphony-cli-developer`   | Adding/modifying/testing CLI verbs in `src/Polyphony/Commands/`.        |
| `polyphony-workflow-author` | Authoring/modifying workflow YAMLs or PowerShell scripts.               |
| `polyphony-sdlc`            | Invoking, debugging, or extending the polyphony-full workflow suite.    |
| `conductor-design`          | Designing/reviewing/modifying conductor workflows (principles).         |
| `conductor-mechanics`       | Authoring/debugging conductor YAML (runtime plumbing).                  |
| `twig-cli`                  | Managing ADO work items via the twig CLI.                               |
| `twig-sdlc`                 | Running the legacy twig-sdlc-full workflow.                             |

---

## Building and contributing

```powershell
dotnet restore
dotnet build
dotnet test
./publish-local.ps1     # publish to ~/.twig/bin/
```

`publish-local.ps1 -Configuration Debug` builds the debug variant for faster
iteration; the publish is intentionally Release-only by default. AOT publish
is currently disabled (see `src/Polyphony/Polyphony.csproj` for the rationale)
but the codebase remains AOT-friendly so re-enabling is a csproj-level change.

Workflow scripts under `.conductor/registry/scripts/` are tested with Pester:

```powershell
Invoke-Pester tests/
```

Polyphony dogfoods itself — feature work on this repo is normally driven by
`polyphony-full@polyphony`. Direct commits to `main` are reserved for hotfixes
and the rare doc-only change. See the
[`polyphony-sdlc`](.github/skills/polyphony-sdlc/SKILL.md) skill for how to
launch a run against a polyphony Epic in this repo.

---

## Architecture in one paragraph

The CLI references `Twig.Domain` and `Twig.Infrastructure` for work-item
models and SQLite cache reads, reads `.conductor/process-config.yaml` (and
optionally `policy.yaml`) for type facets and transition mappings,
emits structured JSON to stdout, and uses exit codes to signal routing
outcomes. **Routing decisions are fully deterministic** — no AI, no LLM, no
non-determinism. AI lives in the *agent* layer of the workflow suite (the
architect, coder, reviewer roles); polyphony is the calm rules engine
underneath that gives those agents a stable contract surface to route on.

For more, see [`docs/polyphony-architecture.md`](docs/polyphony-architecture.md).


