# Polyphony PR/Branch Lifecycle — Ubiquitous Language

> Living document. We add/refine terms as the design solidifies.
> Goal: every term has exactly ONE meaning, and every concept has exactly ONE term.

> **Type-agnosticism rule:** hardcoded process-template type names (Epic / Issue / Task / User Story / Bug / etc.) MUST NOT appear in design docs, plan docs, code, workflow YAML, or polyphony CLI verb names. They are loaded ONLY at runtime from `process-config.yaml`. All polyphony-internal vocabulary uses the relational, structural, facet, scope, and disposition terms below.

## Work item — relational terms

These describe an item's position in the work hierarchy of one run. They are RELATIVE — a `parent` item is itself a `child` of its own parent.

| Term | Definition |
|---|---|
| **Root** | The work item the user passed to the entry-point workflow. Defines the scope of *this run*. The term **`apex`** is forbidden — use `root` everywhere. |
| **Tree-walker** | The orchestrator that walks the in-scope tree of the root, builds the worklist, and dispatches items as their requirements become `ready`. Replaces the previous single-item phase router. (Phase 7 deliverable.) |
| **Parent** | The immediate ancestor of the current work item in the work hierarchy. |
| **Current** | The work item the active workflow step is operating on. |
| **Child** | An immediate descendant of the current item. |
| **Descendant / Ancestor** | Transitive parent / child relationships. |
| **Root fallback gate** | Failsafe gate that fires when a sub-workflow is invoked without a `root` passed in. Asks the user whether to treat the current item as root (isolated run) or abort. Configurable to auto-decide via override. |

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
| **Agent addendum** | The composed prompt-context the driver injects into an agent invocation: union of the item's facet profiles (skills + MCPs) plus any per-item guidance. Produced at agent-invocation prep time by the `polyphony agent compose-addendum <work_item_id>` verb (see `.conductor/registry/workflows/actionable.yaml` for the canonical wiring). Skills and MCPs are deduped and sorted ascending under the ordinal comparer; guidance flows through verbatim. |
| **Facet profile composition** | The deterministic operation that unions one or more facet profiles into a single agent addendum. Identical-value collisions across facets dedupe silently; cross-facet name typos are caught at config-load time by the facet-profile validator (V-20 family). Implemented in `Polyphony.Sdlc.FacetProfileComposer.Compose(facets, profiles, perItemGuidance)`; surfaced to workflow YAML as `polyphony agent compose-addendum`. |
| **Evidence** | The artifact recording satisfaction of an actionable requirement when executed by polyphony. Stored on an evidence branch and promoted via an evidence PR. Evidence inclusion in the feature PR is configurable per item type (default: include). |

## Work item — scope terms

