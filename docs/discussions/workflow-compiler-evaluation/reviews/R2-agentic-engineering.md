# R2 — Agentic Engineering Review

**Verdict:** I **qualify** the direction: use C# to declare workflow structure and the smallest reliable agent-contract surface, but do **not** let the compiler pretend prompts, schemas, or capability declarations are more deterministic than production LLM systems really are.

## Per-report critique

### 1) `01-prior-art.md` — strong structural analogies, weak agent-specific prior art

This report is directionally useful. It correctly identifies the strongest reusable pattern as **typed authoring surface, separate runtime artifact**, and its CDK/Flyte/Dagger triangulation is better than the common "let's compare everything to Temporal" reflex. From an agentic-engineering lens, that is the right starting point: compiled artifacts are good, explicit runtime-unknown values are good, artifact-first composition is good.

My main critique is that the survey is still mostly a survey of **infrastructure orchestration and data orchestration**, not of **LLM-centric systems**. That matters. Agent workflows do not mainly fail because a DAG edge is mis-declared; they fail because prompts drift, schema pressure causes fabrication, tool surfaces are too wide, retries make outputs worse, and model upgrades invalidate prompt heuristics. The report gestures at Dagger artifacts and Flyte registration, but it does not really engage with modern structured-output and tool-calling practice. For this question, that is a missing half.

So I agree with its transferable lessons, but I would narrow them:

- **Steal** the compile/register idea.
- **Steal** the notion that runtime-unknown values need a distinct representation.
- **Steal** artifact/effect-first composition.
- **Do not infer** that because typed host-language compilers worked for infra, they will automatically improve agent behavior.

The most agent-relevant gap is that the report does not say enough about **prompt/version/eval artifacts**. If Polyphony gets a compiler, the compile step should not emit only workflow YAML. It should also emit or snapshot at least the prompt contract fragments, model/tool selections, and schema/view of route-critical fields. Otherwise the compiled artifact is only half the truth for agent nodes.

Net: this report supports the compiler idea structurally, but it should not be treated as evidence that typed workflow authoring alone moves the needle on agent quality.

### 2) `02-type-system-mechanics.md` — excellent on graph mechanics, somewhat too optimistic about agent typing

This is the strongest report on the mechanical side. Replacing stringly route logic with lambdas over typed outputs is real value. Generated verb/workflow descriptors are real value. Build-time emission plus analyzers is absolutely the right bias; JIT/reflection-heavy generation would make debugging and prompt iteration materially worse.

Where I want to slow it down is the implied unification around `Agent<TPromptContext, TOutput>`. That shape is neat, but it risks flattening the hard parts of agent engineering into a tidy generic that hides the actual runtime concerns. Real agent steps are not just input/output transformations. They also need:

- execution mode (`structured output` vs `tool calling`),
- curated tool bundle,
- retry/repair policy,
- refusal / insufficient-context mode,
- transcript retention and redaction rules,
- postcondition checks,
- model selection and model-family assumptions.

If the generated C# model does not make those first-class, the workflow compiler will look more complete than it is.

The report is most persuasive where it is **not** trying to type the soul of the agent: typed route discriminants, typed child-workflow descriptors, typed verb bindings, typed counters, typed exports. All of that is good compiler territory.

The report is least persuasive where it implies prompts, scripts, gates, outputs, prompt contexts, and effects are one common compile-time problem. They are not. Prompt **context slots** are typed. Prompt **strategy** is not. Prompt **rubric quality** is definitely not. Generating prompts from C# declarations helps when the compiler owns the non-negotiable fragments: output fields, enum vocabularies, marker rules, tool restrictions, maybe a skeleton example. It harms when it starts generating the mission prose, decomposition heuristics, tone, or few-shot examples that usually determine whether an agent actually performs well.

I would keep the report's core mechanical proposal and add one strong qualifier: the compiler should own the **contract fragments inside prompts**, not the prompts as a whole.

