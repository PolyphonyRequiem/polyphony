# Platespinner Gap Work — Handoff Prompt

**Date:** 2026-05-28  
**From:** Bach (Architect, polyphony-squad-spike)  
**For:** Fresh agent session running against `C:\Users\dangreen\projects\conductor-platespinner`  
**Repo:** `PolyphonyRequiem/conductor-platespinner` (v0.4.14, MIT)

---

## READ THIS FIRST — You Are a Standalone Agent

You have no context from the polyphony squad. Everything you need is in this
document. Do not ask for clarification before starting to code. Form your own
sub-team if it helps, but ship the changes in the platespinner repo.

---

## 1. Context — What Is Changing Upstream

**Conductor** (the workflow orchestration engine that platespinner monitors) is
receiving two upstream PRs:

- **PR #229** — `on_error:` routing: conductor YAML workflows can now declare
  typed error handlers instead of surfacing every failure as a human gate.
- **PR #213** — user-defined domain notifications: conductor YAML workflows can
  now emit structured notification envelopes (eventually renamed `emit:` via
  commit `27006af`; the current dogfood still uses `type: notification` /
  `notification:` field names — see §5).

**Polyphony** (an SDLC workflow suite built on conductor) will use PR #213 to
emit what it calls **domain signals**: structured, user-actionable notifications
that carry CTA URLs (e.g., "review this PR", "inspect this gate"), correlation
IDs, severity, and lifecycle metadata. These are richer than platespinner's
current notifications, which are derived purely from run state flags
(gate_pending, dialog_waiting, etc.).

The headline unlock is **gate compression**: polyphony's workflow YAML will
replace many `human_gate` nodes with a `(emit domain signal + script-poll-loop)`
pattern. The poll script checks whether the external condition (PR merged, CI
green, review approved) has resolved. Platespinner's job is to surface the
domain signal as a CTA-bearing notification so the user knows what action to
take. The user takes the action; the poll script detects it; the workflow
continues automatically. **No write-back from platespinner to conductor is
ever needed — the user's external action is what the poll detects.**

**Your mission:** Close GitHub issues #541 and #542 on the platespinner repo
by adding domain-signal-aware rendering to the notification system.

---

## 2. The Two Gap Issues (Full Text)

### Issue #541 — CTA-Aware Rendering

**Title:** Notifications need CTA (call-to-action) button rendering  
**Opened by:** Bach  
**Body:**

> Conductor's upcoming notification support (PR #213) allows workflows to emit
> structured notification envelopes that carry `cta_url` and `cta_kind` fields
> in the payload. Polyphony will use this to emit "domain signals" — e.g.
> "PR #42 needs your review" with a link to the PR page.
>
> Currently platespinner's `AppNotification` type has no `cta_url` field, and
> `NotificationFeed.tsx` renders notifications as text-only. There is no CTA
> button.
>
> **Needed:**
> - Add `cta_url?: string` and `cta_kind?: string` to `AppNotification`
> - `NotificationFeed.tsx` renders a small "→ Open" button when `cta_url` is
>   present; button opens the URL in a new tab
> - `fireBrowserNotification` passes `data: { cta_url }` in the
>   `NotificationOptions` so service-worker-delivered toasts can also carry
>   the URL
> - Backend `/api/notifications` (new endpoint) or SSE extension surfaces
>   domain signals from `*.notifications.jsonl` files in the conductor temp dir
>
> This is a pure addition; no existing notification types are affected.

### Issue #542 — Deep-Link Context

**Title:** Notification clicks must deep-link to the right place  
**Opened by:** Bach  
**Body:**

> Clicking a notification currently calls `selectRun(n.runLogFile)`, which
> opens the run detail view inside platespinner. That is fine for run-level
> notifications (completed, failed). For domain signals it is insufficient:
>
> - A "PR review required" signal should open the PR URL in a new browser tab,
>   not just jump to the run in platespinner.
> - A "gate pending" signal should open the gate-interaction page (conductor
>   web UI or ADO work item) — NOT just the platespinner run view.
> - "Run completed" and "run failed" signals have no `cta_url` and should
>   continue to navigate within platespinner as before.
>
> **Needed:**
> - Click handler in `NotificationFeed.tsx` inspects `n.cta_url`:
>   - If present: `window.open(n.cta_url, '_blank', 'noopener')` + mark read
>   - If absent: existing `selectRun(n.runLogFile)` behavior unchanged
> - Service worker (if active) should handle notification click with
>   `clients.openWindow(cta_url)` fallback for PWA installs
> - Windows tray toast (if relevant): include cta_url as the launch action URL
>
> This issue pairs with #541; both must ship together for domain signals to be
> useful.

