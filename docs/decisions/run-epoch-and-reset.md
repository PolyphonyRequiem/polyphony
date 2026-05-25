# ADR: Run epoch + first-class reset for root re-dispatch

> **Status:** Proposed — author signoff sought (Daniel) before PR 1 lands.
> **Companions:** `branch-model.md` Rev 4 (canonical branch names),
> `per-run-worktree-model.md` (worktree lifecycle), the
> `polyphony-dogfood-recovery` skill (manual ceremony this ADR retires),
> and the session design doc `abort-as-first-class-design.md` (the
> minimal proposal this ADR re-scopes after rubber-duck review).

## TL;DR

Add a **per-root run epoch** (tag `polyphony:run-epoch=N` on the root
root, default/absent = 1) that **threads through every branch-name
derivation in the codebase**, plus a **`polyphony reset` verb family**
+ a **`reset-root.yaml` workflow** + a **`-Intent reset` launcher mode**,
so a root can be cleanly re-dispatched after a botched or scrapped run
without the operator hand-running the 409-line recovery skill.

The epoch is the only mechanism that flips polyphony's PR-record-based
satisfaction observations, because ADO/GitHub completed-PR records are
permanent. Without epoch threading, `polyphony reset state` does not
unblock re-dispatch — the original session design doc undercounted this.

## Context

### The wedged-redo problem

Polyphony's SDLC observers (`PlanObserver.cs` under
`src/Polyphony/Sdlc/Observers/`) determine each work item's requirement
disposition by querying ADO/GitHub PRs filtered by the **canonical head
branch name** for that requirement:

| Requirement | Branch queried |
|---|---|
| `plan_authored` / `plan_reviewed` / `plan_promoted` | `plan/{root}` or `plan/{root}-{item}` |
| `implementation_merged` | `impl/{root}-{item}` |

Completed PR records in ADO are **permanent**. They cannot be deleted
via the REST API; their `Status=completed` cannot be reverted to
`active` or `abandoned`. Deleting the source branch does not delete or
hide the PR record. The observer therefore correctly reports
`Disposition.Satisfied` forever once a PR for the canonical branch
merges.

This is **correct semantics** for monotonic forward progress. It is
**broken semantics** when the operator wants to scrap a botched run and
re-do the root from scratch — there is no mechanism to declare "the
prior PRs no longer count for this work item".

Concrete repro: root **62286666** ("Document polyphony usage for
cloudvault-service-api") ran through v2.4.6. Plan PR #15605689 merged
into `feature/62286666`. Impl PR #15606101 merged into the MG branch.
The feature PR creator then failed with a 4000-char ADO description
error. We shipped fixes in v2.4.8 (strip step + work_item_id wiring)
and want to redo the run. But:

```
$ polyphony state next-ready --work-item 62286666
{
  "status": "satisfied",
  "observation_reasons": {
    "plan_authored":         "plan PR #15605689 merged",
    "plan_promoted":         "plan PR #15605689 merged",
    "implementation_merged": "impl PR #15606101 merged"
  }
}
```

So polyphony refuses to dispatch — there is nothing left to do.
Stripping `polyphony:planned` and `polyphony:impl-merged-in-mg=…` tags
(the only existing teardown-shaped verb) does **not** flip the verdict.

### Why the prior design doc undercounted

The session design doc `abort-as-first-class-design.md` proposed five
`polyphony reset` verbs (root/prs/branches/state/worktrees) with a 1–2
dev-day cost estimate. It implicitly assumed `reset state` would do
the satisfaction-flip work via `twig state` rollback. Rubber-duck
review (`reset-design-critique` agent, 2026-05-17) flagged two
structural omissions:

1. **`reset state` alone is insufficient.** `twig state` rollback does
   not affect observers — observations are tied to PR records, not
   work-item state.
2. **Observation override mask is broken** as a simple boolean —
   blocks future re-satisfaction, has to be an epoch/floor (which
   collapses into the present proposal).

The correct fix is an **epoch tag** that participates in branch-name
derivation everywhere, so the new run's PRs target different branch
names (`plan/{root}-r2`, `impl/{root}-{item}-r2`) — old PRs become
invisible to observers without us touching ADO records.

### The recovery skill

`polyphony-dogfood-recovery/SKILL.md` is a 409-line runbook covering
the manual ceremony for the wedged-redo case: halt conductor, abandon
PRs, delete branches (with bot-user switch), reset twig state, remove
worktrees, smoke-test. It works but is fragile, environmental, and
explicitly out of reach of any automation. Collapsing it into a
`polyphony reset root` composite + a one-line launcher invocation is
the user-visible deliverable here. (Sections § 1, § 6, § 8, § 9 of the
skill are durable wisdom that survives — operator judgment, env
gotchas, re-launch deferral, incident history — but § 2–§ 5, § 7 all
collapse into verb behavior.)

## Decision

### Run epoch

Stamp a tag on the root:

```
polyphony:run-epoch=N
```

where `N` is a positive integer. **Absent or `=1` ⇒ legacy
behavior**: branch names follow the existing Rev 4 grammar from
`branch-model.md`. `N > 1` ⇒ every branch this epoch creates carries a
`-r{N}` suffix:

| Branch kind | Epoch 1 (legacy) | Epoch 2+ |
|---|---|---|
| Feature trunk | `feature/{root}` | `feature/{root}-r{N}` |
| Root plan | `plan/{root}` | `plan/{root}-r{N}` |
| Descendant plan | `plan/{root}-{item}` | `plan/{root}-{item}-r{N}` |
| Merge group | `mg/{root}_{path}` | `mg/{root}_{path}-r{N}` |
| Impl | `impl/{root}-{item}` | `impl/{root}-{item}-r{N}` |
| Evidence | `evidence/{root}-{item}` | `evidence/{root}-{item}-r{N}` |
| Evidence orphan | `evidence/{item}` | `evidence/{item}-r{N}` |

The epoch is **per-root, not per-item**. All items in the same root
share the root's epoch. (Per-item epoch was considered and
rejected — see § Alternatives.)

