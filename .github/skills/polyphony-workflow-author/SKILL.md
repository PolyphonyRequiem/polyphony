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
| What phase is this work item in? What action should we take?     | `polyphony route --work-item N`                   | `Commands/RouteCommand.cs`      |
| What state name should I pass to `twig state` for event X?       | `polyphony validate --work-item N --event X`      | `Commands/ValidateCommand.cs`   |
| Is this transition even legal right now?                         | `polyphony validate --work-item N --event X`      | `Commands/ValidateCommand.cs`   |
| Walk children: capabilities, states, tags                        | `polyphony hierarchy --work-item N --depth 3`     | `Commands/HierarchyCommand.cs`  |
| Get suggested branch names for this work item                    | `polyphony route` → `output.workspace_hint`       | `Routing/BranchNameResolver.cs` |
| Validate `.conductor/process-config.yaml` itself                 | `polyphony validate-config --config .conductor`   | `Commands/ValidateConfigCommand.cs` |
| Set the active work item context                                 | `twig set <id> --output json`                     | twig CLI                        |
| Change state                                                     | `twig state <name> --output json`                 | twig CLI                        |
| Add a comment to a work item                                     | `twig note --text "…"`                            | twig CLI                        |
| Refresh cache from ADO                                           | `twig sync --output json`                         | twig CLI                        |
| Read full work-item details (title, type, fields)                | `twig show <id> --output json` / `twig tree`      | twig CLI                        |
| Create a child work item                                         | `twig new --parent <id> --type <T> --title "…"`   | twig CLI                        |

Rule of thumb: if you find yourself wanting to invent a new polyphony verb, re-read
`polyphony-cli-reference.md`. The four existing verbs cover every read concern. Writes
all go through `twig`.

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
    twig note --text 'Task completed and approved by reviewer';
    twig state Done;            # ← hardcoded literal — replace with target_state
    @{ child_id = $taskId; completed = $true } | ConvertTo-Json
```

`scripts/task-router.ps1:106` — hardcoded `Doing` in a helper script:

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
| `scripts/task-router.ps1`         | Within-PG task selection via capability filtering and `polyphony hierarchy`. Note: contains a remaining `twig state Doing` literal at line 106 to be replaced. |
| `scripts/detect-state.ps1`        | Top-level phase detection: combines `polyphony route` + `polyphony validate` + `twig tree` into the apex `state_detector` JSON shape consumed by `twig-sdlc-v2-full.yaml`. |
| `scripts/pg-router.ps1`           | PR group lifecycle: groups items by PG tag, checks remote branches and gh PR state, returns the next PG action. |
| `scripts/child-router.ps1`        | Plannable-child discovery for recursive planning (`plan-level.yaml`). The reference for capability-based filtering ("`_.capabilities -contains 'plannable'`"). |
| `scripts/feature-pr-creator.ps1`  | gh-PR creation against `workspace_hint.feature_branch`. The reference for using `polyphony route` only for branch-name validation. |
| `scripts/load-work-tree.ps1`      | Hierarchy → PG-grouped tree with completion status. The reference for `Group-ByPG` and PG enumeration. |

---

## Adding a new helper script

### File naming and location

- Place under `scripts/<verb>.ps1`. Verbs are kebab-case, action-first
  (`scope-closer`, `task-router`, `pg-router`, `feature-pr-creator`).
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

- `polyphony route` → `phase`, `action`, `message`, `workspace_hint.feature_branch`,
  `workspace_hint.pg_branch` (`Models/RouteResult.cs`).
- `polyphony validate` → `is_valid`, `target_state`, `event`, `message`
  (`Models/ValidateResult.cs`).
- `polyphony hierarchy` → `work_item_id`, `title`, `type`, `state`, `capabilities`,
  `tags`, `children` (recursive; `Models/HierarchyResult.cs`).

These fields are covered by `tests/Polyphony.Tests/Commands/JsonOutputContractTests.cs`,
so they will not change shape silently. If you find yourself reaching for a field that
isn't in the model, add it to the model + test (see polyphony-cli-developer skill),
don't paper over it in PowerShell.
