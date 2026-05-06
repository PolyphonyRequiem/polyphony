# Polyphony Branch Model — Feature Trunk + Plan / Merge-Group / Task Tree

> **Status:** Proposed, **Rev 4** (2026-05). Awaiting sign-off as part of
> Phase 4 of the PR/branch lifecycle overhaul. Rev 2 incorporated a hostile
> design pass that found 8 blocking ambiguities in Rev 1; Rev 3 incorporated
> a second pass that found 2 blockers and 3 worth-addressing items against
> Rev 2. Rev 4 incorporates a third pass focused on branch-name grammar that
> uncovered a real collision bug in the Rev 3 hyphen-joined `mg_path` and
> moves MG branches to a `_`-delimited path encoding while flattening
> impl/plan/evidence branches to leaf-id only. See [§ Revision history](#revision-history).
> **Driver:** Phase 4 of the lifecycle redesign locks in the branch tree the
> driver and PR machinery operate on. Branch names are one-way doors — once a
> run is in flight the names cannot be cheaply renamed without confusing
> resume, review history, and human reviewers.
> **Supersedes:** the ad-hoc PG branch model (single branch per execution
> group, agents committing directly, no per-impl PR, no nesting).

## Context

The current pipeline has a **flat merge-group model**:

- A single branch per execution group (PG today, MG after this ADR).
- Agents commit straight to the MG branch.
- One PR per MG, reviewed by an LLM agent inside the workflow.
- No per-task review surface.
- No nesting — a decomposable child whose parent already has an MG
  cannot itself have an MG; its work commits straight into the parent's MG
  branch with no per-item attribution.
- No plan branches — plans are file artifacts. Reviewers can't comment
  inline on a plan via gh / ADO native review.
- No evidence branches — non-code work has no branch story at all.

Phase 1 settled the vocabulary (`root`, `merge group`, `decomposable`, `facet`).
Phase 2 settled the state model (requirement + disposition tuples, dispatched
per-requirement). This ADR settles **the branches the dispatched work lands on
and the PRs it merges through.**

The model has to satisfy:

1. **Recursive planning** — a tree of plannable items; each child plan
   reviewable independently of its parent.
2. **Recursive merge groups** — a tree of MGs that mirrors the work hierarchy
   without forcing a flat blob at the root.
3. **Per-item review** — every implementable item gets its own PR so reviewers
   can attribute changes to a specific work item.
4. **Cross-platform** — the same branch model on GitHub and ADO. Different PR
   APIs, identical branch tree.
5. **Resume safety** — re-running `polyphony-full` against the same root must
   re-derive identical branch names so existing PRs are continued, not
   duplicated.
6. **Phase 7 cross-item edges** — the branch model must not block the
   tree-walker's edge graph from expressing item-to-item dependencies across
   the tree.
7. **Trunk flow as default** — plans and implementations interleave; the
   branch model must support partial-tree promotion (some MGs done, others
   in flight).

## Decision

### Branch tree (canonical shape)

```
main
 └── feature/{root_id}                          ← single integration trunk
      │   plus committed run manifest at .polyphony/run.yaml (see §Run manifest)
      │
      ├── plan/{root_id}                        ← root-level plan branch
      │    └── plan/{root_id}-{item_id}         ← descendant plan branch (recursive;
      │                                            base branch = parent's plan branch
      │                                            captures the hierarchy)
      │
      ├── mg/{root_id}_{mg_id}                       ← top-level merge group
      │    │                                            (mg_path = mg_id for top-level)
      │    ├── impl/{root_id}-{item_id}              ← impl branch for an item
      │    │                                            inside this MG (always exists
      │    │                                            for every implementable item,
      │    │                                            leaf or non-leaf; base branch =
      │    │                                            enclosing MG)
      │    └── mg/{root_id}_{mg_id}_{nested_mg_id}   ← nested merge group; nested_mg_id
      │         │                                       is planner-declared, or defaults
      │         │                                       to item-{child_id}; mg_path
      │         │                                       segments joined by `_`
      │         ├── impl/{root_id}-{owner_item_id}   ← non-leaf owner's own impl branch
      │         └── impl/{root_id}-{descendant_id}   ← descendant tasks
      │
      └── evidence/{root_id}-{item_id}          ← evidence branches (Phase 6)
```

### Naming rules

| Branch kind | Format | Notes |
|---|---|---|
| Feature | `feature/{root_id}` | One per `polyphony-full` run. Base for everything else. Requires a same-root run lock — see § Concurrent-run lock. |
| Plan (root) | `plan/{root_id}` | Branches from `feature/{root_id}`. |
| Plan (descendant) | `plan/{root_id}-{item_id}` | `item_id` is the **leaf** work-item id only (the item this plan covers). The hierarchy is captured by the base branch — a descendant plan branches from its parent's plan branch. Work-item IDs are project-unique so leaf-only naming is collision-free. |
| Merge group (top) | `mg/{root_id}_{mg_id}` | `mg_id` is a **stable planner-declared id** (see § MG identity). Branches from `feature/{root_id}`. |
| Merge group (nested) | `mg/{root_id}_{mg_path}` | Recursive. `mg_path` is the **`_`-joined chain of `mg_id` segments** from root to current (top-level: `mg_path = mg_id`; nested: `mg_path = parent_mg_id_…_nested_mg_id`). The terminal segment is **planner-declared in the parent's plan**, or defaults to `item-{child_id}` when the planner is silent. Each segment must match `^[a-z][a-z0-9-]{0,30}$`; the `_` delimiter is unambiguous because that regex excludes `_`. Branches from the parent MG. See § Nested MG id. |
| Task | `impl/{root_id}-{item_id}` | **Flat** — `item_id` alone (project-unique). **Always exists** for every implementable item (leaf or non-leaf). Branches from the closest enclosing MG; the base branch records the topology. |
| Evidence | `evidence/{root_id}-{item_id}` | Branches from `feature/{root_id}`. Phase 6. |

#### Delimiter rules

Three delimiters, each with a single role:

- **`/`** — separates the ref-class prefix from the payload. Reserved.
  Never used as an ID hierarchy delimiter (git treats `/` as ref namespace
  and a ref `mg/X` precludes `mg/X/Y`).
- **`-`** — separates the numeric `{root_id}` from a single payload segment.
  Used in `impl/`, `plan/`, `evidence/`, and as the segment-internal
  character of MG ids themselves (e.g. `data-layer`).
- **`_`** — the **MG hierarchy delimiter**. Appears only inside `mg/` branch
  payloads. Unambiguous because the MG id grammar `^[a-z][a-z0-9-]{0,30}$`
  excludes `_`. Splitting an `mg/` branch payload on `_` yields exactly
  `[root_id, mg_id_1, mg_id_2, …]`.

A single MG branch can therefore carry an unambiguous path of any depth:
`mg/1234_data-layer_migrations_schema` parses uniquely as
root=`1234`, path=`data-layer / migrations / schema`. The Rev 3 grammar
that joined `mg_path` with `-` was demonstrably ambiguous (a top-level MG
named `data-layer-migrations` collided with a nested `migrations` under
`data-layer`); see § Alternatives considered.

### Promote chain

| Layer | Head | Base | Merge mode policy key | Merge method |
|---|---|---|---|---|
| Impl PR | `impl/{r}-{item_id}` | enclosing `mg/{r}_{mg_path}` | `(scope, impl_pr)` | squash OR merge — operator choice |
| Nested MG PR | `mg/{r}_{mg_path}` (terminal segment is the nested id) | `mg/{r}_{parent_mg_path}` | `(scope, mg_pr)` | **merge commit** (mandatory) |
| Top MG PR | `mg/{r}_{mg_id}` | `feature/{r}` | `(scope, mg_pr)` | **merge commit** (mandatory) |
| Plan PR (descendant) | `plan/{r}-{item_id}` | parent's plan branch | `(scope, plan_pr)` | **merge commit** (mandatory) |
| Plan PR (root) | `plan/{r}` | `feature/{r}` | `(scope, plan_pr)` | **merge commit** (mandatory) |
| Evidence PR | `evidence/{r}-{i}` | `feature/{r}` | `(scope, evidence_pr)` | squash OR merge — operator choice |
| Feature PR | `feature/{r}` | `main` | `(scope, feature_pr)` | operator choice |

The driver — **not git** — enforces ordering. A parent MG PR's merge
gate refuses to fire until every constituent child impl PR and every nested
MG PR is satisfied (see § Promotion gating). Git ancestry is *evidence* the
gate consults; it is not the enforcement mechanism. (Rev 1 incorrectly
claimed git itself enforces this.)

### Promotion gating (driver-enforced)

The driver opens a PR's merge action only when the corresponding requirement
state is satisfied. Specifically:

| PR kind | Driver gate |
|---|---|
| Impl PR | `implementation_merged` for the impl's owning item is `ready` and the impl PR has aggregated platform status `approved` per `(scope, impl_pr)` policy. |
| Nested MG PR | All impl PRs and grandchild MG PRs in this MG have merged. |
| Top MG PR | All impl PRs and nested MG PRs in this MG have merged. |
| Plan PR | All child plan PRs (if any) have merged AND `plan_reviewed` for the plan's owning item is `ready`. |
| Evidence PR | `action_satisfied` for the evidence's owning item is `ready` and the evidence artifact passes verification. |
| Feature PR | All top MG PRs, root plan PR, and all evidence PRs are merged AND every in-scope item's full requirement set is `satisfied`. |

The branch ancestry produced by these merges is observable, but the *truth*
of "this MG is promoted" is the PR-state record (PR is merged into expected
base), not `git merge-base --is-ancestor`. This makes the model robust to
squash merges at the impl-PR layer.

> **Why merge commits at the promote-chain layers above impl PR.** Plan and
> MG promotions need to be *reviewable as integration events* — reviewers
> ask "what came in when we promoted MG-2?", and a merge commit on
> `feature/{r}` cleanly answers it. Squash at those layers would
> destroy integration-event boundaries. Squash is fine at the impl-PR layer
> because the impl PR *is* the integration event; squashing it produces a
> single attributable commit on the MG branch, exactly what bisect wants.

### MG identity — stable planner-declared id

The planner declares each merge group with a **stable id**:

```yaml
merge_groups:
  - id: data-layer       # stable id, becomes the branch segment
    order: 10            # presentation/dispatch order; can change freely
    intent: "data layer + migration"
    items: [101, 102, 103]
    isolation: per-merge-group
  - id: ui-surface
    order: 20
    intent: "ui surface + e2e tests"
    items: [104, 105]
    isolation: per-item
```

- `id`: lowercase-kebab string, must match `^[a-z][a-z0-9-]{0,30}$`. Becomes
  the terminal `{mg_id}` segment of the branch name (`mg/{r}_data-layer`).
  The `_` between root and id is the MG hierarchy delimiter — see
  § Delimiter rules.
- `order`: integer; controls dispatch order. Changing it is **not** a
  topology change — branches don't move.
- `items` and `intent`: descriptive; can change with planner-PR review.

#### Identity rules

1. **`id` is immutable** once any branch under that MG exists. Driver refuses
   to materialize a different MG under an existing `id`.
2. **`id` is unique within the parent MG's children** (sibling MGs cannot
   share id; nested MGs cannot share id with a sibling at the same level).
