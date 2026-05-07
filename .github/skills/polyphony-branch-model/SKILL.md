---
name: polyphony-branch-model
description: >-
  Activate when planning or implementing work that creates, names, or merges
  branches in a polyphony SDLC run. Covers the canonical branch tree
  (`feature/{root}` integration trunk; recursive `plan/`, `mg/`, `impl/`,
  `evidence/` branches), driver-enforced promotion gates, stable
  planner-declared MG ids, default-nest trigger for decomposable+implementable
  children, mandatory merge commits at promote-chain layers, the run manifest
  + same-root run lock, renegotiation flow with parent-plan-generation
  serialization, and cross-sibling code-dependency rebase rule. Companion to
  the ADR at `docs/decisions/branch-model.md` — load this skill when *acting*;
  read the ADR when *deciding*.
user-invokable: false
---

# Polyphony Branch Model Skill

The canonical branch tree for a polyphony SDLC run. Use this when planning
work that touches branch creation, when authoring workflow YAMLs that open
PRs, or when building CLI verbs that name branches.

The full rationale, alternatives considered, and consequences live in
[`docs/decisions/branch-model.md`](../../../docs/decisions/branch-model.md).
This skill is the operational distillation — what to do, in what order,
with what names, and which gates the driver enforces.

> **Rev 4** of the branch model. If something here contradicts older notes
> (position-based MG numbers, "git enforces ordering," `mg_promoted`
> requirement kind, immediate-parent-only generation tracking, hyphen-joined
> `mg_path`, etc.), the ADR wins.

---

## The branch tree

```
main
 └── feature/{root_id}                            ← integration trunk for the run
      │   plus committed run manifest at .polyphony/run.yaml
      │
      ├── plan/{root_id}                          ← root plan branch
      │    └── plan/{root_id}-{item_id}           ← descendant plan, leaf id only;
      │                                              hierarchy via PR base branch
      │
      ├── mg/{root_id}_{mg_id}                       ← top MG (mg_id = stable string;
      │    │                                            mg_path = mg_id at top level)
      │    ├── impl/{root_id}-{item_id}              ← impl branch (always exists, flat)
      │    └── mg/{root_id}_{mg_id}_{nested_mg_id}   ← nested MG; mg_path joins with `_`;
      │         │                                       nested_mg_id is planner-declared
      │         │                                       or item-{child_id}
      │         ├── impl/{root_id}-{owner_item_id}   ← non-leaf owner's own impl branch
      │         └── impl/{root_id}-{descendant_id}   ← descendant tasks
      │
      └── evidence/{root_id}-{item_id}            ← evidence branch (Phase 6)
```

### Naming rules at a glance

| Branch kind | Format | Branches from |
|---|---|---|
| `feature/{r}` | one per run | `main` |
| `plan/{r}` | root plan | `feature/{r}` |
| `plan/{r}-{item_id}` | descendant plan, **flat** — leaf item id only | parent's plan branch |
| `mg/{r}_{mg_id}` | top MG, `mg_id` = planner-declared stable id | `feature/{r}` |
| `mg/{r}_{mg_path}` | nested MG; `mg_path` = `_`-joined chain of `mg_id` segments; terminal segment is planner-declared or `item-{child_id}` | parent MG branch |
| `impl/{r}-{item_id}` | task, **flat** — leaf item id only | enclosing MG branch |
| `evidence/{r}-{item_id}` | one per item with evidence | `feature/{r}` |

### Hard rules

- **Three delimiters, one role each** (Rev 4):
  - `/` — separates the ref-class prefix from the payload. Reserved.
  - `-` — separates `{root_id}` from a single payload segment in
    `impl/`, `plan/`, `evidence/`, and lives inside MG ids.
  - `_` — the **MG hierarchy delimiter**. Appears only inside `mg/`
    branch payloads. Unambiguous because the MG id grammar
    `^[a-z][a-z0-9-]{0,30}$` excludes `_`.
- Never use `/` for hierarchy — git treats `/` as ref namespacing and
  `mg/X` precludes `mg/X/Y`.
- **One ref-class prefix per branch kind:** `feature/`, `plan/`, `mg/`,
  `impl/`, `evidence/`. Don't invent new prefixes without an ADR amendment.
