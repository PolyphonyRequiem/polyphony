# Project Context

- **Owner:** Daniel Green
- **Project:** Polyphony — type-agnostic SDLC routing engine and conductor workflow suite.
- **Stack:** C# (.NET 11), conductor YAML, PowerShell 7+, Python harness, twig CLI.
- **Created:** 2026-05-28

## Learnings

- 📌 Team formed 2026-05-28. My seat: AI Agents Expert.
- 📌 Agent addendum verb: `polyphony agent compose-addendum <work_item_id>`. Canonical wiring in `.conductor/registry/workflows/actionable.yaml`. Composes facet profiles + per-item guidance.
- 📌 Facet profiles (from `process-config.yaml`): implementable→`{git, build, test}`; actionable→`{telemetry, prod-deploy, teams-comments, service-health}`; plannable→`{search, web, work-item-tree}`.
- 📌 Composer behavior: skills + MCPs deduped and sorted ascending under ordinal comparer; guidance flows verbatim. Identical-value collisions across facets dedupe silently. Cross-facet name typos caught by V-20 family validators at config-load time.
- 📌 Implementation: `Polyphony.Sdlc.FacetProfileComposer.Compose(facets, profiles, perItemGuidance)`.
- 📌 Agent failure mode catalog: `docs/polyphony-agent-failure-modes.md` — 6 documented modes; § 6 covers the `scope_removed: Removed` latent bug.
- 📌 2026-05-28: Participated in squad-wide initial concerns review (10-agent fan-out). Surfaced 3 top agent-design concerns: missing guidance files for non-architect/coder/reviewer roles, facet-profile type-distribution may be too restrictive, agent prompts lack explicit stopping conditions. Nominated short-term wins: create per-type guidance refinements (architect/task.md, coder/issue.md), add explicit stopping conditions to actionable_agent.
