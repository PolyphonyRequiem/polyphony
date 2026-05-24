---
doc_type: decision
status: proposed
synopsis: Retire the legacy `pattern` reset strategy; projection becomes the only path. Close the residual `sdlc/root` branch-pattern hole that the pattern leg covers today. Split the journal store into an explicit read-only reader for diagnostic surfaces.
---

# Retire pattern reset strategy; projection becomes the only path

**Status:** Proposed
**Date:** 2026-05-24
**Supersedes prior draft:** This ADR replaces the never-merged `run-inventory.md` draft from the same date. The earlier draft proposed a new `RunInventory` module + verdict vocabulary + manifest-as-expectation-source; rubber-duck review + an empirical investigation of the existing `src/Polyphony/Journal/{Observers,Drift,Reset,Projections}/` infrastructure showed the proposed module duplicated what already ships behind `--strategy projection`. The honest remaining work is narrower than the draft assumed and is captured here.

## Context

`polyphony reset root` currently dispatches between two reset strategies (`src/Polyphony/Commands/ResetCommands.Root.cs:26-124`):

- **`projection`** (default) — `ProjectionResetExecutor` orchestrates: read journal → coverage check → `JournalDriftAnalyzer` reconciles expected (from `CurrentExpectedState.Project`) against observed (from per-kind `IResourceObserver` implementations) → `ProjectionResetPlanner` builds a plan with an externally-mutated guard → per-kind `IResourceDeleter` acts. This is the architectural deepening.
- **`pattern`** (legacy) — `ResetRootPatternCoreAsync` chains six one-leg-at-a-time helpers: `RunPrsAsync`, `RunWorktreesAsync`, `RunBranchesAsync`, `RunFacetsAsync`, `RunManifestAsync`, `RunStateAsync`, each defined in its own partial in `src/Polyphony/Commands/ResetCommands.{Prs,Worktrees,Branches,Facets,Manifest,State}.cs`. Each leg re-derives "what does this root own?" from a hand-rolled regex over `git for-each-ref` (or equivalent), with no shared notion of expected vs found.

Two bugs in 2026-05 confirmed the structural fragility of the per-leg pattern derivation:

- **AB#3245** — `polyphony reset worktrees` (pattern leg) emitted `Permission denied` while the worktree was already gone on disk. Patched in-place in the pattern leg via a 3-attempt retry with mid-attempt `Directory.Exists` short-circuit (`src/Polyphony/Commands/ResetCommands.Worktrees.cs:236-301`).
- **AB#3246** — `polyphony reset branches` (pattern leg) missed nested `plan/{root}-{child}` and per-item `sdlc/root/{id}` branches because the leg's name regex didn't recognize them. Patched in-place in the pattern leg via `RootBranchPatterns` (`src/Polyphony/Commands/ResetCommands.cs:83-96`) + `EnumerateRootSdlcBranchesAsync` (`src/Polyphony/Commands/ResetCommands.Branches.cs:273-313`).

Both fixes were tactical patches to the *pattern* path. The projection path was independently immune to AB#3245 (`GitWorktreeDeleter.RemoveWithRetryAsync` already retries + treats "path is gone" as success — `src/Polyphony/Journal/Reset/Deleters/GitWorktreeDeleter.cs:37-64`) and largely immune to AB#3246 (`GitBranchDeleter` acts on journal-derived IDs directly — `src/Polyphony/Journal/Reset/Deleters/GitBranchDeleter.cs:33-56`).