The epoch tag is **authoritative on the root work item** in ADO,
so it survives:
- Cache wipes
- Re-clones
- Multi-operator collaboration (one machine bumps; the other sees the
  bump after `twig sync`)

### `RunEpoch` type + `BranchNamingContext`

A new value-object `RunEpoch` (positive int, default 1, parsed/printed
as a bare integer in the tag value). All `BranchNameBuilder` static
methods gain a `RunEpoch` parameter. A new
`BranchNamingContext(RootId root, RunEpoch epoch)` carries the
combination so call sites don't have to thread two parameters
everywhere — pass one context, derive any branch name from it.

```csharp
public readonly record struct RunEpoch(int Value)
{
    public static RunEpoch Legacy => new(1);
    public bool IsLegacy => Value == 1;
    public string Suffix => IsLegacy ? "" : $"-r{Value}";
    public static bool TryParse(string raw, out RunEpoch epoch) { … }
}

public readonly record struct BranchNamingContext(RootId Root, RunEpoch Epoch);

internal static class BranchNameBuilder
{
    public static BranchName Feature(BranchNamingContext ctx) =>
        BranchName.CreateUnsafe($"feature/{ctx.Root.Value}{ctx.Epoch.Suffix}");
    // … etc
}
```

`BranchNameParser` accepts both legacy and epoch-suffixed forms —
crucial because operators may have epoch-1 branches lingering from
before this ADR landed, and the parser must classify them correctly.

### Epoch resolution at call sites

Every call site that today calls `BranchNameBuilder.Foo(root, …)`
becomes `BranchNameBuilder.Foo(ctx, …)`. The `ctx` is resolved from
the root tag via a new helper:

```csharp
public static async Task<BranchNamingContext> ResolveAsync(
    ITwig twig, RootId root, CancellationToken ct)
```

The helper reads the root's tags, parses the epoch (defaulting
to `Legacy`), and caches per-invocation (epoch does not change
mid-invocation; consumers can call freely).

Call-site inventory (verified by grep, 2026-05-17):
- `BranchNameBuilder.*` in `Branching/BranchNameBuilder.cs`
- `BranchCommands.{Ensure*,AssertOnImpl,NextImpl,LoadTree}` (10 sites)
- `PrCommands.{Open*,Merge*,CreateFeatureAdo,AssertImplPrCoverage}` (12 sites)
- `PlanCommands.{DetectState,Rebase*,Recreate*,ClassifyStaleDescendants}` (8 sites)
- `MergeGroupCommands.cs` (1 site)
- `PlanObserver.{ResolvePlanBranch,ResolveImplBranch}` (2 sites)
- Estimated total: **~33 call sites + BranchNameBuilder itself + parser**.

### Reset verb family

