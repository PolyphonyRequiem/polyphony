# Polyphony CLI Reference

Single-page reference for every verb registered by the Polyphony CLI. The CLI is a thin
shell over the Polyphony engine (`PhaseDetector`, `TransitionValidator`, `HierarchyWalker`,
`ConfigValidator`) that reads from twig's local SQLite cache and emits AOT-friendly JSON.

The ground truth for "what verbs exist" is the `app.Add<…>()` block in `Program.cs`:

```text
src/Polyphony/Program.cs:18-21
  app.Add<RouteCommand>();
  app.Add<ValidateCommand>();
  app.Add<ValidateConfigCommand>();
  app.Add<HierarchyCommand>();
```

There are exactly **four** verbs. If you are tempted to invent a fifth (e.g.
`polyphony resolve-transition`, `polyphony next-state`), check this list first —
the answer is almost always already inside `route` or `validate` output.

---

## Verbs at a glance

| Verb              | One-line purpose                                                         | Reads twig cache | Exit codes        | Implementation                          |
|-------------------|--------------------------------------------------------------------------|------------------|-------------------|------------------------------------------|
| `route`           | Detect SDLC phase + recommended action for a work item                   | yes              | `0`, `3`          | `Commands/RouteCommand.cs`               |
| `validate`        | Validate a lifecycle event transition; **returns `target_state`**        | yes              | `0`, `1`, `3`     | `Commands/ValidateCommand.cs`            |
| `validate-config` | Validate `.conductor/process-config.yaml` against rules V-1..V-14        | no (config only) | `0`, `2`          | `Commands/ValidateConfigCommand.cs`      |
| `hierarchy`       | Walk the work-item tree; annotate each node with capabilities            | yes              | `0`, `3`          | `Commands/HierarchyCommand.cs`           |

Exit code constants (`src/Polyphony/ExitCodes.cs:8-29`):

```text
0 Success         — JSON output is valid
1 RoutingFailure  — invalid lifecycle event / illegal transition
2 ConfigError     — config missing, malformed, or invalid
3 CacheError      — twig cache inaccessible or work item not found
```

JSON output contract (enforced by `tests/Polyphony.Tests/Commands/JsonOutputContractTests.cs`):

- snake_case property names (`PolyphonyJsonContext.cs:13` — `PropertyNamingPolicy = SnakeCaseLower`).
- Null fields are omitted (`PolyphonyJsonContext.cs:14` — `DefaultIgnoreCondition = WhenWritingNull`).
- Errors use `{"error":"…","work_item_id":N}` and produce a non-zero exit (`RouteCommand.cs:28`,
  `ValidateCommand.cs:27`, `HierarchyCommand.cs:25`).

---

## `polyphony route`

### Synopsis

```text
polyphony route --work-item <id> [--config <path>]
```

| Flag           | Type | Default                                   | Source                       |
|----------------|------|-------------------------------------------|------------------------------|
| `--work-item`  | int  | (required)                                | `RouteCommand.cs:23`         |
| `--config`     | str  | `.conductor/process-config.yaml`          | `RouteCommand.cs:23`         |

### Purpose

Returns the SDLC phase the item is in (`needs_planning`, `needs_seeding`,
`ready_for_implementation`, `in_progress`, `ready_for_completion`, `done`, `removed`,
`unknown`) plus the recommended `action` (`plan`, `seed`, `implement`, `monitor`, `close`,
`none`). The decision is type-agnostic: it is computed from the item's `StateCategory` and
its type's `capabilities` (`PhaseDetector.cs:20-43`), never from a hardcoded type name.

### Output JSON shape

`RouteResult` (`src/Polyphony/Models/RouteResult.cs`):

```jsonc
{
  "work_item_id":    1234,
  "phase":           "ready_for_implementation",   // SdlcPhase constant
  "action":          "implement",                   // SdlcAction constant
  "message":         "All children of …",          // optional, often present
  "workspace_hint": {
    "feature_branch": "feature/1234-some-slug",    // null if branch_strategy absent
    "pg_branch":      "pg-{n}/1234-some-slug"
  }
}
```

`SdlcPhase` constants (`src/Polyphony/Routing/SdlcPhase.cs`):
`needs_planning` · `needs_seeding` · `ready_for_implementation` · `in_progress` ·
`ready_for_completion` · `done` · `removed` · `unknown`.

`SdlcAction` constants (`src/Polyphony/Routing/SdlcAction.cs`):
`plan` · `seed` · `implement` · `monitor` · `close` · `none`.

`workspace_hint` is populated only when `branch_strategy` is set in the config; templates
substitute `{id}`, `{root_id}`, `{slug}` (`BranchNameResolver.cs:34-43`).

### Exit codes

- `0` Success — JSON is valid.
- `3` CacheError — work item not found in the twig cache.

### Use when

- A workflow needs to decide which sub-workflow to dispatch to next (planning vs.
  implementation vs. close-out). See `scripts/detect-state.ps1:118-134` for the canonical
  consumption pattern.
- A workflow needs the workspace_hint (feature/PG branch names) without re-implementing
  templating. See `scripts/task-router.ps1:31-37`, `scripts/feature-pr-creator.ps1:34-43`.

