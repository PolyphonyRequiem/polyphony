# Polyphony PR/Branch Lifecycle — Ubiquitous Language

> Living document. We add/refine terms as the design solidifies.
> Goal: every term has exactly ONE meaning, and every concept has exactly ONE term.

> **Type-agnosticism rule:** hardcoded process-template type names (Epic / Issue / Task / User Story / Bug / etc.) MUST NOT appear in design docs, plan docs, code, workflow YAML, or polyphony CLI verb names. They are loaded ONLY at runtime from `process-config.yaml`. All polyphony-internal vocabulary uses the relational, structural, facet, scope, and disposition terms below.

## Work item — relational terms

These describe an item's position in the work hierarchy of one run. They are RELATIVE — a `parent` item is itself a `child` of its own parent.

| Term | Definition |
|---|---|
| **Root** | The work item the user passed to the entry-point workflow (`polyphony-full`). Defines the scope of *this run*. **Replaces the previously-used "apex"** — drop "apex" everywhere. |
| **Parent** | The immediate ancestor of the current work item in the work hierarchy. |
| **Current** | The work item the active workflow step is operating on. |
| **Child** | An immediate descendant of the current item. |
| **Descendant / Ancestor** | Transitive parent / child relationships. |
| **Root fallback gate** | Failsafe gate that fires when a sub-workflow is invoked without a `root` passed in (i.e., not via `polyphony-full`). Asks the user whether to treat the current item as root (isolated run) or abort. Configurable to auto-decide via override. |

## Work item — structural terms

INTRINSIC properties: whether an item can have children. Declared by the parent's planner during planning, since structure changes how planning and implementation run.

| Term | Definition |
|---|---|
| **Decomposable** | An item that has, or is permitted to have, child items. **Decomposability is permission, not directive** — a decomposable item declared during planning *may* be decomposed, but doesn't have to be. Decomposability does NOT imply anything about facets. |
| **Leaf** | An item that has no children and is not permitted to. |

## Work item — facet terms

A **facet** is a kind of work an item needs done. An item carries a SET of facets (zero or more). Facets are not mutually exclusive — an item can be plannable AND implementable AND actionable simultaneously, and each facet present triggers its corresponding workflow.

