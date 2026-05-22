# Verdict

I **qualify** the direction: Polyphony should pursue **compiler-lite and perhaps build-time compilation to conductor artifacts**, but a full C#-first workflow-authoring regime is defensible long-term only if it stays operationally transparent, narrowly scoped, and reversible.

## Per-report critique

### 1) `01-prior-art.md`

This report is useful because it correctly distinguishes three very different families that often get conflated: (1) code that synthesizes an artifact for another runtime (CDK, cdk8s, Flyte registration), (2) code that is itself the durable runtime definition (Temporal, Durable Functions, DBOS), and (3) thin ergonomic wrappers over someone else’s schema (the CDKTF warning). From a 3-5 year architecture lens, that distinction matters more than the generic slogan “code-first is better.”

Where I agree strongly:

- The **CDKTF warning** is central, not peripheral. If Polyphony builds “C# names for conductor YAML” without new guarantees, new review leverage, or new operational metadata, it inherits the catch-up tax while still living in conductor’s conceptual model.
- The **Airflow TaskFlow caution** is also important. Better authoring syntax does not rescue a weak cross-step contract model. If the runtime truth is still strings, the system remains stringly even when the source looks typed.
- The **Pulumi/CDK/Flyte** examples support a critical sustaining-engineering point: code-first succeeds when it is attached to a real product boundary and a real platform team, not merely a nicer editor experience.

Where I think the report is too optimistic for Polyphony specifically:

- The analogs are mostly products with either large ecosystems or a strong platform/product split. Polyphony today is not “AWS CDK scale with a platform team”; it is one human plus AI assistants moving extremely quickly. That changes the maintenance math. The same architecture that is reasonable for a platform with a dedicated compiler/tooling team can become a year-2 burden in a repo whose primary job is shipping workflows.
- The report treats **L1/L2/L3 layering** as obviously transferable. It is transferable in principle, but it is also exactly how internal DSLs become frameworks only their author can extend. For Polyphony, L2 and L3 are valuable only if 90% of new workflows can be authored without touching the layer definitions themselves.
- It underplays the social advantage of YAML as a **legible operational artifact**. Today, an operator or new contributor can grep the registry and reason about the workflow graph without understanding the implementation language. That matters more than elegance once the workflow count grows.

Long-term takeaway: prior art supports **code-generated artifacts**; it does **not** yet prove that Polyphony should move its primary authoring surface out of a human-parseable declarative format. The report is strongest as an argument for a compiler backend and weakest as an argument for a wholesale authorship inversion.

### 2) `02-type-system-mechanics.md`

This is the most ambitious report and also the one that most needs architectural qualification.

Its core insight is right: the highest-value static wins are at the **cross-boundary seams** — route fields, child-workflow inputs, command descriptors, counters, and resource declarations. Those are precisely the classes of defects that age badly in a growing workflow library. If Polyphony wants to spend complexity budget, that is where it should spend it.

But the sustaining-engineering risk is that this proposal is not “a typed DSL”; it is really **four systems**:

1. authored contracts,
2. a fluent builder surface,
3. analyzers/source generators,
4. emitted runtime artifacts plus source maps.

That stack can be worth it, but it is easy to underestimate what it costs to keep healthy through .NET upgrades, IDE quirks, design-time builds, CI, and contributor onboarding. Source generators are not free infrastructure. The report is directionally aware of this, but not sober enough about the operational tax.

Specific concerns:

- **Source-generator realism.** The report recommends source generators + analyzers as if this were merely an implementation detail. In practice, generator-heavy stacks are where small teams end up doing accidental compiler engineering. The .NET ecosystem has repeatedly shown this tax: Native AOT and trimming have exposed brittle edges in generator-heavy libraries, including .NET 8 era regressions such as runtime issue #91065, plus recurring `System.Text.Json` source-generation friction around closed generics and up-front type enumeration. None of that means “don’t do it.” It means the maintenance burden is real, recurring, and disproportionately painful for a tiny team.
- **Tooling story is still aspirational.** IntelliSense for a fluent builder is plausible. Debugging emitted YAML with source maps is plausible. Edit-time diagnostics are plausible. But the report presents this as an almost-ready developer experience. It is not. Until the generator, map files, explain/render commands, and harness integration actually exist, the DX story is a promise, not an asset.
- **“Non-C# authors are a non-goal” is more dangerous than the report admits.** Over 3-5 years the relevant audience is not just current maintainers; it is future humans, future AI assistants, incident responders, and adjacent contributors. Even if workflows become code-authored, Polyphony still needs a readable artifact surface for people who are not compiler authors.
- **Counter/resource modeling is high leverage, but also where framework accretion starts.** These features are attractive because they promise “real semantics.” They are also exactly how a pragmatic compiler turns into a bespoke orchestration language.