### 3) `03-conductor-relationship.md` — correct near-term architecture call, but agent runtime cost should be stated even more bluntly

From an agentic-engineering lens, this report's headline recommendation is right: **Option A** is the best first move. Keep conductor as runtime, compile deterministically to artifacts it already understands, and avoid taking on a second runtime project under the name of a compiler.

Why I agree:

- Agent systems are hard enough when prompt, tool, and schema behavior are the only moving parts.
- Hiding the executed artifact behind JIT generation makes postmortems and prompt iteration worse.
- The current conductor runtime already carries valuable operational semantics: orchestration, gates, visibility, stop/cancel plumbing, and a live graph model.

I especially agree that **Option B** is mostly "Option A, but less inspectable." For agents, inspectability is not a nice-to-have. When a model upgrade changes behavior, you need to know the exact workflow artifact, prompt snapshot, tool surface, and parser behavior that were live for that run.

On **Option C**, I think the report is right but could be even more direct. For Polyphony to take over the agent runtime in a credible way, it would need far more than DAG execution:

- provider/model abstraction,
- structured-output parsing and repair,
- refusal handling,
- tool-calling adapters,
- transcript and trace storage,
- sandboxing or tool allowlists,
- human-gate persistence/UI,
- cancellation and resume semantics,
- eval/replay harnesses,
- model rollout / regression infrastructure.

That is not a compiler follow-on. That is a platform program.

The most interesting part of this report, from my lens, is **Option D**. A runtime-neutral IR becomes genuinely valuable if it carries agent metadata that YAML does poorly: prompt snapshot identity, execution mode, selected tool bundle, parser/retry policy, refusal variant, and postconditions. If it does not carry those things, it is mostly just prettier YAML.

So: strong agreement with the report's choice, with one upgrade in tone — becoming the agent runtime is unrealistic for the current team/project shape unless Polyphony intentionally decides to become an orchestration platform.

### 4) `04-agent-contracts.md` — closest to the real problem, and mostly right

This is the report most aligned with production agent behavior. The central thesis — **target the agent contract boundary, not the whole agent** — is the right cut.

The report is strongest in four places:

1. **Unifying one named DTO across prompt schema, routing, consumer bindings, and persisted sidecars.** That is the highest-value cleanup available.
2. **Treating route domains as contracts.** A route enum is more important than another markdown prompt helper.
3. **Arguing for partial prompt generation.** Exactly right: keep human-authored prose, generate the load-bearing contract fragments.
4. **Rejecting automatic exposure of all verbs as tools.** Also exactly right. Most agent systems get worse when every low-level action becomes callable.

My main additions are about structured-output realism.

In practice, required-field pressure causes models to invent values. If you tell a model every field is required and then ask it to proceed under uncertainty, it will often satisfy the schema instead of telling the truth. So the compiler/runtime contract should distinguish:

- **route-critical required fields**,
- **advisory optional fields**,
- **explicit refusal / insufficient-context / tool-failed variants**.

That means I do **not** want a single hard-required object with ten fields if only two fields are actually needed to route the next step. I want the minimum required core to be strict, and the rest to be optional or moved behind a second artifact-producing step.

I also think the report should be even more explicit that capability profiles are **descriptive unless enforced**. Prompt instructions do not secure tools. A compiler can declare that a coder agent "may use git in this worktree," but without sandboxing or allowlists that is not enforcement; it is documentation plus a basis for auditing and postcondition checks.

Still, this report has the best instincts overall. If Polyphony does only one agent-facing thing, it should probably be the thing this report argues for: typed output contracts + route vocabularies + binding provenance + generated prompt fragments.

### 5) `05-roadmap-cost.md` — right sequencing, but the pilot strategy needs one real agent in scope

This report is strong on execution realism. It correctly says the migration burden is real, that semantic parity is the long pole, and that **compiler-lite** is the right first move. I agree with all of that.

