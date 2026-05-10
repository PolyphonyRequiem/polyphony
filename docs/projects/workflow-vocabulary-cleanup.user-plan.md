---
title: "Workflow Vocabulary Cleanup — Runtime-Injected User Types"
type: user-plan
status: draft
intended_consumer: polyphony-conductor-workflows architect agent (plan-level.yaml) via user_plan_path
---

# Workflow Vocabulary Cleanup — User Plan

> This is a **user-authored reference plan** for the architect agent to refine.
> It encodes the design intent, scope boundaries, and open design questions
> already considered. The architect should preserve the design decisions below
> unless they conflict with type constraints, in which case raise as open
> questions per the standard architect contract.

## Problem

The Polyphony engine (`src/Polyphony/`) is type-agnostic — `PhaseDetector.cs`
routes by facet lookup, with zero hardcoded type strings. The planning
workflow (`plan-level.yaml` + `load-type-context.ps1`) honors this pattern by
loading the user's actual work-item-type definition from
`.polyphony-config/work-item-types/{slug}.md` at runtime and injecting it into the
architect prompt.

The **implementation layer regresses against this pattern.**
`implement-pg.yaml`, the platform PR sub-workflows (`github-pr.yaml`,
`ado-pr.yaml`, `feature-pr.yaml`), `close-out.yaml`, and the routing scripts
(`impl-router.ps1`, `pg-router.ps1`, `dependency-check.ps1`) hard-code
ADO-Basic type names — `task_id`, `task_title`, `issue_id`, `issue_title`,
agent names like `task_router` / `task_reviewer` / `issue_reviewer` /
`task_completer`, prose like `"Task ID: ..."` and `"Issue-level item for
this PG"` — in agent names, output schemas, and prompt templates.

This works on ADO Basic (Issue + Task exist), limps on CMMI (Task exists,
no Issue), and is straightforwardly wrong on Agile (User Story + Task + Bug,
no Issue) and Scrum (PBI + Task + Bug, no Issue).

Live evidence (AB#2942 / PR #7): the dogfood run on AB#2937 (a Task whose
parent AB#2930 is an Issue) produced reviewer prompts saying things like
*"Task 2930 calls for a polyphony health subcommand"* — confusing the
parent's type with the child's type because the workflow calls every
non-leaf parent an "Issue" regardless of what it actually is.

## Goal

Complete the type-context-injection pattern from `plan-level.yaml` across
the rest of the orchestration layer, so the workflow operates in two
strictly separated vocabularies:

| Vocabulary | Lives in | Talks about | Stable across repos? |
|---|---|---|---|
| **Workflow vocab** | YAML, scripts, agent role names, schema field names | Roles (`architect`, `reviewer`, `coder`), phases (`planning`, `implementation`), structural relations (`focus`, `parent`, `plannable_child`, `implementable_child`, `level`, `PG`) | Yes — same words for every repo, every process template |
| **User vocab** | `.polyphony-config/work-item-types/*.md` and runtime twig lookup | The customer's actual type names: `Task`, `Issue`, `User Story`, `PBI`, `Scenario`, `Deliverable`, `Bug`, ... | No — varies per process template; loaded into prompts at runtime |

After this work: a Scrum repo's reviewer prompt renders as *"Review PG N
for PBI 2930 against its acceptance criteria"*; a CMMI repo's renders as
*"Review PG N for Scenario 2930 against its acceptance criteria"* — without
either the YAML or the scripts learning the words "PBI" or "Scenario".

## Approach

Apply the pattern that `plan-level.yaml` already demonstrates, downward into
the implementation and PR layers:

1. Add a runtime context loader for implementables (and their parents),
   parallel to `load-type-context.ps1`.
2. Replace ADO-Basic-flavored agent names and schema fields with neutral
   role/structural names.
3. Rewrite agent prompts to substitute user-vocab via the loader rather than
   bake in `Task` / `Issue` literals.
4. Audit and fix the same leakage in the PR sub-workflows and close-out.
5. Update tests and lint contracts to match the new field names.

**Forward compatibility constraint:** the loader must **return** the
resolved facets for an item, not assume them from type alone. v1
populates facets by today's type-only lookup; v2 (out of scope here,
see Future Work) replaces the lookup with conditional rules. By making the
loader the resolution point, v2 only changes the loader — consumers don't
notice.

## Steps

The architect should refine into ordered Tasks/PGs. This is the v1 scope:

1. **Loader** — Add `scripts/load-implementable-context.ps1` (or generalize
   `load-type-context.ps1` to accept an arbitrary `-WorkItemId`). Returns
   JSON shape:
   ```json
   {
     "type": "Task",
     "title": "...",
     "definition": "...",
     "facets": ["implementable"],
     "parent": {
       "id": 2930,
       "type": "Issue",
       "title": "...",
       "definition": "..."
     }
   }
   ```
   The `facets` field is the v2 extension point.

2. **Agent renames** in `implement-pg.yaml`:
   - `task_router` → `next_implementable_router`
   - `task_reviewer` → `implementable_reviewer`
   - `issue_reviewer` → `parent_reviewer`
   - `task_completer` → `implementable_completer`

3. **Schema field renames** in `pg-router.ps1`, `impl-router.ps1`,
   `dependency-check.ps1`, and any contract tests:
   - `task_id`, `task_title` → `primary_id`, `primary_title`
   - `task_ids` → `primary_ids`
   - `issue_id`, `issue_title` → `container_id`, `container_title`
   - `issue_ids` → `container_ids`
   - `dependency_check.blocking_items[].id`/`.title`/`.state` — audit; these
     may already be type-neutral but confirm the prose around them.

