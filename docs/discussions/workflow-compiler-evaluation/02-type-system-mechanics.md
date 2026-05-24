---
doc_type: discussion
status: exploratory
synopsis: Compiler-lite investigation — C# type-system unification mechanics; argues for fluent-builder over typed records, not attributes or reflection.
---

# Workflow compiler investigation — type-system unification mechanics in C#

## Executive summary

- The cleanest unification is **C#-first workflow declarations authored as a fluent builder over typed records**, not attributes and not runtime reflection.
- Polyphony already has the seed of this in `PolyphonyJsonContext`, `[VerbResult]`, and `VerbOutputSchemaCatalog`; the compiler should generalize that pattern to **verbs, scripts, gates, workflow outputs, prompt contexts, and resource effects**.
- The biggest compile-time wins come from replacing stringly Jinja route logic with **lambda-based selectors over typed step outputs**, plus generated descriptors for polyphony verbs and child workflows.
- For polyphony, the primary path should be **build-time YAML emission + Roslyn analyzers**, with optional runtime cache regeneration only for specialized variants; true per-invocation reflection/JIT should not be the default.
- D13’s `[MutatesResource]` + `JournalResourceEffect[]` is the right hook for a compiler: static attributes define what a workflow *may* touch; runtime journal effects record what it *did* touch.

---

## Surface-area sketch: representative workflows rendered in C#

### Proposed authoring model in one sentence

Use **records for contracts**, **generated descriptors** for verbs and child workflows, and a **fluent workflow builder** whose route predicates are ordinary C# lambdas over typed outputs.

I would assume a generated surface roughly like this:

```csharp
using Polyphony.Compiler;
using Polyphony.Compiler.Prompts;
using Polyphony.Generated;
using static Polyphony.Generated.PolyphonyVerbs;
using static Polyphony.Generated.WorkflowCatalog;

// Generated from [Command] + [VerbResult] + PolyphonyJsonContext.
public static partial class PolyphonyVerbs
{
    public static class Branch
    {
        public static VerbDescriptor<BranchNextImplArgs, BranchNextImplResult> NextImpl { get; }
            = GeneratedVerbs.Branch_NextImpl;
    }
}

// Generated from workflow declarations.
public static partial class WorkflowCatalog
{
    public static WorkflowDescriptor<GithubPrInput, GithubPrOutput> GithubPr { get; }
        = GeneratedWorkflows.GithubPr;
}

// Generated overlay over the current string discriminant.
public enum BranchNextImplAction
{
    ImplementItem,
    AllItemsDone,
    Error
}

public sealed partial record BranchNextImplResult
{
    public BranchNextImplAction ActionKind => BranchNextImplActionCodec.Parse(Action);
}
```

That is the crucial bridge: workflow authors reference `PolyphonyVerbs.Branch.NextImpl`, not the string `"branch next-impl"`; routes compare `ActionKind`, not raw strings.

### Example 1 — `root-fallback-gate.yaml`

This is a good small workflow because it exercises: script node, typed route on script output, human gate, terminal emitters, and workflow-level exported output.

```csharp
namespace Polyphony.Workflows;

public sealed record RootFallbackGateInput(int ActiveWorkItemId);

public enum RootFallbackDecision
{
    UseActiveItem,
    Abort,
    AutoResolved
}

public sealed record RootFallbackGateOutput(
    int RootId,
    RootFallbackDecision Decision,
    bool AutoPolicyApplied);

public static class RootFallbackGateWorkflow
{
    public static WorkflowDefinition<RootFallbackGateInput, RootFallbackGateOutput> Build()
    {
        var wf = Workflow.Define<RootFallbackGateInput, RootFallbackGateOutput>(
            name: "root-fallback-gate",
            version: "2.4.8",
            minPolyphonyVersion: "2.4.8",
            tools: [ Tool.Twig ],
            maxIterations: 20);

        var loadPolicy = wf.Script("load_policy")
            .Invoke(Policy.Load)
            .Route(r => r
                .When(x => x.RootFallback.AutoDecide == RootFallbackMode.Prompt, "prompt_user")
                .When(x => x.RootFallback.AutoDecide == RootFallbackMode.UseActiveItem, "use_active_item_auto")
                .When(x => x.RootFallback.AutoDecide == RootFallbackMode.Abort, "abort_auto")
                .Otherwise("prompt_user"));

        wf.Gate<RootFallbackDecision>("prompt_user")
            .Prompt(PromptTemplate.FromMarkdown(
                """
                ## ⚠️ Root work item not provided

                A sub-workflow was invoked without a root work-item id.
                Decide whether to use the active item as root or abort.
                """))
            .Option("✅ Use active item", RootFallbackDecision.UseActiveItem, "use_active_item_prompted")
            .Option("🛑 Abort", RootFallbackDecision.Abort, "abort_prompted");

        var usePrompted = wf.Terminal("use_active_item_prompted")
            .Emit(ctx => new RootFallbackGateOutput(
                RootId: ctx.Input.ActiveWorkItemId,
                Decision: RootFallbackDecision.UseActiveItem,
                AutoPolicyApplied: false));

        var abortPrompted = wf.Terminal("abort_prompted")
            .Emit(_ => new RootFallbackGateOutput(
                RootId: 0,
                Decision: RootFallbackDecision.Abort,
                AutoPolicyApplied: false));

        var useAuto = wf.Terminal("use_active_item_auto")
            .Emit(ctx => new RootFallbackGateOutput(
                RootId: ctx.Input.ActiveWorkItemId,
                Decision: RootFallbackDecision.AutoResolved,
                AutoPolicyApplied: true));

        var abortAuto = wf.Terminal("abort_auto")
            .Emit(_ => new RootFallbackGateOutput(
                RootId: 0,
                Decision: RootFallbackDecision.Abort,
                AutoPolicyApplied: true));

        return wf.Export(e => e.OneOf(usePrompted, useAuto, abortPrompted, abortAuto));
    }
}
```

