# Project Context

- **Owner:** Daniel Green
- **Project:** Polyphony — type-agnostic SDLC routing engine and conductor workflow suite.
- **Stack:** C# (.NET 11), conductor YAML, PowerShell 7+, Python harness, twig CLI.
- **Created:** 2026-05-28

## Learnings

- 📌 Team formed 2026-05-28. My seat: Workflow Author.
- 📌 Driver split: `polyphony.yaml` (outer dispatch loop) → `root-batch-dispatch.yaml` (per-batch for_each fan-out + batch integrator) → `root-item-dispatch.yaml` (per-item: classify → spawn worktree → lifecycle → teardown). Forced by conductor's `for_each` constraint.
- 📌 Sub-workflow library: `plan-level`, `actionable`, `implement-merge-group`, `implement-mg`, `feature-pr`, `github-pr`, `ado-pr`, `close-out`.
- 📌 PR platform abstraction is YAML-level, NOT C#. `pr_platform_router` inline pwsh in `feature-pr.yaml:98-111` and `implement-merge-group.yaml:697-710`. Dispatches to `github-pr.yaml` (Opus reviewer + Sonnet fixer loop + merger agent) or `ado-pr.yaml` (stub + human gate). Both share input/output schema (`pr_number`, `branch_name`, `target_branch`, `review_policy`, `platform` → `merged`, `pr_url`).
- 📌 CORRECTION (from memory): `feature-pr.yaml` DOES use the `pr_platform_router → pr_lifecycle_{github,ado}` abstraction — same pattern as `implement-pg.yaml`. Verify against YAML, not against outdated skill docs.
- 📌 Bubble-up output vs Promote: bubble-up = workflow data flow (sub→parent via `output:`); promote = git merge (head→base). Don't conflate. Authoritative source: conductor-mechanics M7. `plan-level.yaml` exports bubble-up outputs from the renegotiation handler: `renegotiation_pending`, `renegotiation_request`, `validate_scope_verdict`, `scope_violation_files`.
- 📌 Three-vocabulary rule: keep `events` / `state names` / `categories` separated. Never mix in a single route condition.
- 📌 2026-05-28: Participated in squad-wide initial concerns review (10-agent fan-out). Surfaced 3 top workflow-YAML concerns: `github-pr.yaml` output map missing `already_merged_emitter` branch, `close-out.yaml` uses `| json` (not `| tojson`), partial bubble-up of renegotiation scope-violation fields. Nominated short-term wins: patch github-pr.yaml to add already_merged_emitter branch, fix close-out.yaml `| json` → `| tojson`.
- 📌 2026-05-28: Implemented #528 — removed all 19 trivial human_gate error-interrupt nodes across 6 workflow files (ado-pr, github-pr, restack-remedy, implement-merge-group, actionable, plan-level). PR #535.
- 📌 conductor v0.1.18 does NOT ship `on_error:` in route entries — it is an unmerged RFC. The field `on_error:` in routes is rejected with "Extra inputs are not permitted". Migration was implemented as direct routing today; TODO comments mark sites for AB#3257 retrofit.
- 📌 Polyphony CLI verbs exit 0 even on semantic error — they write `{error: "..."}` to stdout JSON. This means conductor's future `on_error:` routing would not fire for polyphony verb failures unless the verbs ALSO write to `$env:CONDUCTOR_ERROR_OUT`. The issue's "no CLI verb changes required" refers to C# source, not this concern.
- 📌 seeder_error_gate: auto-continue to child_router is safest default (seeder errors are typically partial; child_router processes whatever children loaded). classify_error_gate (restack-remedy): auto-skip to $end is safest (can't restack without classify output).
- 📌 plan-level.yaml has a pre-existing conductor validate FAIL: circular sub-workflow self-reference (plan_one_child for_each references plan-level.yaml recursively). Identical on main branch — not introduced by #528.
- 📌 2026-05-28: Participated in implementation round 1 — shipped PR #535 on issue #528 (short-term win).
