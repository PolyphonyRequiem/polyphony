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
- 📌 2026-05-31: Conducted deep conductor-gap analysis (handoff: `.squad/handoffs/stravinsky-conductor-gaps-20260531.md`). Top 5 gaps in the conductor↔agent contract:
  1. **Output schema not enforced** (highest impact): `output:` blocks are documentation-only; conductor does not validate agent output against declared types/enums/required fields. Silent wrong-routes and infinite revise loops result (e.g. `verdict: "APPROVED"` vs `"approved"` catch-all fires indefinitely).
  2. **Addendum injected as prompt text** (2nd highest): Conductor has no `skills:` / `mcps:` / `prompt_addendum:` fields (confirmed: `actionable.yaml:52-57`). Dynamic addendum MCPs and the static conductor `tools:` list create a dual-list incoherence the agent must manually reconcile. Per-item guidance fights the system prompt with no precedence rule.
  3. **Context window — no conductor help** (medium): On re-invocation, prior plan (full verbatim), research findings, and reviewer feedback accumulate in the rendered prompt with no token-budget awareness, warning, or summarization from the engine. Mid-turn truncation produces missing required fields (e.g. `research_request_kind`) silently.
  4. **Agent failure modes — single floor check only**: `evidence_floor_check` (actionable.yaml:525) is the only mechanical post-agent validator in the suite. All other agents (coder, root_reviewer, architect) have no content-floor check; conductor trusts whatever JSON they return.
  5. **Retry spawns fresh, partial history**: Revise loops inject only the most-recent reviewer comment; feedback accumulation works by overwrite not threading. A reviewer that returns an empty comment field causes the agent to retry with no context about why.
- 📌 2026-05-31: Confirmed `profile.yaml` is still a "reserved placeholder" with no live consumer (`docs/polyphony-conductor-directory.md:278-282`). Still V-14 only.
- 📌 2026-05-31: `evidence_floor_check` pattern (≥1 commit + non-empty PR body) is the correct generalizable pattern for post-agent mechanical validation. Should be replicated for coder, architect write step, and any agent that commits to disk.
- 📌 2026-05-31: Catch-all route convention (M4) is defensive but has a subtle failure: for verdict-style agents (`root_reviewer`, `evidence_reviewer`), the catch-all routes to the "retry" target. An agent that emits empty `{}` or a capitalized enum value gets treated as `changes_requested`, not as a schema error — creating silent infinite loops until `max_iterations` fires.