Why this matters: the current YAML’s `is defined` gymnastics become an `OneOf(...)` export over mutually-exclusive terminal nodes. That is exactly the same runtime behavior, but authoring is typed.

### Example 2 — `feature-pr.yaml`

This one is representative because it covers: platform router, typed subworkflow calls, counter loop, agent prompt context, and the ADO dual-poster pattern.

```csharp
public enum PrPlatform { Github, Ado }

public sealed record FeaturePrInput(
    int WorkItemId,
    string FeatureBranch,
    string TargetBranch,
    PrPlatform Platform,
    string Organization,
    string Project,
    string Repository,
    string PlanPath);

public sealed record FeaturePrOutput(bool Merged, string PrUrl, int PrNumber);

public sealed record PlatformRoute(PrPlatform Platform, bool AdoInputsReady, string Missing);
public sealed record RemediationCounterState(int Iteration, int MaxCycles, bool UnderLimit);
public sealed record FeaturePrUpdaterPromptContext(
    int WorkItemId,
    PrPlatform Platform,
    int PrNumber,
    string FeatureBranch,
    string TargetBranch,
    int RemediationCycle,
    string AddendumPlanPath);

public sealed record FeaturePrUpdaterOutput(
    bool Posted,
    int PrNumber,
    bool ReviewRequested,
    int RemediationCycle,
    string CommentBody);

public static class FeaturePrWorkflow
{
    public static WorkflowDefinition<FeaturePrInput, FeaturePrOutput> Build()
    {
        var wf = Workflow.Define<FeaturePrInput, FeaturePrOutput>(
            name: "feature-pr",
            version: "2.4.8",
            minPolyphonyVersion: "2.4.8",
            tools: [ Tool.Twig, Tool.Gh, Tool.Filesystem ],
            maxIterations: 200);

        var platformRouter = wf.Script("pr_platform_router")
            .Emit<PlatformRoute>(ctx => new(
                Platform: ctx.Input.Platform,
                AdoInputsReady:
                    !string.IsNullOrWhiteSpace(ctx.Input.Organization) &&
                    !string.IsNullOrWhiteSpace(ctx.Input.Project) &&
                    !string.IsNullOrWhiteSpace(ctx.Input.Repository),
                Missing: MissingFields(ctx.Input)))
            .Route(r => r
                .When(x => x.Platform == PrPlatform.Github, "feature_pr_creator_github")
                .When(x => x.Platform == PrPlatform.Ado && x.AdoInputsReady, "feature_pr_creator_ado")
                .When(x => x.Platform == PrPlatform.Ado && !x.AdoInputsReady, "feature_pr_inputs_missing_gate")
                .Otherwise(Workflow.End));

        var createGithub = wf.Script("feature_pr_creator_github")
            .Invoke(Pr.CreateFeaturePr, args => args
                .Set(x => x.WorkItem, wf.In(i => i.WorkItemId))
                .Set(x => x.FeatureBranch, wf.In(i => i.FeatureBranch))
                .Set(x => x.TargetBranch, wf.In(i => i.TargetBranch)))
            .RouteTo("pr_lifecycle_github");

        var createAdo = wf.Script("feature_pr_creator_ado")
            .Invoke(Pr.CreateFeatureAdo, args => args
                .Set(x => x.Organization, wf.In(i => i.Organization))
                .Set(x => x.Project, wf.In(i => i.Project))
                .Set(x => x.Repository, wf.In(i => i.Repository))
                .Set(x => x.RootId, wf.In(i => i.WorkItemId))
                .Set(x => x.TargetBranch, wf.In(i => i.TargetBranch)))
            .Route(r => r
                .When(x => !string.IsNullOrEmpty(x.ErrorCode), "feature_pr_creator_failed_gate_ado")
                .Otherwise("pr_lifecycle_ado"));

        var githubLifecycle = wf.Call("pr_lifecycle_github", GithubPr, map => map
                .Required(x => x.PrNumber, createGithub.Out(x => x.PrNumber))
                .Required(x => x.BranchName, wf.In(x => x.FeatureBranch))
                .Required(x => x.TargetBranch, wf.In(x => x.TargetBranch))
                .Required(x => x.WorkItemId, wf.In(x => x.WorkItemId))
                .Const(x => x.ReviewPolicy, ReviewPolicy.Auto))
            .Route(r => r
                .When(x => x.Merged, Workflow.End)
                .Otherwise("pr_remediation_policy"));

        var adoLifecycle = wf.Call("pr_lifecycle_ado", AdoPr, map => map
                .Required(x => x.PrNumber, createAdo.Out(x => x.PrNumber))
                .Required(x => x.BranchName, wf.In(x => x.FeatureBranch))
                .Required(x => x.TargetBranch, wf.In(x => x.TargetBranch))
                .Required(x => x.WorkItemId, wf.In(x => x.WorkItemId))
                .Required(x => x.Organization, wf.In(x => x.Organization))
                .Required(x => x.Project, wf.In(x => x.Project))
                .Required(x => x.Repository, wf.In(x => x.Repository))
                .Required(x => x.RootId, wf.In(x => x.WorkItemId))
                .Const(x => x.ReviewPolicy, ReviewPolicy.Manual))
            .Route(r => r
                .When(x => x.Merged, Workflow.End)
                .Otherwise("pr_remediation_policy"));

        var remediationCounter = wf.Counter<RemediationCounterState>("remediation_counter")
            .PersistedAs(ctx => $"conductor-remediation-count-{ctx.Input.WorkItemId}")
            .Increment(startAt: 0)
            .CapFromPolicy("pr.max_remediation_cycles", fallback: 3)
            .Route(r => r
                .When(x => x.UnderLimit, "remediation_guidance_loader")
                .Otherwise("remediation_cap_gate_policy_router"));

        var updater = wf.Agent<FeaturePrUpdaterPromptContext, FeaturePrUpdaterOutput>("feature_pr_updater")
            .Model(Model.ClaudeSonnet46)
            .Tools(Tool.Gh, Tool.Filesystem)
            .Prompt(FeaturePrPrompts.Updater, ctx => new FeaturePrUpdaterPromptContext(
                WorkItemId: ctx.Input.WorkItemId,
                Platform: ctx.Input.Platform,
                PrNumber: ctx.Coalesce(createAdo.Maybe(x => x.PrNumber), createGithub.Out(x => x.PrNumber)),
                FeatureBranch: ctx.Input.FeatureBranch,
                TargetBranch: ctx.Input.TargetBranch,
                RemediationCycle: remediationCounter.Out(x => x.Iteration),
                AddendumPlanPath: ctx.Ref("remediation_planner", x => x.AddendumPlanPath)))
            .Route(r => r
                .When(_ => wf.In(x => x.Platform) == PrPlatform.Ado, "feature_pr_updater_poster_ado")
                .Otherwise("pr_platform_router"));

        wf.Script("feature_pr_updater_poster_ado")
            .Invoke(Pr.PostCommentAdo, args => args
                .Set(x => x.Organization, wf.In(i => i.Organization))
                .Set(x => x.Project, wf.In(i => i.Project))
                .Set(x => x.Repository, wf.In(i => i.Repository))
                .Set(x => x.PrNumber, createAdo.Out(i => i.PrNumber))
                .Set(x => x.Body, updater.Out(i => i.CommentBody)))
            .RouteTo("pr_platform_router");

        return wf.Export(e => new FeaturePrOutput(
            Merged: e.Coalesce(
                githubLifecycle.Maybe(x => x.Merged),
                adoLifecycle.Maybe(x => x.Merged),
                e.Const(false)),
            PrUrl: e.Coalesce(
                createAdo.Maybe(x => x.PrUrl),
                createGithub.Maybe(x => x.PrUrl),
                e.Const(string.Empty)),
            PrNumber: e.Coalesce(
                createAdo.Maybe(x => x.PrNumber),
                createGithub.Maybe(x => x.PrNumber),
                e.Const(0))));
    }
}
```

