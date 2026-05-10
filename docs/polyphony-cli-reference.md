# Polyphony CLI Reference

The Polyphony CLI is a single .NET 11 binary that exposes ~24 verbs
across 9 command groups. It is the **deterministic decision layer** of the
polyphony SDLC system — every `polyphony <verb>` is a pure-ish function over
twig's local SQLite cache and a small set of YAML config files, emitting JSON.

This document explains:

1. What polyphony-the-CLI is, versus polyphony-the-workflow-suite (they share
   a name and a repo but do different things).
2. The conceptual model behind each command group — *routing*, *validation*,
   *hierarchy*, *state*, *planning*, *policy*, *branch lifecycle*, *PR lifecycle*,
   *diagnostics*.
3. Per-verb reference: synopsis, JSON shape, exit codes, when-to-use.
4. An honest assessment of how much value the CLI is actually adding to the
   solution — what it's load-bearing for, and what it's not.

The ground truth for "what verbs exist" is the `app.Add<…>()` block in
`Program.cs`:

```text
src/Polyphony/Program.cs:23-32
  app.Add<RouteCommand>();
  app.Add<ValidateCommand>();
  app.Add<ValidateConfigCommand>();
  app.Add<HierarchyCommand>();
  app.Add<HealthCommand>();
  app.Add<PlanCommands>("plan");
  app.Add<PolicyCommands>("policy");
  app.Add<BranchCommands>("branch");
  app.Add<StateCommands>("state");
  app.Add<PrCommands>("pr");
```

If you want a one-screen list, run `polyphony --help`.

---

## 1 · CLI vs. workflow: two halves of one system

Polyphony exists in two layers that share a name:

| Layer | What it is | Where it lives | Who consumes it |
|---|---|---|---|
| **Polyphony CLI** | C# binary; ~24 verbs returning JSON. Pure decisions over twig cache + config. | `src/Polyphony/`, ships as `polyphony.exe` | Workflow YAMLs (via `pwsh -Command "polyphony …"`); humans at the terminal |
| **Polyphony workflow suite** | Conductor YAML files (apex-driver tree-walker, planning core, implementation entry, PR sub-workflows, close-out). Multi-agent orchestration. The canonical SDLC entry point is `apex-driver@polyphony`, which dispatches sub-workflows per item per wave from observable state. | `.conductor/registry/workflows/` | Conductor runtime (`conductor run apex-driver@polyphony --input apex_id=<ID>` for a full pass; `conductor run <sub-workflow>@polyphony …` for targeted replays); humans at human-gates |

They ship from the same repo since the in-repo workflow co-location migration,
but they are independent artifacts:

```text
                ┌─────────────────────────────────────────────────┐
                │  conductor (third-party orchestrator)            │
                │  parses YAML, dispatches agents + scripts,       │
                │  runs human gates                                │
                └────────────────────────┬────────────────────────┘
                                         │
              ┌──────────────────────────┴──────────────────────────┐
              │                                                      │
              ▼                                                      ▼
   ┌──────────────────────────┐              ┌──────────────────────────────────┐
   │  Polyphony WORKFLOW       │              │  LLM agents (Opus, Sonnet)       │
   │  suite (9 YAMLs)          │  dispatches  │  for judgment work               │
   │  root + planning +        │ ───────────► │  (architect, reviewer, coder,    │
   │  implementation +         │              │   fixer, merger, scope_reviewer) │
   │  close-out + PR lifecycles│              └──────────────────────────────────┘
   └─────────────┬────────────┘
                 │ shells out to
                 ▼
   ┌──────────────────────────┐              ┌──────────────────────────────────┐
   │  Polyphony CLI            │  reads from  │  twig SQLite cache               │
   │  (this binary)            │ ───────────► │  (.twig/twig.db)                 │
   │                           │              │  populated by twig sync          │
   └─────────────┬────────────┘              └──────────────────────────────────┘
                 │ shells out to (for writes)
                 ▼
   ┌──────────────────────────┐              ┌──────────────────────────────────┐
   │  twig CLI                 │  PATCHes     │  Azure DevOps REST API           │
   │  (sibling project)        │ ───────────► │                                  │
   └──────────────────────────┘              └──────────────────────────────────┘
```

**Polyphony CLI never writes to ADO.** Writes go through twig, which the CLI
shells out to via `ITwigClient` (`src/Polyphony/Infrastructure/Processes/`).
Every CLI verb is a *read* + *decision* — the workflow then takes that
decision and applies it via twig.

The split is a deliberate single-responsibility boundary:

- **Workflow YAML** declares *what should happen and in what order* (graph of
  agents and scripts, with routes between them).
- **Polyphony CLI** decides *what state we're in and what's legal next*
  (deterministic reads, type-agnostic, JSON-out).
- **Agents** do *judgment work* (writing code, reviewing it, planning).
- **Twig CLI** *executes writes* (state transitions, comments, sync).

