# Workflow compiler investigation — roadmap / cost / sequencing

## Executive summary

- **Branch-local size check:** the current `polyphony-arch` branch has **15** registry workflow YAMLs totaling **14,125 LOC**, **295** named nodes, **186** `script` nodes, **70** human gates, **23** explicit `agent` nodes, **12** registry PowerShell scripts totaling **2,338 LOC**, and **111** `[Command]` methods in `src/Polyphony/Commands/`. The core migration burden is real, but it is concentrated: `plan-level.yaml`, `implement-merge-group.yaml`, `polyphony.yaml`, `ado-pr.yaml`, `github-pr.yaml`, and `feature-pr.yaml` account for most of the workflow mass.
- **Recommended MVP:** ship **compiler-lite**, not a full workflow compiler. Concretely: a typed C# registry for command signatures / output contracts / workflow output contracts, plus schema-driven YAML linting and route validation over the existing handwritten workflows.
- **Recommended sequencing:** **do not block the journal**; let AB#3254 continue. Use **AB#3255 as the substrate**, land **AB#3256 before any real authoring DSL**, let **AB#3258 remove the most opaque wrappers**, and treat **AB#3257 as independent**. Only after that should Polyphony attempt one **side-by-side generated workflow pilot**.
- **Long pole:** the hard problem is **semantic parity and migration discipline**, not code generation. Generating a YAML file is easy; proving parity across the giant workflows, prompts, scripts, gates, and re-entry behaviors is the actual 6-month bet.
- **Decision:** **ship the compiler-lite** now, design the full compiler as an optional follow-on, and keep explicit off-ramps after the contract/lint win and after the first generated workflow pilot.

## Baseline: what exists today

The important fact for roadmap planning is not the abstract dream of “declare everything in C#,” but the actual branch-local surface area it would have to cover.

- Workflow authoring today is explicitly a **four-step shell-out chain**: workflow YAML → PowerShell helper → `polyphony` verb → `twig` verb. The workflow-author skill calls this out as the canonical idiom.
- AB#3255 is already pushing the system toward **one typed cross-boundary contract surface**: C# records, `PolyphonyJsonContext`, generated schema/catalog output, workflow output contracts, and a generic envelope lint.
- AB#3256 is already reshaping one of the biggest authoring pain points: the **exploded PR/branch verb matrix**.
- AB#3258 is already reducing registry-script sprawl: on this branch the proposal inventory is **12 registry scripts** and **187 script nodes across 15 workflows**.
- AB#3254 has already reached the point where the journal’s effect model is explicitly described as a **“no-regrets hook for a future workflow compiler.”**

That means the repo is not starting from zero. It is already building the substrate a compiler would want. The right roadmap question is therefore: **how much additional value does C#-first workflow authoring unlock beyond the contract/lint/verb rationalization work already in flight?**

## 1) MVP options: smallest slice that delivers value

### Ranked options

| Rank | MVP option | Value unlocked | Cost to ship | Value / cost | Verdict |
|---|---|---:|---:|---:|---|
| **1** | **Compiler-lite:** typed C# registry of verb inputs + output contracts + workflow output contracts, consumed by schema-driven YAML lint / route validation across all existing workflows | High | Low | **Best** | **Ship first** |
| **2** | **Leaf workflow side-by-side generator:** author one small workflow in C#, generate today’s YAML, keep handwritten YAML beside it for diff testing | Medium | Medium | Good | Good pilot after compiler-lite |
| **3** | **Medium workflow pilot:** generate one real sub-workflow (`root-item-dispatch`, `close-out`, or similar), differential-test against current YAML | Medium-High | High | Mixed | Only after AB#3256 stabilizes |
| **4** | **Full C# DSL / JIT workflow compiler** for the whole registry | Very high | Very high | Worst initial ratio | Not an MVP |

### Commentary on each option

#### Option 1 — Compiler-lite (recommended)
This is the smallest slice that immediately pays for itself.

What it is:
- extend the AB#3255 contract catalog idea so workflow producers and consumers are typed end-to-end;
- add command/input signature metadata so YAML invocations can be validated against real verbs/flags;
- validate route conditions and output-map field paths against the catalog;
- keep handwritten YAML as the source of workflow structure.

What it buys:
- catches field drift, missing fields, null-elision mistakes, deprecated reads, bad flag names, and contract mismatches before runtime;
- reduces the need for bespoke workflow lints;
- gives Polyphony a typed C# “workflow surface registry” without forcing authors to abandon YAML.

