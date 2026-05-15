# Polyphony

**Type-agnostic SDLC routing engine and conductor workflow suite.**

Polyphony takes any Azure DevOps work item — Epic, Issue, Bug, custom-process
type, at any hierarchy depth — and drives it through a full plan → implement →
review → merge → close-out lifecycle, using deterministic routing logic instead
of hardcoded type names.

It is the *replacement* for the original `twig-sdlc-full@twig` workflow, which
was wired specifically to the Basic process template (Epic → Issue → Task).
Polyphony reads `.polyphony-config/process-config.yaml` and adapts to **Basic, Agile,
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

YAML workflow files driving `conductor` (the multi-agent orchestrator). The
**`apex-driver@polyphony`** workflow is the canonical SDLC entry point — a
tree-walking dispatcher built on the EdgeGraph + `state next-ready` model
that drives an apex (run-root) work item end-to-end through planning
(`plan-level`), implementation (`implement-merge-group`, `implement-mg`), PR
lifecycle (`feature-pr`, `github-pr`, `ado-pr`), and close-out. The
workflows shell out to the polyphony CLI for every routing decision and
every configuration query, so the YAML itself contains zero type-name
conditionals.

The sub-workflows above can also be invoked directly when you want to
replay or override a single leg of a run (see [`workflows/README.md`](workflows/README.md)),
but `apex-driver` is what you reach for to drive an SDLC pass.

The two share a name (and ship from the same repo so they version together),
but they are independent artifacts. You install the CLI as a binary; you
register the workflow suite with `conductor`.

### Plus: agent skills and per-target config

- **`.github/skills/`** — nine skills (CLI dev, workflow author, SDLC
  operator, conductor mechanics, bootstrap onboarding, twig CLI / SDLC, the
  design/mechanics design pair, and the workflow path-coverage harness)
  loaded by Copilot CLI / Claude Code when working in this codebase.
- **`.polyphony-config/`** — the configuration consumed by both the CLI and the
  workflow suite at runtime. Polyphony's *own* `.polyphony-config/` directory is
  the dogfood example: this repo runs itself through the polyphony workflow
  suite.

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
its types in `.polyphony-config/process-config.yaml`.

For the deeper "why split it this way?" — see
[`docs/polyphony-architecture.md`](docs/polyphony-architecture.md) and §12 of
[`docs/polyphony-cli-reference.md`](docs/polyphony-cli-reference.md)
(*"How much value is the CLI actually adding?"*).

---

## Install

You will need:

- **.NET 11 SDK** — to build the CLI **from source** (operators using the
  pre-built binary do not need this).
