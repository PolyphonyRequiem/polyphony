# PR Feedback Identity Classification — Marker, Not Author Identity

> **Status:** Accepted (2026-05-25).
> **Affects:** `.conductor/registry/workflows/{plan-level,github-pr,ado-pr}.yaml`
> — specifically the `pr_feedback_analyzer` prompt in each.
> **Supersedes:** the prior "identity policy" rule that excluded any
> comment whose author matched the PR author identity.

## Context

Three workflows (`plan-level.yaml`, `github-pr.yaml`, `ado-pr.yaml`) run a
sentiment-driven review loop centered on a `pr_feedback_analyzer` agent.
The analyzer reads the poll envelope (comments, threads, votes) and
returns `has_negative_feedback` to drive the revise/merge/abort routing.

Polyphony's PRs are almost always **agent-authored under the operator's
PAT** — the coder agent (and the marker-posting reviewer agents) push
through the same token the human operator uses. As a result, the PR
`author_identity` on the platform API is the **human operator**, not
the agent that actually drove the commits or posted the comments.

To disambiguate bot-from-human under a shared identity, polyphony emits a
deterministic HTML marker on every machine-posted comment:

```html
<!-- polyphony:agent-comment agent=plan_reviewer head_sha=abc1234 run_id=xyz -->
```

The parser lives at `src/Polyphony/Commands/PrCommentMarker.cs` and is
called out explicitly in its summary as existing for "the common ADO case
where `plan_reviewer_poster_ado` shares the operator's PAT".

### The bug this ADR addresses

Through 2026-05, the analyzer prompt carried a three-tier classifier:

1. Has `polyphony:agent-comment` marker → **bot**.
2. Else if `author == PR author identity` → **author self-comment**, exclude.
3. Else → **human reviewer**.

Rule 2 is wrong. It excludes the human operator's own comments on
agent-authored PRs — exactly the highest-priority signal in the system,
the **human-in-the-loop intervention**. An incident on 2026-05-25
surfaced this: an analyzer run reported `has_negative_feedback: false`
while three review threads from the operator identity were silently
dropped under rule 2.

The stated rationale ("the PR author narrating their own work doesn't
count as feedback to themselves") conflates two distinct cases:

- The **agent** narrating its own progress — yes, noise, but the marker
  already identifies these as bot comments.
- The **human operator** intervening on an agent-authored PR — this is
  authoritative feedback that must drive the revise loop.

Identity-equality is not a valid proxy for "narrator vs. intervener"
when the agent and the operator share an identity. The marker is.

## Decision

The analyzer classifies comments **by marker presence, not by author
identity**. The full rule, applied uniformly to comments AND votes:

1. **Marker present** → bot. Classify by the marker's `agent`
   attribute. Apply existing bot logic (e.g. the
   `**Blocking concerns**` section convention).
2. **Marker absent** → human. Apply natural-language judgement, regardless
   of whether the author identity matches the PR author identity.

There is **no identity comparison** anywhere in the rule. A `+10` /
`approved` vote from the operator on their own PR counts as positive
feedback. A `-5` / `changes_requested` vote from the operator counts as
negative feedback. A free-form comment from the operator is judged on
its content like any other human comment.

### Why no carve-out for votes

A previous draft of this ADR kept identity-exclusion for the **votes**
case on the grounds that "self-approval is platform-blocked anyway".
That carve-out was rejected because:

- Self-approval is only platform-blocked at the **final merge to main**
  in default branch-protection configurations. Earlier-stage approvals
  (intermediate gates, draft reviews, ADO `+5` "approved with
  suggestions" on a non-protected branch) are not universally blocked.
- Whether self-approval is permitted at any given stage is a
  **policy/branch-protection concern enforced by the platform**, not a
  concern of the polyphony feedback analyzer. If the platform blocks the
  merge for policy reasons, the platform surfaces that downstream
  (`mergeable_state`, merge-time refusal). The analyzer's job is only to
  classify the **feedback content**, not to predict or enforce platform
  merge policy.

Mixing platform-policy reasoning into the analyzer was the original
mistake and the carve-out would have replicated it in miniature.

## Consequences

- **Human operator interventions on agent-authored PRs now drive the
  revise loop.** This is the intended human-in-the-loop affordance and
  the load-bearing reason for the change.
- **Self-approval is honored as positive signal at the analyzer layer.**
  Whether the resulting merge actually proceeds is the platform's call
  via branch protection / approval-required policy. Operators who want
  to forbid self-approval should configure that in branch protection,
  not rely on the analyzer to silently filter it out.
- **The `author_identity` field stays in the rendered prompt context**
  for diagnostic value (it appears in the "Context — Poll envelope"
  block), but the analyzer no longer compares against it. Future
  versions may drop the field entirely; keeping it for now eases
  triage of analyzer reasoning traces.
- **The structural consistency lint
  (`lint-sentiment-loop-consistency.ps1`) is unaffected** — it pins
  agent shape, output schema, and route presence, not prompt text.
- **The marker parser
  (`src/Polyphony/Commands/PrCommentMarker.cs`) becomes more
  load-bearing.** Any future bot poster that fails to inject the marker
  will have its comments classified as human feedback. The existing
  Pester tests around the marker remain the safety net.

## Non-goals

- This ADR does not change which votes are **terminal** (handled by the
  deterministic classifier upstream of the analyzer). The terminal
  classifier's identity rules — if any — are out of scope and continue
  to be evaluated on their own merits.
- This ADR does not change marker injection by polyphony's bot posters;
  it only changes how the analyzer interprets the absence of a marker.