This captures a large share of the actual pain because the current pain is mostly **stringly cross-boundary authoring**, not “YAML exists.”

#### Option 2 — Side-by-side generated leaf workflow
If Polyphony wants to test whether C#-first authoring feels better, the first generated workflow should be **small and isolated**, not `implement-merge-group.yaml`.

Good candidates:
- `root-fallback-gate.yaml`
- `close-out.yaml`
- one tiny router-style workflow with simple `workflow` / `script` / `human_gate` composition

Why this works:
- it proves the authoring model, generated YAML formatting, diff stability, and validation loop;
- it does not bet the whole pipeline on first-generation semantics;
- it creates a clean off-ramp if the DSL feels worse than YAML.

#### Option 3 — Medium workflow pilot
This is where a real compiler starts paying for itself, but also where it starts becoming a real bet.

A medium pilot should happen only after:
- typed contracts are live;
- the PR/branch surface is collapsed or stable enough;
- the low-risk wrapper scripts have already moved out of the way.

Without that, the compiler risks baking in unstable shapes.

#### Option 4 — Full workflow compiler
This is the tempting vision, but it is the wrong MVP.

A full compiler would need to model, emit, and test:
- script nodes
- workflow nodes
- human gates
- route conditions
- output maps with Jinja guards
- prompt blocks
- version metadata / min-version policy
- side-by-side compatibility during migration

That is a real program, not a proving slice.

## 2) Sequencing relative to the in-flight epic tracks

### Dependency graph

```text
AB#3254 journal 2.5  ----> optional future resource/effect-aware validation
         |                          |
         |                          +--> nice-to-have for full compiler, not blocker for compiler-lite
         |
AB#3255 typed contracts -----------> direct prerequisite / substrate for compiler-lite
         |
         +-------------------------> prerequisite for any serious generated workflow model

AB#3256 matrix collapse -----------> should land before authoring DSL over PR/branch workflows

AB#3258 script→verb migration -----> improves ROI and lowers opacity; partial prerequisite only

AB#3257 on_error migration --------> independent; compiler should not wait for it
```

### Recommended sequencing by track

#### Journal (AB#3254)
**Recommendation: run alongside; do not block.**

The journal doc explicitly says it is independent of the conductor `on_error` brief, and Phase **2.5** introduces effect metadata that is a useful future compiler hook. That means:
- **do not pause phases 3/4/5** for compiler work;
- **do not make compiler work a prerequisite** for journal payoff (drift/reset);
- **if** the compiler wants resource-awareness later, Phase **2.5** is the first sensible integration point.

In other words: the journal track is already on a payoff path. Do not derail it.

#### Typed-contract surface (AB#3255)
**Recommendation: treat as the real prerequisite.**

AB#3255 is not merely “helpful.” It is the branch-local evidence for what the compiler should probably be **instead of** doing first:
- authoritative C# records;
- generated contract catalog;
- typed script/workflow output contracts;
- generic envelope lint.

For compiler-lite, AB#3255 is basically **Phase 0/1 of the compiler**. The correct move is to **extend / consume** it, not invent a second metadata system.

#### PR/branch matrix collapse (AB#3256)
**Recommendation: land phases 1-2 before authoring DSL work.**

This proposal explicitly says AB#3255 should preferably land first, and it collapses **26 matrix-expanded verbs** into **five canonical verbs**. A workflow compiler that arrives *before* this would encode soon-obsolete surface area into its authoring API.

So:
- compiler-lite can happen before or in parallel, because it validates existing YAML against today’s shapes;
- any **C# workflow DSL** should wait until the matrix collapse has landed enough of the new public surface to be a stable target.

#### on_error migration (AB#3257)
**Recommendation: do not wait; do not absorb it.**

The inventory says all **19** current error gates are trivial direct translations once conductor lands `on_error`. That means the compiler does **not** fundamentally change the calculus.

The worst move would be to smuggle an error-routing abstraction into the workflow compiler just because conductor is temporarily blocked. That would entangle two architectural bets and duplicate upstream semantics.

#### Script→verb migration (AB#3258)
**Recommendation: partial parallel prerequisite; not all-or-nothing.**

A compiler can technically treat scripts as opaque references. So AB#3258 is **not** a hard prerequisite.

But it strongly affects cost and payoff:
- WRAP-A-VERB deletions and low-risk COMPOSE-VERBS shrink opaque shell-out surfaces;
- fewer scripts means less “foreign” behavior the compiler must represent as opaque boxes;
- it also shrinks the two-system tax later.

