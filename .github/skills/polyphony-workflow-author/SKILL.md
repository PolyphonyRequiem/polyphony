---
name: polyphony-workflow-author
description: >-
  Activate when authoring or modifying workflow YAMLs in `workflows/`, or PowerShell
  helper scripts in `scripts/`, or anything that orchestrates polyphony+twig in the
  v2 SDLC pipeline. Covers the shell-out idiom, the four polyphony CLI verbs, the
  three-vocabulary rule (events / state names / categories), the established helper
  scripts, and conventions for adding new ones.
user-invokable: false
---

# Polyphony Workflow Author Skill

For any work that lives in `workflows/*.yaml` or `scripts/*.ps1`. The single hardest
thing to get right in this codebase is keeping state names out of workflow logic — this
skill exists to prevent that class of failure.

If you are modifying CLI verbs in `src/Polyphony/Commands/`, use the
**polyphony-cli-developer** skill instead.

---

## The shell-out idiom

Every workflow → engine → execution call is a four-step chain:

```text
workflow node      →   PowerShell helper        →   polyphony <verb>      →   twig <verb>
(YAML route)           (.ps1, ConvertFrom-Json)     (JSON to stdout)          (writes ADO)
```

PowerShell scripts must:

1. Call `polyphony …` (or `twig … --output json`), capture stdout.
2. Pipe the result to `ConvertFrom-Json` immediately.
3. Pass any name-like value (state, branch, target_state) **back through** the structured
   output, never by typing a literal.
4. Emit a single `ConvertTo-Json` object on stdout for the caller.
5. Exit 0 on success, 1 on script error. Routing logic in workflow YAML branches on
   `output.<field>`, not on exit code (cf. `scripts/child-router.ps1:13` —
   "Always exits 0 (routing is condition-based, not exit-code-based)").

A canonical, correct example: `scripts/scope-closer.ps1:53-72`.

---

## Decision matrix: which CLI do I call?

| Intent                                                           | Use                                               | Source                          |
|------------------------------------------------------------------|---------------------------------------------------|---------------------------------|
| What state name should I pass to `twig state` for event X?       | `polyphony validate --work-item N --event X`      | `Commands/ValidateCommand.cs`   |
| Is this transition even legal right now?                         | `polyphony validate --work-item N --event X`      | `Commands/ValidateCommand.cs`   |
| Walk children: facets, states, tags                        | `polyphony hierarchy --work-item N --depth 3`     | `Commands/HierarchyCommand.cs`  |
| What's ready to work on next? (per-requirement disposition)      | `polyphony state next-ready --work-item N`        | `Commands/StateCommands.NextReady.cs` |
| Validate `.conductor/process-config.yaml` itself                 | `polyphony validate-config --config .conductor`   | `Commands/ValidateConfigCommand.cs` |
| Build the cross-item edge graph; surface conflicts               | `polyphony edges check --work-item N`             | `Commands/EdgesCommands.Check.cs` |
| Ensure an evidence branch exists (orphan or apex-scoped)         | `polyphony branch ensure-evidence-branch N`       | `Commands/BranchCommands.EnsureEvidenceBranch.cs` |
| Open or reuse the evidence PR against `feature/<apex>`           | `polyphony pr open-evidence-pr N`                 | `Commands/PrCommands.OpenEvidencePr.cs` |
| Extract per-item guidance per the resolved policy                | `polyphony guidance extract N`                    | `Commands/GuidanceCommands.cs` |
| Set the active work item context                                 | `twig set <id> --output json`                     | twig CLI                        |
| Change state                                                     | `twig state <name> --output json`                 | twig CLI                        |
| Add a comment to a work item                                     | `twig note --text "…"`                            | twig CLI                        |
| Refresh cache from ADO                                           | `twig sync --output json`                         | twig CLI                        |
| Read full work-item details (title, type, fields)                | `twig show <id> --output json` / `twig tree`      | twig CLI                        |
| Create a child work item                                         | `twig new --parent <id> --type <T> --title "…"`   | twig CLI                        |

Rule of thumb: if you find yourself wanting to invent a new polyphony verb, re-read
`polyphony-cli-reference.md`. The existing read verbs cover every read concern. Writes
all go through `twig`.

