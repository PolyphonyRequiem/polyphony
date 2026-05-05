---
title: "Open Questions Policy Configuration"
type: user-plan
status: draft
intended_consumer: polyphony-conductor-workflows architect agent (plan-level.yaml) via user_plan_path
---

# Open Questions Policy Configuration — User Plan

> This is a **user-authored reference plan** for the architect agent to refine.
> It encodes the design intent, scope boundaries, and open design questions
> already considered. The architect should preserve the design decisions below
> unless they conflict with type constraints, in which case raise as open
> questions per the standard architect contract.

## Problem

`plan-level.yaml` hard-codes the architect's open-questions handling:

- The severity threshold is baked into the route condition
  (`selectattr('severity', 'in', ['moderate', 'major', 'critical'])` —
  see `plan-level.yaml:313-316`).
- The gate is unconditional when ≥1 blocking question is emitted — there is
  no way to express "auto-proceed regardless" or "always gate even on `low`".
- There is no cap on `architect → gate → architect` answer-loop roundtrips,
  so a misbehaving architect that re-raises questions on every revision can
  loop the workflow indefinitely.
- There is no per-scope tuning. An root Epic should probably always gate;
  a leaf Task probably never should — but today they get the same treatment.

Meanwhile `.conductor/policy.yaml` already has the right machinery for this
exact shape — `auto`/`warning`/`manual` modes, per-scope overrides
(`defaults` / `root` / `by_type`), and caps — applied today to two domains
(`approvals`, `pr`). Open-questions is a third gate that begs for the same
treatment.

Live evidence (Epic 2943 first dogfood run): the architect raised four
questions; some were borderline `low` and didn't need to stop the workflow;
the user had no way to express that without editing the workflow YAML.

## Goal

Add `open_questions` as a third policy domain so users can configure
plan-level open-question handling in `.conductor/policy.yaml`, with the
same `auto/warning/manual` × `defaults/root/by_type` shape used by
`approvals` and `pr`.

After this work, `.conductor/policy.yaml` can express things like:

```yaml
open_questions:
  defaults:
    mode: warning
    min_severity: moderate
    max_question_loops: 3
  root:
    mode: manual           # root Epic always gets the gate
  by_type:
    Task:
      mode: auto           # leaf tasks never stop on questions
```

…and `plan-level.yaml` consults the resolved policy at the route
instead of using a hardcoded severity filter.

## Approach

Mirror the existing two-domain pattern exactly. The engine work is small and
self-contained (extend `PolicyConfig`, `PolicyResolver`, `PolicyCommands`,
`PolicyLoader` defaults, the AOT JSON context, and tests). Workflow wiring
rewrites the route block in `plan-level.yaml` to consume
`polyphony policy resolve --domain open_questions --scope …` and gates
accordingly.

### Modes (must match `PolicyMode` enum semantics)

| Mode      | Behavior                                                        |
|-----------|-----------------------------------------------------------------|
| `auto`    | Never gate. Architect still emits `open_questions` (logged in plan as Assumptions/Risks per existing prompt instructions); workflow proceeds straight to review. |
| `warning` | (default) Gate when ≥1 question has severity ≥ `min_severity`; otherwise proceed. Today's behavior with `min_severity = moderate`. |
| `manual`  | Always gate if `open_questions` is non-empty, regardless of severity. |

### Tunables on the rule

- `min_severity`: `low` \| `moderate` \| `major` \| `critical` — default
  `moderate`. Used by `warning` mode. `auto`/`manual` ignore it.
- `max_question_loops`: int — default `3`. Caps `architect → gate → architect`
  roundtrips. At the cap the gate auto-proceeds (with a notice in the gate
  prompt) so the workflow can't loop forever. Mirrors the
  `max_revision_cycles` / `max_fix_loops` precedent on the existing domains.

### Backward compatibility

`PolicyLoader.ApplyBuiltInDefaults` makes the change zero-config: if a repo's
`policy.yaml` has no `open_questions` block, the resolver returns
`mode=warning, min_severity=moderate, max_question_loops=3` — identical to
today's behavior (the cap was effectively infinity before, but 3 is
conservative and matches user intuition).

## Steps

