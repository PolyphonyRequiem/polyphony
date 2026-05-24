---
doc_type: discussion
status: exploratory
synopsis: Compiler-lite investigation — how a C# workflow authoring surface would relate to the conductor runtime and the polyphony domain brain.
---

# Workflow Compiler Investigation — Relationship to the Conductor Engine

## Executive summary

- Today, conductor is the **runtime** and polyphony is the **domain brain**: the launcher runs `conductor run apex-driver@polyphony --web ...`, while workflow YAML calls polyphony/twig verbs and routes on their JSON envelopes (`scripts/Invoke-PolyphonySdlc.ps1:889-926`, `.github/skills/polyphony-workflow-author/SKILL.md:23-41,79-119`).
- The current workflows depend on conductor for the things polyphony does **not** currently own: graph execution, `for_each`, sub-workflows, human gates, the web dashboard, stop/cancel plumbing, and the workflow event log (`.conductor/registry/workflows/apex-driver.yaml:603-609,1171-1216,1599-1621`; conductor README / workflow-syntax docs).
- The journal/drift/reset work is explicitly **not** a conductor replacement story: the journal proposal says it is not a replacement for conductor events, carries a `run_id` so the two logs can be joined, and treats `on_error` as an independent track (`polyphony-journal.md:51-52,194,406-408,548-553`).
- **Recommended option: A** — declare workflows in C#, compile deterministically to YAML, and keep conductor as runtime. It captures most of the workflow-compiler upside while avoiding a large runtime rewrite or a cross-repo protocol project.
- Keep **Option D** as the long-term stretch path if the compiler proves valuable and typed cross-boundary contracts become the dominant pain. Avoid **Option B** unless “no YAML in source control” is a hard requirement; avoid **Option C** for now.

---

## Current relationship: what conductor actually does today

The present split is sharper than “conductor runs some YAML.” Conductor is the execution shell around a polyphony-native decision layer.

- The launcher is explicitly a conductor launcher: `Invoke-PolyphonySdlc.ps1` describes itself as the canonical wrapper for `conductor run apex-driver@polyphony`, pins `--web` and `--web-port`, injects workflow inputs and metadata, and relies on conductor’s dashboard and stop API (`scripts/Invoke-PolyphonySdlc.ps1:4-13,889-926`).
- `apex-driver.yaml` uses conductor’s top-level `for_each` for wave iteration, nested sub-workflows for decomposition, and human gates for conflicts, renegotiation, preflight failures, and apex completion (`apex-driver.yaml:564-602,650-765,808-930,953-994,1041-1141,1171-1216,1599-1621`).
- `feature-pr.yaml` uses conductor to branch between platform-specific PR lifecycles, invoke sub-workflows (`github-pr.yaml`, `ado-pr.yaml`, `implement-merge-group.yaml`), host LLM reviewer/updater agents with typed `output:` schemas, and funnel full-run aborts through conductor’s `/api/stop` path (`feature-pr.yaml:289-316,471-535,1025-1046,1071-1278`).
- `implement-merge-group.yaml` shows the same pattern at another scale: conductor hosts coder/reviewer/scope-reviewer agents, retry/cap gates, policy routers, and the MG PR lifecycle (`implement-merge-group.yaml:678-760,1608-1705,1904-2036,2237-2430`).
- The registry itself is a conductor artifact: `.conductor/registry/index.yaml` maps workflow names to YAML paths and versions, and CI enforces version/index consistency (`.conductor/registry/index.yaml:1-62`, `.github/workflows/ci.yml:163-179`).

That means the question is not “should polyphony own the source text?” but “should polyphony also own the execution semantics conductor currently supplies?”

---

## Option A — Generate YAML at build time; conductor stays the runtime

### 1. Boundary

Polyphony gains a typed workflow DSL in C#. An MSBuild target emits deterministic YAML into `.conductor/registry/workflows/*.yaml` plus the registry index entry. The emitted YAML remains the artifact conductor reads. Operationally, the boundary remains exactly what it is now: conductor consumes YAML; polyphony verbs and helper scripts do the domain work.

