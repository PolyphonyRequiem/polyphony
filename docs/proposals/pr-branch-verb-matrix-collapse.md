# PR/branch verb matrix collapse

**Status:** Draft  
**Work item:** AB#3256 (parent epic: AB#3253)  
**Sibling issues:** AB#3254 (action journal), AB#3255 (typed contract surface), AB#3257 (failure-mode gate elimination), AB#3258 (script→verb migration), AB#3259 (vocabulary normalization)  
**Dependency:** AB#3255 (typed contract surface) — preferred to land first

## TL;DR

Polyphony currently spends **26 `pr`/`branch` verbs** on one taxonomic problem: the same lifecycle action repeated by **layer** and **platform**. The audited surface is not even a clean rectangle: feature open still uses `create-feature-*`, status is split by platform but not by layer, and four `-pr` verbs already do partial platform dispatch while their siblings do not.

This proposal collapses that surface to **five canonical verbs**:

- `polyphony pr open --layer <feature|plan|merge-group|impl|evidence> [--platform ...]`
- `polyphony pr merge --layer <feature|plan|merge-group|impl|evidence> [--platform ...]`
- `polyphony pr status [--layer <...>] [--platform ...]`
- `polyphony branch ensure --layer <feature|plan|merge-group|impl|evidence>`
- `polyphony branch delete --layer <feature|plan|merge-group|impl|evidence>`

The important constraint is **not** name collapse by itself; it is preserving the real per-cell variation that exists today: plan PRs carry ancestor-generation snapshots and merge under a run lock, impl PRs allow method overrides on GitHub but are squash-only on Azure DevOps, merge-group PRs must preserve ancestry, evidence PRs have orphan-vs-root naming, and feature PRs are asymmetric across platforms. The new surface therefore keeps a thin uniform CLI and moves the variation behind **typed layer/platform handlers** plus a **typed discriminated-union envelope** from AB#3255.

## Goal

Collapse the matrix-expanded `pr` and `branch` verbs into a small parameterized surface without deleting any layer-specific or platform-specific behavior, and without forcing a flag day across the existing workflow fleet.

## Non-Goals

- **Not** addressing derive/extract verbs. Those are tracked by AB#3255 and stay as-is here.
- **Not** collapsing genuine composition primitives (`reset.*`, `lock.*`, `policy.*`, `state.*`, `worktree.*`).
- **Not** redesigning PR comment/vote helpers (`post-comment-ado`, `vote-ado`, `get-comments-ado`) beyond the status collapse described here.
- **Not** changing merge policy semantics. Impl remains squash-by-default, merge-group remains merge-commit / no-fast-forward, and plan merge retains manifest + lock behavior.
- **Not** moving reviewer or label policy into these verbs; the audited command cells do not own that concern today.

---

## Current state audit

Across `src/Polyphony/Commands/PrCommands*.cs` and `src/Polyphony/Commands/BranchCommands*.cs` I found:

- **27 `pr` verbs total**
- **13 `branch` verbs total**
- **21 `pr` verbs** that are matrix candidates
- **5 `branch` verbs** that are matrix candidates
- **26 matrix-expanded verbs total** in scope for this proposal

Two irregularities matter immediately:

1. The current PR surface is already **partially collapsed and inconsistent**. `open-impl-pr`, `merge-impl-pr`, `open-evidence-pr`, and `merge-evidence-pr` already accept `--platform` / repo-identity dispatch and internally bridge to Azure DevOps handlers, while the feature, plan, merge-group, and status families still keep separate platform verbs.
2. The current branch surface only has `ensure-*`; there is no layer-expanded delete family yet. This spec still reserves `branch delete --layer ...` so delete does not re-expand later under a second naming scheme.

### PR matrix as implemented today

