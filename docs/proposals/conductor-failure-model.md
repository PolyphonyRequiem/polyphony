# Engineering Brief: First-Class Failure Model

**Repo:** conductor
**Owner:** conductor engineering
**Submitter:** polyphony (one of conductor's downstream consumers)
**Status:** Ready to design — open questions itemized below
**Polyphony tracking item:** AB#3257 (parent epic: AB#3253) — failure-mode gate elimination work that consumes the conductor primitives proposed here

---

## Summary

Add a typed failure channel to conductor nodes, propagate failures across
`workflow:` boundaries by default, and let nodes declare structured
`on_failure` handlers and `requires:` preconditions. Replaces the current
`{success: bool}` discriminator + branch + human-gate idiom that workflow
authors fall back to when a node needs to fail in a way the runtime
understands.

---

## Context

This brief comes from polyphony, a workflow-heavy consumer of conductor (15
workflows, 296 nodes — census in the appendix). The pattern described below
emerged from auditing those workflows; whether it belongs in conductor or
stays as a polyphony-side pattern is conductor's call. The brief is written
to make either decision cheap.

Conductor recognizes node failure only in narrow runtime senses:

- `script` node: pwsh non-zero exit / unhandled exception
- `agent` node: LLM response violates `output:` schema
- Provider transport: rate-limit / timeout (auto-retried 3× — `providers/copilot.py:69-91`, `providers/claude.py:90-111`)
- Workflow: `limits.max_iterations` exceeded (`engine/limits.py:49`)

There is no concept of "the node ran without throwing, but its result is a
semantic failure," and there is no propagation of any failure across a
`type: workflow` boundary. Workflow authors fill the gap with
`{success: false, error: "..."}` returns + a routes-table branch + a
`human_gate` to absorb the failure case. The downstream symptoms polyphony
has observed:

- Error-recovery gates accumulate (~40 across polyphony's workflows) — every
  script that can fail needs a landing pad and `human_gate` is the only
  available primitive.
- Sub-workflow failures do not surface to the caller. A child workflow
  halting at one of these gates appears to the parent as "didn't return."
  Apex runs wedge silently and require event-log forensics to diagnose.
- The `{success: bool}` discriminator is per-author convention. There is no
  standard shape, no standard place to catch on it, no standard place it
  lands when uncaught.

These are all symptoms of the same missing primitive: a typed failure
channel with propagation semantics.

---

## Goal

A node — `script`, `agent`, or `workflow` — can declare typed failures it may
raise. A caller can catch them by kind with declarative semantics. Uncaught
failures propagate through `workflow:` boundaries and terminate the run with
a structured, durable failure record.

## Non-goals

- No new loop or switch primitive. The graph-cycle-with-counter pattern
  stands; separate brief if there's appetite.
- No change to provider-layer transient retries — those already work.
- No change to checkpoint / resume semantics. A failed run cannot be resumed;
  it can only be re-run.
- No removal or restriction of `human_gate`. The primitive stays; this brief
  reduces the *workarounds* that have been built on top of it.
- No JSON-schema versioning of failure kinds in v1 (start flat strings).

---

## Design

### D1. Typed failure envelope

A failure is a structured record with three required fields and one optional:

```json
{
  "conductor_failure": true,
  "kind":   "<dotted.lowercase.identifier>",
  "message": "<human-readable>",
  "details": { "arbitrary": "json" }
}
```

`kind` is a flat dotted string for v1 (`external.git.drift`,
`precondition.missing`, `internal.script_error`). No hierarchy matching in v1
— equality only. Hierarchy may land later if catch-pattern demand emerges.

#### How a node raises it

- **`script` nodes** write the envelope to a path the runtime supplies via
  the env var `CONDUCTOR_FAILURE_OUT` and exit `0`. The runtime checks the
  file after the script returns; if present and parseable as the envelope
  shape, the node is treated as failed. Exit `0` and no file = success;
  non-zero exit = legacy unstructured failure (kind `internal.script_error`,
  message = stderr tail).

  Rationale for env-var-with-file over stdout sentinel: keeps script stdout
  free for the existing `output:` capture path; avoids reserving a stdout
  marker that could collide with author output; cross-platform clean
  (keeps script stdout free for the existing `output:` capture path; avoids
  reserving a stdout marker that could collide with author output; file IO
  is cross-platform clean).

- **`agent` nodes** emit the envelope as their JSON response. The runtime
  detects the `conductor_failure: true` discriminator before schema
  validation and routes through the failure path instead. A helper shape
  may be added to `output:` schema validation so authors can declare both
  their happy-path schema and the failure kinds they may raise.

- **`workflow` nodes** raise whatever their child raises (see D2).

A conductor-shipped pwsh helper makes the script side ergonomic:

```pwsh
function Write-ConductorFailure {
  param([Parameter(Mandatory)][string]$Kind,
        [Parameter(Mandatory)][string]$Message,
        [hashtable]$Details = @{})
  $env:CONDUCTOR_FAILURE_OUT |
    Set-Content -Encoding utf8 -Value (ConvertTo-Json @{
      conductor_failure = $true; kind = $Kind
      message = $Message;        details = $Details
    } -Depth 8 -Compress)
  exit 0
}
```

### D2. Propagation semantics

A failure propagates up the call stack of `type: workflow` invocations
exactly like an exception, accumulating a frame trail:

```json
{
  "conductor_failure": true,
  "kind": "external.git.drift",
  "message": "feature branch SHA does not match expected lease",
  "details": { "branch": "feature/123", "expected": "abc", "actual": "def" },
  "raised_at":   { "workflow": "feature-pr", "node": "integrate_target_drift" },
  "propagated":  [
    { "workflow": "implement-merge-group", "node": "open_feature_pr" },
    { "workflow": "plan-level",            "node": "do_implementation" },
    { "workflow": "apex-driver",           "node": "build_worklist" }
  ]
}
```

A failure that reaches the root unhandled:

1. Halts the run with a distinct exit status (e.g. exit code reserved for
   "workflow-defined failure," separate from "runtime error" and
   "max-iterations").
2. Writes the failure record to a durable per-run location next to the
   manifest (proposed: `<run-dir>/failures.jsonl`, one record per failure;
   append-only so multiple unhandled failures in a parallel group are all
   recorded).
3. Emits a final event-log entry of type `workflow.failed` with the failure
   record inline so existing event-log consumers see it without a separate
   read.

### D3. `on_failure` decorator

Any node may declare handlers for failures from its callee (the script it
runs, the agent it calls, or the sub-workflow it invokes):

```yaml
- name: stamp_facets
  type: script
  command: pwsh
  args: [-NoProfile, -Command, "..."]
  failures:
    - external.git.drift
    - precondition.missing
  on_failure:
    - when: external.git.drift
      action: { retry: { max: 3, backoff: exponential, initial_seconds: 2 } }
    - when: precondition.missing
      action: { halt: { message: "branch {{ failure.details.branch }} missing" } }
    - when: "*"
      action: { propagate: true }
```

Three actions, exhaustive in v1:

| Action | Semantics |
|---|---|
| `retry: { max, backoff, initial_seconds }` | Re-run the node up to `max` times. `backoff` ∈ `{ none, linear, exponential }`. Counts against `limits.max_iterations` (a retry is a node execution). |
| `halt: { message }` | Terminate the run *here* with kind `halt.intentional`. No propagation, no human gate, no resume. The `message` is rendered with `{{ failure }}` in scope. |
| `propagate: true` | Re-raise to the parent. Default for any uncaught kind. |

`failures:` declares the set the node *may* raise. The runtime treats any
raised kind not in the declared set as `internal.undeclared_failure` (still
catchable, but visible as a bug in the workflow). The declaration is
load-time-checked: `on_failure` `when:` clauses that name kinds not in
`failures:` are a lint error.

### D4. Preconditions

A node declares structural requirements that must hold before it runs:

```yaml
- name: integrate_target_drift
  type: script
  requires:
    - check: branch_exists
      args: { branch: "{{ inputs.target_branch }}" }
    - check: git_authenticated
  args: [...]
```

`check:` names a side-effect-free probe. A failed check raises
`precondition.<check-name>` — catchable like any other failure. Conductor
ships a small built-in set:

- `branch_exists { branch }`
- `file_present { path }`
- `tool_on_path { name }`
- `env_set { name }`
- `git_authenticated` (probes `git ls-remote` against origin)

Custom checks are out of scope for v1. Workflow authors who need them write
a guard script and raise `precondition.<custom-kind>` manually.

---

## Phasing

Land in three independently shippable PRs. Each phase is fully backward-
compatible — existing workflows continue to work unchanged.

### Phase 1 — Envelope + propagation + default-propagate

- Script env-var-with-file mechanism (`CONDUCTOR_FAILURE_OUT`)
- Agent JSON `conductor_failure: true` discriminator
- `workflow:` node propagates child failures
- Unhandled-at-root → `failures.jsonl` + `workflow.failed` event + distinct exit code
- pwsh helper shipped (`Write-ConductorFailure`)

Acceptance:

1. A pwsh script that calls `Write-ConductorFailure -Kind x.y -Message m`
   and exits 0 causes the node to be marked failed.
2. An LLM agent emitting `{conductor_failure: true, kind: "x.y", message: "m"}`
   does the same; schema validation does not run on the failure shape.
3. A 3-deep nested `workflow:` invocation surfaces the deepest failure at
   the root with `propagated:` containing the two intermediate frames in
   bottom-up order.
4. The run directory contains `failures.jsonl` with one record matching the
   raised envelope plus `raised_at` and `propagated`.
5. Run exit code is the reserved "workflow-defined failure" status, distinct
   from existing exit codes.
6. A script that exits non-zero without writing the envelope still works,
   surfacing kind `internal.script_error` with stderr tail as `message`.

### Phase 2 — `on_failure` decorator + `failures:` declaration

- Schema additions: `failures: [string]`, `on_failure: [{ when, action }]`
- Actions: `retry`, `halt`, `propagate`
- Load-time validation: `on_failure when:` names a kind in `failures:` or `*`
- `{{ failure }}` template scope inside `on_failure.action.halt.message`
- Retry counter integration with `limits.max_iterations`

Acceptance:

1. `on_failure: { when: x.y, action: { retry: { max: 3, backoff: exponential, initial_seconds: 1 } } }` re-runs the node up to 3 times on `x.y`, with delays 1s, 2s, 4s.
2. `retry.max` exhaustion re-raises the original failure (does *not* invent a `retry_exhausted` kind in v1).
3. `halt` writes the failure record (kind `halt.intentional`, with the original failure nested under `details.cause`) and terminates the run — no propagation past the halting node.
4. `propagate: true` re-raises unchanged (no frame mutation other than the standard `propagated:` append on workflow-boundary crossings).
5. `*` catches any kind not explicitly handled.
6. Linting rejects an `on_failure when:` value that's not in `failures:` and not `*`.

### Phase 3 — Preconditions / `requires:`

- Schema: `requires: [{ check, args }]`
- Built-in check registry: `branch_exists`, `file_present`, `tool_on_path`, `env_set`, `git_authenticated`
- Checks run in declared order; first failure raises `precondition.<check-name>` and short-circuits remaining checks
- Standard `on_failure` applies (preconditions are normal failures from the caller's POV)

Acceptance:

1. A node with `requires: [{ check: file_present, args: { path: "/no/such" } }]` raises `precondition.file_present` before its body runs.
2. The failure `details` includes the check's `args` verbatim plus any check-specific diagnostic fields.
3. Caller can `on_failure: { when: precondition.file_present, action: ... }` and recover.
4. A `check:` name not in the built-in registry is a load-time error.

---

## Touch points

File references below come from polyphony's read of conductor mechanics docs
and may have drifted; engineering agent should confirm against current source.

- `config/schema.py:824, 833` — `WorkflowDef` siblings for any new top-level fields (none currently planned, but `requires:` / `failures:` / `on_failure:` are agent-level schema additions here).
- `config/schema.py:351 (RetryPolicy)` — model the v1 retry action; consider whether `on_failure.action.retry` reuses this type or introduces a sibling.
- `engine/limits.py:49` — retry-as-iteration accounting.
- `engine/workflow.py:563-676` — sub-workflow invocation; propagation crosses here.
- `engine/workflow.py:629-639` — input coercion path; failure envelope coercion is parallel work.
- `engine/checkpoint.py:100-114, 117-128` — failed runs should not be resumable; checkpoint behavior on failure needs explicit decision.
- `providers/copilot.py:69-91, 281-340` and `providers/claude.py:90-111, 529-555` — agent-side failure-envelope detection lives before schema validation; verify both providers handle identically.

---

## Test plan

Each phase ships with:

- **Unit tests** at the points the brief calls out per phase (envelope round-trip, retry counting, precondition short-circuit, etc.).
- **Workflow integration tests** in `tests/conductor/workflows/` using minimal multi-node YAMLs that exercise the propagation paths end-to-end.
- **At least one cross-platform script test** — confirm the env-var-with-file mechanism works identically on Windows pwsh and Linux pwsh.
- **A regression workflow** that uses the legacy `{success: false}` + routes + gate pattern to prove backward compatibility.

---

## Open decisions for the engineering agent

The agent should pick and document, not ask:

1. **Exit code reserved for workflow-defined failure.** Pick a code distinct from existing runtime-error and max-iterations codes. Document in the same place existing exit codes are documented.
2. **Backoff base.** Phase 2 spec says `initial_seconds` and `backoff: exponential`. Confirm whether multiplier is fixed at 2 or configurable in v1 (recommend fixed at 2).
3. **`failures.jsonl` format.** One JSON object per line is the proposal. Confirm against any existing conductor log conventions.
4. **Checkpoint behavior on failure.** Recommendation: delete or mark-invalid the checkpoint on `workflow.failed`. Document the choice; this is a small back-compat issue for any tooling that consumes checkpoints.
5. **Reserved kind namespace.** `internal.*`, `halt.*`, `precondition.*` are reserved by conductor; document and lint-enforce that user-declared `failures:` may not use these prefixes.
6. **Parallel-group failure aggregation.** When a `parallel:` block has multiple children fail, which one propagates? Recommendation: first-failed wins for propagation; *all* failed records still appended to `failures.jsonl`. Confirm against existing `failure_mode:` semantics on `parallel:` / `for_each:` blocks.

---

## Open questions for the brief author (polyphony)

Surface back with answers before Phase 1 lands:

1. **Migration shape on the polyphony side.** Confirm the 40 error-recovery gates we propose to remove can be reproduced in a fixture workflow for the regression test.
2. **Specific `kind` taxonomy polyphony wants.** Concretely: what's the initial list of `kind`s polyphony's scripts will raise? (Influences the helper docs and any kind-naming guidance the brief publishes.)
3. **PSA + version pinning.** Polyphony's `workflow.version` field is bumped in batch (per repo convention). Coordinate the version bump for any workflow that adopts `failures:` / `on_failure:` / `requires:`.

---

## Appendix: polyphony data referenced above

| Workflow | agent | script | gate | wf | for_each | total |
|---|---:|---:|---:|---:|---:|---:|
| plan-level | 3 | 41 | 26 | 4 | 1 | 75 |
| implement-merge-group | 3 | 31 | 9 | 0 | 0 | 43 |
| apex-driver | 0 | 23 | 7 | 1 | 1 | 32 |
| ado-pr | 3 | 19 | 7 | 0 | 0 | 29 |
| github-pr | 4 | 16 | 5 | 0 | 0 | 25 |
| feature-pr | 3 | 13 | 5 | 3 | 0 | 24 |
| actionable | 2 | 9 | 4 | 0 | 0 | 15 |
| apex-item-dispatch | 0 | 10 | 0 | 4 | 0 | 14 |
| reset-apex | 0 | 7 | 1 | 0 | 0 | 8 |
| research | 4 | 2 | 1 | 0 | 0 | 7 |
| apex-wave-dispatch | 0 | 5 | 0 | 0 | 1 | 6 |
| root-fallback-gate | 0 | 5 | 1 | 0 | 0 | 6 |
| cascade-remedy | 0 | 2 | 2 | 0 | 1 | 5 |
| remedy-stale-descendant | 0 | 3 | 2 | 0 | 0 | 5 |
| close-out | 1 | 1 | 0 | 0 | 0 | 2 |
| **Total** | **23** | **187** | **70** | **12** | **4** | **296** |

Of the 70 `human_gate` nodes, ~40 (name-pattern `*_error`, `*_failure`,
`*_gate` in the recovery sense) currently absorb script-level failures —
the use case a first-class failure model would let polyphony express
differently. The remaining ~30 are approval, scope-decision, and
external-state-acquisition gates. Polyphony's plans for either set are out
of scope for this brief.

Generated 2026-05-20 from `.conductor/registry/workflows/*.yaml`.
