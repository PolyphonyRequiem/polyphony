---
doc_type: discussion
status: exploratory
synopsis: Type-correctness review of compiler-lite — qualified endorsement; C# gives build-time authority, not end-to-end correctness proof.
---

# R1 — Type Correctness / Provable Modeling

**Verdict:** Qualify, not reject: I endorse a **compiler-lite / build-time C# authority** direction, but I reject any implication that C# will give Polyphony end-to-end proof of workflow correctness, agent honesty, or resource ownership closure.

## Per-report critique

## 1) `01-prior-art.md`

This report is directionally strong, but from a type-correctness lens it slightly over-compresses very different notions of “typed.” It correctly identifies the winning pattern as **typed host-language authoring + separate execution artifact/runtime** (`01-prior-art.md:5-9`, `174-201`). That is the right lesson for Polyphony. The wrong lesson would be “other systems used code, therefore Polyphony can prove the world.” Most of the cited systems prove much less than the report’s rhetoric can suggest.

The best part is the distinction between **artifact synthesis** systems (CDK, cdk8s, Flyte) and **durable-code runtime** systems (Temporal, DBOS, Durable Functions) (`01-prior-art.md:7-8`, `103-127`, `169-172`). That matters because Temporal-style strength comes from owning replay, determinism, and history. Polyphony is not proposing that. So Temporal is evidence that “orchestrator as code” is possible, but not evidence that a C# DSL targeting conductor will inherit Temporal-level static confidence.

Likewise, Pulumi/CDK/Dagger show the importance of making runtime-produced values explicit (`01-prior-art.md:21-25`, `47-52`, `176-181`, `198-201`). But those systems still rely on **disciplined modeling**, not dependent-type-style proof. `Output<T>`/token values are an honesty mechanism: “this value is not available yet.” Polyphony needs an equivalent. Without it, a C# DSL will simply reintroduce today’s YAML/Jinja confusion in a nicer syntax.

My main criticism: the report should say more explicitly that the prior art supports **build-time synthesis and typed dataflow**, not **JIT generation** and not **whole-system proof**. Flyte/CDK-style compile/register phases are the real analog; runtime emission is not where the type win comes from.

## 2) `02-type-system-mechanics.md`

This is the most important report, and also the place where wishful thinking is closest to the line. Its best claim is that the highest-value wins come from replacing stringly route expressions with typed selectors and from generating verb/workflow descriptors from existing C# contract authority (`02-type-system-mechanics.md:5-10`, `518-546`, `550-617`). That is defensible.

The report is also notably honest in one crucial place: it explicitly lists what cannot be won at compile time — prompt truthfulness, raw PowerShell behavior, runtime set membership, remote ADO legality, human decisions, and some resource identity precision (`02-type-system-mechanics.md:606-617`). I agree with that section more than with some of the earlier optimism.

Where I push back is on the gap between **“typed overlay”** and **“provable modeling.”** The report’s code samples mostly rely on **generated enum overlays over existing string discriminants** (`02-type-system-mechanics.md:45-57`, `538-546`). That is the honest first step. But enum overlays are not the same thing as real discriminated unions. They prove that `Action` is one of `{ImplementItem, AllItemsDone, Error}` only if:

1. the DTO is normalized into a closed vocabulary,
2. the parser rejects unknown strings, and
3. route authoring is forced through the overlay rather than raw string escape hatches.

C# 12/.NET 9 does **not** give native sum types or exhaustiveness over open record hierarchies. The clean workaround is: **sealed hierarchies or generated enums + switch expressions + a Roslyn analyzer that forbids non-exhaustive route handling**. Without the analyzer, the language itself is not enough. The report is mostly honest about this, but the occasional jump from enum overlays to “generated unions” (`02-type-system-mechanics.md:540-546`, `965-970`) is a real complexity step, not a free refinement.

