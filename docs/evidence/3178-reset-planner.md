# Evidence: Implement reset planner — enumerate artifacts and emit dry-run JSON

**Work Item:** AB#3178
**Implementation Branch:** `impl/3165-3178`
**Commit:** `309e8f7` — `feat: implement ResetPlanner with dry-run ResetPlan enumeration AB#3178`

## What Was Implemented

A standalone `ResetPlanner` class (`src/Polyphony/Reset/ResetPlanner.cs`) and
`ResetPlan` record (`src/Polyphony/Reset/ResetPlan.cs`) that enumerate every
artifact a `polyphony reset` would touch for a given root, **without performing
any mutation**.

### Artifact Categories Enumerated

| Category | Source | Identification Heuristic |
|----------|--------|--------------------------|
| ADO tags | Root + all in-scope descendants via `HierarchyWalker` | `PolyphonyTag.IsPolyphonyOwned` — matches `polyphony` or `polyphony:*` |
| PR comment threads | PRs whose source branch belongs to the root | Closed, top-level threads (status=closed, no file path) — matches `PostCommentAdo` pattern |
| State directory | `<git-common-dir>/polyphony/N/` | `PolyphonyStatePaths.GetStateRootAsync` |
| Local branches | `git branch --list` | `BranchNameParser` grammar: `feature/N`, `plan/N*`, `impl/N-*`, `mg/N_*`, `evidence/N-*`, `sdlc/apex/N` |
| Remote branches | `git branch -r` | Same grammar as local |
| Worktrees | `git worktree list --porcelain` | Path under `polyphony-runs/apex-N/` via `RunsRootResolver` |

### Key Design Decisions

- **Comment enumeration via PR source branch matching** rather than work item
  comment filtering. `PostCommentAdo` creates closed, top-level advisory threads
  on PRs — the planner identifies these by listing PRs whose source branch
  belongs to the root (via `BranchBelongsToRoot`), then filtering threads by
  status=closed + no file path.
- **ADO context is optional** — comment enumeration requires `organization`,
  `project`, and `repository` parameters. When absent, comments are skipped
  gracefully (returns empty array, not null).
- **Best-effort everywhere** — Git/ADO/filesystem failures in individual
  categories do not block enumeration of other categories.
- **`sdlc/apex/N` branch pattern** included alongside the five `ParsedBranch`
  grammar patterns, since it falls outside the formal branch grammar but is
  polyphony-owned.

### `ResetPlan` Serialization

`ResetPlan` and its supporting records (`ResetPlanTagRemoval`, `ResetPlanComment`)
are registered in `PolyphonyJsonContext` for AOT-safe serialization with
`snake_case` property naming and null omission.

## Test Coverage

**16 unit tests** in `tests/Polyphony.Tests/Reset/ResetPlannerTests.cs`:

- **Tag enumeration:** root-only tags, root + descendant tags, non-polyphony tags excluded, `polyphony:facets=*` included
- **Branch enumeration:** local branches across all 6 patterns, remote branches, unrelated branches excluded
- **Worktree enumeration:** worktrees under apex dir matched, others excluded
- **Comment enumeration:** closed top-level threads matched, open/file-attached threads excluded, no-ADO-context graceful skip
- **State directory:** exists vs. does not exist, non-git-repo fallback
- **JSON serialization:** round-trip contract, snake_case naming, null omission, deterministic output

All tests stub `IAdoClient` and git/worktree probes via `FakeAdoClient` and
`FakeProcessRunner`.

## Files Changed

| File | Change |
|------|--------|
| `src/Polyphony/Reset/ResetPlan.cs` | **New** — `ResetPlan`, `ResetPlanTagRemoval`, `ResetPlanComment` records |
| `src/Polyphony/Reset/ResetPlanner.cs` | **New** — 344-line planner with 5 enumeration methods |
| `src/Polyphony/PolyphonyJsonContext.cs` | Added `ResetPlan`, `ResetPlanTagRemoval`, `ResetPlanComment` serialization |
| `tests/Polyphony.Tests/Reset/ResetPlannerTests.cs` | **New** — 16 unit tests covering all artifact categories |
