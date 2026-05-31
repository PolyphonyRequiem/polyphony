# Bach — Seeding Plan Architecture (PR #535 Q1)

**Date:** 2026-05-29  
**Lens:** Architecture / ADR fit

---

## 1. Is "seeding plan" the right concept?

**Push-back:** The ADO work item tree IS the plan — after seeding succeeds. The problem isn't "we need a plan," it's "we need a checkpoint of planner intent so we can reconcile partial execution." I'd call this a **seed manifest**, not a "seeding plan." A plan implies decision-making; this is a record of what the planner already decided, persisted before execution begins.

The ADO tree alone is insufficient because: (a) partial seeds leave ADO in a state where you can't distinguish "planner didn't want this child" from "seeder failed to create it," and (b) ADO is mutable — someone could manually add/remove items. The manifest is the planner's declared intent, frozen at decision time.

## 2. Where does it live and what is it?

A polyphony verb output, written to the run state directory:

```
.polyphony/state/{rootId}/seed-manifest.json
```

Schema: `{ plan_generation: int, created_at: ISO8601, items: [{ id?: "AB#nnnn", type: string, title: string, parent_id: string, facets: string[] }] }`. Items get their `id` field populated as seeding succeeds. Unpopulated `id` = not yet seeded.

**Lifecycle:** Created by planner verb. Consumed by seeder verb (which patches `id` fields on success). Read by reconciliation on restart. Replaced wholesale on renegotiation (new `plan_generation`).

## 3. What does "semantically equivalent" mean?

The comparison primitive is **type + parent linkage + title-normalized hash**. Not title-exact-match (titles get wordsmithed). Not ID (IDs don't exist until seeding succeeds).

Concretely: `hash(lowercase(type) + ":" + parent_id + ":" + normalize(title))` where `normalize` strips punctuation and collapses whitespace. If ADO contains an item matching this hash under the same parent, it's "already seeded." If the manifest has an entry with no matching ADO item, it needs seeding.

This is loose enough to survive minor title edits but tight enough to catch genuinely missing children.

## 4. Renegotiation interaction

Each replan produces a **new manifest** with an incremented `plan_generation`. The old manifest is not migrated — it's replaced. Items already seeded (have `id` populated) are carried forward into the new manifest as "already satisfied." Items in the old manifest that are absent from the new manifest are orphans — the workflow should note them but NOT delete them from ADO (polyphony never deletes work items; that's a twig/human decision).

This aligns with the branch-model ADR's renegotiation flow: renegotiation is a fresh planning pass with awareness of existing state, not a patch on the old plan.

## 5. ADR placement

**New ADR: `seed-manifest-as-durable-state.md`**. Reasons:

- Gate-compression is about the emit+poll pattern — wrong home.
- Domain-signal-envelope is about wire format — wrong home.
- This is a new durable state concept with its own lifecycle, schema, and reconciliation semantics.

It REFERENCES verb-error-boundary (seeder infrastructure failures use exit codes 3/5 for retry logic) and the renegotiation section of branch-model.

## 6. PR #535 survival

PR #535 survives but needs a rebase. The gate removal is correct (Daniel said "skip the gate"). What gets added: the seeder verb reads the manifest, reconciles before seeding, and emits infrastructure-failure exits (codes 3/5) on network/auth errors for retry via `on_error:`. The "3 retries then stop" becomes a conductor `on_error:` chain with a counter, not a human gate.

---

## Recommendation

**Build the seed manifest as a thin verb-output artifact, not a new subsystem.** The planner verb writes it; the seeder verb reads and patches it; reconciliation is a comparison function, not a service. This avoids the two-sources-of-truth trap because the manifest is explicitly a "desired state" document (like a Terraform plan), while ADO is the "actual state." The reconciliation is the diff.

The concept is NOT over-engineered if we keep it as a JSON file with 5 fields. It IS over-engineered if it grows its own storage layer, versioning system, or becomes queryable. Keep it dead simple: one file, one schema, replaced on replan.

New ADR required. PR #535 lives, rebased onto manifest-aware seeder.
