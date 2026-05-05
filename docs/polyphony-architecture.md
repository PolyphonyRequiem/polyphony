# Polyphony Architecture

This document describes the layering, the platform-abstraction seam, the three vocabularies
in play, and the data flow for a typical operation. Read this *before* changing any
workflow YAML, helper script, or CLI verb — most "where does X live?" questions are
answered here.

---

## Layering

From orchestration down to backing store:

```text
┌──────────────────────────────────────────────────────────────────────────┐
│ workflows/*.yaml          (conductor orchestration)                       │
│   declares NODES + ROUTES, dispatches to scripts and sub-workflows        │
└──────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ scripts/*.ps1             (composition + JSON parsing)                    │
│   shells out to polyphony + twig, parses JSON, threads results            │
└──────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ polyphony CLI verbs       (decisions + queries)                           │
│   route · validate · validate-config · hierarchy · health                 │
│   state {detect, preflight, preflight-lite}                               │
│   plan {depth-guard, next-child, load-type, load-guidance,                │
│          review, seed-children}                                           │
│   policy {load, validate, resolve}                                        │
│   branch {route, load-tree, ensure-feature, next-task,                    │
│           check-deps, close-scope}                                        │
│   pr {create-feature-pr}                                                  │
│   src/Polyphony/Commands/*.cs                                             │
│   ↳ For per-verb depth, see docs/polyphony-cli-reference.md               │
└──────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ Polyphony engine          (pure logic over WorkItem + ProcessConfig)      │
│   PhaseDetector · TransitionValidator · HierarchyWalker · ConfigValidator │
│   BranchNameResolver · PolicyResolver                                     │
│   src/Polyphony/Routing/*.cs · src/Polyphony/Configuration/*.cs           │
│   src/Polyphony/Policy/*.cs                                               │
└──────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ twig CLI                  (execution / writes)                            │
│   twig set · twig state · twig sync · twig note · twig show               │
└──────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ Twig libraries (ProjectReference, read-side)                              │
│   Twig.Domain (StateResolver, StateCategoryResolver, IWorkItemRepository) │
│   Twig.Infrastructure (SqliteCacheStore, SqliteWorkItemRepository)        │
└──────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ ADO REST API              (backing store for published items)             │
│   plus local .twig SQLite cache for reads (refreshed via twig sync)       │
└──────────────────────────────────────────────────────────────────────────┘
```

Polyphony's CLI references twig at the assembly boundary in two ways:

1. As a **library** for reads: `Twig.Domain` and `Twig.Infrastructure` are
   `ProjectReference`s. `PolyphonyServiceRegistration` calls
   `services.AddTwigCoreServices(twigDir: twigDir)` to wire `IWorkItemRepository` etc.
   (`src/Polyphony/Infrastructure/PolyphonyServiceRegistration.cs:33`).
2. As a **CLI** for writes: helper scripts shell out to `twig set …`, `twig state …`,
   `twig note …`, `twig sync …`. Polyphony itself never writes to ADO.

This separation is intentional: Polyphony decides *what should happen*; twig executes it.

---

## Where the platform abstraction lives

**The platform abstraction is a workflow-YAML construct, not a C# interface.**

There are no `IWorkItemPlatform`, `IProcessAdapter`, `IPlatformProvider`, `IPlatformGateway`
or similar interfaces in `src/Polyphony/`. (Verify: `git grep -l "IPlatform\|IProcessAdapter" src/Polyphony` returns nothing.)

The split happens entirely between workflow YAML files. The contract is enforced by
matching input/output schemas, not by a typed interface:

```text
              ┌──────────────────────────────────┐
              │ feature-pr.yaml / implement-pg   │
              │  (platform-agnostic orchestrator) │
              └────────────────┬─────────────────┘
                               │
                               ▼
                 ┌──────────────────────────┐
                 │   pr_platform_router     │   ← inline pwsh, no script file
                 │   (a script node that    │     (feature-pr.yaml:98-111
                 │    re-emits `platform`)  │      implement-pg.yaml:697-710)
                 └─────────┬────────────────┘
                           │
            when=='github' │  when=='ado'
                           ▼
        ┌──────────────────┴───────────────────┐
        │                                      │
        ▼                                      ▼
  workflows/github-pr.yaml              workflows/ado-pr.yaml
  (Opus reviewer + Sonnet fixer        (stub: structured error +
   loop + merger agent)                 human gate for manual PR)
```