The architect should refine into ordered Tasks/PGs. This is the v1 scope:

1. **Policy schema extension** — `src/Polyphony/Policy/PolicyConfig.cs`:
   - Add `OpenQuestions` property to `PolicyConfig` (same `DomainPolicy`
     shape as `Approvals` / `Pr`).
   - Add `MinSeverity` (string enum `low`/`moderate`/`major`/`critical`,
     stored as `string?` for YAML round-trip simplicity OR as a new enum —
     architect picks) and `MaxQuestionLoops` (`int?`) to `ScopeRule`. Both
     are optional and inherited like the existing caps.

2. **Policy enum** — `src/Polyphony/Policy/PolicyMode.cs`:
   - Add `OpenQuestions` value to the `PolicyDomain` enum.

3. **Loader defaults** — `src/Polyphony/Policy/PolicyLoader.cs`
   `ApplyBuiltInDefaults`:
   - `open_questions.defaults.mode = warning`
   - `open_questions.defaults.min_severity = moderate`
   - `open_questions.defaults.max_question_loops = 3`

4. **Resolver** — `src/Polyphony/Policy/PolicyResolver.cs`:
   - Extend `Resolve` to handle the new domain (parallel to `Approvals` /
     `Pr` switch arm).
   - Extend `ResolvedRule` with `MinSeverity` (string?) and
     `MaxQuestionLoops` (int?) fields. Both nullable so the existing
     `approvals`/`pr` resolutions stay unchanged in the JSON shape.

5. **CLI verb** — `src/Polyphony/Commands/PolicyCommands.cs`:
   - `Resolve` accepts `--domain open_questions`.
   - `Load` (`SnapshotDomain` helper) extended to surface the new fields.
   - `Validate` rejects bad `min_severity` (must be one of the four values)
     and non-positive `max_question_loops`.

6. **AOT JSON context** — `src/Polyphony/PolyphonyJsonContext.cs`:
   - Whatever new types are introduced (extended `ResolvedRule`,
     `PolicyDomainSnapshot` shape if expanded) need source-gen registration.

7. **Tests** — `tests/`:
   - Resolver: each mode (`auto`/`warning`/`manual`) × each scope
     (`defaults`/`root`/`by_type`) for the new domain.
   - Loader: defaults applied when no `open_questions` block.
   - Validator: bad severity rejected; bad cap rejected.
   - Existing `approvals`/`pr` tests stay green (no shape regression).

8. **Workflow wiring** — `.conductor/registry/workflows/plan-level.yaml`:
   - Add a script node `open_questions_policy` after `architect` that runs
     `polyphony policy resolve --domain open_questions --scope root`
     (scope choice is an open question — see Notes).
   - Replace the two hardcoded severity-filter routes (lines 313-316) with
     three policy-aware routes:
     - `mode == auto` → `review_group` regardless of questions.
     - `mode == manual && questions | length > 0` → `open_questions_gate`.
     - `mode == warning && questions | selectattr('severity','in',
       severities_at_or_above(min_severity)) | length > 0`
       → `open_questions_gate`.
     - else → `review_group`.
   - Add an `open_questions_loops` counter (workflow-scope variable updated
     by a tiny script node on each `answer` route taken). On hitting
     `max_question_loops`, the gate's `answer` route auto-proceeds to
     `review_group` instead of looping back.
   - Update the gate's human-facing prompt to surface the current mode +
     remaining cap ("loop 2 of 3, max_question_loops cap").

9. **Policy file example** — `.conductor/policy.yaml`:
   - Add a commented `open_questions:` block showing the example above
     plus behavioral notes, mirroring the `approvals` / `pr` comment style.

10. **Skill docs** — `.github/skills/polyphony-sdlc/SKILL.md`:
    - Document the new domain in the policy section.
    - If there's a "policy domains" reference table or similar, add a row.

## Notes

### Open questions for the architect

The user has considered these and has a lean, but explicitly wants the
architect to decide rather than rubber-stamp:

1. **`MinSeverity` representation.** Store as `string?` and validate at the
   CLI boundary, or introduce a typed `Severity` enum and parse on load?
   User lean: typed enum, parse on load — matches `PolicyMode` precedent and
   gives the validator nicer error messages. Tradeoff: more YamlDotNet
   binding boilerplate.