The strongest version of A is: **typed source is authoritative, emitted YAML is compiled artifact**. During transition, the emitted YAML can stay checked in so registry-based execution (`apex-driver@polyphony`) and PR review remain unchanged.

### 2. What changes in polyphony

- Add workflow declaration types, a generator, and tests that compare emitted YAML against expected output.
- Add a “do not hand edit” discipline for emitted YAML.
- Optionally add `polyphony workflow render` or `polyphony workflow explain` for debugging generated output.
- Keep all existing domain verbs (`state next-ready`, `edges check`, `branch ensure-*`, `pr *`, `policy load`, etc.) exactly where they are.

This directly targets the current authoring pain: workflow authors today manually encode conductor mechanics and footguns — explicit catch-alls, output-shape rules, `for_each` result aggregation, temp-file loop counters, non-templated workflow paths, and StrictUndefined guards (`conductor-mechanics` M2-M10; `polyphony-workflow-author` apex-driver guidance at lines 181-260).

### 3. What changes in conductor

Almost nothing. Conductor still parses YAML, validates it, runs it, and renders the dashboard. The only change might be social: the conductor team no longer reviews hand-authored YAML as primary source, but they do not need new runtime features to keep polyphony working.

### 4. What the dashboard sees

Exactly what it sees today: a normal conductor workflow DAG. That is a major advantage. The dashboard, gate UI, event stream, and sub-workflow breadcrumbing remain intact because conductor is still executing native artifacts.

### 5. What an operator runs

Ideally, **no operator change**. `./scripts/Invoke-PolyphonySdlc.ps1 -ApexId N` still resolves worktrees and invokes `conductor run apex-driver@polyphony --web ...` (`Invoke-PolyphonySdlc.ps1:889-926`).

The only difference is when emission happens:
- checked-in compiled YAML: emission happens in CI/dev before commit;
- generated-on-build registry bundle: emission happens before running conductor.

### 6. Failure modes / debugging story

This is A’s main weakness. Runtime failures still occur in YAML-space even if authors write C#.

Mitigations are available:
- emit deterministic YAML with source-map comments or metadata;
- preserve node names so dashboard/runtime logs still map cleanly;
- archive emitted YAML beside run transcripts for post-mortem.

The good news is that many current failure classes can move earlier: the compiler can guarantee output schemas, fallback routes, reserved-name avoidance, correct `for_each` scaffolding, and consistent policy-router shapes before conductor ever runs.

### 7. Migration cost / risk

**Low to medium.** This is largely polyphony-internal. Existing workflows can be ported one at a time. Rollback is trivial: stop emitting; keep the last known-good YAML. The main risk is dual-source drift, especially if emitted YAML remains checked in. CI already has a version-drift mindset for the workflow bundle (`ci.yml:163-179`); the compiler can extend that into “generated YAML matches source” enforcement.

### 8. Which goals it best satisfies

- **Workflow compiler:** high.
- **Agent contract safety:** medium. Compile-time authoring gets safer, but conductor still executes erased YAML/JSON contracts.
- **Drift detection / reset transactionality:** high compatibility, because those are polyphony-internal goals anyway.
- **Operational continuity:** very high.

**Bottom line:** this is the highest-value, lowest-regret first move.

---

## Option B — Generate YAML at runtime (JIT); conductor stays the runtime

### 1. Boundary

The authoritative workflow lives inside the polyphony binary. Before dispatch, polyphony renders a run-specific YAML file and conductor executes that ephemeral artifact. The boundary is still YAML, but it becomes **late-bound**.

Concretely: the launcher would become “prepare worktree → render YAML into run state → call conductor on the rendered file.”

### 2. What changes in polyphony

- Everything from Option A, plus a render command and run-scoped storage for rendered artifacts.
- The launcher must invoke the renderer before conductor.
- Polyphony must preserve the rendered artifact for debugging, because without it the actual executed workflow is invisible.

