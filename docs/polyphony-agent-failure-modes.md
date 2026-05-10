# Failure Modes I Just Caused

A short postmortem. Six concrete failure modes I (a Copilot CLI agent) hit while working
on Polyphony, and which doc/skill from this set would have prevented each. Framed as
"AI-agent failure modes when working with polyphony, and how the docs above prevent
them." Each case includes citations the reader can verify cheaply.

---

## 1. Inventing `polyphony resolve-transition` when `polyphony validate` already returns `target_state`

**What happened:** I drafted an implementation that introduced a brand-new CLI verb
called `polyphony resolve-transition` whose job was to map a (work_item, event) pair to
the right state name. The verb didn't exist — but the function it would have performed
already does, on the existing `validate` verb's output.

The four registered verbs are `route`, `validate`, `validate-config`, `hierarchy`
(`src/Polyphony/Program.cs:18-21`). `ValidateResult.target_state`
(`src/Polyphony/Models/ValidateResult.cs:9`) is exactly the name a downstream caller
needs to pass to `twig state`. The reference consumer is
`scripts/scope-closer.ps1:54-60`.

**What doc would have prevented it:** `polyphony-cli-reference.md`. The "verbs at a
glance" table is exactly four rows; the `validate` section opens with "Answer two
questions in one call: is this event legal? what state name should we transition to?"
Reading that page first would have made the new verb obviously redundant.

**What it would have said:** *"`polyphony validate` returns `target_state`; pass that
to `twig state`. This is the established way to derive the state name."*

---

## 2. Auditing `workflows/` only and missing two hardcoded sites in `scripts/`