So the recommended posture is:
- **do not wait for all 12 scripts to be migrated**;
- **do wait for the low-risk contract-driven slices** so the first workflow pilot sees a cleaner surface.

### Recommended Gantt-ish sequence

| Track | Now | Next 2 weeks | Weeks 3-6 | Months 2-3 | Months 4-6 |
|---|---|---|---|---|---|
| **AB#3254 journal** | Phase 2.5 in flight | Phase 3 | Phase 4 | Phase 5 payoff | Optional Phase 6 only if still wanted |
| **AB#3255 typed contracts** | Proposal | Phase 1 | Phases 2-3 | Phase 4 generic lint | Phases 5-6 cleanup |
| **AB#3256 matrix collapse** | Proposal | Phase 1 handlers/shims | Phase 2 workflow migration | Phase 3 docs/tests | Phase 4-5 deprecations/removal |
| **AB#3258 script→verb** | Proposal | CT1/CT2 substrate | WRAP-A-VERB deletions + low-risk migrations | Git-heavy migrations | Cleanup/lint |
| **AB#3257 on_error** | Upstream blocked | Wait / inventory only | If conductor lands, migrate trivial gates | Finish | Done |
| **Compiler-lite** | Not started | Design note + success criteria | Implement against AB#3255 substrate | Roll into CI and lints | Stop here unless pilots prove more value |
| **Generated workflow pilot** | Not started | — | Choose leaf workflow | Side-by-side pilot + diff tests | Decide go / no-go for broader compiler |

## 3) What is the long pole?

### Ranked long poles

1. **Migrating the giant workflows with semantic parity**
2. **Replacing the handwritten YAML representation entirely**
3. **Prompt / human-gate / route authoring ergonomics**
4. **Two-system migration management**
5. **Script migration for the remaining opaque helpers**
6. **User retraining / debugging generated output**
7. **Replacing conductor entirely** (this is a separate program, not the long pole of this one)

### Why the giant workflows are the real long pole

The branch-local inventory is lopsided:
- `plan-level.yaml` — **2,928** lines
- `implement-merge-group.yaml` — **2,240**
- `polyphony.yaml` — **1,530**
- `ado-pr.yaml` — **1,383**
- `github-pr.yaml` — **1,224**
- `feature-pr.yaml` — **1,180**

Those six files are ~80% of the workflow LOC. So a compiler can “support workflows” long before it supports the workflows that actually matter operationally.

### Replacing YAML entirely
This is harder than “generate YAML.” It means the C# source model must fully cover the conductor features Polyphony actually uses, and produce readable, diff-stable, reviewable YAML. The risk is not emission; the risk is **semantic completeness and debuggability**.

### Replacing conductor entirely
This should be explicitly excluded from the roadmap. A workflow compiler that still targets conductor is one architectural bet. Replacing conductor is a second, much larger one. Bundling them is how a 6-week rationalization turns into a 12-month rewrite.

### Agent prompt generation
This is a medium/high pole because prompts are not just strings; they are part content, part routing contract, part operator UX. Polyphony currently has **23** explicit agent nodes and **88** prompt markers. Prompt text is also the place where humans still read the system most directly. That argues for keeping prompt authoring conservative even if structural YAML gets generated later.

### Dashboard / event log changes
If the compiler only emits today’s YAML, this is low. If the compiler changes execution shape or introduces hidden generated structure, observability cost rises immediately. That is another reason to keep the first pilot leaf-sized and side-by-side.

## 4) Cost model

### A useful unit: PR-sized “agent-run equivalent”

The journal track gives a good calibration for Daniel’s current shipping cadence:
- **PR #501 (Phase 1A):** 25 changed files, +1458/-1 — infra slice
- **PR #502 (Phase 1B):** 35 changed files, +2029/-763 — first real adoption slice
- **PR #503 (Phase 2):** 74 changed files, +3687/-1036 — broad domain rollout

That suggests a practical unit: **one focused PR-sized implementation pass with validation**.

### Estimated cost by scope

| Scope | Agent-run equivalents | Human review load | Comment |
|---|---:|---:|---|
| **Compiler-lite** | **2-4** | **2-3 review sessions** | Feels like one AB#3255-extension slice plus one lint/CI slice |
| **Leaf workflow side-by-side pilot** | **2-3 more** | **2 more review sessions** | Mostly authoring model + diff testing + docs |
| **Medium workflow pilot** | **3-5 more** | **3-4 review sessions** | First real semantic-parity cost shows up here |
| **Full migration of all workflows** | **12-18 total** | **10-15 review sessions** | Dominated by the six large workflows and migration tax |
| **“Replace YAML entirely” end state** | **18-24 total** | **high / ongoing** | Only worth it if early pilots are clearly superior |

