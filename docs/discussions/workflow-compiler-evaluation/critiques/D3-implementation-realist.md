---
doc_type: discussion
status: exploratory
synopsis: Implementation-realist critique of compiler-lite — verdict SHIP-WITH-SCOPE-CUT; Phase 1 = typed contract catalog + one real YAML lint, period.
---

# D3 — Implementation Realist Critique: Is Phase 1 Actually Shippable?

**Verdict: SHIP-WITH-SCOPE-CUT** — compiler-lite is buildable, but only if Phase 1 is reduced to "typed contract catalog consumed by one real YAML lint" and not Roslyn analyzers, DSL, or generated workflow pilots.

**Smallest viable Phase 1:** Land a PR that uses the existing `VerbOutputSchemaCatalog` / AB#3255 substrate to validate one class of live workflow `node.output.field` references in CI, with mutation tests proving it catches a real typo.

---

## Phase 1 commit shape

Smallest real PR this week:

**Commit message:**
`feat(workflow-contracts): lint workflow output refs against verb schema catalog`

**Pseudo-PR description**

Files:
- `tests/lint-workflow-output-contracts.ps1`
- `tests/lint-workflow-output-contracts.Tests.ps1`
- `.github/workflows/ci.yml`
- maybe `artifacts/verb-output-schemas.json` if regenerated
- no new DSL, no emitted workflow YAML, no new analyzer package

Scope:
- Load `artifacts/verb-output-schemas.json`, produced from C# `[VerbResult]` + `PolyphonyJsonContext`.
- Parse workflow YAML script nodes whose command/args clearly invoke `polyphony <group> <verb>`.
- Build a map: `nodeName -> verb result schema`.
- Scan `when:` and workflow `output:` templates for simple `{{ node.output.field }}` references.
- Fail if the field is not declared by the result schema.
- Support only direct field paths in v1; nested paths can warn or be skipped with an explicit diagnostic.

Estimated diff: **500-900 LOC**: one lint script, one Pester suite, one CI line, fixtures.

Tests:
- Pester mutation fixture where `root_router.output.action` becomes `root_router.output.actoin`.
- Fixture for nullable/missing field guard pattern.
- Positive test over at least `implement-merge-group.yaml` or `actionable.yaml`.
- CI integration as another Pester lint step.

This proves "compiler-lite" because a C#-authored contract artifact starts policing handwritten YAML. Anything larger by Friday is not Phase 1; it is Phase 1 plus platform work.

## The two-sources-of-truth cliff

The synthesis has to pick this posture:

**Phase 1: handwritten YAML remains canonical. C# contracts are authoritative only for cross-boundary shapes, not graph structure.**

That means no generated YAML in Phase 1. The "compiler-lite" source of truth is not "C# emits workflows"; it is "C# declares contracts that YAML must obey."

For later generated pilots, the posture should be:

**Generated workflow mode: C# is canonical; generated YAML is checked in, reviewed, and executed, but humans do not hand-edit it.**

Migration cost:
- One workflow at a time.
- No partial migration inside a workflow.
- Generated YAML must carry a header: "generated from X; do not edit."
- CI must fail if generated YAML differs from source.
- Emergency hand-edits are allowed only on incident branches and must be back-ported into C# or reverted.

If the plan permits both "humans edit YAML" and "C# regenerates YAML" for the same workflow, it will drift immediately.

## Golden-diff test problem

Raw golden diffs are the wrong gate. YAML formatting, scalar quoting, map ordering, comments, and line wrapping will create noise.

The diff posture should be layered:

1. **Semantic canonical diff** for correctness:
   - Parse expected and actual YAML.
   - Normalize map ordering where order is semantically irrelevant.
   - Preserve sequence ordering.
   - Normalize scalar style, quote style, blank lines, and comments away.
   - Compare canonical JSON-like trees.

2. **Formatter snapshot** for generated readability:
   - A smaller raw golden test on compiler-owned fixtures only.
   - Fails when the emitter churns formatting.
   - Does not compare against legacy handwritten YAML comments.

3. **Source-map/readability check**:
   - Generated YAML must keep stable node names.
   - Optional generated comments are allowed, but not part of semantic parity.

Without this split, every emitter refactor becomes a test explosion.

## Real-conductor harness gate

This part is realistic because the repo already has `tests/harness/`, CI installs conductor, and Pester runs integration-tagged harness scenarios on PRs.

But the gate needs to be explicit:

- Generated pilot PR adds one harness scenario for the generated workflow path.
- The scenario runs the **emitted YAML**, not the C# source.
- FakeProvider handles LLM boundaries.
- Shim handles script-node behavior.
- No network-dependent ADO/GitHub calls in the PR gate.
- One generated leaf scenario should be expected to add tens of seconds, not minutes.

Concern: CI currently installs conductor from `github.com/microsoft/conductor.git@main`. That is a flake vector. A compiler pilot should pin a conductor commit or version for the harness gate; otherwise compiler PRs can fail because conductor moved.

Do not run every workflow through conductor on every PR. Run targeted pilot scenarios in PR CI; run broader generated-registry parity nightly or manually.

## Migration math

