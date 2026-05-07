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

---

## Handler design — workflow output bubble-up

**Status:** accepted (Phase 3 PR P8c — handler wiring)
**Date:** 2026-05

The mechanics PR (P8) shipped the verbs but left
`.conductor/registry/workflows/plan-level.yaml` unchanged. P8c wires
those envelopes into plan-level so the workflow can act on a
`block` verdict and bubble the renegotiation flag up to its caller.

### The signaling question

A child plan PR can now declare "the parent must change" via the
`<!-- polyphony:requests-parent-change -->` fence. After the child's
plan PR merges, *something* has to tell the parent's plan workflow
that re-planning is required. Three options were considered:

- **(A) Twig field write.** Stamp a `renegotiation_pending: true` +
  `renegotiation_request: <text>` into a configurable twig field on
  the parent work item. Parent's plan workflow polls/checks this field
  on entry. Pros: persistent across sub-workflow boundaries; observable
  in ADO. Cons: requires a new twig-field write capability and a
  matching field on every process template; introduces a side-channel
  the workflow runtime cannot see.
- **(B) Conductor signal/event.** Use a conductor primitive for
  child→parent communication. Pros: native to conductor. Cons: today
  conductor exposes no such signaling primitive — sub-workflows are
  invoked synchronously, return their `output:` map, and that is the
  only edge the parent reads. Building a new primitive is well outside
  the scope of one handler PR.
- **(C) Workflow output bubble-up.** Make plan-level.yaml's top-level
  `output:` map include `renegotiation_pending`,
  `renegotiation_request`, `validate_scope_verdict`, and
  `scope_violation_files`. Whoever invokes plan-level (eventually
  apex-driver.yaml; today nothing) reads those outputs and decides
  whether to re-enter parent planning. Pros: clean boundary, no
  side-channel, matches the existing M7 contract that the workflow's
  top-level `output:` map IS the sub-workflow's public API. Cons:
  today plan-level has no caller — the signal goes nowhere until
  apex-driver lands.

### Decision: (C) workflow output bubble-up

We pick (C). Reasons:

1. **Boundary discipline.** The conductor `output:` map is already the
   only contract a parent workflow can read from a sub-workflow
   (`conductor-mechanics` M7). Reusing that contract for the
   renegotiation signal keeps the data flow explicit — there is no
   "magic field somewhere in twig" the next reader has to know about.
2. **No new primitive required.** Both (A) and (B) require new
   infrastructure (twig field schema + write verb; or a conductor
   signaling primitive). (C) requires only the four lines we add to
   the existing top-level `output:` map.
3. **Matches the conductor design principle of explicit data flow
   over implicit state.** The renegotiation signal is data. It belongs
   on the data path.
4. **(A) is not foreclosed if persistence is later required.** If
   apex-driver eventually needs the renegotiation request to survive
   workflow re-entry across sessions, apex-driver can write the
   bubbled output to twig itself with one extra `twig patch` step.
   That is apex-driver's choice, not plan-level's; plan-level stays
   side-effect-free with respect to the work-item record.

### What plan-level.yaml ships in this PR

- New input `child_scope_globs` (comma-separated string, default `""`).
  When empty (today's only callers), `validate_scope` is skipped and
  the workflow merges as before. When set (apex-driver, future), the
  validate_scope + scope_violation_gate path is active.
- New script agent `validate_scope` invoked post-review, pre-merge on
  the github leg via `polyphony plan validate-scope --child-scope`.
  Routes `block` verdicts to `scope_violation_gate`; routes everything
  else (including `allow` + `flag_without_parent_files` warning)
  straight to `merge_plan_pr`.
- New human gate `scope_violation_gate` with two options: `override`
  (route to `merge_plan_pr`) and `abort` (route to `$end`). The gate
  prompt enumerates the out-of-scope files and the authorized scope
  globs so the operator can decide.
- New script agent `extract_renegotiation_flag` invoked post-merge on
  the github leg via `polyphony plan extract-renegotiation-flag`.
  Always runs (independent of whether scope was supplied) so the
  workflow's `renegotiation_pending` output is always populated.
- Top-level `output:` map exporting `renegotiation_pending`,
  `renegotiation_request`, `validate_scope_verdict`, and
  `scope_violation_files`. Booleans are coerced via
  `| string | lower` per M7's auto-coercion footgun. List output uses
  `| tojson` so the parent receives a real list rather than the
  default `str(list)` representation.

### What apex-driver will do (downstream PR)

When apex-driver lands, its plan-PR sub-step invokes plan-level and
reads:

```jinja
{% if plan_level.output.renegotiation_pending %}
  ...bump parent's plan_generation, re-enter parent plan-level with
  intent=redo and the renegotiation_request threaded into the
  architect's prompt addendum...
{% elif plan_level.output.validate_scope_verdict == 'block' %}
  ...the operator chose abort at scope_violation_gate; surface as a
  failed child planning run for the apex backlog...
{% else %}
  ...routine merge; carry on with sibling children...
{% endif %}
```

apex-driver owns the parent re-entry decision. plan-level only
reports facts.

### ADO leg — deferred

Both verbs (`extract-renegotiation-flag`, `validate-scope`) are
github-only today: they consume `gh pr view --json body,files`
internally. The ADO leg of plan-level (`merge_plan_pr_ado` and the
related ADO routing) is intentionally left untouched in this PR. The
workflow output map handles the ADO case by emitting `false` /
`""` / `[]` defaults — the bubble-up contract still holds; it just
always reports "no renegotiation" because the verbs never ran.

ADO sibling verbs (`polyphony plan extract-renegotiation-flag-ado`,
`polyphony plan validate-scope-ado`) are a deferred follow-up. When
they land, the post-`poll_status_ado` and post-`merge_plan_pr_ado`
routes get the same handler wiring as the github leg, and the
workflow output templates extend to read from whichever leg ran.

### Consequences

- plan-level.yaml's `output:` map is now a real public API. Future
  edits must preserve the four bubble-up keys (lint-plan-level.ps1
  enforces this) or break apex-driver when it lands.
- The handler is structurally complete but **operationally inert
  until apex-driver lands**. That is intentional — wiring the verbs
  into plan-level now lets apex-driver be a pure consumer rather than
  having to bundle handler edits with its own scaffold.
- The github-only restriction is documented here AND in the
  `validate_scope` / `extract_renegotiation_flag` agent comments in
  plan-level.yaml. ADO callers receive the bubble-up defaults
  (`false`, `""`, `[]`) and behave as if no renegotiation was
  requested — which matches today's pre-handler behavior, so the ADO
  leg is no worse than before.
