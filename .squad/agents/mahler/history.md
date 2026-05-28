# Project Context

- **Owner:** Daniel Green
- **Project:** Polyphony — type-agnostic SDLC routing engine and conductor workflow suite.
- **Stack:** C# (.NET 11), conductor YAML, PowerShell 7+, Python harness, twig CLI.
- **Created:** 2026-05-28

## Learnings

- 📌 Team formed 2026-05-28. My seat: Conductor Expert.
- 📌 Companion skills: `conductor-design` (principles — workflow state/routing/re-entry/human interaction) and `conductor-mechanics` (YAML plumbing — output access, route mechanics, models, cross-platform invocation). Load both when authoring workflows.
- 📌 Polyphony driver split: `polyphony.yaml` (outer dispatch loop) → `root-batch-dispatch.yaml` (per-batch fan-out via for_each + batch integrator) → `root-item-dispatch.yaml` (per-item: classify → spawn worktree → lifecycle → teardown). The three-file split is FORCED by conductor's `for_each` constraint (exactly one thing per iteration). See `docs/decisions/polyphony.md` Q1.
- 📌 Lifecycle classification (Q2) is a SCRIPT, not YAML routing — the rule set ("if next-ready is plan_authored → plan-level; if action_satisfied → actionable; ...") consults `polyphony state next-ready` JSON output.
- 📌 Conductor web dashboard has NO HTTP gate-respond endpoint. Gates can only be answered via dashboard UI (websocket) or process TTY. Verified 2026-05-24 against conductor 0.1.16.
- 📌 2026-05-28: Participated in squad-wide initial concerns review (10-agent fan-out). Surfaced 3 top conductor-mechanics concerns: output-schema contracts undersized at cross-leg boundaries, route conditions doing script work, re-entry stability sound but node-ID pinning brittle. Nominated short-term wins: normalize renegotiation output schema (null vs empty, structured vs JSON-as-string), add dedicated error-aggregation node.
