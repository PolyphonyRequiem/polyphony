# PowerShell Script → Typed Verb Migration

**Status:** Draft
**Work item:** AB#3258 (parent epic: AB#3253)
**Sibling issues:** AB#3254 (action journal), AB#3255 (typed contract surface), AB#3256 (PR/branch verb matrix collapse), AB#3257 (failure-mode gate elimination), AB#3259 (vocabulary normalization)
**Dependency:** AB#3255 (typed contract surface) — required for WRAP-A-VERB elimination

## TL;DR

The earlier audit and the branch-local recount agree on the headline: **187 script nodes across 15 workflows**. The actionable file-level inventory is **12 registry scripts** under `.conductor/registry/scripts/`: **3 WRAP-A-VERB, 8 COMPOSE-VERBS, 1 SHELL-OUT**.

- **WRAP-A-VERB**: fix the underlying verb contract in AB#3255, then delete the script.
- **COMPOSE-VERBS**: promote the operation into a typed CLI verb.
- **SHELL-OUT**: keep only true external-tool shims, and require `polyphony serialize` plus lint.

The target state is one typed contract surface for deterministic workflow behavior, with PowerShell reduced to a tiny, explicit escape hatch.

## Goal

Move deterministic workflow-step logic out of `.conductor/registry/scripts/` and into typed Polyphony CLI verbs, leaving only tightly constrained external-tool shims in PowerShell.

## Non-Goals

- **Not a migration of repo-root `scripts/*.ps1`.** Those files are launcher/bootstrap/operator surface, not registry workflow helpers.
- **Not a redesign of AB#3255 itself.** This proposal consumes typed-contract work; it does not replace it.
- **Not a removal of external tools.** `git`, conductor's web API, and similar surfaces still exist; the question is where the business logic lives.
- **Not a vocabulary-renaming brief.** This proposal uses canonical post-AB#3259 terms in prose, but it references current filenames as-is.

---

## Audit

Scope: `.conductor/registry/scripts/*.ps1` only. Verified out-of-scope: searches of `.conductor/registry/workflows/*.yaml` show executable PowerShell paths resolve to `{{ workflow.dir }}/../scripts/*.ps1`, i.e. the registry script directory, not repo-root `scripts/`. Repo-root `scripts/*.ps1` appears only in launcher/operator references (for example `reset-root.yaml` mentions `Invoke-PolyphonySdlc.ps1` in prose).

### Catalog

Caller counts are unique workflow files containing an executable reference to the script path. LOC is `Get-Content <file> | Measure-Object -Line`.