My long-term position is narrower than this report’s: keep the static benefits, but do not let the existence of those benefits justify a large authoring framework unless the team proves it can sustain the toolchain itself.

### 3) `03-conductor-relationship.md`

This is the strongest report from a long-term architecture standpoint. Its recommendation — **Option A now, Option D only if later pain proves the YAML boundary itself is the problem** — is the most sustainable of the five reports.

Why I agree:

- It preserves the most valuable operational asset Polyphony already has: conductor’s **runtime, dashboard, human-gate UI, stop/cancel path, and event model**.
- It understands that Polyphony’s journal/reset/drift work does **not** by itself justify a runtime inversion.
- It identifies the real architecture question correctly: not “who owns the source text,” but “who owns execution semantics and the operator surface?”

My main qualification is that Option A is stable long-term only if the boundary is treated as a **real product boundary**, not as an implementation convenience. “Polyphony emits to conductor” can be healthy, but only if:

- the emitted artifact remains first-class and inspectable,
- node identities remain stable,
- source mapping is reliable,
- the two repos maintain compatibility tests,
- and Polyphony does not start depending on undocumented conductor quirks faster than conductor can rationalize them.

Without that discipline, Option A degrades into a quiet coupling where conductor is nominally independent but practically shaped by Polyphony’s compiler.

I also want to push back a bit on “Option D as the only credible later upgrade path.” Another credible later path is simply **stopping at A or compiler-lite**. Not every successful compiler project must continue toward a richer shared execution plan. The report leaves that door open implicitly, but I would make it explicit. A sustainable architecture sometimes includes the option to stop.

### 4) `04-agent-contracts.md`

This report is very strong on the part of the problem that most deserves stronger typing: the **agent boundary**. The best argument for a compiler in Polyphony is not “YAML is ugly”; it is “we are redundantly declaring the same machine contract in prompts, route tables, CLI flags, parsers, and persisted sidecars.”

I agree with three core positions here:

- the agent boundary is the highest-value place to unify contracts;
- prompts should remain partly human-authored, not fully synthesized;
- runtime truth still matters more than static declarations for side effects and agent honesty.

My long-term caution is about framework growth. This report is full of powerful abstractions — `AgentContract<TOutput>`, capability profiles, consumer bindings, generated prompt fragments, semantic invariants — and each one is individually reasonable. Collectively, they risk producing a framework whose extension story is harder than the current problem.

The danger signs are familiar:

- every new workflow shape adds a new contract abstraction,
- prompt conventions harden into mini-DSLs,
- bindings become more elaborate than the underlying workflow,
- and reviewers must understand the framework before they can review the workflow.

That is exactly how “one declaration everywhere” turns from clarity into indirection.

So my critique is not “this is wrong.” It is “this must be constrained aggressively.” The compiler should own only the truly load-bearing fragments: route vocabulary, field names/types, nullability/omission, required codecs, and deterministic postconditions. The moment it starts owning too much prompt structure or too many agent-life-cycle abstractions, it will become a platform project in disguise.

From an on-call perspective, the report is right to insist on **declared capabilities + verifiable postconditions**. That helps year-2 sustainability because incidents are debugged from observable facts, not from what the contract said should happen.

### 5) `05-roadmap-cost.md`

This is the report closest to my verdict.

Its biggest strength is honesty about where the real cost sits: not in YAML emission, but in **semantic parity, migration discipline, and the six giant workflows that dominate the corpus**. That is exactly the long-term architecture lens. The hardest part of a workflow compiler is not creating a syntax tree; it is avoiding a multi-year split-brain state.