For the workflow suite documentation — which YAML calls which verb, what each
agent's job is, the recursion budget — see the **`polyphony-sdlc` skill**
(`.github/skills/polyphony-sdlc/SKILL.md`). This document is purely about the
CLI.

### How do I run an SDLC pass?

The CLI itself does not run a pass — it reports state and decides routing.
The pass is run by the conductor workflow suite. The canonical entry point
is `apex-driver@polyphony`:

```powershell
conductor run apex-driver@polyphony --input apex_id=<ID> --web
```

`apex_id` is the only required input. `intent` (`new` / `resume` / `replan`,
default `new`), `platform` (default `ado`), and `organization` / `project` /
`repository` are optional and threaded through to lifecycle sub-workflows.
For the full invocation contract (with the `-m` metadata block, prerequisites,
and per-leg replay) see [`workflows/README.md`](../workflows/README.md), the
`polyphony-sdlc` skill, and the ADR
[`docs/decisions/apex-driver.md`](decisions/apex-driver.md).

---

## 2 · Verbs at a glance

| Group | Verb | One-line purpose | Reads | Writes |
|---|---|---|---|---|
| top-level | `route` | Detect SDLC phase + recommended action for a work item | twig cache | — |
| top-level | `validate` | Check whether a lifecycle event is legal; return `target_state` | twig cache | — |
| top-level | `validate-config` | Schema-validate `process-config.yaml` against rules V-1..V-14 | config file | — |
| top-level | `hierarchy` | Walk the work-item tree; annotate each node with facets | twig cache | — |
| top-level | `health` | Diagnostic checks (SQLite, dotnet, twig on PATH, etc.) | environment | — |
| `state` | `preflight` | Full SDLC entry gate: 4 required + 3 advisory checks | git, twig, gh, ado | — |
| `state` | `preflight-lite` | Quick 3-check entry gate for the planning sub-workflow | git, twig | — |
| `state` | `detect` | Root routing payload: phase + plan artifacts + git/PR state | twig cache, fs, git, gh | — |
| `plan` | `depth-guard` | Validate recursion depth against a configured maximum | (args only) | — |
| `plan` | `next-child` | List immediate plannable children of a work item | twig cache | — |
| `plan` | `load-type` | Load type-definition + template + decomposition guidance | config files | — |
| `plan` | `load-guidance` | Load `agent-guidance/*.md` into a role map | config files | — |
| `plan` | `review` | Aggregate technical+readability reviewer JSON; emit pass/fail | (args only) | — |
| `plan` | `seed-children` | Idempotently reconcile architect's child list as ADO children | twig cache | twig (writes) |
| `policy` | `load` | Load `policy.yaml` (or defaults) and emit a snapshot | policy file | — |
| `policy` | `validate` | Schema-validate `policy.yaml` without applying defaults | policy file | — |
| `policy` | `resolve` | Effective rule for `<scope>` within `<domain>` (most-specific wins) | policy file | — |
| `branch` | `route` | Classify each PR group; emit next action (create/submit/all_complete) | twig cache, git, gh | — |
| `branch` | `load-tree` | Discover hierarchy + PG groups + per-PG completion status | twig cache, git, gh | — |
| `branch` | `next-impl` | Pick the next implementable item in a PG; transition it to in-progress | twig cache | twig (state) |
| `branch` | `ensure-feature` | Idempotently create+push a feature branch if missing | git | git (push) |
| `branch` | `check-deps` | Check ADO predecessor links for blocking dependencies | twig | — |
| `branch` | `close-scope` | Close all non-terminal items in a PG scope to their target state | twig cache | twig (state) |
| `pr` | `create-feature-pr` | Create the feature PR; reuse existing open PR for same head/base | git, gh | gh (PR create) |

Three of these verbs (`plan seed-children`, `branch next-impl`, `branch close-scope`,
`branch ensure-feature`, `pr create-feature-pr`) write through subordinate
CLIs (twig, git, gh). The pure read+decision verbs are everything else.

### Exit code conventions

```text
0  Success            — JSON output is valid; happy path
1  RoutingFailure     — invalid lifecycle event / illegal transition / runtime failure
2  ConfigError        — config missing, malformed, or invalid
3  CacheError         — twig cache inaccessible or work item not found
4  HealthCheckFailed  — diagnostic check reported a critical failure
```
Source: `src/Polyphony/ExitCodes.cs`.

There is a deliberate split between two exit conventions:

- **Decision verbs** that return `0` even on a "negative" answer
  (`branch route` returns 0 when `Action="error"`; `plan depth-guard` returns 0
  when `Allowed=false`). The workflow routes on the JSON payload, never on
  the exit code. Marked with the `routing-script convention` doc-comment in
  `Commands/*.cs`.
