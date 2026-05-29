# ADR: Seed Manifest as Durable State

**Status:** Accepted  
**Date:** 2026-05-29  
**Authors:** Bach (Architect)  
**Synthesized from:** four-lens debate — Bach (architecture), Mahler (conductor engine),
Mozart (verb error circuit), Sibelius (platform reality); Beethoven (terminal semantics)  
**Implements:** durable seeding state for PR #535 (manifest-aware seeder rebase)  
**Related ADRs:** `polyphony-verb-error-boundary.md`, `gate-compression-pattern.md`,
`domain-signal-envelope.md`

---

## Context

`polyphony plan seed-children` creates ADO work items serially via
`twig.CreateChildAsync`. Each call is a separate REST round-trip with no
transactional guarantee. A partial seed — three of five children created before a
network drop — leaves ADO and the workflow in an indeterminate state: the system
cannot distinguish "planner never intended this child" from "seeder failed before
getting to it."

The current recovery is to re-run the root item dispatch from scratch. Without a
durable record of the planner's intent, re-entry has two bad options: trust ADO
(which may be incomplete) or re-run the planner (which may produce a different plan
under changed conditions, silently generating duplicate children).

### Three specific problems

**1. Partial-seed ambiguity.** On restart, the seeder cannot tell which children it
already created vs which it still needs to create. Re-running blind risks creating
duplicate children when the network recovers.

**2. Corrupt-state forward motion.** Without a reconciliation gate, a partially
seeded root can advance to implementation (via `polyphony state next-ready`) while
children are missing. The directive is unambiguous: "don't let corrupt state just
move forward."

**3. Renegotiation orphan accumulation.** When the architect replans, old children
remain in ADO with their `polyphony:plan-child-id` markers. Without tracking which
generation introduced each child, the seeder cannot distinguish valid children from
prior-generation orphans. Unchecked, renegotiation + re-seed creates duplicates.

### What the ADO tree alone cannot tell you

The ADO work item tree is the "actual state" — what was created. It cannot answer:
- "Was child X intended by the current plan, or by a prior plan that has since been
  superseded?"
- "Which children should exist but don't yet?"
- "Was the seeder interrupted, or did it finish with a genuinely different scope?"

The seed manifest is the "desired state" — the planner's declared intent, frozen at
planning time. It is the missing half of the reconciliation pair.

### What the conductor engine does NOT provide

Conductor's `CheckpointManager` stores to `$TMPDIR` keyed to workflow name +
timestamp. It is crash recovery for a single run, not durable cross-run state. It is
gone on reboot. There is no built-in mechanism for "this artifact survives across
separate runs of the same root."

Conductor's `max_attempts` / `RetryPolicy` apply only to provider-backed agent nodes,
not `type: script` nodes (confirmed against `schema.py` line 449, 843). There is no
`on_exhaust` field in the schema. Script-internal retry loops are the idiomatic and
fully supported pattern.

---

## Decision

The **seed manifest** is a JSON file written by `polyphony plan write-plan` and
consumed by `polyphony plan seed-children`. It records the planner's declared intent
for a root item's children — what should exist in ADO after seeding is complete — and
is patched in place as seeding succeeds. It is the reconciliation anchor for both
retry runs and renegotiation passes.

### Three founding principles

1. **Desired state, not observed state.** The manifest records what the planner
   declared. ADO records what was actually created. The seeder closes the gap between
   them.

2. **Replace on renegotiation, don't patch.** A new plan generation produces a new
   manifest wholesale. The prior manifest is not migrated. The new generation starts
   from scratch with full awareness of current ADO state.

3. **Filesystem is the entire persistence layer.** One JSON file per root item, in
   the repo worktree. Atomic writes via `.tmp` + rename. Survives process restarts and
   reboots. No database, no conductor-native artifact store.

---

## Schema

### Manifest file path

```
.polyphony/state/{rootId}/seed-manifest.json
```