I also agree with its caution on prompt generation. Prompt text is not just decoration; it is part rubric, part operator UX, part implicit protocol. That is exactly why it should be handled conservatively.

My one meaningful disagreement is with the implied pilot shape. A purely leaf-sized, non-agent workflow is a good pilot for the **emitter**, but not for the **agentic question** the investigation is actually asking. If the first generated workflow is only a router/gate/terminal flow, the team can get a false sense of success. It will have validated YAML generation and graph ergonomics, while leaving untouched the hard questions about schema drift, prompt fragments, downstream consumers, and tool surfaces.

So I would split the pilots:

- one **tiny structural pilot** for emitter ergonomics and diff stability, and
- one **small but real agent contract pilot** with a typed output, downstream consumer, and malformed-output tests.

Without the second pilot, the roadmap under-tests the exact surface where agentic systems usually break.

The other missing item is **eval infrastructure as a first-class milestone**. Before a serious DSL pilot, Polyphony should have prompt snapshotting, malformed-output fixtures, route-regression tests, and a model-upgrade rebaseline loop. Otherwise the compiler will improve authoring while leaving the operational tuning loop underpowered.

## Cross-cutting agent-engineering findings

### 1) Prompt-as-type-system: generation helps at the seam, hurts in the strategy layer

The right cut is not "generate prompts" versus "do not generate prompts." The right cut is:

- **Declare in C#**: output fields, enum/decision vocabulary, tool restrictions, marker protocols, consumer bindings, route-critical invariants.
- **Author in human prose**: mission framing, evaluation rubric, prioritization heuristics, examples, anti-pattern warnings, tone, and model-specific guidance.

Generation helps where drift is expensive and human creativity adds little. It harms where tacit prompt craft is the difference between a capable agent and a sterile schema-filler.

### 2) Structured output reality: schemas are necessary, but aspirational unless paired with runtime policy

LLMs violate schemas, especially under novel inputs, long contexts, and retry pressure. The honest design is:

- strict parse for **route-critical core fields**,
- optional/advisory fields outside the routing core,
- explicit refusal/abstain/error variants,
- bounded repair for mechanically safe cases only,
- telemetry on every repair/retry/coercion,
- deterministic postcondition checks after the agent step.

Hard-fail-everything is too brittle. Silent coercion is dangerous. The sweet spot is **strict core, soft perimeter, explicit failure modes**.

### 3) Function-calling / tools API: typed verbs should be tool-describable, not automatically tool-exposed

Modern APIs increasingly want tool schemas. That argues **for** C# declaration, because one authoritative verb declaration can emit both workflow bindings and tool specs.

It does **not** argue for exposing the full verb catalog to every agent. The right pattern is curated, workflow-specific, high-level tools. In most cases Polyphony's agents should still be either:

- structured-output agents that produce a decision/document, or
- narrowly tooled agents with a small allowlist.

Raw verb explosion is a quality and safety regression.

### 4) Prompt iteration loop: the compiler should enable it, not capture it

Prompts will keep changing by hand. Models will keep changing under them. So the compiler should preserve:

- human-editable prompt bodies,
- generated contract fragments,
- prompt snapshots per run,
- source maps from run -> prompt version -> compiled workflow node,
- eval fixtures and regression cases outside the DSL.

The eval surface should live in the harness/replay layer, not in the type system alone.

### 5) Polyphony as agent runtime: not now

Realistically, Polyphony should not try to replace conductor for agent dispatch + LLM loop in this phase. Easier wins are available: typed contracts, curated tools, generated prompt fragments, source maps, harness fixtures, and better postcondition checks.

### 6) Trust boundary: prompt constraints are weakest; sandbox + audit + postconditions are what work

In practice, the order of effectiveness is:

1. **sandbox / allowlist / scoped environment**,
2. **postcondition checks + drift/audit**, 
3. **prompt restrictions**.

Prompt restrictions alone are advisory. If D12 allows direct shelling out, the system should be honest about that and avoid calling descriptive capability metadata "enforcement."