| Term | Definition |
|---|---|
| **Polyphony tag** | The `polyphony:*` namespace stamped on every work item the polyphony pipeline owns. The bare `polyphony` tag marks an in-scope descendant; `polyphony:root` marks a root; `polyphony:planned` is a status sub-tag set by the planner. Authoritative spec: `docs/polyphony-tags.md`. |
| **In-scope** | An item is in-scope for this run iff it carries `polyphony:root` (it IS the root) OR it carries the bare `polyphony` tag (it is a tagged descendant). |
| **Out-of-scope** | A descendant of the root that does NOT carry the polyphony tag. NOT operated on. Reported in close-out. Does NOT block close-out or the feature PR. |
| **Scope renegotiation** | Mechanism for child planning to surface a request for parent-plan changes (e.g., "this child can't fit the parent's plan; please revise the parent"). The parent plan is not complete until consensus is reached with all child plans that requested changes. Mechanically, the child PR carries a [renegotiation flag](#renegotiation-flag); the workflow decides what to do based on the flag and the [out-of-scope files](#out-of-scope-files) the PR touches. |
| <a id="renegotiation-flag"></a>**Renegotiation flag** | An HTML-comment-fenced block in a child plan PR body declaring "the parent plan needs to change." The fence is `<!-- polyphony:requests-parent-change --> ... <!-- /polyphony:requests-parent-change -->`; the inner text is the human-readable reason that becomes input to the parent re-planning prompt. Extracted by `polyphony plan extract-renegotiation-flag`. Mirrors the fenced-block convention used by `polyphony plan load-guidance`. |
| <a id="out-of-scope-files"></a>**Out-of-scope files** | Files a plan PR touches that fall outside the `--child-scope` globs the workflow expects this PR to own. Computed by `polyphony plan validate-scope`. Their presence drives the four-cell verdict matrix together with the [renegotiation flag](#renegotiation-flag): in-scope only → `allow`; in-scope + flag → `allow`; out-of-scope + flag → `allow_renegotiation`; out-of-scope + no flag → `block`. See [decisions/scope-renegotiation.md](decisions/scope-renegotiation.md). |
| <a id="renegotiation-handler"></a>**Renegotiation handler** | The set of nodes in `plan-level.yaml` that consume the `validate-scope` and `extract-renegotiation-flag` envelopes — `validate_scope` (post-review, pre-merge), `scope_violation_gate` (operator override/abort on `block` verdicts), and `extract_renegotiation_flag` (post-merge harvester). Together they convert the verb mechanics into routing decisions and into the workflow's bubble-up output. See [decisions/scope-renegotiation.md](decisions/scope-renegotiation.md) §"Handler design — workflow output bubble-up". |
| <a id="scope-violation-gate"></a>**Scope violation gate** | The `human_gate` node in plan-level.yaml that fires when `validate_scope` returns `verdict: block` — the plan PR touched [out-of-scope files](#out-of-scope-files) without a [renegotiation flag](#renegotiation-flag). Two options: **override** (proceed to merge anyway) or **abort** (terminate the workflow). Distinct from the merge-error gate, which fires on mechanical merge failures rather than scope violations. |
| <a id="bubble-up-output"></a>**Bubble-up output** | A key on a sub-workflow's top-level `output:` map that exists solely to communicate state to the (forthcoming) parent workflow. plan-level.yaml exports four bubble-up outputs from the renegotiation handler: `renegotiation_pending`, `renegotiation_request`, `validate_scope_verdict`, `scope_violation_files`. Distinct from [Promote](#promote): bubble-up is workflow data flow (sub→parent via `output:`), promote is a git merge (head→base). Authoritative source: `conductor-mechanics` M7. |

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
| **Trunk flow** | The architectural default: there is no global "plan everything, then implement everything" phase boundary. The driver dispatches items from the worklist in dependency-edge order; each item's facets fire as their requirements become `ready`. Plan PRs and impl PRs interleave naturally. **Replaces the implicit waterfall of plan-then-implement.** |
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
| **Impl branch** | A branch holding the code changes for a single implementable leaf. | `impl/{root_id}-{n}-{item_id}` |
| **Evidence branch** | A branch holding evidence artifacts for an actionable item that requires evidence. | `evidence/{root_id}-{item_id}` |
| <a id="promote"></a>**Promote** | A merge whose target is the immediate parent in the branch hierarchy (Impl → MG → feature; Sub-MG → MG → feature; Plan(child) → Plan(parent) → feature; Evidence → feature). **Replaces "bubble-up merge".** | Same primitive at every layer. |

## Pull requests

| Term | Definition | Head → Base |
|---|---|---|
| **Impl PR** | Promotes one impl's code into its parent merge group branch. | `impl/{r}-{n}-{i}` → `mg/{r}-{n}` |
| **MG PR** | Promotes one merge group's integrated code into its parent (feature branch or parent MG). | `mg/{r}-{n}` → `feature/{r}` (or `mg/{r}-{parent_n}` if nested) |
| **Plan PR** | Promotes an approved plan up one layer; the root plan promotes into the feature branch. | `plan/{r}-…-{c}` → `plan/{r}-…` (or `plan/{r}` → `feature/{r}`) |
| **Evidence PR** | Promotes an actionable item's evidence into the feature branch. | `evidence/{r}-{i}` → `feature/{r}` |
| **Feature PR** | Promotes the feature branch into `main`. The "ship it" PR. | `feature/{r}` → `main` |
| **PR kind** | One of `impl_pr`, `mg_pr`, `plan_pr`, `evidence_pr`, `feature_pr`. The unit on which review/merge policy is configured. | — |
| **Evidence floor** | The strict, mechanical pre-reviewer bar an evidence PR must clear before any LLM reviewer is invoked: ≥1 commit on the head branch beyond base AND a non-empty PR body (after whitespace trim). Implemented by `polyphony pr check-evidence-floor`. The floor exists to catch "agent crashed before producing anything" misfires; content quality remains the LLM reviewer's exclusive judgment. See **floor check**. |
| **Floor check** | The actionable workflow node (`evidence_floor_check`) that runs the evidence floor verb between `open_evidence_pr` and `evidence_reviewer`. On violation, routes to `floor_failed_gate` (a human gate offering abort / retry / manual_complete) instead of asking the reviewer to judge an empty PR. |
| **Mechanical floor** | Synonym for the evidence floor when contrasted with content judgment. Mechanical = "did the agent produce anything at all"; content judgment = "is what they produced any good". Two separable concerns; the floor handles the first deterministically, the LLM reviewer handles the second. |
| **ADO feature-PR parity** | Property of `feature-pr.yaml` v1.2.0+ that the ADO leg (creator → `pr_lifecycle_ado` → remediation → updater → poster) is structurally identical to the GitHub leg — same remediation chain, same cycle cap, same human gates. Closed by PR #149 / `docs/decisions/ado-feature-pr-parity.md`. The only deliberate gap is reviewer comment-text fetch on ADO (no `pr get-comments-ado` verb yet) — see ADR for the workaround. |
| **Dual-poster pattern** | The "LLM agent generates content + sibling script invokes the polyphony verb to post it" idiom used when an LLM agent needs to write to a system whose verb lives in `polyphony` (which agents do not have as a registered tool). The agent emits e.g. `comment_body`, sets `posted: false`, and routes to a small script node that invokes the verb (`pr post-comment-ado`). First used in `plan-level.yaml` (`plan_reviewer` + `plan_reviewer_poster_ado`); generalized in `feature-pr.yaml` (`feature_pr_updater` + `feature_pr_updater_poster_ado`). Codified in `.github/skills/polyphony-workflow-author/SKILL.md`. |

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
| **Stuck review** | The condition where a pending-review polling loop has cycled too many times without the reviewer producing a verdict. Operationally: the **poll count** has reached the **poll cap**. Surfaces a **stuck-review gate** for operator decision rather than continuing to loop indefinitely. |
| **Poll cap** | The hard limit on how many pending-state poll iterations a workflow will tolerate before escalating to a stuck-review gate. MVP value: hard-coded 60 in `plan-level.yaml` and `ado-pr.yaml`; will eventually be policy-resolved per PR kind (see `docs/decisions/stuck-review-timeout.md`). |
| **Poll count** | The current iteration count for a single PR's pending-review loop. Tracked per-PR in a `$TMPDIR` counter file; resets to zero when the operator chooses `continue_waiting` at the stuck-review gate. |
| **Stuck-review gate** | The `human_gate` (`stuck_review_gate` in plan-level, `ado_stuck_review_gate` in ado-pr) that fires when poll count reaches poll cap. Three options: `continue_waiting` (reset the counter and return to the regular pending gate), `override_approved` (bypass review and route to the merger), `abort` (`$end` the workflow). |

## Configuration vs guidance

| Term | Definition |
|---|---|
| **Configuration** | Operator-authored, REQUIRED settings that govern the pipeline's behavior (`process-config.yaml`, `policy.yaml`, `profile.yaml`). Loaded at startup; missing/malformed config fails fast. |
| **Guidance** | Operator-authored, OPTIONAL agent instructions, tool addendums, MCP addendums, and acceptance-criteria templates that augment but do not change the pipeline's structural behavior. Absent guidance falls back to defaults. |

## Workflows

| Term | Definition |
|---|---|
| **Actionable workflow** | The conductor workflow (`actionable.yaml`) that satisfies the **actionable facet** of a single work item. Routes on the item's `executor` property: the **polyphony** leg drives the agent and produces an **evidence PR**, while the **human** leg only records satisfaction via the **human satisfaction gate**. Entry point: `executor_router`. Sibling to `plannable.yaml` and `implementable.yaml`. |
| **Platform sub-workflow** | A conductor workflow YAML that owns the platform-specific lifecycle for a particular PR kind, invoked by the parent workflow as a `type: workflow` node behind a `pr_platform_router`. `github-pr.yaml` and `ado-pr.yaml` are the canonical pair — `feature-pr.yaml` and `implement-mg.yaml` both invoke them via input_mapping rather than re-implementing the lifecycle. Adding a new PR kind on a new platform is "add a sub-workflow + route to it" rather than "fork the parent". |
| **Executor (`polyphony` / `human`)** | The actor that performs an actionable item's work, declared per item on the actionable facet. `polyphony` means the agent does the work and emits evidence; `human` means the action is outside polyphony's authority and only the human's confirmation of satisfaction is recorded. The executor is the input to the actionable workflow's `executor_router` step and the value carried through to the workflow's `executor` output. |
| **Human satisfaction gate** | The `human_gate` node on the actionable workflow's human leg. Asks the human whether the action has been performed; on `satisfied`, routes to `workflow_completed`; on `not_yet`, loops back to itself; on `abandoned`, routes to `workflow_abandoned`. The only step on the human leg — no evidence branch, agent, or PR is opened. |

## Plan-PR observability

| Term | Definition |
|---|---|
| **Plan status verb** | `polyphony plan status --root <id>` — operator-facing, read-only verb that walks the in-scope subtree from `<id>` (the same BFS the worklist build verb uses), classifies each item's plannable facet from the loaded process config, and queries `gh` per plan branch (`plan/{root}` or `plan/{root}-{item}`) to derive the **plan status enum** value. Optionally enriches each row with the current `plan_generation` from the run manifest. Routing-style: always exits 0; consumers branch on `error_code`. Pure inspection — no manifest mutation, no platform writes. |
| **Plan status enum** | The five values `plan_status` may take in the plan status verb's per-item rows: `needed` (item has the plannable facet but no plan PR exists yet), `open` (plan PR exists and is OPEN on the platform), `merged` (plan PR has been merged), `abandoned` (plan PR was closed without merging), `n/a` (item has no plannable facet — hidden from the items array unless `--include-na` is passed; always counted in `summary.plan_n_a` so the operator can see full tree scope). |
| **Pending revisions** | A boolean signal on a `plan_status="open"` row: `true` when the open plan PR carries an unresolved `CHANGES_REQUESTED` review decision (the reviewer asked for changes that the next plan-PR push has not yet addressed). Surfaced both per-item (`pending_revisions`) and as a summary counter so an operator can answer "how many open plan PRs need attention right now?" in a single read. Only meaningful when `plan_status == "open"` — null otherwise so consumers can distinguish "no signal" from "open, no pending revisions" from "open, changes requested". |

## Apex driver

| Term | Definition |
|---|---|
| **Apex driver** | The Phase 7 keystone SDLC orchestrator (`apex-driver.yaml`). A *driver*, not a pipeline: it builds a worklist for the apex tree, dispatches each wave's items in parallel through `apex-wave-dispatch.yaml` → `apex-item-dispatch.yaml`, integrates the wave, and re-evaluates the worklist until the apex root reports satisfied. Replaces the deleted `polyphony-full.yaml`. Inputs: `apex_id`, `intent`, `platform`. |
| **Dispatch loop** | The outer loop of the apex driver: `build_worklist` → `wave_dispatch_loop` (for_each over waves) → `apex_completion_gate`. The loop variable is the *worklist itself* — recomputed from observable state every iteration — not a step counter or pointer into a static plan. |
| **Lifecycle router** | The `lifecycle-router.ps1` deterministic classifier that wraps `polyphony state next-ready` and emits a routing envelope (`route: plan-level | actionable | implement-pg | feature-pr | fast-path | monitoring | blocked | error`). Consumed by `apex-item-dispatch.yaml`'s `classify_lifecycle` step. Same pattern as `route-actionable-executor.ps1`. |
| **Wave integrator** | The `wave-integrator.ps1` script that merges per-item branches into the apex feature branch in topological order from `polyphony edges check`. Default merge strategy is `--no-ff` for auditability. Conflicts abort the single merge (not the wave) and roll up to a wave-level human gate. |
| **Per-item worktree** | An isolated git worktree at `<repo-parent>/<repo-name>-item-<work_item_id>` on branch `sdlc/apex/<work_item_id>`, branched from the apex feature branch. Spawned by `worktree-manager.ps1` for each item dispatched within a wave; torn down after lifecycle dispatch completes. Idempotent — safe to re-spawn or re-tear-down on re-entry. |
| **Per-MG worktree** | Analogous to per-item worktree but scoped to a merge group (MG). Future extension; not in MVP. |
| **Observable-state re-entry** | The property that lets the apex driver resume after a human gate, restart, or interruption without persisting per-step pointers: every iteration re-builds the worklist from `polyphony state next-ready` and the EdgeGraph, so the next wave is whatever's ready *now*. |
| **Bubble-up signal** | A `renegotiation_pending: true` (or analogous) output that an inner sub-workflow surfaces to the apex driver. Triggers consultation of `policy.renegotiation.auto_decide` (`prompt` / `auto_restart` / `ignore`) and either gates, restarts the loop, or continues. As of Phase 7 follow-up, this signal is wired end-to-end: `plan-level` → `apex-item-dispatch` → `apex-wave-dispatch` (aggregated) → `apex-driver` (rolled up) → `renegotiation_gate`. |
| **Lifecycle dispatch** | The branch-on-router step inside `apex-item-dispatch.yaml` that selects exactly one lifecycle sub-workflow (`plan-level`, `actionable`, `implement-pg`, or `feature-pr`) per invocation, based on the `lifecycle-router.ps1` verdict. Implements the canonical "branch-on-router-into-sub-workflow" pattern (see `feature-pr.yaml`'s platform router for the same shape). Conductor does not support templated `workflow:` paths, so each route value is a separately-named workflow node with explicit `input_mapping`. |
| **Multi-facet sequence** | The implicit ordering of an item's facets across waves: planning before action before implementation. Enforced by `polyphony state next-ready` requirement edges, NOT by any sequence inside one `apex-item-dispatch` invocation — a single dispatch handles one facet, and the next facet (if any) is picked up on the next worklist rebuild. |
| **Fast-path completion** | The `apex-item-dispatch.yaml` terminal taken when the lifecycle router emits `route: fast-path` — typically because `polyphony state next-ready` reports the item as already satisfied or empty. The item exits without invoking any lifecycle sub-workflow; no state advance is needed because the item is already in a terminal disposition. |

## Open glossary questions

_All initial questions resolved. New questions surfacing during plan drafting will land here._