- **Depth 3 = warning, depth 5 = hard stop** for MG nesting. Override
  beyond 5 requires `--allow-deep-nesting` plus recorded human approval.
- **Impl PRs always exist** for every implementable item — leaf or non-leaf.
  The impl PR is the per-item review surface; do not skip it.
- **Implementable non-leaf items get their own impl branch under their
  nested MG** (alongside the descendants' impl branches).
- **The PR base branch carries the topology** for flat impl/plan
  branches. Impl branch `impl/1234-5678` based on `mg/1234_data-layer`
  unambiguously belongs to the `data-layer` MG; the manifest records the
  edge.

---

## Promote chain (head → base)

| Layer | Head | Base | Merge mode policy | Merge method |
|---|---|---|---|---|
| Impl PR | `impl/{r}-{item_id}` | enclosing `mg/{r}_{mg_path}` | `(scope, impl_pr)` | squash OR merge |
| Nested MG PR | `mg/{r}_{mg_path}` (terminal segment is the nested id) | `mg/{r}_{parent_mg_path}` | `(scope, mg_pr)` | **merge commit (mandatory)** |
| Top MG PR | `mg/{r}_{mg_id}` | `feature/{r}` | `(scope, mg_pr)` | **merge commit (mandatory)** |
| Plan PR (descendant) | `plan/{r}-{item_id}` | parent's plan branch | `(scope, plan_pr)` | **merge commit (mandatory)** |
| Plan PR (root) | `plan/{r}` | `feature/{r}` | `(scope, plan_pr)` | **merge commit (mandatory)** |
| Evidence PR | `evidence/{r}-{i}` | `feature/{r}` | `(scope, evidence_pr)` | squash OR merge |
| Feature PR | `feature/{r}` | `main` | `(scope, feature_pr)` | operator choice |

**The driver enforces ordering, not git.** A parent PR's merge action is
held closed until the corresponding requirement state is satisfied. Git
ancestry is observed evidence; PR merge state is the canonical signal.

To resolve the merge mode for any layer:

```powershell
polyphony policy resolve --scope <type:Name|root|default> --domain <pr_kind>
```

Returns one of: `auto`, `manual`, `warning`, `policy_aware_blocked`.

### Driver-enforced merge gates

| PR kind | Gate fires when |
|---|---|
| Impl PR | Owning item's `implementation_merged` is `ready` AND impl PR has aggregated platform status `approved`. |
| Nested MG PR | All impl PRs and grandchild MG PRs in this MG have merged. |
| Top MG PR | All impl PRs and nested MG PRs in this MG have merged. |
| Plan PR | All child plan PRs (if any) have merged AND `plan_reviewed` for the owning item is `ready`. |
| Evidence PR | Owning item's `action_satisfied` is `ready` AND evidence artifact passes verification. |
| Feature PR | All top MG PRs, root plan PR, all evidence PRs merged AND every in-scope item's full requirement set is `satisfied`. |

---

## MG identity — stable planner-declared id

The planner declares each merge group with a **stable string id** that
becomes the branch segment.

```yaml
merge_groups:
  - id: data-layer        # stable id → mg/{r}_data-layer
    order: 10             # dispatch ordering only; can change freely
    intent: "data layer + migration"
    items: [101, 102, 103]
    isolation: per-merge-group
  - id: ui-surface
    order: 20
    intent: "ui surface + e2e tests"
    items: [104, 105]
    isolation: per-item
```

### Identity rules

- `id` shape: `^[a-z][a-z0-9-]{0,30}$` (lowercase kebab, max 31 chars).
- `id` is **immutable** once any branch under that MG exists.
- `id` is **unique within the parent's children**.
- `order` is presentation/dispatch only — change it freely; nothing
  renames.
- Reordering MGs by changing `order`: free.
- Removing a materialized MG: requires explicit retirement gate
  (`human_gate`).
- Reusing a retired `id`: forbidden.

### Default when planner is silent

```yaml
merge_groups:
  - id: default
    order: 0
    items: <all implementable descendants>
    isolation: per-merge-group
```

Reviewers see this default in the plan PR and can request a split.

---

## Decision: when does a child item get a nested MG?

**Default rule (Rev 2):** A child becomes a nested MG when **both**:

1. Child carries the `implementable` facet.
2. Child is `decomposable: true`.

That's it. **No descendant-emergence check.** Topology is fixed at the
moment the parent plan PR merges; runtime descendant flips do not change
the tree.

### Override

```yaml
# In the parent's plan
children_overrides:
  - id: 4567
    merge_group_nesting: flat        # force impl branch even though trigger fires
    reason: "single-file change, nesting overhead not worth it"

  - id: 4568
    nested_mg_id: data-migrations    # name the nested MG that the trigger produces
    reason: "groups with the data-migrations PR ancestry on review side"
```

`merge_group_nesting: flat` and `nested_mg_id` are mutually exclusive on a
child. The driver writes a one-line warning to the parent plan PR
explaining each override.

### Nested MG id — source rule

When the nesting trigger fires for child item `{child_id}`, the resulting
nested MG needs a stable identifier matching the same
`^[a-z][a-z0-9-]{0,30}$` regex MG ids obey. Source order:

1. **Planner-declared.** Read from
   `parent_plan.children_overrides[].nested_mg_id`. Preferred when the
   nested integration scope has meaningful semantics (`data-migrations`).
2. **Default.** When the planner is silent, derive
   `nested_mg_id = item-{child_id}` (e.g. `item-4567`). The literal
   `item-` prefix is required so the derived id starts with a lowercase
   letter and stands out in branch listings.

Both forms are immutable once the nested MG materializes, both contribute
identically to the topology hash, and both are subject to the retirement
gate.

### Why structural and stable

Branch topology must not flip mid-run. Conditioning nesting on "has
implementable descendants in scope" was the Rev 1 rule and is wrong —
descendants emerge during recursive planning, which would force a
topology rename. Default-nest produces some empty MG branches for
decomposables that never grow descendants; that's a small price for
topology stability.

---

## Implementable non-leaf items — they own a impl PR too

If an item is **both** `implementable` and `decomposable`, it owns a
nested MG **and** its own impl branch under that MG:

```
mg/{r}_{parent_mg_path}_{nested_mg_id}    ← child's nested MG
                                             nested_mg_id is planner-declared
                                             or item-{child_id}
 ├── impl/{r}-{child_id}                    ← child's own impl branch
 ├── impl/{r}-{descendant_a_id}             ← flat task names; PR base = enclosing MG
 └── impl/{r}-{descendant_b_id}
```

This satisfies `RequirementKind.ImplementationMerged` for the child
without breaking per-item review attribution. Direct commits to the MG
branch are forbidden — always go through a impl PR.

---

## Renegotiation flow — when a child plan needs to change parent

Use git's natural ancestry. Do **not** introduce a new branch type.

```yaml
# In the child plan document
requests_parent_change: true
parent_plan_generation: 2     # generation of the immediate parent at branch creation
ancestor_plan_generations:    # all ancestor generations at branch creation
  root: 3
  100: 2                      # immediate parent
  # … other ancestors above 100 if any …
parent_change_summary:
  - "Adjusted parent's MG split: data-layer → data-layer + data-migrations"
  - "Added new actionable child {item_id}"
```

### Concurrency rules

When multiple child plan branches each carry parent edits:

1. Driver acquires a **parent-plan write lock** before merging any
   parent-affecting child plan PR.
2. First merge bumps the parent's `plan_generation` counter (recorded in
   the run manifest).