- **Validation verbs** that follow the conventional shell convention
  (`validate` returns 1 for `is_valid: false`; `validate-config` returns 2 on
  errors). Useful as `if polyphony validate …` guards in scripts.

When in doubt, parse the JSON.

### Output contract

Every verb emits exactly one JSON document on stdout (no banner text, no log
lines). The contract is enforced by
`tests/Polyphony.Tests/Commands/JsonOutputContractTests.cs`:

- `snake_case` property names
  (`PolyphonyJsonContext.cs:13` — `PropertyNamingPolicy = SnakeCaseLower`).
- Null fields are omitted
  (`PolyphonyJsonContext.cs:14` — `DefaultIgnoreCondition = WhenWritingNull`).
- Errors use `{"error":"…", "work_item_id": N}` with a non-zero exit (or
  `error: "…"` as a field on the result object for routing-style verbs).

This is the contract that lets the workflow YAMLs do `(polyphony … |
ConvertFrom-Json)` and trust the property names.

---

## 3 · Routing — "where in the lifecycle is this work item?"

### Conceptual model

"Routing" in polyphony means **deciding which next step the workflow should
take** for a given work item. The current model is:

- **Requirement-set routing** (`polyphony state next-ready`): Given a work
  item ID, derive its requirement set from the type's facets, reduce against
  observable state (plan / seeds / implementation), and return the
  dispatchable requirements. The apex driver routes on these.

- **PR-group routing** (`polyphony branch route`): Given a hierarchy of work
  items already partitioned into PR groups (PGs) via `PG-N` tags, which PG
  needs work next, and what action does it need (create branch / submit PR /
  all complete)? This is the decision the implementation sub-workflow
  (`implement-merge-group.yaml`) makes per PG.

Both share a key design rule: **routing is computed from `StateCategory`
plus `facets`, never from a hardcoded type name.** Polyphony does not
have an `if (typeName == "Epic")` anywhere — it reads the type's facets
from `process-config.yaml` and decides on those. This is what makes the system
type-agnostic across Basic / Agile / Scrum / CMMI process templates and
across whatever custom hierarchy a repo defines.

### `polyphony branch route`

```text
polyphony branch route --work-item <id> [--pg-number <n>]
```

PG-level routing. Walks the hierarchy under the root (depth 3), groups
implementable children by their `PG-N` tag, then for each PG decides:

| Action | Meaning |
|---|---|
| `create_branch` | PG branch does not exist on remote; need to create + start work |
| `submit_pr` | All work done; PR not yet created |
| `all_complete` | PG has a merged PR (or all items terminal); skip |

`--pg-number` lets parallel `for_each` callers pick a specific PG instead of
"first non-completed", which is critical when 3 PGs run in parallel.

The decision logic in `BranchCommands.Route.cs:203-265` is the heart of the
implementation phase. Notable: it has explicit *stale branch* defense — a
merged PR with all containers still in the `Proposed` category is treated
as a leftover from a prior failed run and gets `create_branch` instead of
`all_complete`. This is the kind of subtle ratchet that justified moving
this logic out of PowerShell.

JSON shape includes `current_pg`, `branch_name`, `work_item_ids` (containers
like Issues), `child_ids` (implementables like Tasks), `pr_number`, `pr_url`,
`completed_pgs`, `remaining_pgs`, `total_pgs`, `ado_workspace`.

---

## 4 · Validation — "is this state change legal?"

### Conceptual model

Validation in polyphony has three distinct flavors:

1. **Lifecycle event validation** (`polyphony validate`) — *can this item
   transition on this event right now?* Returns `is_valid` and, when valid,
   the target state name. This is the established channel for deriving state
   names from events; nothing else should hardcode `"Done"` or `"Doing"`.

2. **Process-config schema validation** (`polyphony validate-config`) —
   *is `process-config.yaml` well-formed and complete?* Runs 14 rules
   (V-1..V-14). V-1..V-8 are errors (block execution); V-9..V-14 are warnings
   for missing companion files under `.polyphony-config/`.

3. **Policy schema validation** (`polyphony policy validate`) — *is
   `policy.yaml` well-formed?* Runs without applying built-in defaults so
   the operator can opt into defaults explicitly. See § 7 for policy semantics.

### `polyphony validate`

```text
polyphony validate --work-item <id> --event <name> [--config <path>]
```

`<event>` is a lifecycle event name declared in `transitions:` for the item's
type. The engine recognizes preconditions for four well-known events
(`TransitionValidator.cs:66-73`):

| Event | Precondition |
|---|---|
| `begin_planning` | Item must be in `Proposed` category |
| `begin_implementation` | Item must be in `Proposed` or `InProgress` |
| `implementation_complete` | Item must be in `InProgress` |
| `all_children_complete` | Every child must be `Completed` |

Any other event name is accepted as a name lookup against
`transitions[type][event]` with no precondition check. If the event isn't in
the type's transitions, `is_valid: false` with an unknown-event message.

