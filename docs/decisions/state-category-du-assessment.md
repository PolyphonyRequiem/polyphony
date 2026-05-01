# Assessment: StateCategory Enum → Discriminated Union

**Task:** #2807 — Assess StateCategory enum to DU feasibility  
**Issue:** #2794 — Evaluate and Optionally Adopt DU in Twig  
**Date:** 2026-05-01  
**Decision:** **Do not adopt** — keep `StateCategory` as an enum.

---

## Current State

`StateCategory` is a six-member enum in `Twig.Domain.Enums` with explicitly assigned ordinals (`Proposed = 0` through `Unknown = 5`). It serves as a pure classifier — mapping ADO work-item states to one of six categories — and carries no per-variant data. The project-wide `TreatWarningsAsErrors=true` setting already promotes CS8509 (incomplete switch expression) to a compile error, which means any switch expression over `StateCategory` that omits a case will fail the build. This gives the enum near-equivalent exhaustiveness guarantees to a discriminated union for the switch-expression pattern. The five consumer sites in Polyphony (`PhaseDetector`, `TransitionValidator`) and the 20+ consumer sites in Twig2 (`SpectreRenderer`, `SpectreTheme`, `HumanOutputFormatter`, `HintEngine`, `ParentStatePropagationService`, etc.) all rely on this enum in equality comparisons (`== StateCategory.Completed`) and switch expressions — patterns where enum syntax is more concise than DU pattern matching.

## DU Alternative

A discriminated union version of `StateCategory` would look like:

```csharp
public union StateCategory(Proposed, InProgress, Resolved, Completed, Removed, Unknown);
public sealed record Proposed;
public sealed record InProgress;
public sealed record Resolved;
public sealed record Completed;
public sealed record Removed;
public sealed record Unknown;
```

This would replace equality checks (`category == StateCategory.Proposed`) with pattern matches (`category is Proposed`), and switch expressions would use type patterns instead of enum patterns. The primary benefit is that DU switches enforce exhaustiveness without needing `TreatWarningsAsErrors` — the compiler rejects missing cases as errors, not warnings-promoted-to-errors. However, this delta is purely mechanical in Twig's build configuration: `TreatWarningsAsErrors` is a project invariant that is never disabled, so the practical safety gain is zero.

The refactoring would also introduce several costs:

1. **Ordinal loss.** `AdoIterationService.CategoryRank()` relies on enum ordinals for sort ordering. A DU would require an explicit `Rank()` method or lookup table, adding code with no functional improvement.
2. **JSON serialization.** The enum uses `JsonStringEnumConverter<StateCategory>` for zero-config serialization. A DU would need a custom `JsonConverter` or manual mapping, and would need to be registered in `TwigJsonContext` — more code, more surface area for AOT trimming issues.
3. **Cross-repository blast radius.** `StateCategory` is defined in `Twig.Domain` and consumed by both the Twig2 CLI and Polyphony via `ProjectReference`. Changing it to a DU would require coordinated changes across 20+ consumer sites in two codebases, all for a type that carries no per-variant data.
4. **Syntactic overhead.** Equality comparisons (`category == StateCategory.Completed`) are shorter and more readable than type patterns (`category is Completed`) for data-free labels, especially when checking multiple categories in a single condition (`category == StateCategory.InProgress || category == StateCategory.Resolved` vs. `category is InProgress or Resolved`).

## Recommendation

**Do not adopt discriminated unions for `StateCategory`.** The enum is the correct abstraction for a fixed set of data-free labels with ordinal semantics. The `TreatWarningsAsErrors` build invariant already provides compile-time exhaustiveness enforcement for switch expressions, eliminating the primary safety argument for DU adoption. The ordinal dependency, JSON serialization contract, and cross-repository consumer count make the refactoring costly with no measurable safety or expressiveness improvement. DUs deliver their strongest value when variants carry distinct associated data (as demonstrated by `RoutingDecision` and `TransitionOutcome` in this same codebase) — `StateCategory` does not have this characteristic.