I agree strongly with:

- **compiler-lite first**;
- AB#3255 as the real prerequisite/substrate;
- AB#3256 and AB#3258 reducing the authoring surface before a DSL is frozen;
- explicit off-ramps and kill criteria.

My one major warning is that “compiler-lite” is a real off-ramp only if governance makes it one. In practice, teams often say “we’ll just add a thin typed layer” and then, because the metadata exists, the pressure to add code-first authoring becomes irresistible. That is the CDK/Pulumi success trajectory when it works, and the CDKTF problem when it becomes wrapper tax. So the report’s off-ramp is credible in principle, but only if the team explicitly blesses the possibility that **compiler-lite may be the finish line**.

The report is also right to call out Daniel’s shipping cadence. That pace is an asset only if the compiler slices continue to feel like **product work with immediate payoff**, not like a second product. The moment workflow delivery starts depending on compiler plumbing in most PRs, the pace claim has expired.

## Cross-cutting long-term findings

1. **The long-term winner is not “C# everywhere.” It is “one contract surface, one operational artifact, and a narrow compiler.”**
2. **Compiler-lite is the only obviously sustainable first move.** It harvests most of the static value without forcing the project into a years-long authorship migration.
3. **A full DSL is only defensible if it stays smaller than the workflow library it serves.** If most new needs require DSL/compiler changes, the compiler has become the product.
4. **Generated artifacts must remain first-class.** Operators, reviewers, and future AI assistants need a human-readable compiled form plus source mapping. If the executed plan is hidden, the on-call story gets worse immediately.
5. **The conductor relationship can be healthy, but only with boundary governance.** “Polyphony emits to conductor” is stable if the boundary is versioned, observable, and compatibility-tested. It is unstable if Polyphony silently programs to conductor quirks.
6. **Migration debt is the biggest year-2 risk.** Half-finished migrations almost never stay temporary. Angular 1→2, Python 2→3, and Redux’s eventual consolidation around RTK all show the same lesson: transition periods persist unless one side becomes clearly better and tooling makes the migration boring.
7. **There is a credible non-compiler end state.** Stronger schema publication, YAML linting, route validation, command-signature validation, and workflow-family conventions could plausibly solve the majority of current pain while preserving the current operator experience.
8. **AI-first authoring does not remove the need for human legibility.** If anything, it increases the need for narrow abstractions and inspectable artifacts. Future agents will also benefit from simpler surfaces and better source maps.

## The 15-claim rating table

| Report | Claim | Rating | Why |
|---|---|---:|---|
| 01 | Industry has converged on host-language authoring with separate runtime artifacts | ✅ | Strongly supported by CDK/cdk8s/Flyte-style systems. |
| 01 | Polyphony’s closest analog is AWS CDK + Flyte + Dagger | ⚠️ | Useful analogy, but team size and operational maturity are much smaller than those ecosystems. |
| 01 | Polyphony should steal L1/L2/L3 layering | ⚠️ | Valuable only if it does not become an internal framework moat. |
| 02 | The cleanest authoring model is a C# fluent builder over typed records | ⚠️ | Plausible, not yet proven sustainable versus a narrower declarative layer. |
| 02 | Build-time YAML emission + analyzers is the right primary path | ✅ | Best long-term trade among the options presented. |
| 02 | Making non-C# authors a non-goal is acceptable | ❌ | Overstated; operational readers, future contributors, and many AI assistants still need a simpler legible surface. |
| 03 | Option A now is the best first move | ✅ | Best balance of leverage, safety, and operational continuity. |
| 03 | Conductor changes “almost nothing” under Option A | ⚠️ | Runtime code may not change much, but governance, compatibility, and review boundaries definitely do. |
| 03 | Option D is the only credible later upgrade path | ⚠️ | Another credible path is to stop at A/compiler-lite if value plateaus. |
| 04 | The highest-value compiler target is the agent contract boundary | ✅ | This is where the current duplication and stringly drift are most acute. |
| 04 | Prompts should remain human-authored with compiler-owned fragments | ✅ | Strong long-term balance between rigor and readability. |
| 04 | A first-class `AgentContract<T>` style surface is the right abstraction | ⚠️ | Potentially right, but easy to overbuild into a framework only specialists can extend. |
| 05 | Compiler-lite should ship before any full workflow compiler | ✅ | Best payoff/risk ratio by a wide margin. |
| 05 | Compiler-lite captures 60-80% of the value for 10-20% of the cost | ⚠️ | Directionally believable but numerically more confident than the evidence supports. |
| 05 | A 6-month success state is contract/lint wins plus a few generated pilots, not full migration | ✅ | This is the only sustainable success definition on the table. |

