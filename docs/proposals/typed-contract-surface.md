---
doc_type: proposal
status: draft
synopsis: Publish a typed contract surface + schemas for polyphony verbs and workflow nodes (AB#3255; load-bearing for sibling proposals).
---

# Typed Contract Surface + Schema Publication

**Status:** Draft
**Work item:** AB#3255 (parent epic: AB#3253)
**Sibling issues:** AB#3254 (action journal), AB#3257 (failure model), AB#3259 (vocabulary cleanup)

## TL;DR

Polyphony already has the seed of this design: many CLI verbs return typed C# records, `PolyphonyJsonContext` already defines the wire shape, and the existing verb-output generator proves build-time schema publication works. The problem is fragmentation:

- typed CLI DTOs for many verbs;
- hand-authored PowerShell JSON for shared helpers; and
- stringly workflow consumers plus bespoke lints.

AB#3255 collapses that into one surface: **every cross-boundary envelope gets a named C# contract; verbs, shared scripts, and workflow outputs emit those contracts; the build publishes schemas; and one schema-driven `lint-envelopes.ps1` replaces the domain-specific contract lints.**

The current verb-output registry is the seed, not throwaway code: it broadens into a full **contract schema catalog** covering verb, script, workflow, and CLI-meta envelopes.

## Goal

Make polyphony's cross-boundary wire contracts explicit, typed, published, and linted from one source of truth.

## Non-Goals

- **Not a conductor redesign.** This proposal consumes conductor's current `script` / `workflow` / `output:` model; it does not require new runtime primitives.
- **Not a re-litigation of exit-code behavior.** The catalog must describe today's mixed routing-style vs non-zero behavior, but AB#3255 does not settle the larger failure-model question.
- **Not "everything becomes a CLI verb" on day one.** Shared PowerShell helpers can remain scripts if they emit published contracts through the common emitter path.
- **Not a second schema DSL.** JSON Schema and the aggregate catalog are derived artifacts, never handwritten truth.
- **Not a promise that every workflow-local counter or terminal becomes public API.** Some contracts are public; others stay internal.

---

## Design Dimensions

Each dimension with a real tradeoff includes options and a recommendation.

### D1 — Scope: what counts as the typed contract surface?

Current inventory:

| Domain | Already typed today | Still hand-rolled today |
|---|---|---|
| **CLI dispatch / meta** | `RequiredInputErrorResult`; top-level health, hierarchy, status, validate, and config-validation envelopes | none |
| **Agent / guidance / requirements / research** | `AgentComposeAddendumResult`, `GuidanceExtractResult`, `RequirementsDeriveResult`, research article output | none |
| **Policy** | `PolicyLoadResult`, `PolicyValidateResult`, `ResolvedRule` | `resolve-pr-policy.ps1`, `resolve-research-policy.ps1`, `resolve-unattended-cap-mode.ps1`, `root-fallback-gate` terminal envelopes |
| **State** | validate-inputs, preflight-lite, preflight, next-ready result records | `lifecycle-router.ps1` wraps state + hierarchy into its own routing envelope |
| **Root / scope** | root declare / resolve plus scope check / list / tag / untag records | `root-fallback-gate` workflow output envelope |
| **Branch** | assert-on-impl, check-deps, close-scope, ensure-evidence, ensure-feature, ensure-impl, ensure-MG, ensure-plan, load-tree, mark/clear-impl-merged, next-impl, route | several workflow-local branch helper envelopes in `implement-merge-group.yaml` |
| **PR** | evidence / impl / MG / plan / feature open+merge results for GitHub and ADO, poll-status, post-comment, vote, validate-plan-diff, assert-impl-pr-coverage, evidence-floor | `feature-pr.yaml`, `github-pr.yaml`, and `ado-pr.yaml` still emit validators, counters, poster acknowledgements, and terminal PR envelopes by hand |
| **Plan / plan-status** | depth-guard, next-child, load-type, load-agent-guidance, review, derive-ancestor-chain, detect-state, extract-parent-patch, extract-renegotiation-flag, rebase/recreate stale descendant, seed-children, status, validate-scope, write-plan, commit-and-push, classify-stale-descendants | plan-level counters, poster acknowledgements, platform-router micro-envelopes |
| **Edges / worklist / merge-group** | `EdgesCheckResult`, worklist build result, merge-group nesting decision result | `batch-integrator.ps1` (current implementation: `batch-integrator.ps1`), `batch-dispatch-guard.ps1`, root-workflow aggregation envelopes |
| **Manifest** | init, read, topology-hash, record-rebase, record-approval, record-plan-merge, read-plan-generation, read-plan-generation-snapshot records | `manifest-bootstrap.ps1` |
| **Lock** | acquire, release, force-release, status records | none |
| **Worktree** | add, assert-clean, create, GC, init-root worktree bootstrap, list, remove, status records | `worktree-manager.ps1` |
| **Reset** | composite reset plus branches / facets / manifest / PRs / state / worktrees result records | `abort-run.ps1`, reset-workflow terminals |
| **Workflow-local orchestration** | effectively none as first-class published contracts today | actionable terminals, batch/item/polyphony terminals, PR revise counters, pending-poll counters, input validators, retry-cap emitters, reviewer-poster acknowledgements |

**Options:**

- **(a) Verb outputs only.** Extend the existing verb registry and stop there. *Pro:* smallest scope. *Con:* leaves the most failure-prone surface — shared scripts and workflow outputs — stringly.
- **(b) Verbs + shared scripts.** Type anything reused across workflows, but leave workflow outputs/locals implicit. *Pro:* captures the biggest shared helpers. *Con:* parent/child workflow contracts still live in comments and bespoke lints.
- **(c) Every cross-boundary JSON envelope, with stability tiers.** *Pro:* truly one contract surface; generic lint can reason about verb, script, and workflow boundaries uniformly. *Con:* larger rollout and a need to distinguish public contracts from internal ones.

**Recommendation: (c).** The catalog should cover every JSON object that crosses a producer/consumer boundary, but each entry carries a **stability** (`public`, `internal`, `deprecated`) so we do not accidentally promise that every counter file or terminal emitter is long-term public API.

### D2 — The four-layer architecture

AB#3255 is easiest to reason about if we split the system into four layers:

1. **Source layer** — C# records + attributes + `PolyphonyJsonContext`.
   - Every published contract is a named record.
   - The record is registered on `PolyphonyJsonContext`.
   - Verb methods keep `[VerbResult(typeof(...))]`.
   - Non-verb contracts get a new attribute such as `[EnvelopeContract("script.lifecycle-router", Kind = Script, Stability = Public)]`.

2. **Emitter layer** — CLI verbs, shared script wrappers, and workflow outputs.
   - CLI verbs return the record directly.
   - Shared scripts emit the record through a common emitter helper rather than raw `ConvertTo-Json`.
   - Child workflows declare `metadata.output_contract`, and their `output:` blocks are linted against that contract.

3. **Publication layer** — generated catalog + JSON Schema docs.
   - Build produces one aggregate catalog and optional per-contract JSON Schema files.
   - The same build also publishes an installed copy beside the CLI so external tooling can inspect it.

4. **Consumer / lint layer** — workflow routes, output maps, and generic lint.
   - The lint resolves the producer contract automatically.
   - It validates field existence, null-guard discipline, deprecated-field reads, and workflow-output completeness.
   - Per-domain contract lints disappear; only non-contract mechanics checks remain elsewhere.


### D3 — Single source of truth: where does contract authority live?

**Options:**

- **(a) C# records + YAML + script comments jointly define the contract.** *Pro:* matches today's reality. *Con:* not a source of truth at all.
- **(b) Generated JSON Schema is the authority.** *Pro:* language-neutral. *Con:* someone still has to author and review the schema source.
- **(c) C# records are authoritative; everything else is derived.** *Pro:* one review surface, one serializer pipeline, one generator story.

**Recommendation: (c).** The authoritative contract is the **C# record as serialized by `PolyphonyJsonContext`**. JSON Schema, aggregate catalogs, PowerShell emitter metadata, and lint lookup tables are all derived.

Concretely, the catalog needs four contract kinds:

- **`verb`** — emitted by a CLI command.
- **`script`** — emitted by a shared helper script.
- **`workflow`** — emitted by a child workflow's `output:` map.
- **`meta`** — CLI dispatch / parse / missing-input envelopes that are not tied to a specific verb.

That taxonomy keeps the system honest: `RequiredInputErrorResult`, `PlanValidateScopeResult`, the actionable workflow output, and `lifecycle-router.ps1` are all first-class contracts, but not the same kind of contract.

### D4 — Generalize the existing schema-generator prior art, do not replace it

The existing verb-output work already proves the hard part: attribute discovery, `PolyphonyJsonContext` cross-checking, build-time generation, JSON export, and drift diagnostics.

**Options:**

- **(a) Ship a second generator for non-verb envelopes.** *Pro:* avoids touching the existing ADR. *Con:* duplicates infrastructure and splits the catalog again.
- **(b) Rename and generalize the current generator/exporter into a contract catalog.** *Pro:* one pipeline, one artifact, one set of tests, minimal conceptual churn.

**Recommendation: (b).** `VerbOutputSchemaCatalog` evolves into a broader **`ContractSchemaCatalog`**. The current verb entries stay intact; new sections are added for script, workflow, and meta contracts. Existing `POLY1001`-style diagnostics stay; new diagnostics cover missing `EnvelopeContract` registration, workflow output-contract mismatches, and script-path registration drift.

### D5 — Serialization rules: keep the current JSON pipeline, but publish the dangerous parts explicitly

Today's serializer defaults are load-bearing:

- snake_case field names;
- `WhenWritingNull` omission by default; and
- selective `Never` overrides where a field must stay on-wire even when null.

That combination is exactly why current workflows need two-level `is defined` guards.

**Options:**

- **(a) Change global serializer behavior for AB#3255.** *Pro:* would simplify some consumers. *Con:* huge compatibility blast radius unrelated to schema publication.
- **(b) Keep current serialization behavior, but make it explicit in the published contract metadata.** *Pro:* no wire break; lint can reason about omit-when-null honestly.

**Recommendation: (b).** Each published field should expose at least:

- JSON name;
- shape (`scalar`, `object`, `list`, `map`);
- required vs optional;
- can-omit-when-null;
- deprecation metadata; and
- replacement field, when applicable.

This lets generic lint catch current failure modes without pretending nullability annotations are runtime guarantees.

### D6 — How shared PowerShell helpers emit typed contracts

This is the biggest practical fork. The source of truth is C#, but many shared helpers are still PowerShell because they compose git, `gh`, `twig`, or several CLI calls.

**Options:**

- **(a) Keep raw `ConvertTo-Json` and rely on tests/comments.** *Pro:* zero migration cost. *Con:* exactly the problem we are trying to solve.
- **(b) Rewrite every shared helper as a CLI verb immediately.** *Pro:* pure C#. *Con:* too much churn; many helpers are still ergonomically better as scripts.
- **(c) Keep helpers as scripts, but force them through a common typed emitter.** *Pro:* preserves the scripting sweet spot while removing stringly JSON authoring.

**Recommendation: (c).** Ship a shared emitter helper, e.g. `Emit-PolyphonyContract -ContractId script.lifecycle-router -Data @{ ... }`, backed by the generated catalog. Shared scripts stop calling `ConvertTo-Json` directly for published envelopes.

The first migrations should be the scripts already reused or lint-pinned across workflows: the policy resolvers, `route-actionable-executor`, `lifecycle-router`, `manifest-bootstrap`, `worktree-manager`, the batch integrator, and the remaining shared root-workflow helpers. That is where most hand-authored cross-workflow contract risk lives.

### D7 — Workflow outputs are contracts too

The current bespoke lints prove this point. They are not only checking verb fields; they are also hardcoding what child workflows promise to parents.

Examples already treated as contracts in practice:

- actionable: `satisfied`, `executor`, `pr_url`, `pr_number`, `evidence_branch`
- plan-level: `renegotiation_pending`, `renegotiation_request`, `validate_scope_verdict`, `scope_violation_files`
- feature PR / GitHub PR / ADO PR: `merged`, `pr_url`, `pr_number`, `feedback_summary`
- implement-merge-group: `merged`, `pr_url`, `pr_number`, `mg_path`
- root-fallback-gate: `root_id`, `decision`, `auto_policy_applied`
- item / batch / polyphony: rolled-up success, renegotiation, and failed-item envelopes

**Recommendation:** every child workflow that is called as a reusable unit declares a **named workflow output contract** in `metadata.output_contract`, backed by a C# record. The workflow `output:` block remains handwritten Jinja, but it is now mechanically checked against a schema just like a verb result.


### D8 — Publication shape and distribution

**Recommendation:** publish two derived artifacts from the same generator output:

1. **Aggregate catalog** — authoritative machine-readable index.
2. **Per-contract JSON Schema files** — convenience docs for humans and tools.

The aggregate catalog should record, per contract id, the contract kind (`verb` / `script` / `workflow` / `meta`), stability, CLR type, producer binding, exit behavior, and the referenced field/type graph.

**Distribution:**

- repo build artifact for lint/CI: `artifacts/contract-schemas.json` plus `artifacts/schemas/*.json`
- installed copy beside the CLI binary: `schemas/polyphony-contracts.json` plus `schemas/contracts/*.json`

That gives local lints a stable repo path and external tooling a stable installed path without inventing a second publication flow.

### D9 — Consumer binding: inference first, annotation only where needed

A generic lint only works if it can resolve "what contract does this node produce?" without reintroducing hand-maintained maps.

**Recommendation:** use **inference-first binding**:

- **`command: polyphony ...`** → resolve via verb path in the catalog.
- **`pwsh ../scripts/foo.ps1`** → resolve via script-path registration in the catalog.
- **`type: workflow`** → resolve via child `metadata.output_contract`.
- **CLI dispatch/meta envelopes** → resolved by explicit top-level contract id where needed.

Only ambiguous cases need explicit annotation. The common path stays cheap and obvious.

### D10 — Generic lint replaces bespoke envelope lints, not every workflow-specific rule

This distinction matters. The current domain lints mix three things:

1. contract-shape checks;
2. route/node-graph mechanics checks; and
3. business-policy assertions.

AB#3255 should replace **(1)** directly and only replace **(2)/(3)** where a separate generic lint already exists or is obviously warranted.

**Recommendation:** `lint-envelopes.ps1` owns:

- producer resolution;
- field-path existence checks;
- omit-when-null guard enforcement;
- deprecated-field reads;
- workflow output-contract completeness; and
- unknown-field / missing-required-field detection for shared script emitters.

It does **not** need to own every route target, every naming convention, or every policy invariant. Those stay in smaller mechanics lints. The win is that the contract logic is no longer copied six or eight times.

### D11 — Exit behavior belongs in the catalog

Today the wire story is mixed:

- some verbs are true routing-style and always exit 0;
- some emit structured JSON but still return non-zero on certain errors;
- some shared scripts follow routing-style, with a few exceptions.

If the catalog only publishes field schemas, consumers still do not know whether an envelope is guaranteed to exist after a failing producer.

**Recommendation:** every contract entry publishes an `exit_behavior` enum such as:

- `routing` — process success; errors are on-wire
- `structured_nonzero` — JSON may exist, but the process may still fail
- `process_failure` — no structured envelope guaranteed on error

That documents current reality without making AB#3255 carry the burden of normalizing it.

### D12 — Compatibility policy: dual-publish first, then tighten

The branch domain already has compatibility shims such as `current_pg`, `completed_pgs`, `remaining_pgs`, and `total_pgs`. Pretending those do not exist would make the contract catalog dishonest.

**Options:**

- **(a) Hard cut.** Remove legacy fields as soon as the catalog exists. *Pro:* clean. *Con:* high-risk churn across workflows and scripts.
- **(b) Publish legacy fields with deprecation metadata, then migrate consumers.** *Pro:* honest and reviewable.

**Recommendation: (b).** The catalog carries `deprecated_since`, `replacement`, and optionally `sunset_phase`. Generic lint starts by warning on new deprecated reads, then later upgrades to failure once all current consumers are gone.

---

## Phasing

| Phase | Scope | AC | Risk |
|---|---|---|---|
| **0** | This proposal approved | Direction settled | None |
| **1** | Generalize `VerbOutputSchemaCatalog` into `ContractSchemaCatalog`; add non-verb contract attributes; carry over current verb coverage; publish aggregate catalog + per-contract JSON Schema docs + installed copy | Existing verb-registry tests stay green; non-verb contracts can now register | Low — mostly generator/exporter work |
| **2** | Shared PowerShell emitter path + first shared wrapper migrations (`route-actionable-executor`, policy resolvers, `lifecycle-router`, `manifest-bootstrap`, `worktree-manager`, batch integrator) | No shared published wrapper still hand-authors its JSON shape | Medium |
| **3** | Add workflow output contracts for actionable, plan-level, feature PR, GitHub PR, ADO PR, implement-merge-group, root-fallback-gate, item, batch, and polyphony | Parent/child workflow boundaries become schema-backed | Medium |
| **4** | Ship `lint-envelopes.ps1`; move contract logic out of bespoke lints; keep residual mechanics checks only where still needed | Field-drift / null-guard regressions fail one generic lint, not six bespoke ones | Medium |
| **5** | Type remaining workflow-local micro-envelopes (counters, terminals, validators) as `internal`; turn deprecated-field reads into warnings | Full cross-boundary catalog exists, even for internal workflow plumbing | Medium |
| **6** | Remove dead bespoke contract lints and tighten warning→error on deprecated reads once consumers are migrated | Bespoke envelope checks gone; only mechanics lints remain | Higher — workflow churn |

The key sequencing rule: **public shared contracts first, workflow-local internal contracts second**. That gets the leverage early without forcing every low-value counter envelope into phase 1.

---

## What we are NOT deciding here

- **Whether every shared helper should eventually become a CLI verb.** AB#3255 only requires that helpers stop hand-authoring contracts.
- **Whether routing-style exit semantics should become universal.** The catalog describes the current state; a future change can normalize it.
- **Whether conductor should grow first-class failure propagation.** That is the AB#3257 track.
- **Whether the action journal consumes these contracts.** It almost certainly should, but that is AB#3254's decision.
- **Whether glossary follow-through renames every existing symbol immediately.** This doc uses the new vocabulary now; code-symbol/file-path follow-through lands separately.

---

## Open questions for you

1. **Phase-1 publication:** do you want per-contract JSON Schema files immediately, or is the aggregate catalog sufficient for the first PR as long as the file format can grow into per-contract docs without churn?
2. **Installed lookup path:** is "ship beside the binary under `schemas/`" enough, or do you also want a tiny `polyphony contract print-schema <id>` verb so tools never need to know filesystem layout?
3. **Internal micro-envelopes:** should counters/terminal emitters join the catalog in phase 3 with the workflow outputs, or explicitly wait for phase 5 so the early rollout stays focused on shared/public boundaries?
4. **Deprecation ratchet:** should deprecated-field reads start as warnings or immediate failures once the generic lint exists?

---

## Appendix — lint deletion map and first migration targets

### A. Bespoke lint deletion / slimming map

| Current lint | Contract logic it owns today | Replacement under AB#3255 | When it can die |
|---|---|---|---|
| `lint-actionable.ps1` | actionable workflow outputs + router/evidence field assumptions | `workflow.actionable` contract + `script.route-actionable-executor` contract + generic envelope lint | After phase 4, assuming any remaining node-shape checks move to a small mechanics lint |
| `lint-feature-pr.ps1` | feature PR outputs, platform-router fields, remediation counter fields | `workflow.feature-pr` + platform-router/input-validator internal contracts + generic envelope lint | Phase 4/6 split: contract half dies first; residual mechanics may remain briefly |
| `lint-github-pr.ps1` | GitHub PR outputs, poll/fixer/reviewer field assumptions, pending-poll counters | `workflow.github-pr` + internal counter/poster contracts + generic envelope lint | Phase 5 for full deletion; earlier for contract-specific sections |
| `lint-ado-pr.ps1` | ADO PR outputs, inputs validator, revise counter, stuck-review counter assumptions | `workflow.ado-pr` + internal validator/counter contracts + generic envelope lint | Phase 5 for full deletion |
| `lint-plan-level.ps1` | plan-level outputs, scope-validation outputs, renegotiation bubble-up, counters | `workflow.plan-level` + relevant internal counter contracts + generic envelope lint | Phase 4 for contract checks; residual recursion/mechanics checks may stay separately |
| `lint-implement-merge-group.ps1` | MG outputs, PR outputs, legacy compatibility field expectations | `workflow.implement-merge-group` + deprecation-aware generic lint | Phase 4 for contract checks; later once any remaining structural checks are relocated |
| `lint-root-fallback-gate.ps1` | root-fallback output envelope + terminal decision pins | `workflow.root-fallback-gate` + internal terminal contracts + generic envelope lint | Phase 4/5 |
| `lint-polyphony.ps1` | item/batch/root bubble-up outputs plus wrapper-shape assumptions for lifecycle router/worktree/manifest/batch integration | `workflow.item`, `workflow.batch`, `workflow.polyphony`, `script.lifecycle-router`, `script.worktree-manager`, `script.manifest-bootstrap`, `script.batch-integrator` + generic envelope lint | Latest, because it spans the widest surface |

End state: one lint that knows **contracts**, plus smaller lints for naming/mechanics where still valuable.

### B. First contracts to type or migrate

1. **Policy resolver wrappers** — tiny surface, reused in several workflows, immediate leverage.
2. **`route-actionable-executor`** — smallest possible shared router; good proving ground for script contract emission.
3. **`lifecycle-router`** — highest-value shared routing envelope in the root workflow path.
4. **`manifest-bootstrap` + `worktree-manager` + batch integrator** — shared orchestration wrappers currently pinned by the root-workflow lint.
5. **Workflow outputs for actionable / plan-level / feature PR / platform PRs / implement-merge-group** — deletes the most bespoke contract logic fastest.
6. **Root / batch / item outputs** — completes the bubble-up story and lets the root-workflow lint shed contract knowledge.
7. **Internal counters / validators / terminals** — last, typed as `internal`, mainly so the catalog is honest and the generic lint can cover the whole graph.

### C. Recommendation summary

1. **Generalize the existing verb-output registry; do not start over.**
2. **Treat shared scripts and workflow outputs as first-class contracts, not exceptions.**
3. **Force shared PowerShell helpers through a common typed emitter instead of raw `ConvertTo-Json`.**
4. **Publish exit behavior and deprecation metadata alongside field schemas.**
5. **Replace bespoke contract lints with one schema-driven envelope lint, while keeping non-contract mechanics checks separate.**
