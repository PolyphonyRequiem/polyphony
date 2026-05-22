# Workflow Compiler Investigation — Agent Contracts Integration

## Executive summary

- The highest-value compiler target is **the agent contract boundary**, not the whole agent: declare in C# the output shape, route domain, prompt-required facts, downstream consumers, and runtime capability profile; then generate conductor YAML, prompt scaffolding, and lint metadata from that.
- Today, the worst type loss happens at **agent JSON -> Jinja string interpolation -> CLI args -> re-parse in C#**. `plan-level.yaml`'s `architect.output.children` becomes `--children-json "..."`, then `PlanCommands.WritePlan` and `SeedChildren` parse it back into JSON again (`.conductor/registry/workflows/plan-level.yaml:991-1009, 2566-2584`; `src/Polyphony/Commands/PlanCommands.WritePlan.cs:38-50, 69-125`; `src/Polyphony/Commands/PlanCommands.SeedChildren.cs:195-228`).
- Prompts are already part of the contract surface. The prompt, YAML schema, routes, and downstream verb/script bindings repeatedly restate the same facts. A compiler should not fully synthesize prose, but it should generate the **load-bearing contract fragments** inside prompts so they cannot drift.
- Static compilation can enforce field existence, type compatibility, null/omission discipline, route exhaustiveness, and prompt/route/tool alignment. It **cannot** guarantee that the LLM told the truth or obeyed side-effect constraints; that must remain a runtime/harness/human concern.
- AB#3255 is **not yet a conductor replacement**. The proposal explicitly says “not a conductor redesign” (`docs/proposals/typed-contract-surface.md:23-29`). The pragmatic move is “compile to conductor now, keep an intermediate representation that could later power a polyphony-native runtime.”

## One real trace: `architect` -> `write-plan` -> `seed-children`

This is the cleanest existing example of where types are preserved, erased, and reconstructed.

### Step 1: the agent declares a rich structured output

`plan-level.yaml`'s `architect` agent emits:

- `plan: string`
- `children: array<object>` with `child_id`, `title`, `type`, `description`, `acceptance_criteria`, `pg`, `depends_on`
- `open_questions: array<object>`
- research-related fields including a flat discriminant `research_request_kind`

See `.conductor/registry/workflows/plan-level.yaml:582-704`.

This is already doing something important: the workflow authors flattened a prior nested-optional research shape into an always-present discriminated form specifically to avoid `StrictUndefined` failures in conductor (`plan-level.yaml:715-719`).

### Step 2: conductor routes on the structured output

The next route is driven by a string discriminant:

```yaml
- to: open_questions_policy
  when: "{{ architect.output.research_request_kind == 'none' }}"
- to: research_policy_resolver
  when: "{{ research_loop_counter.output.cap_reached == false }}"
```

(`plan-level.yaml:740-757`)

This is already a type contract, but it is only implicit. Nothing ties the prompt text, the YAML schema, and the allowed route values together except reviewer discipline.

### Step 3: structured values are flattened into CLI strings

The workflow then serializes the agent output back into strings:

```yaml
- name: write_plan
  command: polyphony
  args:
    - "plan"
    - "write-plan"
    - "--item-id"
    - "{{ workflow.input.work_item_id }}"
    - "--content-json"
    - "{{ architect.output.plan | tojson }}"
    - "--children-json"
    - "{{ architect.output.children | tojson }}"
```

(`plan-level.yaml:991-1009`)

At this boundary, `string` and `array<object>` are no longer typed values; they are just shell arguments.

### Step 4: the verb reparses them into typed/semityped data

`PlanCommands.WritePlan` expects:

- `itemId: int`
- `contentJson: string` that must decode to a JSON string
- `childrenJson: string` that must decode to a JSON array

and validates exactly that (`src/Polyphony/Commands/PlanCommands.WritePlan.cs:43-50, 69-125`). It then writes the markdown file and a `plan-{id}.children.json` sidecar for later recovery (`WritePlan.cs:21-29, 154-190`; `src/Polyphony/Models/PlanWritePlanResult.cs:31-50`).