> **Superseded (AB#3308, 2026-05-24):** The sub-verb surface enumerated
> below was retired in
> [Pattern strategy retirement](pattern-strategy-retirement.md). The
> projection-based reset shipped in AB#3253 is now the only path, and
> the only public verb is `polyphony reset root` (no `--strategy` flag,
> no per-axis sub-verbs). The historical surface is preserved below for
> context; treat it as accurate-for-its-time, not current.

Six new CLI verbs under `polyphony reset`:

```
polyphony reset root      --root-id N --to-stage planning|implementation [--execute]
polyphony reset prs       --root-id N [--execute]
polyphony reset branches  --root-id N [--keep feature] [--execute]
polyphony reset worktrees --root-id N [--execute]
polyphony reset manifest  --root-id N [--archive|--delete] [--execute]
polyphony reset state     --root-id N --to-stage planning|implementation [--execute]
```

**Safety contract.** All verbs default to **dry-run**. `--execute`
required to actually mutate state. Mirrors `terraform plan` /
`terraform apply` — the destructive verb name alone is not enough.

**`reset root` composite** invokes the other five in the
rubber-duck-corrected order (the original design doc had branches
before worktrees, which fails because git refuses to delete branches
checked out in any worktree):

1. `reset prs` — abandon active PRs for this root (completed PRs left
   alone; reported as "epoch-irrelevant" in output).
2. `reset worktrees` — remove all worktrees under
   `<repo>-runs/root-{N}/`. Per-run-worktree-model ADR
   already gives us idempotent `worktree gc`; this verb wraps it
   with the root filter.
3. `reset branches` — delete local + remote refs matching
   `(plan|impl|mg|evidence|feature)/{root}(-{anything})?` (i.e. all
   epochs ≤ current). `--keep feature` preserves the feature trunk
   when the operator intends to layer a new epoch on top of existing
   merged work (rare; use case is e.g. "the integration is good, I
   just want to re-do one descendant plan branch" — but per the
   per-item-epoch alternative rejection below, this is currently a
   non-goal; the flag is there for future expansion).
4. `reset manifest` — archive `<git-common-dir>/polyphony/{root}/run.yaml`
   to `run-r{prev-epoch}.yaml.archived`, then create a fresh empty
   manifest. (Operator can grep archives for postmortem.)
5. `reset state` — twig-state rollback (`twig state {target}` driven
   by `--to-stage`); clear non-permanent tags
   (`polyphony:planned`, `polyphony:impl-merged-in-mg=*`); keep
   `polyphony:root` and `polyphony:facets=*`; finally **bump
   `polyphony:run-epoch` to N+1**.

The epoch bump is the **last** step. Once bumped, observations flip
within one `twig sync` cycle, and the next launcher invocation will
treat the root as fresh.

**Shared framework.** A new `ResetPlan` type carries the inventory of
intended mutations per verb. Each verb's result envelope (per
`MarkImplMerged` contract — always exit 0, JSON, idempotent,
`already_in_desired_state` flag) extends with per-target operation
arrays:

```csharp
public sealed record ResetPrsResult
{
    public required int RootId { get; init; }
    public required RunEpoch Epoch { get; init; }
    public required bool DryRun { get; init; }
    public required IReadOnlyList<PrAbandonOutcome> ActiveAbandoned { get; init; }
    public required IReadOnlyList<PrAbandonOutcome> AlreadyAbandoned { get; init; }
    public required IReadOnlyList<PrSummary> CompletedImmutable { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }
}
```

Partial-failure semantics: if `reset prs` abandons 4 of 6 active PRs
then network-errors on the 5th, the result envelope shows 4 success +
1 failure + 1 not-attempted; the verb returns `Success=false` so the
composite halts; operator re-runs (idempotent — already-abandoned PRs
are no-ops).

### Workflow `reset-root.yaml`

A new sub-workflow under `.conductor/registry/workflows/reset-root.yaml`
that:

1. Runs `polyphony reset root --dry-run` to inventory.
2. Presents a `human_gate` showing the inventory; options: `confirm`,
   `abort`.
3. On confirm, runs `polyphony reset root --execute`.
4. On any verb failure, surfaces a `human_gate` (`retry`, `abort`,
   `proceed_anyway` — last is reserved for partial-failure cases where
   the operator has manually cleaned up the failed piece).