JSON shape (`src/Polyphony/Models/ValidateResult.cs`):

```jsonc
{
  "work_item_id": 2647,
  "event":        "implementation_complete",
  "is_valid":     true,
  "target_state": "Done",
  "message":      "Transition 'implementation_complete' is valid. Target state: 'Done'."
}
```

Exit codes: `0` valid, `1` invalid (unknown event/type or precondition fail),
`3` work item not found.

**The canonical pattern** in `branch close-scope` and similar:

```powershell
$validate = (polyphony validate --work-item $id --event implementation_complete) | ConvertFrom-Json
if ($validate.is_valid) {
    twig set $id --output json | Out-Null
    twig state $validate.target_state --output json | Out-Null
}
```

The state passed to `twig state` is **always** `$validate.target_state` —
**never** a hardcoded string literal. This is the seam that makes the SDLC
work unchanged on Basic, Agile, Scrum, or CMMI process templates.

### `polyphony validate-config`

```text
polyphony validate-config [--config <dir>] [--output json|human]
```

`--config` is a *directory*; the command appends `process-config.yaml`
(`ValidateConfigCommand.cs:22`). 14 rules are evaluated:

| Range | Severity | What it checks |
|---|---|---|
| V-1..V-8 | error | schema_version, type list, facets, transitions, branch_strategy syntax, etc. |
| V-9..V-14 | warning | companion files exist (`work-item-types/<slug>.md`, templates, `agent-guidance/*.md`, `profile.yaml`) |

Returns `0` on valid (warnings allowed) or `2` on any error.

**Use when:** CI / preflight, before any workflow runs. The root
`state preflight` does *not* call `validate-config` directly — that's a
separate concern that runs earlier in CI or as a one-shot before pushing
config changes.

### `polyphony policy validate`

```text
polyphony policy validate [--path <file>]
```

Same shape as `validate-config` but for `policy.yaml`. Surfaces missing
required fields and unknown enum values as warnings/errors *without applying
defaults* — so the operator can see exactly what they wrote, not what
defaults will paper over. Returns `0` on valid, `2` on errors.

---

## 5 · Hierarchy — "what's in this tree?"

### Conceptual model

Hierarchy is the *tree-walking* primitive. Multiple verbs lean on it:

- `polyphony hierarchy` is the raw walker — give it a root, get a tree with
  facets annotated on each node.
- `polyphony plan next-child` filters the immediate children to the
  `plannable` ones.
- `polyphony branch route` and `polyphony branch load-tree` walk the tree,
  group items by `PG-N` tag, and decide PG actions.
- `polyphony branch close-scope` walks the tree to find which items in a PG
  need closing.

The shared engine is `HierarchyWalker` (`src/Polyphony/Routing/HierarchyWalker.cs`).
It reads from `IWorkItemRepository` (twig SQLite cache), and for each node it
annotates `facets` from the type's entry in `process-config.yaml`.
**Facets are the universal classifier** — every routing decision is
written against `Facets.Contains("plannable")` /
`Facets.Contains("implementable")`, never against a type name.

### `polyphony hierarchy`

```text
polyphony hierarchy --work-item <id> [--depth <n>] [--config <path>]
```

`--depth 0` returns only the root node. Default is 3.

JSON shape (`src/Polyphony/Models/HierarchyResult.cs`); each node has the same
shape (`children` is always an array — `HierarchyCommand.cs:38-48` normalizes
nulls):

```jsonc
{
  "work_item_id": 1234,
  "title":        "Some Epic",
  "type":         "Epic",
  "facets": ["plannable"],
  "state":        "Doing",
  "tags":         "PG-1; release-blocker",
  "children": [
    { "work_item_id": 1235, "title": "…", "type": "Issue",
      "facets": ["plannable","implementable"],
      "state": "To Do", "children": [] }
  ]
}
```

**Use when:** A script needs to flatten + filter by facet or tag without
re-implementing the walker. Typical pattern:

```powershell
$tree = (polyphony hierarchy --work-item $rootId --depth 3) | ConvertFrom-Json
function Flatten($n) {
    @($n) + ($n.children | ForEach-Object { Flatten $_ })
}
$implementable = Flatten $tree |
    Where-Object { $_.facets -contains 'implementable' }
```

**Don't use when:** You need ADO field metadata beyond `state` and `tags`
(e.g. `System.AssignedTo`, full description) — those aren't in the output.
Fall back to `twig show`.

### `polyphony plan next-child`

```text
polyphony plan next-child --work-item <id>
```

A degenerate one-level case of `hierarchy` filtered to `plannable` children.
Used to drive the `for_each` recursion in `plan-level.yaml`. Always exits 0;
emits an empty array if there are no plannable children. The workflow routes
on `has_plannable_children`.

---

## 6 · State — "is the environment ready, and what stage are we at?"

### Conceptual model

The `state` group covers two related concerns:

