# Sibelius — Seeding Plan: Platform Reality Check

**Date:** 2026-05-29T09:33:07-07:00  
**Lens:** ADO + GitHub integration / what the platform actually gives you

---

## 1. What does "seed" actually do today?

`polyphony plan seed-children` fires `twig.CreateChildAsync(parentId, type, title, description)` **once per child item, serially, over ADO REST.** No batch. No transaction. Each call creates one work item and establishes a parent-child link. On success it embeds a marker `<!-- polyphony:plan-child-id=task-N -->` in the description and stamps `polyphony:planned` on the parent via `twig.PatchFieldsAsync` + `twig.SyncAsync` (the sync is critical — without it the tag lives only in twig's local cache).

**Output today:** `PlanSeedChildrenResult` already emits `seeded_items` (items created this run), `reused_items` (items reconciled to an existing ADO item), `error_count`, and `warnings`. The seeder already knows what it did. What it does NOT do: write that knowledge anywhere durable before the run ends. If the conductor job dies, the output is gone.

**What it writes to git:** Nothing directly. The sidecar `plans/plan-{id}.children.json` is written by `plan write-plan`, not the seeder. The seeder *reads* it as fallback input.

---

## 2. What's queryable for "semantic equivalence"?

The marker `<!-- polyphony:plan-child-id=task-N -->` in description is the most stable signal polyphony already writes. It survives renames. You query it today via `twig show-tree` (which returns children with their description fields) — that's exactly what `BuildIndexes` does on restart.

**The problem:** `twig show-tree` reads ADO descriptions, which requires a live network call every time. This works fine for idempotency within one run. For cross-run reconciliation against a seeding plan, you need the description query to be reliable. In practice it is — ADO description fields are durable and not auto-edited by anything except the seeder itself.

**Title match is genuinely fragile** (humans rename, the architect wordsmithes). The code already handles this: marker match first, title fallback second with a warning. Bach's normalize-hash proposal is a middle path, but the marker already exists and is stronger. **My recommendation: use the marker as the canonical cross-run identity, not title hash.** The seed manifest should record `{ child_id: "task-N", ado_id: 3072 }` — exactly what `SeedReconciliation` already emits.

**What polyphony does NOT stamp:** no custom field, no `System.AreaId` marker, no tag on children (only the parent gets `polyphony:planned`). The marker is a description comment, not a queryable ADO field. You cannot WIQL-query it. You must fetch descriptions to find it. This is a real limitation.

---

## 3. GitHub vs ADO split

**Seeding is inherently ADO-shaped.** GitHub Issues has no work item hierarchy — no parent-child link, no area paths, no custom fields. A GitHub Issue has: title, body, labels, assignees, milestone. That's it. The concept of "5 children under root R" doesn't exist natively in GitHub Issues.

**What this means for the seeding plan concept:**
- The seeding plan itself (the JSON manifest of planned children) is platform-agnostic — it's just intent declared by the planner.
- The *execution* of that plan is ADO-specific. On GitHub, there's no structural equivalent.
- `recreate-stale-descendant` is already **GitHub-only** by design (the code says so explicitly: "This GitHub-only implementation matches the reach of the rebase sibling shipped in #107. ADO P9 restack is a separate later workstream.").

**Push-back on making it platform-agnostic at this layer:** the seeding plan concept should carry a `platform` field OR make the manifest explicitly ADO-typed now, with a note that GitHub-Issues support would require a separate implementation. Trying to paper over this difference prematurely will produce an abstraction that satisfies neither. ADO gives you work item trees. GitHub Issues does not. These are not the same thing.

---

## 4. What does PARTIAL seed look like in practice?

Each child is a separate REST call. Real failure modes:

| Failure mode | Produces partial state? | Recoverable by retry? |
|---|---|---|
| Network drop after 3 of 5 children created | Yes — 3 items exist in ADO, 2 do not | **Yes** — seeder's marker/title fallback reuses existing items on next run |
| ADO rate limit (HTTP 429) mid-batch | Yes | Yes, with backoff |
| Permission denied on specific area path | Yes — only items in that path fail | **No** — needs human fix first |
| Validation rule rejecting one field value | Yes — that one child fails, others succeed | No — needs plan correction |
| `twig SyncAsync` fails after all items created | No partial items — but parent tag is missing | Yes — retry reuses existing items via marker, restamps tag |

**The dangerous one:** ADO validation rule rejection. Example: a title exceeds 255 chars (ADO limit), or the type name is invalid for the project's process template (e.g., "Task" vs "User Story" in Basic). The child is not created. On retry, the marker is absent, title-type match also fails (item doesn't exist), so the seeder tries to create it again — and fails again. This is NOT a transient error; retrying 3 times won't help. The seeder correctly surfaces it as `SeedError` with the rejection message, but the current workflow treats all seeder errors identically.

**The other dangerous one:** `twig new` response shape failures. The seeder already handles three fallback paths for extracting the created ID (direct `id` field, URL field, message text parsing). If ALL three fail, the item may exist in ADO but the seeder records an error and doesn't know the ADO ID. On retry, the marker lookup fails (marker was embedded in the twig call — if the ID is unknown, we don't know whether twig even succeeded). This is the actual corrupt state risk.

---

## 5. The "corrupt state" Daniel is worried about

Concretely, the worst case today: `twig.CreateChildAsync` succeeded in ADO, the item exists with the polyphony marker in its description, but the JSON response gave back `id: 0` AND no `url` AND no `message "Created #N"`. The seeder records this as a `SeedError`. On retry, `BuildIndexes` queries the ADO tree via `twig show-tree`, finds the item by marker, and **correctly reuses it**. So the marker recovery path handles this.

**True corrupt state:** item created in ADO WITHOUT the marker in its description (impossible — the description is assembled before the `twig new` call and includes the marker; if the creation succeeded the marker is there). **Conclusion: the marker embeds atomically with creation. If the item exists in ADO, it has the marker. The seeder's reconciliation on restart will find it.**

**What the directive rightly worries about:** the case where the seeder DOESN'T know the item was created (error recorded, marker response missing), and someone calls `polyphony state next-ready` which advances work based on `polyphony:planned` tag — which the seeder skips stamping when `error_count > 0`. So a partial-error seed should NEVER stamp `polyphony:planned` on the parent. **The current code already enforces this** (`if (errors.Count == 0)` gates the tag stamping).

What SHOULD be added: the seed manifest Bach proposes, so the seeder on retry knows what was intended and can compare against ADO without re-running the planner.

---

## 6. Cross-link to renegotiation

Renegotiation today (`recreate-stale-descendant`) operates on **plan branches and PRs**, NOT on ADO work items. When the architect replans, renegotiation:
1. Closes the stale plan PR.
2. Deletes the head branch (best-effort).
3. Creates a fresh plan branch from current parent-plan tip.
4. Opens a new PR.

**It does not touch ADO children at all.** The original children are still in ADO. If the new plan removes a child, that child becomes an orphan — it has the `polyphony:plan-child-id` marker but no corresponding entry in the new plan. The seeder called against the new plan will not reuse it (marker mismatch with new `child_id`s if the architect re-IDs) and won't delete it.

**This is the primary gap.** The seed manifest concept needs to track `plan_generation` so the seeder can detect "this ADO item was created for plan generation 1 but the current plan is generation 2 — it's an orphan, not a reuse candidate." Without this, renegotiation + re-seed silently creates duplicate children (the old ones stay, new ones get created under new IDs).

**Platform note:** renegotiation is GitHub-only today. ADO PR renegotiation is explicitly deferred. Any seed manifest that references plan PRs must account for this asymmetry.
