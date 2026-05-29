# Orchestration Log: Coordinator Directive Capture

**Timestamp:** 2026-05-28T23:43-14Z  
**Source:** Daniel Green (via Copilot CLI)  
**Priority:** P1 (vocabulary + policy)

## Directive 1 — Vocabulary: domain-signal + emit

**Quote:**
> "as for bach's domain signal, yes, I agree domain signal vs notification."

**Rule:** Polyphony-side payload primitive is named **domain signal** (noun). Conductor YAML primitive is `emit:` (per upstream PR #213). Use both terms in tandem: workflows `emit:` a domain signal.

**Application:** All ADRs, M11 SKILL, workflow docs.

**Status:** CAPTURED — apply across all future docs.

---

## Directive 2 — Upstream PR Blocker Policy

**Quote:**
> "Mahler's conflict is out of scope for me, if he needs to raise questions on the PRs, he should do so? not sure it's something we need to bother jason about."

**Rule:** When dogfood rebase hits upstream design conflict:
1. Raise question as comment on upstream PR
2. Stop pursuing local rebase
3. Accept whatever version-pin results

**Rationale:** Avoid escalation to PR author; let upstream maintainers drive decisions.

**Status:** CAPTURED — applies to future conductor PRs and similar upstream conflicts.

---

## Decision Impact

- **PR #229:** Mahler followed directive — raised question on PR, abandoned local rebase, pinned dogfood to v0.1.17.
- **Future PRs:** Standard practice going forward.

## Inbox

- `copilot-directive-2026-05-28T16-43Z.md` (merged to decisions.md)