| Script | Path | Purpose | Callers | LOC | External deps |
|---|---|---|---|---:|---|
| `abort-run.ps1` | `.conductor/registry/scripts/abort-run.ps1` | Abort the whole run by POSTing conductor `/api/stop`. | 7 — `ado-pr.yaml`, `restack-remedy.yaml`, `feature-pr.yaml`, `github-pr.yaml`, `implement-merge-group.yaml`, `plan-level.yaml`, `remedy-stale-descendant.yaml` | 80 | conductor HTTP API |
| `integrate-target-drift.ps1` | `.conductor/registry/scripts/integrate-target-drift.ps1` | Integrate target-branch drift into the feature branch before the feature PR opens. | 1 — `feature-pr.yaml` | 313 | git |
| `lifecycle-router.ps1` | `.conductor/registry/scripts/lifecycle-router.ps1` | Classify a work item into the next lifecycle sub-workflow for the root dispatch loop. | 1 — `root-item-dispatch.yaml` | 337 | polyphony |
| `manifest-bootstrap.ps1` | `.conductor/registry/scripts/manifest-bootstrap.ps1` | Create or validate the per-run manifest for a root-scoped run. | 1 — `polyphony.yaml` | 235 | polyphony |
| `resolve-pr-policy.ps1` | `.conductor/registry/scripts/resolve-pr-policy.ps1` | Resolve PR pre-merge policy for the PR router. | 2 — `ado-pr.yaml`, `github-pr.yaml` | 126 | polyphony |
| `resolve-research-policy.ps1` | `.conductor/registry/scripts/resolve-research-policy.ps1` | Resolve research escalation policy for the planning research leg. | 1 — `plan-level.yaml` | 180 | polyphony |
| `resolve-unattended-cap-mode.ps1` | `.conductor/registry/scripts/resolve-unattended-cap-mode.ps1` | Resolve unattended cap policy for cap-hit gates. | 5 — `ado-pr.yaml`, `feature-pr.yaml`, `github-pr.yaml`, `implement-merge-group.yaml`, `plan-level.yaml` | 110 | polyphony |
| `route-actionable-executor.ps1` | `.conductor/registry/scripts/route-actionable-executor.ps1` | Route actionable work between the Polyphony executor leg and the human executor leg. | 1 — `actionable.yaml` | 59 | none |
| `strip-planning-artifacts.ps1` | `.conductor/registry/scripts/strip-planning-artifacts.ps1` | Remove Polyphony planning artifacts from the feature branch before the feature PR opens. | 1 — `polyphony.yaml` | 232 | git |
| `batch-dispatch-guard.ps1` | `.conductor/registry/scripts/batch-dispatch-guard.ps1` | Track per-root batch failures so later batches short-circuit. | 2 — `polyphony.yaml`, `root-batch-dispatch.yaml` | 158 | git, filesystem |
| `batch-integrator.ps1` | `.conductor/registry/scripts/batch-integrator.ps1` | Merge completed child branches into the feature branch in dependency order. | 1 — `root-batch-dispatch.yaml` | 265 | polyphony, git |
| `worktree-manager.ps1` | `.conductor/registry/scripts/worktree-manager.ps1` | Spawn or tear down per-item git worktrees for parallel dispatch. | 1 — `root-item-dispatch.yaml` | 243 | git |

### Categorization

| Script | Bucket | Why |
|---|---|---|
| `abort-run.ps1` | **SHELL-OUT** | Its only real job is invoking conductor's `/api/stop` endpoint and normalizing the result envelope; no Polyphony business rule is embedded beyond input passthrough. |
| `integrate-target-drift.ps1` | **COMPOSE-VERBS** | Although it shells only to `git`, it owns deterministic branch-policy logic — divergence detection, lease-safe push, conflict handling, and drift integration semantics — that belongs in the binary. |
| `lifecycle-router.ps1` | **COMPOSE-VERBS** | It combines `polyphony state next-ready`, optional `polyphony hierarchy`, and classifier logic that maps requirement kinds to workflow routes. |
| `manifest-bootstrap.ps1` | **COMPOSE-VERBS** | It switches across `polyphony manifest read` and `polyphony manifest init`, adds invocation-side `platform_project` validation, and emits a create/reuse contract no existing verb owns. |
| `resolve-pr-policy.ps1` | **WRAP-A-VERB** | It makes one `polyphony policy resolve --domain pr` call and spends the rest of the script reshaping fields and fallback behavior for workflow routing. |
| `resolve-research-policy.ps1` | **WRAP-A-VERB** | It makes one `polyphony policy resolve --domain research` call and adds only envelope normalization, validation, and fallback defaults. |
| `resolve-unattended-cap-mode.ps1` | **WRAP-A-VERB** | It makes one `polyphony policy load` call and extracts a single policy field into a workflow-friendly envelope. |
| `route-actionable-executor.ps1` | **COMPOSE-VERBS** | It is a pure deterministic router whose only reason to exist is that Polyphony lacks a typed workflow-step verb for executor routing. |
| `strip-planning-artifacts.ps1` | **COMPOSE-VERBS** | It discovers, deletes, commits, and pushes Polyphony-owned plan artifacts; that retention policy is business logic, not a keepable git shim. |
| `batch-dispatch-guard.ps1` | **COMPOSE-VERBS** | It stores Polyphony-owned batch-failure sentinel state under the git common dir, so the control-plane state lives in the wrong layer today. |
| `batch-integrator.ps1` | **COMPOSE-VERBS** | It combines `polyphony edges check` with ordered `git merge` execution and per-branch conflict aggregation, which is exactly the deterministic orchestration the CLI should own. |
| `worktree-manager.ps1` | **COMPOSE-VERBS** | Even though the body is mostly `git worktree` calls, it enforces Polyphony worktree lifecycle invariants such as idempotent attach/reuse and branch-in-use guards. |

