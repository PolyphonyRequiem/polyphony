# ADR: Domain Signal Envelope

**Status:** Accepted  
**Date:** 2026-05-28  
**Authors:** Bach (Architect)  
**Vocabulary authority:** Daniel Green — "domain signal" is the polyphony-side
noun; `emit:` (conductor YAML primitive) is the verb. A workflow _emits_ a
_domain signal_.  
**Platespinner gaps:** Closes design dependency for issues #541 and #542.

---

## Context

Conductor PR #213 adds user-defined notification support to the workflow
engine. Workflows declare a `notifications:` block, define typed notification
schemas, and emit them via `type: notification` steps (renamed to `type: emit`
via commit `27006af` — the rename is transparent to this ADR).

When a step fires, conductor writes a JSON envelope to:
```
{TMPDIR}/conductor/conductor-{workflow_name}-{ts}-{run_id}.notifications.jsonl
```

Each line is:
```json
{"type": "notification", "timestamp": 1748472194.42, "data": { /* envelope */ }}
```

The `data` object is the **conductor notification envelope** — a fixed shape
owned by the conductor engine. Polyphony contributes the `payload` sub-object
of that envelope.

### The Conductor Envelope (data field)

The `data` field is the conductor notification envelope built by
`NotificationExecutor.build_envelope()`. Fields confirmed by the dogfood smoke
capture at `.squad/experiments/dogfood-smoke/README.md` (2026-05-28).

| Field | Type | Notes |
|---|---|---|
| `emission_id` | string | `"{run_id}:{agent_name}:{iteration}"` — deterministic, dedup key |
| `schema_id` | string | `"{namespace}.{notification_type}@{version}"` — stable type identifier |
| `notification_type` | string | e.g. `"pr_review_required"` |
| `namespace` | string | e.g. `"polyphony.feature_pr"` |
| `version` | int | starts at 1; bumped on breaking payload changes |
| `run_id` | string | hex run identifier shared with the `.events.jsonl` filename stem |
| `workflow` | string | workflow name |
| `source_agent` | string | name of the YAML step that fired |
| `subworkflow_path` | string[] | nesting path for sub-workflow steps |
| `correlation` | object | workflow-declared correlation keys, e.g. `{"root_id": "AB#4567"}` |
| `workflow_metadata` | object | engine-supplied metadata dict (may be `{}`) |
| `payload` | object | **polyphony-owned domain signal fields — defined by this ADR** |

### The Problem

Polyphony's workflows are moving from `human_gate` nodes to a
`(emit domain signal + script-poll-loop)` gate-compression pattern (see the
`gate-compression-pattern` ADR). For this pattern to work, domain signals must
carry enough context for:

1. **Platespinner** to render a CTA (call-to-action) notification — e.g. a
   "Review PR" button that opens the right URL
2. **Poll scripts** to identify whether they are responding to the right
   signal instance (correlation)
3. **Lifecycle management** — knowing when a signal is still actionable vs
   resolved or expired

No schema for `payload` exists today. This ADR defines it.

### Vocabulary

- **Domain signal:** the polyphony-side unit of user-actionable information,
  emitted by a workflow step. This ADR defines its wire shape.
- **`emit:`** (or currently `notification:` in dogfood): the conductor YAML
  primitive that fires a domain signal.
- **Conductor envelope:** the outer wrapper that conductor adds (schema_id,
  emission_id, correlation, etc.) — NOT owned by polyphony, not defined here.
- **Payload:** the `payload` sub-object of the conductor envelope — THIS IS
  what polyphony owns and what this ADR defines.

---

## Decision

Polyphony owns the `payload` field of the conductor notification envelope.
The schema below is the polyphony domain signal payload contract.

### Required Payload Fields

| Field | Type | Constraints | Description |
|---|---|---|---|
| `kind` | string | non-empty, machine-readable | Signal type identifier. See Kind Vocabulary below. |
| `severity` | string | `info` \| `warning` \| `error` \| `critical` | Rendering hint and routing input. |
| `title` | string | ≤80 chars | Short human-readable title for toast/notification header. |
| `message` | string | non-empty | Full human-readable body. |