3. **Reordering is free** — swap `order: 10` and `order: 20` between two
   MGs and nothing renames. The dispatch order changes; the branch tree
   doesn't.
4. **Removing a materialized MG requires an explicit retirement gate** —
   the driver raises a `human_gate` rather than dropping branches silently.
   See § Open questions for the retirement-gate sub-spec.
5. **A removed `id` is reserved** — the planner cannot reuse a retired
   `id` for a different MG within the same root.

#### Why structural ids and not list positions

Position-based numbering (`mg-1`, `mg-2`) cannot survive a planner reorder
or removal without renaming branches mid-flight. Stable ids let the
planner edit the plan freely while branches remain pinned to logical
identity.

### Nested-MG trigger

A child item gets a **nested MG** when **both** are true:

1. Child carries the `implementable` facet.
2. Child is `decomposable: true`.

That is the entire trigger. It does **not** depend on whether implementable
descendants currently exist or are complete — those are runtime properties
that can flip; the topology must not.

#### Override

The parent's plan can flatten a child explicitly, or name the nested MG that
the trigger will produce:

```yaml
children_overrides:
  - id: 4567
    merge_group_nesting: flat   # force impl branch even though trigger fires
    reason: "single-file change, nesting overhead not worth it"

  - id: 4568
    nested_mg_id: data-migrations   # name the nested MG explicitly
    reason: "groups with the data-migrations PR ancestry on review side"
```