### 3. What changes in conductor

Little to none if conductor can run a file path directly. But it no longer benefits from the registry-centered steady-state that polyphony uses today (`index.yaml`, named workflows, version bundle discipline). If polyphony wants to keep `apex-driver@polyphony`, it would need to materialize a registry shape on the fly, which is extra plumbing for little gain.

### 4. What the dashboard sees

The dashboard still sees a normal conductor DAG, but only **after render**. That means the run plan is not inspectable until polyphony has already rendered it. For operators, “what workflow am I about to run?” becomes less transparent unless the launcher prints or stores the rendered plan path.

### 5. What an operator runs

The launcher can still hide the change, but operationally the true sequence becomes:

1. `polyphony workflow render --apex N --intent resume ...`
2. `conductor run <rendered>.yaml --web ...`

So the public CLI can remain stable, but the runtime becomes more implicit.

### 6. Failure modes / debugging story

This is B’s big downside. Most runtime failures now require three artifacts to understand:
- the typed declaration,
- the render inputs,
- the rendered YAML actually executed.

This is worse than A because the compiled artifact is no longer a reviewable, versioned file in the repo. A JIT renderer only pays for itself if per-run graph specialization is genuinely necessary.

In the current system, most run-specific data is already modeled as **inputs**, not structure: `apex_id`, `intent`, `platform`, `organization`, `project`, `repository` are all workflow inputs today (`apex-driver.yaml:146-193`). That limits B’s upside. You do not need JIT graph generation just to vary those values.

### 7. Migration cost / risk

**Medium.** Still mostly polyphony-internal, but the operational model is more fragile than A. You must archive render outputs, decide where they live, and explain them in tooling. Rollback is still easy, but live-debugging gets harder.

### 8. Which goals it best satisfies

- **Workflow compiler:** high.
- **Agent contract safety:** medium, same reason as A.
- **Drift/reset:** high compatibility, but no better than A.
- **“No YAML in repo” goal:** high.
- **Operator/debugging clarity:** lower than A.

**Bottom line:** B is mostly “A, but less inspectable.” It only wins if eliminating source-tree YAML is itself a primary requirement.

---

## Option C — Polyphony becomes the runtime; conductor shrinks or disappears

### 1. Boundary

There is effectively no YAML boundary anymore. Polyphony owns workflow declaration **and** execution. Conductor either disappears, becomes a thin dashboard/agent adapter, or is forked into reusable pieces.

### 2. What changes in polyphony

A lot:
- DAG execution engine;
- route evaluation and templating;
- human-gate protocol and persistence;
- sub-workflow composition;
- loop safety and iteration accounting;
- checkpoint/resume semantics;
- parallel/for_each runtime;
- provider/model/tool orchestration;
- dashboard/event-stream protocol;
- cancellation/abort plumbing.

Today conductor owns all of that. The current workflows visibly depend on those semantics: top-level `for_each`, `workflow` nodes, human gates, dashboard/web mode, workflow metadata, and clean unwind via `/api/stop` (`apex-driver.yaml:1599-1621`; `feature-pr.yaml:1233-1278`; `Invoke-PolyphonySdlc.ps1:891-908`).

### 3. What changes in conductor

Either none because it is abandoned, or a great deal because it is repurposed into a library/UI shell. In practice, this is the most politically disruptive option because it either sidelines conductor or forces a substantial refactor of a sibling project around polyphony’s needs.

### 4. What the dashboard sees

This becomes a product question, not an implementation detail. The current conductor dashboard is a meaningful asset: live DAG, node state, agent detail, in-browser gates, stop button, metadata-driven observability (conductor README and CLI/workflow docs). Option C must either:
- recreate that in polyphony,
- preserve conductor only as a dashboard/event consumer,
- or accept a temporary regression.

### 5. What an operator runs

Likely a single polyphony-native command such as `polyphony sdlc dispatch --apex N`, with no conductor invocation underneath.

