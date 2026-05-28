# Orchestration Log: wagner-phase-2-scope

**Agent:** Wagner (Workflow Author)  
**Timestamp:** 2026-05-29T06:14:16Z  
**Model:** sonnet-4.6  
**Duration:** ~476s (first turn)

## Task

Scoped Phase 2 of on_error: retrofit against actual Phase 1 API.

## Outcomes

- ✅ Created GitHub issue #536: "Phase 2: on_error: retrofit — restore the 19 gates removed in #535"
- ✅ Updated PR #535 description with Phase 2 follow-up section
- ✅ Wrote findings document: `.squad/decisions/inbox/wagner-528-phase-2-scope-2026-05-28T22-38-04Z.md`

## Key Findings

Phase 2 splits into two sub-phases:
- **Phase 2a (5 gates):** Pure-abort and skip-only gates; Phase 1 API sufficient
- **Phase 2b (14 gates):** Retry+abort gates; blocks on RFC Phase 2 `retry:` action

## Critical cross-cutting gap

**Polyphony CLI verb error emission:** Verbs exit 0 always. For `on_error:` routes to fire, verbs must also write to `$CONDUCTOR_ERROR_OUT` when they fail. Recommendation: Option C (hybrid pattern) — use `on_error:` for infrastructure failures; keep success-path `when:` routing for polyphony verb calls.

## API asks for conductor

1. **`retry:` route action** — 14 gates depend on this
2. **Confirm retry re-runs the same node** — polyphony's preference for simplicity
3. **`internal.script_error` fires on presence of any `on_error:` route** — reduces boilerplate
4. **Workflow-level default `on_error:` target** — nice-to-have for pure-abort gates
5. **Confirm exit-0 + `$CONDUCTOR_ERROR_OUT` write = on_error fires** — path for CLI verb integration

## Dependencies

- Blocked by: conductor RFC Phase 2 merger (for 14 gates)
- Blocks: #536 implementation until RFC Phase 2 ships
