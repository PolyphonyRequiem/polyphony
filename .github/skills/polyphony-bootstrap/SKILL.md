---
name: polyphony-bootstrap
description: >-
  Activate when onboarding a fresh repo to the polyphony SDLC engine — i.e. a
  repo that has twig already installed and configured, and the polyphony CLI on
  PATH, but no `.conductor/` directory yet. Provides prereq verification, the
  6-step smoke test that proves the wiring before any workflow runs, and a
  catalogue of common bootstrap pitfalls. The full step-by-step walkthrough
  (process template selection, type definitions, templates, agent guidance,
  profile, validation, first run) lives in `docs/onboarding-guide.md`; this
  skill is the agent-loadable companion.
user-invokable: false
---

# Bootstrapping a Repo for Polyphony

This skill is the **agent-loadable companion** to the long-form bootstrap
guide. The full step-by-step walkthrough — process template selection, type
definitions, templates, agent guidance, profile, validation, first run — is
in **`docs/onboarding-guide.md`**, which uses a fictitious project called
**kyber** as its worked example throughout.

What lives **here** that is NOT in the long guide:

- The exact 3 prereq commands to verify before authoring anything.
- A 6-step smoke test that proves every wire is connected before any
  workflow runs.
- A catalogue of bootstrap pitfalls with citations.
- A one-screen checklist for repo onboarders.

Read order if you are doing this for real:

