# ADO Feature-PR Lifecycle Parity (Phase 5 closeout)

> **Status:** Accepted. Wires the ADO leg of `feature-pr.yaml` end-to-end so
> both platforms run the same review → remediation → re-request → merge
> chain. Closes the parity gap left open by PR #121 (which only finished
> the GitHub leg) and depends on the four ADO PR verbs that shipped in
> #119, #100, #120, #122.
> **Supersedes:** the short-circuit emitter
> (`ado_remediation_not_supported_emitter`) introduced as a temporary
> exit in PR #121.

## Context

`feature-pr.yaml` orchestrates the full feature-PR lifecycle: open PR →
delegate to a platform sub-workflow → on merge=true exit success → on
merge=false enter a remediation chain (planner → seeder → implementer
→ updater) and loop. Until v1.2.0 the ADO branch terminated at
`ado_remediation_not_supported_emitter` — a stub script that emitted
`merged=false` and exited because the ADO PR verbs that the remediation
chain needed (post a comment on the PR; poll its review status) did not
exist yet. That was an honest stub: failure mode was a single quiet
exit, not silent corruption.

Those verbs have now landed:

- `pr create-feature-ado` (#119) — creates the feature PR.
- `pr poll-status-ado` (#100) — returns reviewer identity + vote
  (-10 / -5 / 0 / +5 / +10) and a coarse review state.
- `pr merge-feature-ado` (#120) — completes the PR.
- `pr post-comment-ado` (#122) — posts a markdown comment on the PR.

The platform sub-workflow `ado-pr.yaml` was already invoked from
`feature-pr.yaml`'s `pr_lifecycle_ado` node and already runs the
poll → human-gate → merge cycle (with the stuck-review timeout from
#148). The only gap left was the remediation chain.

## Decision

**Reuse `ado-pr.yaml` as the platform sub-workflow.** No structural
changes there; only two operator-facing prompt strings get refreshed
(stale "ADO remediation not wired" claims that became false with this
PR). The remediation chain itself stays single-implementation in
`feature-pr.yaml` and becomes platform-aware via Jinja branching on
`workflow.input.platform`:

- **`remediation_planner`** — branches the PR-context fetch step.
  GitHub uses `gh pr view` + `gh api .../reviews` to read the full
  review body and inline comments. ADO uses
  `polyphony pr poll-status-ado` to read reviewer identity + vote.
- **`feature_pr_updater`** — emits a unified markdown
  `comment_body` no matter the platform. On GitHub the LLM posts it
  directly via `gh pr comment` (and re-requests review via the
  GitHub API). On ADO the LLM cannot post (no `polyphony` tool
  registered) so it sets `posted: false` and routes to a sibling
  script.
- **`feature_pr_updater_poster_ado`** (new) — runs
  `polyphony pr post-comment-ado` to actually post the comment.
  Mirrors the dual-poster pattern from `plan-level.yaml`'s
  `plan_reviewer` + `plan_reviewer_poster_ado` (PR #123) — that
  pairing is now codified in
  `.github/skills/polyphony-workflow-author/SKILL.md` as the canonical
  shape for "LLM agent generates content, sibling script invokes the
  polyphony verb".

Both legs flow through the same chain
(`remediation_counter → planner → seeder → implementer → updater →
pr_platform_router`), so the cycle cap (3) and the
`remediation_cap_gate` human gate apply uniformly.

Lint coverage extended in `lint-feature-pr.ps1` with five new rules:
`missing-ado-creator`, `missing-ado-creator-failed-gate`,
`missing-ado-updater-poster`, `missing-ado-remediation-route`, and
`ado-remediation-stub-present` (positive removal assertion — fails if
the legacy short-circuit emitter ever comes back). Each rule has a
matching mutation test in `lint-feature-pr.Tests.ps1`.

## Reviewer identity model on ADO

GitHub's PR review surface lets us read the full text of a reviewer's
comment. ADO's `pr poll-status-ado` does not — it returns reviewer
identity + vote only. The remediation_planner is honest about this in
its prompt: on ADO it works from reviewer identity + vote + the
original plan + whatever operator-supplied context exists in the
work-item description. If that's too thin, it routes the operator
toward triage rather than fabricating a remediation plan.

This is deliberate. We chose to ship the parity with this gap
documented rather than block on a sixth ADO verb. The follow-up
(`pr get-comments-ado`) **shipped** in a later PR — once workflow
YAMLs (`feature-pr.yaml` remediation_planner, `plan-level.yaml`
`plan_reviewer_poster_ado` area) are migrated to consume it, the
planner's ADO branch can drop its "best-effort" framing and match
the GitHub branch's fidelity. See
`src/Polyphony/Commands/PrCommands.GetCommentsAdo.cs`.

There is also no multi-reviewer vote aggregation yet on either
platform — the planner reads the most recent reviewer's verdict. When
we add that (likely as part of a richer ADO branch policy story), it
will land as a separate ADR.

## What's still GitHub-only

Nothing structural. The `feature_pr_updater` agent does not re-request
review on ADO — that's intentional, not a gap. ADO branch policies
already pin required reviewers per branch; per-PR reviewer re-requests
in the GitHub shape are a `gh`-specific affordance that doesn't have a
direct ADO analogue.

## Forward references

- `pr get-comments-ado` **shipped** —
  `src/Polyphony/Commands/PrCommands.GetCommentsAdo.cs`. Workflow
  migration to consume it (replacing the "best-effort" framing in
  `feature-pr.yaml` remediation_planner and `plan-level.yaml`
  `plan_reviewer_poster_ado` area) is the remaining follow-up.
- Multi-reviewer vote aggregation (open) — applies to both platforms.
- The dual-poster pattern is now used in three places: `plan-level.yaml`
  (`plan_reviewer_poster_ado`), `feature-pr.yaml`
  (`feature_pr_updater_poster_ado`), and any future workflow that
  needs an LLM agent to "post" something via a polyphony verb. If a
  fourth instance shows up, consider lifting the pattern into a
  shared sub-workflow.
