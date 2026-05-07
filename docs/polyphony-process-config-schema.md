# `.conductor/process-config.yaml` Schema Reference

Schema and validation rules for the process configuration file consumed by every
Polyphony command. This file is the single point where work-item types, their
facets, lifecycle event → state-name transitions, and review/branch policies are
declared.

The schema is the C# class graph in `src/Polyphony/Configuration/ProcessConfig.cs`. The
validation rules are in `src/Polyphony/Configuration/ConfigValidator.cs`. The reference
loader is `src/Polyphony/Configuration/ProcessConfigLoader.cs`.

---

## Top-level structure

```yaml
schema_version: 1                          # int, optional, default 0; max supported = 1
process_template: Basic                    # str, REQUIRED — Basic | Agile | Scrum | CMMI | <custom>
platform: github                           # str, default "github"; "github" | "ado" today

types:
  <TypeName>:                              # one entry per work-item type
    facets: [plannable, implementable]
    filing_eligible: true
    max_nesting_depth: 1
    decomposition_guidance: |              # multi-line free text
      Decompose into Tasks when …
    self_referential: false
    allowed_child_types: [Task]
    # parent: <ParentTypeName>             # (optional) Name of the parent type, if this type is a child

transitions:
  <TypeName>:
    <event_name>: <StateName>              # e.g. begin_planning: Active

review_policies:
  planning:
    plan_pr: { agent_review: true, human_review: true, auto_merge: false }
  implementation:
    pg_pr:      { agent_review: true, human_review: false, auto_merge: true }
    feature_pr: { agent_review: true, human_review: true,  auto_merge: false }
  remediation:
    pg_pr: { agent_review: true, human_review: false, auto_merge: true }

branch_strategy:
  feature_branch:  "feature/{root_id}-{slug}"
  planning_branch: "planning/{root_id}"
  pg_branch:       "pg-{n}/{root_id}-{slug}"
  target:          main
```

YAML loading uses `UnderscoredNamingConvention` and `IgnoreUnmatchedProperties`
(`ProcessConfigLoader.cs:16-19`). Unknown keys are silently dropped — typo carefully.

---

## Field reference

### Top-level

| Key                | Type    | Required | Notes                                                                                              |
|--------------------|---------|----------|----------------------------------------------------------------------------------------------------|
| `schema_version`   | int     | no       | Defaults to absent (treated as 0). Must be ≤ 1 or load throws (`ProcessConfigLoader.cs:24-27`).    |
| `process_template` | string  | **yes**  | Free-form, but conventionally `Basic` / `Agile` / `Scrum` / `CMMI`. Validation V-1.                |
| `platform`         | string  | no       | Default `"github"`. Read by workflow `pr_platform_router` nodes; values today: `github`, `ado`.    |
| `types`            | map     | **yes**  | At least one entry. Validation V-2.                                                                |
| `transitions`      | map     | yes      | One entry per type with facets. Validation V-5, V-6.                                         |
| `review_policies`  | object  | no       | Optional; consumed by workflow review-loop nodes.                                                  |
| `branch_strategy`  | object  | no       | When present, drives `polyphony branch route`'s `workspace_hint`. When absent, `workspace_hint` is null.  |

### `types[<TypeName>]`

(`src/Polyphony/Configuration/ProcessConfig.cs:14-23`)

| Key                       | Type      | Default | Notes                                                                                  |
|---------------------------|-----------|---------|----------------------------------------------------------------------------------------|
| `facets`            | string[]  | `[]`    | Must contain at least one of `plannable`, `implementable` (case-insensitive). V-3, V-4. |
| `filing_eligible`         | bool      | `false` | Free-form; not enforced by ConfigValidator.                                            |
| `max_nesting_depth`       | int       | `1`     | Free-form; not enforced by ConfigValidator.                                            |
| `decomposition_guidance`  | string?   | null    | Free-form; consumed by planning agents.                                                |
| `self_referential`        | bool      | `false` | Free-form.                                                                             |
| `allowed_child_types`     | string[]  | `[]`    | Each entry must reference a type defined in `types:`. V-8.                             |
| `parent`                  | string?   | null    | Optional. Name of the parent type, if this type is a child of another.                 |

