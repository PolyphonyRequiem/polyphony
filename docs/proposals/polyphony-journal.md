# Polyphony Action Journal — Spec & Options

**Status:** Proposal / draft for discussion
**Owner:** polyphony-internal architecture
**Work item:** AB#3254 (parent epic: AB#3253)
**Companion:** none (does not depend on the conductor `on_error` brief; tracks
in parallel)

## TL;DR

Polyphony today is **purely observational** of state it does not own (git,
ADO, PRs, manifests). When something goes wrong, the only way to reconstruct
"what did polyphony do?" is to grep conductor's per-run event log and inspect
external systems by hand. This drives a cascade of downstream pain:

- **Reset is observation-based, not transactional.** `polyphony reset apex`
  walks external state hunting residue (AB#3245, AB#3246). It cannot tell
  "polyphony created this branch and forgot to delete it" from "this branch
  was here before polyphony ran."
- **Debugging is grep-time.** No per-work-item causal timeline across runs.
- **Drift is invisible.** When external state diverges from polyphony's
  expectation, we cannot tell whether a human mutated it, another tool did,
  or polyphony itself was wrong.
- **Idempotency is per-script.** Every state-mutating script reinvents
  "have I already done this?" using tempfiles or external lookups.

The proposal: a **per-worktree action journal** that records every
state-mutating polyphony action, queryable via new `polyphony journal`
verbs, consumed by reset / debugging / drift detection.

## Goal

Make polyphony's actions on external systems first-class, recorded,
queryable, and the basis for transactional reset and drift detection.

## Non-Goals

- **Not a replacement for conductor events.** Conductor still owns the
  workflow execution log; the journal records *polyphony actions*, which
  are a subset.
- **Not cross-machine / cross-repo.** The journal lives per-worktree and
  is bound to the run that produced it.
- **Not a billing / cost system.** Cost tracking *could* be built on top
  later; v1 does not solve that.
- **Not a substitute for ADO as work-tracker.** Work-item state continues
  to live in ADO; the journal records "polyphony transitioned WI N from S
  to T at time U," not the WI itself.

---

## Design Dimensions

For each dimension that has a real tradeoff, options are listed with a
recommendation. Obvious choices are stated without options analysis.

### D1 — Scope: what counts as a "journaled action"?

**The single most consequential design call.** Get this wrong and the
journal is either useless (too narrow) or unmaintainable (too broad).

**Options:**

- **(a) Every CLI verb invocation.** Read and write alike. *Pro:* exhaustive;
  every `polyphony …` call is recorded. *Con:* enormous volume, mostly
  uninteresting (`polyphony state next-ready` runs hundreds of times per
  apex); dilutes signal.

- **(b) Every state-mutating verb.** Only verbs that mutate external state
  (`branch ensure-evidence-branch`, `pr open-evidence-pr`, watermark stamps,
  manifest writes, ADO transitions). *Pro:* signal-rich; aligns with what
  reset cares about. *Con:* requires classifying every verb up-front and
  enforcing the classification.

- **(c) Every external side-effect, regardless of verb.** Define "action"
  at the wire boundary (git mutation, gh/az/twig invocation, ADO REST
  write). *Pro:* most precise; would even catch side-effects from inside
  scripts. *Con:* requires intercepting every shell-out; impractical
  without a centralized side-effect layer that doesn't exist today.

**Recommendation: (b)** with a marker interface or attribute
(`[JournaledAction]`) on every CLI verb that mutates external state. The
classification is reviewable, finite (~15-20 verbs today), and grows
linearly with the CLI surface.

**Carve-outs in (b):**
- Read-only verbs (`state next-ready`, `edges check`, `policy load`,
  `guidance extract`) do NOT journal.
- Verbs that mutate **polyphony-internal** state only (e.g., a future
  in-memory cache) do NOT journal.
- Verbs that mutate **observable external state** DO journal: git
  branches/worktrees, PRs, ADO transitions/tags/links, the manifest,
  watermark stamps.

### D2 — Storage location

**Options:**

- **(a) Per-worktree** (`<worktree>/.polyphony-state/journal.db`,
  gitignored). *Pro:* aligns with apex-bound run lifecycle; natural single-
  writer (the run lock); teardown with the worktree. *Con:* destroyed
  with the worktree, so debugging post-cleanup requires copying it out.

- **(b) Per-repo** (`<repo>/.polyphony-state/journal.db`, outside
  worktrees, gitignored). *Pro:* survives worktree cleanup; cross-run
  queries trivial. *Con:* multiple concurrent apex runs (different
  worktrees, same repo) contend for the same DB; the same-root run lock
  helps but doesn't generalize to cross-apex concurrency.

- **(c) Hybrid.** Per-worktree as the *write* store; copy / roll-up to
  per-repo on apex completion or reset for cross-run debugging. *Pro:*
  best of both. *Con:* one more thing that can drift.

**Recommendation: (a) per-worktree** for v1. The teardown loss is real but
mitigable by `polyphony journal export <path>` before cleanup. Per-repo
roll-up can be added later if cross-run queries become a real ask. We
should resist over-engineering this on day one.

### D3 — Storage technology

**Stated without options analysis.** SQLite is the answer:

- AOT-compatible (Microsoft.Data.Sqlite works under NativeAOT).
- Single file, zero external service, zero deployment story.
- Transactional, queryable, indexable.
- Well-understood concurrency story (WAL mode).
- We already have one dependency on it elsewhere in the .NET ecosystem;
  this is not novel ground.

JSON-Lines was considered as an append-only canonical log but rejected:
the query patterns we need (per-item timeline, per-action filter, drift
diff) want indexes, and "JSONL + SQLite index" is strictly worse than
"SQLite + optional JSONL export."

### D4 — Record schema

**The minimum viable shape:**

```sql
CREATE TABLE actions (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id          TEXT    NOT NULL,    -- correlates to conductor run id
    apex_id         INTEGER,             -- nullable; not all actions are apex-scoped
    work_item_id    INTEGER,             -- nullable; not all actions target a WI
    action          TEXT    NOT NULL,    -- discriminator: 'branch_create', 'pr_open', ...
    target          TEXT    NOT NULL,    -- the affected resource id (branch name, PR url, WI id)
    started_at      INTEGER NOT NULL,    -- unix ms
    finished_at     INTEGER,             -- unix ms; null if mid-flight or crashed
    outcome         TEXT,                -- 'success' | 'failure' | 'no_op' | null (mid-flight)
    error_code      TEXT,                -- nullable
    error_message   TEXT,                -- nullable
    payload_json    TEXT                 -- action-specific extra context
);

CREATE INDEX idx_actions_work_item ON actions(work_item_id);
CREATE INDEX idx_actions_apex      ON actions(apex_id);
CREATE INDEX idx_actions_run       ON actions(run_id);
CREATE INDEX idx_actions_action    ON actions(action);
CREATE INDEX idx_actions_started   ON actions(started_at);
```

**Tradeoff on `payload_json`:**

- **(a) Narrow column set + opaque payload blob.** What's here today.
  *Pro:* schema-stable; new action types add freely. *Con:* payload
  shape is per-action and effectively another stringly-typed surface.
  We have to discipline ourselves to publish the per-action payload
  schema separately.

- **(b) Wide normalized schema.** A column per attribute, hundreds of
  nullable columns. *Pro:* fully typed end-to-end. *Con:* unmaintainable
  as action types proliferate.

- **(c) One table per action type.** *Pro:* fully typed per action.
  *Con:* migrations on every new action; cross-action queries become
  unions.

**Recommendation: (a)** with a discipline: each action type publishes
its payload C# record in `Polyphony.Journal.Payloads.*` and serializes
through `PolyphonyJsonContext` (same AOT JSON pipeline as everything
else). The discipline lands the payload shape in code review where it
belongs.

**Outcome enum semantics:**
- `success` — action completed and the intended mutation occurred.
- `failure` — action completed but failed; mutation did NOT occur (or
  rolled back).
- `no_op` — action completed; mutation was already in the desired state
  (idempotent skip). **This is important** — distinguishes "I already
  did this" from "I just did this," which reset and drift both care
  about.

### D5 — Integration pattern: how do verbs get journaled?

**Options:**

- **(a) Explicit library calls.** Every state-mutating verb explicitly
  calls `journal.RecordStart(...)` and `journal.RecordEnd(...)`. *Pro:*
  no magic, no DI surprises. *Con:* trivial to forget; every new verb
  needs reviewer vigilance; tests can pass without journaling.

- **(b) Marker interface / attribute + DI decorator.** Verbs implement
  `IJournaledAction` (or carry a `[JournaledAction]` attribute); a DI
  decorator wraps them at registration time and writes the journal
  entries around invocation. *Pro:* impossible to forget; uniform.
  *Con:* one layer of indirection; AOT-DI has to be set up to handle
  it.

- **(c) Source generator.** Generate journaling code at compile time
  from the marker. *Pro:* zero runtime overhead. *Con:* yet another
  source generator; harder to debug; AOT-compatible but more machinery
  than we need.

**Recommendation: (b) decorator on a marker interface.** ConsoleAppFramework
+ primary-constructor DI already supports this pattern. The decorator
captures `started_at`, invokes the wrapped verb, captures
`finished_at` + outcome + any exception, writes the row.

The catch with (b): some verbs are *partially* journaled — they do
several things, some of which mutate. For those we fall back to (a)
inside the verb implementation, calling the journal store directly. The
two patterns coexist; (b) handles the bulk.

### D6 — Backfill / migration

When the journal lands, in-flight runs have no history. What happens?

**Options:**

- **(a) Greenfield.** Journal starts now. Runs older than the journal-
  enablement commit stay observation-only forever. *Pro:* simple, no
  surprises. *Con:* in-flight apex runs at the cut-over moment have
  partial journals — first half observation-only, second half journaled.

- **(b) Best-effort backfill on first journaled run.** Walk the worktree
  and reconstruct journal entries by inspection (branches present,
  worktrees present, manifest entries) with `outcome='inferred'`.
  *Pro:* even old runs get a workable journal. *Con:* inferred entries
  are lower-fidelity; reset can't trust them as strongly.

- **(c) Forbid in-flight cutover.** Don't enable the journal until all
  apex runs are quiescent. *Pro:* clean. *Con:* operationally annoying
  (we always have runs in flight) and we'd never ship.

**Recommendation: (a) greenfield**, plus a separate `polyphony journal
backfill <apex>` verb that does (b) on demand for the rare case someone
explicitly wants it. Operationally: enable the journal in a release;
any in-flight runs get partial history; everyone moves on.

### D7 — Retention

**Stated without options.** Keep forever locally; ship a manual
`polyphony journal vacuum --before <date>` for cleanup. SQLite is cheap;
debugging value is high; auto-deletion is a hostile default.

### D8 — Concurrency

**Stated without options.** WAL mode SQLite. Multiple writers within a
single worktree are fine (a single apex run can have parallel verb
invocations under the same-root run lock). Cross-worktree concurrency is
not possible by D2 (per-worktree storage).

### D9 — Visibility / query surface

**Verbs to add:**

| Verb | Purpose |
|---|---|
| `polyphony journal show --work-item N` | per-WI timeline |
| `polyphony journal show --apex N` | apex-wide timeline |
| `polyphony journal show --run R` | per-run actions |
| `polyphony journal show --action <name>` | per-action-type filter |
| `polyphony journal drift --apex N` | journal vs. observation diff (D10) |
| `polyphony journal export <path>` | dump for off-worktree debugging |
| `polyphony journal vacuum --before <date>` | retention |

All emit the standard routing-style envelope; query verbs also support
`--render text` for human reading and `--render json` (default) for
tooling.

### D10 — Drift detection: the killer feature

The journal lets us answer **"is the world consistent with what polyphony
expects?"**

A drift check walks the journal for an apex, projects expected state
(branches that should exist, PRs that should be open, ADO transitions
that should have stuck), and diffs against observation:

```
Journal: branch evidence/3165-3179 created at T1, not deleted
Observation: branch does not exist
→ External delete (probably human or other tool). Informational.

Journal: branch plan/62365430-62365456 not present
Observation: branch exists locally
→ Polyphony did not create it. Either pre-existing or someone else
  made it. Informational.

Journal: PR #4587 opened at T1, not closed
Observation: PR #4587 closed at T3, no corresponding journal entry
→ External close. Informational.

Journal: ADO WI N transitioned to Active at T1
Observation: WI N is in New
→ External revert. Action needed; surfaces in drift report.
```

The drift report is the same data structure reset consumes:
"things polyphony believes it created that are still there" become reset
targets; "things polyphony believes it created that are already gone"
become journal cleanup (mark as externally deleted).

**This is what makes reset transactional.** AB#3245 and AB#3246 become
trivial: enumerate journal entries for the apex, delete the resources
they refer to, mark journal entries as cleaned up. No more "enumerate
branches whose names match a pattern."

### D11 — Routing-layer consumption (deferred)

Should workflow YAML query the journal directly?

- `polyphony journal has --action branch_create --target X --run R` → boolean
- Replaces ad-hoc tempfile-based idempotency in scripts

**Defer.** Tempting but expands scope. V1 ships the journal as a
polyphony-internal substrate; workflow integration comes later when we
know what queries actually matter.

---

## Phasing

| Phase | Scope | AC | Risk |
|---|---|---|---|
| **0** | ADR (this doc, post-discussion) | Approved by you | None |
| **1** | Schema + `JournalStore` + `[JournaledAction]` decorator + `polyphony journal show` (text + json render) + `polyphony journal export` | New verb suite green; existing verbs unaffected | Low — additive |
| **2** | Branch ops journal (`branch ensure-evidence-branch`, `branch ensure-feature-branch`, any other branch verb) | Per-apex `journal show` shows real branch lifecycle | Low |
| **3** | PR ops journal (open, comment, merge, close — both GitHub and ADO legs) | Per-apex `journal show` covers PR lifecycle | Medium |
| **4** | ADO transitions + tag mutations + watermark stamps + manifest writes journal | Drift check sees a full picture | Medium |
| **5** | `polyphony journal drift` | Drift verb returns sane diffs on a real apex | Medium |
| **6** | `polyphony reset apex` rewritten against journal; AB#3245 / AB#3246 close as side-effects | Reset reset-tests pass; residue empirically gone on a clean run | Higher — operational |
| **7** | (optional) workflow-layer query verbs (D11) | Counter-like scripts begin retiring | Higher — workflow churn |

Phases 1-5 are non-disruptive: nothing changes for existing workflows.
Phase 6 is the payoff phase; phase 7 is bonus.

---

## What we are NOT deciding here

- **Inversion (polyphony-as-orchestrator).** The journal is forward-
  compatible with the inversion sketch but does not commit us to it.
  If we invert, the journal is even more valuable (it becomes the
  primary source of truth, not a side-log).
- **`on_error` / conductor failure model.** Independent track. Journal
  records errors regardless of how the workflow handles them.
- **Counter verb.** Withdrawn (per discussion). Some "counters" become
  journal projections (phase 5+); some belong as conductor primitives;
  some get absorbed into the verb that does the work.

---

## Open questions for you

1. **D1 carve-out edge cases.** Are there verbs that mutate
   *polyphony-internal* state but whose history we still want? (Examples:
   `polyphony policy resolve` writes nothing today, but if it caches,
   does cache invalidation belong in the journal?) My instinct: no.
2. **D2 storage location.** Per-worktree feels right; do you want the
   per-repo roll-up option committed to the spec as a future, or
   genuinely punted until someone asks?
3. **D6 backfill.** Are you OK with greenfield + on-demand backfill verb?
   Or do you want auto-backfill on first journaled run for in-flight
   apexes (operationally simpler but lower-fidelity history)?
4. **Scope of phase 1.** Is `journal show` + `journal export` enough to
   ship phase 1 standalone, or do you want phase 1 to also include the
   first action class (branch ops) so we have something to actually
   look at?
5. **Naming.** "Journal" reads fine to me. Alternatives: "ledger" (more
   accounting-feeling), "audit" (overloaded with security connotation),
   "actions log" (verbose). Open.

---

## Appendix — relationship to the rest of the audit

| Audit finding | Journal addresses? |
|---|---|
| #4 Workspace state ownership / trust boundary | **Yes — primary fix** |
| #6 Observability is real-time-only | **Yes — primary fix** (per-item timeline across runs) |
| #7 Reset bolted-on, AB#3245/3246 | **Yes — primary fix** (phase 6) |
| #1 Workflow complexity | Partly (phase 7 collapses some scripts) |
| #2 Scripts that should be verbs | Partly (the verbs that journal are exactly the ones that should be typed) |
| #5 Schema sprawl | Partly (action payload schemas are typed end-to-end) |
| #3 Failure-mode gates | No — that's the `on_error` brief's job |
| #8 Three-vocabulary problem | No |
| #9 Testing pyramid inversion | No |
| #10 Cost / model drift | No (cost tracking is a future on top of the journal) |