### 7) Artifact contract: type the companion metadata, not the whole prose artifact

The compiler helps for marker protocols, route digests, IDs, structured summaries, and downstream-consumed fields. It hinders when it tries to over-model freeform artifacts such as plan markdowns, PR bodies, or review comments.

The winning shape is usually:

- freeform `string`/markdown body,
- plus typed companion fields that downstream logic actually needs.

### 8) Multi-agent composition: `Agent<TInput, TOutput>` is too small by itself

Useful abstraction units are closer to **capability bundles + contract + execution mode + retry policy + consumer bindings**. A plain generic agent type is not wrong, but it is not the whole model.

### 9) Eval / observability: typed traces are useful; deterministic replay of real agents is not

Agent eval should capture:

- prompt snapshot,
- model/provider,
- tool bundle,
- raw response,
- parsed output,
- repair attempts,
- route taken,
- postconditions checked.

Traces can be typed after parse. Full deterministic replay is only possible with stubbed/frozen responses. The journal effect model helps with side effects, but it is not a substitute for agent transcript/eval traces.

### 10) What actually makes agentic systems work well

My ranking:

1. **Strong evals with real examples and regressions**
2. **Good tool surface and environmental constraints**
3. **Prompt quality and examples**
4. **Choosing the right model**
5. **Strong I/O contracts and postcondition checks**
6. **Retry/repair discipline**
7. **A typed workflow DSL**

The compiler strongly helps #5, somewhat helps #2 and #6, weakly helps #3 if it only owns contract fragments, and barely helps #1 or #4 unless the surrounding harness/observability story is built too.

### 11) Where architecture should defer to AI-engineering practice

Architecture should standardize the seams: contracts, bindings, tool declarations, traces, eval hooks. It should **not** over-standardize the parts that still benefit from experimentation: prompt wording, few-shot examples, model choice, retry heuristics, and when to split one agent into two.

## 15-claim rating table

| Report | Claim | Rating | Agentic note |
|---|---|---|---|
| 01 | Host-language authoring + separate runtime artifact is the strongest industry pattern | ✅ | Good fit for inspectability and iteration. |
| 01 | The most successful systems make dataflow typed and explicit | ⚠️ | Helpful, but agent success also depends heavily on prompts, tools, and evals. |
| 01 | Polyphony's closest analog is CDK structurally, Flyte operationally, Dagger ergonomically | ⚠️ | Useful, but incomplete without modern agent/tool-calling prior art. |
| 02 | The cleanest unification is C#-first declarations via a fluent builder over typed records | ⚠️ | Strong for graph structure; too strong if read as a full answer for agent authoring. |
| 02 | The biggest compile-time win is replacing stringly route logic with lambdas over typed outputs | ✅ | This is one of the most real wins in the entire investigation. |
| 02 | Build-time YAML emission + analyzers should be primary; JIT reflection should not | ✅ | Better for debugging, reviewability, and prompt iteration. |
| 03 | Option A (compile to YAML, keep conductor runtime) is the best first move | ✅ | Agreed; lowest-regret path for agent systems too. |
| 03 | Option B is mostly Option A but less inspectable | ✅ | Especially true once prompts and parser behavior matter in postmortems. |
| 03 | Option C is unjustified unless Polyphony wants to become an orchestration platform | ✅ | Correct from the agent-runtime cost perspective. |
| 04 | The highest-value compiler target is the agent contract boundary, not the whole agent | ✅ | Best statement in the set. |
| 04 | Prompts should not be fully synthesized, but compiler-owned contract fragments should be generated | ✅ | Exactly the right cut. |
| 04 | Verb declarations should be tool-describable but only a curated subset should be tool-exposed | ✅ | Strongly agree. |
| 05 | The right MVP is compiler-lite, not a full workflow compiler | ✅ | Best value/risk move. |
| 05 | The long pole is semantic parity and migration discipline, not code generation | ✅ | Completely right. |
| 05 | A small leaf workflow is the right first generated pilot | ⚠️ | Only for emitter ergonomics; one real agent contract pilot is also needed. |