That is elegant, but only after reimplementing a large amount of machinery.

### 6. Failure modes / debugging story

C’s best argument is conceptual purity. There is one source language, one runtime, one type system, one debugger. Typed flow can extend across the whole orchestration surface.

But it trades that purity for a new class of failures: runtime bugs that conductor has already learned the hard way. The conductor mechanics skill is essentially a catalog of execution footguns discovered in practice (`conductor-mechanics/SKILL.md:6-113`). Option C signs polyphony up to rediscover or reimplement those semantics.

### 7. Migration cost / risk

**Very high.** This is a scope expansion, not a compiler project. It also increases long-term ownership burden: polyphony becomes responsible not just for SDLC domain logic but for general multi-agent orchestration runtime behavior.

### 8. Which goals it best satisfies

- **Workflow compiler:** very high.
- **Agent contract safety:** very high.
- **Drift/reset:** high, but mostly because everything is in one product — not because those goals inherently require this move.
- **Migration risk / time-to-value:** poor.

**Bottom line:** C is only justified if the actual decision is “polyphony should be an orchestration platform.” The current evidence does not support that leap.

---

## Option D — Polyphony emits richer execution plans; conductor evolves to consume them

### 1. Boundary

Polyphony no longer emits plain YAML as the durable contract. Instead it emits a typed execution-plan artifact — likely JSON — containing the DAG, schemas, source maps, and perhaps declared resource capabilities. Conductor evolves from a YAML parser/runtime into a runtime that can also consume this richer plan.

This is the cleanest shared-future story.

### 2. What changes in polyphony

- Build the typed DSL/compiler.
- Define and serialize a versioned execution-plan schema.
- Decide what stays as runtime input versus what becomes plan-specialized.
- Potentially include source maps back to C# declarations.

The journal proposal hints at this direction indirectly: typed resource effects and verb capabilities are described as a “no-regrets hook for a future workflow compiler” (`polyphony-journal.md:486-492`).

### 3. What changes in conductor

Conductor must grow a second ingestion path. Today its public contract is YAML-centric: `conductor run <workflow.yaml>`, YAML syntax reference, YAML hooks, YAML sub-workflows, YAML validation, registry indexes that point to YAML (`README`, `workflow-syntax.md`, `cli-reference.md`, `.conductor/registry/index.yaml`). D therefore requires explicit conductor evolution.

### 4. What the dashboard sees

Potentially something better than today. If the plan carries source locations, typed outputs, and semantic step metadata, the dashboard could show richer debugging information than raw YAML ever can.

### 5. What an operator runs

A plausible operational flow is:
- launcher invokes `polyphony workflow plan --apex N ...`;
- conductor runs the generated plan: `conductor run-plan <plan.json> --web ...`.

From the operator’s perspective this can still be hidden behind `Invoke-PolyphonySdlc.ps1`, but unlike B the artifact is no longer “secret YAML”; it is a first-class plan contract.

### 6. Failure modes / debugging story

D gives the best debugging story short of C, because it preserves a compiled artifact while avoiding YAML’s weakest parts. Failures can point to:
- typed source declaration,
- execution-plan node id,
- conductor runtime event.

The downside is version skew: if polyphony emits plan schema v7 and conductor only understands v6, dispatch fails before work begins.

### 7. Migration cost / risk

**Medium to high.** Technical risk is manageable, but coordination risk is real. Two sibling repos now share a versioned protocol. That means release sequencing, compatibility policy, and likely duplicated tests across repos.

### 8. Which goals it best satisfies

- **Workflow compiler:** very high.
- **Agent contract safety:** high to very high.
- **Drift/reset:** high compatibility; can surface more metadata but still fundamentally polyphony-owned.
- **Cross-project coordination cost:** high.

**Bottom line:** D is the best long-term collaborative architecture, but not the best first move.

---

## Cross-cutting answers

### What conductor uniquely provides today?