**What happened:** I went looking for hardcoded state names ("the literal `Done`" / "the
literal `Doing`") and grep'd only `workflows/`. I found one (`workflows/implement-merge-group.yaml:370`)
and reported "one site to fix". The audit was wrong — there is also
`scripts/impl-router.ps1:106` (`twig state Doing --output json …`), and the workflow
literal at `implement-merge-group.yaml:370` is itself an inline pwsh fragment, not a YAML
attribute. Both of these are "scripts that hardcode state names", and I missed half the
problem because I treated workflow YAML and helper scripts as if they were one layer.

**What doc would have prevented it:** `polyphony-architecture.md` (Layering section)
plus `polyphony-workflow-author.skill.md` (the canonical helper-scripts table and
"never hardcode state names" rule).

**What it would have said:** *"Workflow YAMLs in `workflows/` and PowerShell helpers in
`scripts/` are two distinct layers, both of which can shell out to twig directly. Audits
of state-name literals must search both directories. The existing anti-patterns are
`workflows/implement-merge-group.yaml:370` and `scripts/impl-router.ps1:106`."* The skill
literally cites both sites by line number.

---

## 3. Estimating 250-1200 LoC for a fix that's actually 2 line replacements

**What happened:** I scoped the "no more hardcoded state names" change as if it required
new infrastructure: a state-resolver service, a script-side wrapper around `polyphony
validate`, helpers for caching the workspace_hint, etc. Estimate landed at 250-1200 LoC.

The actual fix is two line replacements, both following the pattern already in
`scripts/scope-closer.ps1:54-60`. Each anti-pattern site becomes:

```powershell
$validateJson = polyphony validate --work-item <id> --event <event> 2>$null
$validateResult = $validateJson | ConvertFrom-Json
if ($validateResult.is_valid) {
    twig state $validateResult.target_state --output json 2>$null | Out-Null
}
```

That's the entire change. The infrastructure I was about to invent already exists — it
*is* `polyphony validate`.

**What docs would have prevented it:** `polyphony-cli-reference.md` (the `validate`
section's "use when" + the literal scope-closer snippet) and
`polyphony-workflow-author.skill.md` (the right-pattern / wrong-pattern code blocks
side-by-side).

**What they would have said:** *"The validate-then-transition idiom already exists at
`scripts/scope-closer.ps1:54-60`. Replace each anti-pattern site by copying that snippet
and substituting the appropriate event name. No new infrastructure required."*

---

## 4. Assuming a C# `IWorkItemPlatform` interface existed when platform abstraction is workflow-YAML-level only

**What happened:** I drafted plans that referenced an `IWorkItemPlatform` (or similarly,
`IProcessAdapter`) C# interface that didn't exist. I had assumed that the
"github vs. ado" split must be a typed C# seam in `src/Polyphony/`. It isn't. The split
is a workflow-YAML construct: `feature-pr.yaml`'s `pr_platform_router` node
(lines 98-111) and `implement-merge-group.yaml`'s same-named node (lines 697-710) inspect a
`platform` input and route to either `github-pr.yaml` or `ado-pr.yaml`. The two
sub-workflows share an *input/output schema contract* documented in their headers
(`workflows/github-pr.yaml:1-16`, `workflows/ado-pr.yaml:1-15`); there is no C# code
for the split.

**What doc would have prevented it:** `polyphony-architecture.md`, "Where the platform
abstraction lives" section.

**What it would have said:** *"There are no `IWorkItemPlatform` / `IProcessAdapter`
interfaces in `src/Polyphony/`. The platform split is workflow-YAML-only:
`pr_platform_router` in `feature-pr.yaml` and `implement-merge-group.yaml` dispatches on
`workflow.input.platform` to `github-pr.yaml` or `ado-pr.yaml`. Adding a new platform
means writing a `<platform>-pr.yaml` matching the contract and adding a `when` branch."*
That section even includes the dispatch diagram so this can't be missed.

---

## 5. Missing twig's `StateResolver.ResolveByCategory` (which directly answers the user's "auto-map InProgress" question)

**What happened:** The user asked whether twig could auto-map something like
`StateCategory.InProgress` to "the right state name in this template, whatever that is".
I reasoned about it from scratch and proposed building a new helper. I missed that the
function literally exists already: `Twig.Domain.ValueObjects.StateResolver.ResolveByCategory`
(`twig2/src/Twig.Domain/ValueObjects/StateResolver.cs:14-24`):

```csharp
public static Result<string> ResolveByCategory(StateCategory category, IReadOnlyList<StateEntry> states)
{
    for (var i = 0; i < states.Count; i++)
        if (states[i].Category == category) return Result.Ok(states[i].Name);
    return Result.Fail<string>($"No state with category '{category}' found …");
}
```

That is the answer. The doc gap was that I had no map of the twig surface area Polyphony
relies on.

**What doc would have prevented it:** `polyphony-architecture.md`, "The three
vocabularies" → "State categories" subsection. It explicitly cites `ResolveByCategory`
as the function for "give me the state name in this template that maps to category X."

**What it would have said:** *"Twig exposes `StateResolver.ResolveByCategory(category,
states)` for `category → state_name` lookups. This is the function to reach for if you
want auto-mapping by category."*

---

## 6. Hand-waving `scope_removed: Removed` as "footgun" without identifying that it'd actually fail at runtime against ADO Basic

> **Resolved 2026-05-10 by V-21 (issue #281).** The class of failure described
> below is now a hard preflight error. Authors must declare every state per
> type in `process-config.yaml`'s `states:` block, and a transition target not
> declared in `states:` produces V-21 at `polyphony validate-config` time.
> The `StateCategoryResolver` heuristic is gone from polyphony entirely
> (replaced by `ProcessConfig.GetCategory`). See
> `docs/decisions/states-in-process-config.md`. The narrative below is
> retained for historical context.

**What happened:** I flagged `scope_removed: Removed` in `.polyphony-config/process-config.yaml`
(lines 25, 30, 34) as "feels off" but didn't dig into *why*. The actual problem is
concrete and verifiable: ADO's Basic process template has only three states — `To Do`,
`Doing`, `Done` — and no `Removed`. (Reference state set:
`twig2/tests/Twig.TestKit/ProcessConfigBuilder.cs:80-84`.) `polyphony validate-config`
will pass the config, and `polyphony validate --event scope_removed` will return
`is_valid: true, target_state: "Removed"`. The failure surfaces at twig write time when
`StateResolver.ResolveByName` rejects the unknown state with a literal, copy-pastable
error: *"Unknown state 'Removed'. Valid states: To Do, Doing, Done"*
(`twig2/src/Twig.Domain/ValueObjects/StateResolver.cs:58-59`).

**What doc would have prevented it:** `polyphony-process-config-schema.md`, "Anti-pattern
callout" + the per-template state-set tables.

**What it would have said:** *"Validation V-1..V-14 do not cross-reference state names
against the process template's actual state set; mismatches fail at twig write time, not
at `polyphony validate-config` time. ADO Basic has only `To Do`, `Doing`, `Done` — no
`Removed`. The current canonical config's `scope_removed: Removed` rows are latent bugs
against Basic; either drop the row or substitute a state that exists."* And it would
have given the exact twig error string to grep for.