where `{rootId}` is the polyphony root item identifier (e.g. `AB#1234`), matching the
directory layout established for per-root run state.

### Write discipline

Writes are **atomic**: write to `.polyphony/state/{rootId}/seed-manifest.json.tmp`,
then rename to the final path. A `.tmp` file that persists on startup signals a
failed prior write; the seeder treats this as "no valid manifest" and routes back to
the `write-plan` step rather than attempting to read a corrupt file.

### Top-level schema

```json
{
  "plan_generation": {
    "id": "gen-1",
    "parent": null,
    "cause": "initial",
    "created_at": "2026-05-29T10:16:22-07:00"
  },
  "created_at": "2026-05-29T10:16:22-07:00",
  "root_id": "AB#1234",
  "items": [
    {
      "child_id": "task-1",
      "type": "Task",
      "title": "Implement input validation",
      "parent_id": "AB#1234",
      "facets": ["actionable"],
      "introduced_in": "gen-1",
      "ado_id": 5678
    },
    {
      "child_id": "task-2",
      "type": "Task",
      "title": "Write unit tests for validation",
      "parent_id": "AB#1234",
      "facets": ["actionable"],
      "introduced_in": "gen-1",
      "ado_id": null
    }
  ]
}
```

### Field definitions — top level

| Field | Type | Required | Description |
|---|---|---|---|
| `plan_generation` | object | yes | Generation descriptor for this manifest. See sub-schema below. |
| `created_at` | string | yes | ISO 8601 datetime when this manifest was written. Records the planning decision time, not seeding completion time. |
| `root_id` | string | yes | Polyphony root item identifier (e.g. `"AB#1234"`). Redundant with file path; present for standalone readability and integrity checks. |
| `items` | array | yes | One entry per planned child. Treated as a set by the seeder — order is not significant. |

### Field definitions — `plan_generation`

| Field | Type | Constraints | Description |
|---|---|---|---|
| `id` | string | non-empty, stable within root | Generation identifier. Convention: monotonic `gen-N` starting at `gen-1`. Assigned by the planner verb. |
| `parent` | string \| null | null for first generation | The `id` of the prior generation this one supersedes. Forms a linked chain. |
| `cause` | string | see Cause Vocabulary | Why this generation was created. |
| `created_at` | string | ISO 8601 | When this generation was declared. |

**Cause vocabulary:**

| Value | Meaning |
|---|---|
| `initial` | First planning pass for this root item |
| `architect_replan` | Architect agent produced a revised plan |
| `manual_edit` | Operator edited the plan outside the normal workflow |
| `recovery` | Seeding failed; plan re-declared to enable a clean retry |

### Field definitions — `items[]`

| Field | Type | Constraints | Description |
|---|---|---|---|
| `child_id` | string | non-empty, stable within a generation | The `polyphony:plan-child-id` marker value. Canonical cross-run identity for this planned child. |
| `type` | string | valid ADO work item type for target project | e.g. `"Task"`, `"User Story"`, `"Bug"`. Must match the process template of the ADO project. |
| `title` | string | non-empty | Planned display title. May diverge from the ADO title if a human renames post-seeding; the marker is authoritative. |
| `parent_id` | string | non-empty | ADO work item ID of the intended parent (e.g. `"AB#1234"`). |
| `facets` | string[] | non-empty | Polyphony facets assigned to this child (e.g. `["actionable"]`). |
| `introduced_in` | string | matches a `plan_generation.id` | Which generation declared this item. Used for orphan detection on renegotiation. |
| `ado_id` | integer \| null | null until seeded | ADO work item ID assigned on creation. Patched by the seeder on each successful creation. Null means "not yet seeded." |

---

## The Reconciliation Primitive

### Why `polyphony:plan-child-id`, not title hash

