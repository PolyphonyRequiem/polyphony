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