One residual hole exists on the projection path: `ResourceObserverSupport.MatchesPolyphonyBranchPattern` (`src/Polyphony/Journal/Observers/ResourceObserverSupport.cs:120-130`) recognizes `feature/{r}`, `plan/{r}`, `plan/{r}-*`, `mg/{r}_*`, `impl/{r}-*`, `evidence/{r}`, `evidence/{r}-*` but **does not** recognize `sdlc/root/{id}`, even though that scheme is still produced by polyphony (`ResetCommands.Branches.cs:273-313` enumerates them). Branches journaled as polyphony-owned are caught via the expected-state projection regardless of `MatchesPolyphonyBranchPattern`; the hole affects only the **discovered-resource** path (orphan refs not in the journal, which is exactly where the pattern leg's enumeration was load-bearing).

There is no formal deprecation marker (`[Obsolete]`, telemetry, console warning) on the `pattern` strategy today. There is no automatic fallback from projection to pattern; if projection coverage is incomplete the executor returns an error unless the operator passes `--allow-unjournaled` (`ProjectionResetExecutor.cs:35-50`).

Separately, the journal store opens SQLite as `ReadWriteCreate` and initializes the schema on first use (`JournalStore.OpenConnectionAsync`). Any "diagnostic-only" surface that wants to read the journal currently has no read-only entry point — every reader can also create the file and mutate the schema.

## Decision

Make projection the single reset path. Specifically:

1. **Close the residual `sdlc/root` hole on the projection path.** Add `sdlc/root/{r}` and its descendants to `ResourceObserverSupport.MatchesPolyphonyBranchPattern` so the discovered-resource path in `JournalDriftAnalyzer.FoldDiscovered` catches orphan `sdlc/root/{id}` refs. Add test coverage that exercises the discovered-orphan branch path (currently only the expected-resource branch path is covered in `GitBranchObserver`-aware tests).

2. **Verify projection covers every resource kind the pattern legs touch.** Produce a comparison: for each pattern leg, identify what it acts on, then confirm that the corresponding `IResourceObserver` + `IResourceDeleter` pair (plus the discovered-resource fall-through) handles the same surface. Any gap blocks deletion. Expected gaps to surface: tag-clearing semantics from `ResetCommands.Facets.cs`, manifest-file removal semantics from `ResetCommands.Manifest.cs`, state-revert semantics from `ResetCommands.State.cs`.

3. **Delete the pattern strategy.** Remove the `--strategy pattern` option, the `ResetRootPatternCoreAsync` orchestration in `ResetCommands.Root.cs`, and the six per-leg partials in `ResetCommands.{Prs,Worktrees,Branches,Facets,Manifest,State}.cs`. Keep the strategy *parameter* surface for one release as an accepted-but-ignored value if needed for downstream caller compatibility (decided at deletion time based on caller survey).

4. **Split the journal store into an explicit read-only reader.** Introduce `IJournalReader` (or equivalent) that opens SQLite as `Mode=ReadOnly`, performs no schema init, and surfaces "journal missing" as a typed outcome rather than implicitly creating the database. Re-route diagnostic-only consumers (e.g. drift analysis on already-completed runs, status surfaces) to the reader. The existing read-write `IJournalStore` is unchanged for command-side use.

This is **deletion + sharpening**, not new architecture. The deepening already shipped via the projection path; what remained was the legacy escape hatch, the residual pattern-recognition hole, and the journal read-mode conflation.

## Consequences

- **Operators lose `--strategy pattern`.** Any run whose journal coverage is incomplete must use `--allow-unjournaled` on projection. The discovered-resource path (now extended with `sdlc/root/{id}`) provides the regex-driven fallback that pattern provided, but it runs under the projection executor's coverage gate and externally-mutated guard.
- **`ResetCommands` shrinks dramatically.** Six partial files disappear; `ResetCommands.Root.cs` becomes a thin delegator into `ProjectionResetExecutor`. `ResetCommandsBranchesEnumerationTests.cs` and `ResetCommandsWorktreesRetryTests.cs` need either deletion or migration to exercise the projection equivalents.
- **Single mental model for reset.** "What can a root own?" is answered in exactly one place (`CurrentExpectedState` + `IResourceObserver`s + `MatchesPolyphonyBranchPattern`). The `pattern` path's parallel answer is gone.
- **Read-only journal reader unlocks safer diagnostic surfaces.** Future inventory / status / close-out consumers can read the journal without risk of side-effecting the schema.
- **Risk: a hole in the pattern→projection equivalence verification (step 2) translates into silent regression after pattern deletion.** Mitigation: write the equivalence check as test code, not prose; gate deletion on green tests.

## Considered alternatives

1. **New `RunInventory` module with `owned`/`orphan`/`missing`/`disputed` verdict vocabulary, manifest as a second expectation source.** Rejected. The substrate already exists (`JournalDriftAnalyzer` emits `consistent`/`external_delete`/`external_mutation`/`external_create_polyphony_named`); the proposed verdicts were mostly renames. The `disputed` verdict required manifest-as-expectation-source, but the manifest carries run-level decisions (PGs, MGs, rebases, retired MGs), not per-resource ownership claims, so there is no real second source for it to disagree with the journal about. The original draft is captured in git history and `gap-analysis.md` for traceability.

2. **Deprecate `pattern` with `[Obsolete]` + console warning, schedule deletion in a later release.** Rejected. The deprecation window adds carrying cost (two-path mental model, two test suites, two failure modes) without buying meaningful migration time: the only caller of pattern is the operator-issued reset command, and the alternative (`projection` + `--allow-unjournaled`) is already available.

3. **Add manifest as a cross-check expectation source for the existing analyzer.** Rejected for this ADR; not justified by the bug evidence. Manifest-vs-journal divergence is a real failure mode in principle, but it has produced zero recorded incidents and modeling it would require a per-kind manifest→resource projection that the current manifest shape (PG/MG-centric) doesn't support cleanly. Reopen if a real incident surfaces.

4. **Auto-fallback from projection to pattern on coverage failure.** Rejected. Today's behavior (`projection` errors out unless `--allow-unjournaled` is set) is explicit and operator-controllable; an auto-fallback would hide journal-coverage gaps from the operator at the exact moment they need to be visible.

5. **Keep `pattern` as a permanent supported strategy alongside projection.** Rejected. Two paths means two enumeration disciplines means the AB#3245/3246 class can recur. The whole point of the projection design was to make the bug class structurally impossible; keeping pattern around defeats the point.

## Relationship to other ADRs

- **Refines, does not supersede, [`run-reset.md`](run-reset.md)** (accepted, PR 1 of 3 of the reset effort). That ADR introduced the per-root watermark + observer filter + proactive cleanup; this ADR closes out the projection-vs-pattern bifurcation that emerged after it.
- **Continues [`run-epoch-and-reset.md`](run-epoch-and-reset.md)** (draft). The run-epoch + first-class reset verb work assumes a single reset path; deleting `pattern` removes a branch from its design surface.

## Migration

Four chunks, scoped as the work items beneath this ADR's parent Issue:

1. Close residual `sdlc/root` discovered-branch hole + tests.
2. Build the pattern→projection equivalence test suite; surface any gaps as blockers.
3. Delete `pattern` strategy + six legacy `ResetCommands.*.cs` partials; remove or update affected tests.
4. Introduce `IJournalReader` (read-only mode); migrate diagnostic-only consumers.

Chunks 1 and 4 are independent of each other and of chunk 3. Chunk 2 gates chunk 3. Order: 1 + 4 in parallel, then 2, then 3.