| Layer | Open/create surface | Merge surface | Status surface | Notes |
|---|---|---|---|---|
| **feature** | `create-feature-pr` / `create-feature-ado` | GitHub: none yet; ADO: `merge-feature-ado` | generic `poll-status*` pair | open is still called `create`; GitHub merge is still workflow-owned |
| **impl** | `open-impl-pr` / `open-impl-ado` | `merge-impl-pr` / `merge-impl-ado` | generic `poll-status*` pair | `-pr` already dispatches to ADO, but the old twins still exist |
| **merge-group** | `open-mg-pr` / `open-mg-ado` | `merge-mg-pr` / `merge-mg-ado` | generic `poll-status*` pair | still fully doubled by platform |
| **plan** | `open-plan-pr` / `open-plan-ado` | `merge-plan-pr` / `merge-plan-ado` | generic `poll-status*` pair | snapshot-bearing open; lock + manifest merge |
| **evidence** | `open-evidence-pr` / `open-evidence-ado` | `merge-evidence-pr` / `merge-evidence-ado` | generic `poll-status*` pair | `-pr` already dispatches to ADO here too |


### Branch matrix as implemented today

| Layer | Current verb | Real variation that the collapsed verb must preserve |
|---|---|---|
| **feature** | `ensure-feature` | freeform branch/base input plus `exists_in_other_worktree` / `worktree_path` |
| **merge-group** | `ensure-mg` | parent-vs-feature base derivation plus `depth_warning` / `depth_exceeded` |
| **impl** | `ensure-impl` | requires both item and `--mg-path`; base must already be materialized |
| **plan** | `ensure-plan` | root plan vs direct child vs descendant plan in one verb |
| **evidence** | `ensure-evidence-branch` | optional root, orphan-vs-root naming, optional `--from-ref` |


### Outliers

Out-of-scope `pr` verbs: `assert-impl-pr-coverage`, `check-evidence-floor`, `get-comments-ado`, `post-comment-ado`, `validate-plan-diff`, `vote-ado`.

Out-of-scope `branch` verbs: `assert-on-impl`, `check-deps`, `close-scope`, `load-tree`, `mark-impl-merged`, `clear-impl-merged`, `next-impl`, `route`.

### The real per-cell variation

This is the reason the matrix exists today, and the collapse must preserve it:

- **Feature cells are asymmetric.** Open is still called `create`, and GitHub feature merge is not yet a CLI verb.
- **Plan cells are contract-heavy.** Open embeds front matter (`requests_parent_change`, `ancestor_plan_generations`); merge adds lock, manifest, generation, and stale-ancestor behavior.
- **Impl cells are platform-divergent.** GitHub exposes merge-method/admin/delete-branch choices; ADO is squash-only and uses stale-head guards.
- **Merge-group cells are ancestry-preserving.** Both platforms pin merge behavior and force `delete_branch=false`.
- **Evidence cells carry orphan-vs-root behavior.** Open owns special naming and overrides; merge is queued on GitHub and immediate on ADO.
- **Status and feature-branch ensure are already special cases.** Status is logically unified but mechanically split; feature branch ensure is the only branch cell with sibling-worktree resolution.

---

## Design Dimensions

### D1 — Surface grammar: where do layer and platform live?

**Options:**

- **(a) Keep the current matrix names.** Lowest implementation risk, zero conceptual simplification.
- **(b) Collapse only the platform dimension.** Example: `open-plan --platform ado|github`. Better, but still leaves five layer-specific verbs per action.
- **(c) Collapse both dimensions into `action + --layer + --platform`.** One verb per lifecycle action; layer/platform become validated parameters.

**Recommendation: (c).**

That yields the stable surface this proposal is actually after:

```text
polyphony pr open   --layer <feature|plan|merge-group|impl|evidence> [--platform <github|ado>]
polyphony pr merge  --layer <feature|plan|merge-group|impl|evidence> [--platform <github|ado>]
polyphony pr status [--layer <feature|plan|merge-group|impl|evidence>] [--platform <github|ado>]
polyphony branch ensure --layer <feature|plan|merge-group|impl|evidence>
polyphony branch delete --layer <feature|plan|merge-group|impl|evidence>
```

