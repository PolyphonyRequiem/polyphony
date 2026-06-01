---
doc_type: discussion
status: exploratory
synopsis: LLM-skeptic critique of compiler-lite — verdict NARROW-SCOPE; C# cannot guarantee agent reliability, only lintable choreography.
---

# D4 — LLM Skeptic Critique: Agent Contracts Are Not Contracts Unless Runtime Failure Is First-Class

**Verdict: NARROW-SCOPE** — route vocabularies, bindings, malformed-output handling, and typed harness fixtures are worth compiling, but "typing the agent contract boundary" overstates what C# can guarantee about stochastic agents. The synthesis is directionally right to attack duplicate prompt/YAML/route/CLI contracts, but wrong if the phrase "agent contract" implies reliability rather than lintable choreography plus runtime verification.

---

The synthesis and Investigator 4 are right about one thing: Polyphony has real duplication at the agent boundary. `architect.output.children` being declared in a prompt/schema, threaded through YAML/Jinja, serialized into CLI args, reparsed by C#, persisted as a sidecar, and reparsed again is exactly the kind of stringly seam that deserves pressure. Route domains like `approved | changes_requested` should not live as undocumented string folklore. A typo like `architect.output.kids` should fail before dispatch.

But the synthesis makes a stronger claim: "the agent contract boundary is the highest-value target." That phrase is dangerous. It suggests C# can make the LLM boundary contract-like. It cannot. It can type the *projection* of agent output after parsing. It can type the downstream consumers. It can type the route vocabulary. It cannot make the model produce the thing, mean the thing, or tell the truth.

So the right verdict is not "drop all typing." It is: narrow the claim until it stops lying.

## 1. A Type System Cannot Constrain a Stochastic Process

A C# record like this looks comforting:

```csharp
public sealed record PlannerOutput
{
    public required string Plan { get; init; }
    public required IReadOnlyList<PlannedChild> Children { get; init; }
    public required ResearchRequestKind ResearchRequestKind { get; init; }
}
```

The compiler now knows `Children` exists. The model does not.

At runtime, the agent may return:

```json
{
  "plan": "We should split this into two items.",
  "child_items": [
    { "title": "Implement parser", "type": "Task" }
  ],
  "research_request_kind": "not needed"
}
```

Or worse:

```json
{
  "plan": "I cannot safely plan this without more context.",
  "children": [],
  "research_request_kind": "none"
}
```

Both can be made to fit a shape. Only one is mechanically parseable. Neither is necessarily semantically valid.

The synthesis acknowledges "C# cannot prove agent truthfulness," but it still treats the named DTO as a major reliability win. That is only true if the runtime policy is explicit:

- Missing field: hard fail? repair? route to `unexpected_agent_output`?
- Unknown enum: hard fail? map to refusal? retry?
- Empty-but-valid list: allowed? suspicious?
- Required field satisfied with placeholder text: parse success or semantic failure?
- Model refusal: separate variant or shoved into `plan`?

Without those answers, the typed record is a decorative post-parse assertion. The compiler is enforcing the fiction that successful deserialization means the boundary held.

Recommended narrowing: the proposal should rename this from "agent contract" to something like **agent output projection + route binding contract** unless runtime malformed-output behavior is part of the contract surface.

## 2. Schema Is a Soft Suggestion, and the 1% Matters Most

Structured-output APIs are better than raw JSON prompting. They are not contracts. Function calling and JSON schemas usually work in the easy cases, which are not the cases that break SDLC workflows.

The failure distribution is not uniform. The model is most likely to violate the schema when:

- the context is long,
- instructions conflict,
- the requested task is impossible,
- the model is uncertain,
- safety/refusal behavior is triggered,
- a retry asks it to "fix the JSON" without fixing the reasoning,
- the schema pressure forces an answer where "unknown" is the honest response.

That means the 1–5% schema failure rate is not a random nuisance. It is concentrated exactly where Polyphony most needs reliability: novel work items, ambiguous plans, model upgrades, weird ADO state, unexpected PR comments, and failed tool calls.