`merge_group_nesting: flat` and `nested_mg_id` are mutually exclusive on a
given child. The driver writes a one-line warning to the parent plan PR
explaining each override. Use sparingly.

#### Default-nest rationale

Rev 1 conditioned nesting on "has implementable descendants in scope," which
flips topology mid-flight when child planning produces new descendants. Rev 3
defaults to nest for any decomposable+implementable child so the topology is
fixed at the moment the parent plan PR merges. This may produce empty MG
branches for items that never grow descendants; that's a small cost for
topology stability.

### Nested MG id — source rule

When the nesting trigger fires for child item `{child_id}`, the resulting
nested MG needs a **stable identifier** that matches the same
`^[a-z][a-z0-9-]{0,30}$` regex MG ids obey. The source is determined in this
order:

1. **Planner-declared.** The parent's plan may declare the nested MG id via
   `children_overrides[].nested_mg_id` (see § Override above). Preferred when
   the nested integration scope has meaningful semantics (`data-migrations`,
   `auth-rewrite`).
2. **Default.** If the planner is silent, the driver derives
   `nested_mg_id = item-{child_id}` (e.g. `item-4567`). The literal `item-`
   prefix is required so the derived form starts with a lowercase letter and
   is unambiguous in branch listings.

#### Why both options matter

- **Planner-declared** lets reviewers see meaningful nested-MG names in PR
  titles, branch listings, and the run manifest.
- **Default `item-{child_id}`** keeps the system deterministic when the
  planner doesn't bother — most nested MGs auto-named on first run, and
  planner can rename in a follow-up plan PR (which counts as a topology
  change; see § Identity rules and § Run manifest).

#### Identity rules apply

The Identity rules above (§ Identity rules) cover both forms:

- A planner-declared `nested_mg_id` is immutable once any branch under that
  nested MG exists.
- A default `item-{child_id}` is also immutable once materialized — even
  though it's "auto-derived," renaming it after materialization is a
  topology change requiring the retirement gate.
- A retired `nested_mg_id` (planner-declared or default) cannot be reused
  for a different MG within the same root.

#### Hashing and manifest

The nested `mg_id` is recorded in the run manifest's `merge_groups[]` entries
with its full `mg_path`, and contributes to the topology hash (§ Run manifest).
That means whichever source produced the id, the hash treats them
identically — no special-casing of "planner-declared vs default" in the hash
input.

### Implementable non-leaf items — own impl PR

An item that is **both implementable and decomposable** (so it owns a nested
MG) **also gets its own impl branch** under that nested MG. Its own
implementation merges via that impl PR alongside its descendants' impl PRs.

```
mg/{r}_{parent_mg_path}_{nested_mg_id}   ← child's nested MG branch
                                            nested_mg_id is planner-declared
                                            or item-{child_id}
 ├── impl/{r}-{child_id}                   ← child's OWN impl branch
 ├── impl/{r}-{descendant_a_id}            ← descendant tasks (flat names;
 └── impl/{r}-{descendant_b_id}              base = enclosing MG branch)
```

This satisfies the requirement model: `RequirementKind.ImplementationMerged`
applies to *every* implementable item, leaf or not. Without an own-task
branch, the requirement would have nowhere to land. Direct commits to the
MG branch were considered and rejected — they break per-item review
attribution.

### Sibling-MG creation

A parent can have **multiple top-level MGs** when the planner declares them.
Each entry in the `merge_groups:` list becomes one `mg/{r}_{id}` (see
§ MG identity).

#### Default when planner is silent

```yaml
merge_groups:
  - id: default
    order: 0
    items: <all implementable descendants>
    isolation: per-merge-group
```

Reviewers see this default in the plan PR and can request the planner to split.

### Branch-name length cap & nesting depth — operational smell gates

Calculated worst-case branch names easily fit ADO/git limits. Under the
Rev 4 grammar, impl/plan/evidence names are bounded — one root id plus
one item id (`impl/99999999-99999999`, ~25 chars). MG names grow with
depth (`mg/99999999_data-layer_ui-surface_feature-x_area-9_bugfix-99` at
depth 5, well under 200 chars). The cap is **not** about names; it's
about reviewer cognitive load, edge-graph complexity, and PR-chain
rebase propagation.

Rules:

- **Depth 3 (`mg-a-b-c`)**: warning emitted on materialization. Plan PR
  carries a banner.
- **Depth 5 (`mg-a-b-c-d-e`)**: hard stop. Driver refuses with a clear
  error pointing to this ADR.
- Override beyond depth 5 requires an explicit `--allow-deep-nesting` flag
  to `polyphony-full` and a recorded human approval in the run manifest.

If a planner regularly hits depth 3+, the work hierarchy is the smell —
restructure with sibling MGs at a shallower level instead.

