# Architect children-contract audit (F2 / AB#3065)

**Status:** Accepted (audit-only — no code changes).
**Date:** 2026-05-09.
**Apex:** AB#3065.
**Related work:** PR #214 (closed-loop §3.4(a) — `apex_facets` marker), PR #225 (F3 — strict seed-children), F4 (queued — PR-time prose-children lint).

## Background

The closed-loop plan §3.4(a) introduced a hard contract: an architect's
`children` array is the *sole* machine declaration of child work items. Prose
declarations in the plan body (under headings like `## Child Issues` or in
narrative paragraphs) MUST NOT create child items, and MUST NOT silently
satisfy the parent's planning facet. PR #214 added the `apex_facets`
front-matter marker so an architect can explicitly declare an apex
indivisible. PR #225 (F3) made `polyphony plan seed-children` *refuse* to
stamp the `polyphony:planned` tag when `children:[]` arrives without
front-matter — closing the false-satisfied loophole that bit the AB#3064
dogfood.

This audit asks: does the architect-plan-level prompt itself adequately
prevent the prose-only-children failure mode, or does the architect still
have meaningful latitude to emit a plan that says "here are my children"
in markdown body while leaving the structured array empty?

## Audit scope

- [`.conductor/registry/prompts/architect-plan-level.md`](../../.conductor/registry/prompts/architect-plan-level.md)
  - "Decomposition contract" (lines 279–291)
  - "`children` field — read this carefully" (lines 342–388)
- [`src/Polyphony/Commands/PlanCommands.SeedChildren.cs`](../../src/Polyphony/Commands/PlanCommands.SeedChildren.cs)
  (the downstream enforcement boundary, lines 80–127)

## Findings

1. **Prompt language is unambiguous.** Both sections explicitly state that
   the structured `children` array is canonical, that prose-only
   declarations are non-binding and will halt the workflow, and that
   indivisibility requires explicit `apex_facets` front matter rather than
   bare `children: []`. The "no exceptions, no prose-only declarations"
   sentence (line 361) is normative.

2. **Indivisibility ergonomics are correct.** The contract gives the
   architect a non-broken way to express "this apex IS the unit of work" —
   the `apex_facets: [...]` front-matter — and the prompt walks through
   the YAML shape with an example (lines 366–388). The audit's own dogfood
   (this PR's plan, [`plans/plan-3065.md`](../../plans/plan-3065.md))
   exercised the indivisible path successfully on first try, end-to-end.

3. **Seed-time error is actionable, not generic.** The original framing
   ("today this is caught at seed-children time with a generic 'ambiguous'
   error") was outdated by the time this audit ran — F3 (PR #225) shipped
   the message at `PlanCommands.SeedChildren.cs:122–126`:

   > `children-json is empty and plan front-matter declares no apex_facets — refusing to stamp #N as planned. To declare an indivisible apex, add `apex_facets: [<facet>, ...]` to the front-matter of '<file>'. Otherwise, supply --children-json containing the architect's structured decomposition.`

   That message is specific, names the file, and tells the architect (or
   operator) what to add. It is not the "generic ambiguous error" the
   audit charter was concerned about.

4. **Prose-detection at architect time is high-risk.** A `polyphony plan
   check-children` verb that scans plan markdown for child-declaring
   patterns (e.g. headings + bulleted lists with link/id syntax) and
   cross-references the structured `children` array would produce false
   positives on prose like *this audit document itself*, which references
   `## Child Issues` as an example of the failure mode. Heuristic
   tightening would reduce false positives but cannot eliminate them:
   meta-discussion of the contract is precisely the kind of legitimate
   prose the heuristic must allow.

## Decision

**Accept the existing language as sufficient. Ship no prompt edits and no
new verb under this apex.** The four-layer defense — (1) prompt language
making the contract explicit, (2) `apex_facets` marker giving
indivisibility a non-broken expression, (3) seeder refusing ambiguous
empty-children, (4) clear actionable seed-time error — is already in
place after PR #214 + PR #225.

The remaining failure mode — an architect emits prose-declared children
*and* a non-empty `children` array that simply omits some prose-mentioned
items — is structural and cannot be detected at architect time without
the false-positive risk described above. **F4 (queued: PR-time
prose-children lint)** is the right home for catching this class:
PR-time the plan markdown is fixed, the structured `children` are
materialized as actual seeded work items, and a diff-aware lint can
compare the prose against the realised children with full context. F4 is
explicitly out of scope for this apex and tracked separately.

## Non-decisions

- The audit found no opportunity to surgically tighten the prompt
  language. The contract sentences are already declarative and complete.
  Re-wording for emphasis would risk drift from snapshot tests
  (`tests/Polyphony.Tests/Commands/PlanCommandsSeedChildrenTests.cs`
  exercise the seeder error contract; no prompt-snapshot tests block
  changes here, but the constraint is honored regardless).

- This audit deliberately does NOT amend the closed-loop plan §3.4(a)
  text or the F3 implementation. Both are well-formed; this audit is a
  validation, not a revision.

## What would re-open this decision

- A real-world incident where the existing four-layer defense lets a
  prose-only-children plan reach seed-children with a non-empty
  `children` array that subset-matches the prose. The current dogfood
  cohort (AB#3064 → AB#3065) has not produced one, but a single concrete
  example would warrant either prompt tightening or moving F4 forward in
  the polish queue.

- Architect-prompt structural changes in adjacent areas (open-questions,
  PG grouping, type-list resolution) that incidentally weaken the
  children-contract language. A reviewer of any such change should
  re-read sections 279–291 and 342–388 against this audit's findings.