Example: a reviewer agent is asked:

> Review this plan. If there are blocking issues, list them. Return `approved` or `changes_requested`.

The plan is partially missing because an upstream fetch failed. A strict schema may push the model toward:

```json
{
  "verdict": "approved",
  "issues": [],
  "feedback": "No blocking concerns found."
}
```

That is well-typed and catastrophic. The honest output was:

```json
{
  "verdict": "unable_to_review",
  "reason": "Plan content was not available."
}
```

If the contract has no first-class inability/refusal/fetch-failed variant, the type system has not improved safety. It has compressed uncertainty into a legal lie.

R2's "strict core / soft perimeter / explicit refusal" is the right direction, but the synthesis under-specifies it. "Mandatory refusal/abstain variant" appears as a resolution and open question, but the pseudo-code still models refusal as nullable periphery:

```csharp
public AgentRefusal? Refusal { get; init; }
```

That is too weak. A nullable refusal alongside required success fields invites contradictory outputs: `children` plus `refusal`. A real contract needs a sum type shape:

```csharp
PlannerResult =
  | Success { plan, children, research_request_kind }
  | Refused { reason, missing_inputs }
  | Malformed { raw_output, parser_error }
```

C# lacks native discriminated unions, so this is harder. But if the proposal cannot pay that cost, it should stop pretending the agent output is "contracted."

## 3. The Prompt Is the Actual Behavioral Contract

The synthesis says prompt prose stays human-authored and the compiler owns only contract fragments. Good. But that concession weakens the "agent contract boundary" claim.

The thing that actually influences the model is not the C# record. It is the prompt:

- what task it thinks it is doing,
- which examples it sees,
- how uncertainty is framed,
- whether it is rewarded for producing something vs. abstaining,
- whether the route values are semantically explained,
- whether "blocking" means "must fix" or "worth mentioning,"
- whether the model sees enough context to answer honestly.

A generated fragment like:

> Return JSON with fields `verdict`, `issues`, `feedback`.

does not define the behavior. The prompt body defines the behavior:

> Only put issues in `blocking_concerns` if they prevent safe merge. Do not include suggestions there. If plan content is unavailable, return `unable_to_review`.

That is prose. It needs review by humans. It needs examples. It needs evals. It needs fast iteration after model drift.

So what remains for C# to compile? Mostly:

- field names,
- JSON shape,
- enum values,
- route bindings,
- consumer bindings,
- marker protocols,
- prompt fragment injection.

That is useful. But it is not the whole "agent contract." It is **route-condition lint plus schema projection plus generated snippets**.

The synthesis should be more honest: compiler-lite helps prevent prompt/schema/route *drift*. It does not define the agent's judgment contract. The prompt and eval suite do that.

## 4. The Lie Problem: Types Give Lies a Valid Shape

Agents sometimes claim success when they failed. This is not primarily a schema problem.

A coder agent may output:

```json
{
  "status": "complete",
  "summary": "Implemented the requested change and all tests pass.",
  "tests_run": ["dotnet test"]
}
```

But `dotnet test` was never run. Or it failed and the agent summarized optimistically. The JSON is perfect. The route proceeds. The workflow opens a PR with false confidence.

A planner agent may say:

```json
{
  "research_request_kind": "none",
  "open_questions": [],
  "plan": "No research needed."
}
```

But it skipped a required product-policy lookup.

A reviewer agent may say:

```json
{
  "verdict": "approved",
  "blocking_concerns": []
}
```

while the freeform explanation hedges:

> This appears probably okay, though I was unable to inspect the generated workflow in detail.

A typed envelope discards the soft signal unless raw output and confidence cues remain visible.

The defense against lies is not a DTO. It is deterministic verification:

- did the branch remain correct?
- did a commit actually appear?
- did tests actually execute?
- does the PR comment contain the marker?
- does the sidecar match the planned children?
- are route-critical assertions checked by scripts or harness?
- is there drift detection between declared effects and observed repo/ADO state?