---

## 3. Contract Spec — The Domain Signal Envelope

### What Conductor Writes

When a polyphony workflow step fires a `type: notification` node (or `type: emit`
after PR #213's `27006af` rename lands), conductor writes one JSON line to a
**dedicated file** alongside the run's `.events.jsonl`:

```
{TMPDIR}/conductor/conductor-{workflow_name}-{YYYYMMDD-HHMMSS}-{run_id}.notifications.jsonl
```

On Windows, `{TMPDIR}` is `%TEMP%` (typically `C:\Users\{user}\AppData\Local\Temp`).

Each line is a serialized `WorkflowEvent`:
```json
{
  "type": "notification",
  "timestamp": 1748472194.123,
  "data": { /* conductor envelope — see below */ }
}
```

### The Conductor Envelope (data field)

The `data` field is the conductor notification envelope built by
`NotificationExecutor.build_envelope()`. The following fields were confirmed
by the dogfood smoke capture at `.squad/experiments/dogfood-smoke/README.md`:

| Field | Type | Notes |
|---|---|---|
| `emission_id` | string | `"{run_id}:{agent_name}:{iteration}"` — deterministic, dedup key |
| `schema_id` | string | `"{namespace}.{notification_type}@{version}"` — stable type identifier |
| `notification_type` | string | e.g. `"pr_review_required"` |
| `namespace` | string | e.g. `"polyphony.feature_pr"` |
| `version` | int | starts at 1; bumped on breaking payload changes |
| `run_id` | string | hex run identifier shared with the `.events.jsonl` stem |
| `workflow` | string | workflow name string |
| `source_agent` | string | name of the YAML step that fired |
| `subworkflow_path` | string[] | nesting path for sub-workflow steps |
| `correlation` | object | workflow-declared correlation keys, e.g. `{"root_id": "AB#4567"}` |
| `workflow_metadata` | object | engine-supplied metadata dict (may be empty `{}`) |
| `payload` | object | **polyphony-owned domain signal fields — see below** |

### The Domain Signal Payload (payload field)

This is the polyphony-owned contract, defined by polyphony's
`domain-signal-envelope` ADR.

**Required fields:**

| Field | Type | Values |
|---|---|---|
| `kind` | string | machine-readable signal type: `pr_review_required`, `gate_pending`, `run_completed`, `run_failed`, `ci_awaited`, `work_item_pending` |
| `severity` | string | `info` \| `warning` \| `error` \| `critical` |
| `title` | string | ≤80 chars, human-readable |
| `message` | string | full human-readable body |

**Optional fields:**

| Field | Type | Notes |
|---|---|---|
| `cta_url` | string (URI) | the primary action URL — open in new tab |
| `cta_kind` | string | `review_pr` \| `open_pr` \| `open_gate` \| `open_run` \| `open_work_item` \| `external` |
| `correlation_id` | string | stable opaque ID for this gate/action lifecycle |
| `expires_at` | string (ISO 8601) | when this signal is no longer actionable |
| `disposition` | string | `pending` \| `resolved` \| `expired` |
| `details` | object | freeform enrichment dict (e.g. pr_number, repo, work_item_id) |

**Consumers MUST ignore unknown fields** (forward-compatibility invariant).

### Example `.notifications.jsonl` Entries

These are grounded in the real smoke-capture at
`.squad/experiments/dogfood-smoke/README.md` (run 2026-05-28, conductor
v0.1.17 dogfood). Field order and shape match actual output; polyphony-specific
CTA fields are projected forward from the domain-signal-envelope ADR.

**Example 1 — PR review required (typical gate-compression signal):**
```json
{"type":"notification","timestamp":1748472194.42,"data":{"emission_id":"a3f1b2c4:notify_review_required:1","schema_id":"polyphony.feature_pr.pr_review_required@1","notification_type":"pr_review_required","namespace":"polyphony.feature_pr","version":1,"run_id":"a3f1b2c4","workflow":"feature-pr","source_agent":"notify_review_required","subworkflow_path":[],"correlation":{"root_id":"AB#4567"},"workflow_metadata":{},"payload":{"kind":"pr_review_required","severity":"warning","title":"PR Review Required","message":"PR #123 (feat/my-feature) is awaiting at least one reviewer approval before the workflow can continue.","cta_url":"https://github.com/org/repo/pull/123","cta_kind":"review_pr","correlation_id":"a3f1b2c4:pr-review:AB#4567","expires_at":"2026-05-29T23:43:14Z","disposition":"pending","details":{"pr_number":123,"repo":"org/repo","work_item_id":"AB#4567"}}}}
```

**Example 2 — CI check awaited (informational, no user action required):**
```json
{"type":"notification","timestamp":1748472290.01,"data":{"emission_id":"a3f1b2c4:notify_ci_awaited:1","schema_id":"polyphony.feature_pr.ci_awaited@1","notification_type":"ci_awaited","namespace":"polyphony.feature_pr","version":1,"run_id":"a3f1b2c4","workflow":"feature-pr","source_agent":"notify_ci_awaited","subworkflow_path":[],"correlation":{"root_id":"AB#4567"},"workflow_metadata":{},"payload":{"kind":"ci_awaited","severity":"info","title":"CI Running","message":"Waiting for CI checks to complete on PR #123. The workflow will continue automatically when they pass.","cta_url":"https://github.com/org/repo/actions/runs/98765","cta_kind":"open_run","correlation_id":"a3f1b2c4:ci:AB#4567","expires_at":"2026-05-29T23:43:14Z","disposition":"pending","details":{"pr_number":123,"run_id":"98765"}}}}
```

**Example 3 — Gate resolved (lifecycle update — disposition changes to resolved):**
```json
{"type":"notification","timestamp":1748473800.77,"data":{"emission_id":"a3f1b2c4:notify_review_required:2","schema_id":"polyphony.feature_pr.pr_review_required@1","notification_type":"pr_review_required","namespace":"polyphony.feature_pr","version":1,"run_id":"a3f1b2c4","workflow":"feature-pr","source_agent":"notify_review_required","subworkflow_path":[],"correlation":{"root_id":"AB#4567"},"workflow_metadata":{},"payload":{"kind":"pr_review_required","severity":"info","title":"PR Review Approved","message":"PR #123 received an approval. Workflow continuing.","cta_url":"https://github.com/org/repo/pull/123","cta_kind":"review_pr","correlation_id":"a3f1b2c4:pr-review:AB#4567","disposition":"resolved","details":{"pr_number":123,"repo":"org/repo"}}}}
```

> **⚠️ Transition caveat (2026-05-28):** As of this date, the dogfood may
> briefly emit `type: notification` (and the step field `notification: <name>`)
> until Mahler's dogfood refresh incorporating commit `27006af` lands. The
> receiving agent should target `type: emit` per the contract — both keys will
> be synonyms during the brief transition window. The output stream filename
> `.notifications.jsonl` may also be renamed by upstream; verify against the
> current dogfood at integration time by inspecting
> `conductor-dogfood/src/conductor/engine/event_log.py`.

### One-Way Contract — THIS IS LOAD-BEARING

```
conductor → .notifications.jsonl → platespinner → toast/CTA → user → external action → poll script → conductor
```

**Platespinner is a read-only observer.** It reads `.notifications.jsonl` and
renders UI. It NEVER writes back to conductor, NEVER calls conductor APIs,
NEVER emits events that conductor consumes.

The user takes an action (clicks a PR link, approves a review, merges a PR).
That action is detected by a **poll script running inside the conductor
workflow** — not by platespinner. Platespinner has no role in that detection.

**DO NOT implement:**
- Any callback from platespinner to conductor
- Any RPC, webhook, or HTTP call from platespinner to conductor
- Any write to `.events.jsonl` or any conductor file
- Any mechanism for platespinner to "resolve" a domain signal on conductor's behalf

If you find yourself writing code that sends data from platespinner to
conductor, stop. That is out of scope by architectural invariant.

---

## 4. Platespinner Codebase Orientation

**Repo root:** `C:\Users\dangreen\projects\conductor-platespinner`  
**Version:** v0.4.14  
**Backend:** Python/FastAPI in `platespinner/server.py`  
**Frontend:** React/TypeScript in `frontend/src/`

Key files for this work:

| File | What it is |
|---|---|
| `platespinner/paths.py` | Path conventions — where conductor writes files. Add a `notifications_files()` helper here. |
| `platespinner/server.py` | Backend SSE + REST endpoints. Add `/api/domain-signals` endpoint or fold into existing SSE. |
| `frontend/src/stores/notification-store.ts` | `AppNotification` type + Zustand store. Add `cta_url`, `cta_kind`, `correlation_id` fields here. |
| `frontend/src/hooks/use-gate-notifications.ts` | Derives notifications from run state. Domain signals come from a SEPARATE source — don't break this. |
| `frontend/src/components/notifications/NotificationFeed.tsx` | Renders the notification feed. Add CTA button here. |

**How notifications currently work (do not break this):**

1. Backend (`server.py`) tails `*.events.jsonl` files and exposes run state via SSE
2. Frontend hook (`use-gate-notifications.ts`) watches SSE data for `gate_pending`,
   `dialog_waiting`, `run completed`, `run failed` state changes and pushes
   `AppNotification` items into the Zustand store
3. `NotificationFeed.tsx` renders the store items as a bell-menu popup

**Domain signals are a NEW parallel channel** — they come from `*.notifications.jsonl`
files, NOT from the existing SSE run-state stream. Do not conflate the two.

### `paths.py` — What Needs to Change

The existing `conductor_events_dir()` returns `Path(tempfile.gettempdir()) / "conductor"`.
Domain signal files live in the SAME directory with a different filename pattern:
`conductor-{workflow_name}-{ts}-{run_id}.notifications.jsonl`

Add to `paths.py`:
```python
def conductor_notifications_glob() -> str:
    """Glob pattern for conductor domain-signal JSONL files."""
    return str(conductor_events_dir() / "*.notifications.jsonl")
```

---

## 5. Dogfood / Local Testing

**Dogfood conductor install:**
```
C:\Users\dangreen\projects\conductor-dogfood
Branch: dogfood/on-error+notifications
Installed version: Conductor v0.1.17 (reports 0.1.17 but includes PR #213 + PR #229 features)
```

**Install dogfood conductor for local testing:**
```powershell
cd C:\Users\dangreen\projects\conductor-dogfood
pip install -e .
conductor --version  # → Conductor v0.1.17
```

**⚠️ IMPORTANT — Current field names in dogfood:**

The dogfood was built from an earlier PR #213 commit, BEFORE the
`notification:` → `emit:` rename (`27006af`). The **actual YAML field names
in the dogfood are:**

```yaml
- name: my_step
  type: notification          # NOT type: emit (yet)
  notification: pr_ready      # NOT emit: pr_ready (yet)
  payload:
    kind: pr_review_required
    severity: warning
    title: "PR Review Required"
    ...
```

When PR #213's rename commit (`27006af`) is incorporated, these become
`type: emit` / `emit:`. The **envelope format in `.notifications.jsonl` does
NOT change** — only the YAML authoring syntax changes. Platespinner reads the
envelope, not the YAML, so you can build against the dogfood now and the rename
will be transparent.

**Polyphony repo with smoke workflow:**
```
C:\Users\dangreen\projects\polyphony
```

The polyphony repo has the workflows in `.conductor/registry/workflows/`.
A smoke workflow using `type: notification` doesn't yet exist in polyphony's
repo (Wagner is authoring those as patterns #543–#545 on GitHub). For local
testing, create a minimal test workflow in a scratch directory:

```yaml
# scratch-test-notification.yaml
# Uses canonical emit: syntax (type: emit / emit: <name>).
# If Mahler's dogfood refresh has not landed yet, temporarily use
# type: notification / notification: <name> — see transition caveat above.
workflow:
  name: test-notification
  entry_point: fire_signal
  runtime:
    provider: copilot
  limits:
    max_iterations: 5
  notifications:
    namespace: polyphony.test
    correlation: []
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
          disposition:    {type: string}

agents:
  - name: fire_signal
    type: emit                      # pre-refresh dogfood: type: notification
    emit: pr_review_required        # pre-refresh dogfood: notification: pr_review_required
    payload:
      kind: pr_review_required
      severity: warning
      title: "PR Review Required"
      message: "PR #42 needs your review before the workflow continues."
      cta_url: "https://github.com/test/repo/pull/42"
      cta_kind: review_pr
      correlation_id: "test-run:pr-review:AB#0001"
      disposition: pending
    routes:
      - to: $end
```

Run it:
```powershell
conductor run scratch-test-notification.yaml --input '{}'
```

This will produce a `.notifications.jsonl` file in `$env:TEMP\conductor\`.
Point platespinner at that directory and verify the notification appears.

---

## 6. Definition of Done

Both issues close when ALL of the following are true:

### Backend (Issue #541 + #542 backend prerequisite)
- [ ] `paths.py` has a `conductor_notifications_glob()` helper
- [ ] `server.py` has a polling reader (file-watch or periodic scan) that:
  - Finds all `*.notifications.jsonl` files in `conductor_events_dir()`
  - Parses each newline as `{"type": "notification", "timestamp": ..., "data": {...envelope...}}`
  - Extracts `data.payload.kind`, `data.payload.severity`, `data.payload.title`,
    `data.payload.message`, `data.payload.cta_url`, `data.payload.cta_kind`,
    `data.payload.correlation_id`, `data.payload.disposition`, `data.emission_id`
  - De-duplicates by `emission_id` (same signal must not be surfaced twice)
  - Exposes them to the frontend via a new `/api/domain-signals` endpoint
    OR via the existing SSE stream (either approach is acceptable)
- [ ] Unknown payload fields are silently ignored (forward-compatibility)
- [ ] Signals with `disposition: resolved` or `disposition: expired` are
  surfaced with visual distinction (or filtered, designer's call) — they MUST
  NOT fire new toasts

### Frontend / `notification-store.ts` (Issue #541)
- [ ] `AppNotification` gains `cta_url?: string`, `cta_kind?: string`,
  `correlation_id?: string` fields
- [ ] Domain signals from the new backend endpoint are pushed into the store
  as `AppNotification` items with `type: 'domain_signal'` (new type — add it
  to `NotificationType`)

### `NotificationFeed.tsx` (Issue #541)
- [ ] When `n.cta_url` is present, the notification item renders a small
  "→ Open" button (or equivalent) to the right of the body text
- [ ] `TYPE_STYLE` gains an entry for `domain_signal` with an appropriate
  icon (e.g. `🔔`) and accent color mapped from severity:
  `info` → blue, `warning` → yellow, `error` → red, `critical` → red+pulse
- [ ] The CTA button is SEPARATE from the notification row click handler;
  clicking the button opens `cta_url` in a new tab; clicking the row still
  navigates to the run in platespinner

### Click behavior (Issue #542)
- [ ] Notification row click handler: if `n.cta_url` is present, call
  `window.open(n.cta_url, '_blank', 'noopener')` AND `selectRun(n.runLogFile)`;
  if absent, just `selectRun(n.runLogFile)` as before
- [ ] `fireBrowserNotification` includes `data: { cta_url }` in
  `NotificationOptions` when `cta_url` is set
- [ ] Service worker's `notificationclick` handler (if one exists) calls
  `clients.openWindow(cta_url)` when `event.notification.data.cta_url` is present

### Testing
- [ ] At least one unit test covers `AppNotification` with `cta_url` set
- [ ] At least one integration or snapshot test covers `NotificationFeed`
  rendering a domain signal with a CTA button
- [ ] Existing notification tests (`gate`, `dialog`, `completed`, `failed`)
  still pass

---

## 7. Out of Scope for Platespinner

These are explicitly NOT your concern:

- **Writing or modifying conductor workflow YAML** — that is Wagner's domain
  (polyphony issues #543, #544, #545)
- **Changing the conductor notification envelope schema** — that is frozen in
  upstream PR #213. The `data` object shape in `.notifications.jsonl` is
  upstream's contract; polyphony owns only the `payload` sub-fields
- **Polyphony CLI verb behavior** — that is Mozart's domain
- **The `on_error:` routing design** — that is upstream PR #229, conductor's
  concern
- **Renaming `type: notification` → `type: emit`** in any YAML — that is
  upstream's rename, transparent to platespinner
- **Writing back to conductor** — as stated in §3, NEVER

If a task you are asked to do requires writing to conductor, sending HTTP
requests to conductor, or modifying polyphony's YAML — stop and flag it. It
is out of scope.