`facets` is the only field that drives engine behavior:
- `plannable` → routed to planning sub-workflows; needs decomposition.
- `implementable` → routed to implementation sub-workflows; counted as a leaf.
- Both → "issue-as-task" form (`PhaseDetector.cs:35-43`).

### `transitions[<TypeName>][<event>]`

The **event name** is on the left, the **state name** is on the right.

- Event names: declared by Polyphony / workflow logic. Four are precondition-aware
  (`TransitionValidator.cs:66-73`): `begin_planning`, `begin_implementation`,
  `implementation_complete`, `all_children_complete`. Others are accepted as plain
  name lookups.
- State names: must exist in the process template's actual state set (twig validates
  this at write time — see "Failure modes" below).

### `review_policies`

(`ProcessConfig.cs:24-36`) Optional. The shape is:

```yaml
review_policies:
  planning:        { <pr_kind>: { agent_review, human_review, auto_merge } }
  implementation:  { <pr_kind>: { … } }
  remediation:     { <pr_kind>: { … } }
```

`<pr_kind>` is workflow-defined (e.g. `plan_pr`, `pg_pr`, `feature_pr`). Each kind
maps to three booleans. Polyphony does not validate the shape of `review_policies`
beyond YAML parseability.

### `branch_strategy`

(`ProcessConfig.cs:38-44`) Optional. Templates may use the placeholders `{id}`,
`{root_id}`, `{slug}` (case-insensitive), substituted by `BranchNameResolver.cs:34-43`.
The `{n}` placeholder for PG numbers is substituted later by helper scripts
(`scripts/pg-router.ps1:51-54`).

`target` is the default base branch for feature PRs.

---

## Validation rules

`src/Polyphony/Configuration/ConfigValidator.cs` enforces 16 rules. V-1..V-8, V-15, V-16 are **errors** (block execution; exit code `2`). V-9..V-14 are **warnings** (informational; exit code stays `0`).

| ID    | Severity | Trigger                                                                                          | Source line |
|-------|----------|--------------------------------------------------------------------------------------------------|-------------|
| V-1   | Error    | `process_template` missing or whitespace.                                                         | 27          |
| V-2   | Error    | `types` is empty.                                                                                  | 33          |
| V-3   | Error    | A type has no facets.                                                                        | 56          |
| V-4   | Error    | A type lists a facet other than `plannable` / `implementable`.                                | 60-67       |
| V-5   | Error    | A type has no `transitions:` entry.                                                                | 71-74       |
| V-6   | Error    | `transitions:` references a type not declared in `types:`.                                         | 88-95       |
| V-7   | Error    | Duplicate type name (case-insensitive).                                                            | 42-49       |
| V-8   | Error    | `allowed_child_types` references a type not declared in `types:`.                                  | 77-84       |
| V-9   | Warning  | `.conductor/work-item-types/<slug>.md` missing.                                                    | 105-110     |
| V-10  | Warning  | `.conductor/work-item-types/templates/<slug>-template.md` missing.                                 | 113-119     |
| V-11  | Warning  | `.conductor/agent-guidance/{slug}.md` missing for a type.                                         | 139-145     |
| V-12  |          | *Removed in AB#2995: see V-11 for per-type agent guidance check.*                                  |             |
| V-13  |          | *Removed in AB#2995: see V-11 for per-type agent guidance check.*                                  |             |
| V-14  | Warning  | `.conductor/profile.yaml` missing.                                                                 | 144-148     |
| V-15  | Error    | `parent` references a type not declared in `types:`.                                               | 40-47       |
| V-16  | Error    | Cycle detected in type parent relationships.                                                       | 40-52       |

V-9..V-14 only fire when `repoRoot` is supplied (i.e. when run via the CLI rather than
in unit tests). Type-name → file-slug uses `ConfigValidator.ToSlug`
(`ConfigValidator.cs:162-163`): lowercase + spaces-to-hyphens.