I also think the report is too optimistic on the effect model. D13-style `[MutatesResource]` + runtime `JournalResourceEffect[]` is useful (`02-type-system-mechanics.md:763-823`), but it can only prove **kind subset** and sometimes **pattern compatibility**. It cannot, in general, prove “this workflow can only end up owning these concrete resources,” because:

- many resource ids are derived from runtime values,
- scripts remain opaque unless reified as typed primitives,
- agent-direct mutations are outside compiler control, and
- cross-step symbolic identity reasoning is only partially modeled.

So: great hook, not ownership proof.

Source-generator realism also needs qualification. There is real precedent here: Polyphony already uses source-generated JSON contracts (`src\Polyphony\PolyphonyJsonContext.cs:13-80`), an incremental Roslyn generator (`src\Polyphony.SchemaGenerator\Polyphony.SchemaGenerator.csproj:4-18`), and artifact sanity tests (`tests\Polyphony.Tests\Annotations\VerbCatalogSanityTests.cs:8-15`, `80-90`). That makes a compiler plausible. But the existing generator surface is **catalog generation**, not **full workflow lowering + prompt snapshots + source maps + route analysis + effect metadata**. Also, the main CLI currently has AOT/trim publish disabled because of YamlDotNet reflection requirements (`src\Polyphony\Polyphony.csproj:2-14`). So “AOT-friendly posture” is true; “AOT-proven workflow compiler” is not.

Finally, on expression failure: the report’s `ForEach<TItem,TChildOut>` API is plausible, but the current workflows show hard cases it hand-waves. `apex-driver.yaml` fans out waves at runtime (`apex-driver.yaml:1599-1621`), `apex-wave-dispatch.yaml` then fans out items and reparses `dispatch_items.outputs | tojson` back through PowerShell to aggregate results (`apex-wave-dispatch.yaml:268-360`, `512-534`), and `apex-item-dispatch.yaml` branches into explicit sub-workflows with synthetic MG inputs (`apex-item-dispatch.yaml:252-343`). A DSL can model the graph shape, but it cannot statically prove cardinality, keying, singleton-array behavior, or aggregator correctness unless those are lifted into first-class compiler primitives. Today they are not.

## 3) `03-conductor-relationship.md`

From my lens this report lands in the right place: **Option A now, Option D only as a later protocol bet** (`03-conductor-relationship.md:5-9`, `27-83`, `204-257`, `325-345`). That is the only option that keeps the typed authority where it helps while preserving a concrete, reviewable execution artifact.

Type-correctness likes Option A for one simple reason: it keeps the compiler’s claims falsifiable. If the DSL emits YAML deterministically, that YAML can still be linted, diffed, and path-covered. Polyphony already has live YAML-focused validation: version-drift checks (`.github\workflows\ci.yml:163-179`), Jinja resolver lint against the generated verb schema catalog (`.github\workflows\ci.yml:306-323`), and the workflow harness that runs the **real conductor engine against real YAML** (`tests\harness\README.md:3-8`, `41-50`; `docs\decisions\harness-mvp.md:33-47`). Option A aligns with that ecosystem; Option B weakens it.

I agree strongly that Option B is mostly “A, but less inspectable” (`03-conductor-relationship.md:86-140`). From a provability standpoint, JIT rendering is worse, not better. If the emitted artifact is ephemeral, the debugging story collapses into “trust the compiler.” That is the opposite of what this project should do.

My main critique is of the option comparison table’s “agent contract safety” ratings (`03-conductor-relationship.md:306-319`). Option C/D are rated very high; that is too generous unless the runtime also owns agent execution semantics, tool mediation, and postcondition verification. A richer plan protocol or a polyphony-native runtime can improve **shape safety**, but not agent truthfulness. The report understands this elsewhere; the table should be more careful.

## 4) `04-agent-contracts.md`

This is the strongest report from an honesty standpoint. It correctly identifies the real pain: the same logical contract is repeated across prompt prose, YAML `output:` schema, Jinja route conditions, CLI string bindings, reparsers, and persisted sidecars (`04-agent-contracts.md:5-9`, `11-100`, `135-194`). The planner/child sidecar example is especially persuasive: a named `PlannerOutput` / `PlannedChild` record would eliminate a lot of today’s stringly reparsing (`04-agent-contracts.md:67-100`, `474-495`).