### Ambiguous cases

These files have characteristics of more than one bucket, but the migration recommendation is still decisive:

- **`worktree-manager.ps1`** — looks like a SHELL-OUT because it is git-heavy, but the idempotent spawn/attach/teardown rules are Polyphony branch-model logic, so it is COMPOSE-VERBS.
- **`integrate-target-drift.ps1`** — looks like a git-only helper, but the lease/conflict/rebase policy is part of Polyphony's PR workflow contract and should not remain in script-land.
- **`manifest-bootstrap.ps1`** — almost a wrapper, but it really composes two verbs plus caller-side validation into a new operation, so it is COMPOSE-VERBS, not WRAP-A-VERB.
- **`batch-dispatch-guard.ps1`** — could disappear entirely if AB#3254/AB#3257 absorbs the sentinel concept, but until then the state it owns is internal control-plane state and should not remain in PowerShell.

---

## Design Dimensions

For each dimension that has a real tradeoff, options are listed with a recommendation.

### D1 — Taxonomy granularity: classify per script file or per call site?

**Options:**

- **(a) Per call site.** More precise, but the migration unit is not the call site.
- **(b) Per file.** Matches deletion, replacement PRs, and linting.
- **(c) Per statement block.** Theoretically pure, operationally useless.

**Recommendation: (b) per file.** Ambiguous files get called out explicitly, but each file still ends with one owner and one destination.

### D2 — Destination surface: existing nouns, `workflow-step`, or both?

**Options:**

- **(a) Existing nouns only.** Cleaner top level, but awkward for pure routers/guards.
- **(b) `workflow-step` for everything.** Easy bucket, but too generic for durable domain operations.
- **(c) Hybrid.** Noun-specific verbs where the operation is durable; `workflow-step` only for route/guard verbs.

**Recommendation: (c) hybrid.** Use noun-specific verbs for `manifest ensure`, `pr integrate-target-drift`, `plan strip-artifacts`, `branch worktree`, and `branch integrate-batch`; reserve `workflow-step` for `lifecycle-route`, `actionable-executor`, and `batch-failure-guard`.

### D3 — WRAP-A-VERB treatment: keep wrappers, add more wrappers, or fix the underlying verb?

**Options:**

- **(a) Keep wrappers.** Fast locally, wrong long-term.
- **(b) Add a generic adapter layer.** Fewer scripts, still two contract surfaces.
- **(c) Fix the underlying verb and delete the wrapper.** Correct, but blocked on AB#3255.

**Recommendation: (c).** WRAP-A-VERB scripts are evidence that the owning verb contract is incomplete.

For dependency mapping in this document, AB#3255 is split into three contract slices:

- **CT1 — serializer primitive.** `polyphony serialize` exists for the scripts that legitimately survive.
- **CT2 — policy projections on existing verbs.** `policy load` / `policy resolve` surface workflow-ready typed fields plus `source` / `policy_error` without script help.
- **CT3 — new result records for migrated verbs.** New typed verbs ship their own `*Result` contracts in `PolyphonyJsonContext`.

#### WRAP-A-VERB deletions

| Script | Underlying verb | What the verb must emit so the script dies | Delete when |
|---|---|---|---|
| `resolve-unattended-cap-mode.ps1` | `polyphony policy load` | A typed projection for `unattended.cap_mode` plus `source` and `policy_error`, so workflows can route directly on verb output. | **CT2** — the policy-load projection PR. |
| `resolve-pr-policy.ps1` | `polyphony policy resolve --domain pr --scope <scope>` | A typed PR-domain projection with `mode`, `source`, and `policy_error`; no script-side allow-list or fallback envelope. | **CT2** — the policy-resolve PR-domain projection PR. |
| `resolve-research-policy.ps1` | `polyphony policy resolve --domain research --scope <scope>` | A typed research-domain projection with `mode`, `escalation_cap`, `source`, and `policy_error`; no script-side normalization. | **CT2** — the policy-resolve research-domain projection PR. |