---

## State names vs. categories on the right side of `transitions:`

`transitions:` values are **literal state names**, not categories. This is a deliberate
choice that lets `polyphony validate` return a `target_state` consumable directly by
`twig state` — no second translation. The cost is that the config file is
*template-specific*: a config valid for Agile is invalid for Basic and vice versa.

### ⚠ Anti-pattern callout: `scope_removed: Removed`

The current canonical config (`.conductor/process-config.yaml:25, 30, 34`) declares:

```yaml
transitions:
  Epic:
    scope_removed: Removed
  Issue:
    scope_removed: Removed
  Task:
    scope_removed: Removed
```

`Removed` is a state in **Agile, Scrum, CMMI** but **not in Basic**
(`twig2/tests/Twig.TestKit/ProcessConfigBuilder.cs:80-84` — Basic has only `To Do`,
`Doing`, `Done`). This passes `polyphony validate-config` (no V-rule checks state-name
existence against the template), and `polyphony validate --event scope_removed` will
return `is_valid: true, target_state: "Removed"`.

The failure happens later, inside twig: `StateResolver.ResolveByName`
(`twig2/src/Twig.Domain/ValueObjects/StateResolver.cs:30-60`) will reject the unknown
state with:

```text
Unknown state 'Removed'. Valid states: To Do, Doing, Done
```

**Mitigation:** for a Basic project, drop the `scope_removed` row (Basic has no
removal lifecycle), or — if you want a removal lifecycle in Basic — choose a state
that does exist (e.g. transition to `Done` and tag the item).

---

## Worked examples per ADO process template

The state sets below come from `twig2/tests/Twig.TestKit/ProcessConfigBuilder.cs:48-96`.

### Basic (Epic → Issue → Task)

States per type: `To Do`, `Doing`, `Done`. **No `Removed`.**

```yaml
process_template: Basic
platform: github

types:
  Epic:
    facets: [plannable]
    allowed_child_types: [Issue]
  Issue:
    facets: [plannable, implementable]
    allowed_child_types: [Task]
  Task:
    facets: [implementable]

transitions:
  Epic:
    begin_planning: Doing
    all_children_complete: Done
  Issue:
    begin_planning: Doing
    begin_implementation: Doing
    implementation_complete: Done
  Task:
    begin_implementation: Doing
    implementation_complete: Done
```

### Agile (Epic → Feature → User Story / Bug → Task)

States per type vary; Epic/Feature have `New`, `Active`, `Closed`, `Removed`. User
Story adds `Resolved`. Task lacks `Resolved`.

```yaml
process_template: Agile
platform: ado

types:
  Epic:
    facets: [plannable]
    allowed_child_types: [Feature]
  Feature:
    facets: [plannable]
    allowed_child_types: [User Story, Bug]
  User Story:
    facets: [plannable, implementable]
    allowed_child_types: [Task]
  Bug:
    facets: [plannable, implementable]
    allowed_child_types: [Task]
  Task:
    facets: [implementable]

transitions:
  Epic:
    begin_planning: Active
    all_children_complete: Closed
    scope_removed: Removed
  Feature:
    begin_planning: Active
    all_children_complete: Closed
    scope_removed: Removed
  User Story:
    begin_planning: Active
    begin_implementation: Active
    implementation_complete: Closed
    scope_removed: Removed
  Bug:
    begin_implementation: Active
    implementation_complete: Resolved
    scope_removed: Removed
  Task:
    begin_implementation: Active
    implementation_complete: Closed
    scope_removed: Removed
```

### Scrum (Epic → Feature → Product Backlog Item / Bug → Task)

PBI/Bug states: `New`, `Approved`, `Committed`, `Done`, `Removed`. Task has different
states: `To Do`, `In Progress`, `Done`, `Removed`.