1. This skill (you're here) — confirm prereqs, run smoke test.
2. **`docs/onboarding-guide.md`** — the long walkthrough.
3. **`docs/polyphony-conductor-directory.md`** — every file in `.conductor/`.
4. **`docs/polyphony-process-config-schema.md`** — full YAML schema, V-1..V-14.
5. **`docs/polyphony-agent-failure-modes.md`** — prior agents' bootstrap mistakes.

---

## 1 · Verify prerequisites (3 commands)

```powershell
polyphony --help          # must list exactly: route validate validate-config hierarchy
twig --version            # twig CLI must resolve
twig workspace            # must return a workspace, not "no workspace found"
```

The four-verb canon is at `src/Polyphony/Program.cs:18-21`. A fifth verb
means you are running a fork. If `twig workspace` errors, run `twig init` to
create `.twig/config` (the workspace discovery walks up from CWD looking for
`.twig/`, falling back to `<cwd>/.twig` per
`twig2/src/Twig.Infrastructure/Config/WorkspaceDiscovery.cs:23-67` and
`src/Polyphony/Program.cs:11-12`).

Pick **one real work item ID** to smoke-test against — list with
`twig workspace --tree`. Call it `$WI` from here on.

---

## 2 · Run the long-form bootstrap

Follow `docs/onboarding-guide.md` sections 2–8 to:

- Select your process template (Basic / Agile / Scrum / CMMI / custom)
- Run `bootstrap-conductor.ps1 -ProcessTemplate <template>` to scaffold
  `.conductor/`
- Author `process-config.yaml`, type definitions, templates, agent guidance,
  and `profile.yaml`
- Run `polyphony validate-config --config .conductor --output human` until
  it exits 0 (warnings allowed)

Once `validate-config` passes, return here and run the smoke test below
**before** invoking any workflow. The smoke test is what catches
configuration that passes validation but fails at runtime — see pitfall 5a.

---

## 3 · Smoke-test the bootstrap end-to-end

These six commands prove every wire is connected without needing to run a
workflow. Set `$WI` to the real work item ID you picked in step 1.

```powershell
$WI = 1234   # ← replace with your real ID
```

### 3.1 — `validate-config` returns exit 0

```powershell
polyphony validate-config --config .conductor
$LASTEXITCODE
# Expect: 0   (warnings allowed; errors block)
```

### 3.2 — `hierarchy` returns valid JSON with non-empty `type`

```powershell
polyphony hierarchy --work-item $WI --depth 1 |
  ConvertFrom-Json |
  Format-List work_item_id, type, state, facets
```

If `facets` is `[]`, the work item's type is missing from your
`types:` map. Fix by adding the type to `process-config.yaml`. Without a
match, downstream workflows treat the item as neither plannable nor
implementable and skip it silently.

### 3.3 — `state next-ready` returns a per-requirement view

```powershell
polyphony state next-ready --work-item $WI | ConvertFrom-Json | Format-List
```

Expected: `work_item_id`, `status`, `requirements`, `next`. `status`
collapses the per-requirement view into a routing hint
(`needed | ready | fulfilling | satisfied | empty`); `requirements` is
the full `(kind, disposition)` set; `next` is the ready-to-dispatch
subset (the workflow's `for_each` input).

### 3.4 — `validate` returns `is_valid` + `target_state`

Pick an event the work item's type supports. Common events emitted by
sub-workflow scripts include `begin_planning` and `implementation_complete`
(see `scripts/scope-closer.ps1:55`). Use `implementation_complete` for an item
in an `InProgress`-category state:

```powershell
polyphony validate --work-item $WI --event implementation_complete |
  ConvertFrom-Json
```

Expected: `is_valid: true`, `target_state: "<your-template's-done-state>"`.
If `is_valid: false`:

- `target_state: null` → unknown event or unknown type
  (`src/Polyphony/Routing/TransitionValidator.cs:29-44`).
- `target_state` populated → precondition failed; the four
  precondition-aware events check the item's `StateCategory`
  (`TransitionValidator.cs:66-73`).

### 3.5 — `twig state $target_state` would resolve (DRY TEST — does not write)

`twig state` has no `--dry-run` flag (`twig2/src/Twig/Commands/StateCommand.cs:34`).
Cheapest dry test: confirm the state name exists in the type's state set via
`twig process <Type>`:

```powershell
$validate = polyphony validate --work-item $WI --event implementation_complete |
  ConvertFrom-Json
$type = (polyphony hierarchy --work-item $WI --depth 0 |
  ConvertFrom-Json).type
twig process $type | Select-String $validate.target_state
```

Non-empty match → twig will accept the state name. Empty → `twig state`
would fail at write time with `"Unknown state '<name>'. Valid states: …"`
(`twig2/src/Twig.Domain/ValueObjects/StateResolver.cs:58-59`). This is the
single most valuable smoke-test step — it catches the
`scope_removed: Removed` footgun (pitfall 5a) without writing to ADO.

### 3.6 — Optional: round-trip a real state change

Only run if you are willing to actually change the work item's state:

```powershell
twig set $WI
twig state $validate.target_state
```

Success → entire bootstrap is wired end-to-end. Revert with
`twig state <previous>`.

---

## 4 · Walk every event through the smoke test

Step 3.5 catches one event mismatch. Repeat it for every event in your
`transitions:` table — for each `(type, event)` pair, run:

```powershell
$v = polyphony validate --work-item $someWiOfType --event $eventName |
  ConvertFrom-Json
twig process $type | Select-String $v.target_state
```

This catches every state-name-vs-template mismatch before any workflow runs.
It is especially worth running for `scope_removed` because no live workflow
emits it today (verified by grep across `polyphony/scripts/` and
`twig2/workflows/`); the row would only fail in production.

---

## 5 · Common bootstrap pitfalls

### 5a · `scope_removed: Removed` against ADO Basic

Basic has only `To Do`, `Doing`, `Done`
(`twig2/tests/Twig.TestKit/ProcessConfigBuilder.cs:80-84`).
`validate-config` passes; `polyphony validate --event scope_removed`
returns `is_valid: true, target_state: "Removed"`; `twig state Removed`
then fails with `"Unknown state 'Removed'. Valid states: To Do, Doing,
Done"`. Detect via step 3.5 / section 4. Full treatment:
`docs/polyphony-agent-failure-modes.md` § 6 and
`docs/polyphony-process-config-schema.md` "Anti-pattern callout".

### 5b · State-name vocabulary mismatch (`InProgress` vs `Active`)

The right side of `transitions:` is a **literal state name** passed
verbatim to `twig state` (`src/Polyphony/Configuration/ProcessConfig.cs:8`).
It is **not** a `StateCategory`. Writing `begin_implementation: InProgress`
against an Agile template fails — Agile's category-`InProgress` state is
named `Active`. The category-based lookup function exists
(`StateResolver.ResolveByCategory` —
`twig2/src/Twig.Domain/ValueObjects/StateResolver.cs:14-24`) but polyphony
does not call it. Spell out the literal name correct for your template.
Full contract: `docs/polyphony-architecture.md` "The three vocabularies".

### 5c · Wrong `--config` argument shape

| Verb              | `--config` is a … | Default                            |
|-------------------|-------------------|------------------------------------|
| `validate-config` | **directory**     | `.conductor`                       |
| `validate` / `hierarchy` / `state next-ready` | **file path** | `.conductor/process-config.yaml` |

Source: `ValidateConfigCommand.cs:20-22`, `ValidateCommand.cs:22`,
`HierarchyCommand.cs:19`, `StateCommands.NextReady.cs:22`. Passing a file to
`validate-config` or a directory to the others fails noisily — easy to miss
when copying commands between docs.

### 5d · Invalid facet values

V-4 (`ConfigValidator.cs:60-67`) accepts only `plannable` and
`implementable`. There is **no** `actionable` facet. Facets like
`coordination`, `actionable`, `grouping` will fail validation. If you need
a coordination-only type, give it `[plannable]` (parent types are
inherently grouping containers) and document the convention in its
`work-item-types/<slug>.md` file.

### 5e · V-9..V-14 warnings — what to fix first

V-9 and V-10 fire **per type** (`ConfigValidator.cs:100-120`), so a 5-type
config emits 10 of those warnings before V-11..V-14. Recommended order:

| Warning   | When to address                                                        |
|-----------|------------------------------------------------------------------------|
| V-9, V-10 | **Before running any planning workflow** — agents read these directly  |
| V-14      | Optional today (reserved placeholder; no live consumer — see           |
|           | `docs/polyphony-conductor-directory.md` § 5)                            |
| V-11/12/13| Before promoting to production SDLC use; defaults work meanwhile       |

---

## 6 · Bootstrap checklist (one screen)

```
[ ] 1.   polyphony --help shows exactly: route validate validate-config hierarchy
[ ] 1.   twig workspace returns a workspace (not "no workspace found")
[ ] 1.   Pick a real $WI to smoke-test against
[ ] 2.   Walk docs/onboarding-guide.md sections 2-8
[ ] 2.   polyphony validate-config --config .conductor → exit 0 (warnings ok)
[ ] 3.1  validate-config exit code is 0
[ ] 3.2  hierarchy --work-item $WI --depth 1 returns valid JSON with non-empty type
[ ] 3.3  route --work-item $WI returns phase + action
[ ] 3.4  validate --work-item $WI --event <evt> returns is_valid + target_state
[ ] 3.5  twig process $type lists $target_state in its state set
[ ] 4    Walk every (type, event) pair in transitions: through 3.5 (catches 5a)
[ ] 6    docs/onboarding-guide.md § 9: conductor run twig-sdlc-v2-full@twig works
```

If every line passes, the repo is bootstrapped.