This shows the main win: platform routing, child-workflow invocation, counters, prompt contexts, and poster scripts all become one typed graph. The compiler lowers that to the same YAML shape the current workflow uses.

### Example 3 — `implement-merge-group.yaml`

This is the workflow that best demonstrates typed verb invocation and typed route selection over a discriminated result.

```csharp
public sealed record ImplementMergeGroupInput(
    int WorkItemId,
    int RootId,
    int PgNumber,
    string MgPath,
    ImmutableArray<int> WorkItemIds,
    string FeatureBranch,
    PrPlatform Platform,
    string Organization,
    string Project,
    string Repository);

public sealed record ImplementMergeGroupOutput(bool Merged, string PrUrl, int PrNumber, string MgPath);
public enum ReviewVerdict { Approved, ChangesRequested }
public sealed record ScopeReviewOutput(ReviewVerdict Verdict, string Feedback, ImmutableArray<string> Issues);
public sealed record ScopeReviseCounterState(int Iteration, int MaxRevisions, bool CapReached);

public static class ImplementMergeGroupWorkflow
{
    public static WorkflowDefinition<ImplementMergeGroupInput, ImplementMergeGroupOutput> Build()
    {
        var wf = Workflow.Define<ImplementMergeGroupInput, ImplementMergeGroupOutput>(
            name: "implement-merge-group",
            version: "2.4.8",
            minPolyphonyVersion: "2.4.8",
            tools: [ Tool.Twig, Tool.Gh, Tool.Filesystem ],
            maxIterations: 300);

        var ensureMg = wf.Script("branch_ensure_mg")
            .Invoke(Branch.EnsureMg, args => args
                .Set(x => x.RootId, wf.In(i => i.RootId))
                .Set(x => x.MgPath, wf.In(i => i.MgPath)))
            .RouteTo("root_router");

        var rootRouter = wf.Script("root_router")
            .Invoke(Branch.NextImpl, args => args
                .Set(x => x.WorkItem, wf.In(i => i.WorkItemId))
                .Set(x => x.PgNumber, wf.In(i => i.PgNumber))
                .Set(x => x.MgPath, wf.In(i => i.MgPath)))
            .Route(r => r
                .When(x => x.ActionKind == BranchNextImplAction.ImplementItem, "impl_branch_ensure")
                .When(x => x.ActionKind == BranchNextImplAction.AllItemsDone, "dependency_check")
                .When(x => x.ActionKind == BranchNextImplAction.Error || !string.IsNullOrEmpty(x.Error), "root_router_error_gate")
                .Otherwise("dependency_check"));

        var scopeReviewer = wf.Agent<ScopeReviewPromptContext, ScopeReviewOutput>("scope_reviewer")
            .Model(Model.ClaudeOpus47)
            .Tools(Tool.Twig, Tool.Filesystem)
            .Prompt(ImplementMergeGroupPrompts.ScopeReviewer, ctx => new ScopeReviewPromptContext(
                MgPath: ctx.Input.MgPath,
                MgBranch: $"mg/{ctx.Input.RootId}_{ctx.Input.MgPath}",
                ParentWorkItemId: ctx.Input.WorkItemId,
                TaskIds: ctx.Input.WorkItemIds,
                TriageDisposition: ctx.Ref("scope_empty_mg_triage", x => x.Disposition),
                TriageDetail: ctx.Ref("scope_empty_mg_triage", x => x.Detail)))
            .Route(r => r
                .When(x => x.Verdict == ReviewVerdict.Approved, "pr_platform_router")
                .Otherwise("scope_approvals_policy"));

        var scopeReviseCounter = wf.Counter<ScopeReviseCounterState>("scope_revise_counter")
            .PersistedAs(ctx => $"conductor-mg-scope-revise-{ctx.Input.WorkItemId}-{ctx.Input.MgPath}")
            .Increment(startAt: 0)
            .CapFromPolicy("approvals.max_revision_cycles", fallback: 3)
            .Route(r => r
                .When(x => x.CapReached, "scope_revise_cap_gate_policy_router")
                .Otherwise("clear_root_impl_marker"));

        var clearRootImplMarker = wf.Script("clear_root_impl_marker")
            .Invoke(Branch.ClearImplMerged, args => args
                .Set(x => x.WorkItem, wf.In(i => i.RootId))
                .Set(x => x.MgPath, wf.In(i => i.MgPath)))
            .RouteTo("root_router");

        var prPlatformRouter = wf.Script("pr_platform_router")
            .Emit<PlatformRoute>(ctx => new(
                ctx.Input.Platform,
                !string.IsNullOrWhiteSpace(ctx.Input.Organization) &&
                !string.IsNullOrWhiteSpace(ctx.Input.Project) &&
                !string.IsNullOrWhiteSpace(ctx.Input.Repository),
                MissingFields(ctx.Input)))
            .Route(r => r
                .When(x => x.Platform == PrPlatform.Github, "mg_pr_open")
                .When(x => x.Platform == PrPlatform.Ado && x.AdoInputsReady, "mg_pr_open_ado")
                .When(x => x.Platform == PrPlatform.Ado && !x.AdoInputsReady, "mg_pr_inputs_missing_gate")
                .Otherwise(Workflow.End));

        var openMgAdo = wf.Script("mg_pr_open_ado")
            .Invoke(Pr.OpenMergeGroupAdo, args => args
                .Set(x => x.Organization, wf.In(i => i.Organization))
                .Set(x => x.Project, wf.In(i => i.Project))
                .Set(x => x.Repository, wf.In(i => i.Repository))
                .Set(x => x.RootId, wf.In(i => i.RootId))
                .Set(x => x.MgPath, wf.In(i => i.MgPath)))
            .Route(r => r
                .When(x => !string.IsNullOrEmpty(x.ErrorCode), "mg_pr_failed_gate_ado")
                .Otherwise("mg_pr_merge_ado"));

        var mergeMgAdo = wf.Script("mg_pr_merge_ado")
            .Invoke(Pr.MergeMergeGroupAdo, args => args
                .Set(x => x.Organization, wf.In(i => i.Organization))
                .Set(x => x.Project, wf.In(i => i.Project))
                .Set(x => x.Repository, wf.In(i => i.Repository))
                .Set(x => x.RootId, wf.In(i => i.RootId))
                .Set(x => x.MgPath, wf.In(i => i.MgPath)))
            .Route(r => r
                .When(x => !string.IsNullOrEmpty(x.ErrorCode), "mg_pr_failed_gate_ado")
                .When(x => x.Merged, "scope_closer")
                .Otherwise("mg_pr_failed_gate_ado"));

        return wf.Export(e => new ImplementMergeGroupOutput(
            Merged: e.IsSuccessful("scope_closer"),
            PrUrl: e.Coalesce(openMgAdo.Maybe(x => x.PrUrl), e.Ref("mg_pr_open", x => x.PrUrl), e.Const(string.Empty)),
            PrNumber: e.Coalesce(openMgAdo.Maybe(x => x.PrNumber), e.Ref("mg_pr_open", x => x.PrNumber), e.Const(0)),
            MgPath: wf.In(x => x.MgPath)));
    }
}
```