### Cross-sibling code dependencies

Pure ordering ("Y waits for X to be satisfied") is a worklist concern,
solved entirely by Phase 7's edge graph. **Code dependencies** are
different — Y's branch must contain X's code to compile or test against it.

The planner declares dependency kind per cross-MG edge:

```yaml
cross_item_edges:
  - from_item: 101         # X
    to_item: 205           # Y
    kind: ordering_only    # default
  - from_item: 102
    to_item: 206
    kind: code_dependency  # Y consumes X's code
```

For `code_dependency` edges that cross sibling MG boundaries, the driver
requires one of:

1. **Same-MG remedy** (preferred): planner repacks the items into the same
   MG. No cross-branch import needed.
2. **Promote-and-rebase remedy**: upstream MG promotes to the common
   ancestor (`feature/{r}` for top-level siblings); downstream MG rebases
   onto the promoted ancestor before dispatching the dependent item.
   Driver schedules the rebase automatically; it is recorded in the run
   manifest for auditability.
3. **Replan remedy**: driver raises a `human_gate` proposing the
   restructure, defers dispatch.

`ordering_only` edges remain pure worklist ordering and require nothing
of the branch model.

#### Materialization gate on promote-and-rebase

Auto-rebase under remedy 2 is only safe **before the downstream MG branch
or any of its impl branches are materialized.** Once downstream work has
landed, rebasing the downstream MG branch invalidates SHAs that may already
underpin open impl PRs and reviewer comments.

Rule: the driver only auto-rebases when

- the downstream `mg/{r}_{downstream_mg_path}` branch does not yet exist, **or**
- it exists but has no descendant task or nested-MG branches.

When downstream branches **are** materialized:

1. Mark the downstream MG branch (and its impl PRs) `stale: cross_mg_code_dep`
   in the run manifest.
2. Post a comment on each affected open PR explaining why.
3. Apply the `(scope, cross_mg_code_dep_rebase)` policy:
   - `auto` → driver schedules an audited rebase of the downstream MG
     branch and re-bases each open impl PR onto the rebased MG branch,
     recording every commit in the run manifest.
   - `warning` → driver schedules the rebase, posts the warning, requires
     reviewer acknowledgement on each affected PR before merging.
   - `manual` → driver opens a `human_gate`; no rebase happens
     automatically.

The same materialization gate applies recursively if upstream itself has
nested MGs that would need to promote further. The driver chooses the
lowest common ancestor that has not yet materialized downstream branches
where possible; otherwise it falls back to `human_gate`.

### Renegotiation flow (child plan changing parent plan)

`plan/{r}-{c}` branches **from** the parent's plan branch (root or
ancestor descendant), so the child plan branch can include modifications
to parent plan files in its diff naturally. Branch-name flatness does
not affect this property — the parentage is encoded in the PR base.

Child plan document declares:

```yaml
requests_parent_change: true
parent_plan_generation: 2     # generation of the immediate parent at branch creation
ancestor_plan_generations:    # all ancestor generations at branch creation
  root: 3
  100: 2                      # immediate parent
  # … any other ancestors above 100 …
parent_change_summary:
  - "Adjusted parent's MG split: data-layer → data-layer + data-migrations"
  - "Added new actionable child {item_id}"
```

#### Concurrency rules

When multiple child plan branches each carry parent edits:

1. The driver acquires a **parent-plan write lock** before merging any
   parent-affecting child plan PR.
2. The first child to merge bumps the parent's `plan_generation` counter.
3. Other in-flight parent-affecting child PRs become **stale** (their
   recorded `parent_plan_generation` no longer matches).
4. Stale child PRs are not auto-rebased silently. The driver:
   - blocks merge,
   - posts a comment on the stale PR,
   - either schedules an auditable rebase (recording the rebase commit in
     the run manifest) or surfaces a `human_gate` per
     `(scope, child_plan_rebase)` policy.

Pure non-parent-affecting child plan PRs are unaffected by the lock.

#### Ancestor-cascade staleness

A child plan PR may appear fresh against its **immediate** parent yet be
stale against an **ancestor** further up the chain. The driver enforces
ancestor cascade:

1. Whenever any ancestor's `plan_generation` is bumped (root, grandparent,
   great-grandparent…), the driver walks the descendant tree of in-flight
   plan branches.
2. Every descendant plan PR whose `ancestor_plan_generations[ancestor_id]`
   no longer matches the manifest's current value is marked
   `stale: ancestor_plan_drift`.
3. The same block + comment + policy-driven rebase rule from the immediate-
   parent case applies, keyed on
   `(scope, ancestor_plan_rebase)` — separate policy from
   `child_plan_rebase` because the remedy is harder (the diff to integrate
   may be ancestors removed).
4. When a stale-by-ancestor PR is rebased, its
   `ancestor_plan_generations[]` map is updated to the manifest's current
   values for **all** ancestors on its chain, not just the one that bumped.

This prevents the silent drift case the rubber-duck flagged: grandparent
renegotiates, child plan PR still references the old grandparent topology,
PR merges anyway, descendants are stale against ancestor reality.

#### Hard rule

Any parent-affecting change without `requests_parent_change: true` is a
review finding. The driver flags it; reviewers must reject.

#### Reopened parent plan after merge

If a parent plan PR has already merged and a child later requests parent
changes, the driver:

1. Bumps `parent_plan_generation`.
2. Opens a new parent plan PR from the same `plan/{r}` or
   `plan/{r}-{parent_item_id}` branch (with the child-proposed parent
   edits cherry-picked or merged in).
3. Marks any in-flight sibling child plan PRs as stale per the rule above.

### Isolation-scope ↔ branch contract

| Isolation | Worktree | Impl branches | Impl PRs |
|---|---|---|---|
| `per-merge-group` (default) | One per MG; agents serialize within the MG | Created in the MG worktree, one per item | Always opened |
| `per-item` | One per implementable item | Created in the per-item worktree | Always opened |