### Don't use when

- You need to *change* a state — `route` is read-only. Use `validate` to learn the target
  state, then `twig state <name>` to apply it.
- You need rich work-item fields (title, type, full child tree) — `route` only returns
  `work_item_id`, `phase`, `action`, `message`, `workspace_hint`. Use `hierarchy` for that.

### Examples

Workflow context (script invocation, parsed JSON drives routing in `detect-state.ps1`):

```powershell
$routeJson = polyphony route --work-item $WorkItemId 2>$null
$routeResult = $routeJson | ConvertFrom-Json
$phase  = $routeResult.phase                 # e.g. "ready_for_implementation"
$action = $routeResult.action                # e.g. "implement"
$hint   = $routeResult.workspace_hint        # { feature_branch, pg_branch }
```

One-off (terminal):

```bash
polyphony route --work-item 2647
# {"work_item_id":2647,"phase":"in_progress","action":"monitor","message":"…","workspace_hint":{"feature_branch":"feature/2647-fix-routing"}}
```

---

## `polyphony validate`

### Synopsis

```text
polyphony validate --work-item <id> --event <name> [--config <path>]
```

| Flag           | Type | Default                                   | Source                          |
|----------------|------|-------------------------------------------|---------------------------------|
| `--work-item`  | int  | (required)                                | `ValidateCommand.cs:22`         |
| `--event`      | str  | (required)                                | `ValidateCommand.cs:22`         |
| `--config`     | str  | `.conductor/process-config.yaml`          | `ValidateCommand.cs:22`         |

`<event>` is a lifecycle event name declared in `transitions:` for the item's type. The
engine recognizes preconditions for four well-known events
(`TransitionValidator.cs:66-73`):

- `all_children_complete` — every child must be `Completed`.
- `begin_planning` — item must be in `Proposed` category.
- `begin_implementation` — item must be in `Proposed` or `InProgress`.
- `implementation_complete` — item must be in `InProgress`.

Any other event name is accepted as a name lookup against `transitions[type][event]` with
no precondition check.

**Note on live emission:** as of writing, the v2 SDLC workflow suite emits only two of
these four events — `begin_planning` (from `scripts/detect-state.ps1`) and
`implementation_complete` (from `scripts/scope-closer.ps1:55`). `begin_implementation` and
`all_children_complete` exist in the engine and in process-config templates but no live
script invokes them today. New workflows that need stricter precondition enforcement can
opt in by emitting these events.

### Purpose

Answer two questions in one call:

1. *Is this event legal for this item right now?* (`is_valid`)
2. *If so, what state name should we transition to?* (`target_state`)

`target_state` is the established channel by which workflows and helper scripts learn the
correct state name to pass to `twig state`. This eliminates hardcoded state names from
PowerShell and YAML.

### Output JSON shape

`ValidateResult` (`src/Polyphony/Models/ValidateResult.cs`):

```jsonc
{
  "work_item_id": 2647,
  "event":        "implementation_complete",
  "is_valid":     true,
  "target_state": "Done",                           // omitted when null
  "message":      "Transition 'implementation_complete' is valid. Target state: 'Done'."
}
```

For invalid transitions, `is_valid` is `false`, `target_state` may be present (when the
event/type is known but the precondition failed) or absent (when the event or type is
unknown). See `TransitionValidator.cs:29-44` (unknown type/event → `target_state: null`)
vs. `TransitionValidator.cs:88-90` (precondition fail → `target_state` populated).

### Exit codes

- `0` Success — `is_valid: true`.
- `1` RoutingFailure — `is_valid: false` (unknown event/type, or precondition failure).
- `3` CacheError — work item not found.

### Use when

- You are about to call `twig state <name>` and want to know which name. **This is the
  established way to derive the state name** — do not hardcode `"Done"` or `"Doing"`.
- You want to know whether an event is legal *before* attempting it (e.g. a workflow gate
  that needs to check preconditions).

### Don't use when

- You don't know what *event* to validate — `validate` requires the event name. Use
  `route` instead, which returns the appropriate `action` for the item's current phase.

### Examples

Workflow context — the canonical "validate then transition" pattern from
`scripts/scope-closer.ps1:54-60`:

```powershell
# Validate transition before close (#2647)
$validateJson = polyphony validate --work-item $item.work_item_id --event implementation_complete 2>$null
$validateResult = $validateJson | ConvertFrom-Json

if ($validateResult.is_valid) {
    twig set $item.work_item_id --output json 2>$null | Out-Null
    twig state $validateResult.target_state --output json 2>$null | Out-Null
    # …
}
```

Note: the state passed to `twig state` is `$validateResult.target_state`, **never** a
hardcoded literal like `Done`. This is the seam that lets the same PowerShell run
unchanged on Basic, Agile, Scrum, or CMMI process templates.

One-off (terminal):

```bash
polyphony validate --work-item 2647 --event implementation_complete
# {"work_item_id":2647,"event":"implementation_complete","is_valid":true,"target_state":"Done","message":"…"}
```

---

