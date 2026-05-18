# Run-reset: per-apex run watermark + observer filter + proactive cleanup

**Status:** Accepted (PR 1 of 3)
**Date:** 2026-05-17

## Context

`polyphony state next-ready` decides whether an apex (and each item beneath
it) is `satisfied` by **observing** the world: it queries ADO/GitHub for the
latest PR on the canonical branch (`plan/{root}`, `impl/{root}-{item}`,
`feature/{root}`) and reduces the per-kind observations into a disposition.

This is correct for the first run. It fails on the **redo** path:

- The canonical branch name is a pure function of `(root, item)` — every
  rerun targets the **same** branch.
- PR records are permanent on both platforms. ADO PRs can be abandoned but
  not deleted; GitHub PRs can be closed but the record persists.
- When the observer queries by branch, it finds the **prior** run's merged
  PR and emits `Satisfied`.
- The driver refuses to dispatch a satisfied apex.

Concrete instance: apex 62286666 hit a 4000-char ADO description bug on
v2.4.6, we shipped fixes in v2.4.7+, we want to redo the run. The previous
plan PR #15605689 and impl PR #15606101 are both merged; the feature PR was
abandoned but the apex is still "Started" in ADO. Every redispatch attempt
short-circuits to `satisfied` because the observer can't tell "current run"
from "any prior run."

## Decision

Introduce a **per-apex run watermark** plus an **observer filter**.

### Watermark

Single tag on the apex root work item:

```
polyphony:run-started-at=<ISO-8601-UTC>
```

Stamped by `polyphony reset state` (PR 2). Re-stamped on every subsequent
reset. **Not** stamped on a fresh apex's first dispatch — fresh apexes
have no prior PRs to filter, so absent-tag = no-filter is the correct
no-op semantics.

Format: `yyyy-MM-ddTHH:mm:ss.fffZ` (UTC, millisecond precision). The
reader (`PolyphonyTags.ReadRunStartedAt`) accepts any ISO-8601 form
`DateTimeOffset.TryParse` recognises, and — defensively against
duplicate tags from reset bugs or operator edits — scans **all** tags
with the prefix and returns the **maximum** parseable value.

### Observer filter

In `PlanObserver`'s `Map*` functions, when the watermark is present and the
PR's `MergedAt` is at or before the watermark, the merged PR is treated as
a **prior-run artifact** and the observation degrades to `Needed` with a
diagnostic reason naming both timestamps.

- Only `MERGED` PRs are filtered. Open PRs created before the watermark
  are reset's responsibility to abandon (PR 2); if one survives, that's a
  reset bug, not an observation bug.
- Absent watermark = no filter = legacy behavior. No migration burden for
  apexes that haven't been reset.
- The `Observe*Async` wrappers do **not** apply the watermark — they're
  raw poll-result formatters used by tests and ad-hoc tooling. The
  watermark is applied only at composer entry points
  (`StateCommands.NextReady` and `PlanCommands.DetectState`) where the
  scope-level fetch can populate it.

### Fail-closed fetch posture

The watermark fetch (`PlanObserver.ReadRunStartedAtAsync`) throws
`InvalidOperationException` when `twig show` returns null — the only
way that happens is a twig-process failure, since the apex root is
known to exist by the time we get here. The composers capture the
exception into `NextReadyObservationScope.RunStartedAtFetchError` and
**force all four plan-state composers to `Needed`** with a fetch-error
reason. This is intentionally conservative: a transient twig failure
must not be silently interpreted as "no watermark" — that would let
prior-run PRs slip back into the `Satisfied` path until the next call
succeeds.

`PlanCommands.DetectState` adopts the same posture in its MERGED
branch: a fetch error emits a `DetectStateError` envelope; a prior-run
merged PR emits `state: "not_started"` (symmetric to the existing
"closed PR + branch deleted" short-circuit on line 242).

### Proactive branch cleanup (PR 3)

To keep the branch tree clean across runs, flip the default `delete-branch`
behavior to `true` for merge-group and plan merges. Impl + evidence
already auto-delete. Feature branch stays `delete-branch=false` because the
run manifest lives at `feature/{root}:.polyphony/run.yaml`.

## Consequences

### Positive

- **Reset becomes structurally complete.** After PR 2's `reset state`
  stamps a fresh `run-started-at`, every observer query for the apex flips
  to `Needed` for the prior PRs — the driver can re-dispatch cleanly.
- **No migration burden.** Existing apexes work as before until they're
  explicitly reset; the absent-tag path preserves all current semantics.
- **Small surface area.** ~5 files touched in PR 1 (tag constants,
  observer, scope, verb composer, tests). Compare ~50-file
  "epoch-in-branch-names" alternative.
- **Cross-platform.** Works identically on GitHub (`gh pr view --json
  mergedAt`) and ADO (`ClosedDate` for completed PRs, already plumbed
  through `AdoClient.cs:1404` and `GhPullRequestPollAdapter.cs:66`).

### Negative

