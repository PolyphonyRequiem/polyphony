# Wagner's Conductor Gaps — 2026-05-31

**Author:** Wagner (Workflow Author)  
**Date:** 2026-05-31T10:51:14-07:00  
**Context:** Post-PR #547 (gate compression) debrief. These are gaps I felt writing polyphony workflows daily — amplified by the pain of PR #547.  
**Companion work:** Mahler is producing the conductor-mechanics-expert view + on_error retrofit assessment in parallel.

---

## Top 5 Conductor Gaps (Workflow Author's View)

---

### Gap 1 — `on_error:` routing is absent

**Gap name:** No step-level error routing

**What I do today (the hack):**

Every script step that can fail infrastructure-level (network, auth, transient API failures) must exit `0` unconditionally and fold its error into a JSON envelope. Then a `when:` route picks it up:

```yaml
# github-pr.yaml:409–450 — poll_status
- name: poll_status
  type: script
  command: pwsh
  args:
    - "-Command"
    - |
      # ... on any failure:
      @{ error = "polyphony pr poll-status failed: $json"; route = 'none' } | ConvertTo-Json -Compress
      exit 0    # ← MUST exit 0 — non-zero exits conductor's control flow
  routes:
    - to: poll_error_gate
      when: "{{ poll_status.output.error is defined and poll_status.output.error }}"
    # ... happy-path routes
```

Then there's a `human_gate` node (`poll_error_gate`) that the operator has to click through even for a retry that's 100% deterministic:

```yaml
# ado-pr.yaml:551–580
- name: poll_error_gate
  type: human_gate
  prompt: |
    ## ⚠️ PR Poll Failed
    Common causes: missing/invalid PAT, ADO timeout, transient API failure.
    Retry is safe (single read).
  options:
    - label: "🔄 Retry"
      value: retry
      route: poll_status
    - label: "🛑 Abort"
      value: abort
      route: abort_run
```

The `TODO` at `github-pr.yaml:1087` and `ado-pr.yaml:1196` is explicit:

```yaml
# TODO(AB#3257): add on_error: to: poll_error_gate once conductor RFC
#   Phase 2 (on_error: in routes) ships. Until then, infrastructure
#   failures exit non-zero and surface as conductor step errors.
```

**Why it hurts:**

