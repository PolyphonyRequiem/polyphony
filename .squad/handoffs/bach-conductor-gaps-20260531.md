# Bach — Conductor Gaps: Seam-Level Architectural Analysis

**Author:** Bach (Architect)  
**Date:** 2026-05-31  
**Requested by:** Daniel Green  
**Status:** Complete — written offline. No follow-up needed before Daniel reads.

---

## Framing

My angle is the polyphony↔conductor seam (S1 in the north star). Every gap below is
evaluated through one question: *does this force a concern to live on the wrong side of
the line?* I am not cataloguing conductor bugs or feature gaps in the abstract — I am
cataloguing the places where a conductor absence causes polyphony to absorb engine work,
or where conductor is forced to know domain things it should not.

Source artifacts used: `gate-compression-pattern.md`, `domain-signal-envelope.md`,
`polyphony-verb-error-boundary.md`, `seed-manifest-as-durable-state.md`, north-star §4.1,
`.squad/decisions.md` (Bach + Beethoven initial concerns), `.conductor/registry/workflows/`
(root-item-dispatch, plan-level, root-fallback-gate, root-batch-dispatch), `bach/history.md`.

---

## Top 5 Seam-Level Conductor Gaps

---

### Gap 1 — No `on_error:` for `type: script` nodes

**Seam violation:**
Polyphony is absorbing infrastructure-failure routing that belongs in the engine.
When a verb fails (twig unreachable, ADO unreachable, git push rejected), the
current pattern is to handle the failure inside the script, emit a domain-shaped
error token on stdout, and route via normal `condition:` checks — or to surface a
`human_gate` so the operator clicks through a retry/abort decision. Both are
polyphony doing what conductor's error circuit should do declaratively.

This is the engine concern leaking up: retry-then-escalate is not a polyphony policy
question. It is the same control flow regardless of which verb failed or which workflow
called it. Polyphony should declare `on_error: retry: 3` and `on_error: route: infra_error_gate`
and be done.

**Evidence in the codebase today:**

- `polyphony-verb-error-boundary.md` §Context: "Without this change, every polyphony verb
  failure passes silently through `on_error:` as if it were a success." The entire ADR
  exists because `on_error:` is not yet usable for script nodes.
- `gate-compression-pattern.md` §Notes: "If the poll script fails with a non-zero exit
  (infrastructure failure, not a 'condition not met' result), the `on_error:` handler on
  `poll_pr_approved` **should** route to the appropriate retry or escalation path."
  The word "should" is the tell — the pattern is aspirational because `on_error:` on script
  nodes is not confirmed to work for this use case.
- `.squad/decisions.md` (Beethoven initial concerns): "The `on-error-migration-inventory.md`
  documents 19 `*_error_gate` nodes across 6 workflows, describes all 19 as 'trivial'
  (retry/abort logic), yet they're surfaced as human gates where operators must choose."
  Those 19 gates are polyphony doing engine routing.
- `seed-manifest-as-durable-state.md` §Context: "Conductor's `max_attempts` / `RetryPolicy`
  apply only to provider-backed agent nodes, not `type: script` nodes (confirmed against
  `schema.py` line 449, 843). There is no `on_exhaust` field in the schema.
  Script-internal retry loops are the idiomatic and fully supported pattern." This means the
  seeder carries its own retry circuit (3 attempts, codes 3 and 5) entirely in script logic.

**What "fixed" looks like:**
`on_error:` lands for `type: script` nodes with at minimum: `retry:`, `route:`, and a typed
error envelope on `CONDUCTOR_ERROR_OUT` (which is already the polyphony-verb-error-boundary
contract). Polyphony scripts declare retry policy in YAML; seeder no longer carries a
script-internal retry loop; the 19 error gates collapse to `on_error:` declarations.

**Impact rank:** 5/5. This is the single biggest seam violation. Infrastructure retry
logic should never live inside polyphony domain code.

**Cost:** Medium (conductor PR #229 reportedly exists — delivery timeline is the blocker,
not design ambiguity).

---

### Gap 2 — No Dynamic Sub-workflow Dispatch

**Seam violation:**
Conductor forces polyphony to encode topology routing — "which lifecycle workflow to run
for this item" — as explicit branching YAML rather than as a data-driven dispatch. This
is a conductor engine concern (sub-workflow selection) that leaks into polyphony's workflow
authoring as sprawling branch-on-router structures.

**Evidence in the codebase today:**

