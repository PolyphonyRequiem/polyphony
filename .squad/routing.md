# Work Routing

How to decide who handles what.

## Routing Table

| Work Type | Route To | Examples |
|-----------|----------|----------|
| Architecture & seam decisions | Bach | Layering reviews, ADRs, glossary changes, seam-boundary judgments |
| Architecture / design / readability **REVIEW** (antagonistic) | Boulez | Adversarial review of ADRs, design docs, workflow-level designs, vocabulary drift; rejects on thesis/invariants/seam grounds. NOT a producer. |
| Code craft / implementation elegance / technical accuracy **REVIEW** (antagonistic) | Ravel | Adversarial review of C#, PowerShell, Python, YAML, JSON contracts against per-language idioms; rejects on naming, exception swallowing, async misuse, encoding, race conditions, missing tests. NOT a producer. |
| Mission & scope | Beethoven | "Does this serve human-assisted automated SDLC?", scope-creep gating, human-gate placement |
| Testability & test infra | Brahms | Harness scenarios, lint scaffolds, xunit/Pester tests, design-for-testability reviews |
| Conductor YAML mechanics | Mahler | Route conditions, gates, re-entry, sub-workflow plumbing |
| Workflow YAML authoring | Wagner | New workflows, PR-platform abstraction, recursion design, sub-workflow library hygiene |
| Agent prompts & addendum | Stravinsky | Facet profiles, per-item guidance, addendum composition, agent failure modes |
| C# / .NET code | Mozart | Polyphony CLI verbs, engine, schema export, RunManifest, contract tests |
| PowerShell scripts | Liszt | Helper scripts, shell-out idiom, launcher orchestration, Pester scaffolds |
| Twig / ADO integration | Sibelius | Twig CLI usage, `.twig/config` issues, ADO behavior, work-item lifecycle |
| Git / worktrees / branches | Reich | Branch tree, per-item worktrees, promotion gates, merge semantics, reset flow |
| Code review (constructive) | Domain owner | Route to whoever owns the domain; cross-domain → Bach |
| Code review (antagonistic / hostile pass) | Ravel | Final pass before merge for anything non-trivial; pairs with Boulez on cross-cutting changes |
| Design review (antagonistic / hostile pass) | Boulez | Required pass for any new ADR, design doc, or major proposal before squad-wide adoption |
| Testing | Brahms | Coverage gaps, harness edge cases, lint failures |
| Scope & priorities | Beethoven | What to build next, mission trade-offs, human-gate trade-offs |
| Session logging | Scribe | Automatic — never needs routing |

## Issue Routing

| Label | Action | Who |
|-------|--------|-----|
| `squad` | Triage: analyze issue, assign `squad:{member}` label | Lead |
| `squad:{name}` | Pick up issue and complete the work | Named member |

### How Issue Assignment Works

1. When a GitHub issue gets the `squad` label, the **Lead** triages it — analyzing content, assigning the right `squad:{member}` label, and commenting with triage notes.
2. When a `squad:{member}` label is applied, that member picks up the issue in their next session.
3. Members can reassign by removing their label and adding another member's label.
4. The `squad` label is the "inbox" — untriaged issues waiting for Lead review.

## Rules

1. **Eager by default** — spawn all agents who could usefully start work, including anticipatory downstream work.
2. **Scribe always runs** after substantial work, always as `mode: "background"`. Never blocks.
3. **Quick facts → coordinator answers directly.** Don't spawn an agent for "what port does the server run on?"
4. **When two agents could handle it**, pick the one whose domain is the primary concern.
5. **"Team, ..." → fan-out.** Spawn all relevant agents in parallel as `mode: "background"`.
6. **Anticipate downstream work.** If a feature is being built, spawn the tester to write test cases from requirements simultaneously.
7. **Issue-labeled work** — when a `squad:{member}` label is applied to an issue, route to that member. The Lead handles all `squad` (base label) triage.
8. **Antagonistic reviewers are NEVER producers.** Boulez and Ravel review, reject, and stop. They never write the proposal, the code, the fix, or the alternative. On REJECT a different agent revises (Reviewer Rejection Protocol).
9. **Antagonistic review is not a gate by default.** Spawn Boulez/Ravel when (a) a major design or non-trivial code change lands, (b) Daniel asks for a hostile-read pass, or (c) a domain owner wants their work stress-tested. Routine work does NOT need their pass.
10. **Boulez and Ravel coordinate on cross-cutting changes** where code defects might imply architectural failure (or vice versa). When that happens, Ravel pauses and escalates to Boulez; Boulez does not weigh in on code craft.
