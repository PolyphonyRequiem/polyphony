# Scope renegotiation: HTML-comment fence + four-cell verdict matrix

**Status:** accepted (Phase 3 PR P8 — mechanics-only)
**Date:** 2026-05
**Supersedes:** —
**Superseded by:** —

## Context

Child plan PRs sometimes need to ask the parent to change. The existing
`polyphony pr validate-plan-diff` verb (Phase 3 wave 1) already handles
that for the **plan-tree** case: it walks the plan-document hierarchy,
classifies the diff against parent/ancestor paths it derived from a
work-item id, and reads a YAML front-matter `requests_parent_change`
declaration from the plan document itself.

Phase 3 PR P8 needs the same idea for a **plan-PR** case where the
boundary is *not* the plan tree:

- The workflow knows up-front the **glob set** of paths this PR is
  allowed to touch (e.g., `plans/1100/1101.md`, `plans/1100/notes/**`).
- The "I'm asking the parent to change" declaration lives in the **PR
  body**, not in any tracked file — front-matter is unsuitable because
  the body is the agent-authored prose surface.
- The downstream consumer of the renegotiation reason is a sibling
  agent prompt (re-plan the parent), not a doc that needs `---`-style
  separators.

We deliberately ship two `validate-*` verbs rather than overloading one,
because the inputs (plan tree vs. arbitrary globs) and the declaration
sites (front-matter vs. PR body) are genuinely different and consumed
by different workflow handlers.

## Decision

Add two routing-style verbs:

1. **`polyphony plan extract-renegotiation-flag --pr <n>`**
   - Reads the PR body via `gh pr view --json body`.
   - Extracts every `<!-- polyphony:requests-parent-change -->...<!-- /polyphony:requests-parent-change -->` block.
   - Concatenates inner text (multi-block separator: one blank line).
   - Reports `Absent` / `Present` / `Malformed` (open without close).

2. **`polyphony plan validate-scope --pr <n> --child-scope "<glob>,<glob>"`**
   - Pulls flag (call 1) and changed files (call 2: `--json files`).
   - Classifies each touched path as in-scope or out-of-scope using
     posix glob semantics (`*` segment-bound, `**` crosses).
   - Emits the four-cell verdict matrix:

     | files                | flag absent              | flag present              |
     |----------------------|--------------------------|---------------------------|
     | in-scope only        | `allow`                  | `allow` + warning         |
     | out-of-scope present | `block`                  | `allow_renegotiation`     |

The HTML-comment fence convention mirrors `Polyphony.Guidance.GuidanceExtractor`
(PR #133's `<!-- polyphony:guidance --> ... <!-- /polyphony:guidance -->`).
Consistency across user-authored declarations matters more than picking
the "best" syntax for each case in isolation.

We use a custom posix-style glob matcher rather than `FileSystemName.MatchesSimpleExpression`
because the latter treats `*` as crossing path separators, which would
silently widen scope and defeat the point of the verb.

## Consequences

- **Mechanics only here.** This PR ships the verbs, JSON contracts, and
  tests. The workflow handler that consumes the JSON envelopes (and
  decides what to do on `block` / `allow_renegotiation`) ships in a
  follow-up PR (`p3-renegotiation-handler`); `plan-level.yaml` is
  intentionally unchanged in this PR.
- The fenced convention is now a third entry alongside `polyphony:guidance`
  (PR #133) and the existing front-matter `requests_parent_change`
  (`pr validate-plan-diff`). Future user-facing flags should reuse the
  fence pattern, not invent a fourth.
- The "block on out-of-scope files without flag" half-cell is the only
  hard gate; the other three cells either allow or allow-with-warning,
  preserving the agent's autonomy to declare its intent.