### Step 5: the next verb reparses the same logical contract again

Later, `seeder` calls:

```yaml
- "plan"
- "seed-children"
- "--children-json"
- "{{ architect.output.children | tojson if (architect is defined and architect.output.children is defined) else '' }}"
- "--children-from-ref"
- "origin/main"
```

(`plan-level.yaml:2566-2599`)

`PlanCommands.SeedChildren` then resolves the same child list from one of four sources: CLI JSON, local sidecar, git ref sidecar, or root-facets fallback (`src/Polyphony/Commands/PlanCommands.SeedChildren.cs:195-205`). It must defensively re-parse and re-type every field because the source is now just JSON text (`SeedChildren.cs:450-478`).

### Where types are lost

1. **Prompt -> YAML schema**: duplicated by hand.
2. **YAML schema -> CLI arguments**: rich values become strings.
3. **CLI strings -> verb internals**: reparsed into `JsonDocument`/`JsonNode` instead of a shared DTO.
4. **Persistence sidecar**: child objects are durable, but not obviously backed by one named record.
5. **Semantic contract**: “stable `child_id` across replans” is load-bearing (`plan-level.yaml:598-607`) but not representable in conductor's schema.

If the workflow compiler does only one thing for agents, it should eliminate this pattern by letting one declared `PlannerOutput` and one declared `PlannedChild` contract drive the agent schema, the CLI binding codec, the persisted sidecar schema, and the seeder's deserializer.

## 1. What today's contracts look like

Here are four representative agent-contract shapes already in use.

| Flow | Agent output | Consumer | Where type info degrades |
|---|---|---|---|
| `architect` -> `write-plan` / `seed-children` | `plan: string`, `children: array<object>`, `research_request_kind: string` | `polyphony plan write-plan`, `polyphony plan seed-children`, route predicates | array/object flattened into CLI strings; child item schema reimplemented in parser logic |
| `plan_reviewer` -> poster -> analyzer | `blocking_concerns: string[]`, `comment_body: string`, `posted: bool`, `pr_number: number` | `plan_reviewer_poster[_ado]`, then `pr_feedback_analyzer` and `poll_status[_ado]` | marker syntax and “blocking only” semantics live in prompt prose, not type metadata (`plan-level.yaml:1242-1248, 1361-1384, 2848-2865`) |
| `feature_pr_updater` -> `post-comment-ado` | `comment_body: string`, `posted: bool`, `review_requested: bool`, `pr_number: number` | `feature_pr_updater_poster_ado` -> `polyphony pr post-comment-ado` | agent emits `pr_number`, but poster ignores it and re-reads from creator output; string body passes via CLI (`feature-pr.yaml:1071-1185, 1213-1230`) |
| `scope_reviewer` / `root_reviewer` -> routes | `verdict: string`, `issues: string[]`, `feedback: string` | route tables leading to PR open, revise loop, or policy gate | route domain is hand-enumerated in YAML; no shared enum between prompt and routes (`implement-merge-group.yaml:678-796, 1608-1805`) |

A few patterns stand out.

### Route domains are contracts too

`root_reviewer.output.verdict` is effectively an enum with two legal values, but conductor only knows “string”:

- `approved` -> `impl_pr_open`
- `changes_requested` -> `coder`
- catch-all -> `coder`

(`implement-merge-group.yaml:787-796`)

Likewise `scope_reviewer.output.verdict` routes to PR open or approvals policy, with a catch-all safety route (`implement-merge-group.yaml:1796-1805`).

### Omission/null behavior leaks through the whole stack

Conductor's LLM output schemas have no real optional fields; every declared field is required (`.github/skills/conductor-mechanics/references/m02-llm-output-schemas.md:71-80`). Meanwhile the polyphony serializer normally omits nulls unless a field overrides that behavior. That is why plan-level needed both workflow guards and C# fixes such as `[JsonIgnore(Condition = Never)]` on `PlanExtractRenegotiationFlagResult.RenegotiationRequest` (`src/Polyphony/Models/PlanExtractRenegotiationFlagResult.cs:33-60`).