> **The Phase 6 + 7 additions** (`edges check`, `branch ensure-evidence-branch`,
> `pr open-evidence-pr`, `guidance extract`) are routing-style — same envelope
> contract as the four reads, exit-code-0-on-routing-failure included. See
> the **polyphony-sdlc** skill for what each verb does conceptually; the
> envelope-routing rules below apply uniformly.

---

## Routing-style verb envelope convention

Every Phase 5+ polyphony verb that participates in a workflow route — `route`,
`validate`, `edges check`, `branch ensure-evidence-branch`,
`pr open-evidence-pr`, `pr create-feature-ado`, `pr merge-feature-ado`,
`worklist build`, etc. — follows the **same envelope contract**:

1. **Always exits 0** (or `ExitCodes.Success`). Non-zero exit means the verb
   itself crashed (e.g. cache I/O failure), not that the routing decision was
   "no".
2. **Surfaces success or failure in the JSON envelope** via fields like
   `success`, `has_conflicts`, `error`, `error_code`. Workflows route on those
   fields, not on the exit code.
3. **Never throws to the workflow.** Programmer errors (empty inputs, unknown
   types) are caught and re-emitted as `error_code` values like
   `invalid_argument`, `work_item_not_found`, `type_unknown`,
   `derivation_failed`, `cache_error`.

Concrete example for `polyphony edges check`:

```yaml
- name: edges_check
  type: script
  args: [polyphony, edges, check, "--work-item", "{{ workflow.input.work_item_id }}"]
  output_map:
    has_conflicts: "$.has_conflicts"
    conflicts: "$.conflicts"
    error_code: "$.error_code"

- name: conflict_gate
  type: human_gate
  when: "{{ edges_check.output.has_conflicts == true }}"
  message: "Cross-item edge conflicts detected. Resolve before proceeding."
  options: [retry, abort]

# Downstream waves consume the graph only after the gate clears.
```

The same shape applies to evidence verbs (`success` + `branch` /
`pr_url` + `error` / `error_code`) and to `worklist build`. Always
gate on the envelope; never on the exit code.

---

## Worktree isolation for parallel agents

When multiple agents may run against the same checkout — sibling SDLC
runs, multiple coders inside an MG, retrofit work running alongside a
live PR — **default to a dedicated git worktree per agent** to avoid
stash/checkout collisions:

```powershell
git fetch origin
git worktree add -b sdlc/<task-name> ../polyphony-<task> origin/main
cd ../polyphony-<task>
```

This is the polyphony parallel-fleet convention now (5 of the 9 most
recent fleet PRs needed this pattern). The `<repo>-<task>` directory
sits as a sibling of the source-of-truth checkout; the source repo's
`.git` is shared, so worktrees are cheap to create and cheap to remove.

When the workflow exits cleanly:

```powershell
git worktree remove ../polyphony-<task>
```

The `git_repo` workflow metadata (the source-of-truth checkout) and
`cwd` (the worktree) are tracked separately for exactly this reason —
see the **polyphony-sdlc** skill's "Workflow Metadata" section.

---

## Root fallback gate

When you author a sub-workflow that operates on a `root_id` but is **also**
invokable on its own (i.e. the user can call it directly without first
running tree-walker), call `root-fallback-gate` as the very first node and
route on its envelope. The gate composes `polyphony policy load` with a
`human_gate` that fires only when the resolved `root_fallback.auto_decide`
is `prompt`. The other two policy values — `use_active_item` and `abort`
— skip the prompt and emit the deterministic terminal directly. The
output envelope is always:

```
{
  "root_id": "<id-or-empty>",
  "decision": "use_active_item" | "abort" | "auto_resolved",
  "auto_policy_applied": true | false
}
```

Consume the envelope from the parent workflow with `is defined` guards on
each terminal — never assume the user-prompted branch ran. The gate is
the single source-of-truth for fallback behavior; do **not** re-implement
the policy lookup inline. The policy schema lives at
`.conductor/policy.yaml` under the `root_fallback:` key, and is validated
by `polyphony policy validate`.

---

## Apex driver (tree-walking dispatch)

The Phase 7 keystone is `apex-driver.yaml` — the tree-walking SDLC
orchestrator that ships with three companion deterministic dispatch
helpers. When authoring or modifying anything in this area, the rules
of this skill all apply, plus a few specifics:

**Three-file split.** Conductor's `for_each` invokes one thing per
iteration, so wave-handling and item-handling each need their own
sub-workflow:

