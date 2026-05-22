# Polyphony Action Journal — Spec & Options

**Status:** Proposal — D1, D2, D6, D7, D12, naming, Phase 1A/1B order decided. Ready to implement.
**Owner:** polyphony-internal architecture
**Work item:** AB#3254 (parent epic: AB#3253)
**Companion:** none (does not depend on the conductor `on_error` brief; tracks
in parallel)

## Decision log

| Dimension | Decision | Date | Notes |
|---|---|---|---|
| D1 — Scope | Every state-mutating verb, **including polyphony-internal**, journals | 2026-05-21 | See revised D1 below |
| D2 — Storage | **Per-root** (lives in the root worktree; sub-worktrees write through to it) | 2026-05-21 | See revised D2 below |
| D6 — Backfill | Greenfield only. No backfill verb. In-flight runs at cutover are breakable. | 2026-05-21 | See revised D6 below |
| D7 — Retention | No retention management. Journal lives + dies with the root worktree. | 2026-05-21 | No `journal vacuum` verb |
| D12 — Artifacts / trust boundary | Journal is a pointer log (git/platform are content stores). Agent-direct mutations are NOT journaled; drift detector surfaces them honestly. | 2026-05-21 | See D12 below |
| Phase 1A / 1B order | **Phase 1A first** (infra-only slice: schema + store + decorator + show/export); 1B (branch-ops journaled) follows in a separate landing | 2026-05-21 | Smaller blast radius; foundation lands clean before any verb gains the attribute |
| Naming | `journal` | 2026-05-21 | "ledger", "actions log", "audit" all rejected |

## TL;DR

Polyphony today is **purely observational** of state it does not own (git,
ADO, PRs, manifests). When something goes wrong, the only way to reconstruct
"what did polyphony do?" is to grep conductor's per-run event log and inspect
external systems by hand. This drives a restack of downstream pain:

- **Reset is observation-based, not transactional.** `polyphony reset root`
  walks external state hunting residue (AB#3245, AB#3246). It cannot tell
  "polyphony created this branch and forgot to delete it" from "this branch
  was here before polyphony ran."
- **Debugging is grep-time.** No per-work-item causal timeline across runs.
- **Drift is invisible.** When external state diverges from polyphony's
  expectation, we cannot tell whether a human mutated it, another tool did,
  or polyphony itself was wrong.
- **Idempotency is per-script.** Every state-mutating script reinvents
  "have I already done this?" using tempfiles or external lookups.

The proposal: a **per-root action journal** that records every
state-mutating polyphony action, queryable via new `polyphony journal`
verbs, consumed by reset / debugging / drift detection.

## Goal

Make polyphony's actions on external systems first-class, recorded,
queryable, and the basis for transactional reset and drift detection.

## Non-Goals

- **Not a replacement for conductor events.** Conductor still owns the
  workflow execution log; the journal records *polyphony actions*, which
  are a subset.
- **Not cross-machine / cross-repo.** The journal lives per-root and
  is bound to the root run that produced it.
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
  root); dilutes signal.

- **(b) Every external-state-mutating verb.** Only verbs that mutate
  external state (`branch ensure-evidence-branch`, `pr open-evidence-pr`,
  watermark stamps, manifest writes, ADO transitions). *Pro:* signal-rich
  for the reset/drift use cases. *Con:* misses polyphony-internal
  mutations that are still mutations someone might want a timeline for.

- **(c) Every external side-effect, regardless of verb.** Define "action"
  at the wire boundary (git mutation, gh/az/twig invocation, ADO REST
  write). *Pro:* most precise; would even catch side-effects from inside
  scripts. *Con:* requires intercepting every shell-out; impractical
  without a centralized side-effect layer that doesn't exist today.

- **(d) Every state-mutating verb, internal or external.** Strict
  superset of (b): polyphony-internal mutations (cache invalidations,
  manifest entries, journal-internal state) journal alongside
  externally-observable mutations. *Pro:* one rule, no boundary
  classification disputes; future-proof against verbs that move from
  external to internal or vice versa. *Con:* slightly higher volume
  than (b); requires that "state-mutating" stay a clean property of a
  verb.

**Decision: (d).** Every verb that mutates *any* state — external (git,
ADO, PRs, manifest) or polyphony-internal (caches, locks, journal
metadata itself) — carries `[JournaledAction]` and journals. The
boundary between "external" and "internal" is exactly the kind of
slippery distinction we should not be relitigating per verb.

**Carve-out (the only one):**
- Read-only verbs (`state next-ready`, `edges check`, `policy load`,
  `guidance extract`, `journal show`, `journal export`, etc.) do NOT
  journal.

The decorator pattern (D5) enforces this: a verb either has
`[JournaledAction]` (mutates → journals) or it does not (read-only).
Reviewer responsibility at PR time.

### D2 — Storage location