The important part is `root_router`: today it routes on a string field named `action`. In a compiler model, that becomes a typed discriminant. That single move eliminates a large class of misroutes.

---

## 1. What’s in a conductor workflow YAML, and what is the C# expression for each piece?

**Observed source of truth:** the local repo has many live workflow examples and no discoverable `workflow-schema.json`. So the practical schema is “what the engine accepts” as evidenced by the YAML corpus plus conductor mechanics docs. Also notable: I found **no live uses** of `env:`, `output_map:`, `retry:`, or `on_error:` in this repo’s workflow YAMLs. The C# surface should leave room for them, but they are not part of the current polyphony authoring core.

### Node kinds and corresponding C# forms

| YAML concept | Evidence in repo | Proposed C# expression |
|---|---|---|
| Workflow header (`name`, `version`, `entry_point`, `metadata`, `limits`) | every workflow | `Workflow.Define<TIn,TOut>(...).Version(...).MinPolyphonyVersion(...).MaxIterations(...)` |
| Workflow input schema | every workflow | C# input record `record FeaturePrInput(...)` |
| Workflow output map | `feature-pr`, `implement-merge-group`, `root-fallback-gate` | output record + `wf.Export(...)` |
| Script node | dominant pattern | `wf.Script("name").Invoke(descriptor, binder => ...)` or `.Emit<T>(ctx => ...)` |
| LLM/agent node | `coder`, `root_reviewer`, `remediation_planner`, `feature_pr_updater` | `wf.Agent<TPromptContext,TOutput>(...)` |
| Human gate | many workflows | `wf.Gate<TSelection>(...)` with `.Option(label, value, route)` |
| Subworkflow call | `feature-pr -> github-pr/ado-pr`, `root-item-dispatch -> implement-merge-group` | `wf.Call("name", ChildWorkflowDescriptor, map => ...)` |
| `for_each` group | `polyphony`, `root-batch-dispatch`, `restack-remedy`, `plan-level` | `wf.ForEach<TItem,TChildOut>(...)` |
| `parallel` group | described by conductor mechanics, sparse in current workflow set | `wf.Parallel(...)` |
| Terminal no-op script emitting JSON | `root-fallback-gate`, abort/auto terminals | `wf.Terminal("name").Emit(ctx => new TOutput(...))` |
| Counter scripts | `feature-pr`, `implement-merge-group`, `github-pr` patterns | `wf.Counter<TCounterState>(...)` as a first-class DSL primitive |