### Prompt prose is carrying machine constraints

The `plan_reviewer` prompt says the first line of `comment_body` **must** be a marker comment and that only the `**Blocking concerns**` section counts for analyzer routing (`plan-level.yaml:1242-1248, 1361-1384`). That is not editorial flourish; it is protocol.

## 2. What a unified type system could enforce

A workflow compiler with C# as authority can enforce more than field names.

### Producer/consumer compatibility

- `PlannerOutput.Children : IReadOnlyList<PlannedChild>` can only bind to a consumer that accepts `IReadOnlyList<PlannedChild>` or an explicit codec to JSON text.
- `FeaturePrUpdaterOutput.CommentBody : MarkdownCommentBody` can bind to `PrPostCommentAdo.body` only through a declared string codec.
- `RootReviewerVerdict` can be an enum, not “string with comments.”

### Route exhaustiveness

For booleans and enums, the compiler should require:

- all declared variants handled, or
- an explicit generated catch-all terminal.

Examples:

- `HasNegativeFeedback: bool` in `pr_feedback_analyzer` should statically prove there is a `true` branch, a `false` branch, and the policy for malformed/absent output.
- `ReviewVerdict.Approved | ChangesRequested` should map exactly to two routes plus optional `unexpected_output` fallback.
- `ResearchRequestKind.None | Request` in `architect` should match the route table in `plan-level.yaml:740-757`.

This is stronger than JSON Schema. It is a **workflow-level exhaustiveness check**.

### Prompt/route alignment

If an agent can choose among routes, the compiler should be able to prove:

- the prompt names the route values or decision outcomes;
- the YAML routes cover them;
- any downstream sub-workflow or verb expects the same vocabulary.

Today, this is manual. `pr_feedback_analyzer`'s prompt defines the semantics of `has_negative_feedback`, while the route table assumes those semantics (`plan-level.yaml:1617-1713`).

### Null/omission discipline

The compiler should publish, per field:

- JSON field name
- CLR nullability
- omit-when-null behavior
- whether conductor may treat it as absent

AB#3255 already heads this direction: it explicitly recommends publishing required/optional and can-omit-when-null metadata (`docs/proposals/typed-contract-surface.md:121-145`).

### Binding provenance

Every workflow read should be traceable to a producer contract. Inference-first binding is already proposed for verbs, scripts, and child workflows (`typed-contract-surface.md:193-204`). For agents, the same rule should hold: every `{{ agent.output.foo }}` reference should resolve to a declared field on a named contract.

### Semantic invariants beyond raw shape

A few constraints belong in contract metadata even though they are not JSON-structural:

- `comment_body` must begin with a marker line.
- `feedback_digest` must be stable across identical inputs.
- `child_id` must be stable across replans.
- `blocking_concerns` are the only bot-generated items that gate revision.

Those are ideal candidates for generated tests and lints rather than raw schema.

## 3. Should the prompt be generated from the C# declaration?

**Not fully. Definitely partially.**

Why not fully generated? Because the best prompts in this repo are not just schemas with instructions stapled on. `architect-plan-level.md` is a large, state-sensitive artifact that changes behavior based on re-entry source, prior reviewer feedback, parent-patch extraction, research dispatch, and open-question loops (`.conductor/registry/prompts/architect-plan-level.md:1-258`). Replacing that with fully synthesized prose would destroy valuable tacit knowledge.

But prompts are still part of the contract system. The middle ground is:

### Keep human-authored prose, generate compiler-owned fragments

I would split a prompt into three layers:

1. **Human-authored mission/rubric prose**
2. **Compiler-owned contract fragment**
   - exact output fields
   - route vocabulary
   - tool restrictions
   - marker conventions
   - examples derived from the C# contract
3. **Workflow-supplied context slots**
   - PR number
   - branch name
   - type guidance
   - prior feedback summary

That yields something like:

```csharp
public sealed record AgentPromptTemplate<TOutput>(
    string HumanBodyMarkdown,
    IReadOnlyList<PromptSlot> Slots,
    ContractPromptPolicy ContractSectionPolicy);
```

