# `polyphony reset` — Scrub polyphony state for a root and re-dispatch

> **Status:** Design specification (AB#3173). The verb implementation ships
> as sibling tasks; this document is the authoritative reference for scope,
> flags, UX, and the remediation pattern.

## Why

Polyphony's outputs — ADO tags, comments, branches, per-root state, and
worktrees — become inputs to the next run on the same root. Without a
scrub mechanism, re-running the apex driver against an already-processed
root deterministically fails: the `polyphony:planned` tag tricks the
architect into skipping planning, the empty merge group (because all work
is already on `main`) fires a false-positive `structural_violation`, and
the scope reviewer enters a 12-cycle loop before hitting
`scope_revise_cap_gate` (AB#3165).

`polyphony reset --root-id N` is the one-shot path to put a work item
back into a re-dispatchable shape. It scrubs every polyphony-authored
artifact for root `N`, then the operator can reopen the work item and
re-launch the pipeline with a clean slate.

## Synopsis

```
polyphony reset --root-id <N> [--force] [--dry-run]
```

| Flag | Default | Behavior |
|---|---|---|
| `--root-id <N>` | *(required)* | The ADO work item ID of the run root whose polyphony state should be scrubbed. |
| `--dry-run` | off | Enumerate every artifact that would be touched and emit the plan as JSON — no mutations are performed. |
| `--force` | off | Skip the interactive confirmation gate and proceed directly to scrubbing. |

When neither `--force` nor `--dry-run` is passed, the verb prints a
summary of every artifact it will touch and prompts the operator with a
`[y/N]` confirmation gate before performing any mutation.

## Scrub scope

The reset verb removes **exactly** the artifacts polyphony authored for a
given root. It does not touch operator-created tags, human-authored
comments, or branches unrelated to root `N`.

### 1. ADO tags

Strips all tags matching the `PolyphonyTag` discriminated union from the
root work item (and, recursively, its in-scope descendants). The
`PolyphonyTag` DU is the sole source of truth for which tags polyphony
owns:

| Variant | Tag pattern | Example |
|---|---|---|
| **State** | `polyphony:planned` | Marks a planned item |
| **Intent** | `polyphony:root` | Marks the run root |
| **Facet** | `polyphony:facets=<value>` | `polyphony:facets=implementable` |
| **Scope** | `polyphony` | Bare scope-ownership marker |

Tags **not** matching these patterns (e.g. `twig`, operator-created tags)
are preserved. The DU is defined in `src/Polyphony/Models/PolyphonyTag.cs`
and consumed by both `reset` and the scope verbs (`scope check`, `scope
tag`, `scope untag`).

### 2. Polyphony-authored ADO comments

Comments authored by the polyphony pipeline are **archived** to a sidecar
artifact before deletion, then cleared from the work item. The sidecar
preserves the comment history for forensic review.

**Sidecar location:**

```
<git-common-dir>/polyphony/<root_id>/comment-archive.json
```

This follows the per-root state directory convention established by the
branch model ADR (Rev 4.2 § State location). The sidecar persists across
resets so the operator can inspect prior run comments even after multiple
reset cycles.

**Sidecar format:**

```jsonc
{
  "root_id": 3127,
  "archived_at": "2026-05-14T12:00:00Z",
  "comments": [
    {
      "work_item_id": 3127,
      "comment_id": 42,
      "text": "polyphony: plan artifact committed at ...",
      "created_date": "2026-05-13T18:00:00Z",
      "author": "Daniel Green"
    }
    // ... additional comments
  ]
}
```

Each reset **appends** to the archive (the `comments` array grows across
resets). The archive is never truncated by the reset verb itself.

### 3. Per-root state directory

Deletes the entire per-root state directory:

```
<git-common-dir>/polyphony/<root_id>/
```

This directory holds the run manifest (`run.yaml`), the run lock
(`locks/run.lock`), and the comment archive sidecar. The comment archive
is written *before* the directory is removed, so the archive of the
current run's comments is captured in the sidecar before deletion.

**Ordering:** archive comments → write sidecar → delete state directory.
The sidecar is written into a temporary location first, then the state
directory is deleted, and finally the sidecar is moved back into a freshly
created state directory. This preserves the archive across resets.

### 4. Per-root branches (local and remote)

Deletes all polyphony-managed branches for root `N`, both locally and on
the configured remote:

| Branch pattern | Example |
|---|---|
| `feature/<N>` | `feature/3127` |
| `plan/<N>*` | `plan/3127`, `plan/3127-3128` |
| `impl/<N>-*` | `impl/3127-3127`, `impl/3127-3128` |
| `mg/<N>_*` | `mg/3127_pg-3127` |
| `sdlc/apex/<N>` | `sdlc/apex/3127` |

The branch patterns derive from the canonical branch tree documented in
`docs/decisions/branch-model.md`. Branches not matching these patterns are
never touched.

**Remote deletion** uses `git push origin --delete <branch>` for each
matched remote ref. Local branches are deleted with `git branch -D`.
Branches that exist only on the remote (or only locally) are handled
independently — the verb does not fail if a branch exists in one location
but not the other.

### 5. Per-run worktrees

Removes the per-apex worktree tree:

```
<runs-root>/apex-<N>/
```

This is the worktree root created by `polyphony worktree init-apex` (see
`docs/per-run-worktree-layout.md`). All child worktrees under the apex
root (feature, plan, impl, mg, evidence) are removed via
`git worktree remove` before the directory is deleted.

## JSON output contract

### Dry-run output

```jsonc
{
  "root_id": 3127,
  "dry_run": true,
  "tags_to_strip": [
    {
      "work_item_id": 3127,
      "tags": ["polyphony:root", "polyphony:planned", "polyphony:facets=implementable"]
    },
    {
      "work_item_id": 3128,
      "tags": ["polyphony", "polyphony:planned"]
    }
  ],
  "comments_to_archive": 4,
  "state_dir": "/Users/you/projects/polyphony.git/polyphony/3127/",
  "branches": {
    "local": ["feature/3127", "plan/3127", "impl/3127-3127", "mg/3127_pg-3127", "sdlc/apex/3127"],
    "remote": ["feature/3127", "plan/3127", "impl/3127-3127", "mg/3127_pg-3127", "sdlc/apex/3127"]
  },
  "worktree_dir": "/Users/you/projects/polyphony-runs/apex-3127/"
}
```

### Execution output

```jsonc
{
  "root_id": 3127,
  "dry_run": false,
  "tags_stripped": 5,
  "comments_archived": 4,
  "comment_archive_path": "/Users/you/projects/polyphony.git/polyphony/3127/comment-archive.json",
  "state_dir_deleted": true,
  "branches_deleted": {
    "local": ["feature/3127", "plan/3127", "impl/3127-3127", "mg/3127_pg-3127", "sdlc/apex/3127"],
    "remote": ["feature/3127", "plan/3127", "impl/3127-3127", "mg/3127_pg-3127", "sdlc/apex/3127"]
  },
  "worktree_dir_deleted": true
}
```

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Reset completed (or dry-run enumeration succeeded). |
| `1` | Runtime failure (git error, ADO unreachable, etc.). |
| `3` | Work item not found in twig cache. |

## The remediation pattern

When the launcher (`scripts/Invoke-PolyphonySdlc.ps1`) refuses to dispatch
because the target work item is in a terminal state (AB#3165 Item 2), the
refusal message tells the operator to run `polyphony reset`. The full
remediation pattern is:

### Step 1: Reset polyphony state

```bash
polyphony reset --root-id <N>
```

This strips all polyphony-authored state for root `N`. Use `--dry-run`
first to inspect what will be removed:

```bash
polyphony reset --root-id <N> --dry-run
```

### Step 2: Reopen the work item

```bash
twig set <N>
twig state 'To Do'
```

The reset verb scrubs polyphony's artifacts but does **not** transition
the ADO work item state. The operator must explicitly reopen the work item
to move it out of the terminal state (`Done`, `Closed`, `Removed`,
`Resolved`) that triggered the pre-flight refusal.

### Step 3: Re-launch the pipeline

```bash
./scripts/Invoke-PolyphonySdlc.ps1 -ApexId <N>
```

The pipeline now starts with a clean slate: no stale tags, no prior plan
artifacts, no leftover branches or worktrees. The apex driver will treat
the work item as a fresh dispatch.

### When to use the remediation pattern

Use this pattern when **all** of the following are true:

1. The launcher refuses with the pre-flight terminal-state refusal
   message.
2. You want to **start fresh** — not resume or extend the prior run.
3. The prior run's work has already landed on `main` (or is no longer
   needed).

If you want to **resume** a prior run (e.g. the conductor crashed
mid-run), use `-Intent resume` instead — this bypasses the terminal-state
check and re-attaches to the existing branches and state.

### What the remediation pattern does NOT do

- **It does not revert code changes.** Commits already merged to `main`
  via feature PRs are permanent. The reset only removes polyphony's
  orchestration state.
- **It does not transition the work item.** The operator must separately
  reopen the work item via `twig state`.
- **It does not affect other roots.** Each root's state is isolated under
  `<git-common-dir>/polyphony/<root_id>/`. Resetting root `3127` does not
  touch root `3085`.

## `PolyphonyTag` discriminated union

The reset verb's tag-stripping logic is driven by the `PolyphonyTag` DU —
a typed model that is the single source of truth for which tags polyphony
owns. This replaces the previous ad-hoc string checks scattered across
`scope check`, `scope tag`, `scope untag`, and `root resolve`.

| Variant | Pattern | Semantics |
|---|---|---|
| `State` | `polyphony:planned` | Status sub-state set by the planner. |
| `Intent` | `polyphony:root` | Marks the item as a polyphony run root. |
| `Facet` | `polyphony:facets=<value>` | Facet assignment stamped during planning. |
| `Scope` | `polyphony` (bare) | In-scope marker for descendants. |

The DU is defined in `src/Polyphony/Models/PolyphonyTag.cs`. A static
`TryParse(string tag, out PolyphonyTag result)` method returns `true` for
any tag that matches one of the four variant patterns, `false` otherwise.
The reset verb calls `TryParse` on every tag in the work item's
`System.Tags` field and strips those where `TryParse` returns `true`.

### Tag catalogue cross-reference

The authoritative tag catalogue is `docs/polyphony-tags.md`. That document
specifies the `polyphony:*` namespace, idempotency contracts, and workflow
integration. The `PolyphonyTag` DU is the *typed* encoding of that
catalogue for consumption by C# verbs. When the catalogue adds a new tag,
a corresponding DU variant must be added.

## Cross-references

- **Branch model (branch patterns):**
  `docs/decisions/branch-model.md` — canonical branch tree and naming
  rules that define the branch patterns the reset verb deletes.
- **Per-root state location (Rev 4.2):**
  `docs/decisions/branch-model.md` § State location — the
  `<git-common-dir>/polyphony/<root_id>/` directory the reset verb deletes.
- **Per-run worktree layout:**
  `docs/per-run-worktree-layout.md` — the `polyphony-runs/apex-<N>/`
  directory the reset verb removes.
- **Tag catalogue:**
  `docs/polyphony-tags.md` — the `polyphony:*` tag namespace that defines
  what the reset verb strips.
- **Terminal-state refusal (AB#3165 Item 2):**
  `scripts/Invoke-PolyphonySdlc.ps1` lines 244–305 — the pre-flight
  check whose refusal message points operators to this remediation pattern.
- **Re-run idempotency epic:**
  AB#3165 — the parent epic that motivated the reset verb.
- **CLI developer conventions:**
  `.github/skills/polyphony-cli-developer/SKILL.md` — ConsoleAppFramework
  patterns, AOT JSON serialization, exit code conventions.
- **State effects catalog:**
  `docs/polyphony-state-effects-catalog.md` — pre/post conditions for
  polyphony verbs; the reset verb's entry should be added when the
  implementation ships.