```yaml
process_template: Scrum
platform: ado

types:
  Epic:
    facets: [plannable]
    allowed_child_types: [Feature]
  Feature:
    facets: [plannable]
    allowed_child_types: [Product Backlog Item, Bug]
  Product Backlog Item:
    facets: [plannable, implementable]
    allowed_child_types: [Task]
  Bug:
    facets: [plannable, implementable]
    allowed_child_types: [Task]
  Task:
    facets: [implementable]

transitions:
  Epic:
    begin_planning: In Progress
    all_children_complete: Done
    scope_removed: Removed
  Feature:
    begin_planning: In Progress
    all_children_complete: Done
    scope_removed: Removed
  Product Backlog Item:
    begin_planning: Committed
    begin_implementation: Committed
    implementation_complete: Done
    scope_removed: Removed
  Bug:
    begin_implementation: Committed
    implementation_complete: Done
    scope_removed: Removed
  Task:
    begin_implementation: In Progress
    implementation_complete: Done
    scope_removed: Removed
```

### CMMI (Epic → Feature → Requirement / Bug → Task)

Universal state set across types: `Proposed`, `Active`, `Resolved`, `Closed`, `Removed`.

```yaml
process_template: CMMI
platform: ado

types:
  Epic:
    facets: [plannable]
    allowed_child_types: [Feature]
  Feature:
    facets: [plannable]
    allowed_child_types: [Requirement]
  Requirement:
    facets: [plannable, implementable]
    allowed_child_types: [Task]
  Bug:
    facets: [plannable, implementable]
    allowed_child_types: [Task]
  Task:
    facets: [implementable]

transitions:
  Epic:
    begin_planning: Active
    all_children_complete: Closed
    scope_removed: Removed
  Feature:
    begin_planning: Active
    all_children_complete: Closed
    scope_removed: Removed
  Requirement:
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
```

---

## Custom processes

If your process template renames states, only the `transitions:` right-hand sides need
updating. Examples:

- **`Done` renamed to `Closed`:** change every `implementation_complete: Done` to
  `implementation_complete: Closed`. No engine code changes.
- **`InProgress` split into `In Review` and `Active`:** add additional event names
  (e.g. `submit_for_review: In Review`, `begin_implementation: Active`). The four
  precondition-aware events (`begin_planning`, `begin_implementation`,
  `implementation_complete`, `all_children_complete`) gate on the *category* the new
  state resolves to (via `StateCategoryResolver.Resolve`), so as long as the new state
  name maps to `InProgress` (either by ADO's authoritative category or by the
  hardcoded heuristic that recognises `"in progress"`, `"active"`, `"doing"`,
  `"committed"`, `"approved"` —
  `twig2/src/Twig.Domain/Services/Process/StateCategoryResolver.cs`), preconditions
  continue to fire correctly.
- **New event name (e.g. `pause`):** add it to `transitions:`. It will be accepted by
  `polyphony validate` as a plain name lookup with no precondition check.

---

## Failure modes

### A state name that doesn't exist in the type's state set

Polyphony will not catch this — `validate-config` does not cross-reference state names
against the actual process template's state set. The failure happens at twig write time:

```text
Unknown state 'Removed'. Valid states: To Do, Doing, Done
```

(Format from `twig2/src/Twig.Domain/ValueObjects/StateResolver.cs:58-59`.)

**Detection:** integration test that runs `polyphony validate --event <each event>`
followed by a dry `twig state $target_state` against a representative work item per
type. Catch the error and assert `Result.IsSuccess`.

### Unsupported `schema_version`

`ProcessConfigLoader` throws on `schema_version > 1`
(`ProcessConfigLoader.cs:24-27`):

```text
Unsupported process config schema version <N>. This version of Polyphony supports
schema_version 0 (absent) and 1.
```

The CLI surfaces this as a `CONFIG`-rule load error with exit code `2`
(`ValidateConfigCommand.cs:32-37`).

### Missing config file

`ProcessConfigLoader.Load` throws `FileNotFoundException`
(`ProcessConfigLoader.cs:12-13`); caught and surfaced the same way as above.