- **PowerShell 7+** — workflow scripts are PowerShell.
- **`twig` CLI** — Polyphony's write-side companion. Install from
  [`PolyphonyRequiem/twig`](https://github.com/PolyphonyRequiem/twig) and put
  it on PATH (typically at `~/.twig/bin/twig`).
- **`conductor` CLI** — multi-agent workflow orchestrator. Required only if
  you want to *run* the workflow suite, not if you only want to use the CLI.
- **`gh` CLI** — used by the GitHub PR sub-workflow.
- **`git` CLI** — git worktrees are how we run multiple SDLC instances in
  parallel.

### Operator one-liner (recommended)

If you don't intend to develop polyphony itself — you just want to *use* it
to drive an SDLC run on some other repo — install everything in one shot:

**Windows (PowerShell)**
```powershell
iex (irm https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/install.ps1)
```

**Linux / macOS (bash)**
```bash
curl -fsSL https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/install.sh | bash
```

Both scripts download the latest release binary, the launcher trio, and
both copilot skills (`polyphony-runtime`, `polyphony-bootstrap`) into
`~/.twig/bin/` and `~/.copilot/skills/` respectively. Idempotent — re-run
to upgrade. Pin a specific version with `install.ps1 -Version v2.4.0` /
`install.sh v2.4.0`. Prereqs (git, pwsh, conductor) are *not* auto-installed
— the script warns if any are missing and prints the install command.

After install, open a copilot CLI session in any repo and ask
*"set up polyphony for this repo"* (bootstrap) or *"run polyphony for
work item N"* (runtime).

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

conductor registry list polyphony       # Lists the registered workflows
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
conductor validate .conductor/registry/workflows/plan-level.yaml
```

If all four pass, you're ready to run a workflow.

---

## Quick start

The fastest way to see polyphony work is to drive an existing ADO work item
through `apex-driver@polyphony`. From a repo that already has its own
`.polyphony-config/` configured (see *Configure your repo*, below):

```powershell
# Set up an isolated worktree for this apex
$APEX = 1234
git worktree add -b sdlc/$APEX ../$(Split-Path $pwd -Leaf)-$APEX main
cd ../$(Split-Path $pwd -Leaf)-$APEX
dotnet restore
twig set $APEX
twig sync

# Drive the apex through a full SDLC pass.
Start-Process -WindowStyle Hidden -FilePath conductor -ArgumentList @(
  "run", "apex-driver@polyphony",
  "--input", "apex_id=$APEX",
  "--input", "intent=new",
  "--input", "platform=ado",
  "--input", "organization=<org>",
  "--input", "project=<project>",
  "--input", "repository=<repo>",
  "-m", "tracker=ado",
  "-m", "project_url=https://dev.azure.com/<org>/<project>",
  "-m", "git_repo=$((Resolve-Path ..).Path)\<repo>",
  "-m", "workitem_id=$APEX",
  "-m", "worktree_name=<repo>-$APEX",
  "-m", "cwd=$(Resolve-Path .)",
  "--web"
)
```

Use `--input intent=resume` to re-enter an in-flight apex after a human gate
or interruption; the dispatch loop is observable-state-driven and re-derives
the next wave from the work-item tree on every iteration. Sub-workflows
(`plan-level`, `implement-merge-group`, `feature-pr`, …) can be invoked directly to
replay or override a single leg — see [`workflows/README.md`](workflows/README.md).

The full set of metadata fields (and what each does in the dashboard) is
documented in the
[`polyphony-sdlc` skill](.github/skills/polyphony-sdlc/SKILL.md).

For a no-workflow smoke test that exercises only the CLI:

```powershell
polyphony state next-ready --work-item 1234 # JSON: dispatchable requirements + edges
polyphony hierarchy   --work-item 1234       # Tree with role annotations
polyphony validate    --work-item 1234 --event begin_planning
polyphony state preflight --work-item 1234   # Full preflight (12 checks)
```

---

## Configure your repo

To onboard a *different* repo to polyphony — i.e. a target codebase you want
the workflow suite to operate on — you create a `.polyphony-config/` directory with
the following layout. Polyphony's own `.polyphony-config/` is the dogfood example.

```
.polyphony-config/
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

For everything that lives in `.polyphony-config/` *outside* `process-config.yaml`,
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
| `polyphony validate`          | Validate a state transition against ADO + SDLC rules.    |
| `polyphony validate-config`   | Schema-check `.polyphony-config/process-config.yaml`.           |
| `polyphony hierarchy`         | Display work item hierarchy with role annotations.       |

### `polyphony state <verb>`

| Verb                              | Purpose                                                          |
|-----------------------------------|------------------------------------------------------------------|
| `polyphony state preflight`       | Verify config, tools, and work item readiness before a run.      |
| `polyphony state preflight-lite`  | Lightweight subset for nested workflow entry points.             |
| `polyphony state next-ready`      | Dispatchable requirements for a work item (EdgeGraph driver).    |

### `polyphony plan <verb>`

| Verb                            | Purpose                                              |
|---------------------------------|------------------------------------------------------|
| `polyphony plan depth-guard`    | Enforce the recursion-depth budget.                  |
| `polyphony plan next-child`     | Pick the next plannable child for recursive planning.|
| `polyphony plan load-type`      | Inject type definition + template into prompts.      |
| `polyphony plan load-guidance`  | Inject `.polyphony-config/agent-guidance/*.md` into prompts.|
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

The YAMLs in `.conductor/registry/workflows/`:

| File                               | Role                                                                |
|------------------------------------|---------------------------------------------------------------------|
| `apex-driver.yaml`                 | **Canonical SDLC entry point.** Tree-walking dispatch over EdgeGraph waves with per-item worktree isolation, observable-state re-entry, and renegotiation handling. |
| `apex-wave-dispatch.yaml`          | Per-wave inner sub-workflow invoked by apex-driver — for_each over wave items + integrate the wave. |
| `apex-item-dispatch.yaml`          | Per-item innermost sub-workflow invoked by apex-wave-dispatch — classify lifecycle, spawn/teardown worktree, dispatch lifecycle. |
| `plan-level.yaml`                  | Recursive planning core. Self-recurses for nested plannable levels. |
| `actionable.yaml`                  | Actionable-facet workflow — executor router, polyphony evidence PR or human satisfaction gate. |
| `implement-merge-group.yaml`                | Single PG lifecycle: tasks → review → PR → merge → scope close.     |
| `implement-mg.yaml`                | Single merge-group lifecycle.                                       |
| `github-pr.yaml`                   | GitHub PR lifecycle (review + fix loop, max 10 iterations).         |
| `ado-pr.yaml`                      | ADO PR lifecycle (currently a manual-gate stub).                    |
| `feature-pr.yaml`                  | Feature PR + remediation cycles (max 3, then human gate).           |
| `close-out.yaml`                   | Post-mortem + structured-observation filing.                        |
| `cascade-remedy.yaml`              | Cascade remediation across descendant plans.                        |
| `remedy-stale-descendant.yaml`     | Stale-descendant remediation sub-workflow.                          |
| `root-fallback-gate.yaml`          | Fallback gate when a sub-workflow is invoked without a root work-item id. |

> **Reach for `apex-driver@polyphony` first.** The other workflows are valid
> as targeted single-leg invocations (replay a planning level, re-run a
> feature-PR remediation cycle), but the apex-driver is what runs an SDLC
> pass end-to-end. See [`workflows/README.md`](workflows/README.md) and
> [`docs/decisions/apex-driver.md`](docs/decisions/apex-driver.md).

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
| [`docs/polyphony-conductor-directory.md`](docs/polyphony-conductor-directory.md)          | Everything in `.polyphony-config/` outside `process-config.yaml`.             |
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
| `polyphony-sdlc`            | Invoking, debugging, or extending the polyphony workflow suite.        |
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

### Workflow path-coverage harness

For testing the conductor workflows themselves, this repo ships a
**scenario-based path-coverage harness** under
[`tests/harness/`](tests/harness/README.md). It runs the real conductor
engine in-process from Python with a scripted `FakeProvider` for the LLM
boundary and a .NET shim binary on PATH for `script:` calls — no real LLM
tokens, no ADO writes, no git mutations. Scenarios live under
`tests/harness/scenarios/<name>/scenario.yaml` and run on every PR build
via the existing Pester step in CI.

Add a scenario when you want to pin a workflow path against regression
(e.g. a real dogfood incident). See
[`docs/decisions/harness-mvp.md`](docs/decisions/harness-mvp.md) for the
design ADR and
[`.github/skills/polyphony-harness/SKILL.md`](.github/skills/polyphony-harness/SKILL.md)
for scenario-authoring conventions.

Polyphony dogfoods itself — feature work on this repo is normally driven by
the polyphony workflow suite. Direct commits to `main` are reserved for hotfixes
and the rare doc-only change. See the
[`polyphony-sdlc`](.github/skills/polyphony-sdlc/SKILL.md) skill for how to
launch a run against a polyphony Epic in this repo.

---

## Architecture in one paragraph

The CLI references `Twig.Domain` and `Twig.Infrastructure` for work-item
models and SQLite cache reads, reads `.polyphony-config/process-config.yaml` (and
optionally `policy.yaml`) for type facets and transition mappings,
emits structured JSON to stdout, and uses exit codes to signal routing
outcomes. **Routing decisions are fully deterministic** — no AI, no LLM, no
non-determinism. AI lives in the *agent* layer of the workflow suite (the
architect, coder, reviewer roles); polyphony is the calm rules engine
underneath that gives those agents a stable contract surface to route on.

For more, see [`docs/polyphony-architecture.md`](docs/polyphony-architecture.md).