3. Other in-flight parent-affecting child PRs become **stale** — their
   recorded `parent_plan_generation` no longer matches.
4. Stale PRs are not auto-rebased silently. Driver:
   - blocks merge,
   - posts a comment on the stale PR,
   - schedules an auditable rebase OR raises a `human_gate` per
     `(scope, child_plan_rebase)` policy.

### Ancestor-cascade staleness

Tracking only the **immediate** parent's generation lets a child plan PR
silently drift against an **ancestor** further up the chain. The driver
enforces ancestor cascade:

1. When any ancestor's `plan_generation` is bumped (root, grandparent,
   etc.), the driver walks the descendant tree of in-flight plan branches.
2. Every descendant plan PR whose
   `ancestor_plan_generations[ancestor_id]` no longer matches the
   manifest's current value is marked `stale: ancestor_plan_drift`.
3. Same block + comment + policy rebase rule applies, keyed on
   `(scope, ancestor_plan_rebase)` — separate from `child_plan_rebase`
   because the ancestor remedy is harder (the diff to integrate may be
   ancestors removed).
4. After rebase, the PR's `ancestor_plan_generations[]` map is updated to
   the manifest's current values for **all** ancestors on its chain.

### Hard rule

Any parent-affecting change without `requests_parent_change: true` is a
review finding. The driver flags it; reviewers must reject.

