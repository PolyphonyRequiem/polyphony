# on_error Migration Inventory

**Status:** Migration complete as of #528; conductor on_error: retry support pending AB#3257 Phase 1
**Owner:** polyphony-internal architecture
**Work item:** AB#3257 — Failure-mode gate elimination
**Companion:** [conductor on_error brief](https://github.com/PolyphonyRequiem/conductor/blob/02eace858dbaf2d1d22598a6e7debdf2d4c8a439/docs/projects/error-routing/on-error-routing.brainstorm.md)
**Generated:** 2026-05-21 by `on-error-inventory` background explorer agent

## TL;DR

**19 `*_error_gate` nodes across 6 workflows. All 19 are "trivial".** Zero
routers, zero policy gates. Every gate is a direct retry/abort (sometimes
retry/continue/abort) on the failing step.

This is a much cleaner migration than the AB#3257 description anticipated
(~40 gates expected; actual is ~half that, and 100% can be replaced by
direct `on_error:` declarations on the failing step). No shared
`error-router.yaml` subworkflow is needed.

## Summary

| Category | Count | % | Migration shape |
|---|---:|---:|---|
| **Trivial** | 19 | 100% | `on_error: { to: <existing destination> }` on the failing step |
| **Router** | 0 | 0% | (none) |
| **Policy** | 0 | 0% | (none) |

## Per-workflow detail

### `actionable.yaml`
| Gate | Lines | Parent step(s) | Routes | Category | Notes |
|---|---:|---|---|---|---|
| `workflow_error_gate` | 870-918 | executor_router, ensure_evidence_branch, compose_addendum, open_evidence_pr, evidence_floor_check, evidence_reviewer, merge_evidence_pr | bare → executor_router; retry / abandon | trivial | Catch-all human gate; prompt is stage-aware, but routing is not |

### `ado-pr.yaml`
| Gate | Lines | Parent step(s) | Routes | Category | Notes |
|---|---:|---|---|---|---|
| `poll_error_gate` | 505-531 | poll_status | bare → poll_status; abort → abort_run | trivial | Single-shot poll retry |

### `github-pr.yaml`
| Gate | Lines | Parent step(s) | Routes | Category | Notes |
|---|---:|---|---|---|---|
| `poll_error_gate` | 406-429 | poll_status | bare → poll_status; abort → abort_run | trivial | Single-shot poll retry |

### `implement-merge-group.yaml`
| Gate | Lines | Parent step(s) | Routes | Category | Notes |
|---|---:|---|---|---|---|
| `root_router_error_gate` | 1250-1277 | root_router | bare → root_router; abort → abort_run | trivial | Prompt references AB#3126 but routing is plain |
| `squash_coverage_error_gate` | 1032-1060 | assert_impl_pr_coverage | bare → assert_impl_pr_coverage; abort → abort_run | trivial | Tool-failure gate |

### `plan-level.yaml`
| Gate | Lines | Parent step(s) | Routes | Category | Notes |
|---|---:|---|---|---|---|
| `root_resolver_error_gate` | 346-364 | root_resolver | bare → abort_run | trivial | Abort-only |
| `type_loader_error_gate` | 384-402 | type_loader | bare → abort_run | trivial | Abort-only |
| `ancestor_chain_error_gate` | 428-450 | ancestor_chain | bare → abort_run | trivial | Abort-only |
| `state_detector_error_gate` | 518-538 | state_detector | bare → abort_run | trivial | Abort-only |
| `write_plan_error_gate` | 1015-1037 | write_plan | retry → write_plan; abort → abort_run | trivial | Idempotent retry |
| `ensure_plan_branch_error_gate` | 1069-1092 | ensure_plan_branch | retry → ensure_plan_branch; abort → abort_run | trivial | Idempotent retry |
| `commit_and_push_error_gate` | 1131-1156 | commit_and_push | retry → commit_and_push; abort → abort_run | trivial | Idempotent retry |
| `open_plan_pr_error_gate` | 1191-1220 | open_plan_pr | retry → open_plan_pr; abort → abort_run | trivial | Idempotent retry |
| `poll_error_gate` | 1506-1529 | poll_status | retry → pr_poll_platform_router; abort → abort_run | trivial | Platform-aware re-poll via router, but gate itself is plain |
| `merge_error_gate` | 2448-2539 | merge_plan_pr | retry → merge_plan_pr; abort → abort_run | trivial | Cause-aware prompt only; no routing by error code |
| `seeder_error_gate` | 2604-2658 | seeder | retry → seeder; continue → child_router; abort → abort_run | trivial | Partial-seed continuation is unusual but not type-discriminated |
| `open_plan_pr_ado_error_gate` | 2808-2837 | open_plan_pr_ado | retry → open_plan_pr_ado; abort → abort_run | trivial | Idempotent retry |
| `merge_plan_pr_ado_error_gate` | 3008-3034 | merge_plan_pr_ado | retry → merge_plan_pr_ado; abort → abort_run | trivial | Idempotent retry |

### `restack-remedy.yaml`
| Gate | Lines | Parent step(s) | Routes | Category | Notes |
|---|---:|---|---|---|---|
| `classify_error_gate` | 112-139 | classify | retry → classify; skip → $end | trivial | Verb error only; not a router |

## Cross-cutting findings

- **Dominant pattern (15/19):** "retry the failing verb; abort otherwise".
  Direct candidate for `on_error: { retry: { max: N }, on_giveup: { to:
  abort_run } }` (assuming the conductor brief's `retry` shape lands).
- **Pure-abort pattern (4/19):** plan-level early-phase gates that just
  funnel any error to `abort_run`. Direct candidate for `on_error: { to:
  abort_run }`.
- **Shared abort destination:** every gate funnels to `abort_run`. There
  may be value in a workflow-default `on_error: { to: abort_run }` if the
  conductor brief supports it (gates would only need to override for
  non-abort behavior).
- **Zero error-type discrimination.** Not a single gate branches on error
  code, message pattern, or type. This is either (a) excellent — the
  failure model is genuinely uniform — or (b) a sign that we've been
  losing information that could have been routed. Worth surveying again
  *after* `on_error` lands; we may discover that some retry/abort
  decisions belong to error-code discrimination.
- **`workflow_error_gate` in actionable.yaml is the broadest** — one gate
  attached to 7 different parent steps. Migration here is per-step (each
  parent step gets its own `on_error:` declaration), but the destination
  is the same.

## Recommendations

### Migration order (smallest blast radius first)

1. **`poll_error_gate`** (ado-pr.yaml + github-pr.yaml) — identical shape
   in both PR platforms; do them together as one PR.
2. **Pure-abort gates in plan-level.yaml** (root_resolver, type_loader,
   ancestor_chain, state_detector) — one `on_error: { to: abort_run }`
   each; trivial mechanical change.
3. **Idempotent-retry gates in plan-level.yaml** (write_plan,
   ensure_plan_branch, commit_and_push, open_plan_pr,
   open_plan_pr_ado, merge_plan_pr_ado) — uniform shape.
4. **`merge_error_gate`** — preserve the cause-aware prompt as a comment
   on the `on_error` block.
5. **`seeder_error_gate`** — the only `continue`-shaped one; needs
   conductor-brief support for "on error, continue to a different step
   instead of retrying".
6. **`workflow_error_gate`** (actionable.yaml) — broadest blast radius;
   migrate last after the pattern is well-established.

### Estimated savings

- **~19 gate nodes deleted.**
- **~6 workflow files touched.**
- **~600-900 lines of YAML removed** (gates are typically 20-50 lines
  each including the prompt text).

### Blockers

None evident. All 19 gates have direct `on_error` translations once the
upstream conductor brief lands its `to` / `retry` actions. No CLI verb
changes are required.

### Worth investigating after on_error lands

- Do any retry gates need typed error envelopes (`error_code` field on
  the verb's failure output) to enable smarter retry decisions? Today
  they don't, but the absence may reflect missing capability rather than
  satisfied requirements.
- Could `workflow_error_gate` (the actionable catch-all) be replaced by
  a workflow-default `on_error` if conductor supports it?

## Method

Generated by an `explore` agent reading every YAML under
`.conductor/registry/workflows/*.yaml` and classifying each `*_error_gate`
node by route shape. Read-only; no source modifications. Line numbers
are post-W1 (PR #500) and may shift as the workflow YAMLs evolve.