### Routing primitives and corresponding C# forms

| YAML concept | Proposed C# form |
|---|---|
| `routes:` | `.Route(r => ...)` |
| `when:` condition | ordinary lambda over typed output: `.When(x => x.Merged, next)` |
| catch-all route | `.Otherwise(next)` |
| gate option route | `.Option(label, enumValue, routeName)` |
| `failure_mode` on `for_each` / `parallel` | `.FailureMode(FailureMode.ContinueOnError)` |
| `max_concurrent` | `.MaxConcurrent(3)` |
| `source:` for `for_each` | `.Source(parent.Out(x => x.Waves))` or `.Source(wf.In(x => x.BatchItems))` |
| reserved iteration variable via `as:` | hidden behind typed lambda variable: `.ForEach(batch => ...)` |

The core design move is: **route conditions stop being strings**. The compiler lowers lambdas into conductor-compatible `when:` Jinja only after type-checking.

### Data primitives and corresponding C# forms

| YAML concept | Proposed C# form |
|---|---|
| `workflow.input.foo` | `wf.In(x => x.Foo)` |
| `node.output.bar` | `node.Out(x => x.Bar)` |
| mutually-exclusive node output | `node.Maybe(x => x.Bar)` or `OptionalRef<T>` |
| child `input_mapping` | `map.Required(x => x.PrNumber, parent.Out(x => x.PrNumber))` |
| agent `output:` schema | output record type `TOutput` |
| prompt template inputs | prompt-context record `TPromptContext` plus `.Prompt(template, binder)` |
| workflow-scope output | output record + `wf.Export(...)` |
| tools list | `.Tools(Tool.Twig, Tool.Gh, Tool.Filesystem)` |
| env vars | optional `.Environment(e => e.Set("NAME", someRef))` on external-process nodes |

Two special cases matter:

1. **Optional references.** Current YAML often uses `is defined` because only one branch executes. In C#, that becomes `OptionalRef<T>` and forces the exporter to acknowledge branch exclusivity explicitly.
2. **Counters.** If counters remain arbitrary inline PowerShell, the type system stops at the edge. A compiler should make counters a first-class node shape even if it lowers them to scripts initially.

### Why builder over attributes or raw object initializers?

- Attributes are good for **leaf metadata** (`[Command]`, `[VerbResult]`, `[MutatesResource]`), but workflow graphs are not leaf metadata.
- Raw immutable records are possible internally, but authoring them directly is painful for long route chains.
- A fluent builder is the right authoring surface because it keeps **graph shape, route structure, and input bindings adjacent** while still compiling to a plain immutable IR.

So the recommended split is:

- **records/classes** for IR + contracts;
- **generated descriptors** for verbs/workflows;
- **fluent builder** for authors.

---

## 2. What is the surface area of a C#-declared workflow?

The authoring surface needs to cover four things simultaneously:

1. **workflow contracts** (`TInput`, `TOutput`),
2. **step contracts** (verb result type, agent output type, gate selection type),
3. **prompt contexts** (typed input to markdown prompts), and
4. **resource contracts** (what the workflow may mutate).

A minimal but complete surface looks like this:

```csharp
Workflow.Define<TWorkflowInput, TWorkflowOutput>(...)
    .Tools(...)
    .Script("name").Invoke(VerbDescriptor<TArgs,TResult>, binder => ...)
    .Script("name").Emit<TOutput>(ctx => new TOutput(...))
    .Agent<TPromptContext, TAgentOutput>("name").Prompt(template, binder)
    .Gate<TSelection>("name").Option(...)
    .Call("name", WorkflowDescriptor<TChildIn,TChildOut>, map => ...)
    .ForEach<TItem, TChildOut>("name", source => ...)
    .Counter<TCounterState>("name", spec => ...)
    .Export(...)
    .Resources(...);
```

### Type-safe verb references

This is the keystone. A workflow author should never write:

```yaml
command: polyphony
args: ["branch", "next-impl", ...]
```

They should reference a generated descriptor whose type came from the actual command method:

```csharp
rootRouter.Invoke(PolyphonyVerbs.Branch.NextImpl, args => args
    .Set(x => x.WorkItem, wf.In(i => i.WorkItemId))
    .Set(x => x.PgNumber, wf.In(i => i.PgNumber))
    .Set(x => x.MgPath, wf.In(i => i.MgPath)));
```

The descriptor would be generated from the existing `[Command]` + `[VerbResult]` + `PolyphonyJsonContext` shape. That means the workflow compiler is not inventing a second truth about verbs; it is consuming the one polyphony already has.

### Type-safe route references

Routes should reference one of three shapes:

1. **ordinary scalar fields** — `x.Merged == true`
2. **generated enums** — `x.ActionKind == BranchNextImplAction.Error`
3. **generated unions** — `x is BranchNextImplOutcome.Error`

For polyphony’s current DTOs, I would start with **generated enum overlays** over string discriminants because they are cheap and compatible. Later, the compiler can grow true discriminated-union projections where worth it.

---

## 3. Compile-time guarantees we can win

### 3.1 “This verb’s payload schema matches what the agent emits”

Yes, **if the binding passes through typed records on both sides**.

Example:

- `feature_pr_updater` emits `FeaturePrUpdaterOutput.CommentBody : string`
- `pr post-comment-ado` takes `PrPostCommentAdoArgs.Body : string`
- binder does `.Set(x => x.Body, updater.Out(x => x.CommentBody))`

If the agent output were renamed, or if the type changed to `string[]`, the compiler/analyzer catches it.

### 3.2 “This route’s when condition references a field that the previous node actually emits”

Yes, strongly.

This is nearly the biggest win. `root_router.output.action_typo` becomes impossible because the route is a lambda over `BranchNextImplResult` or its generated overlay. The only escape hatch would be raw string expressions, which should be explicitly marked as such and discouraged.

### 3.3 “This counter increments and bounds-checks consistently”

Yes, **but only if counters stop being handwritten scripts**.

For the workflows I read, the risky counter logic is not just persistence; it is the repeated, hand-coded agreement between:

- file key,
- increment rule,
- cap source,
- comparison operator (`<` vs `>=`), and
- reset behavior.

A `CounterNode<TState>` can centralize that. Then the compiler/analyzer can prove:

- increment happens exactly once per step execution,
- cap source is defined,
- reset goes back to zero or configured baseline,
- routes use `UnderLimit` / `CapReached` rather than ad hoc string fields.

### 3.4 “This subworkflow’s inputs are satisfied at every call site”

Yes.

Given `WorkflowDescriptor<TChildIn,TChildOut>`, a call site can require every non-optional child input to be mapped. This is much better than the current failure mode where a nested workflow later blows up on missing `workflow.input.work_item_id`.

### 3.5 “Every node’s outputs are consumed by at least one downstream consumer”

Mostly yes, but this should be a **warning-level analyzer**, not always an error.

There are legitimate cases where outputs exist mainly for observability or exported workflow output. But in practice, a lot of dead fields are accidental drift. The analyzer can report:

- step output field never read,
- entire step unreachable,
- exported output field never consumed by any parent workflow in repo,
- optional branch output declared but impossible to reach.

### 3.6 What we cannot honestly win at compile time

We should be explicit about the limits:

- **Prompt truthfulness.** The agent can still hallucinate while satisfying the JSON schema.
- **Inline/raw PowerShell bodies.** If we permit arbitrary script text, the compiler cannot verify the emitted JSON shape beyond declared wrappers.
- **Runtime set membership.** A `for_each` source may be typed as `IReadOnlyList<Wave>` but empty, stale, or semantically wrong at runtime.
- **ADO/process legality.** A type-safe `twig state` target can still be rejected by the live process template or remote service.
- **Resource identity precision for some verbs.** `[MutatesResource]` can say “this verb may touch an ADO work-item state,” but a compiler cannot always know *which* work item without understanding runtime dataflow.
- **Human decisions.** A gate option is typed, but the human can still choose the destructive path.

The right mindset is: **make structural mistakes impossible; make semantic mistakes obvious; do not pretend the type system can prove the world.**

---

## 4. Source generators vs. reflection vs. reverse generation vs. analyzers

### Option A — source generators emit YAML from C# declarations

**Pros**

- Best fit for polyphony’s existing prior art (`VerbOutputSchemaCatalog`).
- AOT-safe: no runtime reflection requirement.
- Emits deterministic artifacts that can be diffed, linted, and harness-tested.
- Lets analyzers share the same IR and descriptors.
- Keeps conductor as-is: runtime still consumes YAML.

**Cons**

- Requires a serious migration from handwritten YAML to C# declarations.
- Build gets more coupled to workflow authoring.
- Generated YAML drift policy must be decided (checked in vs artifact only).

### Option B — source generators emit C# from existing YAML

**Pros**

- Excellent migration/bootstrap tool.
- Lowers the first adoption step: keep YAML truth, gain typed read-only view.
- Useful for diffing or analyzers against the existing corpus.

**Cons**

- Does not actually unify authoring; YAML remains the source of truth.
- Many invariants remain post-hoc rather than proactive.
- Stringly routes and script args still dominate the authored form.

This is valuable as a **migration aid**, not as the primary architecture.

### Option C — Roslyn analyzers only, no code generation

**Pros**

- Great edit-time diagnostics.
- Smaller blast radius.
- No runtime changes.

**Cons**

- Still leaves YAML, PowerShell JSON emitters, prompt contexts, and workflow outputs fragmented.
- Does not produce typed descriptors or compiled artifacts.
- Still requires authors to work in the stringly surface.

This is necessary, but not sufficient.

### Option D — runtime registry / reflection / object-graph execution

**Pros**

- Feels closest to the “JIT” idea.
- Could support dynamic composition without emitting intermediate YAML.
- Potentially simpler mental model if Conductor were replaced.

**Cons**

- Worst fit for .NET 9 trim/AOT constraints.
- Reflection-heavy registry discovery is exactly the kind of surface that gets brittle under AOT.
- Failures move later, from edit/build time to runtime.
- Harder to diff, review, and test against the current conductor harness.
- Conductor still exists and expects YAML; this adds a second execution model.

I would avoid this as the primary path.

### Recommended primary approach