- **Clock skew is theoretically possible**, but reset uses
  `DateTimeOffset.UtcNow` and the PR merge timestamps come from
  ADO/GitHub server clocks. Both are reliable enough for second-resolution
  comparison.
- **No mid-run cancellation semantics.** If reset is stamped at T and a
  long-running merge completes at T+ε after the stamp, the merge counts
  for the current run. This is the correct behavior: reset's point is
  "from now on, only PRs newer than NOW satisfy the run."
- **Observer must read one extra tag per scope.** `BuildObservationScopeAsync`
  now issues a single `twig show <rootId>` to load the watermark, even
  for apexes that have never been reset. The cost is negligible and the
  fetch is already coalesced (the same `twig show` is reused inside the
  scope).

### Neutral

- **Open PR filtering is deferred.** `PullRequestSummary` doesn't carry
  `CreatedAt` today; adding it would let us filter open PRs too. The
  current design relies on reset abandoning open PRs explicitly. If that
  proves leaky in practice, add `CreatedAt` + open-PR filter as a
  follow-up.

## Considered alternatives

### A. Epoch suffix in branch names (`plan/{root}-r2`)

Threaded a `RunEpoch` / `BranchNamingContext` through `BranchNameBuilder`,
`BranchNameParser`, every `Ensure*` verb, every `Open*Pr` verb, every
`Merge*` verb, and both observers. Estimated 50+ file touches. Discarded
because:

1. The work in the intermediate PRs is **disposable** — only the feature
   PR reaches main, and even there the feature branch is keyed on root
   (no epoch needed because the previous feature PR was abandoned).
2. The actual problem is purely the **observation** scheme. Renaming
   branches solves it indirectly; filtering observations solves it
   directly with O(1) call-site changes.

### B. Observation mask (force `Needed` for all kinds during a "redo" window)

Rejected because the mask has no natural end: it would need to track
"is this kind currently being satisfied by an in-flight new PR?" which
collapses into the same comparison the watermark gives us for free, but
with worse provenance (a flag vs. a timestamp).

### C. Generation counter (`polyphony:run-generation=N`)

Equivalent expressive power to the watermark but requires labeling **each**
PR with its generation. The watermark uses `PR.MergedAt` which is already
populated by the server — no per-PR write needed.

## Implementation notes (PR 1 scope)

**Files touched:**

- `src/Polyphony/Tagging/PolyphonyTags.cs` — added
  `RunStartedAtPrefix`, `RunStartedAt(DateTimeOffset)` (ms precision),
  `ReadRunStartedAt(TagSet)` (multi-match → max).
- `src/Polyphony/Sdlc/Observers/PlanObserver.cs` — added
  `ReadRunStartedAtAsync(rootId)` (fail-closed on twig failure); added
  optional `mergedAt` + `runStartedAtFilter` params to four `Map*`
  functions; added two private helpers (`IsPriorRunMergedPr`,
  `FormatPriorRunReason`).
- `src/Polyphony/Commands/NextReadyObservationScope.cs` — added
  `RunStartedAt` and `RunStartedAtFetchError` fields.
- `src/Polyphony/Commands/StateCommands.NextReady.cs` — added
  `FetchRunStartedAtAsync` helper (captures fetch errors); called it
  inside `BuildObservationScopeAsync`; threaded `RunStartedAt` through
  four `Compose*` methods with fail-closed error check.
- `src/Polyphony/Commands/PlanCommands.DetectState.cs` — added
  watermark fetch + prior-run filter in the MERGED branch (emits
  `state: "not_started"` for prior-run PRs).
- `tests/Polyphony.Tests/Tagging/PolyphonyTagsRunStartedAtTests.cs` —
  formatter + reader round-trip, ms-precision tests, multi-match
  reader tests, malformed-value posture, multi-form ISO-8601
  normalisation.
- `tests/Polyphony.Tests/Sdlc/Observers/PlanObserverTests.cs` — three
  `ReadRunStartedAtAsync` tests (including
  `_TwigShowFails_PropagatesException`) + eight `Map*` filter tests.
- `tests/Polyphony.Tests/Commands/PlanCommandsDetectStateTests.cs` —
  `StubRootWatermarkAbsent` helper called from all 9 MERGED-branch
  tests; new tests for prior-run filter + fetch-error posture.
- `tests/Polyphony.Tests/Commands/StateNextReadyPlanIntegrationTests.cs` —
  `StubApexWatermarkAbsent` helper folded into `NewRunnerWithRemote()`
  so every test gets the absent-watermark default.

**Out of scope for PR 1, planned for PR 2:**

- `polyphony reset state` verb (the only writer of the watermark).
- Full reset verb family (`reset prs`, `reset worktrees`, `reset branches`,
  `reset manifest`, `reset state`, composite `reset apex`).

**Out of scope for PR 1, planned for PR 3:**

- `reset-apex.yaml` workflow.
- `Invoke-PolyphonySdlc.ps1 -Intent reset -ToStage planning` launcher
  mode.