- `apex-driver.yaml` — outer loop (`build_worklist` →
  `wave_dispatch_loop` for_each → `apex_completion_gate`).
- `apex-wave-dispatch.yaml` — per-wave fan-out (`dispatch_items`
  for_each → `integrate_wave` script).
- `apex-item-dispatch.yaml` — per-item pipeline (classify → spawn
  worktree → lifecycle dispatch → teardown worktree).

**Deterministic dispatch helpers.** The three companion scripts under
`.conductor/registry/scripts/` follow the same routing-style envelope
pattern as `route-actionable-executor.ps1` (always exit 0; surface
failure via `error_code` / `error_message`):

- `lifecycle-router.ps1` — wraps `polyphony state next-ready` and
  classifies each item into `plan-level | actionable | implement-pg
  | feature-pr | fast-path | monitoring | blocked | error`. The
  workflow YAML reads `classify_lifecycle.output.route` and
  dispatches; the YAML stays trivial because the classification
  rules live in the script (testable, observable).
- `worktree-manager.ps1` — spawns / tears down per-item git
  worktrees idempotently. Spawn returns success when the worktree
  already exists; teardown returns success when the path is already
  gone. This idempotence is what makes re-entry safe.
- `wave-integrator.ps1` — merges per-item branches into the apex
  feature branch in topological order from `polyphony edges check`.
  `--no-ff` by default. Captures conflicts per branch and continues
  the wave; conflicts roll up to a wave-level human gate.

**Pattern to copy.** When you need to route across N divergent
downstream paths from a single inflection point, write a `route-X.ps1`
that emits a `{route, ...}` envelope and let the YAML be a thin
switch on `output.route`. Don't try to express the classification as
Jinja conditions in route blocks — those don't unit-test.

**Re-entry.** The dispatch loop variable is the worklist itself,
recomputed via `polyphony worklist build` every iteration — never
persist a "last completed item" pointer. After a gate or a restart,
re-build the worklist; the EdgeGraph re-classifies what's still
pending and the next wave is whatever's ready *now*.

The full design rationale lives in
`docs/decisions/apex-driver.md`; the keyword set lives in the
`docs/glossary.md` "Apex driver" section.

---

## Composing facet profiles

The driver injects skills + MCPs onto an agent invocation by composing
the item's facet profiles together with the resolved execution mode and
extracted per-item guidance. The canonical compose pattern is:

```csharp
// 1. Inject mode-driven within-item edges into the derived requirement set.
var setWithMode = ExecutionModeInjector.Inject(
    derived.Set,
    resolved.ExecutionMode);

// 2. Compose the agent addendum: union facet profiles, append guidance.
var addendum = FacetProfileComposer.Compose(
    facets:           item.Facets,
    profiles:         processConfig.GetFacetProfiles(),
    perItemGuidance:  guidanceExtract.Guidance);

// 3. Hand `addendum.Skills`, `addendum.Mcps`, `addendum.GuidanceContext`
//    to the agent invocation prep.
```

The composition is deterministic: skills + MCPs sorted ordinal
ascending, identical-name collisions deduped silently, unknown facet
names omitted. See the **polyphony-sdlc** skill's "Facet Profiles and
Agent Addendum" section for the conceptual model and the
config-load-time validator (V-20).

> **Wiring status.** The composer + injector ship in PRs #128 / #129
> as standalone primitives. The driver / `worklist build` retrofit
> that calls them at agent-invocation prep time is the in-flight
> retrofit work — workflow YAMLs that reference facet profiles should
> assume the composition layer is available, not hand-roll the union
> themselves.

---

## Reviewing evidence PRs

The actionable workflow's evidence PR (head = `evidence/<apex>-<item>`,
base = `feature/<apex>`, opened via `polyphony pr open-evidence-pr`)
follows the **same lifecycle shape** as the plan PR and feature PR —
the rubric is the only thing that differs:

| Stage | Plan PR | Evidence PR | Feature PR |
|-------|---------|-------------|------------|
| Reviewer rubric | "Is this design sound?" | "Does this evidence justify the action?" | "Is this code change correct + integrated?" |
| Routing | `pr_platform_router` → GH or ADO | `pr_platform_router` → GH or ADO | `pr_platform_router` → GH or ADO |
| Fix loop | architect revision | actionable agent re-runs against the same branch | remediation cycle → new PG |
| Cap | `policy.approvals.max_revision_cycles` | shared with approvals cap | `policy.pr.max_remediation_cycles` |