### When parent plan PR has already merged

Driver bumps `parent_plan_generation`, opens a new parent plan PR from
the same branch with the child-proposed edits, and marks any in-flight
sibling child plan PRs stale.

---

## Cross-sibling code dependencies

Pure ordering edges are a worklist concern (Phase 7). **Code dependencies**
(downstream item compiles/tests against upstream item's code) need
content from the upstream branch.

Planner declares dependency kind:

```yaml
cross_item_edges:
  - from_item: 101
    to_item: 205
    kind: ordering_only    # default — no branch action needed
  - from_item: 102
    to_item: 206
    kind: code_dependency  # downstream needs upstream code
```

For `code_dependency` edges across sibling MG boundaries, the driver
applies one of:

1. **Same-MG remedy**: planner repacks items into the same MG.
2. **Promote-and-rebase remedy**: upstream MG promotes to common ancestor;
   downstream MG auto-rebases onto the promoted ancestor before dispatch.
   Rebase commit recorded in the run manifest.
3. **Replan remedy**: driver raises a `human_gate`; defers dispatch.

Choice driven by `(scope, cross_mg_code_dep)` policy.

### Materialization gate on promote-and-rebase

Auto-rebase under remedy 2 is only safe **before the downstream MG branch
or any of its descendant task / nested-MG branches are materialized.**
Once they exist, rebasing the downstream MG branch invalidates SHAs that
underpin open impl PRs and reviewer comments.

Rule: the driver auto-rebases when

- the downstream `mg/{r}_{downstream_mg_path}` branch does not yet exist, OR
- it exists but has no descendant task or nested-MG branches.

When downstream branches **are** materialized:

1. Mark the downstream MG (and its impl PRs) `stale: cross_mg_code_dep`
   in the run manifest.
2. Post a comment on each affected open PR explaining why.
3. Apply `(scope, cross_mg_code_dep_rebase)` policy:
   - `auto` → driver schedules an audited rebase of the downstream MG
     branch and re-bases each open impl PR onto it; every commit is
     recorded in the run manifest.
   - `warning` → schedules the rebase, posts the warning, requires
     reviewer acknowledgement on each affected PR before merging.
   - `manual` → opens a `human_gate`; no rebase happens automatically.

---

## Run manifest + concurrent-run lock

Every run commits `.polyphony/run.yaml` on the feature branch:

```yaml
schema: 1
root_id: 1234
platform_project: dev.azure.com/dangreen-msft/Twig
created_at: 2026-05-06T15:30:00Z
created_by: dangreen
branch_model_version: 1

plan_generations:
  root: 3
  100: 2
  101: 1

# Top-level. Hashed input is canonicalized — see §Topology hash inputs.
topology_hash: sha256:abc123…

merge_groups:
  - id: data-layer
    mg_path: data-layer            # `_`-joined chain; top-level => single segment
    parent_mg_path: null         # top-level under feature/
    items: [101, 102]
    nesting: top
    isolation: per-merge-group
    nesting_override: null
  - id: ui-surface
    mg_path: ui-surface
    parent_mg_path: null
    items: [103]
    nesting: top
    isolation: per-merge-group
    nesting_override: null
  - id: item-4567                # default-derived nested MG id
    mg_path: data-layer_item-4567  # `_`-joined: data-layer / item-4567
    parent_mg_path: data-layer
    items: [4567, 4571, 4572]
    nesting: nested
    isolation: per-merge-group
    nesting_override: null

# Recorded rebase events (cross-MG code-dep, child-plan, ancestor-cascade)
rebases:
  - branch: mg/1234_data-layer
    onto: feature/1234
    reason: cross_mg_code_dep
    commit: 0b1f3e9
    recorded_at: 2026-05-06T18:00:00Z

# Recorded human-gate approvals
human_approvals:
  - gate: deep_nesting_depth_4
    approved_by: dangreen
    approved_at: 2026-05-06T17:00:00Z
    detail: "mg/1234_data-layer_item-4567_migrations approved at depth 4"

# Retired MG ids (cannot be reused under this root)
retired_merge_group_ids:
  - id: legacy-pipeline
    retired_at: 2026-05-06T16:00:00Z
    reason: "split into two new MGs by replan"
```

The shape above is **normative** for `schema: 1`. Adding a new field
requires bumping `schema:`.

### Topology hash inputs

`topology_hash` is `sha256` over the **canonicalized** sequence of
records, not the YAML text:

1. Each MG contributes one record:
   `(mg_path, items_sorted_asc, isolation, nesting_override_or_null)`.
2. Records are sorted by `mg_path` (lexicographic).
3. Within each record, `items` are sorted ascending.
4. Each record is serialized as a tab-separated UTF-8 line:
   `mg_path\titems_csv\tisolation\tnesting_override\n`.
5. `nesting_override` is the literal string `null` when absent.
6. The full canonical text is the concatenation of all lines.
7. `topology_hash = sha256(canonical_text)`, stored as `sha256:{hex}`.

Including `mg_path` (not just `id`) means two MGs with the same terminal
`id` segment under different parents produce different `mg_path` and so
distinct hash records. `parent_mg_path` is implicit in `mg_path` (drop
the trailing segment) and not duplicated. Because `_` is excluded from
the MG id grammar, `mg_path` admits one and only one segmentation, so
the canonical text is unambiguous.

`plan_generations`, `rebases`, `human_approvals`, and
`retired_merge_group_ids` are **not** part of the topology hash —
they're operational state, not topology.

This manifest is the **canonical resume key**, not the agent prompt or
regenerated plan content.

### Run lifecycle

1. The apex driver against `<root>` acquires a lock on `(repo, project, root_id)`.
2. If `feature/{root}` exists with a manifest:
   - **Same topology hash** → resume the existing run.
   - **Different hash, no branches materialized for the diff** → accept,
     update manifest.
   - **Different hash, branches exist for the diff** → enter
     plan-revision/migration `human_gate`. Never silently rename.
3. If no `feature/{root}` exists → start fresh, commit a new manifest.
4. **Concurrent same-root runs are refused.** Operator UX distinguishes
   *attach to live controller* (read-only telemetry) from *start a
   second driver* (refused). Lock-held message:
   *"A run for root 1234 is already in flight (held by {host}, started
   {ts}, pid {pid}). Attach? Wait? Abort and force-release?"*