I agree with the core recommendation: treat the **agent contract boundary** as the highest-value compiler target. That is where field-existence, binding compatibility, null/omit metadata, and route-domain normalization can actually be enforced.

Two qualifiers matter.

First, “prompt/route alignment” is only partially statically enforceable (`04-agent-contracts.md:160-168`, `196-233`). The compiler can prove alignment only for **compiler-owned fragments**: output field list, allowed discriminants, tool restrictions, marker conventions. It cannot prove that the human-authored prose actually teaches the model the right rubric. The report mostly says this; I would make it sharper.

Second, `AgentContract<TOutput>` as sketched still has a weak point: `ConsumerBinding<TOutput>` uses `Expression<Func<TOutput, object?>>` (`04-agent-contracts.md:427-432`). That preserves member identity, but it erases destination typing too early. If this surface ships, the binding API should stay generic through the consumer edge; otherwise a lot of “same field” proof degenerates into runtime codec checks.

This report is also the right place to be explicit about the AI-boundary limit: the compiler can prove **shape, vocabulary, and wiring**; the runtime/harness/humans must prove **truth and effects** (`04-agent-contracts.md:234-269`, `270-301`, `496-510`). That division is the honest one.

## 5) `05-roadmap-cost.md`

From a type-correctness lens, I agree with the decision: **ship compiler-lite first** (`05-roadmap-cost.md:5-9`, `23-50`, `366-382`). The reason is simple: most of the things that can genuinely be proven are **local contract invariants**, not full workflow semantics.

The report is right that semantic-parity migration is the long pole (`05-roadmap-cost.md:175-210`). It is also right that the largest workflows are exactly where the modeling gaps show up: `implement-merge-group.yaml` and friends are full of route-domain strings, branch-exclusive outputs, gates, counters, and postcondition verifiers (`05-roadmap-cost.md:187-200`). If the team jumps straight to “full compiler,” it will spend months building a language before it proves the three mechanisms that actually matter.

My main addition is a testing requirement: any generated workflow pilot must pass **three** gates, not one:

1. C# unit tests on the builder/analyzers,
2. golden diff tests on emitted YAML/artifacts, and
3. harness execution against the emitted YAML.

Without (3), the pilot proves only that the compiler likes itself.

I also think the report’s ROI estimate for compiler-lite (“60-80% of value at 10-20% of cost” — `05-roadmap-cost.md:376-378`) is plausible but speculative. The more honest framing is: compiler-lite captures most of the **provable** wins. Whether it captures most of the ergonomic or architectural value is a separate question.

## Cross-cutting type-system findings

### What the type system can actually prove

| Invariant | Best honest enforcement layer | Notes |
|---|---|---|
| Producer field exists and type-matches consumer | **Compile time** | Requires generated descriptors + expression-based binders over named DTOs. |
| Child workflow required inputs are all supplied | **Compile time** | Straightforward with `WorkflowDescriptor<TIn,TOut>`. |
| Route reads a real field on the prior step | **Compile time** | Only if route predicates are lambdas, not raw strings. |
| Route exhaustiveness over a closed domain | **Compile time with analyzer** | Needs enums or sealed unions plus Roslyn exhaustiveness checks; C# alone is insufficient. |
| Null/omission discipline | **Compile time + runtime** | Metadata can be generated statically; actual omission/required-field behavior still needs runtime validation because conductor treats declared fields as required. |
| Prompt/schema/route/tool alignment | **Build time, partially** | Only the compiler-owned prompt fragment is provable. Human prose semantics are not. |
| Counter consistency | **Compile time only if counter is first-class** | Not provable while counters remain handwritten scripts. |
| Resource-budget subset by kind/pattern | **Compile time, partially** | Kind subset and some pattern checks are feasible; concrete resource identity/ownership closure is not. |
| `for_each` cardinality/progress/aggregation correctness | **Runtime + harness** | Shape can be modeled statically; actual collection contents and engine serialization quirks are runtime concerns. |
| Agent truthfulness / tool obedience / remote legality | **Runtime / harness / human** | Not a type-system problem. |