1. Every script in the entire registry must honor the "exit 0 always, encode errors in JSON" contract. This is an invisible global invariant — there's no linter for it. New contributors don't know it exists until a script exits non-zero and conductor surfaces a cryptic step failure instead of routing to the gate.
2. Nineteen `*_error_gate` nodes across six workflows (per Beethoven's inventory from `decisions.md`) are human gates for retry/abort decisions that contain zero judgment — they're pure retry logic. These gates burn operator attention every time a transient network hiccup hits a 30-minute dogfood run.
3. The gate is defined in YAML, meaning it's 20–40 lines per gate that could be 3 lines if conductor had `on_error: to: <node>`.
4. On `poll_pr_state_delta` specifically (PR #547's core node), there is **no** `on_error:` protection at all right now — infrastructure failures just terminate the step with a conductor error. The TODO documents the known gap; the fix is blocked on RFC Phase 2.

**What "fixed" looks like:**

```yaml
- name: poll_pr_state_delta
  type: script
  command: pwsh
  args: [...]
  on_error:
    to: poll_error_gate    # ← infrastructure failures route here directly
  routes:
    - to: notify_pr_review_resolved
      when: "{{ poll_pr_state_delta.output.reaction_kind == 'pr_merged' }}"
    # ... other domain routes
```

The 19 trivial human gates collapse to `on_error:` declarations. Only gates with genuine operator judgment remain.

**Impact rank:** 1/5 (the single highest-leverage gap — touches every script node in the registry)  
**Cost to build:** Medium (RFC Phase 2 design is already scoped per AB#3257; implementation is conductor-engine work, not workflow work)

---

### Gap 2 — Re-entry has no declarative mid-graph anchor

**Gap name:** Stateless re-entry forces total idempotency

**What I do today (the hack):**

Conductor's `entry_point:` is a single static node declared at the top of the file. There's no way to say "if state X is true, re-enter at node Y." Every workflow must restart from `entry_point` and detect already-completed state on every node. From `polyphony.yaml:70`:

```yaml
# Q4 — Resume is pure observable-state re-entry. `intent: resume`
#      re-runs `worklist build` + `edges check` and lets the
#      dispatch loop pick up wherever the prior run left off. The
#      polyphony writes NO checkpoint state of its own; the source
#      of truth is the work-item tree (P1).
```

And from `implement-merge-group.yaml:47`:

```yaml
# Re-entry behavior (P3):
#   Every step in this workflow is idempotent — `branch ensure-mg`,
#   `branch ensure-impl`, `pr open-mg-pr`, `pr open-impl-pr`,
#   `pr merge-impl-pr`, `pr merge-mg-pr` all detect prior state and
#   either reuse or no-op.
```

This means every single script in every workflow must defend against being called twice. That defensive logic is often the majority of the script body. For example, `branch ensure-mg` has to check if the branch exists before creating it; `pr open-mg-pr` has to check if a PR is already open for this head/base pair. The idempotency contract is enforced by convention, not by the engine.

The workaround in `plan-level.yaml` is a "state detector" pattern (line 457): an early node reads ADO state and emits a routing key that then branches the graph to the correct resumption point. But this requires the workflow author to hand-build the resume-state-to-node mapping explicitly:

```yaml
# plan-level.yaml:457 — state_detector pattern
# to decide which downstream path to take. Enables P3-style re-entry.
- name: state_detector
  type: script
  # reads ADO work item state, emits routing key
  routes:
    - to: architect
      when: "{{ state_detector.output.phase == 'planning' }}"
    - to: pr_poll_platform_router
      when: "{{ state_detector.output.phase == 'awaiting_review' }}"
    # etc.
```

Every workflow with any re-entry story needs its own version of this pattern.

**Why it hurts:**

1. Idempotency is a hidden contract on every script node. It can't be expressed or verified in YAML.
2. The state-detector pattern adds 1–2 nodes per workflow purely to paper over the missing engine feature. `plan-level.yaml` has at least 4 nodes that exist solely to route re-entry.
3. When re-entry semantics break — as they have multiple times in dogfood — the failure mode is the workflow running duplicate work silently (double-planning, double-PR-opening) rather than a clear "re-entry failed" error.
4. There's no way to declare "this node is safe to skip on re-entry because X is already true." The workflow must build that reasoning from scratch for every step.

**What "fixed" looks like:**

A declarative `resume_from:` that can be conditional:

```yaml
workflow:
  name: implement-merge-group
  entry_point: branch_ensure_mg
  resume_from:
    # Engine evaluates these conditions against observable state at workflow start.
    # First matching condition wins; falls back to entry_point if none match.
    - node: root_router
      when: "{{ branch_exists('mg/{{ workflow.input.mg_path }}') }}"
    - node: impl_pr_open
      when: "{{ pr_exists(workflow.input.mg_branch, workflow.input.feature_branch) }}"
```

The engine handles re-entry routing; the workflow author declares the invariants.

**Impact rank:** 2/5 (systemic — affects every long-lived workflow with idempotent steps)  
**Cost to build:** Large (requires conductor to evaluate resume conditions at startup; integration with external state is non-trivial)

---

### Gap 3 — Sub-workflow composition leaks: arrays must be JSON-encoded strings

**Gap name:** Structured data can't cross sub-workflow boundaries natively

**What I do today (the hack):**

Conductor's `input_mapping:` flattens nested data. Arrays cannot be passed directly — they must be JSON-encoded to a string by the parent and decoded by the child. This is documented explicitly in `root-batch-dispatch.yaml:83`:

```yaml
    batch_items:
      type: string
      required: true
      description: >-
        JSON-encoded array of work items in this batch. Each element is a
        WorklistItem ({ item_id, parent_item_id, plan_status, ... }).
        Encoded as a string because conductor's input_mapping flattens
        nested arrays; the `dispatch_items` for_each parses this back.
```

And then in `polyphony.yaml:1614`:

```yaml
    input_mapping:
      batch_items: "{{ batch.items | tojson }}"    # ← forced encode
```

And then inside `root-batch-dispatch.yaml`, the child workflow's `for_each` source node has to decode the string back to an array.

The same problem surfaces in output propagation. `root-batch-dispatch.yaml:127`:

```yaml
  renegotiation_items: >-
    {%- if aggregate_renegotiation is defined -%}{{ aggregate_renegotiation.output.renegotiation_items | default('[]') }}
    {%- else -%}[]{%- endif -%}
```

`renegotiation_items` is a JSON-as-string all the way up the call stack: it's encoded in `root-item-dispatch.yaml`, survives as a string through `root-batch-dispatch.yaml`, and `polyphony.yaml` has to decode it again before it can do anything with it. The contract is "JSON as a string field," not "structured array" — as Mahler documented in `decisions.md:193`.

Also: there's no way for a sub-workflow to pass back fields the parent didn't explicitly declare in its `output:` block. If `github-pr.yaml` adds a new field (e.g., `reviewer_count`), every parent workflow that calls it must be manually updated to thread the field through. There's no `passthrough: all` or wildcard forwarding.

**Why it hurts:**

1. Every structured data boundary in the call stack requires a `tojson` / `fromjson` round-trip. This is ~5–10 lines of encode/decode logic per call site.
2. Adding a field to a deep sub-workflow's output requires a cascade of edits up through every parent's `output:` block. In the polyphony stack, that's often 4 layers deep.
3. Error messages involving JSON-as-string fields are opaque. If `renegotiation_items` contains invalid JSON (e.g., a Jinja expression that partially evaluated), it surfaces as a confusing string-comparison failure rather than a type error.
4. The `batch_items` JSON-as-string contract has already caused bugs — the for_each source node must parse it back, and any shape change (adding a field to `WorklistItem`) requires updates in both the serializer and the deserializer.

**What "fixed" looks like:**

Native structured pass-through for arrays and objects in `input_mapping:`:

```yaml
    input_mapping:
      batch_items: "{{ batch.items }}"    # ← pass array directly; engine handles it
```

And wildcard output forwarding for sub-workflow outputs:

```yaml
output:
  merged: "{{ pr_lifecycle_github.output.merged }}"
  pr_url: "{{ pr_lifecycle_github.output.pr_url }}"
  _passthrough: pr_lifecycle_github    # ← forward all other fields from this node
```

**Impact rank:** 3/5 (affects every multi-layer workflow; particularly painful at the polyphony → root-batch-dispatch → root-item-dispatch boundary)  
**Cost to build:** Medium (engine change to not flatten arrays in input_mapping; wildcard output is additive)

---

### Gap 4 — `notification` / `emit` type name instability: bulk-rename pending forever

**Gap name:** Pre-release type alias in production YAMLs

**What I do today (the hack):**

Conductor's PR #213 (`27006af` cherry-pick) ships `type: emit` / `emit:` as the canonical name for domain signal nodes. But the polyphony repo is on the pre-release dogfood build that uses `type: notification` / `notification:` instead. PR #547 shipped three notification nodes in `github-pr.yaml` and three in `ado-pr.yaml`, all with TODO comments:

```yaml
# github-pr.yaml:488
# TODO(post-upstream-merge): rename type: notification → type: emit,
#   and notification: → emit: when conductor PR #213 cherry-pick lands.
- name: notify_pr_review_resolved
  type: notification         # ← dogfood alias, not the final name
  notification: pr_review_required
```

```yaml
# github-pr.yaml:1046
# TODO(post-upstream-merge): rename type: notification → type: emit,
#   and notification: → emit: when conductor PR #213 cherry-pick lands.
- name: notify_pr_pending
  type: notification
  notification: pr_review_required
```

```yaml
# github-pr.yaml:1131
# TODO(post-upstream-merge): rename type: notification → type: emit.
- name: notify_pr_ci_attention
  type: notification
  notification: pr_ci_attention_required
```

Six nodes total across two files, all using the pre-release alias. When the upstream PR lands, I need a bulk-rename pass before I can tell operators the YAML matches conductor's released API.

**Why it hurts:**

1. Every `type: notification` node in the codebase now carries a mental "this is not the real name" annotation. The TODO comments are load-bearing documentation — if they're missed or deleted, the rename pass will be incomplete.
2. There's no version-tagged aliasing at the workflow level. If conductor shipped `type: emit` with `type: notification` as a deprecated alias for one release, I could just ship the new name immediately. Instead, I'm on a fork of conductor where `notification` is the only name.
3. New notification nodes I add going forward face a decision: write `notification` (wrong name, will need renaming) or write `emit` (correct name, will fail until the upstream cherry-pick lands). There's no way to win.
4. If the rename is ever forgotten and a downstream polyphony consumer upgrades conductor, all six nodes break at runtime with no warning at YAML-load time.

**What "fixed" looks like:**

Conductor needs a stable type name from day one, or a `deprecated:` alias mechanism:

```yaml
# Ideal: conductor declares deprecated alias internally
# type: notification → alias for type: emit (until conductor 3.0)
# Workflow author writes the canonical name from day one:
- name: notify_pr_pending
  type: emit
  emit: pr_review_required
```

Or, if the engine can't alias, a polyphony `$schema:` declaration that locks conductor version + validates type names:

```yaml
workflow:
  name: github-pr
  conductor_version: ">=2.4.8"
  # lint rule: fail if any node uses type: notification when conductor >= 2.5
```

**Impact rank:** 4/5 (low blast radius for now; high cognitive tax ongoing; becomes P1 the moment a consumer upgrades conductor)  
**Cost to build:** Small (type alias in conductor engine is a one-line mapping; schema-locked version in workflow header is a linter rule)

---

### Gap 5 — `when:` has no `else:` keyword; catch-all routes are magic bare `- to:`

**Gap name:** Catch-all routes are fragile implicit fallback

**What I do today (the hack):**

Conductor routes evaluate conditions top-to-bottom and the last bare `- to: <node>` (no `when:`) is the catch-all. This is required by M4 (the polyphony-workflow-author skill's "every reachable agent state has an explicit catch-all `to:` last" rule). But the semantics are implicit — there's no `else:` keyword, no explicit "this is the default route" marker. Examples from multiple files:

```yaml
# feature-pr.yaml:309–316 — pr_platform_router dispatch
    routes:
      - to: feature_pr_creator_github
        when: "{{ pr_platform_router.output.platform == 'github' }}"
      - to: feature_pr_creator_ado
        when: "{{ pr_platform_router.output.platform == 'ado' and pr_platform_router.output.ado_inputs_ready == true }}"
      - to: feature_pr_inputs_missing_gate
        when: "{{ pr_platform_router.output.platform == 'ado' and pr_platform_router.output.ado_inputs_ready == false }}"
      # Catch-all per M4: unknown platform value fails safely to $end
      # rather than raising `No matching route found` mid-run.
      - to: $end
```

```yaml
# feature-pr.yaml:483–488 — pr_lifecycle_github sub-workflow
    routes:
      - to: $end
        when: "{{ pr_lifecycle_github.output.merged == true }}"
      - to: pr_remediation_policy
        when: "{{ pr_lifecycle_github.output.merged == false }}"
      # Catch-all: undefined/missing 'merged' -> remediation cycle
      # (treats ambiguous outcomes as 'not merged') per M4.
      - to: pr_remediation_policy
```

Every multi-way branch in every file requires a comment explaining why the bare `- to:` is there and what it handles. The `when:` system also can't express boolean logic beyond what Jinja inline expressions support — no `any_of:` or `all_of:` sugar. When a route needs a compound condition across multiple output fields, the only options are:

a) Inline a long Jinja expression: `when: "{{ a == 'x' and b is defined and b != '' and c | length > 0 }}"`  
b) Add a script router node that pre-computes the compound condition and emits a routing key

Option (b) is what we do in practice — the `pr_platform_router`, `pending_review_gate_policy_router`, `revise_cap_gate_policy_router`, `pr_pre_merge_policy_router`, and `stuck_review_gate_policy_router` nodes in the PR lifecycle files all exist primarily to avoid compound `when:` expressions that would be fragile inline. Each one is 30–60 lines of YAML to wrap a script that computes 2–4 boolean conditions.

**Why it hurts:**

1. The implicit catch-all is a silent footgun. If a workflow author forgets M4 and omits the bare `- to:`, conductor silently fails with "No matching route found" at runtime — which surfaces mid-run, not at validation time. The YAML linter does not enforce catch-all presence.
2. Policy router scripts (the Option (b) workaround) add 200–400 lines of YAML per workflow for what is conceptually a `switch` statement. In `github-pr.yaml` alone there are four policy-router nodes that are pure routing-condition computation with no side effects.
3. The `is not defined` / `is defined` guards on `when:` conditions are necessary everywhere because conductor is StrictUndefined — but the M3 rule (from the mechancis skill) is invisible in the YAML schema. The only hint is when a run crashes with "UndefinedError: 'some_node' is undefined."
4. Expressing "route to X if any of these three conditions hold" requires either a chain of three `when:` routes to the same destination (valid but verbose) or an inline `or` expression (fragile if any sub-expression can be undefined).

**What "fixed" looks like:**

An explicit `else:` keyword and a `match:` shorthand:

```yaml
    routes:
      - match:
          - when: "{{ output.platform == 'github' }}"
            to: feature_pr_creator_github
          - when: "{{ output.platform == 'ado' and output.ado_inputs_ready }}"
            to: feature_pr_creator_ado
          - when: "{{ output.platform == 'ado' and not output.ado_inputs_ready }}"
            to: feature_pr_inputs_missing_gate
          else: $end    # ← explicit, readable, lintable
```

And compound condition helpers that are undefined-safe by default:

```yaml
    routes:
      - to: poll_error_gate
        when: "{{ output.error | is_truthy }}"    # ← safe even if 'error' is undefined
```

**Impact rank:** 5/5 (not blocking, but the most pervasive daily tax — every route block in every file)  
**Cost to build:** Small-Medium (syntax extension in conductor route evaluator; linting rule for catch-all enforcement)

---

## Bonus: PR-Platform Abstraction Status

### Is `pr_platform_router` → `pr_lifecycle_{github,ado}` clean?

**Paragraph 1 — The good: symmetry at the sub-workflow interface is real.**

The core interface contract is clean. Both `github-pr.yaml` and `ado-pr.yaml` accept the same inputs (`pr_number`, `branch_name`, `target_branch`, `review_policy`, `work_item_id`) and emit the same outputs (`merged`, `pr_url`). The `pr_lifecycle_github` and `pr_lifecycle_ado` nodes in `feature-pr.yaml` (lines 471–535) mirror each other in structure. The `input_mapping:` blocks are structurally parallel. From `feature-pr.yaml:37` (header comment): "Both platform legs are full-lifecycle as of v1.2.0." The PR #547 gate-compression changes applied symmetrically — both `github-pr.yaml` and `ado-pr.yaml` got identical `notify_pr_pending`, `poll_pr_state_delta`, and `notify_pr_review_resolved` nodes with the same routing logic and identical `TODO(AB#3257)` and `TODO(post-upstream-merge)` comments. The abstraction held.

**Paragraph 2 — The leak: ADO inputs bleed upward and the remediation cycle is platform-aware in the parent.**

The abstraction has two real leaks. First, `pr_lifecycle_ado` requires four ADO-specific inputs (`organization`, `project`, `repository`, `root_id`) that `pr_lifecycle_github` does not need (see `feature-pr.yaml:511–526`). The ADO `input_mapping:` is significantly longer, and the comment at line 519 documents a dogfood crash caused by the missing `work_item_id` threading: "Omitting it crashed guidance_loader with a Jinja 'work_item_id' undefined error against an empty workflow.input in ado-pr.yaml." This means the sub-workflow boundary is ADO-aware in the parent — `feature-pr.yaml` can't treat the two legs as interchangeable. Second, the remediation cycle in `feature-pr.yaml` uses Jinja branching on `workflow.input.platform` directly (line 17 of the file header: "platform-aware via Jinja branching on `workflow.input.platform`"). The remediation planner, seeder, and updater all branch on `platform` inline — meaning the platform split re-enters `feature-pr.yaml`'s body rather than being fully encapsulated in the sub-workflows. A v2 would push the ADO-specific inputs into a sub-workflow-level defaults mechanism (so `feature-pr.yaml` passes only `platform: ado` and the sub-workflow resolves its own connection inputs from workflow context), and would wrap the remediation cycle into its own `remediation-cycle-github.yaml` / `remediation-cycle-ado.yaml` sub-workflow pair — making `feature-pr.yaml` a pure orchestrator that dispatches to platform-specific sub-workflows end-to-end, with no inline Jinja branching on `platform` in the parent body.

---

## File Reference Index

| File | Key lines / patterns cited |
|------|-----------------------------|
| `github-pr.yaml` | 488, 1046, 1087–1090, 1131 (TODO comments); 409–450 (poll_status exit-0 pattern); 1048–1069 (notify_pr_pending); 1090–1121 (poll_pr_state_delta) |
| `ado-pr.yaml` | 532–548 (poll_status routes + error gate); 551–580 (poll_error_gate); 1196–1210 (TODO on_error comment) |
| `feature-pr.yaml` | 309–316 (M4 catch-all pattern); 471–535 (platform-sub-workflow input asymmetry); 483–488 (merged catch-all) |
| `root-batch-dispatch.yaml` | 83–84 (batch_items JSON-as-string); 127 (renegotiation_items JSON-as-string) |
| `polyphony.yaml` | 70–74 (resume is observable-state re-entry); 1614 (tojson forced encode) |
| `implement-merge-group.yaml` | 47–54 (re-entry idempotency contract) |
| `plan-level.yaml` | 457 (state_detector re-entry pattern) |

---