- `root-item-dispatch.yaml` comment (lines 30–35):
  > "Conductor does NOT support dynamic templated `workflow:` paths
  > (`workflow: "./{{ classify.output.workflow }}.yaml"`), so the
  > branch-on-router pattern is the canonical mechanism for 'invoke
  > sub-workflow X or Y based on a router output'."
  
  The comment explicitly names the gap. The branch-on-router pattern produces four
  explicit `type: workflow` nodes (`plan_level`, `actionable`, `implement_merge_group`,
  `feature_pr`) with four matching route conditions that fan from `spawn_worktree`.
  This is purely mechanical — it encodes zero polyphony domain logic. It exists only
  because conductor cannot dispatch a sub-workflow by computed name.

- `feature-pr.yaml` uses the same pattern for `pr_platform_router` → `pr_lifecycle_github` /
  `pr_lifecycle_ado`. Again, zero domain logic — pure dispatch mechanical overhead.

- Every time a new lifecycle workflow is added (e.g., a `research` lifecycle, or a
  decomposable-only fast path), `root-item-dispatch.yaml` must be surgically updated
  to add another branch. The dispatch table is maintained by hand across two files.

**What "fixed" looks like:**
Conductor supports `workflow: "{{ classify.output.workflow_path }}"` (templated
sub-workflow path). Polyphony's lifecycle classifier emits `lifecycle_workflow: plan-level`
and the dispatch becomes a single node. The branch-on-router pattern survives as a
fallback for complex conditional routing, but the simple "N:1 dispatch table" case
collapses to one line.

**Impact rank:** 4/5. Medium-high. The current workaround is clean and documented, but
it is load-bearing dead weight — every new lifecycle adds a mandatory touch to the dispatch
workflow, which is a maintenance tax with no architectural benefit. The north star invariant
"No orchestration runtime inside polyphony" is technically met, but the intent is violated:
polyphony is doing engine dispatch bookkeeping.

