# Polyphony Tag Mechanism

> Status: Phase 1 design (PR-lifecycle overhaul).
> Authoritative source for the `polyphony:*` tag namespace.
> Companion to `docs/glossary.md` (terms) and `docs/polyphony-cli-reference.md` (verbs).

## Why

Polyphony needs a deterministic way to answer:

1. **Is this work item in scope for this run?** — when a workflow walks the tree under a root, it must distinguish items that belong to *this* polyphony pipeline from items that happen to be descendants but were created by humans / other tools / earlier runs.
2. **Which root does this item belong to?** — when a sub-workflow is invoked against an arbitrary item (e.g. `plan-level` re-entered to revise one plan), polyphony must walk up to find the originating root, OR fire the root fallback gate when no root is found.
3. **What status sub-state does this item already carry?** — e.g. "the planner has already declared this item planned and the artifact is committed", a precondition for downstream gates.

All three questions are answered by tags stored on the work item's `System.Tags` field. Tags are cheap, persistent, queryable from twig, and visible in the platform UI.

This document specifies the **`polyphony:*` namespace** — the tags polyphony stamps and reads, their semantics, and the CLI verbs that manipulate them.

## Storage

| Where  | What |
|---|---|
| Field  | `System.Tags` (ADO standard work-item field). |
| Format | Semicolon-delimited list. ADO normalizes to `"tag1; tag2; tag3"` on display but accepts unnormalized input on write. |
| Case   | Comparison is **case-insensitive** (matches ADO behaviour). Polyphony writes lower-case-with-colons. |
| Auth   | All writes go through `twig` (via `ITwigClient.PatchFieldsAsync`); polyphony does NOT call ADO directly. |

## Tag catalogue (Phase 1)

| Tag | Set by | Read by | Meaning |
|---|---|---|---|
| `polyphony` | `polyphony scope tag` (and indirectly by the planner when seeding children) | `polyphony scope check`, `polyphony scope list`, `polyphony reset run` | **Scope-ownership marker.** This descendant of a root is in-scope for the polyphony pipeline. |
| `polyphony:root` | `polyphony root declare` | `polyphony root resolve`, `polyphony reset run` | This item is the root of a polyphony run. Implies in-scope. |
| `polyphony:planned` | The planner agent on plan-completion (existing behaviour, preserved as-is) | The plan-level workflow's resume-detection gate, `polyphony reset run` | The planner has produced and committed a plan artifact for this item. Status sub-state. |
| `polyphony:facets=<csv>` | `polyphony plan seed-children` | `RequirementInputResolver`, `polyphony reset run` | Per-item facet override for indivisible apex items. |
| `polyphony:run-{root_id}` | *(reserved — not stamped in Phase 1)* | *(reserved)* | OPTIONAL run-association marker for future multi-run concurrency. Slot reserved; no Phase 1 verb writes it. |

> **Typed source of truth:** The `PolyphonyTag` discriminated union in
> `src/Polyphony/Tagging/PolyphonyTag.cs` is the authoritative enum of
> all tags polyphony owns. The `IsPolyphonyOwned(string)` helper matches
> any tag in the `polyphony:*` namespace (including future variants).
> `polyphony reset run` consumes this helper to strip all owned tags.

### In-scope semantics

An item is **in-scope** for the polyphony pipeline iff at least one of:

- It carries the `polyphony:root` tag (it IS a root).
- It carries the `polyphony` tag (it's a descendant explicitly tagged in-scope).

An item that is a descendant of a root but carries NEITHER tag is **out-of-scope**: polyphony will report it in the close-out summary but NOT operate on it. Scope status does NOT block close-out or the feature PR.

### Why two scope tags (`polyphony` and `polyphony:root`)?

Roots and descendants serve different lookup purposes:

- `polyphony:root` is rare (one per run) and supports the cheap upward walk in `polyphony root resolve`.
- `polyphony` is common (potentially every item under a root) and supports the cheap downward filter in `polyphony scope list`.

Stamping both on the root would add a redundant tag and confuse `root resolve` (which would need to skip "root descendants" with the bare tag). Keeping them disjoint keeps each lookup O(direct check).

## Verbs (Phase 1)

All verbs emit JSON to stdout and exit 0 on success. Failures (e.g., work item not found) emit JSON with `error` set and exit non-zero.

### `polyphony scope check <id>`

Returns the scope-ownership disposition of a single item.

```jsonc
{
  "work_item_id": 1234,
  "in_scope": true,
  "is_root": false,
  "tags": ["polyphony", "twig"]
}
```

### `polyphony scope list <root_id>`

Walks the work hierarchy under `root_id` and returns every in-scope item (root + tagged descendants).

```jsonc
{
  "root_id": 100,
  "in_scope_items": [
    { "id": 100, "is_root": true,  "title": "...", "type": "Epic" },
    { "id": 101, "is_root": false, "title": "...", "type": "Issue" },
    { "id": 102, "is_root": false, "title": "...", "type": "Task" }
  ],
  "out_of_scope_items": [
    { "id": 103, "title": "...", "type": "Bug" }
  ],
  "in_scope_count": 3,
  "out_of_scope_count": 1
}
```

### `polyphony scope tag <id>`

Adds the `polyphony` tag to the item. **Idempotent** — if the tag is already present, no write is performed and `changed: false` is returned.

```jsonc
{
  "work_item_id": 1234,
  "changed": true,
  "tags_before": ["twig"],
  "tags_after":  ["twig", "polyphony"]
}
```

### `polyphony scope untag <id>`

Removes the `polyphony` tag. **Idempotent**. Does NOT remove `polyphony:root` (use `root undeclare` for that, in a future phase).

```jsonc
{
  "work_item_id": 1234,
  "changed": true,
  "tags_before": ["twig", "polyphony"],
  "tags_after":  ["twig"]
}
```

### `polyphony root declare <id>`

Stamps `polyphony:root` on the item. Idempotent. The item is now a root for the polyphony pipeline; subsequent `polyphony root resolve` calls under it will return its ID.

```jsonc
{
  "work_item_id": 1234,
  "changed": true,
  "tags_before": [],
  "tags_after":  ["polyphony:root"]
}
```

### `polyphony root resolve <id>`

Walks ancestors (from `id` upward) to find the nearest item carrying `polyphony:root`. Returns the resolved root ID, the chain of ancestors walked, and a flag indicating whether the fallback gate is required.

```jsonc
{
  "work_item_id": 1234,
  "resolved_root_id": 100,
  "ancestors_walked": [1234, 234, 100],
  "fallback_required": false
}
```

If no ancestor carries `polyphony:root`, `resolved_root_id` is null, `fallback_required` is true, and the workflow must fire the root fallback gate (Phase 1 deliverable, separate sub-workflow) to either treat the current item as root or abort.

```jsonc
{
  "work_item_id": 1234,
  "resolved_root_id": null,
  "ancestors_walked": [1234, 234],
  "fallback_required": true
}
```

## Idempotency contract

Every `tag` / `untag` / `declare` verb is idempotent:

- If the requested mutation is a no-op (tag already present for `tag`/`declare`, or tag absent for `untag`), no write to ADO is performed.
- `changed: false` is returned in that case.
- `changed: true` only when ADO was actually written.

This is critical for resume-safe workflows: a tree-walker that re-enters a partially-completed run must not double-stamp tags or generate spurious ADO history.

## Workflow integration (preview)

Phase 7's tree-walker workflow uses these verbs as follows:

1. **Entry**: tree-walker receives `root_id` as input. Calls `polyphony root declare {root_id}` (idempotent) to stamp the root tag.
2. **Worklist build**: calls `polyphony scope list {root_id}` to enumerate in-scope items.
3. **Per item**: when seeding children during planning, the seeder calls `polyphony scope tag {child_id}` so the new children appear in subsequent worklist rebuilds.
4. **Sub-workflow re-entry**: a directly-invoked sub-workflow (e.g., `plan-level`) calls `polyphony root resolve {item_id}`. If `fallback_required: true`, the workflow fires the root fallback gate.

## Migration

- Phase 1: verbs ship; existing `polyphony:planned` semantics unchanged; nothing reads/writes `polyphony` or `polyphony:root` from workflows yet.
- Phase 2: workflow integration begins; the existing planner adds `polyphony:planned` AND a `polyphony scope tag` call when seeding children.
- Phase 7: tree-walker uses `root resolve` and `scope list` as primary inputs.

No back-compat concerns — these tags are new.