### Discriminated unions / exhaustiveness

C# 12/.NET 9 is good enough for **closed-vocabulary routing**, not for native sum-type elegance. The minimum honest stack is:

- generated enum overlays for existing string discriminants,
- sealed record hierarchies only where payload-bearing variants justify them,
- switch expressions in author code,
- Roslyn analyzer to require exhaustiveness and ban raw-string fallback except explicit escape hatches.

That is enough for `approved | changes_requested`, `platform = github | ado`, and many `action`-style router outputs. It is not enough to pretend Polyphony now has Haskell/Rust-style pattern matching.

### Source-generator constraints

A build-time compiler is realistic because the repo already ships an incremental generator and source-generated JSON contract pattern (`src\Polyphony.SchemaGenerator\Polyphony.SchemaGenerator.csproj:4-18`; `src\Polyphony\PolyphonyJsonContext.cs:13-80`). A runtime/JIT compiler is much less honest:

- the main CLI is not currently AOT-published (`src\Polyphony\Polyphony.csproj:2-14`),
- runtime regeneration would need stable lowering code inside the CLI,
- emitted artifacts must be persisted for debugging,
- and cross-target determinism becomes a product requirement.

So the “workflow compiler” should mean **build-time emission by default**. Anything else should be opt-in and rare.

### Failure of expression in today’s workflows

Two real workflow shapes stress the design:

1. **Nested runtime fan-out with manual aggregation.** `apex-driver.yaml` → `apex-wave-dispatch.yaml` → `apex-item-dispatch.yaml` uses nested `for_each`, explicit sub-workflows, and PowerShell reparsing of `.outputs` / `.errors` because conductor’s grouping shape is awkward (`apex-driver.yaml:1599-1621`; `apex-wave-dispatch.yaml:268-360`, `512-534`). A DSL can express the graph, but the hard part is the aggregation semantics.
2. **Branch-exclusive joins plus review-loop safety.** `implement-merge-group.yaml` routes on string verdicts and string actions, then uses catch-alls deliberately to avoid infinite loops or silent bypass (`implement-merge-group.yaml:787-796`, `1796-1807`, `2237-2385`). A DSL can improve route typing, but the safety property depends on control-flow analysis plus runtime postconditions, not on C# syntax.

### Testability

Yes, DSL-authored workflows can be unit-tested without conductor. But those tests do **not** replace the current harness. The harness’s whole point is that it runs the **real conductor engine against real YAML** with only the LLM and CLI boundaries faked (`tests\harness\README.md:3-8`, `41-50`, `181-203`). The compiler aligns with the harness only if it emits real YAML and keeps the harness in the loop.

## 15-claim rating table