Both sub-workflows accept the **same inputs** (`pr_number`, `branch_name`,
`target_branch`, `review_policy`, `platform`) and emit the **same outputs**
(`merged`, `pr_url`) — that interface contract is documented in the file headers
(`workflows/github-pr.yaml:1-16`, `workflows/ado-pr.yaml:1-15`). The dispatch is driven
by the `platform` input that ultimately originates from
`process-config.yaml`'s `platform: github | ado` field
(`src/Polyphony/Configuration/ProcessConfig.cs:11`).

If you go looking for a C# interface to "add a new platform", you are in the wrong layer.
Adding a platform means: (1) write a `<platform>-pr.yaml` matching the contract;
(2) add a `when` branch to `pr_platform_router` in `feature-pr.yaml` and `implement-pg.yaml`.

---

## The three vocabularies

Three distinct vocabularies cross paths in this codebase. Mixing them up is the single
biggest source of subtle bugs, including hardcoding state names and shipping configs
that fail at runtime against real ADO process templates.

### 1. Lifecycle event names (Polyphony-owned)

Examples: `begin_planning`, `begin_implementation`, `implementation_complete`,
`all_children_complete`, `scope_removed`.

- Owned by Polyphony.
- Declared in workflow logic (e.g. `polyphony validate --event implementation_complete`)
  and on the **left side** of `transitions:` entries in `process-config.yaml`.
- Four event names have hard-coded preconditions in
  `TransitionValidator.cs:66-73`: `all_children_complete`, `begin_planning`,
  `begin_implementation`, `implementation_complete`. Any other event name is accepted
  but has no precondition check.

### 2. State names (process-template-owned)

Examples (Basic): `To Do`, `Doing`, `Done`. Examples (Agile): `New`, `Active`,
`Resolved`, `Closed`, `Removed`.

- Owned by the ADO process template.
- Appear on the **right side** of `transitions:` entries (the values).
- Different per template — see
  `twig2/tests/Twig.TestKit/ProcessConfigBuilder.cs:48-96` for the canonical state sets:
  - **Basic:** `To Do`, `Doing`, `Done` (note: **no** `Removed`).
  - **Agile:** `New`, `Active`, `Resolved`, `Closed`, `Removed`.
  - **Scrum:** `New`, `Approved`, `Committed`, `Done`, `Removed` (PBI/Bug);
    `To Do`, `In Progress`, `Done`, `Removed` (Task).
  - **CMMI:** `Proposed`, `Active`, `Resolved`, `Closed`, `Removed`.
- Twig's write-side resolves names via `StateResolver.ResolveByName`
  (`twig2/src/Twig.Domain/ValueObjects/StateResolver.cs:30-60`) — exact match wins,
  otherwise unambiguous prefix. Unknown name → error of the form:
  `"Unknown state '<name>'. Valid states: <list>"`.

### 3. State categories (universal, twig-owned)

The `StateCategory` enum (`twig2/src/Twig.Domain/Enums/StateCategory.cs`):
`Proposed`, `InProgress`, `Resolved`, `Completed`, `Removed`, `Unknown`.

- Owned by twig.
- Universal across all process templates — all the engine's preconditions and routing
  decisions are written against categories, not names.
- Resolved from a state name via
  `StateCategoryResolver.Resolve(state, entries)` — prefers authoritative
  `StateEntry.Category` from ADO; falls back to a hardcoded heuristic on lowercased
  state names (`twig2/src/Twig.Domain/Services/Process/StateCategoryResolver.cs`):

  ```text
  "new"|"to do"|"proposed"          → Proposed
  "active"|"doing"|"committed"|
    "in progress"|"approved"        → InProgress
  "resolved"                        → Resolved
  "closed"|"done"                   → Completed
  "removed"                         → Removed
  ```

- Twig also exposes `StateResolver.ResolveByCategory(category, states)` for "give me
  the state name in this template that maps to category X"
  (`twig2/src/Twig.Domain/ValueObjects/StateResolver.cs:14-24`). This is the function
  to reach for if you want twig to auto-map something like
  `StateCategory.InProgress` → the right state name.

### The contract

Polyphony / workflows speak EVENTS. The engine maps EVENT → STATE NAME via process-config.
Twig ultimately resolves NAME → CATEGORY for execution, and *the engine itself routes on
CATEGORY*, not on the name.

```text
   workflow / script               polyphony engine                     twig
   ────────────────                ────────────────                    ─────
   "implementation_complete"  ──►  Transitions[type][event]            ChangeStateAsync(name)
        (event)                       │                                     │
                                      ▼                                     ▼
                                  "Done"                            StateResolver.ResolveByName
                                  (state name, returned             (validates name in template)
                                   as target_state)                       │
                                                                          ▼
                                                                  StateCategory.Completed
                                                                  (universal category)
```

The single visible JSON field that crosses the seam is `target_state` on
`ValidateResult` (`src/Polyphony/Models/ValidateResult.cs:9`).