## `polyphony validate-config`

### Synopsis

```text
polyphony validate-config [--config <dir>] [--output json|human]
```

| Flag        | Type | Default       | Source                              |
|-------------|------|---------------|-------------------------------------|
| `--config`  | str  | `.conductor`  | `ValidateConfigCommand.cs:20`       |
| `--output`  | str  | `json`        | `ValidateConfigCommand.cs:20`       |

`--config` is a *directory*; the command appends `process-config.yaml`
(`ValidateConfigCommand.cs:22`).

### Purpose

Loads `process-config.yaml` and runs the 14 validation rules implemented in
`ConfigValidator.cs`. Rules V-1..V-8 produce errors (block execution); V-9..V-14 produce
warnings for missing companion files under `.conductor/`.

### Output JSON shape

`ConfigValidationResult` (`src/Polyphony/Configuration/ConfigValidationResult.cs`):

```jsonc
{
  "is_valid": false,
  "errors": [
    { "rule_id": "V-3", "message": "Type 'Foo' must have at least one capability.", "severity": "Error" }
  ],
  "warnings": [
    { "rule_id": "V-9", "message": "Type definition file missing: .conductor/work-item-types/foo.md", "severity": "Warning" }
  ]
}
```

For load failures (file missing / YAML unparseable / unsupported `schema_version`), the
output is a single `V-/CONFIG` error and the exit code is `2`
(`ValidateConfigCommand.cs:54-80`, `ProcessConfigLoader.cs:24-27`).

### Exit codes

- `0` Success — config valid (warnings allowed).
- `2` ConfigError — load failure or one or more V-1..V-8 errors.

### Use when

- CI / preflight: ensure the workspace's `.conductor/process-config.yaml` is well-formed
  before any workflow runs.
- After editing `process-config.yaml`, before committing.

### Don't use when

- You want to validate a *transition* (use `validate`) or *route* a work item (use
  `route`). `validate-config` only checks the config file shape.

### Examples

CI / preflight (JSON for downstream parsing):

```powershell
polyphony validate-config --config .conductor --output json
# {"is_valid":true,"errors":[],"warnings":[]}
```

Local dev (human-readable):

```bash
polyphony validate-config --output human
# Configuration is valid.
```

---

## `polyphony hierarchy`

### Synopsis

```text
polyphony hierarchy --work-item <id> [--depth <n>] [--config <path>]
```

| Flag          | Type | Default                                   | Source                       |
|---------------|------|-------------------------------------------|------------------------------|
| `--work-item` | int  | (required)                                | `HierarchyCommand.cs:19`     |
| `--depth`     | int  | `3`                                       | `HierarchyCommand.cs:19`     |
| `--config`    | str  | `.conductor/process-config.yaml`          | `HierarchyCommand.cs:19`     |

`--depth 0` returns only the root node (`HierarchyWalker.cs:17`).

### Purpose

Recursively walk the work-item tree from the given root, annotating each node with the
type's `capabilities` from the process config. Used by every PG-aware script to flatten,
group, and filter items by capability without touching type names.

### Output JSON shape

`HierarchyResult` (`src/Polyphony/Models/HierarchyResult.cs`); each node has the same
shape (`children` is always an array — `HierarchyCommand.cs:38-48` normalizes nulls):

```jsonc
{
  "work_item_id": 1234,
  "title":        "Some Epic",
  "type":         "Epic",
  "capabilities": ["plannable"],
  "state":        "Doing",
  "tags":         "PG-1; release-blocker",   // omitted when null
  "children": [
    { "work_item_id": 1235, "title": "…", "type": "Issue", "capabilities": ["plannable","implementable"], "state": "To Do", "children": [] }
  ]
}
```

### Exit codes

- `0` Success — JSON is valid.
- `3` CacheError — root work item not found.

### Use when

- A script needs to find all `implementable` items under a root, regardless of type
  (`scripts/task-router.ps1:51`, `scripts/child-router.ps1:36-40`).
- A script needs to group items by PG tag (`scripts/load-work-tree.ps1:46`,
  `scripts/scope-closer.ps1:39`).
- You need both `state` *and* `capabilities` in one call.

### Don't use when

- You only need a phase decision — `route` is one call instead of "fetch tree + manually
  classify".
- You need ADO field metadata beyond `state` and `tags` (e.g. `System.AssignedTo`,
  description) — those are not in the output shape. Fall back to `twig show`.

### Examples

Workflow context — flatten and filter by capability
(`scripts/task-router.ps1:27, 39-51`):

```powershell
$hierarchy = (polyphony hierarchy --work-item $WorkItemId --depth 3 2>$null) | ConvertFrom-Json

function Flatten-Hierarchy($node) {
    $items = @($node)
    if ($node.children) { foreach ($c in $node.children) { $items += Flatten-Hierarchy $c } }
    return $items
}
$allItems = @(Flatten-Hierarchy $hierarchy)
$implementable = @($allItems | Where-Object { $_.capabilities -contains 'implementable' })
```

One-off (terminal, depth 1):

```bash
polyphony hierarchy --work-item 2647 --depth 1
```