Reuse the existing platform routing (`gh` for GitHub via
`github-pr.yaml`, `pr post-comment-ado` for ADO via `ado-pr.yaml`)
unchanged. The reviewer agent's prompt is the only thing that swaps —
either rubric-branch on the active item's facet inside the existing
`plan_reviewer`, or ship a sibling `evidence_reviewer` with its own
prompt. See `actionable.yaml` once it lands (Phase 6 PR #4 in flight).

---

## The three-vocabulary rule

Workflows speak EVENTS. The engine maps EVENT → STATE NAME via process-config. Twig
ultimately resolves NAME → CATEGORY for execution.

> **Never hardcode a state name in workflow YAML or in a helper script.** Always derive
> it via `polyphony validate` and pass `target_state` into `twig state`.

### Right pattern (cited)

`scripts/scope-closer.ps1:54-60`:

```powershell
# Validate transition before close (#2647)
$validateJson = polyphony validate --work-item $item.work_item_id --event implementation_complete 2>$null
$validateResult = $validateJson | ConvertFrom-Json

if ($validateResult.is_valid) {
    twig set $item.work_item_id --output json 2>$null | Out-Null
    twig state $validateResult.target_state --output json 2>$null | Out-Null
}
```

### Wrong patterns (anti-patterns to be replaced)

`workflows/implement-pg.yaml:370` — hardcoded `Done` in an inline pwsh `args` block:

```yaml
args:
  - "-NoProfile"
  - "-Command"
  - >-
    $taskId = {{ primary_router.output.primary_id }};
    twig set $taskId;
    twig note --text 'Item completed and approved by reviewer';
    twig state Done;            # ← hardcoded literal — replace with target_state
    @{ child_id = $taskId; completed = $true } | ConvertTo-Json
```

`scripts/impl-router.ps1:106` — hardcoded `Doing` in a helper script:

```powershell
# ── Transition to Doing ───────────────────────────────────────────────────
twig set $nextItem.work_item_id --output json 2>$null | Out-Null
twig state Doing --output json 2>$null | Out-Null     # ← hardcoded literal
```

The fix in both cases is the same shape as `scope-closer.ps1`: call
`polyphony validate --event implementation_complete` (for `Done`) or `--event
begin_implementation` (for `Doing`), then pass `target_state` into `twig state`. These
two literals are the only remaining hardcoded state names in the workflow surface; both
will fail at runtime against any non-Basic process template (e.g. Agile expects `Active`
not `Doing`).

---

## The canonical helper scripts

If you're about to write a new helper script, first check whether one of these is
already the reference for the pattern you need. Each is the single source of truth
for its idiom; copy from it rather than re-inventing.

| Script                            | What it is the canonical reference for                                              |
|-----------------------------------|--------------------------------------------------------------------------------------|
| `scripts/scope-closer.ps1`        | Validate-then-transition: `polyphony validate` → `twig state $target_state`. The reference for state-name handling. |
| `scripts/impl-router.ps1`         | Within-PG task selection via facet filtering and `polyphony hierarchy`. Note: contains a remaining `twig state Doing` literal at line 106 to be replaced. |
| `scripts/pg-router.ps1`           | PR group lifecycle: groups items by PG tag, checks remote branches and gh PR state, returns the next PG action. |
| `scripts/child-router.ps1`        | Plannable-child discovery for recursive planning (`plan-level.yaml`). The reference for facet-based filtering ("`_.facets -contains 'plannable'`"). |
| `scripts/feature-pr-creator.ps1`  | gh-PR creation against the resolved feature branch. The reference for branch-name validation in PR-creation flows. |
| `scripts/load-work-tree.ps1`      | Hierarchy → PG-grouped tree with completion status. The reference for `Group-ByPG` and PG enumeration. |

---

## Adding a new helper script

### File naming and location

- Place under `scripts/<verb>.ps1`. Verbs are kebab-case, action-first
  (`scope-closer`, `impl-router`, `pg-router`, `feature-pr-creator`).
- A companion `scripts/<verb>.Tests.ps1` is required (Pester). See e.g.
  `scripts/scope-closer.Tests.ps1`.
- Shared helpers live in `scripts/lib/` (`pg-helpers.ps1`, `ado-helpers.ps1`,
  `gh-helpers.ps1`). Dot-source them at the top of the script.

### Required script header

```powershell
<#
.SYNOPSIS
    One-line purpose.
.DESCRIPTION
    Multi-line description. Note which polyphony verbs and twig commands are called.
.PARAMETER WorkItemId
    ADO work item ID …
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$WorkItemId,
    # …
)
$ErrorActionPreference = 'Stop'
```

(Pattern from every existing script; e.g. `scripts/scope-closer.ps1:1-23`,
`scripts/pg-router.ps1:1-19`.)

### JSON contract with caller

- Emit **one** JSON object to stdout. Use `[ordered]@{ … } | ConvertTo-Json -Depth N`
  for stable key order (`scripts/scope-closer.ps1:75-83`).
- Field names: snake_case to match polyphony output and ADO conventions.
- For routing decisions, include a string field that workflow YAML can branch on
  (e.g. `action`, `status`, `phase`). Workflow YAML uses `when: "{{ <node>.output.<field> == '<value>' }}"`.

### Error handling

```powershell
try {
    # … main logic …
    [ordered]@{ … } | ConvertTo-Json -Depth 3
} catch {
    [ordered]@{ error = $_.Exception.Message } | ConvertTo-Json
    exit 1
}
```

(Cited shape: `scripts/scope-closer.ps1:84-87`, `scripts/load-work-tree.ps1:115-118`,
`scripts/pg-router.ps1:220-223`.)

### Exit code conventions

| Exit code | Meaning                                                              |
|-----------|----------------------------------------------------------------------|
| `0`       | Script completed successfully; routing is by `output.<field>` value. |
| `1`       | Script crashed (exception caught); `output.error` is populated.      |

Routing in workflow YAML is **almost always condition-based**, not exit-code-based.
See `scripts/child-router.ps1:13` for the explicit doc-comment of this convention.

### Polyphony output is stable; consume by field name

- `polyphony validate` → `is_valid`, `target_state`, `event`, `message`
  (`Models/ValidateResult.cs`).
- `polyphony hierarchy` → `work_item_id`, `title`, `type`, `state`, `facets`,
  `tags`, `children` (recursive; `Models/HierarchyResult.cs`).
- `polyphony state next-ready` → `status`, `requirements`, `next` (per-item
  requirement set with `(kind, disposition)` pairs; the `next` array is the
  ready set the workflow should dispatch on).

These fields are covered by `tests/Polyphony.Tests/Commands/JsonOutputContractTests.cs`,
so they will not change shape silently. If you find yourself reaching for a field that
isn't in the model, add it to the model + test (see polyphony-cli-developer skill),
don't paper over it in PowerShell.

---

## The min-polyphony-version rule

**Every workflow YAML MUST declare** the minimum polyphony CLI version
it requires:

```yaml
workflow:
  name: my-workflow
  version: "1.0.0"
  metadata:
    min_polyphony_version: "1.0.0"   # required, enforced by Pester lint
```

When you change a workflow to call a verb / flag / JSON field that didn't
exist in an earlier polyphony release, **bump
`min_polyphony_version` to the lowest CLI release that has the new
surface**. The bundled-SemVer model means this almost always equals the
YAML's own `workflow.version` after a release cut — but you bump
`min_polyphony_version` *with the change* (in the contract-bumping PR),
not at release time.

The check is enforced at runtime by `polyphony state preflight` /
`polyphony state preflight-lite` via the `--workflow-yaml "{{ workflow.file }}"`
flag. Wherever a workflow declares `min_polyphony_version`, its preflight
agent reads the YAML, parses the metadata, and compares against the
running CLI's version. On mismatch, preflight returns a failed check, the
gate routes to retry/abort, and there is no Proceed Anyway — silent
misroutes are exactly what this guard exists to prevent.

> **Why a flag, not a `{{ workflow.metadata.min_polyphony_version }}`
> template?** Conductor only exposes `workflow.input`, `workflow.dir`,
> `workflow.file`, `workflow.name` in template context. **`workflow.metadata`
> is NOT templatable.** The verb reads the YAML itself and parses the
> metadata. If you're tempted to interpolate metadata into a script's
> `args:`, re-read `m07-output-map-vs-schema.md`.

See [`docs/decisions/versioning-strategy.md`](../../../docs/decisions/versioning-strategy.md)
for the full rationale, the bundled-SemVer model, and the three-layer
truth (git tag · YAML self-description · `index.yaml` versions list).