The synthesis mentions postconditions and journal effects, but those are outside the agent-contract proposal. That means the highest-value safety work may not be typing the agent boundary at all. It may be expanding deterministic postcondition checks and effect observation.

Recommended fix: every agent contract should explicitly separate **reported facts** from **verified facts**. The compiler should not let a route depend on an agent-reported success if a deterministic verifier exists.

## 5. Model-Version Drift Turns Contracts Into Fossils

Claude 4.6 and Claude 4.7 may both satisfy the schema while changing behavior:

- one uses `unknown`, another uses `none`;
- one refuses in prose before JSON, another emits partial JSON;
- one treats empty arrays as "no issues," another as "not inspected";
- one follows examples more strongly than schema descriptions;
- one starts overusing abstain after a safety tuning change;
- one starts fabricating required fields more aggressively.

The C# contract does not encode model-family assumptions. It can say `ResearchRequestKind.None`, but it cannot say "Claude Sonnet 4.6 tends to comply with this prompt if the refusal variant is shown as the first example; GPT-4.1 needs an explicit negative example."

If contracts are checked into source and generated into YAML, they risk becoming fossils of one model's behavior. When the model changes, the contract still compiles, the YAML still validates, and the break moves into runtime semantics.

This is where evals are not optional. A real agent-contract discipline requires:

- prompt snapshot identity,
- model/provider version in trace,
- raw response retention,
- parsed output retention,
- malformed-output rate tracking,
- route distribution tracking,
- nightly eval corpus,
- model-upgrade rebaseline.

The synthesis mentions malformed-output fixtures and a model-upgrade rebaseline loop mostly through R2 summary, but it does not make eval infrastructure part of D14. That is a serious gap.

If the claim is "agent boundary is highest-value," then evals are part of that boundary. Otherwise compiler-lite is optimizing the authoring surface while leaving the actual behavioral contract untested.

## 6. False Confidence Is a Real Tax

Typed envelopes change reviewer behavior. Once the output is `PlannerOutput`, people stop reading the raw transcript. They inspect `children.Count`, `ResearchRequestKind`, and route taken. The weirdness gets compressed away.

But raw agent output often contains valuable soft signals:

> I believe this is likely sufficient.

> Assuming the branch contains the latest changes...

> I could not find the file, but the plan should still work.

> The tests appear to pass based on the provided output.

Those are not schema violations. They are uncertainty leaks. A typed parser may throw them away or bury them in `summary`.

This is the ORM problem: the abstraction makes easy cases easier and weird cases invisible. With LLMs, weird cases are the work.

Compiler-lite should therefore require operational UX around raw-vs-parsed output:

- every parsed output trace links to raw response,
- every repair/coercion is visible,
- every route-critical field shows provenance,
- every abstain/refusal is routed visibly,
- every "successful parse with suspicious content" can be flagged by heuristics.

Without that, the compiler may reduce YAML mistakes while increasing human over-trust.

## 7. Evals Are the Missing Comparison Class

The synthesis compares compiler-lite to current chaos. That is too easy. The right comparison is:

- compiler-lite on agent contracts,
- versus more evals and postcondition checks,
- versus better prompt snapshotting,
- versus narrower tool surfaces,
- versus malformed-output replay fixtures.

If the goal is fewer production failures from agents, evals may dominate typing.

For example, consider `plan_reviewer`. A typed contract can catch `blockingConcern` vs `blocking_concerns`. Good. But an eval suite can catch:

- model puts non-blocking suggestions into blocking section,
- model approves incomplete plan,
- model omits marker line under long context,
- model treats "needs research" as "changes requested,"
- model changes behavior after provider upgrade.

Those are more important than field-name typos.

Compiler-lite can help generate typed eval fixtures, but that should be a primary deliverable, not a side effect. D14 should include: for every typed agent contract, at least N malformed-output fixtures and M semantic eval cases must exist before the contract is considered migrated.