- Default `--delete-branch true` for merge-group and plan PR merges.
- Recovery skill collapse.


## PR 2 implementation notes — reset verb family

Ships six verbs under the `polyphony reset` verb group:

| Verb                  | Role                                                     |
| --------------------- | -------------------------------------------------------- |
| `reset state`         | **Sole** writer of the `polyphony:run-started-at=*` tag. |
| `reset prs`           | Abandon every open PR in the apex's polyphony scope.     |
| `reset branches`      | Delete every apex-scoped branch on origin and locally.   |
| `reset worktrees`     | Remove every git worktree under `{runs_root}/apex-{N}/`. |
| `reset manifest`      | Read-only inspection (clearing deferred — see below).    |
| `reset apex`          | Composite: `prs → worktrees → branches → manifest → state`. |

### Convention divergence: dry-run by default, `--execute` opt-in

Every reset verb defaults to **dry-run**; the operator must pass
`--execute` to mutate state. This diverges from the existing
`WorktreeCommands.InitApex` convention (`--dry-run` opt-in). The reset
family is failsafe because the per-verb actions are destructive across
multiple boundaries simultaneously (ADO PRs, git branches, filesystem
worktrees) and partial damage is hard to undo. Rubber-duck flagged this
as a design risk in flags 6/7 of the PR 1 critique; defaulting to
dry-run lets operators preview the chain's full impact in a single
envelope before committing.

### Composite ordering rationale

`prs → worktrees → branches → manifest → state`:

1. **prs first** — close open PRs before deleting their branches so the
   platform's "branch deleted" notice never races with the abandon
   action; the resulting audit log reads PR-by-PR rather than
   intermingled branch/PR events.
2. **worktrees before branches** — git refuses to delete a checked-out
   branch. Tearing down worktrees first guarantees no local branch is
   pinned.
3. **manifest after branches** — the manifest lives on `feature/{N}`,
   so deleting that branch clears the manifest as a side-effect. The
   manifest verb is read-only and runs purely as an inspection step.
4. **state last** — the watermark stamp is the signal that the
   observers consult for "is this run current?". Advancing it on top of
   a half-done cleanup would leak ghost satisfaction signals past the
   new watermark. Halt-on-step-failure means a crash anywhere upstream
   leaves the system "still mid-reset" rather than "watermark advanced
   but ghosts remain."

### Manifest is read-only in PR 2

`reset manifest` inspects `origin/feature/{N}:.polyphony/run.yaml` and
emits the would-be state alongside a `DeferralReason` field. The actual
clearing happens implicitly via `reset branches` (which deletes
`feature/{N}`). The verb is wired here so a future partial-reset mode
that preserves `feature/{N}` while clearing the manifest in-place has a
natural home — and so the workflow surface in PR 3 can route on the
manifest's observed state without a separate primitive.

### Known refactor opportunity: `BranchSide` flags enum + capture-and-reparse

Two implementation patterns are deliberately non-obvious:

- `ResetCommands.Branches.cs` uses a private `[Flags] enum BranchSide`
  to track whether a discovered branch lives on origin, locally, or
  both. This lets a single per-branch loop emit the right delete
  command on each side without doubling up the enumeration.
- `ResetCommands.Apex.cs` uses a `Console.SetOut` **capture-and-reparse**
  pattern to call each public sub-verb from the composite. The verbs
  emit their own JSON envelope as the last step, so the composite swaps
  in a `StringWriter`, invokes the verb, and parses its output back
  into a typed record. The wrapping is narrow (per-verb) so it nests
  cleanly inside the outer `Program.cs` capture used by ConsoleAppFramework.

The capture-and-reparse is the correct contract today (composite sees
exactly what an operator would see standalone) but is a known
opportunity to refactor toward thin `[Command]` wrappers around
internal `*Async` methods that return the result record directly.
Tracked as future work; not blocking PR 2.

### Files touched

- `src/Polyphony/Commands/ResetCommands*.cs` — 7 partial files (shell + 6 verbs)
- `src/Polyphony/Models/Reset*Result.cs` — 6 result-type files
- `src/Polyphony/PolyphonyJsonContext.cs` — 13 new `[JsonSerializable]` entries
- `src/Polyphony/Program.cs` — `app.Add<ResetCommands>("reset")` + `knownVerbRoots`
- `src/Polyphony/Infrastructure/Processes/IGitClient.cs` + `GitClient.cs` —
  added `ListLocalBranchesAsync(pattern)` (via `git for-each-ref`) and
  `DeleteLocalBranchAsync(branch, force)` (via `git branch -D/-d`)
- `tests/Polyphony.Tests/Commands/ResetCommandsTests.cs` — round-trip
  coverage for each verb's halt path, dry-run path, and the composite
  step ordering
- `tests/Polyphony.Tests/Locking/LockCommandsTests.cs` +
  `tests/Polyphony.Tests/Infrastructure/Paths/PolyphonyStatePathsTests.cs` —
  stub updates for the two new `IGitClient` methods
