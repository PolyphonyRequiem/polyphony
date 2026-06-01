---
doc_type: discussion
status: exploratory
synopsis: Journal-integrator critique of compiler-lite — verdict NEEDS-EXPLICIT-WIRING; compiler-lite must not claim ownership-closure proofs the journal already owns.
---

# D5 — Journal Integrator Critique

**Verdict: NEEDS-EXPLICIT-WIRING** — compiler-lite is not redundant with the D13 journal effect model, but the synthesis currently treats journal integration as a side note rather than a load-bearing boundary. The right relationship is layered: journal owns verb-level truth and runtime effects; compiler-lite may own workflow-level static shape, route contracts, and kind-level resource capability checks — but only if the deck explicitly forbids compiler-lite from pretending it can prove concrete ownership closure.

---

## Mental model: two contracts at different altitudes

D13 defines the **verb/resource effect contract**. Every journaled verb declares static resource capability with `[MutatesResource(kind)]` and `[MayObserveResource(kind)]`, then emits runtime `JournalResourceEffect[]` containing:

```csharp
Kind, Id, Intent, Mutation, PolyphonyOwned, Platform?, ParentId?, Attributes?
```

That is deliberately result-dependent. `branch next-impl` does not know which work item it will transition until runtime. Branch and PR verbs can distinguish `CreatedNow` from `NoChangedExternalAlreadyPresent`, and the ownership rule is safety-critical: external already-present resources must not become reset targets.

Compiler-lite, as proposed in the synthesis, sits one level higher. It types workflow I/O, agent outputs, route predicates, child workflow call sites, and possibly verb descriptor catalogs. That is **not the same problem** as D13. The synthesis already hints at this when it says D13 can support "kind-level resource subsetting" but cannot prove concrete ownership closure. That distinction should become a first-class section, not an appendix question.

A useful diagram:

```text
C# compiler-lite source / descriptors
        |
        | validates workflow shape:
        | - node output fields
        | - route enum domains
        | - child workflow inputs
        | - verb static capabilities by resource kind
        v
Generated or handwritten workflow YAML
        |
        | invokes actual polyphony verbs
        v
Verb implementation + D13 journal decorator
        |
        | records runtime truth:
        | - concrete resource IDs
        | - intent/mutation
        | - ownership
        | - parent/platform/attributes
        v
Journal entries with JournalResourceEffect[]
        |
        | Phase 4 fold
        v
polyphony journal drift / reset projections
```

The key point: compiler-lite can say, "this workflow may call verbs that mutate `git_branch` and `ado_work_item_state`." The journal says, "this specific run created `feature/100`, reused external branch `plan/100-200`, and changed ADO work item state for `workitem:3179`."

Those are complementary, not interchangeable.

## Awkward question 1: is compiler-lite redundant with journal attributes?

Mostly no — but the synthesis should say why.

D13 already gives every verb a static capability declaration. If compiler-lite only re-describes "this verb mutates GitBranch," then yes, it is wrapper tax. The only defensible compiler-lite value is using those existing attributes as inputs to a **workflow-level analysis**:

| Layer | Owner | Static? | Runtime? | Can prove |
|---|---|---:|---:|---|
| Verb method | D13 attributes | Yes | No | Possible resource kinds |
| Verb invocation | Journal effects | No | Yes | Concrete resource effects |
| Workflow graph | Compiler-lite | Yes | No | Which nodes may call which capability-bearing verbs |
| Drift/reset | Journal fold | No | Yes | Expected/current/resource cleanup projections |

So compiler-lite must not create a second resource declaration regime. It should read the D13 metadata from the actual verb catalog and project it into workflow validation. If a C# workflow descriptor calls `Branch.NextImpl`, the descriptor should inherit `[MutatesResource(AdoWorkItemState)]` and `[MayObserveResource(AdoWorkItem)]` from the verb method. The compiler should not ask the workflow author to redeclare that manually.

Recommended synthesis addition: "Compiler-lite consumes D13 verb capabilities; it does not define a parallel resource-effect language."

## Awkward question 2: should compiler-lite subsume journal authoring?

This is the highest-risk integration temptation. The attractive version is obvious: if workflows become C#, maybe `branch.NextImpl()` can emit both the YAML node and the journal effect declaration. That sounds like one source of truth.

But it collapses two contracts with different churn rates.

D13 is tied to **what verbs actually do**. It belongs near command implementation and payload/result selectors. In the shipped code, effects are selected from real payloads after execution. That matters because ownership and mutation are not knowable from the call site. Whether a branch was created or already existed is runtime information. Whether `next-impl` selected a work item is runtime information. Whether a PR was reused or created is runtime information.

Compiler-lite is tied to **how workflows are authored and validated**. It will churn as conductor YAML shape, route syntax, agent contracts, and source-map conventions evolve.

If compiler-lite subsumes journal authoring, every workflow DSL refactor risks invalidating or obscuring the resource-effect contract. Worse, it incentivizes compile-time "expected effects" that look authoritative but are only aspirational.

