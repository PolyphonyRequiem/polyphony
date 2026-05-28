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