Notes:

- `--platform` defaults from repo identity resolution. Explicit platform still wins.
- `--layer merge-group` is the canonical public spelling. The legacy `mg` token stays in branch grammar (`mg/...`, `--mg-path`) and in shims only.
- `create-feature-*` is normalized into `pr open --layer feature`.
- The missing GitHub feature-merge cell becomes `pr merge --layer feature --platform github`; the current workflow-owned `gh pr merge` logic moves under the same handler registry as the other cells.

### D2 — Request model: one verb, many required-field combinations

**Options:**

- **(a) One giant optional-flag bag with ad-hoc validation in the verb body.** Easy to expose, hard to reason about.
- **(b) A single JSON input blob.** Strong typing internally, poor operator ergonomics and poor parity with the rest of the CLI.
- **(c) Thin CLI flags outside, typed request unions inside.** The surface stays CLI-friendly while the implementation immediately normalizes into `Feature`, `Plan`, `MergeGroup`, `Impl`, and `Evidence` request cases.

**Recommendation: (c).**

The public surface should stay small:

- `pr open --layer … [cell-specific flags]`
- `pr merge --layer … [cell-specific flags]`
- `pr status [--layer …] [platform-specific identifiers]`
- `branch ensure|delete --layer … [cell-specific flags]`

Canonical flags across the family are `--root`, `--item-id`, `--parent-item-id`, `--mg-path`, `--pr-number`, `--pr-url`, `--target-branch`, `--head`, `--base-branch`, `--title`, `--body`, `--platform`, and the ADO identity triple `--organization/--project/--repository`. Not every flag is legal in every cell.

Validation rules by layer:

| Layer | Open / ensure requirements | Merge requirements | Distinct rules |
|---|---|---|---|
| **feature** | `--root`; optional target/base branch | `--root`; optional target branch + head-match | derives `feature/{root}`; no `--item-id` or `--mg-path` |
| **impl** | `--root`, `--item-id`, `--mg-path` | same plus optional method/admin/delete-branch/head-match | GitHub honors method overrides; ADO coerces to squash semantics |
| **merge-group** | `--root`, `--mg-path` | same plus optional head-match | merge mode fixed to ancestry-preserving merge; delete false |
| **plan** | `--root`, `--item-id`; optional parent + ancestor ids | same plus `--pr-number`; optional manifest/lock args | root/direct-child/descendant cases validated separately |
| **evidence** | `--item-id`; optional `--root`, head, base | `--pr-number`; optional `--pr-url`, `--root` | `--root` defaults to item; orphan-vs-root naming preserved |

`pr status` stays layer-agnostic at its core. GitHub requires `--pr-url`; ADO requires the identity triple plus `--pr-number`; `--layer plan` can imply metadata parsing by default.

The important point is that **the CLI is collapsed, not the semantics**. Validation still proves the caller selected a valid cell.

### D3 — Output contract: preserve cell-specific data without 26 verbs

**Options:**

- **(a) One discriminated-union envelope per action family.** Shared top-level fields, layer/platform-specific payload cases.
- **(b) Keep returning per-layer DTOs even behind the new verb names.** Simpler now, but the output contract stays stringly and harder to lint.
- **(c) Opaque `object` / JSON bag.** Smallest implementation, worst consumer experience.

**Recommendation: (a), with (b) as the fallback only if AB#3255 misses the landing order.**

Canonical shapes:

- `PrOpenResult = FeatureOpen | ImplOpen | MergeGroupOpen | PlanOpen | EvidenceOpen`
- `PrMergeResult = FeatureMerge | ImplMerge | MergeGroupMerge | PlanMerge | EvidenceMerge`
- `PrStatusResult = StatusSnapshot` with an optional plan-metadata case
- `BranchEnsureResult` / `BranchDeleteResult` with the same five layer cases