**Background — why "per-worktree" was the wrong frame.** Polyphony has
*nested* worktrees: a root run gets a feature worktree, which contains
plan/impl/evidence sub-worktrees, sometimes recursively. A
journal-per-worktree would shred a single root's timeline across many
files and require cross-worktree joins for any interesting query. The
correct grain is **per-root**.

**Options:**

- **(a) Per-root, in the root worktree.** Journal lives at
  `<root-worktree>/.polyphony-state/journal.db`, gitignored. Every
  sub-worktree (impl, evidence, plan) under the root walks up the
  worktree tree to find the root journal and writes through to it.
  *Pro:* one journal per root run = one cohesive timeline; sub-worktree
  actions are first-class entries in the root timeline; reset still
  works because tearing down the root worktree also tears down the
  journal. *Con:* the root worktree must exist before any sub-worktree
  action journals; bootstrap order matters.

- **(b) Per-repo, outside any worktree.** Single
  `<repo>/.polyphony-state/journal.db`. *Pro:* survives all worktree
  teardowns; cross-root queries trivial. *Con:* multiple concurrent
  root runs in the same repo (different roots, different worktrees)
  contend on the same DB; we'd need a discriminator column on every row
  and the noise of unrelated roots in every query.

- **(c) Per-root + per-repo rollup.** Per-root as the *write* store;
  copy / roll-up to per-repo on root completion or reset for cross-run
  debugging. *Pro:* best of both. *Con:* one more thing that can drift.