### Optional Payload Fields

| Field | Type | Constraints | Description |
|---|---|---|---|
| `cta_url` | string | valid URI | Primary action URL. Platespinner renders as a "→ Open" button. |
| `cta_kind` | string | see CTA Kind Vocabulary | Machine-readable hint for how to handle the URL. |
| `correlation_id` | string | stable within a gate lifecycle | Opaque ID for this gate/action instance. Shared between the `emit` step and the poll script. |
| `expires_at` | string | ISO 8601 datetime | When this signal is no longer actionable. |
| `disposition` | string | `pending` \| `resolved` \| `expired` | Lifecycle state. Defaults to `pending` if absent. |
| `details` | object | any | Freeform enrichment dict for tooling and diagnostics (e.g. `pr_number`, `repo`, `work_item_id`). |

### Kind Vocabulary

| Kind | When to use |
|---|---|
| `pr_review_required` | A PR is awaiting reviewer approval |
| `pr_checks_pending` | CI/CD checks running on a PR |
| `gate_pending` | A workflow gate is open and awaiting resolution |
| `work_item_pending` | An ADO work item needs a state transition |
| `run_completed` | A workflow run completed successfully |
| `run_failed` | A workflow run failed |
| `ci_awaited` | Waiting for a CI run to complete |
| `merge_conflict` | A PR has a merge conflict requiring resolution |

New kinds may be added without a version bump as long as existing consumers
ignore unknown values.

### CTA Kind Vocabulary

| CTA Kind | Meaning for platespinner |
|---|---|
| `review_pr` | Open PR page and expect the user to leave a review |
| `open_pr` | Open PR page (no specific action expected) |
| `open_gate` | Open the conductor gate interaction UI |
| `open_run` | Open a CI or conductor run page |
| `open_work_item` | Open an ADO work item |
| `external` | Open an arbitrary external URL |

### Complete Wire Example (payload only — goes inside conductor envelope's `payload` field)

```json
{
  "kind": "pr_review_required",
  "severity": "warning",
  "title": "PR Review Required",
  "message": "PR #123 (feat/my-feature) is awaiting at least one reviewer approval before the workflow can continue.",
  "cta_url": "https://github.com/org/repo/pull/123",
  "cta_kind": "review_pr",
  "correlation_id": "a3f1b2c4:pr-review:AB#4567",
  "expires_at": "2026-05-29T23:43:14Z",
  "disposition": "pending",
  "details": {
    "pr_number": 123,
    "repo": "org/repo",
    "work_item_id": "AB#4567"
  }
}
```

### Full `.notifications.jsonl` Line (for platespinner implementors)

Grounded in the dogfood smoke capture (`.squad/experiments/dogfood-smoke/README.md`,
2026-05-28). Field order matches actual conductor output.

```json
{"type":"notification","timestamp":1748472194.42,"data":{"emission_id":"a3f1b2c4:notify_review_required:1","schema_id":"polyphony.feature_pr.pr_review_required@1","notification_type":"pr_review_required","namespace":"polyphony.feature_pr","version":1,"run_id":"a3f1b2c4","workflow":"feature-pr","source_agent":"notify_review_required","subworkflow_path":[],"correlation":{"root_id":"AB#4567"},"workflow_metadata":{},"payload":{"kind":"pr_review_required","severity":"warning","title":"PR Review Required","message":"PR #123 (feat/my-feature) is awaiting at least one reviewer approval before the workflow can continue.","cta_url":"https://github.com/org/repo/pull/123","cta_kind":"review_pr","correlation_id":"a3f1b2c4:pr-review:AB#4567","expires_at":"2026-05-29T23:43:14Z","disposition":"pending","details":{"pr_number":123,"repo":"org/repo","work_item_id":"AB#4567"}}}}
```

### Workflow YAML Declaration (canonical `emit:` syntax)