Every case carries `action`, `layer`, `platform`, `error_code`, `error`, and the relevant identity fields (`branch`, `head_branch`, `base_branch`, `pr_number`, `pr_url`). The payload then preserves the cell-specific data that exists today: feature title/summary, plan snapshot + stale bits, plan-merge manifest/lock data, merge-group depth data, evidence orphan data, and so on. In other words: the new union is a typed wrapper over the existing DTO families, not a loss of fidelity.
Two normalization decisions matter here:

1. **The new collapsed verbs should be envelope-first.** Expected refusals surface as `error_code`, not as a maze of verb-specific exit-code conventions. Truly malformed CLI usage can still use the standard required-input/config failure path.
2. **Legacy verbs keep their legacy envelopes while the shim exists.** That prevents a breaking change for downstream callers that currently deserialize `PrOpenPlanPrResult`, `PrMergePlanPrResult`, and friends.

If AB#3255 lands later than AB#3256, the fallback is: new verbs exist, but first-party workflows stay on shims until the union schema is available. We should not force early adopters to eat a second output-contract change.

### D4 — Implementation strategy: collapse names, not behavior

**Options:**

- **(a) Keep the current bridge pattern.** New verbs shell back into old verbs and capture stdout.
- **(b) Extract per-cell handlers keyed by `(action, layer, platform)`, then make both new verbs and legacy shims call those handlers directly.**
- **(c) Rewrite every cell from scratch under the new names.**

**Recommendation: (b).**

The existing code already shows the shape we want, but in the wrong direction. Today some `-pr` verbs bridge to ADO by capturing console output from the old ADO twin. The collapse should invert that:

- move the real behavior into handler classes or internal typed methods
- key them by `(open|merge|status|ensure|delete, layer, platform)`
- let `pr open` / `pr merge` / `pr status` / `branch ensure` select a handler after validation
- let old verbs become thin adapters that fix `layer` and `platform`, translate old parameter names, and emit old DTOs

That preserves the real behavior differences without preserving the public matrix.

Examples of behavior that stay cell-specific inside handlers:

- plan open/merge handlers own manifest reads, front matter, stale-generation logic, and run-lock interaction
- impl merge handlers own GitHub method overrides vs ADO squash-only coercion
- merge-group merge handlers pin ancestry-preserving merge mode and `delete_branch=false`
- evidence handlers own orphan-vs-root naming and queued-vs-immediate merge semantics
- feature handlers own the current GitHub/ADO asymmetry until merge parity is deliberately revisited

### D5 — Migration and deprecation: move the fleet without a flag day

**Options:**

- **(a) Flag day.** Rename every workflow and every downstream caller at once.
- **(b) Add collapsed verbs, keep old verbs as shims, then migrate first-party callers by domain.**
- **(c) Add the new verbs but leave the old matrix indefinitely.**

**Recommendation: (b).**

Migration should be **per domain**, not one repo-wide search/replace, because the output maps and validation rules differ per layer. Within a domain, the edit is mechanical:

- **feature PR:** `.conductor/registry/workflows/feature-pr.yaml`, `.conductor/registry/workflows/ado-pr.yaml`, `.conductor/registry/workflows/github-pr.yaml`
- **plan PR:** `.conductor/registry/workflows/plan-level.yaml`
- **merge-group / impl:** `.conductor/registry/workflows/implement-merge-group.yaml`
- **evidence:** `.conductor/registry/workflows/actionable.yaml`
- **root branch bootstrap:** root workflow file (`polyphony.yaml` after AB#3259 rename)

Also update `.conductor/registry/tests/*.ps1`, `.conductor/registry/tests/verb-signature-contracts.Tests.ps1`, `tests/Polyphony.Tests/Commands/PrCommands*`, `tests/Polyphony.Tests/Commands/BranchCommandsEnsure*`, verb-catalog tests, and the operator docs.

Deprecation mechanics are straightforward: every old matrix verb stays callable for **two releases**, warns on **stderr only**, prints the exact replacement with fixed `--layer` / `--platform` values, and is banned in first-party CI once the workflow migration lands. Removal requires zero first-party references plus one full release on the new verbs. The window should stay release-based because external consumers such as `cloudvault-service-api` are not globally observable.

### D6 — Irregular cells: feature merge, generic status, and future delete

**Options:**

- **(a) Collapse only the cells that already exist as neat pairs.** Leave feature merge on GitHub in workflow YAML and leave branch delete undefined.
- **(b) Use the collapse to finish the job: normalize feature open, add the missing GitHub feature merge handler, keep status generic, and reserve branch delete now.**

**Recommendation: (b).**

Concretely:

- `create-feature-pr` / `create-feature-ado` become the feature case of `pr open`
- the current GitHub feature-merge logic in `github-pr.yaml` becomes the GitHub feature case of `pr merge`
- `pr status` remains intentionally layer-agnostic, but `--layer plan` may default `--include-metadata=true` because only plan PRs currently carry front matter
- `branch delete --layer ...` is defined in this spec even though the audited code only has `ensure-*` today; otherwise delete will reappear later as a second matrix

---

## Phasing

| Phase | Scope | Risk |
|---|---|---|
| **0** | ADR / proposal approval | None |
| **1** | Extract handlers; add collapsed verbs + shims | Low |
| **2** | Migrate first-party workflows by domain | Medium |
| **3** | Migrate lints, tests, and operator docs | Medium |
| **4** | Enable strict deprecations in first-party CI/harness | Medium |
| **5** | Remove shims after two releases | Higher |

If AB#3255 slips, phases 1-2 split:

- **1a** extract handlers + add shims only
- **1b** once typed envelopes exist, expose the collapsed verbs publicly and start workflow migration

That sequencing keeps the public output contract stable.

---

## What we are NOT deciding here

- **Derive/extract surface rationalization.** That is AB#3255, not this doc.
- **Non-matrix PR helpers.** `check-evidence-floor`, `validate-plan-diff`, `post-comment-ado`, `vote-ado`, and `get-comments-ado` stay separate.
- **Branch-model semantics.** This spec does not revisit branch naming, plan-generation rules, merge-group ancestry, or manifest semantics.
- **Reviewer / label assignment.** I did not find reviewer/label-setting logic in the audited matrix verbs; that policy remains above the CLI in workflow/policy space.
- **Delete implementation details.** The public grammar is reserved here; the actual delete handlers can land with whichever branch-deletion work next owns that behavior.

---

## Open questions for you

1. **Shim window:** I assumed two releases because downstream direct usage is unverifiable.
2. **`branch delete` timing:** define the surface now and implement later, or require the first delete handler in the same change set?
3. **Flag normalization:** new verbs on canonical flags immediately, or defer that part to AB#3259?

---

## Appendix

Matrix candidates audited: `create-feature-pr`, `create-feature-ado`, `open-impl-pr`, `open-impl-ado`, `open-mg-pr`, `open-mg-ado`, `open-plan-pr`, `open-plan-ado`, `open-evidence-pr`, `open-evidence-ado`, `merge-impl-pr`, `merge-impl-ado`, `merge-mg-pr`, `merge-mg-ado`, `merge-plan-pr`, `merge-plan-ado`, `merge-evidence-pr`, `merge-evidence-ado`, `merge-feature-ado`, `poll-status`, `poll-status-ado`, `ensure-feature`, `ensure-mg`, `ensure-impl`, `ensure-plan`, `ensure-evidence-branch`.

Outliers audited: `assert-impl-pr-coverage`, `check-evidence-floor`, `get-comments-ado`, `post-comment-ado`, `validate-plan-diff`, `vote-ado`, `assert-on-impl`, `check-deps`, `clear-impl-merged`, `close-scope`, `load-tree`, `mark-impl-merged`, `next-impl`, `route`.
