---
title: "Polyphony Self-Contained Orchestration — Scripts → Verbs + Policy Layer"
type: user-plan
status: draft
intended_consumer: polyphony architect agent (plan-level.yaml) via user_plan_path
---

# Polyphony Self-Contained Orchestration — User Plan

> This is a **user-authored reference plan** for the architect agent to refine.
> It encodes the design intent, scope boundaries, and open design questions
> already considered. The architect should preserve the design decisions below
> unless they conflict with type constraints, in which case raise as open
> questions per the standard architect contract.

## Problem

The polyphony SDLC pipeline lives across two stores of workflow logic:

1. **`polyphony/scripts/` (15 PowerShell scripts)** — referenced from workflow
   YAMLs as `scripts/foo.ps1`, which conductor resolves relative to the
   consumer's CWD (the worktree). This works only because polyphony self-hosts:
   the registry is the same git repo as the consumer's checkout. For any other
   repo to consume polyphony as a github registry, it would have to copy the
   scripts in — re-introducing the cross-repo drift problem we just solved by
   co-locating the registry.

2. **`.conductor/registry/scripts/` (2 helpers — `review-router.ps1`,
   `seeder.ps1`)** — referenced as `{{ workflow.dir }}/../scripts/*.ps1`.
   Conductor's github fetcher only grabs siblings of the workflow YAML; these
   aren't fetched, so they would silently break in any github-registry
   consumption.

A second problem rides along: configurable behavior (revision cap=5, PR fix
loops=10, remediation cycles=3, gate suppression policies, per-PG concurrency)
is hardcoded inside scripts and YAML literals. There is no way for a consumer
to set policy at the repo level — every consumer gets the same behavior.

## Goal

Two outcomes, delivered together:

- **A) Polyphony as a true github-style registry.** A consumer repo can
  `conductor registry add polyphony --source github://PolyphonyRequiem/polyphony`,
  install the `polyphony` binary, and run `polyphony-full@polyphony` end-to-end
  without copying any PowerShell into its own repo. All workflow logic lives
  in the binary.

- **B) Per-consumer policy.** A consumer repo's `.conductor/policy.yaml`
  controls plan-approval behavior, PR review/merge behavior, and concurrency —
  scoped by `root` (the root work item) / `by_type` / `defaults`, with three
  semantically clear modes (`auto` / `manual` / `warning`).

After this work: polyphony stops being self-hosted-only, and the human-in-loop
contract becomes a first-class config layer instead of hardcoded magic numbers.

## Approach

Two parallel tracks delivered as 8 phases:

**Track A — Scripts → polyphony binary verbs**, grouped by lifecycle:

| Lifecycle group | Scripts to migrate |
|---|---|
| `polyphony state <verb>` | preflight-check, preflight-lite, detect-state |
| `polyphony plan <verb>` | depth-guard, child-router, load-type-context, load-agent-guidance, review-router, seeder |
| `polyphony branch <verb>` | load-work-tree, pg-router, impl-router, dependency-check, scope-closer |
| `polyphony pr <verb>` | feature-pr-creator (invoke-gh and resolve-gh-token collapse to internal C# helpers, NOT public verbs) |

**Hard cutover** — no `.ps1` shims; scripts are deleted at end of Phase 6.
`bootstrap-conductor.ps1` is the sole exception (deferred per scope).

Existing top-level verbs (`route`, `validate`, `validate-config`, `hierarchy`)
may move into the `state` group or stay top-level — architect decides in
Phase 0 (see Open Questions).

**Track B — `.conductor/policy.yaml` config layer**, three resolution scopes
(most-specific wins) and three modes:

| Scope | Applies when |
|---|---|
| `root` | This is the root work item (passed via `--input work_item_id`). Recursive sub-trees do NOT re-scope as root. |
| `by_type.<Type>` | This work item's type matches a per-type override |
| `defaults` | Fallback for everything else |

| Mode | Approval gate | PR merge |
|---|---|---|
| `auto` | Approve when quality met OR retry cap reached. **No human ever.** | Merge clean reviews; merge anyway when fix-loop cap hit. No human ever. |
| `manual` | Always require human gate (current default). | Always require human merge. |
| `warning` | Auto-approve clean reviews; gate human only when `forced_by_cap=true` (cap reached without quality). | Merge clean reviews; gate human only when fixer exhausts retries without reviewer satisfaction. |

Schema sketch:

```yaml
# .conductor/policy.yaml
approvals:
  defaults:
    mode: warning
    quality_threshold:
      avg_score_at_least: 90
      blocking_count_at_most: 0
    max_revision_cycles: 5
  root:
    mode: manual
  by_type:
    Task:  { mode: auto }
    Issue: { mode: warning }

pr:
  defaults:
    mode: warning
    max_fix_loops: 10
  root:
    mode: manual
    max_remediation_cycles: 3
  by_type:
    Task:  { mode: auto }
    Issue: { mode: warning, max_fix_loops: 5 }

concurrency:
  max_concurrent_children: 3
  max_concurrent_pgs: 3
```

Provider axis (github vs ado) deferred until ADO PR sub-workflow is real.

## Steps

The architect should refine these into ordered PGs. Phases 2–5 can run in
parallel after Phase 1; Phase 6 is the integration choke point. Phase 7
(policy) is independent of Phase 6 once Phase 1 lands.

### Phase 0 — Design + contracts

- Finalize verb names per the lifecycle-group mapping above. Decide: do the
  existing top-level verbs (`route`, `validate`, `validate-config`,
  `hierarchy`) migrate into `state`, or stay top-level?
- Define the JSON output contract preservation pattern: each verb that
  replaces a script must emit byte-equivalent JSON. Pattern: golden-sample
  fixtures in `JsonOutputContractTests` per the `polyphony-cli-developer`
  skill.
- Define the test-fixture mocking strategy for twig and gh shell-outs
  (`IProcessRunner` interface vs test-mode env var vs CLI mock binary on PATH).
- Decide per-verb CLI surface (flags, positional args, stdin/stdout shape).
- One-page schema sketch for `policy.yaml` (full schema in Phase 7).

**Acceptance:** design doc lands; verb names locked; one ADR or note in
`docs/decisions/` recording the lifecycle-group naming choice.

### Phase 1 — Proof-of-pattern verbs

- Migrate `depth-guard.ps1` → `polyphony plan depth-guard`
- Migrate `child-router.ps1` → `polyphony plan next-child`
- Establishes: ConsoleAppFramework command pattern, JSON contract test
  pattern, MSTest fixtures (replacing Pester for these), workflow YAML
  reference syntax (`polyphony plan depth-guard ...` instead of
  `pwsh -File scripts/depth-guard.ps1 ...`).
- Smallest scripts (~1–2 KB), zero external CLI coupling — lowest blast
  radius. Validates Phase 0 design.

**Acceptance:** both verbs ship with golden-sample contract tests passing;
workflow YAML refs updated; one full SDLC re-run uses verbs in place of
those two scripts end-to-end.

### Phase 2 — State + plan-context verbs

- Migrate `preflight-check.ps1` → `polyphony state preflight`
- Migrate `preflight-lite.ps1` → `polyphony state preflight --lite`
  (or `polyphony state preflight-lite` — architect picks)
- Migrate `detect-state.ps1` → `polyphony state detect`
- Migrate `load-agent-guidance.ps1` → `polyphony plan load-guidance`
- Migrate `load-type-context.ps1` → `polyphony plan load-type`
- These already shell out to polyphony — inlining eliminates a process
  boundary per call.

**Acceptance:** 5 verbs ship; YAML refs updated; SDLC re-planning run
through these verbs completes end-to-end against this very Epic.

### Phase 3 — Branch verbs

- Migrate `load-work-tree.ps1` → `polyphony branch load-tree`
- Migrate `pg-router.ps1` → `polyphony branch route` (or `next-action`)
- Migrate `impl-router.ps1` → `polyphony branch next-impl`
- Migrate `dependency-check.ps1` → `polyphony branch check-deps`
- Migrate `scope-closer.ps1` → `polyphony branch close-scope`
- These shell out to twig — test fixtures must mock the twig CLI.

**Acceptance:** 5 verbs ship; YAML refs updated; multi-PG implementation
run completes.

### Phase 4 — PR verbs

- Migrate `feature-pr-creator.ps1` → `polyphony pr create-feature-pr`
- Collapse `invoke-gh.ps1` and `resolve-gh-token.ps1` into an internal
  `Polyphony.GitHub` helper class. **Not** surfaced as public verbs — they
  are utility plumbing, not workflow steps.

**Acceptance:** `polyphony pr create-feature-pr` ships; gh helpers exist
as internal types covered by unit tests; YAML refs updated.

### Phase 5 — Plan helpers (review, seed-children)

- Migrate `review-router.ps1` → `polyphony plan review`
  - CLI flag `--max-cycles N` (default 5) replaces the hardcoded value at
    `review-router.ps1:79`.
  - JSON output preserves the `passed`, `forced_by_cap`, `average_score`,
    `technical_score`, `readability_score`, `revision_cycles_completed`,
    `blocking_issue_count`, `combined_feedback` shape.
- Migrate `seeder.ps1` → `polyphony plan seed-children`
  - Preserves marker-match logic (children matched via
    `<!-- polyphony:plan-task-id=task-N -->` in description).
  - Preserves the `polyphony:planned` tag stamp on parent on success.
- Update `plan-level.yaml` refs: drop the `{{ workflow.dir }}/../scripts/`
  indirection in favor of direct `polyphony plan review` /
  `polyphony plan seed-children` invocations.

**Acceptance:** 2 verbs ship; replanning a real Epic works through gates as
before.

### Phase 6 — Sweep workflow YAMLs and delete scripts

- Walk every YAML in `.conductor/registry/workflows/` and replace remaining
  `scripts/*.ps1` refs with `polyphony <group> <verb>` invocations.
- Delete `polyphony/scripts/` entirely except `bootstrap-conductor.ps1`
  and its tests.
- Delete `.conductor/registry/scripts/` (review-router and seeder are now
  in the binary).
- Update `docs/onboarding-guide.md` to reflect the single-binary install
  model (no copy-scripts step).
- Update `README.md` to document the new verb groups.
- Add a short section to `polyphony-sdlc` skill describing the verb
  invocation idiom (workflows shell out to `polyphony <group> <verb>`,
  not to PowerShell scripts).

**Acceptance:**
- `Get-ChildItem polyphony/scripts/*.ps1 | Where Name -ne 'bootstrap-conductor.ps1'`
  returns empty.
- `grep -r 'scripts/' .conductor/registry/workflows/` returns zero matches
  for `.ps1` paths.
- Full SDLC run end-to-end on a fresh epic in this very repo.
- Smoke test: a separate test repo (e.g., a fresh CMMI scratch repo)
  consumes polyphony as a github registry and runs at least one
  `polyphony state <verb>` successfully without copying any PS scripts.

### Phase 7 — Policy layer

- Define the `policy.yaml` schema (see Approach for the sketch; finalize
  schema in this phase).
- Add `polyphony policy load` — reads `.conductor/policy.yaml` (or
  `--input policy=<path>`), validates against schema, emits resolved JSON
  for workflow consumption.
- Add `polyphony policy resolve --scope <root|type:Issue|default> --domain
  <approvals|pr>` — returns the effective mode + caps for a given scope,
  for use in workflow route conditions and verb argument resolution.
- Add `polyphony policy validate` — schema validation only (parallels
  `validate-config`).
- Wire the workflow YAMLs:
  - Top of `polyphony-full.yaml`: call `polyphony policy load`, save
    resolved JSON as `policy_json`.
  - `plan-level.yaml`:
    - Resolve effective approval mode for current item (root if `depth==0`,
      otherwise by_type lookup).
    - Replace `--max-cycles 5` flag on `polyphony plan review` with
      policy-resolved value.
    - Add route condition on `plan_approval`: skip when `mode==auto` OR
      (`mode==warning` AND `review_output.forced_by_cap==false`).
  - `implement-pg.yaml`:
    - Resolve PG's parent item type → effective mode for `user_acceptance`
      gate. Same route-suppression logic.
  - `github-pr.yaml`:
    - Resolve effective PR mode for the work item this PR closes (root
      for feature PR, parent type for PG-PR).
    - Replace hardcoded fix-loop cap (10) with policy value.
    - Add merger route: auto-merge when `mode==auto` OR (`mode==warning`
      AND fixer didn't exhaust).
  - `feature-pr.yaml`:
    - Replace hardcoded remediation cap (3) with
      `policy.pr.root.max_remediation_cycles`.
  - Replace `max_concurrent: 3` literals in plan-level.yaml and
    polyphony-implement.yaml with `policy.concurrency.*` values.

**Acceptance:**
- A `policy.yaml` with `pr.by_type.Task: { mode: auto }` causes a
  Task-scoped PG-PR to merge without a human gate when reviewer approves
  cleanly.
- `approvals.root.mode: manual` keeps the root `plan_approval` gate firing
  even when reviewers all hit ≥90.
- `approvals.defaults.mode: warning` + a clean Issue plan skips the human
  gate.
- A bad-quality plan that hits `max_revision_cycles` under `mode: warning`
  still gates a human (forced_by_cap warning fires).
- Same plan under `mode: auto` proceeds without gate (the explicit "approve
  anyway" semantic).

## Notes

### Open questions for the architect

The user has considered these and has a lean, but explicitly wants the
architect to decide rather than rubber-stamp:

1. **Top-level verb migration scope.** Do the existing top-level verbs
   (`route`, `validate`, `validate-config`, `hierarchy`) migrate into
   `polyphony state <verb>` for naming consistency, or stay top-level for
   shorter human-typing? User lean: stay top-level — they're the verbs
   humans hand-invoke most often, and the lifecycle-group prefix only
   really helps verbs that workflows call. But architect should decide.

2. **Lite-preflight verb shape.** `polyphony state preflight --lite` (sub-flag)
   vs `polyphony state preflight-lite` (distinct subcommand). Lean: sub-flag,
   for fewer verbs to maintain — but if the lite path diverges meaningfully
   from full preflight, distinct subcommands are clearer. Architect should
   inspect the existing `preflight-lite.ps1` divergence and decide.

3. **review-router input shape.** Today the script takes review JSON via
   stdin. As a verb (`polyphony plan review`), should it take JSON via stdin,
   via repeated `--review <path>` flags, or via a single `--reviews <json>`
   flag? Lean: stdin to preserve current contract; flags are ergonomic for
   ad-hoc CLI use but workflows pipe JSON anyway.

4. **Test-fixture mocking strategy.** For verbs that shell out to twig and
   gh: `IProcessRunner` interface (DI-friendly, full unit-test isolation),
   test-mode env var (simpler, less type churn), or CLI mock binary on
   PATH (most realistic, slowest). Architect should pick one consistent
   pattern and apply it to all branch + pr verbs.

5. **Static vs per-invocation policy resolution.** `polyphony policy load`
   could resolve everything once at start-of-run (faster, more predictable,
   but requires re-running the whole workflow to pick up policy edits) or
   each verb could re-read policy on each invocation (more responsive to
   live policy edits, slightly slower). Lean: static — workflows are
   long-running, mid-run policy edits are not a real use case.

6. **Verb-naming bikeshed within groups.** `polyphony branch route` vs
   `polyphony branch sync`; `polyphony plan review-score` vs
   `polyphony plan review`; `polyphony branch close-scope` vs
   `polyphony branch close`. Architect picks; consistency within a group
   matters more than any individual choice.

### Out of v1 scope (noted as follow-on)

These belong to follow-on Epics. The v1 design must not preclude them.

- **`bootstrap-conductor.ps1` migration.** This script bootstraps
  `~/.conductor/` for first-time users. Lives at a different layer
  (one-time install vs per-run workflow) and has 22 KB of Pester tests.
  Out of scope here — separate decision later about whether and how it
  becomes a verb (`polyphony bootstrap`?).

- **ADO PR sub-workflow real implementation.** `ado-pr.yaml` is a stub
  today (manual gate only). When ADO PR support becomes real, the policy
  schema may want a per-provider override layer. Schema design in Phase 7
  must leave clean room for this without blocking on it.

- **Trust-mode presets.** User explicitly does NOT want preset modes named
  "interactive" / "assisted" / "autonomous". Policy is user-supplied
  configurations. If preset profiles become useful later, they'd be
  shipped as example policy files in `docs/`, not as enum values in the
  binary.

- **PR-based plan-approval flow.** The `manual` mode for approvals is
  human-gate today; the user has signaled this becomes a PR-based
  approval flow later (architect publishes the plan as a PR; reviewer
  approves via PR review). Out of v1 scope; v1 just keeps the human gate
  semantics intact under `mode: manual`.

### Acceptance criteria (v1 overall)

- All 16 PowerShell scripts (except `bootstrap-conductor.ps1`) are
  migrated to polyphony verbs and deleted from disk.
- All workflow YAMLs reference `polyphony <group> <verb>` instead of
  `pwsh -File scripts/...`.
- A fresh repo (one without polyphony source on disk) can consume
  polyphony as a github registry and run `polyphony-full@polyphony`
  successfully, given only the polyphony binary on PATH and a
  `.conductor/process-config.yaml`.
- A `.conductor/policy.yaml` with non-default values materially changes
  workflow behavior end-to-end (gate suppression, cap overrides,
  auto-merge thresholds).
- All existing tests green; new contract tests cover JSON-output parity
  per migrated verb.
- `polyphony validate-config` and `polyphony policy validate` both pass
  on the polyphony repo's own config.

### Process notes

- This Epic is the second dogfood of the `user_plan_path` mechanism on a
  non-trivial real feature, and the first dogfood of polyphony driving its
  own SDLC for a multi-phase architectural change. Expect to surface bugs
  in both polyphony-the-binary and the workflow registry. File any
  findings as siblings under this Epic.
- The work touches both the polyphony C# project (`src/Polyphony/`) and
  the registry workflow YAMLs (`.conductor/registry/workflows/`). Both
  live in this repo (registry was co-located in commit cb48a54), so this
  is single-repo, no cross-repo coordination needed.
- Phases 2–5 are parallelizable after Phase 1 lands; the architect should
  surface that opportunity in PG layout (one PG per phase, with Phase 1
  blocking the parallel band, and Phase 6 blocking on all of them).