5. On full success, runs `polyphony state next-ready --work-item N` as
   a smoke test; asserts `status != satisfied`. Surfaces another gate
   if the assertion fails (means epoch threading missed a call site
   — that's a bug to file).
6. Terminal: `$end`.

**Not wired into any in-workflow gate's `Abort` option.** Per the
design doc § 6.2 analysis, gate-time teardown introduces abort-during-
abort races and partial-teardown-worse-than-no-teardown failure modes.
Reset is a deliberate, out-of-band operator action via the launcher.

### Launcher mode

`Invoke-PolyphonySdlc.ps1 -RootId N -Intent reset -ToStage planning|implementation`
shells out to `conductor run reset-root@polyphony --input root_id=N --input to_stage=…`.

Refuses to run if:
- The same-root run lock is held by another conductor (operator must
  abort the prior run first via `/api/stop`).
- The root tag doesn't exist (`polyphony:root` absent — wrong
  work item).

## Open questions (require sign-off)

These are the **5 design questions** flagged in plan.md, with a
recommended answer for each. Edit/comment before PR 1 lands.

### Q1: Root-only epoch, or per-item?

**Recommended:** Root-only.

**Why:** Per-item epoch (e.g. bump epoch on one descendant work item
without touching siblings) would allow surgical re-do of one subtree.
But:
- Operators don't currently ask for this — wedged-redo is always
  full-root.
- Per-item epoch fragments branch identity: a parent at epoch 1, a
  child at epoch 2 would create `plan/{root}-{child}-r2` rebased on
  `plan/{root}` (legacy) — fine, but if the child has its own
  children, do they inherit the parent's epoch or their own? The
  combinatorics explode.
- Root-only matches the "one root = one logical run" model in
  `branch-model.md` and `per-run-worktree-model.md`.

If per-item ever becomes needed, the root-only design is forward-
compatible — add a per-item override tag later.

### Q2: Feature PR creation reuses completed PRs by head/base. How does epoch fit?

**Recommended:** The PR creator already looks up by `head=feature/{root}`.
After epoch bump, the new head is `feature/{root}-r2`, so the lookup
naturally misses the old completed PR. **No filter change needed in
the creator.**

The same applies to all `Open*Pr` verbs — they all look up by branch
name, and the branch name carries the epoch.