**Cost:** Medium (conductor schema + engine change; does not touch polyphony C# at all).

---

### Gap 3 — No Cross-Run Artifact Persistence

**Seam violation:**
Conductor provides crash-recovery state (`CheckpointManager`) scoped to a single run.
It provides no mechanism for state that must survive across *separate* runs of the same
logical root. This forces polyphony to own a filesystem persistence contract
(`.polyphony/state/{rootId}/seed-manifest.json`) that is architecturally an engine concern:
"given a logical workflow root, what durable facts does the system know?"

This is the state-persistence concern leaking up into polyphony's domain layer.

**Evidence in the codebase today:**

- `seed-manifest-as-durable-state.md` §Context (Mahler finding): "Conductor's
  `CheckpointManager` stores to `$TMPDIR` keyed to workflow name + timestamp. It is
  crash recovery for a single run, not durable cross-run state. It is gone on reboot.
  There is no built-in mechanism for 'this artifact survives across separate runs of
  the same root.'" This is the direct admission of the gap.
- The seed manifest ADR codifies a polyphony-owned filesystem contract with atomic write
  discipline (`.tmp` + rename), generation tracking, and orphan detection. These are
  persistence primitives that polyphony had to design from scratch because conductor
  provides nothing here.
- The write path lives in `polyphony plan write-plan` (a CLI verb) and the read/reconcile
  path in `polyphony plan seed-children`. The filesystem at `.polyphony/state/` is now
  a polyphony-maintained artifact store that conductor knows nothing about.
- `seed-manifest-as-durable-state.md` founding principle 3: "Filesystem is the entire
  persistence layer." The ADR is explicit that this is intentionally lean — but that
  leanness is because conductor provides no better option, not because filesystem-first
  is the right model at scale.

**What "fixed" looks like:**
Conductor offers a named-artifact store keyed by `(workflow_name, root_identifier)`,
with cross-run read/write access. Polyphony declares the manifest as a conductor artifact;
conductor guarantees persistence across restarts and reboots. Polyphony's seeder calls
`conductor artifact read/write` instead of managing filesystem paths, atomic renames,
and `.tmp` sentinel detection itself.

This is not urgent (the filesystem approach works and is correct) but it is a category
error: polyphony is designing a database-lite layer for concerns that belong in the engine.

**Impact rank:** 3/5. The current implementation is stable (ADR merged at 619ce7c). The
seam violation is architectural rather than operational. Risk accumulates if the manifest
schema grows (queryability, versioning, migration) — that growth pressure will hit
polyphony directly rather than the engine.

**Cost:** Large (conductor would need to design and ship a named artifact store; polyphony
migration is straightforward but follows conductor's design).

---

### Gap 4 — `run_id` / Correlation Context Not Surfaced to Workflows

**Seam violation:**
Conductor knows its own run identifier. Polyphony workflows need that identifier to
construct stable `correlation_id` values for domain signals. Because conductor does not
expose `{{ conductor.run_id }}` as a workflow variable, polyphony is forced to thread
`run_id` through as a workflow input — a domain concern (correlation tracking) is being
implemented by duplicating engine-internal state.

**Evidence in the codebase today:**

- `domain-signal-envelope.md` §Vocabulary (Bach notes from history.md):
  "`{{ conductor.run_id }}` unknown — workaround via workflow input, Mahler to verify."
- `gate-compression-pattern.md` canonical YAML (line 98):
  `correlation_id: "{{ workflow.input.run_id }}:pr-review:{{ workflow.input.root_id }}"`
  The `workflow.input.run_id` is being threaded through explicitly because conductor does
  not expose a built-in run identifier in the template namespace.
- `domain-signal-envelope.md` §Conductor Envelope: `run_id` appears as a conductor-owned
  field (it is in `data.run_id` of the `.notifications.jsonl` envelope). So conductor
  *has* a run_id — it just doesn't expose it in the Jinja template context for workflow
  authors to reference.

**What "fixed" looks like:**
Conductor exposes `{{ conductor.run_id }}` (or equivalent: `{{ workflow.run_id }}`,
`{{ env.CONDUCTOR_RUN_ID }}`) as a first-class template variable in all step contexts.
Polyphony's `correlation_id` becomes:
`"{{ conductor.run_id }}:pr-review:{{ workflow.input.root_id }}"` — no input threading
required. The `run_id` input can be dropped from every workflow that only needed it for
correlation. This also makes the domain signal envelope's `correlation_id` more reliable:
the engine's own run_id is stable and unique; a workflow input can be accidentally omitted
or mistyped.

**Impact rank:** 3/5. Mechanical to fix on the conductor side. Real cleanliness benefit for
the domain-signal contract (removes a class of "correlation_id didn't match because run_id
wasn't passed correctly" bugs) and removes an unnecessary input threading requirement from
all gate-compression patterns.

**Cost:** Small (conductor template context expansion; one input dropped from all affected
workflows).

---

### Gap 5 — `CONDUCTOR_OUTPUT` Schema Contract is Implicit (No Type-Level Enforcement)

**Seam violation:**
The seam between polyphony verb outputs and conductor's Jinja template consumption is
uncontracted. Polyphony emits JSON to `CONDUCTOR_OUTPUT`; conductor reads it; workflow
YAML references fields by path (`{{ agent.output.foo.bar }}`). No shared schema exists,
no compile-time check exists, and a type rename in C# produces a silent runtime regression
only detectable after a 30-minute dogfood cycle.

This is the verb-shape / output-schema concern living in two disconnected places
simultaneously — C# JSON serialization (`PolyphonyJsonContext.cs`) and YAML Jinja paths
(`.conductor/registry/workflows/`) — with nothing binding them.

**Evidence in the codebase today:**

- `.squad/decisions.md` (Bach initial concern #1): "Workflow YAML references verb outputs
  by Jinja path (`{{ agent.output.foo.bar }}`), but no compile-time check ensures the path
  exists, is nullable-safe, or matches what the CLI actually emits. Bugs #6 (field-name
  drift) and #8 (silent null-elision via `DefaultIgnoreCondition = WhenWritingNull`) surface
  only at runtime, typically after 30+ minute dogfood cycles."
- The proposed fix (`[VerbResult(typeof(X))]` attribute backfill + Jinja path lint) is
  described in the verb-output-schema-registry ADR as "Proposed" — not yet accepted or
  implemented. The lint (ADR #175 companion) is blocked on the attribute backfill.
- `polyphony-verb-error-boundary.md` adds `CONDUCTOR_ERROR_OUT` as a second output channel
  for infrastructure failures. This channel also has no schema-level contract with
  conductor's `on_error:` routing — the `kind` field vocabulary is polyphony-defined but
  nothing in conductor validates it.
- `gate-compression-pattern.md` §Notes: "The poll script should write a deterministic JSON
  output: `{'approved': true}` or `{'approved': false}`." This is a hand-maintained contract
  between the script and the route condition `{{ agent.output.approved == true }}`.

**What "fixed" looks like:**
Two complementary changes:

1. **Polyphony side:** `[VerbResult(typeof(X))]` attribute on all ~50 Command methods,
   generating a verb-output schema registry (ADR #173). The registry is a JSON artifact
   shipped with the polyphony binary.
2. **Conductor side:** Conductor optionally accepts a `$schema:` reference on a
   `type: script` agent block. When provided, conductor validates the output against the
   schema at runtime before making it available to Jinja routing. This gives polyphony a
   second-line defense: even if the lint is skipped, conductor will reject mismatched
   outputs at the boundary rather than silently propagating null/undefined into routing.

The seam violation is most visible in the gap between the C# side (which has types) and the
YAML side (which only has strings). The lint is a polyphony-only mitigation. A conductor-side
schema validation hook would close the contract at the engine level.

**Impact rank:** 4/5. High. This is the highest-velocity defect class today. Field-name drift
has already caused dogfood regressions. Every new verb, every JSON rename, and every nullable
field is a hidden landmine until the lint ships.

**Cost:** Medium (polyphony attribute backfill: ~4h mechanical work; Jinja lint: ~1 week;
conductor schema hook: needs-research — may require conductor schema.py changes).

---

## Summary Table

| Gap | Seam Direction | Impact | Cost |
|---|---|---|---|
| 1 — No `on_error:` for script nodes | Engine concern leaks up (polyphony absorbs retry routing) | 5/5 | Medium |
| 2 — No dynamic sub-workflow dispatch | Engine concern leaks up (polyphony maintains dispatch table) | 4/5 | Medium |
| 3 — No cross-run artifact persistence | Engine concern leaks up (polyphony owns filesystem store) | 3/5 | Large |
| 4 — `run_id` not exposed to templates | Domain concern absorbs engine-internal state | 3/5 | Small |
| 5 — `CONDUCTOR_OUTPUT` schema implicit | Contract gap between C# types and YAML paths | 4/5 | Medium |

---

## Bonus: Would `on_error:` Change the Gate-Compression ADR?

**Short answer: Yes — it would simplify and complete the pattern, not replace it.**

The `gate-compression-pattern.md` currently describes a three-step pattern (emit domain
signal → poll → wait-on-miss → loop) and notes in two places that `on_error:` handling is
needed but not yet confirmed: "the `on_error:` handler on `poll_pr_approved` **should**
route to the appropriate retry or escalation path" and Ask 4 explicitly defers to
"Mahler to confirm that conductor's `on_error: timeout` works correctly for long-running
poll loops." The pattern today is incomplete at the failure boundary — it covers the
happy path (condition detected) and the miss path (condition not yet met) but does not
resolve what happens when the poll script itself fails (non-zero exit: twig unavailable,
ADO unreachable). That third path is handled today by script-internal try/catch or by
silently propagating as a domain miss. Both are wrong.

When `on_error:` lands for `type: script` nodes, the gate-compression pattern gains a
clean fourth step: an `on_error:` declaration on the poll step that routes infrastructure
failures to an escalation path without the script needing to catch them. This means the
canonical YAML pattern grows slightly (a fourth `on_error:` block on `poll_pr_approved`)
but the script itself shrinks (no internal try/catch needed). More importantly, Ask 4 in
the ADR resolves cleanly: conductor-level timeout via `on_error: timeout` is confirmed
available, the poll counter approach in Option A is unnecessary, and the ADR's
"prefer C if Mahler confirms" clause can be closed in favor of C. The ADR would be
amended to add the `on_error:` block to the canonical YAML and mark Ask 4 as resolved.
The compression rule and gate-shape catalogue remain unchanged — `on_error:` is plumbing,
not policy.

---

## Bonus 2: State of the Polyphony↔Conductor 2-System Architecture

**Honest verdict: Holding up, but showing four stress fractures that all point the same way.**

The fundamental split — polyphony as type-agnostic SDLC routing domain, conductor as
execution engine — is architecturally sound and the invariants from north-star §4.1 are
being observed (no conductor types leak into `src/Polyphony/{Routing,Configuration,Policy}/`;
polyphony does not absorb orchestration runtime). What the audit above reveals is that
conductor is missing exactly the features that would allow the seam to stay clean under
realistic domain pressure: durable artifact state, cross-run correlation, dynamic dispatch,
and `on_error:` for non-provider nodes. Each absence forces polyphony to paper over the
gap — the 19 error gates, the seed manifest filesystem store, the run_id threading, the
dispatch table sprawl — and that papering is well-designed (the ADRs are evidence of
careful thought) but it is still polyphony absorbing engine concerns. The system will
hold for the near term. The risk accumulates if: (a) the seed manifest grows queryable
or versioned, (b) new lifecycle types keep inflating the dispatch table, or (c) the
verb-output schema gap produces a high-visibility production regression before the lint
ships. None of these is a crisis today; they are directional debt that conductor feature
delivery can eliminate.

---

## Directional Call for Daniel (see also inbox file)

One call needs a yes/no: Should polyphony formally track "conductor feature blockers"
as first-class items in its own backlog (i.e., ADRs or tracked issues that say
"this polyphony gap resolves when conductor ships X"), and should the polyphony
workflow suite version lock against a minimum conductor version that guarantees
`on_error:` for script nodes? Currently `metadata.min_polyphony_version` is tracked
in workflow YAMLs (e.g., `version: "2.4.8"` in root-item-dispatch) but there is no
corresponding `min_conductor_version` guard. A `meta.min_conductor_version` field in
workflow YAML would make the conductor dependencies explicit and give operators an
upgrade-path signal.

---

*Bach — Architect*  
*2026-05-31*