## Minimal agent-contract surface: if only 3 things are declared in C#

If I could only declare three things at the agent boundary, I would declare these:

1. **Output contract with route-critical core + explicit failure/refusal variant**
   - Includes required vs optional fields, enum vocabularies, null/omit metadata, and the minimal fields needed to route safely.

2. **Capability/tool surface with scope + postconditions**
   - Which tools are exposed, what resource scope they are supposed to touch, and what deterministic checks run afterward.

3. **Consumer binding map**
   - Which output fields feed which routes, verbs, persisted artifacts, or downstream agents, with codecs and marker/invariant rules.

If those three are typed, most of the real agent-boundary drift becomes visible. Prompt prose can remain human-authored around them.

## Anticipated cross-reviewer tensions + preemptive responses

### With the type-correctness reviewer

**Expected pushback:** "If schemas are aspirational, the type system is theater. Make the schema strict, fail hard, and prove more."

**My response:** the type system is still very valuable, but it must describe the boundary honestly. There are two different truths here:

- **structural truth**: which fields exist, which route values are legal, which consumers bind to which fields;
- **semantic truth**: whether the model actually knew the answer.

Types can prove a great deal about the first and almost nothing about the second. If we make every field hard-required even when the model may not know them, we induce fabrication. The right compromise is strict typing for the route-critical core, explicit refusal/error variants, optional advisory fields, and post-parse verification. That preserves proofs where they are real and avoids forcing the model to lie where they are not.

### With the long-term architecture reviewer

**Expected pushback:** "Human-authored prompts and curated tool bundles create long-term maintenance burden. Standardize more in the compiler."

**My response:** standardize the seams, not the heuristics. I agree on aggressively standardizing contracts, bindings, tool declarations, traces, and generated prompt fragments. I disagree with pushing mission prose, examples, and model-specific tactics into the compiler. Those are the parts that change when models change. Hiding them in a type system does not remove maintenance; it just makes the iteration loop slower and less legible.

### Where I expect agreement with both reviewers

- build-time emission over JIT,
- Option A over runtime replacement,
- generated descriptors and route typing are real wins,
- emitted artifacts must stay inspectable,
- compiler-lite is the right first move.

## The "naive about agents" callouts

A few claims or tendencies across the reports strike me as naive or at least under-seasoned relative to production LLM behavior:

- **Any implication that schema conformance equals truthful output.** It does not.
- **Any implication that capability declarations are enforcement without sandboxing or allowlists.** They are not.
- **Any implication that a successful non-agent leaf pilot materially de-risks the agentic part of the compiler.** It does not.
- **Any tendency to model agents primarily as `Agent<TIn, TOut>` and stop there.** Real agents also have execution mode, tool surface, retry policy, and postconditions.
- **Any suggestion that typed workflow compilation gives deterministic replay of real model behavior.** Only frozen/stubbed replay is deterministic.

Overall the reports are better than average on this topic; the main blind spot is overestimating how much agent quality falls out of stronger typing.

## Open questions for synthesis

1. What is the mandatory **refusal/abstain/error** shape for agent outputs, and does every agent contract need one?
2. Should the compiler emit **prompt snapshots, tool specs, and parser policy** as first-class artifacts alongside workflow YAML?
3. For the first real pilot, which workflow gives the smallest honest test of the agent boundary: a reviewer/analyzer/poster flow rather than a pure router?
4. Where does the **eval corpus** live, and how are model-upgrade regressions run against compiled workflows?
5. Are long-form artifacts intentionally modeled as **freeform body + typed companion fields**, rather than over-typing markdown itself?
6. Does the intermediate representation carry enough metadata to support a future runtime without becoming a disguised second runtime now?
7. What is the explicit policy on **safe coercions** versus hard parse failures for structured outputs?