**Edge case:** the v2.4.4 fix changed `open-X-ado` to scan
status=All to find previously-merged PRs (AB#3227 fix). That logic is
still correct under epoch — the scan finds PRs for the *current
epoch's* branch name; old-epoch PRs target different branches and are
invisible. No regression.

### Q3: Manifest archival path

**Recommended:** Archive in place — rename `run.yaml` to
`run-r{prev_epoch}.yaml.archived`. Keep all archives.

**Alternatives considered:**
- Delete: loses postmortem evidence.
- Archive to a sibling `archive/` dir: extra directory complexity for
  no real win.
- Rewrite in place: same risk as delete; loses bookkeeping.

Archived manifests are operator-visible at
`<git-common-dir>/polyphony/{root}/run-r{N}.yaml.archived`.

### Q4: Active (not-yet-completed) PRs at reset time

**Recommended:** Auto-abandon during `reset prs` with no operator
prompt. Active PRs are by definition not-yet-merged work; the operator
has chosen reset, so they've committed to scrapping in-flight work.

Reported clearly in the `--dry-run` output so the operator can see
what's about to be abandoned before passing `--execute`.

**Future enhancement:** an `--abandon-policy=ask|all|none` flag if
operators ever want finer control. Not needed for MVP.

### Q5: `--execute` required vs `--dry-run` default

**Recommended:** `--execute` required. Verbs default to dry-run plan
output. This matches `terraform apply` / `kubectl apply --dry-run` —
the destructive action requires explicit opt-in even when invoked by a
"destructive-sounding" verb name. Reduces operator-finger risk.

`reset root --execute` propagates `--execute` to all five subordinate
verbs; the composite is the safety boundary.

## Alternatives considered

### A. Local cache override file

A `polyphony-state-override.json` under `<git-common-dir>/polyphony/`
that observers consult. **Rejected.** Machine-local — doesn't survive
re-clones, doesn't propagate to collaborators, doesn't appear in any
audit trail.

### B. Plain observation mask tag

`polyphony:observation-mask=plan_authored,implementation_merged` that
short-circuits observers to `Needed`. **Rejected.** Breaks future
satisfaction — the next plan PR for the same item also gets masked.
Would need to be a "mask PR IDs ≤ X" floor — which collapses into the
epoch design (epoch IS a floor).

### C. Per-item epoch instead of per-root

**Rejected.** See Q1 above.

### D. Delete completed PR records

**Not technically possible.** ADO PRs cannot be deleted; only
abandoned (which is itself irreversible). GitHub PRs cannot be
deleted either. The constraint is upstream.

### E. Rewrite git history to un-merge the impl PR

**Rejected.** Destructive to shared history; requires operator
coordination across collaborators; doesn't affect the ADO PR record
(which is the source-of-truth for observers anyway).

## Implementation plan

Three sequential PRs, each landable and shippable in isolation:

### PR 1 — Run epoch + epoch-aware branch identity

Scope:
- `RunEpoch`, `BranchNamingContext` types.
- `BranchNameBuilder` accepts `BranchNamingContext`.
- `BranchNameParser` accepts both legacy and epoch-suffixed forms.
- `BranchNamingContext.ResolveAsync(twig, root, ct)` helper.
- Thread `BranchNamingContext` through ~33 call sites.
- `PolyphonyTags.RunEpochPrefix` constant + `RunEpoch` round-trip.
- Tests: branch round-trip; parser accepts both forms; observer flips
  to `Needed` after `polyphony:run-epoch=2` is stamped.
- No new verbs; no workflow change. Epoch is dormant in this PR —
  callers default to `RunEpoch.Legacy` unless tag is present.

**Definition of done:** `polyphony state next-ready --work-item 62286666`
flips from `satisfied` → `needed` if a human stamps
`polyphony:run-epoch=2` on the work item manually.

### PR 2 — Reset verb family

Scope:
- `ResetPlan`, `Reset*Result` shared types.
- 6 verbs under `polyphony reset` namespace.
- `--dry-run` default; `--execute` opt-in.
- Idempotency + read-after-write per verb.
- Tests against canned ADO/GitHub responses (existing test patterns
  under `tests/Polyphony.Tests/Commands/`).

**Definition of done:** `polyphony reset root --root-id 62286666
--to-stage planning --dry-run` prints a complete and correct inventory
of intended mutations; `--execute` carries them out idempotently.

### PR 3 — Workflow + launcher mode + skill collapse

Scope:
- `.conductor/registry/workflows/reset-root.yaml`.
- `min_polyphony_version` bump (bundled-SemVer; all 14 workflows go
  together).
- `Invoke-PolyphonySdlc.ps1 -Intent reset -ToStage …`.
- Post-reset validation node in the workflow.
- Recovery skill collapse: rewrite to ~3 sections (env gotchas, state
  table for `--to-stage` enum docs, smoke-test pointing at
  `reset root --dry-run`).

**Definition of done:** `Invoke-PolyphonySdlc.ps1 -RootId 62286666
-Intent reset -ToStage planning` runs to completion; followup
`-Intent new` dispatches a fresh epoch-2 run; plan feedback collected
in the new PR.

## Risks

1. **~33 call sites is a lot of mechanical change.** Mitigation: a
   single `BranchNamingContext` parameter at every site keeps the
   diff regular; a lint can assert no `BranchNameBuilder` overload
   without `BranchNamingContext` survives.
2. **`twig sync` race after epoch bump.** Mitigation: read-after-write
   per the MarkImplMerged pattern; if the cache doesn't reflect the
   bump within N retries, surface a clear error.
3. **Manifest archive accumulation.** Mitigation: `polyphony reset
   manifest --archive` is opt-in via the root composite; archives
   live under `<git-common-dir>/polyphony/{root}/` and don't pollute
   the working tree.
4. **Backwards compatibility.** Mitigation: `RunEpoch.Legacy` = 1 =
   absent tag = current behavior. No migration required for existing
   in-flight roots; they continue to use legacy branch names
   indefinitely. Epoch only engages on explicit `reset root` invocation.

## What this ADR does NOT do

- Does not add gate-time abort+teardown (per design doc § 6.2 analysis).
- Does not address the twig `.twig/config` rewrite-on-every-invocation
  problem (Issue #2 from `polyphony-findings-2026-05-17.md`; punted to
  twig).
- Does not address the PowerShell transcript truncation issue
  (Issue #3; orthogonal).
- Does not address the `-Intent replan` silent hang (Issue #4;
  separate after PR 3).
- Does not retire the recovery skill entirely (sections § 1, § 6, § 8,
  § 9 remain — operator judgment, env gotchas, re-launch deferral,
  incident history).