Sibelius confirmed that the `<!-- polyphony:plan-child-id=task-N -->` marker is
embedded atomically in the ADO work item description by `twig.CreateChildAsync`. If
the item exists in ADO, the marker is there — the description is assembled before the
`twig new` call and the marker is included in that assembly. The marker survives
renames. The existing `BuildIndexes` method already uses it for same-run
reconciliation. `twig show-tree` returns children with description fields and
`BuildIndexes` parses the markers.

The seed manifest records what `child_id` values were planned. The seeder's existing
marker-lookup handles cross-run reconciliation without a new comparison primitive.
This is a reuse of tested code, not a new surface.

**Title hashing is explicitly rejected.** Humans rename titles post-seeding;
architects wordsmith during replanning. A hash primitive produces false "unmatched"
results on legitimate renames and requires a normalization function that becomes a
hidden compatibility surface. The marker is stronger and already present.

---

## Lifecycle

```
1. polyphony plan write-plan
      → architect agent runs, declares children
      → plans/{id}.children.json written (existing behavior)
      → seed manifest written to .polyphony/state/{rootId}/seed-manifest.json (atomic)
      → manifest has all items with ado_id: null

2. polyphony plan seed-children  (first run or any subsequent restart)
      → startup guard (see below)
      → reads manifest
      → queries ADO via twig show-tree + BuildIndexes marker lookup
      → reconciliation pass: match manifest items to ADO children by marker
      → for matched items: patch ado_id in manifest (atomic write after each patch)
      → for unmatched items (gaps): seed via twig.CreateChildAsync, patch ado_id on success
      → if all items converged → stamp polyphony:planned on parent → exit 0
      → if retry budget exhausted on gaps → emit seeding.infra_exhausted → exit non-zero

3. Renegotiation (architect replan)
      → polyphony plan write-plan runs again
      → new manifest written, plan_generation bumped (new id, parent = prior id)
      → prior manifest REPLACED (not patched)
      → seeder called against new manifest detects prior-gen children as orphan candidates
```

### Startup guard

On `polyphony plan seed-children` invocation, before any ADO calls:

1. Does `.polyphony/state/{rootId}/seed-manifest.json` exist?  
   — No → exit 2 (config error). Workflow routes to `write-plan`.
2. Does `.polyphony/state/{rootId}/seed-manifest.json.tmp` exist?  
   — Yes → prior atomic write failed. Exit 2. Workflow routes to `write-plan`.
3. Manifest parses successfully?  
   — No → exit 2. Workflow routes to `write-plan`.
4. `root_id` in manifest matches the `--root-id` argument?  
   — No → exit 2 (config error — wrong manifest for this root).
5. All guards pass → proceed to reconciliation.

The seeder never enters the seeding loop with a corrupt, absent, or mismatched
manifest. This is the "don't let corrupt state just move forward" guard.

---

## Reconciliation Algorithm

```
function reconcile(manifest, adoChildren):
    currentGenId = manifest.plan_generation.id
    gaps = []

    for item in manifest.items:
        adoMatch = adoChildren.find(c => c.marker == item.child_id)
        if adoMatch:
            item.ado_id = adoMatch.ado_id      // already seeded — patch in memory
        else:
            gaps.append(item)                  // not found in ADO — needs seeding

    return gaps
```

```
function seedGaps(gaps, manifest, maxAttempts=3):
    for item in gaps:
        attempts = 0
        seeded = false

        while attempts < maxAttempts and not seeded:
            result = twig.CreateChildAsync(item.parent_id, item.type, item.title)

            if result.success:
                item.ado_id = result.ado_id
                manifest.writeAtomic()         // patch manifest on disk after each success
                seeded = true

            elif result.exitCode == 1:         // unclassified crash — no retry
                raise PermanentSeedError(item, result)

            elif result.exitCode in [3, 5]:    // twig unavailable or ADO unreachable
                attempts++
                if attempts >= maxAttempts:
                    raise InfraExhaustedError(item, result)
                sleep(backoff(attempts))

            else:                              // all other non-zero codes — no retry
                raise PermanentSeedError(item, result)

    return manifest
```