4. **Prompt vocabulary substitution** — every coder/reviewer/completer/
   PR-creator prompt that today says "Task" or "Issue" resolves
   `{{ ctx.type }}` / `{{ ctx.parent.type }}` from the loader. Headers like
   `## Task ID: NNN` become `## {{ ctx.type }} ID: {{ ctx.id }}`. Body prose
   like *"the issue-level reviewer flagged..."* becomes *"the
   {{ ctx.parent.type }}-level reviewer flagged..."*.

5. **PR sub-workflows and close-out** — apply the same audit/cleanup to
   `github-pr.yaml`, `ado-pr.yaml`, `feature-pr.yaml`, `close-out.yaml`. The
   PR sub-workflows are mostly already type-neutral (they talk about PRs,
   not work-item types) but their reviewer/creator prompts may still leak.

6. **Tests + lint** — `polyphony-conductor-workflows/tests/lint-*.ps1` and
   `polyphony/scripts/*.Tests.ps1` updated for renamed fields. No
   back-compat shim — hard cutover, since all consumers are in-repo.

## Notes

### Open questions for the architect

The user has considered these and has a lean, but explicitly wants the
architect to decide rather than rubber-stamp:

1. **Loader factoring.** One extended `load-type-context.ps1` with a
   target-id param, or two scripts (`load-type-context.ps1` for focus,
   `load-implementable-context.ps1` for PG iteration)? User lean: extend the
   existing one for DRY, accept the slightly broader parameter surface.
2. **Depth of user-vocab in prompts.** Just `{ type, title }` substitution,
   or also pipe full `definition` (the type's `.polyphony-config/work-item-types/
   {slug}.md` content) into the implementable_reviewer the way `plan-level.
   yaml` does for the architect? Latter is more powerful + more consistent
   with planning, costlier to roll out. User lean: yes, do the deep version
   while we're in there — it's the principled completion of the planning
   pattern, and it makes per-repo customization meaningful at the
   implementation layer.
3. **Workflow-vocab nouns.** Settle the role-vocab: `implementable` /
   `parent` / `container` vs alternatives. Whatever the architect picks
   must be applied consistently across all five workflow files and the
   loader.
4. **Order of operations.** Schema renames first (breaks tests) then
   prompts then loader? Or loader first (no breakage) then prompts (use
   loader) then schema renames last? Architect should sequence for minimum
   red-build window.
5. **Scope of "PR sub-workflows" cleanup.** github-pr.yaml/ado-pr.yaml are
   mostly clean (PR-centric, not type-centric) but their prompts may still
   say "issue" in places. Audit needed; small if any cleanup expected.

### Out of v1 scope (noted as follow-on)

These belong to a follow-on Epic. The v1 design must not preclude them:

- **Conditional facet resolution rules**
  (`facets: { rules: [...] }` schema). v1 loader returns the
  `facets` array so the rules engine can be slotted in behind it
  without changing consumers.

  Sketch of the future shape (Shape A with generalized `rules:` key):
  ```yaml
  Task:
    facets:
      rules:
        - when: parent_type == TaskGroup
          values: [actionable, implementable]   # architect picks per task
        - default: [implementable]
  ```

- **`actionable` facet** + the no-PR / act-and-confirm execution mode.
  `actionable` means "work that a human or bot performs and confirms, but
  produces no code artifact" — cloudvault TaskGroup → child Task is the
  canonical example. This will require a third execution branch in
  `implement-pg.yaml` alongside the coder→reviewer loop (something like
  `actor → confirmer`, no git branch, no PR, but the same lifecycle event
  model — Proposed → InProgress → Done). The v1 cleanup should leave clean
  structural slots so this branch can be added without re-renaming.

- **`review_policies` config wiring.** The `ProcessConfig.ReviewPolicies`
  schema (`src/Polyphony/Configuration/ProcessConfig.cs:9, 24-36`) is
  fully dead today — no C# code reads it, no workflow YAML branches on
  it, the per-scope review behavior is hardcoded in `implement-pg.yaml`
  and `feature-pr.yaml`. This is a separate sibling concern; flag in
  v1 commit messages so future-us doesn't conflate it. The actionable
  follow-on Epic will likely need to wire review_policies because
  actionable items have different review semantics than implementable
  ones.

### Acceptance criteria

- A CMMI repo (Scenario → Deliverable → Task) and an Agile repo (User
  Story → Task) running `polyphony-full` produce agent prompts that say
  *"Scenario 1234"* / *"User Story 1234"*, not *"Issue 1234"*.
- No string `task_id`, `task_title`, `issue_id`, `issue_title`,
  `issue_reviewer`, `task_router`, `task_reviewer`, `task_completer`
  remains in `implement-pg.yaml` or the platform PR workflows
  (verified by grep + lint test).
- The loader returns a `facets` field, populated from current
  type-only lookup, with a comment marking the v2 rules-extension point.
- `polyphony-conductor-workflows` test suite is green.
- `polyphony` test suite is green (`scripts/*.Tests.ps1`).
- Manual smoke: re-run polyphony-full on a Polyphony Epic against this
  Polyphony repo (Basic process); reviewer prompts should now say
  *"Issue NNNN"* via runtime substitution rather than as hardcoded prose.

### Process notes

- This user plan is the first dogfood of the `user_plan_path` mechanism
  end-to-end on a non-trivial real feature. Expect to surface bugs in
  that path. File any findings as siblings under Epic 2919
  (workflow-bugs-found-via-dogfood).
- Most code changes land in the
  [polyphony-conductor-workflows](https://github.com/PolyphonyRequiem/polyphony-conductor-workflows)
  repo, not polyphony itself. Expect the SDLC to operate cross-repo.