### Ongoing maintenance cost

#### Today
Today’s maintenance cost is high because a non-trivial workflow change often spans:
- YAML structure
- PowerShell wrapper or helper
- CLI DTO / result shape
- bespoke lint assumptions
- version floor management

The skills and proposals all point at the same problem: too many contracts are implicit.

#### After compiler-lite
Maintenance cost drops meaningfully because the **implicit contracts become explicit and machine-checked**. Importantly, this reduces the expensive class of failures without forcing the team to relearn authoring.

#### After a full compiler
Long-term maintenance might be lower, but only **after** enough workflows migrate. During migration it is definitely **higher** because Polyphony would own both:
- the compiler / DSL / emitter / diff tests; and
- the still-handwritten workflows.

So the full compiler has an unavoidable **two-system tax** for a while.

### Cost of *not* doing this

There is a real “do nothing” cost:
- field drift and null-elision bugs remain a runtime discovery problem;
- bespoke lints keep multiplying;
- the workflow author still reasons across YAML, PowerShell, CLI verbs, and DTO shapes;
- matrix-expanded verbs and wrapper scripts continue to leak topology into authoring.

But the important nuance is: **most of that cost is already targeted by AB#3255 + AB#3256 + AB#3258.** That is why “full compiler now” is not the best ROI move.

## 5) Risk register

| Risk | Severity | Likelihood | Mitigation |
|---|---|---|---|
| Big-bang migration stalls at 50%; Polyphony pays a two-system tax indefinitely | High | High | No big-bang. Require side-by-side mode, per-workflow pilots, and explicit stop/go gates |
| Compiler authors the wrong public surface because AB#3256 has not landed yet | High | High | Do compiler-lite first; defer authoring DSL until matrix collapse phases 1-2 land |
| C# DSL is less ergonomic than YAML for routes/gates/prompts; authors reject it | High | Medium | Pilot on a leaf workflow first; keep handwritten YAML canonical until ergonomics are proven |
| Generated YAML is semantically correct but diff-noisy / hard to review | High | Medium | Require stable formatting, golden outputs, and differential tests before broader rollout |
| Some workflows or scripts must stay handwritten, creating permanent two-system tax | Medium | High | Treat compiler as opt-in by workflow family; measure whether the remaining handwritten set is acceptable |
| Conductor changes break generator assumptions | Medium | Medium | Keep compiler target limited to current used constructs; maintain compatibility tests against real registry YAML |
| Source-generator / AOT edge cases block shipping | Medium | Low-Medium | Reuse the existing schema-generator pattern; avoid inventing a second generator stack |
| Validation surface explodes and CI gets materially slower | Medium | Medium | Keep compiler-lite as metadata/lint first; cache catalogs; run differential generation only for pilot workflows |
| Debugging generated workflows becomes harder than debugging handwritten YAML | Medium | High | Keep generated YAML checked in or exportable; never hide the emitted artifact |
| Prompt generation becomes brittle or unreadable | Medium | Medium | Keep prompt bodies external / conservative; generate structure before generating prose containers |
| on_error lands mid-pilot and forces route model churn | Low | Medium | Keep error semantics out of the compiler’s first scope; preserve current YAML shapes |
| Conflating “workflow compiler” with “replace conductor” turns scope into a rewrite | High | Medium | Explicitly forbid conductor replacement in the compiler roadmap |

## 6) Off-ramps

Polyphony can stop at several points with useful wins retained:

1. **After compiler-lite ships**
   - Keep the typed catalog, generic lint, and signature validation.
   - Stop there if the runtime defect/friction reduction is already good enough.

2. **After one leaf workflow pilot**
   - If the DSL feels worse than YAML, delete the pilot compiler layer and keep compiler-lite.
   - The typed-contract work still stands on its own.

3. **After one medium workflow pilot**
   - If semantic-parity testing is too costly, keep the pilot as an isolated exception or revert it.
   - Do not proceed to the giant workflows.

4. **Before any handwritten YAML is deleted**
   - This is the critical kill switch. As long as handwritten YAML remains canonical or side-by-side, the project can walk back with low sunk cost.

## 7) The Daniel-shape lens: right phase shape

The journal proposal is the precedent: decisive ADR, narrow phases, immediate payoff slices, and explicit 2.5-style uplift when the first design proves incomplete.