The manifest is patched atomically (`.tmp` + rename) after **each** successful item
creation — not at the end of the batch. If the seeder is killed mid-batch, the next
restart picks up from the last successfully persisted state.

### Retry exit code mapping

The exit code catalogue is authoritative from `polyphony-verb-error-boundary.md`
(Mozart's corrected version — the squad brief had wrong codes):

| Exit code | Meaning | Retry posture |
|---|---|---|
| 0 | Domain outcome (success or named domain failure) | n/a |
| 1 | Unclassified crash | **No retry — permanent** |
| 2 | Config / manifest error | **No retry — permanent** |
| 3 | Twig CLI unavailable | **Retry up to 3 attempts** |
| 4 | Git operation failure | No retry for seeding context |
| 5 | ADO platform unreachable (includes 401/403 via twig) | **Retry up to 3 attempts** |

Auth failures (ADO 401/403) surface as code 5. Auth failure and network failure share
the same retry posture — both are transient infra states, not structural errors. There
is no separate auth code.

A validation failure from ADO (invalid type name, title over 255 chars, invalid area
path) surfaces as a domain outcome — exit 0, error in stdout JSON. The seeder does
NOT retry these: the item cannot be created until the plan is corrected. The seeder
surfaces the rejection clearly, and the workflow treats this as a planning error
requiring a renegotiation pass.

### On retry exhaustion

The seeder emits a typed error envelope to `CONDUCTOR_ERROR_OUT`:

```json
{
  "kind": "seeding.infra_exhausted",
  "message": "Failed to seed 2 of 5 planned children after 3 attempts: ADO platform unreachable",
  "details": {
    "root_id": "AB#1234",
    "plan_generation": "gen-1",
    "exhausted_items": ["task-4", "task-5"],
    "last_exit_code": 5,
    "attempts": 3
  }
}
```

The seeder then exits non-zero. The conductor workflow routes on
`seeding.infra_exhausted` to the `seeding_blocked` terminal.

---

## Renegotiation Linkage

### The generation chain

Each manifest carries a `plan_generation` object that forms a linked list of planning
decisions:

```
gen-1  { parent: null,  cause: "initial",          created_at: "..." }
  ↓
gen-2  { parent: "gen-1", cause: "architect_replan", created_at: "..." }
  ↓
gen-3  { parent: "gen-2", cause: "recovery",         created_at: "..." }
```

This chain answers "why did the plan change?" for any generation without inspecting
git history or workflow logs.

Each manifest item carries `introduced_in: "gen-N"`, recording which generation
declared it. When generations are monotonically numbered (`gen-1`, `gen-2`, ...),
orphan detection is a single set-membership test.

### Orphan detection

When seeding under the current manifest, any ADO child whose `polyphony:plan-child-id`
marker is NOT in the current manifest's `items` list is an orphan — created for a
prior generation that has since been superseded.

```
function detectOrphans(manifest, adoChildren):
    currentIds = Set(item.child_id for item in manifest.items)
    orphans = []
    for child in adoChildren:
        if child.marker is not null and child.marker not in currentIds:
            orphans.append({
                "ado_id": child.ado_id,
                "marker": child.marker,
                "title": child.title
            })
    return orphans
```

Orphans are surfaced in the seeder's stdout output. **The seeder does not delete
them.** Polyphony never deletes work items. Orphan disposition — close, relabel,
archive — is an operator or twig tooling concern.

### Generation ID assignment

The planner verb assigns generation IDs. Convention: monotonic `gen-N` within a root
item, starting at `gen-1`. The seeder reads generation IDs; it never assigns them.

If the manifest is being created for the first time (no prior manifest for this root),
the planner assigns `gen-1` with `parent: null, cause: "initial"`. If a prior manifest
exists, the planner reads the prior `plan_generation.id`, increments, and sets
`parent` accordingly.

---

## Verb Evolution

Two existing verbs evolve. No new verbs are introduced.

### `polyphony plan write-plan` (gains manifest write)

**Current behavior:** Writes `plans/plan-{id}.children.json` to the plans directory.  
**New behavior:** Additionally writes the seed manifest to
`.polyphony/state/{rootId}/seed-manifest.json` (atomic) in the same verb invocation,
immediately after the planner agent produces output. If the manifest write fails (disk
full, permissions), the verb exits non-zero and does NOT emit a successful result —
partial output is not acceptable here.

The verb's stdout JSON gains a field:

```json
{
  "status": "written",
  "plan_path": "plans/plan-AB#1234.children.json",
  "manifest_path": ".polyphony/state/AB#1234/seed-manifest.json",
  "plan_generation": "gen-1",
  "item_count": 5
}
```

### `polyphony plan seed-children` (gains manifest-aware reconciliation)

**Current behavior:** Calls `twig.CreateChildAsync` for all planned children; emits
`PlanSeedChildrenResult`.  
**New behavior:** Reads manifest → startup guard → reconcile against ADO by marker →
seed only gaps → patch `ado_id` atomically on each success → surface orphans → emit
structured result.

The verb's stdout JSON gains fields:

```json
{
  "status": "seeded",
  "plan_generation": "gen-1",
  "seeded_items": [5678, 5679, 5680],
  "reused_items": [5681],
  "orphan_count": 1,
  "orphans": [
    { "ado_id": 4200, "marker": "task-0", "title": "Prior gen task" }
  ],
  "error_count": 0
}
```

The existing invariant is unchanged: `polyphony:planned` is stamped on the parent only
when `error_count == 0`. A partial-error seed never advances the root.

---

## The `seeding_blocked` Terminal

### Why not `workflow_abandoned`

Beethoven's analysis is definitive: every route into `workflow_abandoned` is an
operator-volitional act requiring a human to click a button. It means "operator gave
up." Exhausted infrastructure retries are not volitional — the platform was
unreachable, the seeder hit its limit, and stopped. These are semantically distinct
and must not share a terminal:

| Terminal | Semantics | How it's reached |
|---|---|---|
| `workflow_abandoned` | Operator made a conscious decision to stop | Human gate: click "Abandon" |
| `seeding_blocked` | Infrastructure failed to converge within retry budget | Automatic: `seeding.infra_exhausted` error kind |

If infra exhaustion routes to `workflow_abandoned`, the batch aggregator cannot
distinguish "operator gave up intentionally" from "ADO was down when we tried to seed."
Re-trigger logic, SLO accounting, and incident response depend on this distinction.

### `seeding_blocked` semantics

`seeding_blocked` means: the seeder attempted to converge the ADO tree to the manifest
and could not complete due to an infrastructure failure that did not resolve within the
retry budget. The work item is **not abandoned** — it is **blocked**. The operator can
re-trigger when infrastructure recovers. The root item remains actionable without
requiring a judgment call.

### YAML wiring sketch

```yaml
- name: seed_children
  type: script
  script: |
    polyphony plan seed-children --root-id "{{ workflow.input.root_id }}"
  on_error:
    seeding.infra_exhausted: seeding_blocked
  routes:
    - condition: "{{ agent.output.status == 'seeded' }}"
      to: post_seeding_step

- name: seeding_blocked
  type: terminal
  output:
    status: seeding_blocked
    root_id: "{{ workflow.input.root_id }}"
    plan_generation: "{{ seed_children.output.plan_generation }}"
    reason: infra_exhausted
```

The `on_error:` handler matches on the `kind` field in `CONDUCTOR_ERROR_OUT`. No new
conductor primitives are needed.

---

## Relationship to Other ADRs

### `polyphony-verb-error-boundary.md`

The seeder uses the exit code taxonomy exactly as defined in that ADR. Mozart's
correction is adopted: code 5 is "ADO platform unreachable" (covers auth and network);
there is no separate auth code. Code 1 (unclassified crash) is permanent — no retry.
The invariant "infrastructure failures exit non-zero AND write `CONDUCTOR_ERROR_OUT`"
applies to the seeder; `seeding.infra_exhausted` is the error kind the seeder emits on
exhaustion.

### `gate-compression-pattern.md`

PR #535's gate removal is the direct precondition for this ADR. The prior design
placed a `human_gate` between `write-plan` and `seed-children` that acted as a
"did the seed succeed?" confirmation checkpoint. That gate is replaced by the
manifest + reconciliation + `seeding_blocked` pattern. The seeder becomes re-entrant
and self-verifying; the judgment gate is eliminated because correct seeding is now
machine-verifiable, not requiring human confirmation. The gate-compression ADR's
rule holds: a gate compresses when resolution is detectable by a poll/script. The seed
manifest makes that detection possible for the first time.

### `domain-signal-envelope.md`

When `polyphony plan seed-children` exits 0 (fully converged), the workflow emits a
seeding-complete domain signal. That signal's `details` field SHOULD carry the
reconciliation delta — how many items were newly seeded vs reused, the orphan count,
and the plan generation — so the batch aggregator and platespinner have visibility into
whether seeding was a clean first run or a partial-recovery continuation:

```json
{
  "kind": "seeding_complete",
  "severity": "info",
  "title": "Seeding complete — AB#1234",
  "message": "5 children seeded (3 new, 2 reused). Generation gen-2.",
  "details": {
    "root_id": "AB#1234",
    "plan_generation": "gen-2",
    "seeded_new": 3,
    "reused": 2,
    "orphan_count": 1
  }
}
```

---

## Alternatives Considered

**Type + parent + title-normalized hash as reconciliation primitive (Bach's prior
proposal):** Rejected in favor of the existing `polyphony:plan-child-id` marker.
Sibelius confirmed the marker is already embedded atomically, already parsed by
`BuildIndexes`, and already used for same-run reconciliation. Title normalization
introduces a new compatibility surface — subtle changes to the normalization function
would silently break cross-run equivalence. The marker is a stronger, tested primitive.
The hash approach is over-engineering a solved problem.

**Monotonic integer `plan_generation` counter:** Rejected. Daniel explicitly asked for
richer linkage: a chain that records parent generation and cause, not just an ordinal.
The integer loses the "why did this change?" context that is valuable for debugging
renegotiation loops and orphan accumulation.

**Conductor checkpoint as cross-run state:** Rejected. Mahler confirmed
`CheckpointManager` stores to `$TMPDIR` keyed to workflow name + timestamp. It is
gone on reboot; it is not keyed to root item ID; it is not designed for durable
cross-run state. The filesystem path in the repo worktree is the correct answer.

**Script-external retry via conductor `on_error: { max_attempts: 3 }`:** Rejected.
Mahler confirmed this syntax does not exist for `type: script` nodes. `RetryPolicy`
and `max_attempts` apply only to provider-backed agent nodes. Script-internal retry
loops are the idiomatic pattern for transient infra failures in seeder-style scripts.

**Route infra exhaustion to `workflow_abandoned`:** Rejected. Beethoven's analysis
establishes that `workflow_abandoned` is entirely operator-volitional — four routes,
all requiring a human gate click. Automatic infra exhaustion is semantically different.
Conflating them corrupts observability and breaks re-trigger logic.

**Add a `platform` field for GitHub/ADO branching:** Rejected. Seeding is
ADO-shaped (work item hierarchy, parent-child links, description markers). GitHub
Issues has no structural equivalent. A platform abstraction at this layer would produce
an abstraction that satisfies neither. GitHub seeding, if ever needed, is a separate
implementation — not an extension of this schema.

---

## Consequences

### Positive

- Restarts are safe: the seeder seeds only gaps, never re-creates items that already
  exist in ADO
- Corrupt partial seeds cannot advance to implementation — the startup guard ensures
  the manifest is valid before any ADO queries
- Renegotiation orphans are visible rather than silently accumulating
- The `seeding_blocked` terminal gives the batch aggregator a precise, observable
  signal that distinguishes infra failure from operator abandonment
- The plan generation chain provides a lightweight audit trail of replanning decisions
  without requiring git log access

### Negative

- `polyphony plan write-plan` gains a new output artifact; callers that rely only on
  `plans/*.children.json` are unaffected, but implementations must now maintain the
  manifest write as part of the verb contract
- Every seeder invocation now begins with a `twig show-tree` query even for items that
  have already been seeded — this is an additional ADO round-trip per restart. For
  roots with large child counts and frequent restarts, this may add latency
- Orphan detection produces output that must be triaged by an operator or downstream
  tooling; there is no automated disposition

### Neutral

- The `.polyphony/state/` directory is the per-root run state home. The manifest is
  the first resident. Other future per-root artifacts (e.g. seeding error logs) follow
  the same convention
- Roots seeded before this ADR shipped have no manifest. On first re-run they receive
  a fresh manifest from `write-plan`. There is no migration path from
  "no manifest" to a populated one — the planner always runs before the seeder

---

## Invariants

1. **No manifest, no seeding.** `polyphony plan seed-children` exits 2 if no valid
   manifest exists for the specified root. The workflow must call `write-plan` first.
2. **Atomic writes only.** All manifest writes — creation and per-item `ado_id`
   patches — use `.tmp` + rename. No partial file is ever the canonical manifest.
3. **Seeder never deletes.** Detection of orphaned ADO children is surfaced as output.
   The seeder takes no delete action.
4. **`polyphony:planned` only on full convergence.** The parent tag is stamped only
   when `error_count == 0` after all gaps are resolved. This is the hard guard against
   corrupt-state advancement.
5. **Generation IDs are assigned by the planner, read by the seeder.** The seeder
   patches `ado_id` fields only; it never assigns or modifies generation metadata.
6. **GitHub is out of scope.** No `platform` field, no GitHub execution path, no
   abstraction layer. ADO-only.

---

## Open Questions

These are genuine open questions, not blocking decisions.

**1. Orphan disposition workflow.** The seeder surfaces orphans in output but takes no
action. For roots with frequent renegotiation, orphan accumulation in ADO may become
operational noise. Should polyphony emit a domain signal for each orphan batch,
allowing an operator to triage? Or is this a twig CLI concern (`twig orphan-report`)?
Wagner and Sibelius to propose.

**2. `seeding_blocked` re-trigger path.** When a root is in `seeding_blocked`, the
re-trigger should skip `write-plan` (the manifest is valid) and go straight to
`seed-children`. This requires a re-entry discriminator in `root-item-dispatch.yaml`
— a check for manifest presence before routing. Wagner to design the YAML re-entry
branch.

**3. Manifest schema version field.** The current schema has no explicit `schema_version`
at the top level. If the schema gains new required fields, existing manifests will fail
the startup guard's parse check silently or with a confusing error. Consider adding
`schema_version: 1` to enable explicit forward-compatibility checks. Low-priority
until a first schema change is imminent.

**4. Seeder idempotency on `ado_id` patches across renegotiation.** When the new
generation carries forward items whose `child_id` values are unchanged (same work,
new plan), the new manifest starts with `ado_id: null` and the seeder's reconciliation
pass repopulates them from ADO. This is correct but means one `twig show-tree` query
per renegotiation regardless of what changed. Acceptable at current renegotiation
frequencies; revisit if architects replan frequently on large root items.

**5. Seeding-complete domain signal kind vocabulary.** The `seeding_complete` kind used
in the domain signal example above is not yet registered in the `domain-signal-envelope`
ADR's kind vocabulary. Add it on next revision of that ADR.