Better answer: compiler-lite may generate **capability expectations**, not journal effects. The verb implementation remains the only author of concrete `JournalResourceEffect[]`.

Acceptable:

```text
Workflow calls Branch.NextImpl
Compiler sees: may mutate AdoWorkItemState, may observe AdoWorkItem
Lint allows/flags workflow resource policy
```

Not acceptable:

```text
Workflow call site declares:
Expected effect = SetState(workitem {{ selected_id }})
```

That moves runtime truth into a place that cannot know it.

## Awkward question 3: drift integration is powerful, but only as a three-way comparison

The prompt's strongest "compiler-lite makes drift easier" idea is real, but needs narrowing.

Today Phase 4 drift is specified as journaled effects vs current world state. That is the right core:

```text
Journal expected state ⟷ observed world
```

Compiler-lite could add a third input:

```text
Compiled workflow capability shape
        |
        v
Journal recorded effects ⟷ observed world
```

That enables useful checks:

- Workflow was compiled to call only verbs with `GitBranch` capability, but journal records `AdoWorkItemState`.
- Workflow policy says this path should never invoke reset-like deletion verbs, but journal records `EnsureAbsent`.
- A generated workflow node claims to call `branch.next-impl`, but the journal action name differs.
- A route path expected to be observe-only invokes a mutating verb.

But compiler-lite should not redefine drift as "compiler expected X effects, journal recorded Y." That is too strong. Many valid effects are route-dependent, agent-dependent, human-gate-dependent, and result-dependent. Renegotiation-born side effects are especially dangerous: the compiler can know the possibility of renegotiation, not the concrete resources produced by it.

The synthesis should say: compiler-lite can provide an **expected capability envelope per workflow/run**, and drift can optionally report "journal escaped the compiled envelope." The journal remains authoritative for concrete expected state.

## Awkward question 4: reset integration is the payoff, but not via compiler-owned ownership

Reset is where overclaiming gets dangerous. D13's reset safety comes from `OwnedResources` and `ResetTargets`, derived from runtime effects and the ownership rule. That is exactly where the system prevents deleting branches merely because their names match a pattern.

Compiler-lite could improve reset by providing better **run provenance**:

- workflow source identity
- generated YAML identity/hash
- node identity
- source-map span
- declared workflow version
- maybe "run family" / "renegotiation generation"

That would let reset say: "show me resources created by runs of this workflow version under this root," or "reset resources created after renegotiation generation N." That is valuable.

But compiler-lite should not decide what is safe to delete. Safety belongs to D13's runtime `PolyphonyOwned` projection. Reset can be more surgical because compiler-lite gives better grouping and traceability, not because compiler-lite knows ownership.

The deck should acknowledge this explicitly because reset is the sharper test than drift. If compiler-lite makes reset decisions based on compiled expectations, it becomes a footgun.

## Awkward question 5: two attribute regimes need positioning

Today's resource attributes are verb-level:

```csharp
[MutatesResource(ResourceKind.AdoWorkItemState)]
[MayObserveResource(ResourceKind.AdoWorkItem)]
```

Tomorrow's workflow attributes might be:

```csharp
[Workflow]
[WorkflowStep]
[RouteWhen]
```

These are orthogonal only if the synthesis draws a bright line:

- D13 attributes describe **side-effect capability of executable verbs**.
- Compiler-lite/workflow attributes describe **graph structure and dataflow contracts**.
- Workflow attributes may reference verb descriptors, but must not duplicate verb side-effect declarations.

Without that rule, two bad futures are likely.

First, duplication: workflow descriptors maintain their own `MutatesResource` list, which drifts from the command implementation.

Second, semantic collision: reviewers see two attributes that both seem to answer "what does this do?" and cannot tell which one is authoritative.

The deck already warns against second metadata systems for AB#3255. Apply the same rule here: no second resource-effect metadata system.

## Awkward question 6: versioning collision is real

The journal contract has its own evolution path. Workflow YAML has `min_polyphony_version`. Compiler-lite would add at least one more version axis: DSL/compiler version, emitted artifact schema version, possibly source-map version.

The synthesis should avoid a lockstep version trap. If every DSL bump forces every workflow to rebuild, and every rebuild changes generated YAML hashes or source-map identities, journal/drift tooling could lose continuity. Operators need to answer: "was this resource created by the same workflow shape?" even if the C# DSL package was patched.

Recommended rule:

| Version axis | Owns | Should force workflow rebuild? |
|---|---|---|
| Journal effect contract version | journal storage/projections | Only when effect schema changes |
| Polyphony CLI version | verb behavior/runtime | Sometimes |
| Conductor YAML/min version | execution artifact compatibility | When runtime syntax changes |
| Compiler-lite DSL version | authoring/analyzer surface | Not by itself |
| Source-map format version | debug mapping | Only if map consumer changes |