1. **Preflight** — *before we start, is everything wired up?* Two flavors:
   `state preflight` (full) and `state preflight-lite` (3 checks, used by
   sub-workflows that re-enter mid-run).
2. **Next-ready dispatch** — *which requirements are dispatchable right now?*
   `state next-ready` derives the requirement set from the item's facets,
   reduces against observable plan/seed/implementation signals, and returns
   the per-disposition arrays the apex driver routes on.

Both follow the **routing-style exit convention**: always exit 0; route on the
JSON payload's `ready` (preflight) or per-requirement disposition (next-ready)
fields. Errors are reported via the payload, not via process exit code, so the
calling workflow can route to a human gate even on environment failures.

### `polyphony state preflight`

```text
polyphony state preflight --work-item <id>
```

Runs 4 required + 3 advisory checks:

| Required | What it checks |
|---|---|
| `git_repo` | Currently inside a git repo |
| `twig_cli` | `twig --version` succeeds |
| `twig_config` | `twig config get organization` and `project` both return values |
| `ado_access` | `twig show <id>` succeeds (proves connectivity + access) |

| Advisory | What it checks |
|---|---|
| `gh_auth` | `gh auth status` reports authenticated |
| `polyphony_cli` | (Always passes — we ARE polyphony; reads our own assembly version) |
| `dotnet_sdk` | `dotnet --version` succeeds |

`ready` is true iff all required checks pass. Advisory checks contribute to
`warning_count` but never block.

### `polyphony state preflight-lite`

```text
polyphony state preflight-lite
```

Three checks only: `git_repo`, `twig_cli`, `polyphony_cli`. No work item
needed. Used by re-entering sub-workflows that already passed full preflight
on the root run.

---

## 7 · Policy — "what's the configured behavior for this scope?"

### Conceptual model

`policy.yaml` (optional file at `.polyphony-config/policy.yaml`) is the SDLC's
**adjustable behavior layer** — caps, modes, severity thresholds. It exists
because some workflow behaviors should vary by scope (per-type, per-root) but
shouldn't be hardcoded in the YAML. Examples:

- How many revision cycles before a reviewer's `changes_requested` is forced
  through? (`approvals.defaults.max_revision_cycles`)
- How many fix loops before a PR-fixer agent escalates to a human gate?
  (`pr.defaults.max_fix_loops`)
- How many remediation cycles on a feature PR before the cap gate fires?
  (`pr.defaults.max_remediation_cycles`)
- How many rounds of open-questions can the architect surface before forcing
  a decision? (`open_questions.defaults.max_question_loops`)
- How many merge groups can run in parallel? (`concurrency.max_concurrent_children`)

Three verbs implement load-/validate-/resolve:

### `polyphony policy load`

```text
polyphony policy load [--path <file>]
```

Loads `policy.yaml` (or `--path`) and returns a snapshot of the resolved
configuration with built-in defaults applied. When the file doesn't exist,
returns a defaults-only snapshot with `used_defaults: true`. This is the
verb the root workflow calls once at the top to bake the policy into the run.

### `polyphony policy validate`

See § 4. Schema-only check; doesn't apply defaults.

### `polyphony policy resolve`

```text
polyphony policy resolve --scope <token> --domain <token> [--path <file>]
```

Returns the *effective* rule for a given scope within a given domain. Layers
the policy file most-specific-wins: `root > type:<Name> > default`.

`--scope` is one of `root`, `default`, or `type:<TypeName>`.
`--domain` is `approvals`, `pr`, or `open_questions`.

The returned `mode` field is the workflow's lever — `enforce` blocks on
violations, `warning` surfaces them but proceeds, `silent` ignores. Caller
emits a route based on `mode`.

This is the verb that lets a workflow ask "for *this* particular work-item
type, in *this* particular policy domain, what should I do?" and get a
single-record answer.

---

## 8 · Planning verbs — "support the architect agent"

### Conceptual model

The planning sub-workflow (`plan-level.yaml`) interleaves an LLM architect
agent with deterministic helper verbs that:

- Enforce recursion budget (`plan depth-guard`)
- Discover what to recurse into next (`plan next-child`)
- Load type-specific context for the architect (`plan load-type`)
- Load reusable agent guidance (`plan load-guidance`)
- Score the architect's plan via two reviewer agents and decide loop-or-proceed
  (`plan review`)
- Idempotently materialize the architect's task list as ADO children
  (`plan seed-children`)

These verbs were originally PowerShell scripts. Migrating them to the CLI
gave them unit tests, deterministic JSON contracts, and a single typed
contract surface for the workflow YAML — but the *logic* hasn't fundamentally
changed. They are the most "thin shell" verbs in the CLI and the most
defensible target for the question "could this just be a script?". The answer
is *yes* — but having them in C# means one contract surface for the workflow
YAML, which is what justifies the cost.

### `polyphony plan depth-guard`

