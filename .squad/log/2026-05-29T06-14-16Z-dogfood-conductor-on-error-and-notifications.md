# Session Log: dogfood-conductor-on-error-and-notifications

**Date:** 2026-05-28/29  
**Timestamp:** 2026-05-29T06:14:16Z  
**Team:** Beethoven, Wagner, Bach, Mahler  
**Focus:** Conductor Phase 1 (on_error routing) + notifications pattern forward-design

## Summary

This round completed comprehensive scope definition and architecture review for polyphony's adoption of conductor's on_error and notification primitives. Phase 1 APIs are sufficient for 5 pure-abort gates; remaining 14 retry+abort gates block on RFC Phase 2 `retry:` action. Forward-designed 8 concrete workflow patterns (notify_then_route, decision_point_notification, progress_notification, etc.) that unlock operator observability and gate compression. Identified 3 ADRs needed (verb-error-boundary, domain-signal-envelope, gate-compression-pattern) and initiated dogfood environment for validation.

## Open items for Daniel

**BLOCKING (Phase 2 retrofit):**
1. **Beethoven's gate disposition questions (2 items):**
   - Seeder partial-seed safety on catastrophic crash?
   - `workflow_abandoned` ADO side-effect semantics?

2. **Mahler's context.py conflict (1 item):**
   - `agent_outputs.get()` sentinel vs. keep `is_dict_output` init?

**UNBLOCKING (architectural approval):**
3. **Bach's 3 ADRs need Daniel's call:**
   - `polyphony-verb-error-boundary.md` (Option C recommended)
   - `domain-signal-envelope.md` (envelope schema + severity + disposition)
   - `gate-compression-pattern.md` (which gates compress, poll cadence)

**FEATURE VERIFICATION (Mahler):**
4. Verify `{{ conductor.run_id }}` availability in Jinja2 templates
5. Confirm `type: wait` accepts templated `seconds:` field

## Deliverables this round

- ✅ Gate disposition review (Beethoven)
- ✅ Phase 2 retrofit scope + 19-gate inventory (Wagner)
- ✅ 8 concrete workflow patterns (Wagner)
- ✅ Architectural design + platespinner integration (Bach)
- ✅ 3 ADR proposal stubs (Bach)
- ✅ Dogfood environment + adoption survey (Mahler)
- ✅ 5 GitHub issues filed (#536-#540, #541-#545)
- ✅ PR #213 feedback incorporated (rename `notification:` → `emit:`)
- ✅ PR #229 semantic conflict identified (context.py)

## Cross-agent dependencies

- **Wagner↔Bach:** Envelope-field asks (severity, run_id, templated wait.seconds) resolved in coordination
- **Bach↔Mahler:** Feature verification blockers (run_id availability, wait.seconds templating)
- **Wagner↔Mahler:** Pattern validation via dogfood (bounded_retry_loop, escalation_chain)
- **All→Beethoven:** 3 disposition review confirmations needed before Phase 2 retrofit can commit

## Next phase

Pending Daniel's answers to blocking questions: (1) Beethoven dispositions, (2) Mahler conflict resolution, (3) Bach ADR approvals. Once unblocked, Phase 2a retrofit can proceed (5 Phase-1-only gates). Phase 2b (14 retry+abort gates) remains blocked on conductor RFC Phase 2 `retry:` action merger.