Compiler-lite should stamp generated artifacts with compiler version, source hash, and journal capability envelope version, but not imply that all of these are the same compatibility boundary.

## Awkward question 7: source maps are not optional if YAML is generated

The synthesis already flags source-map fidelity as an open question. Journal integration makes it more urgent.

Once YAML is generated, a journal entry like `action = branch_next_impl` needs to be traceable to:

- workflow run id
- YAML node id
- generated YAML file/span
- C# source workflow/span
- verb descriptor version
- actual command action name

Otherwise drift reports become less operable. Today an operator can read the handwritten YAML. In a generated world, "node X invoked verb Y" is not enough if node X was machine-produced. The journal should not need compiler expertise, but it should carry enough stable identity to let tooling reverse-map.

Minimum requirement: generated YAML node names must be stable and source-map backed before any compiled workflow is used in serious journal/drift/reset flows.

## Awkward question 8: Phase 3 should not wait

Phase 3 is retrofitting 25–30 more verbs across state, manifest, lock, plan, merge-group, worktree, and reset areas. It should not pause for compiler-lite.

Reason: D13's verb-level pattern is the substrate compiler-lite needs. The current code already places effect selection next to command payload/result knowledge, which is the correct authority boundary. Waiting for compiler-lite would risk moving journal semantics upward prematurely.

The only adjustment I would make is to ensure Phase 3 emits enough static metadata for later compiler consumption:

- every journaled verb gets capability attributes from day one
- action names are stable
- resource kinds are not ad hoc strings outside `ResourceKind`
- effect selectors are test-covered for no-op and external-already-present paths
- reset verbs clearly distinguish `DeletedNow + PolyphonyOwned=true` from cleanup of externally absent resources

Compiler-lite can layer on after that.

## Concrete recommendation for the synthesis deck

Yes, the synthesis should add an explicit journal integration section. I would add it after "What compiler-lite means concretely" or inside Proposed D14.

Suggested bullets:

1. **Compiler-lite consumes D13; it does not replace it.** Verb implementations remain the authority for `[MutatesResource]`, `[MayObserveResource]`, and runtime `JournalResourceEffect[]`.

2. **Compiler-lite may validate workflow-level capability envelopes only.** It can prove kind-level resource compatibility and route/call-site shape; it cannot prove concrete resource identity, mutation outcome, or ownership closure.

3. **Drift remains journal-first.** Phase 4 drift folds runtime effects against observed world state. Compiler-lite may optionally add an "escaped compiled capability envelope" diagnostic, but not redefine expected state.

4. **Reset safety remains D13-owned.** Compiler-lite can improve provenance and grouping for reset, but deletion authority must come from `OwnedResources` / `ResetTargets`, never compiled expectations.

5. **Generated workflows require stable node identity and source maps.** Any journal entry emitted from generated YAML must be traceable back to the workflow source without making operators reverse-engineer compiler output.

## Blocking issues

### Blocking: synthesis under-specifies the compiler-lite / D13 authority boundary

**Impact:** Without an explicit boundary, compiler-lite could accidentally become a second journal metadata system or overclaim ownership guarantees.

**Fix:** Add a D14 clause: "D13 is authoritative for verb effects; compiler-lite consumes D13 metadata for workflow-level lint only."

### Blocking: drift/reset language risks overclaiming compile-time expected effects

**Impact:** Reset safety could be weakened if compiled expectations are treated as deletion authority.

**Fix:** State that drift/reset are journal-first. Compiler-lite may contribute provenance and envelope diagnostics, not concrete ownership.

## Non-blocking issues

### Non-blocking: source-map requirement is too soft

**Impact:** Generated YAML plus journal entries will be harder to debug unless node identity maps back to C# source.

**Fix:** Promote source maps from appendix open question to pilot gate for any generated workflow that invokes journaled verbs.

### Non-blocking: versioning axes are not separated

**Impact:** DSL churn could unnecessarily invalidate workflow artifacts or confuse journal compatibility.

**Fix:** Define separate versions for compiler, generated artifact, conductor min version, and journal effect contract.

## Suggestions

### Suggestion: use Phase 3 as compiler-readiness hardening

Do not block Phase 3, but ensure all newly journaled verbs have stable action names, capability attributes, and no-op/external ownership tests. That gives compiler-lite a clean substrate later.

### Suggestion: add "escaped envelope" as future drift diagnostic

A useful compiler-lite + drift integration is: "this workflow's compiled capability envelope did not include resource kind X, but the journal recorded X." That is a strong anomaly signal without pretending compile-time can know concrete runtime effects.

## Final verdict

**NEEDS-EXPLICIT-WIRING.** Compiler-lite and D13 are complementary as long as they stay at different layers: compiler-lite types workflow shape and consumes verb capability metadata; D13 owns runtime effect truth, ownership, drift, and reset safety. The specific decision Daniel must make is whether compiler-lite is allowed to author or duplicate journal/resource-effect declarations; my recommendation is **no** — it may consume and project them, but not own them.