The compiler should own the parts that must never drift: “return JSON with these fields,” “these are the allowed decision values,” “you do/do not have the `polyphony` tool,” “this field must start with marker X.”

That is exactly the kind of duplication visible in `plan_reviewer` and `feature_pr_updater` today (`plan-level.yaml:1293-1301, 1361-1414`; `feature-pr.yaml:1091-1183`).

## 4. Output schema enforcement vs. agent reality

Conductor's current schema enforcement is necessary but incomplete:

- LLM agents need explicit `output:` schemas or conductor treats the response as raw text (`m02-llm-output-schemas.md:3-35`).
- Every declared field is effectively required (`m02-llm-output-schemas.md:71-80`).
- The validator does not cross-check template reads against schemas (`m02-llm-output-schemas.md:82-87`).

A compiler should therefore produce **three layers of enforcement**.

### Static

- output field existence
- type compatibility
- route exhaustiveness
- null/omission guard requirements
- prompt/route/tool consistency

### Runtime

- structured-output retry policy
- optional lightweight coercion for mechanically safe cases only
- deterministic postcondition checks

The repo already shows the right runtime instinct: do not blindly trust the agent. `branch_assert_on_impl_post_coder` verifies the coder did not move HEAD off the expected branch after a direct-mutation turn (`implement-merge-group.yaml:512-552`). That is the right model: **verify effects, not intent**.

### Test harness

The contract compiler should generate harness fixtures from C# records. For each agent contract, you should be able to feed a typed stub output into the workflow harness and assert the route/result. That is much better than hand-authoring ad hoc JSON.

I would explicitly support:

- happy-path fixtures from strongly typed records
- malformed-output fixtures (missing field, wrong enum value)
- semantic-failure fixtures (empty marker, unstable digest)

## 5. Artifact handling and the trust boundary (D12)

D12 is the hard stop against magical thinking.

Polyphony's journal is honest that **agent-direct mutations are not polyphony mutations**. The coder agent commits directly with git; polyphony sees none of that and should not pretend otherwise (`docs/proposals/polyphony-journal.md:410-447`).

That means a compiler cannot honestly promise “this agent MUST commit code” unless the runtime also controls the tools.

### The right model: declared capabilities + verifiable postconditions

I would model agent-side contracts like this:

- **Capabilities**: what tools/resources the agent is allowed to touch
- **Scopes**: which branches/files/PRs/work items it is supposed to affect
- **Postconditions**: what the deterministic runtime can verify afterward

For example:

```csharp
public sealed record AgentCapabilityProfile(
    IReadOnlySet<ToolCapability> AllowedTools,
    IReadOnlySet<ResourceScope> ResourceScopes,
    IReadOnlyList<PostconditionCheck> Postconditions);
```

For `coder`, that might be:

- allowed: filesystem, git, twig read
- forbidden: switching away from expected impl branch
- postconditions: HEAD still on impl branch; commit exists or explicit no-op note exists

Today the workflow approximates this with prompts plus `assert-on-impl` verifiers (`implement-merge-group.yaml:304-349, 532-552`). A compiler should formalize that pattern, not overclaim sandbox enforcement it does not have.

## 6. Agent-side type unification / SDK shapes

Near term: **yes, but keep it thin**.

The useful generated artifacts are:

- typed output DTOs for harness fixtures
- prompt-side snippets/examples
- tool/consumer descriptors for editor help
- maybe JSON Schema/TypeScript for external tooling

The overreach would be pretending today's prompt-only agent model is already a rich typed SDK model. It is not. The agent still mostly sees prose plus a small tool palette.

So I would generate:

- `*.schema.json` / `*.d.ts` for contract inspection
- prompt snippets/examples
- harness stub builders

I would not yet invest in a large “agent SDK” abstraction unless polyphony starts exposing first-class tool-calling to agents.

## 7. OpenAI/Anthropic structured output and function calling

The compiler should target a provider-neutral intermediate model with two execution modes:

1. **structured JSON output**
2. **tool/function invocation**

Most existing polyphony agents are better described as “produce a typed decision or document.” Those should stay structured-output-first.