| Capability | Evidence | A | B | C | D |
|---|---|---|---|---|---|
| Graph runtime / route engine | YAML routes, sub-workflows, `for_each` (`apex-driver.yaml`, `feature-pr.yaml`) | Preserve | Preserve | Replace | Preserve/evolve |
| Human-gate UI | many `human_gate` nodes; markdown prompts | Preserve | Preserve | Replace | Preserve |
| Web dashboard | `--web`, live DAG, in-browser gates (README / CLI docs) | Preserve | Preserve | Replace/unclear | Preserve |
| Workflow event log | journal doc says conductor owns it (`polyphony-journal.md:51-52,406-408`) | Preserve | Preserve | Replace | Preserve |
| Agent dispatch protocol | conductor agents with model/tools/output schema | Preserve | Preserve | Replace or wrap | Preserve |
| Parallel / `for_each` / sub-workflow execution | core runtime feature (`apex-driver.yaml:1599-1621`) | Preserve | Preserve | Replace | Preserve |
| Stop/cancel API | dashboard stop button, `/api/stop`, abort-run integration (`feature-pr.yaml:1233-1278`) | Preserve | Preserve | Replace | Preserve |
| Registry resolution / versioned bundle | `.conductor/registry/index.yaml`, `conductor run apex-driver@polyphony` | Preserve | Often bypass | Replace | Evolve |

### What conductor does **not yet** provide that polyphony wants

1. **End-to-end typed node contracts.** Conductor can validate some YAML references and parse LLM JSON into declared `output:` schemas, but the practical contract is still fragile enough that polyphony carries a full mechanics skill for output-shape, routing, `for_each`, and StrictUndefined pitfalls (`conductor-mechanics/SKILL.md:33-104`). This motivates a compiler, but not necessarily a new runtime.
2. **First-class loop / retry / error-routing ergonomics.** `apex-driver` still uses temp-file counters for outer loops because conductor has no first-class loop primitive (`conductor-mechanics` M10). The on-error migration inventory shows 19 trivial error gates waiting on the upstream `on_error` work (`on-error-migration-inventory.md:9-27,73-127`). This argues for either upstreaming improvements or hiding them behind generated YAML — not automatically for replacing conductor.
3. **Polyphony-specific drift/journal semantics.** These should stay in polyphony. The journal proposal explicitly says the journal is not a replacement for conductor events and that routing-layer journal queries are deferred (`polyphony-journal.md:367-376,406-408,548-556`).
4. **Domain-aware lifecycle hooks.** Generic runtime hooks (`on_start`, `on_complete`, `on_error`) belong upstream in conductor’s runtime surface; domain hooks like journal projections, reset authority, and drift folds belong in polyphony.

### Political / coordination cost

- **A:** lowest. Mostly internal to polyphony; conductor remains an independent runtime.
- **B:** still low, but with more launcher/ops churn.
- **C:** technically internal, but strategically high-friction because it sidelines or duplicates the sibling conductor project.
- **D:** highest explicit coordination cost because it creates a versioned inter-repo protocol.

### The conductor `on_error` work

The open `on_error` brief matters because it reduces a lot of today’s YAML boilerplate. But it does **not** force Option C. In fact, it weakens the case for C: if conductor is already moving to better error-routing primitives, polyphony can let the compiler target those primitives as they arrive. A compiler-to-conductor strategy (A now, D later if desired) can absorb this evolution with less disruption than a runtime rewrite.

### The journal / drift work

The journal work is overwhelmingly a polyphony concern. Its key claims are:
- not a replacement for conductor events;
- joinable via `run_id`;
- useful even without workflow-layer journal queries;
- forward-compatible with inversion, but not a commitment to it (`polyphony-journal.md:51-52,194,367-376,406-408,548-553`).

That pushes the ranking toward **A first**. Drift detection and reset transactionality do not require conductor to understand the journal, and therefore do not justify Option C or D by themselves.

---

## Comparison table — options vs. user goals