---

## Why state names (not categories) on the right side of `transitions:`

`process-config.yaml`'s `transitions:` section uses **state names** as values, not
categories (`src/Polyphony/Configuration/ProcessConfig.cs:8` —
`Dictionary<string, Dictionary<string, string>>`, where the inner value is the literal
state name passed to `twig state`).

This is a deliberate decision: it lets `polyphony validate` return a `target_state` that
can be passed verbatim to `twig state`, with no second translation step. The cost is that
the config file is template-specific.

### The footgun: `scope_removed: Removed` against ADO Basic

Look at the canonical config (`.conductor/process-config.yaml:25, 30, 34`):

```yaml
transitions:
  Epic:
    scope_removed: Removed
  Issue:
    scope_removed: Removed
  Task:
    scope_removed: Removed
```

`Removed` is a **valid state in Agile, Scrum, and CMMI**, but **not in Basic**
(`twig2/tests/Twig.TestKit/ProcessConfigBuilder.cs:80-84` — Basic's state set is
exactly `To Do`, `Doing`, `Done`).

If a workflow ever issues `polyphony validate --event scope_removed --work-item …`
against a real ADO Basic project and then calls `twig state Removed`,
`StateResolver.ResolveByName` will fail with:

```text
Unknown state 'Removed'. Valid states: To Do, Doing, Done
```

It will not be a Polyphony-side validation failure (the event is in the transitions
table, so `polyphony validate` returns `is_valid: true, target_state: "Removed"`).
It will be a twig-side runtime failure when the state change is applied. The current
repo config gets away with this because no current workflow actually emits
`scope_removed` — but the line is a latent bug. See
`polyphony-process-config-schema.md` for a corrected per-template config.

---

## Data flow: completing a task

A worked example for the most common operation in the SDLC — closing an
`implementable` task as part of a PG.

1. **Workflow YAML** (`workflows/implement-pg.yaml`) routes to a node that runs the
   inline pwsh "transition completed task to Done" snippet. This snippet currently
   contains the anti-pattern `twig state Done` (line 370).

   ```yaml
   args:
     - "-NoProfile"
     - "-Command"
     - >-
       $taskId = {{ task_router.output.task_id }};
       twig set $taskId;
       twig note --text 'Task completed and approved by reviewer';
       twig state Done;       # ← hardcoded — violates the three-vocabulary rule
   ```

   The correct pattern (used by `scripts/scope-closer.ps1`) is to call `polyphony
   validate --event implementation_complete` first and pass `$validateResult.target_state`
   to `twig state`.

2. **Helper script** (`scripts/scope-closer.ps1:55-60`) does it right:

   ```powershell
   $validateJson = polyphony validate --work-item $item.work_item_id `
       --event implementation_complete 2>$null
   $validateResult = $validateJson | ConvertFrom-Json

   if ($validateResult.is_valid) {
       twig set $item.work_item_id --output json 2>$null | Out-Null
       twig state $validateResult.target_state --output json 2>$null | Out-Null
   }
   ```

3. **Polyphony CLI** (`src/Polyphony/Commands/ValidateCommand.cs:22-57`):
   - Reads the work item via `IWorkItemRepository.GetByIdAsync` (twig SQLite cache).
   - Reads its children.
   - Calls `TransitionValidator.Validate(item, "implementation_complete", children)`.
   - Serializes a `ValidateResult` with `is_valid` and `target_state`.

4. **Polyphony engine**
   (`src/Polyphony/Routing/TransitionValidator.cs:22-59, 123-134`):
   - Looks up `processConfig.Transitions["Task"]["implementation_complete"]` → `"Done"`.
   - Computes the precondition: `StateCategoryResolver.Resolve(item.State, null)` must
     be `InProgress`.
   - Returns `ValidTransition(workItemId, "implementation_complete", "Done", "…")`.

5. **Twig CLI** receives `twig state Done`. Twig's `StateResolver.ResolveByName`
   (`twig2/src/Twig.Domain/ValueObjects/StateResolver.cs:30-60`) validates `"Done"`
   against the type's `StateEntry` list (sourced from ADO).

6. **Twig write side** issues an ADO REST PATCH to update `System.State` to `Done`,
   via `IMutationProvider.ChangeStateAsync`
   (`twig2/src/Twig.Domain/Services/Mutation/IMutationProvider.cs:12`).

7. **ADO REST API** persists. Subsequent `twig sync` calls refresh the local SQLite
   cache.

The whole chain has exactly one place where a literal state name appears: the right side
of `transitions:` in `process-config.yaml`. Every script and workflow above gets the name
back via `target_state`, never by typing it.
