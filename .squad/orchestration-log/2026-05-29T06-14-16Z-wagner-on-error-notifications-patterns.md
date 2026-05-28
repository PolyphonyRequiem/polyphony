# Orchestration Log: wagner-on-error-notifications-patterns

**Agent:** Wagner (Workflow Author)  
**Timestamp:** 2026-05-29T06:14:16Z  
**Model:** sonnet-4.6  
**Duration:** ~2024s (extended via write_agent across ~3 turns)

## Task

Forward-design concrete polyphony workflow patterns enabled by on_error + notifications.

## Outcomes

- ✅ Created issues: #543 notify_then_route, #544 decision_point_notification, #545 progress_notification
- ✅ Added "M11: on_error + Notifications Patterns" section to conductor-mechanics SKILL.md
- ✅ Posted forward-pointer comment on #536
- ✅ Wrote findings document: `.squad/decisions/inbox/wagner-on-error-notifications-patterns-2026-05-28T23-00-46Z.md`

## 8 Concrete Patterns

1. **`notify_then_route`** — Error observability at failure boundary (Phase 1)
2. **`decision_point_notification`** — Auditable auto-dispositions (Phase 1)
3. **`progress_notification`** — Long-running op observability (Phase 1)
4. **`bounded_retry_loop`** — Soft retry without `retry:` action (Phase 1 stopgap)
5. **`escalation_chain`** — Typed error recovery with fallback (Phase 1 + typed helpers)
6. **`renegotiation_notification`** — Parent cascade observability (Phase 1)
7. **`async_gate_prompt`** — Notify + human gate with action URL (Phase 1)
8. **`gate_disposition_policy`** — Declared override point (Phase 1.5 concept)

## Envelope field requirements surfaced for Bach

| Field | Pattern | Priority |
|-------|---------|----------|
| `severity` enum | 1, 2, 3, 5 | P1 |
| `{{ conductor.run_id }}` built-in | 7 | P1 (workaround: thread as workflow input) |
| `type: wait` templated `seconds:` | 5 | P1 (fallback: fixed backoff or `Start-Sleep`) |

## Ranked usefulness (top 5)

1. `notify_then_route` — applies to ALL 19 retrofit gates
2. `decision_point_notification` — resolves Beethoven audit gap
3. `progress_notification` — dramatic plan-level observability win
4. `escalation_chain` — cleanest error recovery for infrastructure scripts
5. `bounded_retry_loop` — Phase 1 stopgap for 2-3 critical gates only

## Dependencies

- Cross-agent with: Bach (envelope schema), Mahler (conductor feature verification)
- Feeds into: #536 Phase 2 implementation