**Decision: (a) per-root, in the root worktree.** Single writer per
journal (the root run's same-root lock already serializes); single
timeline per root; tears down with the root worktree (which is the
right ownership boundary). Sub-worktrees discover the root journal by
walking up the worktree parent chain until they find `.polyphony-state/`
or hit the repo root.

**Resilience against bootstrap-order issues:** the journal is created
lazily on first write. Actions that happen before the root worktree
exists (e.g., the verb that creates the root worktree itself) journal
into a transient buffer that gets flushed once the journal file is
reachable. If the create-worktree action fails, the buffer is discarded
— there is no journal to attach it to anyway.

**Resilience against teardown loss:** a `polyphony journal export
<path>` verb dumps the journal to an arbitrary path before reset /
worktree cleanup. Useful for post-mortem.

Per-repo rollup is **deferred**. If cross-root debugging becomes a real
ask, we can add a rollup verb later that aggregates per-root journals
into a per-repo SQLite. We resist building it on day one.

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
    root_id         INTEGER,             -- nullable; not all actions are root-scoped
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
CREATE INDEX idx_actions_root      ON actions(root_id);
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

**Decision: greenfield, breakable.** Journal starts now. Runs older than
the journal-enablement commit stay observation-only forever. In-flight
root runs at the cut-over moment have partial journals — first half
observation-only, second half journaled — and we **accept that as
breakage**.

The polyphony-wide stance is "breaking changes are good; we don't manage
compatibility shims." Backfill is a compatibility shim by another name.
We don't ship one.

If post-cutover an old run's reset misbehaves because its history is
half-observed, the operator runs `polyphony reset root --force` (the
observation-based path that exists today) and moves on. The journal
benefits everyone *after* cutover.

### D7 — Retention

**Stated without options.** No retention management. Per D6, "no need
to manage history." SQLite is cheap; debugging value is high; we journal
forever locally. The journal lives and dies with the root worktree (per
D2); when reset tears down the root worktree, the journal goes with it.
There is no `journal vacuum` verb in v1.

### D8 — Concurrency

**Stated without options.** WAL mode SQLite. Multiple writers within a
single root (the root worktree plus any number of sub-worktrees writing
through to the root journal) are fine: the same-root run lock already
serializes parallel verb invocations within a root run. Cross-root
concurrency is not a concern: each root has its own journal (D2), so
different roots never contend on the same DB.

### D9 — Visibility / query surface

**Verbs to add:**

| Verb | Purpose |
|---|---|
| `polyphony journal show --work-item N` | per-WI timeline |
| `polyphony journal show --root N` | root-wide timeline |
| `polyphony journal show --run R` | per-run actions |
| `polyphony journal show --action <name>` | per-action-type filter |
| `polyphony journal drift --root N` | journal vs. observation diff (D10) |
| `polyphony journal export <path>` | dump for off-worktree debugging |

All emit the standard routing-style envelope; query verbs also support
`--render text` for human reading and `--render json` (default) for
tooling.

### D10 — Drift detection: the killer feature

The journal lets us answer **"is the world consistent with what polyphony
expects?"**

A drift check walks the journal for an root, projects expected state
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
trivial: enumerate journal entries for the root, delete the resources
they refer to, mark journal entries as cleaned up. No more "enumerate
branches whose names match a pattern."

### D11 — Routing-layer consumption (deferred)

Should workflow YAML query the journal directly?

- `polyphony journal has --action branch_create --target X --run R` → boolean
- Replaces ad-hoc tempfile-based idempotency in scripts

**Defer.** Tempting but expands scope. V1 ships the journal as a
polyphony-internal substrate; workflow integration comes later when we
know what queries actually matter.

### D12 — Artifact handling and the trust boundary

How do agent-produced artifacts (plan markdowns, code patches, draft PR
bodies, review comments) show up in the journal? **They don't — directly.
They show up indirectly via the polyphony verb that handles them.**

Three artifact patterns, three answers:

**1. File-shaped artifacts committed to git** (plan.md, code patches,
draft PR bodies that live in the repo).

Covered automatically by the existing `commit-and-push` journal entry.
The entry's `payload_json` records `[{path, sha, bytes}, ...]` for each
committed file. The *content* is recoverable from git by sha. The
journal is a pointer log, not a content store; git is the durable
content store and we don't duplicate.

**2. External-system artifacts** (PR comments, ADO attachments, review
votes, labels).

Covered by the posting verb carrying `[JournaledAction]`. e.g.,
`polyphony pr post-comment-ado` journals `{pr_id, comment_id_returned,
body_sha}`. The platform is the durable store; the journal records the
handle (comment_id) that lets you fetch the content.

**3. In-memory agent outputs that only inform routing** (planner returns
`{decision: split}` which drives a fan-out).

**Not in scope.** That's conductor's per-run event log territory;
polyphony never sees it. The journal carries a `run_id` field that
lets the two logs be joined offline.

**Agent-direct mutations (the trust-boundary case).**

Some agents shell out to `git` (or `gh`, `az`, `twig`) directly,
inside their own turn, without invoking a polyphony verb. The coder
agent in `implement-merge-group.yaml` commits code patches this way;
the fixer agent does the same in PR-review loops. **Polyphony does
not see these calls and the journal does not record them.**

This is intentional. Three observers exist:

| Observer | Knows |
|---|---|
| Conductor event log | Agent X ran from T1 to T2 |
| Git history | Commit Y landed on branch B at T1.5 |
| Polyphony journal | Silent during T1..T2 (no polyphony verb invoked) |

The full causal story is the *join* of all three — done offline by an
operator or a future tool. Polyphony's job is to be accurate about its
own slice, not to fabricate a complete picture by intercepting agent
git calls.

**Where the journal earns its keep here: drift.** The
`polyphony journal drift --root N` verb walks every branch under the
root and compares "last journal-touched sha on this branch" to "actual
HEAD on this branch." Agent-direct commits land in the diff as
*external commits ahead of last journaled state*, with the commit
author/committer surfacing who probably did it (agent, human, CI).
Invisible side-effects become visible classification without coupling
the agent's tooling to polyphony's CLI.

**Rejected alternative: `polyphony git commit` wrapper.** We considered
forcing agents to commit through polyphony so the journal sees them.
Rejected because it would require every agent prompt to use a
non-standard git verb (drift hazard), reinvent git's CLI inside
polyphony, and break the trust-boundary cleanliness ("polyphony is the
executor of polyphony's actions; the agent is the executor of the
agent's actions"). The drift detector solves the visibility problem
without the coupling.

---

## Phasing

| Phase | Scope | AC | Risk |
|---|---|---|---|
| **0** | ADR (this doc) | D1, D2, D6, D7, naming **decided**; Phase 1 shape open | None |
| **1A** | Schema + `JournalStore` + `[JournaledAction]` decorator + `polyphony journal show` (text + json render) + `polyphony journal export` | New verb suite green; existing verbs unaffected; journal is empty | Low — additive |
| **1B** | (1A) + `[JournaledAction]` on every branch verb | Per-root `journal show` shows real branch lifecycle on day one | Low+ — slightly bigger landing |
| **2** | PR ops journal (open, comment, merge, close — both GitHub and ADO legs) | Per-root `journal show` covers PR lifecycle | Medium |
| **3** | ADO transitions + tag mutations + watermark stamps + manifest writes journal | Drift check sees a full picture | Medium |
| **4** | `polyphony journal drift` | Drift verb returns sane diffs on a real root | Medium |
| **5** | `polyphony reset root` rewritten against journal; AB#3245 / AB#3246 close as side-effects | Reset tests pass; residue empirically gone on a clean run | Higher — operational |
| **6** | (optional) workflow-layer query verbs (D11) | Counter-like scripts begin retiring | Higher — workflow churn |

Phases 1-4 are non-disruptive: nothing changes for existing workflows.
Phase 5 is the payoff phase; phase 6 is bonus.

**Phase 1A vs. 1B is the only open question.** See the Open Questions
section above.

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

All design dimensions decided. Phase 1A is in flight; the remaining
phases (1B, 2-6) will be re-spec'd briefly before each lands. Open this
section again only if Phase 1A surfaces something the design didn't
anticipate.

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