**Primary recommendation:** author workflows in a **typed fluent C# DSL**, then use **source generators + analyzers** to emit deterministic YAML, contract catalogs, prompt bindings, and resource metadata at build time.

That is the best fit because it:

- extends an existing pattern already shipping in polyphony,
- stays trim/AOT-safe,
- keeps conductor as the runtime executor,
- maximizes compile-time guarantees,
- gives reviewers concrete generated YAML to argue over,
- and does not require a wholesale orchestration-engine rewrite.

---

## 5. The compiler’s outputs

The compiler should emit more than YAML.

### 5.1 Runtime artifacts

1. **Workflow YAML**
   - consumer: conductor runtime, harness scenarios
   - location: keep emitting into `.conductor/registry/workflows/*.yaml` or a generated sibling path that conductor already reads

2. **Registry index**
   - consumer: workflow discovery/versioning
   - location: `.conductor/registry/index.yaml`
   - compiler owns path, versions, and descriptions derived from C# declarations

3. **Prompt snapshots (Markdown)**
   - consumer: reviewers, harness/debugging, prompt diffing
   - location: e.g. `.conductor/registry/prompts/<workflow>/<node>.md`
   - source of truth remains C# prompt templates + typed context records; snapshots are derived, reviewable artifacts

### 5.2 Build/test artifacts

4. **Unified contract catalog**
   - consumer: lints, IDE analyzers, external tooling
   - location: `artifacts/contract-schemas.json`
   - generalizes `VerbOutputSchemaCatalog`

5. **Per-contract JSON schema docs**
   - consumer: humans, external tools, script wrappers
   - location: `artifacts/schemas/*.json`

6. **Workflow resource catalog**
   - consumer: effect-budget analyzer, reset/drift tooling, synthesis work
   - location: `artifacts/workflow-resource-contracts.json`
   - records declared owned/touched resource kinds, patterns, and producing steps

7. **Source-to-generated map**
   - consumer: diagnostics, debugging, IDE “go to generated YAML” support
   - location: `artifacts/workflow-compiler-map.json`

### 5.3 Generated C# surfaces

8. **Typed verb descriptors**
   - consumer: workflow declarations
   - location: `Polyphony.Generated.PolyphonyVerbs.g.cs`
   - generated from existing command methods, not hand-maintained

9. **Typed child-workflow descriptors**
   - consumer: parent workflow declarations
   - location: `Polyphony.Generated.WorkflowCatalog.g.cs`

10. **Discriminant overlays / enum codecs**
   - consumer: route lambdas and analyzers
   - location: generated partials over existing DTOs

I would **not** emit a separate runtime “typed client for each verb” beyond the generated descriptor surface unless another caller actually needs it. The workflow compiler needs descriptors, not a second command API.

---

## 6. Forward-compat with the journal effect model

D13 is unusually helpful here because it separates:

- **static capability** — `[MutatesResource]` / `[MayObserveResource]`
- **runtime fact** — `JournalResourceEffect[]`

That is exactly what a compiler needs.

### Proposed workflow-level resource declaration

```csharp
wf.Resources(r => r
    .Owns(ResourcePattern.GitBranch("mg/{root_id}_{mg_path}"))
    .Owns(ResourcePattern.PullRequest("mg/{root_id}_{mg_path}"))
    .Touches(ResourceScope.AdoWorkItems.DescendantsOf(wf.In(x => x.WorkItemId)))
    .Touches(ResourcePattern.ManifestEntry("root:{root_id}")));
```

### Proposed validation model

At compile time, the analyzer can check three things:

1. **Kind subset**
   - if a step invokes a verb whose descriptor says `[MutatesResource("git_branch")]`, the workflow must allow `git_branch` somewhere in its declared budget.

2. **Pattern compatibility**
   - if the workflow says it owns `GitBranch("evidence/*")`, then a step invoking `Branch.EnsureMg` should fail because that verb’s branch id template resolves to `mg/{root}_{mgPath}`, not `evidence/...`.

3. **ownership discipline**
   - steps that may create resources outside the workflow’s owned set are rejected unless they are explicitly marked as `TouchesExternal(...)`.

### Concrete example

Suppose a workflow declares:

```csharp
wf.Resources(r => r
    .Owns(ResourcePattern.GitBranch("evidence/{root_id}-{work_item_id}"))
    .Owns(ResourcePattern.PullRequest("evidence/{root_id}-{work_item_id}")));
```

Then these steps are legal:

- `Pr.OpenEvidencePr`
- `Branch.EnsureEvidenceBranch`

These steps are illegal in that workflow:

- `Branch.EnsureMg`
- `Pr.MergeMergeGroupAdo`
- `Branch.EnsureFeature`

because their static capability or derived resource id does not fit the workflow’s resource budget.

### Why both static and runtime matter

- **Compiler phase:** “could this workflow ever mutate something outside its declared lane?”
- **Journal phase:** “what did this specific run actually mutate?”

That is the right division of labor. The compiler uses the attribute layer; drift/reset keeps using `JournalResourceEffect[]`.

---

## 7. What does “JIT generation” mean in practice?

There are three possible meanings.

### 7.1 Build-time emission of static YAML

This is the safest and should be the default.

- edit C#
- build emits YAML
- tests/harness/lints run against YAML
- runtime consumes emitted YAML directly

This is not “JIT” in the romantic sense, but it is the best default for polyphony.

### 7.2 Runtime emission per invocation

This means the CLI lowers a workflow declaration to YAML right before calling conductor.

Useful only when the **graph itself** varies by runtime conditions that are not cleanly modeled as ordinary workflow input, such as:

- compile-time disabled experimental steps,
- runtime-selected provider/platform specializations that materially change node graph,
- repo-local profile expansion that should not be committed.