Otherwise the proposal optimizes for compile-time confidence where the failures are behavioral.

## 8. YAML May Be Better for Debugging Than C# Projections

Today, debugging a bad agent means opening the workflow and prompt artifacts. You can see:

- the node,
- the output schema,
- the route conditions,
- the prompt path or embedded prompt,
- downstream script args.

C#-authored contracts risk adding hops:

1. open generated YAML,
2. find source-map comment,
3. open C# contract,
4. inspect generated prompt fragment,
5. open human prompt markdown,
6. inspect generated schema,
7. inspect emitted route condition,
8. compare raw run output.

The synthesis tries to preserve generated YAML as first-class, which is necessary. But if the C# contract becomes the authority, debugging now requires understanding the generator's projection rules. That is worse exactly when the system is already failing.

This is not an argument against lint. It is an argument against over-centralizing authorship.

For compiler-lite, the best shape is probably:

- YAML remains the operational artifact,
- prompt markdown remains the behavioral artifact,
- C# registry declares route-critical fields and bindings,
- CI lints YAML/prompt/schema against registry,
- generated snippets are visibly delimited,
- source maps are mandatory.

Do not make the C# contract the only place a human can understand the agent.

## 9. Where the Synthesis Is Right

Some things absolutely should be typed.

Route vocabulary is the strongest example. If `scope_reviewer.output.verdict` can only be `approved` or `changes_requested`, that should be an enum. Routes should be exhaustive. Unknown values should have an explicit failure route. Prompt fragments should list exactly those values.

Consumer bindings are also good compiler territory. If `architect.output.children` is consumed by `write-plan`, `seed-children`, and a sidecar parser, one named DTO is better than anonymous JSON. A compiler can catch field drift, codec mistakes, and route reads against missing fields.

Marker protocols are also worth declaring. If `comment_body` must start with `<!-- polyphony:agent-comment ... -->`, that can be linted, tested, and included in prompt fragments.

Typed harness fixtures are a real win. Being able to feed `PlanReviewerOutput { Verdict = ChangesRequested }` into a workflow harness and assert the route is valuable. Malformed fixtures are even more valuable.

So the useful compiler surface is:

- route-critical enum overlays,
- field existence lint,
- explicit codecs,
- required/optional/null/omit metadata,
- generated prompt contract fragments,
- malformed-output policy,
- typed fixtures,
- raw/parsed trace schema,
- deterministic postcondition binding.

That is narrower than "agent contract boundary." It is still worth doing.

## 10. Recommended Course Adjustment

Change the central claim from:

> The agent contract boundary is the highest-value compiler target.

to:

> The highest-value compiler target is the deterministic perimeter around agent outputs: route vocabularies, consumer bindings, schema/prompt drift, malformed-output routing, and typed harness fixtures. The model's behavior remains governed by prompt quality, evals, tool constraints, and postcondition verification.

Then make three changes to D14:

1. **Make malformed-output runtime policy first-class.** Every agent contract must define parse failure, unknown enum, missing route-critical field, refusal, and contradictory-output behavior.

2. **Promote evals from open question to milestone.** No real agent contract pilot counts unless it includes semantic evals and model-upgrade replay cases, not just malformed JSON fixtures.

3. **Separate reported from verified facts.** Routes should prefer deterministic verifier outputs over agent self-report whenever possible.

## Verdict on the Agent-Contract-Boundary Claim

**NARROW-SCOPE.** Keep the compiler-lite work for route vocabularies, output projections, binding provenance, prompt-fragment drift prevention, and typed/malformed harness fixtures. Drop the stronger implication that C# can create a reliable "agent contract" at the LLM boundary. The contract that matters is distributed across prompt, schema, parser, retry policy, raw transcript, eval corpus, tool sandbox, and postcondition verifier; typing only one slice is useful, but calling it the highest-value target without elevating evals and runtime failure policy creates exactly the false confidence this system cannot afford.