```yaml
workflow:
  name: feature-pr
  notifications:
    namespace: polyphony.feature_pr
    correlation:
      - root_id
    types:
      pr_review_required:
        version: 1
        payload:
          kind:           {type: string}
          severity:       {type: string}
          title:          {type: string}
          message:        {type: string}
          cta_url:        {type: string}
          cta_kind:       {type: string}
          correlation_id: {type: string}
          expires_at:     {type: string}
          disposition:    {type: string}
          details:        {type: object}

agents:
  - name: notify_review_required
    type: emit                      # dogfood pre-refresh: type: notification
    emit: pr_review_required        # dogfood pre-refresh: notification: pr_review_required
    payload:
      kind: pr_review_required
      severity: warning
      title: "PR Review Required"
      message: "PR #{{ workflow.input.pr_number }} is awaiting review before the workflow continues."
      cta_url: "{{ workflow.input.pr_url }}"
      cta_kind: review_pr
      correlation_id: "{{ workflow.input.run_id }}:pr-review:{{ workflow.input.root_id }}"
      expires_at: "{{ workflow.input.deadline }}"
      disposition: pending
      details:
        pr_number: "{{ workflow.input.pr_number }}"
        repo: "{{ workflow.input.repo }}"
    routes:
      - to: poll_pr_approved
```

---

## One-Way Contract

The domain signal envelope travels in ONE direction only:

```
polyphony workflow → conductor → .notifications.jsonl → platespinner → toast/UI → user
```

No system downstream of the `.notifications.jsonl` write (platespinner, toast
subscribers, external tooling) writes back to conductor or to polyphony.
Resolution of the gate is signalled by **external state change** (PR merged,
review approved) that is detected by the **poll script running inside the
workflow** — not by platespinner.

---

## Alternatives Considered

**Reuse conductor's error envelope shape:** Rejected. The error envelope is
`{kind, message, details}` — no severity, no CTA, no lifecycle. The domain
signal envelope serves a fundamentally different purpose (user notification vs
error routing).

**Minimal payload (title + message only):** Rejected. Without `cta_url`, the
gate-compression pattern cannot surface actionable notifications. Without
`correlation_id`, poll scripts cannot safely associate a pending signal with
their current run context. Without `disposition`, platespinner cannot manage
signal lifecycle (resolved signals should not fire new toasts).

**CTA URL as a top-level conductor field:** Rejected. Conductor's envelope
fields (schema_id, emission_id, correlation) are engine-level concepts.
Polyphony-specific semantics belong in `payload`. This keeps the polyphony
contract additive on top of conductor without requiring conductor changes.

---

## Consequences

### Positive
- Platespinner can render CTA-bearing notifications from polyphony domain
  signals (closes design dependency for issues #541 and #542)
- Poll scripts can correlate signals to their gate instance via `correlation_id`
- `disposition` enables lifecycle management — platespinner can suppress new
  toasts for resolved/expired signals
- The schema is additive: new optional fields can be added without version bumps

### Negative
- Workflow authors must set `expires_at` explicitly from workflow input; there
  is no engine-level TTL (see Open Questions in `gate-compression-pattern` ADR)
- `details` is freeform — not schema-validated — which trades safety for
  flexibility

---

## Evolution Policy

1. **Adding new optional fields:** permitted without a `version` bump on the
   `NotificationTypeDef`. All consumers MUST ignore unknown fields.
2. **Adding new `kind` or `cta_kind` values:** permitted without version bump.
   Consumers that switch on `kind` MUST have a default/fallback case.
3. **Changing field type or making an optional field required:** requires a
   `version` bump on the `NotificationTypeDef` in the workflow YAML.
4. **Removing fields:** requires a `version` bump AND a migration plan for
   existing consumers.

---

## Invariants

1. **`payload` is polyphony-owned.** The conductor engine wraps it but does
   not interpret it. Only polyphony and its downstream consumers (platespinner)
   may define field semantics.
2. **Consumers MUST ignore unknown fields.** Forward compatibility is non-
   negotiable.
3. **`kind` and `severity` are ALWAYS required.** A domain signal without
   these fields is a polyphony implementation defect.
4. **`cta_url` MUST be present when `cta_kind` is present.** `cta_kind` is
   meaningless without a URL.
5. **No write-back.** No system that reads a domain signal from
   `.notifications.jsonl` writes to conductor, polyphony, or any conductor-
   managed file.