| Term | Definition |
|---|---|
| **Facet** | A kind of work the item carries. Replaces "capability". |
| **Plannable facet** | The item needs decomposition into a plan and (optionally) child items before its other facets can begin. |
| **Implementable facet** | The item carries code changes that land via PRs. |
| **Actionable facet** | The item carries work that is done and recorded as evidence (e.g., infra change, approval, configuration), with or without code. Carries an **executor** property: `polyphony` (polyphony performs the action — evidence required) or `human` (action is outside polyphony's authority — no evidence required, only satisfaction recorded). |
| **Facet set** | The set of facets carried by an item. Loaded from `process-config.yaml` at runtime. |
| **Facet relationships** | Facets are **orthogonal** — they form a set with no inclusion hierarchy. Any combination is valid, including the **empty set** (a pure organizational container that decomposes children but does no own work). The empty set requires `decomposable=true`. |
| **Facet profile** | The tool/MCP/permission addendum bound to a facet. The driver composes the agent's tool addendum by unioning the profiles of the item's facets. Examples: implementable → `{git, build, test}`; actionable → `{telemetry, prod-deploy, teams-comments, service-health}`; plannable → `{search, web, work-item-tree}`. Declared in `process-config.yaml` (configuration) and overridable per item via `guidance`. |
| **Evidence** | The artifact recording satisfaction of an actionable requirement when executed by polyphony. Stored on an evidence branch and promoted via an evidence PR. Evidence inclusion in the feature PR is configurable per item type (default: include). |

## Work item — scope terms

| Term | Definition |
|---|---|
| **Polyphony tag** | A tag stamped on every work item the polyphony pipeline owns. The root is tagged on first run; planning-seeded children are tagged at creation. |
| **In-scope** | An item is in-scope for this run iff it is the root OR a descendant of the root that carries the polyphony tag. |
| **Out-of-scope** | A descendant of the root that does NOT carry the polyphony tag. NOT operated on. Reported in close-out. Does NOT block close-out or the feature PR. |
| **Scope renegotiation** | Mechanism for child planning to surface a request for parent-plan changes (e.g., "this child can't fit the parent's plan; please revise the parent"). The parent plan is not complete until consensus is reached with all child plans that requested changes. |

## Work item — disposition terms

Each item carries a list of REQUIREMENTS it must satisfy before close-out. Each requirement carries a disposition. The aggregate disposition picture replaces the old single-string "phase".

| Term | Definition |
|---|---|
| **Requirement** | A condition the item must satisfy before close (e.g., "plan exists", "plan reviewed", "children seeded", "implementation merged", "evidence accepted"). The set of requirements is derived from the item's facet set. |
| **Disposition** | The state of a single requirement: `needed` (not yet ready to start) → `ready` (preconditions satisfied; can begin) → `fulfilling` (in progress) → `satisfied` (done). |
| **State** | The aggregated disposition picture for an item. Computed from its requirements. **Replaces "phase".** |
| **Acceptance criteria** | The plan-declared, item-specific conditions for `satisfied` on a given requirement. **Standardized term in polyphony prompts.** |

## Execution model

| Term | Definition |
|---|---|
| **Trunk flow** | The architectural default: there is no global "plan everything, then implement everything" phase boundary. The driver dispatches items from the worklist in dependency-edge order; each item's facets fire as their requirements become `ready`. Plan PRs and task PRs interleave naturally. **Replaces the implicit waterfall of plan-then-implement.** |
| **Plan-then-implement** | An optional, conservative `execution_mode` config setting. When enabled, the driver auto-injects edges making every implementable in the tree depend on every plannable in the tree being `satisfied`. Operators who want the waterfall behavior opt in here; the driver itself is unchanged. |
| **Within-item facet order** | Within a single item carrying multiple facets: `plannable` always fires first when present. The order between `actionable` and `implementable` (when both are present) is **declared by the planner per item** (rule **B**). The planner annotates each item with `facet_order: [a, i]` or `facet_order: [i, a]` based on whether the action is preparatory (e.g., provision infra) or follow-up (e.g., audit, telemetry verification). |
| **Successor item** | A separate work item linked by a successor dependency edge to capture an ordering that crosses item boundaries. Used when the within-item facet rule isn't enough — e.g., "deploy to prod" follows the implementation of a feature is its own item with a successor edge from the implementation. |

## Dependency-edge taxonomy

Edges enter the dependency graph through three distinct sources. Each source has a different authority and a different change cadence.

| Source | Authority | Examples |
|---|---|---|
| **Definitional edges** | Wired into the requirement model itself. Cannot be wrong; cannot be overridden. | Within-item: `implementable.ready` requires `plannable.satisfied` if both present. Cross-item: child requirement requires parent to have created the child. Within-item facet order: when planner declares `[a, i]`, `implementable.ready` requires `actionable.satisfied`. |
| **Policy edges** | Defaults declared in `process-config.yaml`; resolved per `(scope, edge_kind)` by `polyphony policy resolve`. Operator-tunable. | Default `plan_gate_for_children`: child.plannable can start at parent's `plan_reviewed`; child.implementable waits for parent's `plan_promoted`. Default `execution_mode: trunk_flow`. Default scope-renegotiation thresholds. |
| **Planner-declared edges** | Emitted by the planner per item; surfaced in the plan document; reviewed via the plan PR. Work-specific. | "Implement A before B (same MG)". "Cross-tree edge: this implementable depends on that other plan being approved." Scope-renegotiation request edges (`requests_parent_change`). Isolation scope per merge group. Within-item facet order (`[a, i]` or `[i, a]`). |

## Plan-gate granularity

| Term | Definition |
|---|---|
| **Plan gate** | The disposition threshold of a parent's plannable facet that unblocks a child's facets. Split by child facet: child.plannable starts at parent's `plan_reviewed` (so children can plan against an approved-but-not-merged parent plan); child.implementable waits for parent's `plan_promoted` (so code is never written against unmerged plans). Configurable per type via policy edges. |

## Work hierarchy & worklist

| Term | Definition |
|---|---|
| **Work hierarchy** | The structural tree of work items (parent ↔ child) for one run. **Distinct from git worktree** to avoid overload. |
| **Worklist** | The driver's queue of in-scope items to dispatch. Ordered by the dependency-edge graph: an item's facet becomes dispatchable when its requirements are `ready`. |
| **Dependency edge** | An ordering constraint: `A depends on B` means B must reach a specified disposition threshold before A's dependent requirement(s) become `ready`. Edges enter the graph from three sources — see *Dependency-edge taxonomy* above. |
| **Git worktree** | A separate working directory bound to a distinct branch (`git worktree add`). NOT the same as work hierarchy. |
| **Isolation scope** | The unit of work that owns a git worktree. Two valid scopes: **per-merge-group** (one worktree per MG; items in the MG share it and serialize) or **per-item** (one worktree per implementable leaf; items run in parallel and merge into the MG branch as a final integration step). The **planner declares the isolation scope per merge group** based on whether items are independent enough to parallelize. **Default: per-merge-group.** Authoritative source: planner-declared edge. |
| **Integration step** | The merge of completed item work into the MG branch. In per-MG mode this is a no-op (items already commit to the MG branch). In per-item mode the driver merges each completed item branch into the MG branch in dependency order. |

## Branches

| Term | Definition | Naming |
|---|---|---|
| **Feature branch** | The single branch that aggregates all work — plans + code + evidence — for the run. Always corresponds to the root item. Merged to `main` via the feature PR. | `feature/{root_id}` |
| **Plan branch** | A branch where plan documents for a single decomposable item are authored, reviewed, and from which approved plans promote up. | `plan/{root_id}` for the root; `plan/{root_id}-{child_id}-…` for descendants (hyphens, not slashes — git ref hierarchy collision rule) |
| **Merge group (MG) branch** | A branch holding the integrated code changes for one merge group. **MG replaces "PG"** — "Processing Group" was originally "Pull Request Group"; "merge group" is clearer. | `mg/{root_id}-{n}` (top level) or `mg/{root_id}-{parent_n}-{n}` (nested) |
| **Task branch** | A branch holding the code changes for a single implementable leaf. | `task/{root_id}-{n}-{item_id}` |
| **Evidence branch** | A branch holding evidence artifacts for an actionable item that requires evidence. | `evidence/{root_id}-{item_id}` |
| **Promote** | A merge whose target is the immediate parent in the branch hierarchy (Task → MG → feature; Sub-MG → MG → feature; Plan(child) → Plan(parent) → feature; Evidence → feature). **Replaces "bubble-up merge".** | Same primitive at every layer. |

## Pull requests

| Term | Definition | Head → Base |
|---|---|---|
| **Task PR** | Promotes one task's code into its parent merge group branch. | `task/{r}-{n}-{t}` → `mg/{r}-{n}` |
| **MG PR** | Promotes one merge group's integrated code into its parent (feature branch or parent MG). | `mg/{r}-{n}` → `feature/{r}` (or `mg/{r}-{parent_n}` if nested) |
| **Plan PR** | Promotes an approved plan up one layer; the root plan promotes into the feature branch. | `plan/{r}-…-{c}` → `plan/{r}-…` (or `plan/{r}` → `feature/{r}`) |
| **Evidence PR** | Promotes an actionable item's evidence into the feature branch. | `evidence/{r}-{i}` → `feature/{r}` |
| **Feature PR** | Promotes the feature branch into `main`. The "ship it" PR. | `feature/{r}` → `main` |
| **PR kind** | One of `task_pr`, `mg_pr`, `plan_pr`, `evidence_pr`, `feature_pr`. The unit on which review/merge policy is configured. | — |

## Reviewers, status, and merge policy

| Term | Definition |
|---|---|
| **Reviewer** | Anyone who can vote/comment on a PR — agent or human. |
| **Status** | A reviewer's verdict on a PR. Polyphony's neutral term. Normalized to `{approved, changes_requested, pending}`. Platform-native vocabulary (GitHub `review state`, ADO `vote`) is used ONLY inside platform-specific sub-workflows. |
| **Aggregated status** | The platform-computed aggregation of all reviewer statuses on a PR, normalized to the same triple. |
| **Merge mode** | Policy governing the transition from "approved" to "merged" for a PR kind. One of: `auto` / `manual` / `warning` / `policy_aware_blocked`. |
| `auto` | Workflow merges as soon as aggregated status is `approved`. |
| `manual` | Workflow surfaces an approved PR to a `human_gate` for explicit merge. |
| `warning` | Workflow merges automatically but emits a warning event with open concerns; humans can intervene asynchronously. |
| `policy_aware_blocked` | Workflow merges only when aggregated status is `approved` AND configured policy guards pass (e.g., CI green, no unresolved threads, required-reviewer set satisfied). |
| **Review policy** | The combination of `{required_reviewers, agent_reviewers, merge_mode}` resolved per `(scope, pr_kind)` by `polyphony policy resolve`. |

## Configuration vs guidance

| Term | Definition |
|---|---|
| **Configuration** | Operator-authored, REQUIRED settings that govern the pipeline's behavior (`process-config.yaml`, `policy.yaml`, `profile.yaml`). Loaded at startup; missing/malformed config fails fast. |
| **Guidance** | Operator-authored, OPTIONAL agent instructions, tool addendums, MCP addendums, and acceptance-criteria templates that augment but do not change the pipeline's structural behavior. Absent guidance falls back to defaults. |

## Open glossary questions

_All initial questions resolved. New questions surfacing during plan drafting will land here._