```text
polyphony plan depth-guard --depth <int> [--max-depth <int>]
```

Pure arithmetic — returns `allowed: depth < maxDepth`. Always exits 0; the
workflow routes on `allowed`. Default `maxDepth=6` matches the documented
recursion budget in the polyphony-sdlc skill.

### `polyphony plan next-child`

See § 5.

### `polyphony plan load-type`

```text
polyphony plan load-type --work-item <id> [--config-dir <dir>]
```

Reads the work item's type, computes the slug
(`typeName.ToLowerInvariant().Replace(' ', '-')`), and loads three files:

- `<config-dir>/work-item-types/<slug>.md` (definition — required)
- `<config-dir>/work-item-types/templates/<slug>-template.md` (template — optional)
- `processConfig.Types[typeName].DecompositionGuidance` (inline in YAML — optional)

Returns them as a single JSON record for the architect agent's prompt
template. Returns non-zero on missing definition (workflow routes to type-loader
error gate); returns 0 with empty `template` on missing template (graceful
degradation).

### `polyphony plan load-guidance`

```text
polyphony plan load-guidance [--config-dir <dir>]
```

Reads every `.md` file under `<config-dir>/agent-guidance/`, returns a
JSON object keyed by basename (extension stripped) with file contents as
values. Returns `{}` when the directory doesn't exist — graceful degradation
for repos without agent guidance configured.

### `polyphony plan review`

```text
polyphony plan review --tech-reviewer-json <json> --readability-reviewer-json <json> --prior-cycle-count <int> [--max-cycles <int>]
```

Aggregates two reviewer agents' JSON outputs (each with `score` and
`blocking_issues` fields) and decides whether the planning loop should
re-invoke the architect or proceed to the human plan-approval gate.

Pass criteria (any one wins, in priority order):

1. `average_score >= 90`
2. `blocking_issue_count == 0`
3. `prior_cycle_count >= max-cycles` (`forced_by_cap=true` to escape oscillation)

Default `max-cycles=5`. The verb always exits 0; the workflow routes on
`passed` and `forced_by_cap`.

### `polyphony plan seed-children`

```text
polyphony plan seed-children --work-item <id> --children-json <json> [--planned-tag <tag>]
```

The most consequential planning verb. Idempotently reconciles an
architect-emitted child list (`architect.output.children`) against the existing
children of a parent work item.

Match precedence per child entry:

1. **Marker match** — existing child whose description contains
   `<!-- polyphony:plan-child-id={id} -->` matching the architect's id → reused
   (no create).
2. **Title+type match** — existing child with the same `(title, type)` under
   the parent → reused with a warning (marker damaged or missing).
3. **No match** — created via `twig new` with the marker embedded as the
   last line of the description.

On zero errors, merges the planned tag (default `polyphony:planned`) into
the parent's `System.Tags`. `PhaseDetector` reads this tag to recognize the
"already planned" state without consulting `process-config.yaml`
(`PhaseDetector.cs:60-67`) — this is what stops the root workflow from
re-routing into planning forever after it succeeds.

---

## 9 · Branch lifecycle verbs — "manage PG branches and dependencies"

### Conceptual model

Five verbs that own the lifecycle of a PR group's branch:

| Verb | Stage | Responsibility |
|---|---|---|
| `branch route` | classify | Decide next action across all PGs |
| `branch load-tree` | overview | Discover PGs + their completion status |
| `branch ensure-feature` | start | Create feature branch if missing |
| `branch next-impl` | execute | Pick next implementable item; transition to in-progress |
| `branch check-deps` | guard | Block work if predecessor links aren't terminal |
| `branch close-scope` | finish | Close all items in a PG to their target state after merge |

Together they replace what was a sprawling set of PowerShell scripts
(`pg-router.ps1`, `load-work-tree.ps1`, `impl-router.ps1`, `dependency-check.ps1`,
`scope-closer.ps1`). The migration gave each of them:

- A unit-tested JSON contract.
- Cross-platform behavior (the PowerShell versions had Windows-isms).
- Type-agnostic terminal-state detection via `Twig.Domain.StateCategoryResolver`
  (replacing hardcoded `{Done, Removed, Closed}` lists).

### `polyphony branch route`

See § 3.

### `polyphony branch load-tree`

```text
polyphony branch load-tree --work-item <id>
```

Read-only sibling of `branch route`. Returns a structured tree-of-PGs view
(rather than "next action"), useful for dashboards / overview screens. Calls
`twig sync` first so the view is fresh.

### `polyphony branch ensure-feature`

```text
polyphony branch ensure-feature --branch <name> [--base-branch <name>] [--remote <name>]
```

Idempotent: if the branch exists locally, checks it out; if it exists on
remote but not locally, fetches and checks out; if it exists nowhere, creates
from `--base-branch` (default `main`) and pushes. Default remote is `origin`.