| Option | Workflow compiler | Agent contract safety | Drift detection | Reset transactionality | Operator continuity | Coordination cost |
|---|---|---|---|---|---|---|
| **A. Build-time YAML** | **High** | Medium | **High** (unchanged polyphony substrate) | **High** | **Very high** | **Low** |
| **B. JIT YAML** | **High** | Medium | **High** | **High** | Medium | Low |
| **C. Polyphony runtime** | **Very high** | **Very high** | High | High | Low | Medium politically / **Very high** engineering |
| **D. Typed execution plan** | **Very high** | **High–Very high** | **High** | **High** | High | **High** |

Short read:
- If the primary goal is **compiler ergonomics without product upheaval**, pick **A**.
- If the primary goal is **removing YAML from source control at all costs**, pick **B**.
- If the primary goal is **turning polyphony into an orchestration platform**, pick **C**.
- If the primary goal is **shared evolution of a typed orchestration boundary**, pick **D**.

---

## Recommended option

## Recommend **Option A now**, with **Option D as the only credible later upgrade path**

### Defense

Option A best matches the evidence.

1. **It addresses the real pain.** The pain shown in this codebase is mostly authoring pain: conductor mechanics, YAML boilerplate, router ceremony, output-shape footguns, loop scaffolding, and duplicated patterns across workflows. A typed compiler can absorb those patterns without throwing away conductor’s runtime assets.
2. **It preserves the current operational win.** The launcher, registry, dashboard, stop API, event log, and operator workflow all stay recognizable. That matters because polyphony already has a non-trivial operational wrapper around conductor (`Invoke-PolyphonySdlc.ps1:4-13,889-926`).
3. **It does not over-read the journal work.** The journal proposal explicitly refuses to make inversion part of the decision. Drift/reset are polyphony’s domain, not conductor’s.
4. **It keeps coordination cheap.** Polyphony can move independently and still benefit from upstream conductor improvements like `on_error` later.
5. **It leaves room for D.** If the compiler proves its worth and the remaining pain is truly the YAML boundary itself, A can evolve into D deliberately instead of leaping straight to C.

### Three most important risks to mitigate

1. **Dual-source ambiguity.** If generated YAML is checked in, humans may edit it by hand.
   - Mitigate with generated-file headers, CI that rejects manual drift, and “source of truth = C#” policy.
2. **Debugging split-brain.** Runtime failures will still surface in compiled YAML terms.
   - Mitigate with source-map comments/metadata, deterministic node naming, and a `polyphony workflow explain/render` command that preserves the exact emitted artifact.
3. **Generator lock-in to current conductor quirks.** If the compiler merely bakes today’s workarounds forever, it becomes fossilized.
   - Mitigate by treating conductor as a backend target: compiler IR should be more semantic than the emitted YAML so future backends (improved YAML target, plan target, or D-style protocol) remain possible.

---

## Open questions for the synthesis step

1. **Should emitted YAML be checked in, or only generated into a build/registry output?** This is the biggest operational choice inside Option A.
2. **Do we want the compiler’s internal IR to be conductor-shaped or domain-shaped?** A domain-shaped IR makes a later D-path possible.
3. **Is source mapping a must-have for v1?** My view: yes, if runtime still executes YAML.
4. **Which conductor mechanics should become compiler invariants on day one?** Minimum set: explicit fallback routes, LLM `output:` schemas, `for_each` aggregation helpers, reserved-name avoidance, and router-to-subworkflow expansion.
5. **Does the team actually want a future D-style shared plan contract, or is conductor intentionally “YAML runtime only”?** That is a product/coordination question, not a compiler question.
6. **Can the compiler target upcoming conductor `on_error` improvements behind a stable C# abstraction?** If yes, that further strengthens A.
7. **Is there any run-time graph specialization that truly requires B?** Based on the current workflows, most variability already fits ordinary workflow inputs.
8. **If C is ever reconsidered, what conductor capability is considered non-negotiable to retain?** Dashboard, gates, event log, and cancellation should be named explicitly before any inversion discussion continues.