**Impl PRs always exist.** Isolation only changes *where* the agent does the
work; the per-item review surface (the impl PR) is non-negotiable. A
pseudo-isolation mode that writes straight to the MG branch was considered
and rejected — it loses per-item review attribution and breaks the
"reviewer can comment on this item's diff" property.

The glossary's `per-merge-group` entry implies "items already commit to the
MG branch; integration step is a no-op." That predates this ADR and
must be updated as part of Phase 4b's vocabulary clean-up. New language:
"per-merge-group serializes items in a single worktree; each item still
opens its own impl PR; the integration step is a sequence of impl-PR
merges into the MG branch."

### Run manifest + concurrent-run lock

Every run commits a **run manifest** at `.polyphony/run.yaml` on the
feature branch on first push:

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

# Top-level field. Hashed input is canonicalized in §Topology hash inputs.
topology_hash: sha256:abc123…

merge_groups:
  - id: data-layer
    mg_path: data-layer            # `_`-joined chain from root; top-level => single segment
    parent_mg_path: null           # null => top-level under feature/
    items: [101, 102]
    nesting: top
    isolation: per-merge-group
    nesting_override: null         # planner override if any (flat | nested_mg_id)
  - id: ui-surface
    mg_path: ui-surface
    parent_mg_path: null
    items: [103]
    nesting: top
    isolation: per-merge-group
    nesting_override: null
  - id: item-4567                  # default-derived nested MG id
    mg_path: data-layer_item-4567  # `_`-joined: data-layer / item-4567
    parent_mg_path: data-layer
    items: [4567, 4571, 4572]
    nesting: nested
    isolation: per-merge-group
    nesting_override: null

# Recorded rebase events for auditability (cross-MG code-dep, child-plan, etc.)
rebases:
  - branch: mg/1234_data-layer
    onto: feature/1234
    reason: cross_mg_code_dep
    commit: 0b1f3e9
    recorded_at: 2026-05-06T18:00:00Z
  - branch: plan/1234-101
    onto: plan/1234-100
    reason: child_plan_drift
    commit: 7c4e2a1
    recorded_at: 2026-05-06T19:15:00Z

# Recorded human-gate approvals (deep nesting, retirement, force overrides)
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

# Idempotency ledger for plan-PR merges. Each entry records which PR's
# merge caused which plan_generations bump so retries are safe and
# multi-bump bugs cannot silently happen. Operational/audit only — NOT
# part of the topology hash.
merged_plan_prs:
  - pr_number: 42
    item_key: "100"          # "root" or numeric id-as-string (matches plan_generations key shape)
    merge_commit: 7c4e2a1
    previous_generation: 1
    current_generation: 2
    recorded_at: 2026-05-06T19:30:00Z