That suggests the compiler roadmap should look like this:

- **Phase 0 — brief + kill criteria**
  - Define success as fewer runtime contract errors, not “all workflows in C#.”
- **Phase 1 — compiler-lite substrate**
  - Extend AB#3255’s contract catalog and validation surface.
- **Phase 1.5 — leaf workflow smoke test**
  - Prove authoring ergonomics and generated diff stability.
- **Phase 2 — stop/go checkpoint**
  - If ergonomics are bad, stop.
- **Phase 3 — one medium workflow**
  - Only after AB#3256 has stabilized the target surface.
- **Phase 4 — broader adoption or stop**
  - Migrate only if Phase 3 shows clear net benefit.

That is the Daniel-shaped version of this work: **small, reviewable, payoff-first slices**, not a “trust me, six months from now this will all be nicer” rewrite.

## 8) The 6-month bet and 12-month end state

### If everything goes well: 6 months

A realistic strong outcome at 6 months is **not** “all workflows are now authored in C#.” It is:
- AB#3255 landed and generic envelope lint is authoritative;
- AB#3256 landed enough that PR/branch authoring targets are stable;
- AB#3258 deleted the wrapper-class scripts and some low-risk compose-verbs;
- compiler-lite is live in CI;
- 1-3 small workflows are generated side-by-side;
- Polyphony has enough data to know whether further migration is worth it.

That is already a win.

### If everything goes well: 12 months

A plausible 12-month end state is:
- most first-party workflow structure is authored in C# or a C#-backed IR;
- generated YAML is either checked in as an artifact or emitted at build time;
- prompts remain conservative (external files or embedded resources, but not heavily abstracted);
- registry scripts are down to true external shims;
- generic contract lint replaces most bespoke contract checks.

Even then, conductor still exists. The compiler changes authoring, not the runtime engine.

### Walking backward from that end state

1. **Month 0-1:** ship compiler-lite
2. **Month 1-2:** finish typed-contract rollout and matrix stabilization
3. **Month 2-3:** leaf workflow pilot + differential tests
4. **Month 3:** stop/go decision
5. **Months 4-6:** medium workflow pilot, maybe one more if the first is clearly better
6. **Months 6-12:** only if the pilots are successful, start migrating the large core workflows

## 9) The kill-switch version (3 months in)

If, three months in, Polyphony decides the compiler is not worth it, the desired state should be:
- AB#3255 work remains fully valuable;
- AB#3256 and AB#3258 remain fully valuable;
- AB#3254 remains fully valuable;
- any generated pilot workflows can be turned off without breaking runtime behavior;
- the team loses only the pilot/compiler implementation effort, not the broader rationalization work.

That is why the compiler must be a **consumer** of the rationalization substrate, not its owner.

## 10) The smaller alternative: compiler-lite

**Defend it.** The smaller alternative is the right decision.

Why:
- the branch-local proposals already show that the biggest current pain is **contract drift and multi-surface maintenance**, not the existence of YAML itself;
- AB#3255 already points toward the high-value answer: typed C# contracts + generated catalog + generic lint;
- AB#3256 and AB#3258 reduce the authoring surface independently of a compiler;
- a full compiler creates a large migration/debugging burden before it proves additional value.

My estimate is that compiler-lite captures **most of the value** that motivated the workflow-compiler idea — probably **60-80%** of the practical pain reduction — for something like **10-20% of the cost and risk**.

That is exactly the sort of trade Daniel’s current cadence is optimized for.

## Recommended decision

**Ship the compiler-lite.** Treat a full workflow compiler as a follow-on experiment gated behind: (1) typed-contract rollout, (2) PR/branch surface stabilization, and (3) one side-by-side generated workflow pilot that proves authoring ergonomics and differential-testability.

## Open questions for the synthesis step

1. Which single **leaf workflow** is the best pilot: `root-fallback-gate`, `close-out`, or another small workflow?
2. Should compiler-lite include **input/flag signature validation** immediately, or only output-contract validation first?
3. Should generated YAML be **checked in** for reviewability, or emitted just-in-time with a golden diff artifact?
4. Does the compiler eventually own **prompt container structure** only, or prompt text too?
5. Is journal Phase **2.5** enough metadata for any future effect-aware compiler checks, or should the compiler ignore journal semantics entirely at first?
6. What is the explicit **stop condition** after the first generated workflow pilot: noisy diffs, bad ergonomics, slow CI, or weak payoff?
7. Does the team want the compiler to target only first-party workflows, or also become a public authoring surface for downstream repos?
