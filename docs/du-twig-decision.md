# Decision: DU Adoption in Twig.Domain

**Task:** #2809 — Implement DU refactoring in Twig.Domain or document decision not to adopt  
**Issue:** #2794 — Evaluate and Optionally Adopt DU in Twig  
**Date:** 2026-05-01  
**Decision:** **Recommend adoption — defer implementation to the Twig repository.**

---

## Summary

The DU preview evaluation (Issue #2794) has been completed across four tasks:

1. **StateCategory assessment** (#2807) — Concluded `StateCategory` should remain an enum. Data-free labels with ordinal semantics are not DU candidates. `TreatWarningsAsErrors` already provides compile-time exhaustiveness.
2. **Candidate catalog** (#2808) — Surveyed 7 routing-adjacent result types in Twig.Domain. Recommended **3 for adoption**, deferred 1, skipped 3.
3. **Polyphony DU validation** (PG-2, PG-3) — Successfully implemented 2 DU types (`RoutingDecision` with 8 cases, `TransitionOutcome` with 2 cases) in Polyphony. Validated AOT compatibility, pattern matching exhaustiveness, and test patterns.
4. **This decision** (#2809) — Documents the adoption recommendation and rationale.

## Types Evaluated

### Recommended for Adoption (3)

| Type | Current Form | Proposed DU | Anti-Pattern | Consumer Impact |
|------|-------------|------------|--------------|-----------------|
| `MutationResult` | `sealed record` + `bool IsSuccess` + nullable fields | `MutationOutcome(Succeeded, Failed)` | Bool + nullable permits `IsSuccess=true` with `ErrorMessage` set | 5 files |
| `ParentPropagationResult` | `sealed record` + `ParentPropagationOutcome` enum + 4 nullable fields | `ParentPropagationResult(NotApplicable, NoParent, AlreadyActive, Propagated, Failed)` | Enum + conditional nullable fields; validity depends on enum value | 1 file |
| `TransitionResult` | `record` + `TransitionKind` enum + `bool IsAllowed` | `TransitionResult(Allowed, Denied)` | Redundant bool derivable from enum; permits `Kind=None, IsAllowed=true` | 1 file |

Each candidate exhibits a clear anti-pattern (bool + nullables, enum + conditional fields, or redundant bool) where the type's primary constructor permits impossible states that static factory methods attempt — but do not guarantee — to prevent. DU refactoring makes these impossible states unrepresentable at compile time.

### Deferred (1)

| Type | Reason |
|------|--------|
| `Result` / `Result<T>` | High value but ~40-file blast radius, `readonly record struct` → reference type allocation change, generic DU complexity. Revisit as a dedicated effort when DU generics support matures. |

### Skipped (3)

| Type | Reason |
|------|--------|
| `DescendantVerificationResult` | Weak invariant (empty vs. non-empty list), shared fields across variants, JSON contract impact |
| `SeedPublishResult` | Data-rich type with many shared fields, 5 enum variants, wide consumer surface, JSON contract |
| `SeedValidationResult` | Aggregator with computed property, not a variant type — no impossible states exist |

## Why Not Implementing Here

The 3 recommended types (`MutationResult`, `ParentPropagationResult`, `TransitionResult`) reside in the **Twig** codebase (`Twig.Domain`), not in this Polyphony repository. Implementation requires modifying source files, updating consumers, and running tests in the Twig project. This work cannot be performed as a cross-repository change from Polyphony.

## Existing DU Coverage

The DU pattern has been validated in Polyphony with **2 production DU types** introduced during this evaluation:

| # | Type | File | Cases | Introduced |
|---|------|------|-------|------------|
| 1 | `RoutingDecision` | `Routing/RoutingDecision.cs` | 8 (NeedsPlanning, NeedsSeeding, ReadyForImplementation, ImplementationInProgress, ReadyForCompletion, RoutingDone, RoutingRemoved, RoutingUnknown) | PG-2 |
| 2 | `TransitionOutcome` | `Routing/TransitionOutcome.cs` | 2 (ValidTransition, InvalidTransition) | PG-3 |

The Twig codebase additionally has **6 existing DU types** (per the catalog): `ActiveItemResult`, `BranchLinkResult`, `MatchResult`, `MergeResult`, `StatusResult`, and `SyncResult`.

## Implementation Guidance for Twig

When the Twig.Domain DU work is undertaken, follow these patterns validated in Polyphony:

1. **Union declaration** lists all case types as a single `public union` line
2. **Each case is a `sealed record`** with only the properties relevant to that variant
3. **Asymmetric nullability** across cases encodes semantic meaning (e.g., `ValidTransition.TargetState` is non-nullable; `InvalidTransition.TargetState` is nullable)
4. **Exhaustive `switch` expressions** enforced by the compiler — no default/discard arm needed
5. **JSON output models remain unchanged** — DU is internal only; output commands map to existing JSON-serializable models
6. **Tests use `IUnion.Value.ShouldBeOfType<T>()`** for case assertions, pattern matching for exhaustiveness tests, and `with` expressions for record copy tests

### Priority Order

1. `MutationResult` → `MutationOutcome` — smallest scope (5 files), clearest anti-pattern
2. `ParentPropagationResult` → 5-case DU — textbook enum-with-conditional-fields, eliminates enum + all nullable fields
3. `TransitionResult` → `TransitionResult(Allowed, Denied)` — eliminates redundant bool, 1 consumer file

## Revisit Criteria

- **`Result<T>` generic DU:** Revisit when C# DU generics support is more mature and the Twig codebase enters a lower-churn state. The ~40-file blast radius makes this a standalone refactoring effort.
- **Skipped types:** No revisit recommended unless JSON contract requirements change or the types are restructured for other reasons.

## Conclusion

DU adoption is **validated and recommended** for the 3 identified Twig.Domain types. The Polyphony evaluation (PG-1 through PG-3) confirmed that C# preview DUs are AOT-compatible, provide meaningful compile-time safety improvements for variant types with per-case data, and integrate cleanly with the existing test and build infrastructure. Implementation should proceed in the Twig repository following the patterns and priority order established in this evaluation.