```

The shape above is **normative** for `schema: 1`.

**Field-addition policy.** Hashed fields (those participating in
`topology_hash` per § Topology hash inputs) are frozen for `schema: 1`;
adding or modifying a hashed field requires bumping `schema:`. Adding a
new **operational/audit** field (e.g. `merged_plan_prs`, future ledgers)
at the same schema is permitted iff: (a) the field is not part of the
topology hash, (b) readers tolerate its absence (default to empty), and
(c) the addition is documented here. This keeps schema bumps reserved
for genuine wire-incompatible changes.

#### Topology hash inputs

The `topology_hash` is a SHA-256 over the **canonicalized** sequence of
records below — not the YAML text. Canonicalization rules:

1. Each MG contributes one record:
   `(mg_path, items_sorted_asc, isolation, nesting_override_or_null)`.
2. Records are sorted by `mg_path` (lexicographic).
3. Within each record, `items` are sorted ascending.
4. Each record is serialized as a tab-separated UTF-8 line:
   `mg_path\titems_csv\tisolation\tnesting_override\n`
   where `mg_path` is the canonical `_`-joined segment chain (the
   manifest's `mg_path` field verbatim), `items_csv` is comma-joined
   sorted item ids, and `nesting_override` is the literal string `null`
   when absent or the override value otherwise.
5. The full canonical text is the concatenation of all lines.
6. The hash is `sha256(canonical_text)` and stored as `sha256:{hex}`.

Including `mg_path` (not just `id`) is what closes the rubber-duck blocker:
two MGs with the same terminal `id` segment under different parents produce
different `mg_path` values, so the hash distinguishes them. `parent_mg_path`
is implicit in `mg_path` (drop the trailing segment) and is therefore not
duplicated in the hash input. Because `_` is excluded from the MG id grammar,
`mg_path` admits one and only one segmentation, so the canonical text is
unambiguous.

`plan_generations`, `rebases`, `human_approvals`,
`retired_merge_group_ids`, and `merged_plan_prs` are **not** part of
the topology hash — they are historical/operational state, not topology.
Topology changes are about which MGs exist, what items they own, how
they nest, and how they are isolated.

#### Plan-merge idempotency ledger

`merged_plan_prs` is the **idempotency ledger** for plan-PR merges. Each
entry records:

- `pr_number` — the platform PR number that was merged.
- `item_key` — the plan key whose generation was bumped (`"root"` or
  numeric id as string).
- `merge_commit` — the platform-reported merge commit SHA.
- `previous_generation`, `current_generation` — the values written.
- `recorded_at` — UTC timestamp.

The `polyphony manifest record-plan-merge` verb consults the ledger
**before** mutating `plan_generations`:

1. If a ledger entry already exists for `pr_number` with the same
   `item_key` and (when both sides have it) matching `merge_commit`,
   the verb returns idempotent success without bumping. This is the
   safe re-entry path.
2. If a ledger entry exists for `pr_number` with conflicting `item_key`
   or `merge_commit`, the verb fails loudly — the same PR cannot be
   recorded against two different items.
3. If no ledger entry exists, the verb bumps `plan_generations` by 1
   and appends a new entry.

Without this ledger, `plan_generations[item_key] += 1` arithmetic alone
cannot distinguish "this PR was already recorded" from "another plan PR
for the same item correctly bumped the generation," and partial-failure
retries can silently over-bump the counter — which would corrupt the
ancestor-staleness math (§ Plan-PR ancestor-cascade staleness).

Legacy callers that omit `pr_number` skip the ledger and bump
unconditionally; this preserves the original behavior for callers that
have not yet adopted the ledger and is intended for transitional use
only. Production callers (the `polyphony pr merge-plan-pr` verb) MUST
pass `pr_number` and `merge_commit`.

#### Resume rules

The manifest is the **source of truth for resume**. On any
`polyphony-full <root>` invocation:

1. Driver acquires a lock keyed on `(repo, platform_project, root_id)`.
2. If `feature/{root_id}` exists and the run manifest is present:
   - Compare current planner output's topology hash to the recorded one.
   - **Same hash** → resume the existing run (re-detect requirement state,
     dispatch what's `ready`).
   - **Different hash + no branches materialized for the diff** → accept
     the new topology, update the manifest.
   - **Different hash + branches materialized for the diff** → enter the
     plan-revision/migration gate (`human_gate`); do not silently rename.
3. If no `feature/{root_id}` exists → start a fresh run; commit a new
   manifest.
4. **Concurrent runs of the same root are refused** — the lock holds for
   the duration of the run. Operator UX (FYI from the Rev 2 critique):
   distinguish *attach to the existing controller* from *start a second
   driver*. The lock-held message is:
   *"A run for root 1234 is already in flight (held by {host}, started at
   {ts}, pid {pid}). Attach to live controller? Wait for completion?
   Abort and force-release? Force-release requires manual cleanup."*
   "Attach" is read-only telemetry; it does not bypass the lock.

The lock implementation is a Phase 4b detail. Reasonable options:
- A row in the SQLite cache twig already maintains.
- A heartbeat file on the feature branch.
- An advisory file lock on a sentinel path.

### Phase 7 cross-item edges — how they map to branches

Phase 7 introduces the dependency-edge graph that orders the worklist.
Most edges are **runtime ordering** concerns, not branch topology
concerns. Branches still look exactly as defined here.

The branch model's contributions to Phase 7:

- **Definitional edges from PR state.** The driver translates "MG X
  promoted" into "MG X's PR is merged into its base" — observable from
  PR metadata. The `git merge-base --is-ancestor` check is corroborating
  evidence, not the canonical signal.
- **Code-dependency edges across sibling MGs** require the rebase remedy
  (see § Cross-sibling code dependencies) — branches stay where they are,
  but content imports across them.
- **Plan-promotion edges.** A child plan PR being merged is exactly the
  moment the parent's `plan_promoted` requirement can satisfy — driven by
  PR state, not branch ancestry.

The branch model contains **no cross-item edges by itself.** Cross-MG and
cross-tree ordering is enforced exclusively by the worklist edge graph;
the branch tree is the *substrate* the edges are computed over, not the
edge encoding.

This separation lets Phase 7 introduce the `polyphony worklist build`
verb without revisiting branch names.

### Trunk flow vs plan-then-implement

Both execution modes use the **same branch tree**. The difference is purely
in the edge graph (Phase 7):

- `trunk_flow` (default): plan PRs and impl PRs interleave; some MGs may be
  in flight while other plans are still being reviewed.
- `plan_then_implement`: every implementable depends on every plannable in
  the tree being satisfied. The driver waits for all plan PRs to merge
  before opening any MG.

The branch tree is identical. Only dispatch order changes.

## Alternatives considered

### Flat MG (status quo)

One branch per execution group, no nesting, no impl branches.

- ✗ No per-item review.
- ✗ No way for nested decomposables to claim their own integration scope.
- ✗ Agents committing directly to the MG branch makes "who did what"
  forensic analysis painful.

Rejected as the starting point of this whole redesign.

### One branch per item, no MG aggregation

Skip MGs; each item has its own branch and PR; merges go straight to feature.

- ✗ No reviewable integration boundary between "this set of changes coheres"
  and "everything in this run."
- ✗ Loses the natural integration event that MG promotion provides.
- ✗ Forces every cross-item dependency into the worklist edge graph with no
  natural git-level boundary.

Rejected as too flat.

### Hyphen-joined `mg_path` (Rev 3 form, superseded)

Use `-` as both the in-id character and the MG hierarchy delimiter, so an
MG branch is `mg/{r}-{mg_path}` with `mg_path` joined by `-`.

- ✗ **Real collision bug**, not just human ambiguity. Because MG ids
  themselves contain `-` (e.g. `data-layer`), the branch
  `mg/1234-data-layer-migrations` admits two distinct topology
  interpretations:
  - top-level MG with id `data-layer-migrations`, OR
  - nested MG `migrations` under `data-layer`.
  Two distinct topologies can produce the same branch ref. Topology hash
  inherits the ambiguity.
- ✗ Equally bad at depth 3+ even when ids are parent-local: two parents
  in different subtrees can share a parent id (`payments/api/migrations`
  and `identity/api/migrations` both flatten to
  `mg/1234-api-migrations`).

Rejected in Rev 4. Keep `-` for in-id and root-payload separation; use
`_` (excluded from the MG id alphabet) as the MG hierarchy delimiter.
See § Delimiter rules.

### Path-encoded task and plan branches

Mirror the MG path structure on task and plan branches:
`impl/{r}_{mg_path}_{item_id}` and `plan/{r}_{plan_path}`.

- ✓ Topology readable from branch listing.
- ✗ Branch names grow long (`impl/1234_data-layer_migrations_schema_5678`)
  for a property already captured by the **base branch** of the PR
  (`impl/{r}-{i}` based on `mg/{r}_{mg_path}` is unambiguous about which
  MG owns the item).
- ✗ Work item IDs are project-unique already, so adding the path is
  redundant for identity.

Rejected at the task and plan layers in Rev 4. Kept at the MG layer
because integration boundaries are where reviewers most need topology
visible without consulting the manifest. The hybrid is documented in
§ Naming rules.

### Fully flat MG names (`mg/{r}-{mg_id}` with global uniqueness)

Drop topology from MG branches too; require `mg_id` to be globally
unique under the root.

- ✓ Shortest names; cleanest prefix workflows.
- ✗ Forces planners to invent globally unique names for nested MGs that
  would otherwise be locally meaningful (`migrations` under `auth`
  vs. `migrations` under `data-layer`). Generic names become
  unavailable for nested scopes.
- ✗ Topology becomes legible only via the manifest, which raises the
  human cost of debugging mid-run failures (which MG owns this branch?
  what's its parent?).
- ✗ Retirement and ancestor-cascade reasoning shifts from
  prefix-based to manifest-graph-based, requiring tooling that doesn't
  yet exist.

Rejected in Rev 4. The MG branch is the surface where humans most
benefit from path encoding; flatness pays off most where ID alone is
unambiguous (task, plan, evidence).

### Branch-name hierarchy with `/` separators (e.g. `mg/123/data/migrations`)

Use `/` instead of `-` between hierarchy levels.

- ✗ Git treats `/` as ref namespacing. `mg/123/data` and
  `mg/123/data/migrations` cannot coexist (ref directory vs ref file
  conflict). Common pitfall.

Rejected. We use `_` as the MG hierarchy delimiter and `-` as the
root-payload separator everywhere else; `/` is reserved for the single
ref-class prefix (`mg/`, `plan/`, `impl/`, `feature/`, `evidence/`).

### Position-based MG numbering (Rev 1)

Use the 1-indexed position in the planner's `merge_groups:` list as the
branch segment.

- ✗ Reordering renames branches → breaks resume.
- ✗ Removal-with-gap conflicts with "stable identifier" promise.
- ✗ Insertion in the middle silently re-points existing branch names to
  different logical MGs.

Rejected. Identity is the planner-declared `id`, not a list position.

### "Git enforces ordering" (Rev 1)

Trust git ancestry to refuse a parent PR merge while child PRs are open.

- ✗ False. Git doesn't know about task→MG→feature semantics. A parent MG
  PR can be mergeable on `feature/{r}` even with open impl PRs; nothing
  in core git stops the merge button.
- ✗ Branch protection rules can be configured to enforce some of this,
  but require active configuration on every repo and can't express
  "all impl PRs in this MG must be merged" without polyphony's
  knowledge.

Rejected. The driver enforces ordering via PR-state gates; git ancestry
is observed evidence, not the enforcement mechanism.

### Squash merges everywhere (Rev 1 implicit)

Squash all PR layers including MG and plan promotions.

- ✗ Squash on MG/plan promotion destroys the integration-event boundary
  reviewers need to ask "what came in when we promoted MG-data-layer?"
- ✗ Breaks any code path that uses `git merge-base --is-ancestor` as
  satisfaction evidence (because the source branch head is no longer an
  ancestor of the target).

Rejected at the MG/plan layers; allowed at the impl-PR and evidence-PR
layers where the PR *is* the integration event.

### Separate "renegotiation" branch type for parent-change requests

A `renegotiate/{r}-{c}` branch that targets the parent plan branch.

- ✗ Duplicates merge machinery for no extra expressive power.
- ✗ Reviewers learn two paths instead of one.
- ✗ Doesn't reuse git's natural "child branch can include parent edits"
  property.

Rejected. Use the existing child plan branch with an explicit flag in
the plan document plus the parent-plan-generation lock.

### Policy-driven nesting trigger

Let `polyphony policy resolve (scope, mg_nesting)` decide whether a child
gets a nested MG.

- ✗ Two operators with different policies would produce different branch
  trees from the same work hierarchy.
- ✗ Resume across policy edits becomes ambiguous: does the in-flight MG
  obey the old or new policy?
- ✗ Branch topology is structural, not behavioral.

Rejected. The trigger is structural (decomposable + implementable).
Policy controls *behavior on the branches* (merge mode, review timeout),
not *which branches exist*.

## Consequences

### Easier

- Per-item review attribution: every implementable item has a impl PR; all
  reviewer comments anchor to the item that produced the change.
- Resume safety: branch names are deterministic functions of (root,
  planner-declared MG ids) and the run manifest is the canonical resume
  key.
- Cross-platform: same branches on GitHub and ADO; only the PR API differs.
- Bisect: MG/plan merge commits produce clean integration-event boundaries
  on `feature/{r}`; impl-PR squashes give attributable per-item commits on
  the MG branch.
- Concurrency safety: single-root run lock prevents accidental double-runs.

### Harder

- **More branches.** A run with 3 MGs of 4 tasks each produces 1 + 1 + 3 + 12 = 17
  branches. Cleanup matters: the close-out step deletes `impl/`, `mg/`,
  `plan/` branches after the feature PR merges; `evidence/` branches
  retain by default (audit trail) — see § Open questions.
- **More PRs to review.** Reviewers see impl PRs, MG PRs, plan PRs, the
  feature PR. Mitigated by per-PR-kind merge mode policy: routine impl PRs
  default to `auto` merge; the human attention focuses on plan PRs and the
  feature PR.
- **Manifest discipline.** The driver must read/write `.polyphony/run.yaml`
  on every dispatch decision. Conflict resolution if two operators each
  edit the manifest concurrently is the same answer as the run lock — only
  one run holds the lock.
- **Sibling code-dependency rebases.** Cross-sibling MG code edges trigger
  rebases of the downstream MG. Reviewers see the rebase commit; PR diff
  may grow.

### Revisit when

- A planner regularly hits the depth-3 warning → revisit whether trees
  that deep are an anti-pattern, or whether the warning threshold is
  too tight.
- Reviewers complain about per-impl PR fatigue → revisit `impl_pr` merge
  mode default (likely `auto` is correct; the impl PR exists for the diff,
  not for human attention by default). If the issue is CI/API/merge-queue
  fatigue, introduce `impl_pr_granularity: per-item | batched` with
  explicit per-MG opt-in.
- A future phase needs cross-tree branch references (e.g., "this implement
  depends on a branch from a different root") → revisit; this ADR
  explicitly scopes branches to a single root.
- Topology hash collisions become a concern (vanishingly unlikely with
  SHA-256 over a small tuple set) → switch to opaque uuid.

## Open questions deferred to implementation

- **Cleanup timing per branch kind.** Defaults proposed:
  - `impl/`, `plan/`, `mg/`: delete on close-out after a configurable
    retention window. Default 7 days post-feature-PR-merge.
  - `evidence/`: **retain by default**. Evidence carries audit weight;
    deletion requires explicit `(scope, evidence_branch_retention)` policy
    set to `delete`.
  Configurable per `(scope, branch_kind)`.
- **Retirement gate for removed MGs.** When the planner removes an MG with
  materialized branches, the driver raises a `human_gate`. Sub-spec for
  the gate (close PRs? cherry-pick to surviving MGs? leave dangling?) is
  Phase 4b.
- **Rebase implementation for cross-sibling code deps.** `polyphony branch
  rebase-mg <mg> --onto <ancestor>` (new verb in Phase 4b). Records the
  rebase commit in the run manifest.
- **Manifest schema evolution.** `branch_model_version: 1` lets us bump
  later. Migration script TBD.
- **Concurrent root runs across orgs.** Today, no — ADO IDs are
  project-scoped. Document the assumption explicitly when ADO multi-project
  support lands.

## Revision history

- **Rev 1** (drafted 2026-05-06, never submitted): position-based MG
  numbering; nested-MG triggered on "has implementable descendants";
  ancestry-based satisfaction; `mg_promoted` requirement kind; no run
  manifest; no concurrent-run lock; squash-everywhere implicit; no
  cross-sibling code-dep rule; non-leaf implementable items had no
  branch surface.
- **Rev 2** (drafted 2026-05-06, superseded): all blocking issues from the
  first design pass addressed. Stable MG ids, structural nested-MG default,
  driver-enforced promotion gates, mandatory merge commits at promote-chain
  layers, alignment with `RequirementKind.ImplementationMerged`,
  `.polyphony/run.yaml` manifest as canonical resume key, single-root run
  lock, depth gates reframed as smell gates with override path,
  parent-plan-generation concurrency control for renegotiation, per-evidence
  retention default, cross-sibling code-dep rebase rule, implementable
  non-leaves get own impl PR.
- **Rev 3** (drafted 2026-05-06, superseded): second design-pass critique
  addressed. Nested MG id source pinned (planner-declared via
  `children_overrides[].nested_mg_id`, default `item-{child_id}`); topology
  hash inputs canonicalized over `(mg_path, items, isolation,
  nesting_override)` with `mg_path` distinguishing same-id MGs under
  different parents; manifest schema additions
  (`mg_path`/`parent_mg_path`/`isolation`/`nesting_override` per MG,
  `rebases[]`, `human_approvals[]`, `retired_merge_group_ids[]`);
  promote-and-rebase materialization gate (auto only before downstream
  branches exist; otherwise stale + policy-driven remedy); ancestor-cascade
  staleness for grandparent renegotiation (`ancestor_plan_generations`
  recorded per child plan branch); operator UX language for run lock
  distinguishes attach vs start.
- **Rev 4** (this document): third design-pass critique addressed branch-name
  grammar. Hyphen-joined `mg_path` was found to admit real branch-ref
  collisions (a top-level MG `data-layer-migrations` collides with a nested
  `migrations` under `data-layer`), since MG ids themselves contain `-`. The
  hybrid resolution adopted: MG branches use `_` as the hierarchy delimiter
  (`mg/{root}_{mg_path}`, where `_` is excluded from the MG id grammar and
  is therefore an unambiguous separator); task, plan, and evidence branches
  flatten to a single leaf id (`impl/{root}-{item_id}`,
  `plan/{root}-{item_id}`, `evidence/{root}-{item_id}`) since work-item IDs
  are already project-unique and the PR base branch records the topology.
  Manifest `mg_path` field is now formally `_`-joined; topology hash
  canonicalization uses the same string. Three new entries added to
  § Alternatives considered (hyphen-joined `mg_path`, path-encoded
  impl/plan, fully flat MG names with global uniqueness).
- **Rev 4.1** (Phase 3 P5a, additive): added the `merged_plan_prs`
  idempotency ledger (operational/audit field, NOT in topology hash).
  Required to make `polyphony pr merge-plan-pr` re-runnable without
  silently double-bumping `plan_generations` when partial failures
  interleave. Field-addition policy clarified: hashed fields are frozen
  for `schema: 1`; additive operational fields with backward-compat
  readers are permitted at the same schema.

## References

- `files/glossary.md` (session — promoted to `docs/glossary.md` in Phase 1)
  for vocabulary definitions of `root`, `merge group`, `decomposable`,
  `facet`, `task`, `plan`. Glossary's `per-merge-group` description needs
  the update noted above as part of Phase 4b vocab cleanup.
- `docs/decisions/versioning-strategy.md` for the ADR format precedent.
- `.github/skills/polyphony-branch-model/SKILL.md` (paired skill — derived
  from this ADR; loaded by planning agents).
- `src/Polyphony/Sdlc/RequirementKind.cs` for the canonical requirement
  kinds. This ADR uses `ImplementationMerged` (not `mg_promoted`) and
  `PlanPromoted` exclusively.