Fifteen workflows is misleading; the six giant workflows dominate:
- `plan-level.yaml`: 2955 lines
- `implement-merge-group.yaml`: 2240
- `apex-driver.yaml`: 1530
- `ado-pr.yaml`: 1383
- `github-pr.yaml`: 1224
- `feature-pr.yaml`: 1180

A "one workflow per week" migration gives false confidence because the first four leafs are easy and the large six are the real project.

Force this choice:

**All-or-nothing per workflow, but opt-in per workflow family. No intra-workflow hybrid mode.**

Costs:
- Leaf pilots are feasible.
- Medium workflow pilot is a real semantic-parity project.
- Giant workflows are a multi-month migration and should not be promised.
- Hybrid exists at registry level: some workflows handwritten, some generated.
- Hybrid must not exist inside a single workflow, or the compiler has to understand arbitrary hand-authored holes forever.

Add a policy: after a workflow enters generated mode, handwritten canonical YAML is deleted within 90 days or the migration is declared failed.

## Roslyn analyzer setup cost

The synthesis under-prices this. The repo already has `Polyphony.SchemaGenerator`, which helps, but analyzers are still not "free."

Realistic cost:
- Extend existing generator metadata: **8-16 hours**
- New analyzer diagnostics, release files, test harness: **24-40 hours**
- YAML/Jinja reference resolver: **20-40 hours**
- CI/IDE behavior, false-positive tuning: **8-16 hours**
- Review + stabilization: **1-2 PR cycles**

Total: **60-100 hours** for a serious analyzer-backed lint.

PowerShell lint equivalent:
- One invariant, one artifact, simple path parser: **6-12 hours**
- Pester mutation tests: **4-8 hours**
- CI wiring: **<1 hour**

For Friday, PowerShell wins. Roslyn becomes worth it only after the invariant proves valuable and noisy cases are understood. Otherwise Daniel buys analyzer platform complexity before proving the lint catches real defects.

## Escape hatch that survives

There must be an operator bypass, but it must be intentionally ugly:

- Normal path: generated YAML checked in and executed.
- Emergency path: conductor can run a hand-edited YAML file by explicit path.
- Required flag/name: `--workflow-yaml <path>` or "incident override," not silent fallback.
- CI policy: hand-edited generated YAML cannot merge unless either:
  1. source C# is updated and regeneration matches, or
  2. the workflow is explicitly reverted to handwritten mode.

The escape hatch bitrots if it is never tested. Add exactly one harness scenario or smoke test that proves conductor can still run a checked-in YAML artifact without invoking the compiler. Do not test "random hand-edited YAML" forever; test the operational bypass path.

## Journaling parallel

The journal Phase 2.5 pattern is the strongest argument for compiler-lite, not full DSL.

The useful precedent is:

> C# declares metadata adjacent to existing behavior, and another layer consumes it later.

That maps naturally to:
- `*.WorkflowContracts.cs`
- `*.AgentContracts.cs`
- `*.VerbContracts.cs`
- generated catalogs
- CI lint over YAML

It does **not** naturally imply `[Workflow]`-attributed fluent C# graph authoring. That is a different idiom: C# no longer annotates behavior; it owns orchestration structure.

So the next step should look like the journal partial-class pattern: small, adjacent declarations consumed by a decorator/lint/catalog layer. The full fluent builder can wait.

## Where the synthesis is realistic

The realistic part is the typed contract registry surface. The repo already has:
- `Polyphony.SchemaGenerator`
- `[VerbResult]`
- `PolyphonyJsonContext`
- `VerbOutputSchemaCatalog`
- sanity tests ensuring embedded and disk schema artifacts match
- CI Pester lint culture
- real conductor harness scenarios

That is genuine existing investment. Compiler-lite can reuse it.

The less-realistic parts are:
- route lambda lowering
- full workflow C# builder
- Roslyn exhaustiveness analyzer
- generated YAML parity for large workflows
- two-pilot Phase 1.5 as if it is a small extension of Phase 1

Those are new platform work. They may be good ideas, but they are not "land by Friday" ideas.

## Ship-it-Friday test

If Daniel says "land Phase 1 by Friday," cut scope to:

**In:**
- One lint proving C# contract catalog can police handwritten YAML.
- One real workflow included.
- Mutation tests.
- CI gate.
- Brief ADR/decision note saying handwritten YAML remains canonical.

**Out:**
- No generated YAML.
- No C# workflow DSL.
- No Roslyn analyzer.
- No source maps.
- No leaf pilot.
- No agent-bearing pilot.
- No migration policy implementation beyond documenting the posture.

Floor below which nothing useful shipped:
- A contract catalog that no workflow lint consumes.
- A design-only ADR.
- A generator that emits YAML but is not executed by conductor.
- A golden file with no real harness or live-registry lint.

## Verdict: SHIP-WITH-SCOPE-CUT

Compiler-lite is buildable, but the synthesis is currently over-staging Phase 1 by letting analyzer, generator, pilot, and migration concerns blur together. The cutline is: Phase 1 proves C# contracts can catch one real YAML drift class in CI; generated workflows start only after that. If that Friday PR lands and catches a real mutation, the roadmap becomes credible; if Phase 1 tries to include Roslyn analyzers or emitted workflow pilots, it is not commit-sized yet.