All three wrappers should disappear in the **same phase that lands the corrected verb contract**. There is no value in shipping a “better wrapper” once the underlying verb can emit the final envelope directly.

### D4 — COMPOSE-VERBS migration surface

These files need **new typed verbs**, not better wrappers. The compact inventory is:

| Script | Proposed verb | C# sketch | CT3 contract need |
|---|---|---|---|
| `route-actionable-executor.ps1` | `workflow-step actionable-executor --work-item <id> --executor <polyphony|human>` | `Emit(ActionableExecutorRouteResult.From(workItem, executor))` | `ActionableExecutorRouteResult` |
| `manifest-bootstrap.ps1` | `manifest ensure --root-id <id> --platform-project <dev.azure.com/org/project>` | `read = manifest.Read(); read ? Reused(read) : manifest.Init()` | `ManifestEnsureResult` + stable manifest error codes |
| `lifecycle-router.ps1` | `workflow-step lifecycle-route --work-item <id> --root <id>` | `next = state.NextReady(); childCount = root ? hierarchy.ChildCount() : 0; classify(next, childCount)` | `LifecycleRouteResult`; stable `next_ready` fields |
| `worktree-manager.ps1` | `branch worktree --operation <spawn|teardown> --work-item <id> --base-branch <name>` | `plan = worktrees.Plan(...); apply(plan)` | `WorktreeOperationResult` |
| `integrate-target-drift.ps1` | `pr integrate-target-drift --feature-branch <name> --target-branch <name>` | `outcome = prService.IntegrateTargetDrift(...); Emit(TargetDriftIntegrationResult.From(outcome))` | `TargetDriftIntegrationResult` |
| `strip-planning-artifacts.ps1` | `plan strip-artifacts --root <id> --feature-branch <name>` | `files = planArtifacts.Find(...); RemoveCommitPush(files)` | `PlanningArtifactsStripResult` |
| `batch-dispatch-guard.ps1` | `workflow-step batch-failure-guard --operation <clear|check|record> --root <id> --batch-index <n>` | `batchFailureStore.Execute(operation, root, batchIndex, reason)` | `BatchFailureGuardResult` |
| `batch-integrator.ps1` | `branch integrate-batch --root <id> --batch-index <n> --work-items <csv>` | `topo = edges.TopologicalOrder(root); branchIntegrator.IntegrateBatch(...)` | `BatchIntegrationResult`; pin `edges_check.topological_order` |

Ship these as **one script-at-a-time PRs**: add verb, add result model to `PolyphonyJsonContext`, extend `JsonOutputContractTests`, retarget the workflow, bump `min_polyphony_version`, delete the script.

### D5 — SHELL-OUT keepers: how strict should “keep” be?

**Options:**

- **(a) Keep any external-tool script.** Easy, but it preserves business-logic drift.
- **(b) Keep only thin shims.** Small, reviewable, lintable surface.
- **(c) Ban scripts entirely.** Unrealistic while conductor and similar tools remain external.

**Recommendation: (b).** On the current inventory, only one file qualifies.

| Script | Why it stays | Tightening required |
|---|---|---|
| `abort-run.ps1` | It talks to conductor's web API, which Polyphony does not own, and the script contains no reusable domain algorithm beyond invoking `/api/stop` and normalizing the result. | Replace hand-rolled `ConvertTo-Json` with `polyphony serialize`; keep deterministic `error_code` values; forbid any future logic beyond input passthrough, API call, and envelope emission. |

#### Shell-out hygiene rules

1. **External boundary only.** No Polyphony business decisions in script-land.
2. **No `polyphony` except `serialize`.** Anything else is misbucketed.
3. **Deterministic output.** Stable fields, stable ordering, fixed `error_code` taxonomy.
4. **One envelope.** Emit once to stdout through `polyphony serialize`, never hand-roll JSON.
5. **No control-plane state.** If the script needs sentinels or workflow policy, move it to a typed verb.

### D6 — Enforcement: document-only or linted?

**Options:**

- **(a) Advisory only.** *Con:* drift resumes immediately after the first cleanup PR.
- **(b) Hard lint once the replacements exist.** *Pro:* lets migration proceed without blocking itself, then prevents regression.
- **(c) Immediate hard fail.** *Con:* blocks the repo before the typed replacements land.

