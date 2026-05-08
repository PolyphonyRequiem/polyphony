# Jinja2 Resolver Lint for Workflow YAML Output References

> **Status:** Proposed. Implements [`#175`](https://github.com/PolyphonyRequiem/polyphony/issues/175)
> (prong 2 of [`#172`](https://github.com/PolyphonyRequiem/polyphony/issues/172)).
> **Driver:** Workflow YAMLs reference verb output fields via Jinja paths
> (`{{ <step_id>.output.<path> }}`) and nothing today checks that the
> path resolves against the producing verb's wire shape, that the field
> is reachable at runtime under `WhenWritingNull`, or that the step-id
> half of the reference even exists in the workflow. Bugs #6 and #8 are
> the targets — both are mechanically detectable now that
> [`#173`](https://github.com/PolyphonyRequiem/polyphony/issues/173)
> ships the verb-output schema registry.
> **Supersedes:** none — greenfield.

## Context

[`#173`](https://github.com/PolyphonyRequiem/polyphony/issues/173)
publishes `artifacts/verb-output-schemas.json` — a verb→DTO→field map
derived from the C# `PolyphonyJsonContext` and per-`[Command]`
`[VerbResult(typeof(X))]` attributes. The schema describes every field
each polyphony verb can produce, the kind (scalar / object / list /
map), the JSON name (already snake-cased), the C# nullability, and —
crucially — `can_omit_when_null`: whether the property is silently
elided from the JSON object when null.

That registry is the producer half of a contract. The consumer half
lives in `.conductor/registry/workflows/*.yaml` — every workflow step
whose `prompt:`, `args:`, `when:`, `command:`, or output mapping
references `{{ <step>.output.<path> }}`. Today nothing checks that
`<path>` exists on `<step>`'s verb output, and nothing checks that
omit-when-null fields are guarded.

The cost of the missing check has been made concrete twice:

- **Bug #6** (PR #168) — `type_loader.output.type_name` referenced a
  field the verb does not emit (`type`).
- **Bug #8** (PR #170) — `derive_ancestor_chain.output.parent_item_id`
  is `WhenWritingNull` and disappears from the JSON envelope when null,
  so referencing it without `is defined` / `default()` raises
  `UndefinedError` mid-run under conductor's `strict_undefined`.

Both bugs surface only at run time, after the workflow is partway
through a mutation. Lint catches them at PR time.

## Decision

Ship a **PowerShell lint** (`tests/lint-jinja-resolver.ps1`) that
parses every workflow YAML, builds a `step_id → verb` map, walks every
`{{ <step>.output.<path> }}` reference in every Jinja-evaluated string
field, and resolves the path against the registry. Diagnostics are
emitted in two formats: human-readable (default) and GitHub Actions
workflow-command syntax (`-Format github`). The script is wired into
the existing CI workflow alongside `lint-type-agnostic.ps1` and
`lint-strict-undefined.ps1`.

The four design forks:

| Fork | Decision | Why |
|---|---|---|
| **Implementation language** | PowerShell, mirroring `lint-strict-undefined.ps1`. | Existing lint suite is uniform PS; CI already has `pwsh` + `powershell-yaml`; the lint is a pure JSON-and-YAML walk with no need for the polyphony binary. A C# verb would couple lint to `dotnet build` ordering and lose the ability to run on a clean checkout without first restoring the .NET workload. |
| **Registry consumption** | Read `artifacts/verb-output-schemas.json` directly via `ConvertFrom-Json`. Provide a clear "run `dotnet build` first" error if missing — matches the failure-mode contract committed to in the registry ADR. | Decouples lint from the source generator; same JSON shape works for any future consumer (docs tooling, IDE hints). |
| **Diagnostic taxonomy** | Five codes (`JINJA001`–`JINJA005`), error/warning split, GitHub-Actions-compatible output. | Mirrors the registry generator's `POLY1001`–`POLY1006` taxonomy for consistency. Errors fail CI; warnings annotate without failing so unverifiable references (sub-workflows, non-polyphony scripts) don't block. |
| **Scope discipline** | Schema resolution only. Control-flow availability and sub-workflow output access are deferred and explicitly documented as gaps — the lint will say so in its summary. | Bug #13a (control-flow availability) and bugs #11/sub-workflow output need different machinery (dominance analysis, declared-output schemas). Conflating them with this PR delays the schema-resolution win. |

## Diagnostic taxonomy

| Code | Severity | Trigger | Remedy |
|---|---|---|---|
| `JINJA001` | Error | `{{ X.output.foo }}` — `foo` (or any path segment after `output.`) is not a field of `X`'s verb result type. | Use the actual field name from the registry; or annotate the verb's result DTO. |
| `JINJA002` | Warning | `{{ X.output.foo }}` — `foo` (or a segment along the path) has `can_omit_when_null: true` and the reference is not guarded by `\| default(...)`, an enclosing `{% if X is defined %}`, or `{% if X.output.foo is defined %}`. | Add a `\| default(...)` filter or wrap in `{% if X.output.foo is defined %}`. |
| `JINJA003` | Error | `{{ Y.output.bar }}` — `Y` does not match any step `id`/`name` declared earlier in the workflow (and is not a workflow-builtin like `workflow.input.*`). | Check spelling; verify the upstream step is actually declared above this reference. |
| `JINJA004` | Warning (suppressed by default; opt in with `-Pedantic`) | The producing step's command is not a polyphony verb in the registry (sub-workflow, `pwsh`, `twig`, `gh`, etc.). Lint cannot verify the path. | None required — emitted only in `-Pedantic` mode. By default the lint emits a single summary line ("N references skipped — non-registry steps"). Per-reference detail is available via `-Pedantic` for targeted audits but not on every CI run, because non-polyphony steps appear in every workflow and would dominate the diagnostic stream. |
| `JINJA005` | Error | Path walks through a `kind: scalar` or descends into a `kind: list`/`kind: map` without an integer index, recognized list-method (`length`, `first`, `last`), or appropriate filter pipe. e.g. `foo.items.name` when `items` is a list. | Use `foo.items[0].name`, `foo.items \| first`, `foo.items \| length`, or `{% for x in foo.items %}{{ x.name }}{% endfor %}`. |

Severity policy:
- **Errors** fail the lint (exit code 1) and emit `::error file=...`
  workflow commands in GitHub-Actions mode.
- **Warnings** do not fail the lint (exit code 0) but emit
  `::warning file=...` workflow commands so PR authors see them.

`-FailOnWarnings` flips the policy for callers who want strict mode.

## Mechanics

### Step → verb mapping

Two shapes appear in the wild:

1. `type: script` with `command: polyphony` and the verb in `args:`:
   ```yaml
   - name: derive_ancestor_chain
     type: script
     command: polyphony
     args:
       - "plan"
       - "derive-ancestor-chain"
       - "{{ workflow.input.work_item_id }}"
   ```
   The verb is the first two non-flag args joined by a space:
   `"plan derive-ancestor-chain"`. Top-level commands (no group) take
   only the first arg: `"validate"`, `"hierarchy"`, etc.

2. `type: script` with `command: pwsh` / `twig` / `gh` — not a polyphony
   verb. Step is recorded but its output schema is unknown → any
   downstream reference fires `JINJA004` (warning).

3. `type: agent` — agent steps emit a free-form JSON object the agent
   chose to return. The workflow's own `output:` mapping under the
   agent step declares the contract, but the lint does not enforce it
   today (out of scope; tracked separately). Downstream references to
   `<agent_step>.output.<X>` fire `JINJA004`.

4. `type: workflow` (sub-workflow invocation) — output shape is the
   sub-workflow's `output:` mapping. Cross-workflow resolution is a
   later PR; today fires `JINJA004`.

### Reference extraction

The lint scans every Jinja-evaluated string field for the regex
`\{\{\s*([A-Za-z_]\w*)\.output\.([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)`.
That's the conservative shape — pipe filters and indexing characters
delimit the path, so `{{ X.output.foo.bar | default('') }}` extracts
the path `foo.bar`. Bracket-indexed paths
(`{{ X.output.items[0].name }}`) are detected separately and the
list-segment is recognized as a valid list descent.

Fields scanned:
- `prompt:` (block scalar) on `type: agent` steps
- `args:` (list of strings) on `type: script` steps
- `command:` (top-level string) on `type: script` steps
- `when:` (string) on each route
- `output:` mappings (workflow-scope and per-step)
- `input_mapping:` on sub-workflow invocations

### Guard detection

A reference `{{ X.output.foo }}` is considered guarded when any of:

1. The pipe chain in the same expression contains `default(`.
2. The reference appears inside `{% if X is defined %}` ... `{% endif %}`
   — block scope is detected by maintaining a guard stack as the lint
   scans the field's text linearly.
3. The reference appears inside `{% if X.output is defined %}` (zero
   prefix — guards all `X.output.*` descendants), or inside
   `{% if X.output.<prefix> is defined %}` — the path-prefix guard
   covers any descendant of `<prefix>`.
4. The reference itself is the operand of `is defined` /
   `is not none` / `is none` (it's the test, not the consumption).
5. **Expression-scope guard** — within a single Jinja expression
   (`{{ ... }}` or `when:` / output-mapping value), if a reference `R`
   appears alongside an `is defined` / `is not defined` test on the
   same identifier or a path prefix of `R`, joined by `and` / `or`,
   `R` is considered guarded. This is the `{{ X.output.error is not
   defined or X.output.error == '' }}` and
   `{{ X is defined and X.output.verdict == 'changes_requested' }}`
   idiom that pervades the workflow corpus — Jinja's short-circuit
   semantics make these safe at runtime, and the lint must mirror
   them. Without this, the lint false-positives on five+ occurrences
   in `apex-driver.yaml`, `feature-pr.yaml`, and `implement-mg.yaml`
   alone.

The guard-stack is per Jinja-evaluated string field (one stack per
`prompt`, one per `args` entry, one per `when`, one per output-mapping
value). Block guards do not leak across fields.

Path-segment omit-when-null: when walking
`{{ X.output.a.b.c }}`, the lint checks each segment along the path.
If any segment has `can_omit_when_null: true` and is not covered by
one of the guard forms above for that prefix, `JINJA002` fires once
for that reference (the deepest unguarded segment). Reports cite the
shallowest unguarded prefix to keep the message actionable.

### List / map descent

For `kind: list`:
- `[N]` (numeric index) → continue from `element_kind` /
  `element_type_ref`.
- `length`, `first`, `last` → recognized as list attribute access.
- Bare `.foo` → `JINJA005` (lists do not expose attribute access; this
  is the silent runtime trap).

For `kind: map`:
- `.<key>` → continue from `value_kind` / `value_type_ref` (treated as
  a value lookup; key validity is not enforced because map keys are
  open).
- `[<expr>]` → same.
- `keys`, `values`, `items`, `length` → recognized as map methods.

For `kind: scalar` with a trailing path segment → `JINJA005`.

### What this lint does NOT catch

| Class | Example | Why deferred |
|---|---|---|
| **Control-flow availability** | `preflight_failure_gate.prompt` references `commit_and_push_manifest.output.error` before the agent has run. (Bug #13a class.) | Requires dominance / reachability analysis over the workflow graph — separate machinery, separate PR. |
| **Sub-workflow output access** | `{{ feature_pr.output.merged }}` where `feature_pr` is a `type: workflow` step. | Needs cross-workflow resolution: load the sub-workflow YAML, walk its `output:` mapping, map back to the parent's references. Tractable but out of scope here. |
| **Agent step `output:` declarations** | `actionable_agent` declares an `output: { summary: ... }` but the agent might not emit it. | Schema is hand-authored YAML rather than registry-derived — cross-checks against the agent's actual output are conductor-runtime concerns, not static lint. |
| **Terminal envelope conformance** | Bug #11 — `apex-item-dispatch` terminals emit `{}` instead of the canonical 12-field envelope. | The envelope shape lives in YAML, not C#. Needs a workflow-level "terminal envelope conformance" lint. |
| **Workflow-builtin shape** | `workflow.input.foo` is unchecked against the workflow's `input:` declaration. | Easy to add later; not in #175's scope. |

These gaps are documented in the lint's `SYNOPSIS` block and the
summary line at the end of every run says *what was checked* and *what
was skipped* so the reader is not surprised.

## CI integration

The lint runs in `.github/workflows/ci.yml` after the existing
`lint-type-agnostic.ps1` step. It depends on
`artifacts/verb-output-schemas.json` existing — which is a post-build
side effect of the `dotnet build` step that already runs earlier in
the same job.

```yaml
- name: Lint workflow YAMLs (Jinja2 resolver)
  shell: pwsh
  working-directory: polyphony
  run: pwsh -NoProfile -File tests/lint-jinja-resolver.ps1 -Format github
```

When run in `-Format github`, every diagnostic emits a workflow
command (`::error file=...,line=...,col=...::JINJA00X: <message>`) so
GitHub annotates the PR diff. The default `-Format human` is used
locally and prints a colourized table.

If `artifacts/verb-output-schemas.json` is missing, the lint exits 2
with the canonical "run `dotnet build` first" message committed to in
the registry ADR.

## Fixture strategy

Two fixture corpora:

1. **Synthetic fixtures** under `tests/lint/fixtures/workflows/` —
   small (~20-line) YAML files, each crafted to trigger one diagnostic
   plus one clean baseline:
   - `clean.yaml` — every diagnostic absent.
   - `JINJA001-missing-field.yaml` — references a field that doesn't
     exist on the producing verb.
   - `JINJA002-omit-when-null-unguarded.yaml` — references an
     omit-when-null field without a guard. A companion
     `JINJA002-omit-when-null-guarded.yaml` shows the same field
     correctly guarded (clean).
   - `JINJA003-unknown-step.yaml` — references a step id that doesn't
     exist.
   - `JINJA004-non-polyphony.yaml` — references the output of a
     `pwsh`-command step.
   - `JINJA005-scalar-descent.yaml` — walks a path through a scalar
     leaf.
   - `JINJA005-list-bare-attribute.yaml` — `foo.items.name` when
     `items` is a list.

   The synthetic fixtures freeze the diagnostic semantics. The
   registry fixture under `tests/lint/fixtures/verb-output-schemas.json`
   is checked in (a copy of the artifact at the time #175 was
   developed, locked so synthetic-fixture tests don't drift when the
   registry shape changes).

2. **The real corpus** — `.conductor/registry/workflows/*.yaml` (14
   files, 444 `.output.` references at time of writing). The Pester
   suite asserts the lint runs over this tree without errors. Warnings
   are expected (sub-workflow refs, non-polyphony script outputs).

If the real-corpus assertion surfaces genuine bugs, they're documented
in the PR description but not fixed here — bug fixes are separate PRs
to keep diff scope clean. An allowlist file
(`tests/lint-jinja-resolver.allowlist.yaml`) is used to suppress
known-bug-but-deferred references; entries point at a tracking issue
so the suppression has an exit path.

**Allowlist size cap.** The lint hard-fails if the allowlist exceeds
**15 entries**. The mechanism exists for tracked-bug suppression and
shouldn't accumulate into a parallel source of truth — the cap is the
forcing function. Hitting the cap means clean up suppressions or
raise the limit by ADR amendment.

## Amendment 2026-05-08 — `can_omit_when_null` respects nullability

The original generator computed `can_omit_when_null = ignore != "Never"`. Because
`PolyphonyJsonContext`'s default condition is `WhenWritingNull`, every property
without an explicit `[JsonIgnore]` override was emitted with
`can_omit_when_null=true` — even compiler-non-null types like
`required string State`. The lint then warned authors to wrap those references in
`{% if x is defined %}`, producing 202 false-positive JINJA002 warnings on the
live workflow corpus (issue #187).

The corrected logic respects the C# nullable annotation (project-wide
`<Nullable>enable</Nullable>` + `TreatWarningsAsErrors` makes the annotation a
real contract):

| `IgnoreCondition`    | Value type | `Nullable<T>` | Non-nullable ref | Nullable ref |
|----------------------|-----------:|--------------:|-----------------:|-------------:|
| `Always`             |  true      |  true         |  true            |  true        |
| `Never`              |  false     |  false        |  false           |  false       |
| `WhenWritingNull`    |  false     |  true         |  false           |  true        |
| `WhenWritingDefault` |  true      |  true         |  false           |  true        |

(`Always` rows are degenerate — `[JsonIgnore]` properties are filtered out earlier;
the row is here for completeness.)

Pinned by `tests/Polyphony.SchemaGenerator.Tests/CanOmitWhenNullTests.cs`. The
fixture under `tests/lint/fixtures/verb-output-schemas.json` is regenerated
in the same change, so the Pester real-corpus assertion continues to mirror the
live registry. Issue #187 is closed by this fix; the residual 32 JINJA002
warnings (down from 202) all reference genuinely-nullable fields and are the
correct lint surface for case-by-case workflow guarding.

## Amendment 2026-05-08 — CI gate flipped to live registry

When this ADR was written, #173 (the verb-output schema registry) had not
yet shipped, so CI ran `lint-jinja-resolver.ps1` against the checked-in
fixture under `tests/lint/fixtures/verb-output-schemas.json` as a
stopgap. #173 shipped as PR #184 on 2026-05-07; the CI gate now lints
against the live `artifacts/verb-output-schemas.json` generated at build
time by `Polyphony.SchemaExporter`.

The fixture file remains in place and is still used by the Pester unit
suite (`tests/lint-jinja-resolver.Tests.ps1`, opt-in via
`-UseFixtureRegistry`) for hermetic synthetic-input testing. Only the
real-workflow CI gate flipped.

This closes the loop the original ADR's "Locked registry fixture vs.
live registry" finding accepted as a documented gap — the gap was
specifically about CI freshness, and is now closed.

## Rubber-duck findings deferred

| Finding | Disposition |
|---|---|
| PowerShell may hit a complexity ceiling around 400 lines once the guard-stack and intra-expression `and`/`or` parsing land. | Accepted as a flag, not a fix. We commit to PS for ecosystem consistency; if the script crosses 500 lines or grows a second pass, a one-time port to Python is the planned escape hatch. |
| Locked registry fixture vs. live registry will diverge silently on field renames. | Accepted as a documented gap. The synthetic-fixture suite is small (one fixture per diagnostic) and references stable registry shapes; refresh is a one-line copy. A "diff fixture against live artifact" CI step is overkill for this scope. |

## Consequences

**Wins**

- Bugs #6 and #8 become PR-time lint failures rather than
  mid-workflow runtime crashes.
- The cost of adding a new verb stays the same as today (one
  `[VerbResult]` attribute, courtesy of #173), and the cost of using
  it in a workflow gains free path validation.
- The contract between the C# DTO graph and the YAML consumer is
  finally machine-checked end-to-end.

**Costs**

- The lint adds ~300 lines of PowerShell + a Pester test suite.
- Running the lint locally requires `artifacts/verb-output-schemas.json`,
  which requires a prior `dotnet build`. CI builds before linting so
  this is invisible there; locally, the lint emits a clear remediation
  message rather than a confusing failure.

**What we're betting on**

- That the `kind: scalar`/`object`/`list`/`map` tagging in the
  registry is rich enough to walk arbitrarily deep paths. The registry
  ADR's "What this registry catches" section makes the same bet.
- That the guard-detection heuristic is a sufficient first cut. We
  detect three guard forms (`default(`, `{% if X is defined %}`,
  `{% if X.output.<prefix> is defined %}`) plus the trivial
  `is defined` test case. False positives surface as lint failures
  that the author defangs with one of the recognized guard forms;
  false negatives (rare guard idioms we don't recognize) miss the
  diagnostic but don't fail. The allowlist is the safety valve for
  edge cases.

## Migration

None — greenfield. The lint ships in the same PR as its tests and CI
wiring. Existing workflows that surface real diagnostics are
allowlisted with a tracking-issue link; the allowlist's stated policy
is "every entry must have an issue link or be removed within one
sprint."

## Forward references

- Bug #13a (control-flow availability lint) — separate PR.
- Sub-workflow output resolution — separate PR (depends on a
  per-workflow declared-output schema registry).
- Terminal envelope conformance lint (Bug #11 class) — separate PR.