---

## Isolation scope ↔ branch contract

| Isolation | Worktree | Where agent commits | Impl branches | Impl PRs |
|---|---|---|---|---|
| `per-merge-group` (default) | One per MG | Impl branch in shared MG worktree (sequential) | ✓ | ✓ |
| `per-item` | One per implementable item | Impl branch in per-item worktree (parallel) | ✓ | ✓ |

**Impl branches and impl PRs always exist.** Isolation changes *where*
the agent works, not *whether* per-item review surfaces exist. A
pseudo-mode that writes straight to the MG branch was rejected — it
breaks per-item review attribution.

> **Glossary note.** The current `docs/glossary.md` `per-merge-group` entry
> says "Integration step is a no-op; items already commit to the MG
> branch." That predates this ADR and is wrong post-Phase 4. Updated
> language: "per-merge-group serializes items in a single worktree;
> each item still opens its own impl PR; the integration step is a
> sequence of impl-PR merges into the MG branch." Phase 4b cleans this
> up.

---

## Recipes

### Open a top-level MG PR

```powershell
polyphony branch ensure-mg-branch --root 1234 --mg-id data-layer
polyphony pr open-mg-pr --root 1234 --mg-id data-layer
# Branch: mg/1234_data-layer, base: feature/1234, method: merge commit
```

### Open a nested MG PR

```powershell
polyphony branch ensure-mg-branch --root 1234 --mg-path data-layer --nested-mg-id migrations
polyphony pr open-mg-pr --root 1234 --mg-path data-layer --nested-mg-id migrations
# Branch: mg/1234_data-layer_migrations, base: mg/1234_data-layer
#
# If the planner left nested_mg_id empty for child item 4567, the driver derives
# nested_mg_id = item-4567 and the branch becomes mg/1234_data-layer_item-4567.
```

### Open a impl PR (including for an implementable non-leaf)