But it is easy to overuse. Generating one bespoke workflow per work item or MG is mostly unnecessary because conductor already has input mapping and templates.

### 7.3 Hybrid

This is what I recommend:

- **canonical path:** build emits stable YAML artifacts
- **runtime fallback:** if emitted YAML is missing/stale, the CLI can regenerate into a cache
- **specialization path:** only a small number of declarations may opt into runtime lowering when the graph truly depends on installation-time facts

### Latency and caching story

If runtime generation exists at all, cache it by:

- workflow declaration hash,
- compiler version,
- polyphony binary version,
- relevant specialization inputs.

Then most runs are cache hits and pay effectively zero compiler cost.

What I would *not* do is compile a fresh per-work-item YAML on every invocation. That is operational churn without much type-system payoff.

---

## 8. Developer experience

### File layout

I would separate authored C# from emitted runtime artifacts:

```text
src/Polyphony.Workflows/
  Contracts/
    FeaturePrContracts.cs
    ImplementMergeGroupContracts.cs
  Definitions/
    FeaturePrWorkflow.cs
    ImplementMergeGroupWorkflow.cs
    RootFallbackGateWorkflow.cs
  Prompts/
    FeaturePrPrompts.cs
    ImplementMergeGroupPrompts.cs
  Resources/
    WorkflowResourceBudgets.cs

src/Polyphony.Generated/
  PolyphonyVerbs.g.cs
  WorkflowCatalog.g.cs
  WorkflowContracts.g.cs
  WorkflowLowering.g.cs

.conductor/registry/
  workflows/*.yaml          # generated runtime artifacts
  index.yaml                # generated registry
  prompts/...               # optional generated snapshots
```

This respects the existing conductor/runtime placement while moving authorship into C#.

### Edit/build/test loop

1. Author edits a workflow definition or prompt context record.
2. IDE analyzers flag bad routes, missing child input mappings, unused outputs, or resource-budget violations immediately.
3. Build emits updated YAML/catalogs.
4. Existing workflow lints and harness tests run against emitted YAML.
5. Reviewer can diff both the C# declaration and the generated YAML if desired.

### Validation errors at edit time

The interesting diagnostics are things like:

- `WF0012`: route reads field `feedback_summary` but producer `AdoPrOutput` has no such field
- `WF0021`: child workflow `AdoPr` requires `Repository`; call site did not map it
- `WF0030`: step `mg_pr_open` may mutate `git_branch` outside workflow-owned pattern `evidence/*`
- `WF0044`: counter `scope_revise_counter` is reset nowhere on the `re_loop` path
- `WF0051`: exported workflow output `pr_number` is sourced only from nodes that are unreachable from the entry point

That is where the compiler pays for itself.

### What about authors who do not know C#?

Honest answer: **this is probably a non-goal for first-class authoring.**

A workflow compiler pushes authorship into a developer surface. Given how architectural these workflows already are, that is probably acceptable. The mitigation is not “pretend this stays no-code”; the mitigation is:

- keep generated YAML reviewable,
- keep prompt snapshots readable,
- optionally provide a YAML→C# bootstrap converter for migration,
- and document a small, opinionated DSL instead of a giant API.

But if the question is “can a non-C# workflow author remain a primary editor of these graphs?” the honest answer is: **much less so than today.**

---

## Recommended primary approach + defense

**Recommendation:** adopt a **C# fluent workflow DSL backed by Roslyn source generators and analyzers**, with **build-time emission of conductor YAML as the default runtime artifact**, and use the existing `PolyphonyJsonContext` / `[VerbResult]` / schema-generator pattern as the seed.

### Why this is the right fit for polyphony specifically

1. **It extends a real precedent.** Polyphony already generates a compile-time catalog from C# contracts. This is not a speculative pattern import.
2. **It stays AOT-friendly.** Runtime reflection registries are the wrong trade for a trim-published CLI.
3. **It preserves conductor.** Other investigators can debate long-term orchestration direction; mechanically, this path lets polyphony keep its current executor and harness.
4. **It moves the highest-value mistakes left.** Route typos, missing child inputs, output drift, and effect-budget mismatches become edit/build-time problems.
5. **It keeps artifacts arguable.** Generated YAML remains available for debugging and code review, which matters because the team already reasons about workflows as concrete runtime graphs.

The key design decision inside that recommendation is: **builder for authors, records for contracts, generators for lowering, analyzers for invariants.** Not attributes-only, not reflection-first, not YAML-as-source forever.

---

## Open questions for the synthesis step

1. **How far should the compiler go in normalizing current string discriminants into enums or full discriminated unions?** Enum overlays are cheap; full DU projections are cleaner but more invasive.
2. **Do we commit generated YAML to the repo, or treat it as a build artifact only?** Polyphony’s review culture may benefit from checked-in generated YAML during migration.
3. **Are counters first-class compiler primitives on day one, or do we initially wrap them as generated script nodes?** This strongly affects how many guarantees we really win.
4. **How much runtime specialization is actually needed?** My read is “very little,” but this should be explicit.
5. **Should prompt templates live as C# raw strings, embedded resources, or external `.md` files with generated typed binders?** I lean toward external Markdown + generated typed binders for readability.
6. **How precise should resource-budget analysis be in v1?** Kind-only, kind+pattern, or kind+pattern+ownership semantics?
7. **Is a YAML→C# bootstrap converter worth building to accelerate migration of the existing workflow corpus?** I think yes as a migration tool, not as the primary architecture.
8. **Where is the authoritative conductor workflow schema?** I found no local `workflow-schema.json`; today the practical schema is the live corpus + engine behavior. A compiler effort wants an explicit runtime contract.