| Report | Claim | Rating | Why |
|---|---|---:|---|
| 01 | Industry convergence favors typed host-language authoring with separate runtime artifacts. | ✅ | Strong prior-art support; best fit for Polyphony’s proposal. |
| 01 | The most successful systems make dataflow typed and explicit. | ⚠️ | True directionally, but often via disciplined modeling/runtime contracts, not compile-time proof. |
| 01 | Polyphony should steal CDK/Flyte/Dagger patterns: compile phase, L1/L2/L3, typed runtime values. | ✅ | All three are high-leverage and type-relevant. |
| 02 | Lambda-based route predicates over typed outputs are a major compile-time win. | ✅ | This is the clearest genuinely provable improvement. |
| 02 | Build-time YAML emission + analyzers is the right primary path; runtime JIT should not be default. | ✅ | Best fit for current repo, harness, and artifact sanity model. |
| 02 | `[MutatesResource]` + `JournalResourceEffect[]` gives the compiler a strong ownership model. | ⚠️ | Strong for capability subsets; too weak for ownership closure or concrete identity proof. |
| 03 | Option A (build-time YAML, conductor runtime) is the right first move. | ✅ | Best balance of proof, inspectability, and compatibility. |
| 03 | Option B is mostly Option A but less inspectable. | ✅ | True, especially from a debugging/provability perspective. |
| 03 | Option D gives high-to-very-high agent contract safety. | ⚠️ | Only for shape safety; agent truthfulness and effects remain runtime issues. |
| 04 | The highest-value compiler target is the agent contract boundary. | ✅ | Agreed; that is where the stringly drift currently hurts most. |
| 04 | The compiler can enforce prompt/route/tool alignment. | ⚠️ | Only for generated contract fragments, not for human-authored prose semantics. |
| 04 | Prompt prose should remain human-authored while compiler-owned fragments are generated. | ✅ | This is the honest middle ground. |
| 05 | Compiler-lite is the right MVP. | ✅ | Captures most of the genuinely provable wins first. |
| 05 | The long pole is semantic parity and migration discipline, not YAML emission. | ✅ | Completely right from a modeling standpoint. |
| 05 | Compiler-lite likely captures 60-80% of value for 10-20% of cost. | ⚠️ | Plausible, but still an estimate rather than a typed-modeling fact. |

## Minimal modeling primitives (the 3)

1. **Typed descriptors + expression-based refs/binders**
   - `VerbDescriptor<TArgs,TResult>`, `WorkflowDescriptor<TIn,TOut>`, `Ref<TStep,TValue>`.
   - This is the foundation for field existence, producer/consumer compatibility, and required input mapping.

2. **Closed route domains + exhaustiveness analyzer**
   - Generated enum overlays first; sealed unions only where variants carry distinct payloads.
   - Required to make route tables honest, especially for `action`, `verdict`, `platform`, and review-state vocabularies.

3. **Explicit boundary metadata**
   - Value codecs, null/omit policy, optional/branch-exclusive refs, and resource capability descriptors.
   - This is what stops “same field” claims from devolving into ad hoc string serialization.

If the team ships only those three mechanisms, it gets the majority of the real type win.

## Anticipated cross-reviewer tensions + preemptive responses

### Expected pushback from the agentic-AI reviewer

**Pushback:** “LLMs do not respect schemas consistently, so this much type rigor at the agent boundary is over-engineering.”

**Response:** Correct premise, wrong conclusion. Because LLMs are unreliable, the compiler should focus on the parts that *are* controllable: shared DTOs, route vocabularies, tool restrictions, poster bindings, null/omit rules, and harness fixtures. The type system does not prove agent truth; it removes accidental drift so runtime verification can focus on semantic failure rather than wiring failure.

### Expected pushback from the long-term architecture reviewer

**Pushback:** “Roslyn analyzers + generators + DSLs become a sustaining-engineering burden.”

**Response:** Agreed if the project jumps straight to a full compiler. That is why the type-correctness recommendation is intentionally narrow: extend the existing generator/catalog pattern, keep emitted YAML as the runtime artifact, and refuse JIT/runtime cleverness until the three core primitives prove their worth. The sustaining-cost risk is real; the answer is scope discipline, not giving up on type authority.

## Open questions for synthesis

1. Is the first-class route-domain normalization plan **enum overlays** only, or are there real payload-bearing routers that justify sealed unions in v1?
2. Which workflows must the compiler model on day one to prove `OptionalRef` / branch-exclusive joins / `for_each` aggregation, rather than only leaf workflows?
3. Is **persisted emitted YAML per run** mandatory for any JIT/specialized path, or is JIT off the table entirely for v1?
4. What exact subset of D13 effect modeling is promised statically: kind subset only, pattern compatibility, or ownership discipline? The synthesis should avoid collapsing those into one claim.
5. Does the team agree that the harness remains authoritative for runtime semantics, with compiler tests added alongside it rather than instead of it?
6. What is the explicit “no raw string route conditions” escape hatch policy? Without that, many of the claimed compile-time wins evaporate.