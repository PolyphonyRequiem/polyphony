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

**Next moves:**
- Confirm Phase 2 retrofit can proceed once Beethoven questions resolved
- Add `domain signal`, `gate compression`, `CTA`, `correlation ID`, `disposition (signal)` to glossary (P1)