**Recommendation: (b).** After the first delete/migrate batch, add a dedicated lint that:

- Requires a header tag such as `# polyphony-script-category: SHELL-OUT` on every surviving registry script.
- Fails any new registry script whose category is not `SHELL-OUT`.
- Fails any SHELL-OUT script that invokes `polyphony` for anything other than `serialize`.
- Fails any SHELL-OUT script that lacks deterministic envelope emission.

---

## Phased plan

| Phase | Scope | AC | Risk |
|---|---|---|---|
| **1** | Catalog + taxonomy (this doc) | Inventory is checked, every registry script has a bucket, ambiguous cases are named explicitly | None |
| **2** | AB#3255 contract slices CT1 + CT2 | `polyphony serialize` exists; `policy load` / `policy resolve` emit workflow-ready typed projections | Low — additive |
| **3** | Delete WRAP-A-VERB scripts | `resolve-unattended-cap-mode.ps1`, `resolve-pr-policy.ps1`, and `resolve-research-policy.ps1` are gone; workflows route directly on typed verb output | Low |
| **4** | Migrate low-risk COMPOSE-VERBS | Typed replacements land for `route-actionable-executor.ps1`, `manifest-bootstrap.ps1`, and `lifecycle-router.ps1`; scripts deleted in the same PRs | Medium |
| **5** | Migrate git-heavy COMPOSE-VERBS | Typed replacements land for `worktree-manager.ps1`, `integrate-target-drift.ps1`, `strip-planning-artifacts.ps1`, `batch-dispatch-guard.ps1`, and `batch-integrator.ps1` | Medium-High |
| **6** | Tighten remaining SHELL-OUT scripts + add lint | `abort-run.ps1` emits via `polyphony serialize`; script-category lint prevents new non-SHELL-OUT helpers | Medium |
| **7** | Cleanup + documentation convergence | Registry script count is down to true keepers only; CLI reference and workflow docs point at verbs, not deleted scripts; `min_polyphony_version` floors are correct everywhere | Low |

Phases 2 and 3 are the unlock for wrapper deletion. Phases 4 and 5 should be intentionally incremental: one script, one verb, one workflow call-site migration, one set of JSON contract tests per PR.

---

## Open questions

1. **Surface naming.** Is the hybrid surface (`workflow-step` for pure routers/guards, noun-specific verbs for durable operations) the right split, or do you want to forbid `workflow-step` entirely and force everything under existing nouns?
2. **`batch-dispatch-guard.ps1` timing.** Should we migrate it directly in phase 5, or hold it behind AB#3254 / AB#3257 in case the action journal or failure-model work deletes the sentinel concept first?
3. **`abort-run.ps1` end-state.** Is one long-lived SHELL-OUT acceptable, or do you want the stronger “zero registry scripts” end-state with a future typed `polyphony run abort` even though conductor is external?
4. **Lint tag format.** Is a simple header pragma (`# polyphony-script-category: SHELL-OUT`) good enough, or do you want a stronger manifest/metadata file for script taxonomy?
5. **Batch integration noun.** `branch integrate-batch` is semantically decent, but if you want all root-aggregation verbs under a different noun, this is the place to decide it before phase 5 starts shipping verbs.

---

## Appendix

### A1 — Audit method

- **Script inventory:** `Get-ChildItem .conductor/registry/scripts -Filter *.ps1`
- **Caller count:** unique workflow files containing an executable `{{ workflow.dir }}/../scripts/<name>.ps1` reference
- **LOC:** `Get-Content <file> | Measure-Object -Line`
- **Registry-level script-node recount:** grep for `type: script` across `.conductor/registry/workflows/*.yaml` → 187 script nodes on this branch

### A2 — Repo-root `scripts/` verification

Repo-root `scripts/*.ps1` is out-of-scope for AB#3258. The workflow registry executes `.conductor/registry/scripts/*.ps1`; repo-root `scripts/` is launcher/bootstrap/operator surface and appears in registry YAML only in prose or comments (for example the `reset-root.yaml` reference to `Invoke-PolyphonySdlc.ps1`).