2. **Scope passed to `policy resolve` for open_questions.** The plan-level
   workflow knows the work item's type via `type_loader.output.type_name`.
   Should the resolve call use `--scope type:{{ type }}` (so `by_type`
   overrides apply) or `--scope root` (since open_questions only fires on
   the root of the run, not on children)? User lean: `--scope type:{{ type }}`
   — same as approvals/pr resolve calls elsewhere, and lets a repo say "auto
   for Issue, manual for Epic" sensibly.

3. **Counter mechanism.** The github-pr.yaml fix-loop uses a script
   node + workflow variable. Open-questions counter could use the same
   pattern, OR could rely on conductor's iteration counter for the
   architect agent (which goes up 1 every loop-back). User lean: explicit
   script-node counter, mirroring github-pr's pattern. Conductor's
   iteration counter resets on certain re-entries and we'd be coupling to
   internals — explicit is safer.

4. **Architect prompt re-entry hint.** When `mode=auto`, the architect's
   re-entry prompt currently has no signal that questions are being
   ignored. Should `architect-plan-level.md` get a conditional block
   that notes `mode=auto` and tells the architect not to bother? User
   lean: yes, small token-cost optimization. Also useful for `mode=manual`
   where any question — even `low` — will gate, so the architect should
   feel free to surface low-severity ones.

5. **Severity vocabulary.** Today the architect prompt says only emit
   questions with severity ≥ `moderate`. With `mode=manual` + low
   `min_severity`, we now want the architect to surface lower-severity
   questions too. Does the architect prompt need to be made
   policy-aware (resolved policy injected as context), or do we just
   relax the prompt to "emit anything ≥ low" and let the route filter
   handle gating? User lean: relax the prompt; route filtering is the
   right separation of concerns, and we don't want the architect making
   policy decisions.

### Out of v1 scope (noted as follow-on)

- **Open-questions for the implementation phase.** The coder/reviewer
  loop has no `open_questions` channel today. Adding one is a sibling
  concern that would benefit from the same policy machinery, but the
  semantics ("ask the user mid-implementation") are quite different and
  deserve their own design pass.

- **Per-question routing.** All questions in a batch are gated together
  today. Future work could route individual questions to different
  reviewers (e.g. security questions to a security gate) but the v1
  policy domain just controls whether the batch gates at all.

- **The deferred coder/reviewer escape valve from #3001.** That's a
  different loop on a different surface (implementation, not planning),
  but it would benefit from the same counter pattern. Worth filing as a
  sibling Epic that re-uses the `max_*_loops` precedent established here.

### Acceptance criteria

- A repo with no `open_questions:` block in `policy.yaml` runs identically
  to today (warning mode, threshold=moderate).
- Setting `open_questions.defaults.mode: auto` skips the gate entirely
  even when the architect emits `critical`-severity questions.
- Setting `open_questions.defaults.mode: manual` triggers the gate even
  when the only question is `low`-severity.
- Setting `open_questions.defaults.min_severity: major` lets `moderate`
  questions through without gating.
- Architect → gate → architect cannot loop more than `max_question_loops`
  times; on hit, the gate auto-proceeds with a visible "loop cap reached"
  message in the gate prompt.
- `polyphony policy resolve --domain open_questions --scope root` returns
  the merged rule with the new fields populated.
- `polyphony policy validate` rejects bad `min_severity` values and
  non-positive `max_question_loops`.
- Existing `approvals` and `pr` policy resolutions are unchanged
  byte-for-byte (regression check on the JSON shape).
- All existing tests stay green; new tests cover the new domain.

### Process notes

- This Epic dogfoods the new root flow shipped earlier
  (`state_detector → feature_pr → close_out → epic_closer`), which has
  not been exercised end-to-end on a real Epic since it landed. Two birds.
- Scope is small enough to land in 2 PGs (one .NET, one YAML+docs); the
  architect may collapse to 1 if preferred.
- File any workflow-engine bugs surfaced during the run as siblings under
  Epic 2919 (workflow-bugs-found-via-dogfood).