The apex driver calls `branch ensure-feature` once after state detection.
Sub-workflows trust the branch name as an input rather than re-running the check.

### `polyphony branch next-impl`

```text
polyphony branch next-impl --work-item <id> --pg-name <PG-N>
                              # (or --pg-number <int>)
```

Picks the next non-terminal implementable item in the named PG, transitions
it via `begin_implementation` (validating against the process config), and
emits the branch name + workspace metadata the workflow needs to start
work on it.

This is one of the few verbs that **writes** (it sets the item's state). The
write goes through `twig state`, not direct ADO REST.

### `polyphony branch check-deps`

```text
polyphony branch check-deps --work-item <id>
```

Reads the work item's `relations` and finds every predecessor link
(`System.LinkTypes.Dependency-Reverse` or `attributes.name == "Predecessor"`).
For each predecessor, checks whether its state's *category* (not name) is
`Completed` or `Removed`. Returns `blocked: true` with the list of blockers
if any aren't terminal.

The terminal check uses `Twig.Domain.StateCategoryResolver` — so it correctly
recognizes `Closed`, `Done`, `Resolved` (depending on template) without
hardcoding.

### `polyphony branch close-scope`

```text
polyphony branch close-scope --work-item <id> --pg-name <PG-N> [--pr-number <int>]
                                # (or --pg-number <int>)
```

Walks the hierarchy under the root, filters to the given PG by tag, finds
non-terminal items, and for each one calls `polyphony validate
--event implementation_complete` to derive the target state, then issues
`twig state <target>`. Returns `closed_items` (succeeded) and `failed_closures`
(per-item reasons for failure).

This is the verb that runs *after* a PG's PR has merged, to reflect the merge
back into ADO. The pre-CLI version was the source of multiple
hardcoded-`Done` bugs in different process templates — moving it here let us
delete the literal strings.

---

## 10 · PR lifecycle — "create the feature PR"

### Conceptual model

Only one verb in this group today: `pr create-feature-pr`. The PG-level PR
creation lives inside the implementation sub-workflow as an inline `gh pr
create` call (the `pr_submit` agent in `implement-merge-group.yaml`); the *feature*
PR (the umbrella PR for an entire epic, post-PG-merge) gets a dedicated
verb because it has more complex logic (idempotent reuse of existing PRs).

The PR review/fix/merge lifecycle is **not** in the CLI — it's a workflow
concern in `github-pr.yaml` (Opus reviewer + Sonnet fixer loop) or
`ado-pr.yaml` (stub). The CLI's job ends after PR creation.

### `polyphony pr create-feature-pr`

```text
polyphony pr create-feature-pr --work-item <id> --feature-branch <name> --target-branch <name> [--title <text>]
```

Steps:

1. (Best-effort) consistency-check against polyphony's own `workspace_hint`
   feature branch; warn on mismatch.
2. Verify the feature branch exists on the remote (otherwise fail with
   `RoutingFailure`).
3. Resolve the GitHub repo slug from the `origin` remote URL.
4. Resolve the PR title — use `--title` if provided; else derive from the
   work item's title via `twig show-tree`.
5. Build a PR body that includes the work-item hierarchy as a JSON code block.
6. **Idempotency check** — list open PRs for the same head/base pair; if one
   exists, return `created: false` with the existing PR's number/URL.
7. Otherwise call `gh pr create`, parse the URL, return `created: true`.

The idempotency check exists because the workflow may re-enter this verb
after a partial failure, and creating a duplicate PR is unrecoverable
without manual cleanup. The check was added during the AB#3009 dogfood
(see PR #41) after the lack of it caused a confusing duplicate-PR scenario.

---

## 11 · Diagnostics — "is everything wired up?"

### `polyphony health`

```text
polyphony health [--config <path>]
```

A standalone diagnostic, not part of any workflow. Checks:

| Check | Critical? |
|---|---|
| `process-config` loads | yes |
| `twig` is on PATH and responsive | yes |
| `git` is on PATH and responsive | yes |
| `dotnet` runtime ≥ 7.0 | yes |
| AOT support (informational) | no |
| SQLite available + WAL mode | yes |
| YamlDotNet basic parse OK | yes |

Plus environment metadata: OS, architecture, dotnet version, polyphony version,
and the canonical SDLC entry-point reference (`canonical_workflow`, currently
`apex-driver@polyphony`). The same reference is also written to STDERR as a
one-line breadcrumb (`Canonical SDLC entry point: conductor run
apex-driver@polyphony --input apex_id=<ID>`) so first-time users see the entry
without parsing the JSON.

Exits `0` if all critical checks pass, `4` (`HealthCheckFailed`) otherwise.

**Use when:** Troubleshooting a fresh setup. New repos with missing
dependencies surface here cleanly.

---

## 12 · How much value is the CLI actually adding?

Honest answer: **enough to justify existing, but not uniformly.**

### What's load-bearing

Five verbs do work that would be genuinely hard to do correctly anywhere
else:

| Verb | Why it justifies a typed binary |
|---|---|
| `state next-ready` | Type-agnostic dispatchability over facet-derived requirement sets. The reduction logic over `StateCategory` × `facets` × observable signals needs the `Twig.Domain` resolvers — and PowerShell calling into .NET assemblies is awkward enough that owning the call here pays for itself. |
| `validate` | Returns `target_state` so PowerShell never types `"Done"` literally. This single field is the seam that makes the SDLC process-template-agnostic. |
| `validate-config` | 14 schema rules are far easier to author and test as C# than as a YAML linter. |
| `branch route` | Stale-PR defense, parallel-PG dispatch via `--pg-number`, type-agnostic terminal-state checks via `StateCategoryResolver` — three subtle ratchets that previously bit us in the PowerShell version. |
| `policy resolve` | Most-specific-wins layering with deterministic fallback. The code is short but a handful of edge cases (missing root, defaults applied vs. not, scope token parsing) earn their unit tests. |

### What's medium-value

`state preflight`, `plan seed-children`, `branch close-scope`, `pr
create-feature-pr` are non-trivial enough that owning them in C# is fine —
they have meaningful state machines, idempotency requirements, or external
calls that benefit from typed clients (`IGhClient`, `IGitClient`, `ITwigClient`).
Could they live in PowerShell? Probably. Are they better in C#? Probably,
but the marginal value over a tested PowerShell script is small.

### What's thin shell

`plan depth-guard`, `plan load-guidance`, `branch ensure-feature`,
`plan next-child`, and arguably `plan load-type` are very close to "PowerShell
with a JSON output contract". Their justification is **uniformity** — once
the workflow YAML talks to one binary with one JSON contract and one DI
container, adding a verb is much cheaper than maintaining a pwsh script with
its own argument-parsing, JSON-emitting, and exit-code handling.

The cost of these thin verbs is real: slow .NET cold-start (~150ms per
invocation), a `JsonOutputContractTests` suite that has to enumerate every
result type, and the overhead of a typed contract for what could be a plain
JSON-emitting script. The benefit is also real: when a verb's contract
changes, exactly one place changes — and the workflow's call site is
type-checked by the JSON contract test, not by hope.

### What the CLI *is not* adding

- **Workflow orchestration.** That's conductor + the YAMLs.
- **Agent intelligence.** That's the LLMs.
- **ADO writes.** That's twig.
- **PR review or merge.** That's the GitHub PR sub-workflow agents.
- **Recursive planning.** The recursion lives in `plan-level.yaml` via
  `for_each`; the CLI just enforces the budget (`plan depth-guard`).

### The strategic value

The strongest argument for the CLI isn't any individual verb — it's the
**contract surface**. Without it, the workflow YAMLs would have one of two
shapes:

1. PowerShell heredocs inside YAML (the pre-CLI anti-pattern). Untestable,
   unparseable by tools, prone to subtle quoting bugs.
2. LLM agents asked to compute things they shouldn't be computing
   (lifecycle phase detection, schema validation, PG classification). Slow,
   non-deterministic, expensive.

The CLI splits the SDLC into "things deterministic code should decide" and
"things an LLM should judge". Once you have that split, the question of
*which* deterministic verbs justify being in C# is much less interesting
than *that the split exists*. The CLI is the tangible expression of the split.

### When *not* to add a new verb

Before adding a fifth verb to a command group, ask:

1. Is the logic genuinely deterministic? (If it needs judgment, that's an
   agent's job.)
2. Does it need access to `Twig.Domain` resolvers? (If yes, definitely a
   verb.)
3. Will it be called more than once per run? (Cold-start cost matters.)
4. Could it equivalently be a PowerShell script in
   `.conductor/registry/scripts/`? (If yes, prefer the script unless the
   contract surface argument applies.)

If the answer to (1)–(3) is "yes" and (4) is "no", the CLI is the right
home. Otherwise, the script registry is where it belongs.

---

## Cross-references

- **Workflow suite documentation:** `.github/skills/polyphony-sdlc/SKILL.md`
  (which YAML calls which verb, recursion budget, agent roster).
- **Architecture and three-vocabulary rule:** `docs/polyphony-architecture.md`
  (event names vs. state names vs. state categories — the rule that
  underpins `validate`).
- **`process-config.yaml` schema:** `docs/polyphony-process-config-schema.md`
  (validated by `validate-config`).
- **`.polyphony-config/` directory layout:** `docs/polyphony-conductor-directory.md`
  (consumed by `plan load-type` and `plan load-guidance`).
- **Onboarding a fresh repo:** `docs/onboarding-guide.md` (which verbs to
  call when, with worked examples).
- **CLI authoring conventions:** `.github/skills/polyphony-cli-developer/SKILL.md`
  (when adding a new verb).


