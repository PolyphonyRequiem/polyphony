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
- 📌 2026-05-28: Scoped Phase 2 on_error: retrofit (#536) — 19 gates inventoried, Phase 1-only gates identified (5 gates, 3 files), 14 retry+abort gates blocked on RFC Phase 2 retry: action. Key finding: polyphony CLI verbs exit 0 always; on_error: only fires for infrastructure scripts (git, API). Mixed pattern recommended: success-path routing for polyphony verbs, on_error: for infrastructure. Scope doc: .squad/decisions/inbox/wagner-528-phase-2-scope-2026-05-28T22-38-04Z.md.
- 📌 2026-05-28: Catalogued 8 workflow patterns combining on_error: (Phase 1) + type:notification. Top 5: notify_then_route (#543), decision_point_notification (#544), progress_notification (#545), escalation_chain, bounded_retry_loop. Bach envelope asks: severity field, conductor.run_id built-in, type:wait templated seconds. Patterns doc: .squad/decisions/inbox/wagner-on-error-notifications-patterns-2026-05-28T23-00-46Z.md.
- 📌 type:notification is fire-and-forget and has access to {{ failing_step.error.* }} when placed downstream of an on_error: route. This is the load-bearing insight for notify_then_route.
- 📌 bounded_retry_loop (M10 + type:set + type:wait) is the only retry mechanism in Phase 1, but adds 5 nodes per gate and consumes significant max_iterations budget. Use sparingly; replace with retry: when RFC Phase 2 ships.
- 📌 conductor dogfood combined branch is dogfood/on-error+notifications in C:\Users\dangreen\projects\conductor-notifications.

## Learnings — 2026-05-28 (emit smoke test round)

### Dogfood emit / type:notification smoke test

**Status:** ✅ PASSED — 2026-05-28

**Schema reality (vs M11 design assumptions):**
- The dogfood (cherry-pick of PR #213) uses `type: notification` + `notification: <type_name>` — NOT `type: emit` / `emit: <type_name>`. The reviewer-cleaned commit `27006af` renames those fields, but that commit is NOT what the dogfood cherry-picked. My M11 patterns must use `type: notification` for now.
- Notification payload lands in a **dedicated `.notifications.jsonl`** file, NOT in `.events.jsonl`. The task brief assumed `.events.jsonl` — this is a caveat to propagate. The `.events.jsonl` does contain `notification_started` markers (no payload), and the full payload object is in `.notifications.jsonl`.
- The schema envelope includes: `schema_id` (`<namespace>.<type>@<version>`), `emission_id`, `run_id`, `source_agent`, `subworkflow_path`, `correlation`, `workflow_metadata`, `payload`. Very clean — richer than what I assumed in the pattern designs.
- `correlation:` fields (from `workflow.notifications.correlation:`) are auto-merged onto every notification — zero config per-step.
- `type: script` entry point works perfectly with no LLM; ideal for smoke tests. `pwsh -Command "Write-Output 'ping'"` exits cleanly.

**For Mahler:** Nothing broken. Dogfood install at `C:\Users\dangreen\projects\conductor-dogfood` is at v0.1.17 (pip install -e .). PR #213 cherry-pick is the older naming — if 27006af is re-cherry-picked, the workflow `notification:` / `type: notification` fields would need updating to `emit:` / `type: emit`.

**For Daniel:** Demo is ready. Run: `conductor run .squad\experiments\dogfood-smoke\emit-smoke.yaml` — no LLM budget needed. The proof lives at `.squad/experiments/dogfood-smoke/README.md`.

---

## Learnings — 2026-05-28

### Wagner (Workflow Author)

**Current focus:** on_error: retrofit Phase 2 scope + pattern catalogue  
**Status:** Completed comprehensive scope definition + forward-designed 8 concrete patterns

**Session round outcomes:**
- ✅ Scoped Phase 2 retrofit against Phase 1 API; split into 2a (5 Phase-1 gates) + 2b (14 RFC Phase 2 gates)
- ✅ Created issue #536: Phase 2 retrofit tracker
- ✅ Forward-designed 8 concrete patterns (notify_then_route, decision_point_notification, progress_notification, bounded_retry_loop, escalation_chain, renegotiation_notification, async_gate_prompt, gate_disposition_policy)
- ✅ Created issues #543-#545 for pattern-specific work items
- ✅ Documented 5 asks for conductor (retry action, same-node re-run, implicit opt-in, default on_error, exit-0 + CONDUCTOR_ERROR_OUT contract)
- **Cross-agent:** Coordinated envelope-field asks with Bach; flagged feature verification needs for Mahler

**Next moves:**
- Wait for conductor RFC Phase 2 to merge (blocks 14 gates)
- Prototype gate compression in github-pr.yaml once patterns approved
- Restore retry capability for 14 removed gates via on_error + counter scripts (Phase 2b)

### 2026-05-28T23:43-14Z — Inbox round (Scribe merge)
- ✅ Dogfood emit smoke test PASSED (`.squad/experiments/dogfood-smoke/`)
- ✅ Schema naming finding: dogfood pre-dates PR #213 rename (`type: notification` vs `type: emit`)
- ✅ Output file location finding: `.notifications.jsonl` (separate from `.events.jsonl`)
- Ready for M11 pattern update when PR #213 lands in dogfood
