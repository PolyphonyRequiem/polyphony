# Project Context

- **Owner:** Daniel Green
- **Project:** Polyphony — type-agnostic SDLC routing engine and conductor workflow suite.
- **Stack:** C# (.NET 11), conductor YAML, PowerShell 7+, Python harness, twig CLI.
- **Created:** 2026-05-28

## Learnings

- 📌 Team formed 2026-05-28. My seat: Mission Keeper.
- 📌 Mission: "human-assisted automated SDLC." The engine is automated; humans gate the judgment-heavy steps. NOT "fully autonomous SDLC" and NOT "AI-assisted human SDLC" — the asymmetry matters.
- 📌 Current human gates in workflow YAMLs: `pending_review_gate`, `scope_violation_gate`, root-fallback gate, stuck-review-timeout, renegotiation flow, dogfood-recovery reset. Any new gate must justify what judgment can only happen at that point.
- 📌 The work hierarchy in `docs/projects/` (du-preview-adoption, polyphony-core-engine, polyphony-health-command, polyphony-self-contained-orchestration, type-agnostic-sdlc, validation-testing, workflow-vocabulary-cleanup, workflow-yaml-refactoring) is the active plan surface. Anything not traced to one of these is scope creep until proven otherwise.
- 📌 2026-05-28: Participated in squad-wide initial concerns review (10-agent fan-out). Surfaced 3 top mission concerns: deterministic error handling wasting attention, Phase 5 pursuing DU adoption vs consumability, incomplete policy layer. Nominated short-term wins: migrate 19 error gates to conductor on_error (AB#3257), sequence self-contained orchestration phase.
- 📌 2026-05-28: Daniel resolved north-star sequencing: polyphony-self-contained-orchestration is the mission outcome; DU adoption + hygiene ride in-situ within scoped work, not as a competing thrust.
- 📌 2026-05-28: Created GitHub epic/issue backlog on PolyphonyRequiem/polyphony (issues #521–530). Epic 0 (#521) = mission north star; Epic A (#522) = short-term wins umbrella; Epics B–E (#523–526) = supporting investments; Issues #527–530 = 4 child short-term wins.
- 📌 gh label create errors on existing labels but exits non-zero — safe to ignore and continue. All 15 squad:* labels were net-new; none pre-existed.
- 📌 `gh issue create --body-file` is required on PowerShell; `--body` mangles multiline markdown (backtick escaping, here-string boundary collisions). Always write to a .md file first.
- 📌 Epic-vs-issue distinction: epics are tracking issues (no implementation work directly), owned by a phase/outcome; child issues carry the actual acceptance criteria, owner, and file anchors. Epic 0 references B–E by number; Epic A references #527–530 as a checklist.
- 📌 In-situ directive framing in Epic 0: DU + hygiene are NOT a separate thrust — they are a standing clause on every implementation spawn. Epic 0 body carries this explicitly; all 4 child short-term issues repeat the clause verbatim.
- 📌 2026-05-28: Created epic + issue structure (issues #521–#530) for the 2026-05-28 squad-wide initial concerns fan-out. Embedded the north-star directive (self-contained orchestration > Phase 5 DU adoption) in Epic 0 and all 4 short-term child issues. Implementation round 1 coordinated.

## Learnings — 2026-05-28

### Beethoven (Mission Keeper)

**Current focus:** Gate disposition review for error-routing retrofit  
**Status:** Completed review of 3 unilateral disposition changes in PR #535  

**Session round outcomes:**
- ✅ Reviewed 3 gate dispositions: seeder (❌ request change), classify (⚠️ approve with note), evidence (❌ partial request change)
- ✅ Documented decision rationale and blast radius analysis
- ✅ Posted PR #535 comment with detailed feedback
- **Open items:** Awaiting Daniel answers on seeder partial-seed safety + workflow_abandoned semantics

**Default recommendations on re-framed questions:**
1. **Seeder error gate (Q1):** Keep the human gate. Partial seeds are a silent-failure risk; the idempotent seeder is safe to retry. The gate friction is justified.
2. **workflow_abandoned side-effects (Q2):** Conductor-state-only (no ADO writes in the terminal itself). The parent workflow (root-item-dispatch) owns any state transitions. An abandoned workflow is reversible.

**Next moves:**
- Confirm Phase 2 retrofit can proceed once Beethoven questions resolved
- Add `domain signal`, `gate compression`, `CTA`, `correlation ID`, `disposition (signal)` to glossary (P1)

### 2026-05-28T23:43-14Z — Inbox round (Scribe merge)
- ✅ PR #535 gate disposition questions re-framed with defaults (captured at `.squad/handoffs/beethoven-pr535-questions-restated.md`)
- ✅ Awaiting Daniel's one-click decisions on both questions
- Ready for Phase 2 retry retrofit planning

## Learnings — 2026-05-31

### 2026-05-31T10:51-07:00 — Conductor gaps report (mission lens)

**Task:** Identify top conductor gaps that are bending polyphony away from its "human-assisted automated SDLC" mission.

**Findings (ranked by mission impact):**

1. **Gap 1 — No `on_error:` routing (Impact 5/5):** Conductor's absence of native error routing is the single largest mission-bending gap. It forces 19 `*_error_gate` human gates across 6 workflows where no judgment is required — operators clicking Retry/Abort on transient network failures. This violates §5.2 (gates exist at inflection/surrender/attestation only) and calcifies the routing-style envelope discipline (~50 verb implementations). Tracked as AB#3257 with RFC Phase 2 pending.

2. **Gap 2 — No agent context injection (Impact 4/5):** Conductor agent steps have no `skills:`, `mcps:`, or `prompt_addendum:` fields. Polyphony built `compose_addendum` + `guidance_loader` as pre-agent pipeline steps to compensate — this is polyphony absorbing orchestration engine behavior, directly violating north-star §4.1 ("No orchestration runtime inside polyphony"). Agent capability binding is advisory (prompt text), not enforced (runtime connection).

3. **Gap 3 — No `type: emit` (Impact 4/5):** Conductor lacks a first-class non-blocking event emission primitive. Polyphony uses `type: notification` as a stopgap with 4 rename TODOs open. Without clean domain-signal emission, §5.6 observability is built on a workaround. No observability → trust ramp stalls → operators can't safely move surfaces from `manual` to `warning`/`auto`. Conductor PR #213 cherry-pick is pending.

4. **Gap 4 — No workflow checkpoint (Impact 3/5):** Conductor doesn't checkpoint step results. Resume re-runs from entry point; every idempotent verb compensates. Polyphony's P3 discipline (idempotency on every verb) is impressive but is engine-durability work absorbed into the CLI contract. Long runs re-run completed agent steps on resume.

5. **Gap 5 — No `for_each` output aggregation (Impact 3/5):** Conductor's for_each can't aggregate sub-workflow outputs natively. The `aggregate_renegotiation` PowerShell script in root-batch-dispatch.yaml is engine behavior absorbed into a script. Silent aggregation failure = missed surrender gate (renegotiation bubble-up drops silently).

**Fork verdict:** No. The three highest-impact gaps have pending upstream fixes (AB#3257 RFC, conductor PR #213 cherry-pick, agent-context injection is a schema extension). Forking fragments the YAML contract without benefit. The real risk is contribution delay — each week without upstream progress, the workarounds calcify deeper into polyphony's CLI and workflow contract.

**Deliverable:** `.squad/handoffs/beethoven-conductor-gaps-20260531.md`

---

## Learnings — 2026-05-29

### 2026-05-29T09:29:44-07:00 — workflow_abandoned trigger analysis

**`workflow_abandoned` routes — verified from `actionable.yaml`:**
- There are exactly **4 routes** to `workflow_abandoned`, ALL are human-gate choices — no automatic routing exists.
  1. `floor_failed_gate` → abort: passes_floor==false AND operator clicks Abort
  2. `revise_loop_gate` → abandon: reviewer requests_changes AND operator clicks Abandon
  3. `human_satisfaction_gate` → abandoned: human executor, operator clicks Abandoned
  4. `workflow_error_gate` → abandon: any upstream error AND operator clicks Abandon (don't retry)

**Prior claim on reversibility — CONFIRMED CORRECT:**
- `root-item-dispatch.yaml` does NOT write to ADO when actionable exits via `workflow_abandoned`.
- The actionable node routes unconditionally to `teardown_worktree` → `dispatched` terminal (pure JSON emit, no twig/polyphony calls).
- The only ADO-writing terminal in root-item-dispatch is `satisfied` (terminal-satisfied leg), reached only via `lifecycle_workflow == 'terminal-satisfied'` — entirely separate from the actionable dispatch path.

**Key correction to mental model:**
- "abandoned" currently conflates two semantically different things: volitional operator withdrawal (Routes A/B/C) and error-then-no-retry (Route D). Route D is the one to watch — it looks like abandonment but may be a transient infrastructure failure.
- When the AB#3257 error-handling retrofit lands, Route D is the candidate to split into `abandoned_on_error` for better batch-aggregator observability.