## Sustaining-engineering invariants (the 3)

1. **Operational truth must remain inspectable without compiler expertise.** Every run must retain a human-readable compiled artifact, stable node names, and source mapping from runtime failures back to authored source.
2. **Most workflow changes must not require compiler changes.** If adding or modifying ordinary workflows routinely requires touching generators/analyzers/framework layers, the architecture is already too clever.
3. **Migration must stay reversible and single-source-per-workflow.** No long-lived dual-authoring state; every migrated workflow has one authority, parity tests, and an explicit kill-switch if the generated path proves worse.

## Anticipated cross-reviewer tensions + preemptive responses

### With the type-correctness reviewer

**Likely pushback:** “You are overweighting ergonomics. If the type system can prevent route drift, child-input mismatches, and agent-contract skew, that should dominate the decision.”

**My response:** I agree on where correctness matters most: cross-boundary contracts, route vocabularies, child mappings, command signatures, and null/omission rules. I disagree that the answer is therefore “push authorship fully into C#.” A correctness system that only its author can extend is not more correct over 3-5 years; it merely moves the failure from runtime bugs to change paralysis. The sustainable target is **narrow, high-leverage correctness**, not maximal semantic capture.

### With the agentic-engineering reviewer

**Likely pushback:** “AI will be the main workflow author. Machine-friendly typed code matters more than human-friendly YAML in the long run.”

**My response:** AI authorship strengthens the case for explicit contracts; it does not eliminate the need for a human-readable operational artifact. Future agents also benefit from constrained surfaces, examples, generated docs, and inspectable compiled output. A narrow declarative DSL plus compiled artifact is often **more** agent-friendly than arbitrary C# metaprogramming because it reduces latent complexity and keeps the execution model legible.

## Day-1 fine, year-2 wound callouts

- **“Generated YAML is checked in for now.”** Temporary dual-source arrangements tend to become permanent.
- **“Runtime fallback JIT generation”** as a convenience. This often becomes the hidden execution path nobody can inspect easily.
- **“Non-C# authors are not a goal.”** Fine for day-1 compiler authors; bad for incident response, onboarding, and cross-team collaboration.
- **Enum overlays and contract projections everywhere.** Helpful early, but they can become a constant tax as underlying DTOs evolve.
- **Prompt fragments expanding into prompt frameworks.** Generating the load-bearing contract text is good; owning prompt architecture wholesale is how the compiler becomes a second product.
- **IR designed for a hypothetical future runtime.** Good abstraction is fine; a shadow runtime architecture that nobody needs yet is not.
- **Assuming compiler-lite automatically leads to the full compiler.** That path dependence is precisely what must be resisted.

## Open questions for synthesis

1. What explicit **owner/reviewer model** governs the compiler surface, the emitted artifacts, and the workflow library separately?
2. What is the concrete **stop/go metric** after compiler-lite and after the first generated workflow pilot?
3. Will emitted artifacts be **checked in, archived per run, or both**, and how will on-call engineers navigate from a runtime failure to authored source?
4. Which workflow families, if any, are allowed to remain **hand-authored permanently**?
5. What compatibility contract does Polyphony need with conductor: **shared tests, versioning policy, source-map guarantees, node identity guarantees**?
6. What is the maximum acceptable size of the v1 authoring surface — how many primitives before the DSL is clearly becoming a framework?
7. Is the desired outcome an **internal authoring aid** for first-party workflows, or a surface other people/repos are expected to extend?