```powershell
polyphony branch ensure-impl-branch --root 1234 --mg-path data-layer_migrations --item 5678
polyphony pr open-impl-pr --root 1234 --mg-path data-layer_migrations --item 5678
# Branch: impl/1234-5678, base: mg/1234_data-layer_migrations
```

### Resolve merge mode for a impl PR

```powershell
polyphony policy resolve --scope type:DefaultLeafType --domain impl_pr
# { mode: "auto" }
```

(Type names load from `process-config.yaml` at runtime — read actual type
names from the config, not from this skill.)

### Decide nesting for a child you're planning

1. Does the child have the `implementable` facet?
   - No → no MG concern; the child is plannable-only or actionable-only.
2. Is the child `decomposable: true`?
   - No → impl branch in parent MG.
   - Yes → **nested MG** (default). Determine `nested_mg_id`:
     - If you're naming it: set `children_overrides[].nested_mg_id` in the
       parent plan to a meaningful kebab-case id (e.g. `data-migrations`).
     - If you don't name it: driver defaults to `item-{child_id}`
       (e.g. `item-4567`).
   - Branch: `mg/{r}_{parent_mg_path}_{nested_mg_id}`. Add a impl branch
     for the child's own implementation under it.

### Resume detection

```powershell
polyphony state resume --root 1234
# Reads .polyphony/run.yaml on feature/1234, compares topology hash, returns
# one of: { decision: "resume" | "accept-new-topology" | "human-gate-required" }
```

### Cross-sibling code dependency

```powershell
# Planner emits cross_item_edges in the plan; driver consults policy
polyphony policy resolve --scope root --domain cross_mg_code_dep
# { remedy: "promote_and_rebase" }
# Driver schedules rebase of downstream MG onto promoted upstream automatically
```

---

## Anti-patterns

| Don't | Do | Why |
|---|---|---|
| `mg/1234/data/migrations` (slash separators) | `mg/1234_data_migrations` | Git ref namespace conflict |
| `mg/1234-data-layer-migrations` (Rev 3 hyphen-joined `mg_path`) | `mg/1234_data-layer_migrations` (Rev 4 `_`-joined) | Hyphen-joining `mg_path` collides — top-level `data-layer-migrations` looks identical to nested `migrations` under `data-layer` |
| `impl/1234-data-layer-migrations-5678` (Rev 3 path-encoded) | `impl/1234-5678` (Rev 4 flat) | Item IDs are project-unique; the PR base branch records the enclosing MG |
| Number MGs `1, 2, 3` (positional) | Use planner-declared stable ids | Reorder/remove without renaming |
| Trust git ancestry to enforce ordering | Driver gates on PR state + requirement disposition | Git doesn't know task→MG→feature semantics |
| Squash-merge an MG or plan PR | Use a merge commit | Squash destroys integration-event boundary |
| Skip impl PRs when isolation is per-MG | Always open impl PRs | Per-item review attribution non-negotiable |
| Skip the implementable non-leaf's own impl PR | It still gets one under its nested MG | Required for `implementation_merged` |
| Encode cross-item dependencies in branch names | Use `cross_item_edges` in the plan | Branch tree is substrate, not order encoding |
| Add a new branch prefix without an ADR | Amend the ADR | Five prefixes is the contract |
| Auto-rebase a stale child plan branch silently | Block + comment + audit-record the rebase | Renegotiation must be auditable |
| Treat absence of run manifest as "no run" | Treat as corruption — refuse and surface | Manifest is canonical truth |
| Run two apex-driver runs against the same root | One holds the lock; second resumes or refuses | Branch tree can't host two simultaneous runs |
| Use `mg_promoted` requirement | Use `implementation_merged` (canonical kind) | `mg_promoted` doesn't exist in code |

---

## When this skill is wrong

If you find yourself wanting to do something this skill forbids and you
have a reasoned argument, **propose an ADR amendment** rather than
working around the rules. The ADR at `docs/decisions/branch-model.md`
explicitly invites revisits when:

- Planners regularly hit the depth-3 warning.
- Reviewers complain about per-impl PR fatigue (likely the answer is
  `impl_pr` mode `auto`; if API/CI fatigue, propose `impl_pr_granularity`
  with `batched` opt-in).
- A future phase needs cross-tree branch references.

Branch names are one-way doors. Amend deliberately, in writing.