A smaller subset are natural tool candidates:

- poster flows (`post-comment-ado`)
- maybe simple routing/classification helpers
- maybe low-risk read verbs

What it should **not** do initially is expose all ~80 polyphony verbs directly as callable tools. That would explode the action surface, blur safety boundaries, and make prompt/tool selection harder.

Better approach:

- keep C# verb declarations as the source of truth for possible tools;
- expose only a curated, workflow-specific subset as callable functions;
- prefer higher-level workflow tools over raw low-level verbs.

In other words: verb declarations should be **tool-describable**, not automatically tool-exposed.

## 8. Polyphony as the agent runtime?

There is a real architectural north star here, but AB#3255 is not asking for that jump yet.

### Benefit

A polyphony-native runtime could eliminate several current boundaries:

- YAML/Jinja indirection
- PowerShell wrappers for simple bindings
- duplicated route vocabularies
- separate schema/publication mechanisms

### Cost

It would also require polyphony to absorb a lot of conductor behavior:

- prompt execution and model/provider integration
- retries and parse recovery
- route engine
- human gates
- parallel/foreach orchestration
- context history/checkpointing

That is an entirely different project. The current proposal explicitly says it is **not a conductor redesign** (`typed-contract-surface.md:23-29`).

My recommendation: design the workflow compiler around a **runtime-neutral IR** and make the first backend emit conductor YAML + script bindings. If that IR proves out, a future polyphony-hosted runner becomes plausible without committing now.

## 9. Compose-ability: shared agents and near-clone agents

Avoid inheritance-heavy designs. The reuse units here are more naturally:

- output contracts
- prompt fragments
- capability profiles
- binding sets
- route specs

A good abstraction shape is closer to composition:

```csharp
public sealed record AgentContract<TOutput>(
    string Id,
    Type OutputType,
    PromptTemplate Template,
    AgentCapabilityProfile Capabilities,
    IReadOnlyList<RouteSpec<TOutput>> Routes,
    IReadOnlyList<ConsumerBinding<TOutput>> Consumers);
```

Then you can share:

- a `MarkerCommentReviewerOutput` contract across `plan_reviewer`-style agents
- a `PosterBinding<T>` for ADO/GitHub comment posting
- prompt fragments like “blocking concerns protocol” or “ADO dual-poster restriction”

This fits the actual reuse patterns in the repo better than subclassing. `plan_reviewer` and `feature_pr_updater` are not the same agent, but they share a **dual-poster** shape and a “comment_body is consumed downstream” contract (`plan-level.yaml:1250-1261`; `feature-pr.yaml:1054-1070, 1194-1230`).

## The “agent contract” as a C# concept

Here is the concrete shape I would prototype.

```csharp
public enum ContractStability { Public, Internal, Deprecated }
public enum AgentExecutionMode { StructuredOutput, ToolCalling }

public sealed record AgentContract<TOutput>
{
    public required string Id { get; init; }
    public required ContractStability Stability { get; init; }
    public required AgentExecutionMode ExecutionMode { get; init; }
    public required PromptTemplate Prompt { get; init; }
    public required AgentCapabilityProfile Capabilities { get; init; }
    public required RetryPolicy Retry { get; init; }
    public required IReadOnlyList<RouteSpec<TOutput>> Routes { get; init; }
    public required IReadOnlyList<ConsumerBinding<TOutput>> Consumers { get; init; }
}

public sealed record ConsumerBinding<TOutput>(
    Expression<Func<TOutput, object?>> Field,
    ConsumerKind Kind,
    string ConsumerId,
    ValueCodec Codec,
    bool Required);
```

Example for plan reviewer:

```csharp
public sealed record PlanReviewerOutput(
    bool Posted,
    int PrNumber,
    string Summary,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> BlockingConcerns,
    IReadOnlyList<string> NonBlockingSuggestions,
    string CommentBody);

var planReviewer = new AgentContract<PlanReviewerOutput>
{
    Id = "agent.plan-reviewer",
    Stability = ContractStability.Public,
    ExecutionMode = AgentExecutionMode.StructuredOutput,
    Prompt = PromptTemplate.FromMarkdown("plan-reviewer.md")
        .WithCompilerOwnedSection(ContractSections.OutputExample<PlanReviewerOutput>())
        .WithCompilerOwnedSection(ContractSections.MarkerRule(
            field: x => x.CommentBody,
            marker: "<!-- polyphony:agent-comment agent=plan_reviewer ... -->")),
    Capabilities = AgentCapabilityProfile.ReadOnly(filesystem: true),
    Retry = RetryPolicy.StructuredOutputDefault,
    Routes =
    [
        RouteSpec.Bool(x => x.Posted, true, to: "poll_status"),
        RouteSpec.CatchAll("poster_failure_gate")
    ],
    Consumers =
    [
        ConsumerBinding.For(x => x.CommentBody)
            .ToVerb("pr.post-comment-ado", arg: "body", codec: ValueCodec.String),
        ConsumerBinding.For(x => x.BlockingConcerns)
            .ToAgent("pr_feedback_analyzer", semanticRole: "negative-feedback-source")
    ]
};
```

And for the planner child sidecar flow:

```csharp
public sealed record PlannedChild(
    string ChildId,
    string Title,
    string Type,
    string Description,
    IReadOnlyList<string> AcceptanceCriteria,
    string? Pg,
    IReadOnlyList<string> DependsOn);

public sealed record PlannerOutput(
    string Plan,
    IReadOnlyList<PlannedChild> Children,
    IReadOnlyList<OpenQuestion> OpenQuestions,
    string Summary,
    ResearchRequest Research);
```

The key is that `PlannedChild` is no longer anonymous JSON inside a prompt/YAML/sidecar/parser chain. It is one contract everywhere.

## Recommendations: what to enforce where

| Boundary | Compiler should enforce | Runtime should enforce | Humans should own |
|---|---|---|---|
| agent output schema | field existence, type compatibility, null/omit metadata, route exhaustiveness | parse retry, provider-specific structured-output behavior | whether the rubric is good |
| prompt contract | generated output/route/tool fragments, marker rules, tool restrictions | none beyond rendering | mission prose, heuristics, domain nuance |
| agent -> script/verb binding | arg/field compatibility, explicit codecs, deprecation warnings | actual subprocess success/failure | exceptions to the model |
| agent side effects | capability declaration, scope declaration | postcondition checks, drift/journal observation, sandbox if available | risk acceptance when not sandboxed |
| child workflow outputs | metadata.output_contract, parent/child field compatibility | end-of-run rendering/coercion | whether the parent should depend on that output at all |

In plain English:

- **Compiler**: shapes, vocabularies, bindings, and declared intent.
- **Runtime**: truth of execution.
- **Humans**: quality of judgment and policy.

## Open questions for synthesis

1. Should the first implementation type only **agent outputs consumed by verbs/routes**, or also all route-only/internal counter envelopes from day one?
2. Do we want a first-class `AgentContract<TOutput>` abstraction in C#, or a lighter annotation model on workflow-builder nodes?
3. How far should prompt compilation go: generated fragments only, or a fuller prompt-template DSL?
4. Should route domains be normalized toward enums/discriminated unions instead of `string`/`bool` fields in agent outputs?
5. For function calling, do we expose curated workflow tools or derive tools from all verb declarations automatically? I strongly recommend the curated path.
6. Is there appetite for runtime-enforced capability profiles (sandbox/tool allowlists), or is the near-term contract only descriptive plus postcondition checks?
7. Should persisted artifacts like `plan-{id}.children.json` be generated from named DTOs immediately? That seems like the single cleanest early win.
8. How much of conductor's current null/omission pain should be modeled in the compiler versus waiting for a polyphony-native runtime/backend?

## Bottom line

If this workflow compiler does not treat **agent outputs, prompt protocol, route vocabulary, and downstream verb bindings as one declared contract**, it will only move the stringly boundary around. The correct design center is not “generate YAML from C#”; it is “declare agent contracts once, then project them into YAML, prompts, scripts, tests, and docs.”
