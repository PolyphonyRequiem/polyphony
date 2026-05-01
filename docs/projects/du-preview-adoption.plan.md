# Phase 5: DU Preview Adoption — Solution Design

**Epic:** #2585 — Phase 5: DU Preview Adoption  
**Status:** 🔨 In Progress  
**Revision:** 0  
**Revision Notes:** Initial draft.

---

## Executive Summary

This plan adopts C# 15 discriminated unions (preview feature) in the Polyphony SDLC routing engine to replace stringly-typed routing outcomes with compiler-enforced exhaustive types. The Twig2 codebase already uses DUs successfully across 6 union types (`SyncResult`, `ActiveItemResult`, `BranchLinkResult`, `MatchResult`, `StatusResult`, `MergeResult`) with AOT compilation, proving the feature works with `PublishAot=true` and `TrimMode=full`. This plan brings the same type-safety discipline to Polyphony's `RoutingDecision` and `ValidateResult` types, making missing cases a compile-time error rather than a runtime surprise. The refactoring is purely internal — all JSON output contracts and exit code behaviors are preserved unchanged.

---

## Background

### Current Architecture

Polyphony is an AOT-compiled .NET 11 CLI (`PublishAot=true`, `TrimMode=full`, `InvariantGlobalization=true`) that routes work items through an SDLC state machine. The routing engine consists of:

- **`SdlcPhase`** — static class with 8 string constants (`needs_planning`, `needs_seeding`, `ready_for_implementation`, `in_progress`, `ready_for_completion`, `done`, `removed`, `unknown`)
- **`SdlcAction`** — static class with 6 string constants (`plan`, `seed`, `implement`, `monitor`, `close`, `none`)
- **`RoutingDecision`** — sealed record with `string Phase`, `string Action`, `string? Message` — the internal routing outcome
- **`RouteResult`** — sealed record that maps `RoutingDecision` to JSON output (adds `WorkItemId` and `WorkspaceHint`)
- **`ValidateResult`** — sealed record with `bool IsValid`, `string? TargetState`, `string? Message` — lifecycle event validation outcome
- **`PhaseDetector`** — state machine that evaluates work item state/type/children → `RoutingDecision`
- **`TransitionValidator`** — validates lifecycle events against process config → `ValidateResult`

The string-based `Phase`/`Action` fields in `RoutingDecision` allow invalid combinations (e.g., `Phase = "needs_planning"` with `Action = "close"`) that can only be caught at runtime. The `ValidateResult.IsValid` boolean with nullable fields creates a similar gap — nothing prevents constructing `IsValid = true` with a null `TargetState`.

### Prior Art

The Twig2 project (referenced via `Twig.Domain.csproj`) already uses C# 15 DUs with `LangVersion: preview` and provides the compiler polyfill (`UnionAttribute`, `IUnion`) in `System.Runtime.CompilerServices`. Twig2 defines 6 union types that work correctly with AOT compilation, pattern matching, and the existing test infrastructure. This eliminates the primary risk (AOT compatibility) since the same .NET 11 SDK and build pipeline are used.

### Call-Site Audit: RoutingDecision

| File | Method/Context | Current Usage | DU Impact |
|------|---------------|---------------|-----------|
| `Routing/RoutingDecision.cs` | Type definition | `sealed record` with `string Phase`, `string Action`, `string? Message` | Replace with `union RoutingDecision(...)` |
| `Routing/SdlcPhase.cs` | String constants | 8 phase constants used in `RoutingDecision` construction | Retain as JSON mapping layer |
| `Routing/SdlcAction.cs` | String constants | 6 action constants used in `RoutingDecision` construction | Retain as JSON mapping layer |
| `Routing/PhaseDetector.cs` | `Detect()` | Returns `new RoutingDecision { Phase=..., Action=..., Message=... }` | Return DU cases |
| `Routing/PhaseDetector.cs` | `DetectPlannablePhase()` | Returns 4 different `RoutingDecision` instances | Return DU cases |
| `Routing/PhaseDetector.cs` | `DetectImplementablePhase()` | Switch on `StateCategory` → `RoutingDecision` | Return DU cases (exhaustive match) |
| `Routing/PhaseDetector.cs` | `ClassifyByChildren()` | Returns 3 different `RoutingDecision` instances | Return DU cases |
| `Commands/RouteCommand.cs` | `Route()` | Reads `decision.Phase`, `decision.Action`, `decision.Message` → `RouteResult` | Pattern match DU → `RouteResult` |
| `Tests/.../RoutingDecisionTests.cs` | 7 tests | Construct, compare, `with` copy `RoutingDecision` records | Rewrite for DU API |
| `Tests/.../PhaseDetectorTests.cs` | 13 tests | Assert `result.Phase == SdlcPhase.X`, `result.Action == SdlcAction.Y` | Assert on DU case type |
| `Tests/.../SdlcPhaseTests.cs` | 3 tests | Verify string constant values | Retain (constants still exist) |
| `Tests/.../SdlcActionTests.cs` | 3 tests | Verify string constant values | Retain (constants still exist) |
| `Tests/.../CrossProcessPhaseDetectorTests.cs` | ~20 tests | Assert phase/action across 4 process templates | Update assertions for DU |
| `Tests/.../RouteCommandTests.cs` | ~8 tests | End-to-end via JSON output | No change (JSON unchanged) |
| `Tests/.../CrossProcessRouteCommandTests.cs` | ~12 tests | Cross-process end-to-end | No change (JSON unchanged) |

### Call-Site Audit: ValidateResult

| File | Method/Context | Current Usage | DU Impact |
|------|---------------|---------------|-----------|
| `Models/ValidateResult.cs` | Type definition | `sealed record` with `IsValid`, `TargetState`, `Message` | Replace internal type with DU |
| `Routing/TransitionValidator.cs` | `Validate()` | Returns `ValidateResult` with `IsValid=true/false` | Return `TransitionOutcome` DU cases |
| `Commands/ValidateCommand.cs` | `Validate()` | Serializes `ValidateResult` via `PolyphonyJsonContext`, checks `result.IsValid` | Pattern match DU → `ValidateResult` for JSON |
| `PolyphonyJsonContext.cs` | Serialization | `[JsonSerializable(typeof(ValidateResult))]` | Unchanged (output model stays) |
| `Tests/.../TransitionValidatorTests.cs` | 15 tests | Assert `result.IsValid`, `result.TargetState`, `result.Message` | Update assertions for DU |
| `Tests/.../CrossProcessTransitionValidatorTests.cs` | ~15 tests | Cross-process transition validation | Update assertions for DU |
| `Tests/.../ValidateCommandTests.cs` | ~4 tests | End-to-end via JSON output | No change (JSON unchanged) |
| `Tests/.../CrossProcessValidateCommandTests.cs` | ~4 tests | Cross-process end-to-end | No change (JSON unchanged) |
| `Tests/.../JsonOutputContractTests.cs` | ~5 tests | Verify JSON field names, exit codes | No change (JSON unchanged) |
| `Tests/.../IntegrationScenarioTests.cs` | ~8 tests | Multi-step lifecycle scenarios | No change (JSON unchanged) |

---

## Problem Statement

1. **Stringly-typed routing outcomes:** `RoutingDecision` combines `string Phase` and `string Action` with no compile-time guarantee that valid combinations are used. Adding a new phase requires updating both the constant class and every switch/if chain — missing one is a silent runtime bug.

2. **Boolean-with-nullable-fields validation result:** `ValidateResult.IsValid` is a bool paired with nullable `TargetState` and `Message`. Nothing prevents constructing `IsValid = true` with `TargetState = null`, or `IsValid = false` with a populated `TargetState`. The type allows states that are semantically impossible.

3. **Non-exhaustive switch expressions:** `DetectImplementablePhase` uses a `StateCategory` switch with a `_ =>` default arm. If a new `StateCategory` value is added, the default silently handles it. With DUs, the compiler would flag the missing case.

---

## Goals and Non-Goals

### Goals
- **G1:** Replace `RoutingDecision` with a discriminated union where each case encodes a specific phase+action pair, making invalid combinations unrepresentable
- **G2:** Replace the internal validation result type with a discriminated union (`ValidTransition` / `InvalidTransition`), eliminating the bool+nullable pattern
- **G3:** All 80+ existing tests pass unchanged (behavioral equivalence) — zero regressions
- **G4:** AOT compilation (`PublishAot=true`, `TrimMode=full`) continues to work without warnings or errors
- **G5:** JSON output contracts are preserved byte-for-byte (snake_case naming, null omission, exit codes)

### Non-Goals
- **NG1:** Refactoring `StateCategory` enum in Twig.Domain to a DU (evaluated separately in Issue 5.2)
- **NG2:** Adding new features, commands, or routing logic — this is a pure type-safety refactor
- **NG3:** Changing the `SdlcPhase` / `SdlcAction` string constant classes — they remain as the JSON serialization layer
- **NG4:** Forcing DUs on every type — only types where exhaustiveness adds measurable safety
- **NG5:** Changing the public JSON output models (`RouteResult`, `ValidateResult` for serialization, `HierarchyResult`)

---

## Requirements

### Functional
- **FR1:** `PhaseDetector.Detect()` returns a `RoutingDecision` union with exhaustive case coverage for all 8 phases
- **FR2:** `TransitionValidator.Validate()` returns a `TransitionOutcome` union with `ValidTransition` and `InvalidTransition` cases
- **FR3:** `RouteCommand` maps the DU to the existing `RouteResult` JSON output model via pattern matching
- **FR4:** `ValidateCommand` maps the DU to the existing `ValidateResult` JSON output model via pattern matching
- **FR5:** The compiler polyfill (`UnionAttribute`, `IUnion`) is available to Polyphony — either transitively from Twig.Domain or via a local copy

### Non-Functional
- **NFR1:** AOT build produces no new warnings (IL3050, IL2026, etc.)
- **NFR2:** Trimmer analysis passes with no new warnings
- **NFR3:** `PolyphonyJsonContext` source generator works correctly with preview LangVersion
- **NFR4:** ConsoleAppFramework source generator works correctly with preview LangVersion

---

## Proposed Design

### Architecture Overview

The refactoring maintains the existing layered architecture but introduces type-safe unions at the internal routing boundary:

```
                     ┌─────────────────────┐
                     │   CLI Commands       │
                     │ (RouteCommand, etc.) │
                     └──────────┬──────────┘
                                │ pattern match DU → JSON model
                     ┌──────────▼──────────┐
                     │  JSON Output Models  │  ← UNCHANGED
                     │ (RouteResult, etc.)  │  (same JSON contract)
                     └──────────┬──────────┘
                                │
                     ┌──────────▼──────────┐
                     │   Routing Engine     │
                     │  PhaseDetector       │  ← returns RoutingDecision DU
                     │  TransitionValidator │  ← returns TransitionOutcome DU
                     └──────────┬──────────┘
                                │
                     ┌──────────▼──────────┐
                     │  Twig.Domain         │  ← UNCHANGED
                     │ (WorkItem, StateCategory, etc.)
                     └─────────────────────┘
```

**Key principle:** DUs live at the internal routing boundary. JSON output models (`RouteResult`, `ValidateResult`) remain unchanged sealed records. The command layer performs the DU → JSON model mapping via exhaustive pattern matching.

### Key Components

#### 1. RoutingDecision Union

Replaces the current `sealed record RoutingDecision` with a discriminated union where each case encodes a specific phase+action combination:

```csharp
// Each case carries only the data specific to that routing outcome
public sealed record NeedsPlanning(string Message);
public sealed record NeedsSeeding(string Message);
public sealed record ReadyForImplementation(string Message);
public sealed record ImplementationInProgress(string Message);
public sealed record ReadyForCompletion(string Message);
public sealed record RoutingDone(string Message);
public sealed record RoutingRemoved(string Message);
public sealed record RoutingUnknown(string Message);

public union RoutingDecision(
    NeedsPlanning, NeedsSeeding, ReadyForImplementation,
    ImplementationInProgress, ReadyForCompletion,
    RoutingDone, RoutingRemoved, RoutingUnknown);
```

**Why these names:** Cases like `RoutingDone` and `RoutingRemoved` use prefixed names to avoid collisions with common identifiers. `ImplementationInProgress` is used instead of `InProgress` to avoid collision with the `StateCategory.InProgress` enum value that's in scope throughout the routing code.

**Phase-Action coupling:** Each case implicitly encodes both the phase and the action. `NeedsPlanning` always maps to `(SdlcPhase.NeedsPlanning, SdlcAction.Plan)`. It's impossible to create a `NeedsPlanning` decision with the wrong action — the invalid state is unrepresentable.

#### 2. TransitionOutcome Union

Replaces the boolean-based `ValidateResult` as the internal validation type:

```csharp
public sealed record ValidTransition(int WorkItemId, string Event, string TargetState, string Message);
public sealed record InvalidTransition(int WorkItemId, string Event, string? TargetState, string Message);

public union TransitionOutcome(ValidTransition, InvalidTransition);
```

**Why two cases:** A valid transition always has a `TargetState` (non-nullable). An invalid transition may or may not have a target state (nullable) — it has one when the event is known but a precondition failed, and `null` when the event or type is unrecognized. This constraint is now encoded in the types.

#### 3. Command-Layer Mapping

The `RouteCommand` maps `RoutingDecision` → `RouteResult` via exhaustive pattern matching:

```csharp
var (phase, action, message) = decision switch
{
    NeedsPlanning d       => (SdlcPhase.NeedsPlanning, SdlcAction.Plan, d.Message),
    NeedsSeeding d        => (SdlcPhase.NeedsSeeding, SdlcAction.Seed, d.Message),
    ReadyForImplementation d => (SdlcPhase.ReadyForImplementation, SdlcAction.Implement, d.Message),
    ImplementationInProgress d => (SdlcPhase.InProgress, SdlcAction.Monitor, d.Message),
    ReadyForCompletion d  => (SdlcPhase.ReadyForCompletion, SdlcAction.Close, d.Message),
    RoutingDone d         => (SdlcPhase.Done, SdlcAction.None, d.Message),
    RoutingRemoved d      => (SdlcPhase.Removed, SdlcAction.None, d.Message),
    RoutingUnknown d      => (SdlcPhase.Unknown, SdlcAction.None, d.Message),
};
```

Adding a new phase to the union forces every consumer to handle it — the compiler rejects non-exhaustive matches.

The `ValidateCommand` maps `TransitionOutcome` → `ValidateResult` similarly:

```csharp
var outputResult = outcome switch
{
    ValidTransition v => new ValidateResult
    {
        WorkItemId = v.WorkItemId, Event = v.Event,
        IsValid = true, TargetState = v.TargetState, Message = v.Message,
    },
    InvalidTransition iv => new ValidateResult
    {
        WorkItemId = iv.WorkItemId, Event = iv.Event,
        IsValid = false, TargetState = iv.TargetState, Message = iv.Message,
    },
};
```

### Data Flow

**Route command (before):**
```
WorkItem → PhaseDetector.Detect() → RoutingDecision{Phase="...", Action="...", Message="..."}
    → RouteCommand reads .Phase, .Action, .Message → RouteResult → JSON
```

**Route command (after):**
```
WorkItem → PhaseDetector.Detect() → RoutingDecision union (e.g., NeedsPlanning("..."))
    → RouteCommand pattern-matches → (phase, action, msg) → RouteResult → JSON
```

**Validate command (before):**
```
WorkItem → TransitionValidator.Validate() → ValidateResult{IsValid=true/false, ...}
    → ValidateCommand serializes directly → JSON
```

**Validate command (after):**
```
WorkItem → TransitionValidator.Validate() → TransitionOutcome union (ValidTransition/InvalidTransition)
    → ValidateCommand pattern-matches → ValidateResult (output model) → JSON
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Keep JSON output models unchanged** | External scripts consume the JSON contract. Changing it would break conductor workflows. DUs are internal only. |
| **Keep SdlcPhase/SdlcAction string constants** | These serve as the JSON string mapping layer. They're consumed by the command-layer pattern match. |
| **Name collision avoidance with prefixed case names** | `RoutingDone` instead of `Done`, `ImplementationInProgress` instead of `InProgress` — avoids conflicts with `StateCategory` enum values in scope. |
| **Separate internal DU from output model** | `TransitionOutcome` (internal) vs `ValidateResult` (JSON output) — the DU enforces invariants while the output model preserves backward compatibility. |
| **Do not refactor HierarchyResult** | `HierarchyResult` is a data transfer object, not a routing outcome. It has no variants — a DU would add complexity with no benefit. |

---

## Dependencies

### External
- **.NET 11 Preview SDK** (11.0.100-preview.3) — already in use; provides C# 15 union syntax
- **Compiler polyfill types** (`UnionAttribute`, `IUnion`) — provided by Twig.Domain reference; verify transitive availability or add local copy
- **ConsoleAppFramework 5.7.13** — must be compatible with `LangVersion: preview` (already works in Twig2)
- **System.Text.Json source generator** — must handle DU-adjacent types (DU types are not serialized directly)

### Internal
- **Twig.Domain** — provides `StateCategory`, `StateCategoryResolver`, `WorkItem` — no changes needed
- **Twig.Infrastructure** — provides data access services — no changes needed

### Sequencing
- Phase 4 (all tests pass, system stable) must be complete before starting this work
- Issue 1 (infrastructure) must complete before Issues 2-3 (refactoring)
- Issue 2 and Issue 3 can proceed in parallel after Issue 1
- Issue 4 (Twig) depends on Issues 1-3 proving success

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| DU preview syntax breaks AOT compilation in Polyphony | Low | High | Twig2 already proves DU + AOT works with same SDK. Verify early in Issue 1. If broken, REVERT and stop. |
| ConsoleAppFramework source generator incompatible with preview LangVersion | Low | Medium | Twig2 uses same version with preview. Test in Issue 1 before any refactoring. |
| Compiler polyfill not transitively available from Twig.Domain | Low | Low | Add local `CompilerPolyfill.cs` to Polyphony project (same pattern as Twig2). |
| Trimmer removes DU metadata needed at runtime | Low | High | DU types are internal — not serialized or reflected. Trimmer should preserve them. Verify with AOT publish. |
| Test rewrite introduces subtle behavioral differences | Medium | Medium | Run tests before and after each refactoring. JSON output contract tests serve as safety net (they don't touch internal types). |
| `PolyphonyJsonContext` source generator confused by union types | Low | Medium | DU types are not registered in `PolyphonyJsonContext` — only the existing output models (`RouteResult`, `ValidateResult`) are serialized. |

---

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| OQ-1 | Does the compiler polyfill from Twig.Domain propagate transitively, or does Polyphony need its own copy? | Low | Easy to test: try building with `union` syntax. If it fails, add local copy. |
| OQ-2 | Should `RoutingDecision` DU case names use a common prefix (e.g., `Routing*`) or be unprefixed? | Low | Design choice with no functional impact. Current plan uses mixed naming to avoid collisions. |
| OQ-3 | Is StateCategory → DU refactoring in Twig worthwhile given it's already an enum with exhaustive switch support? | Low | Deferred to Issue 5.2 evaluation. Enum already provides exhaustiveness via `TreatWarningsAsErrors`. |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Polyphony/Routing/RoutingDecision.cs` | Rewritten — DU union type with 8 case records (replaces existing sealed record) |
| `src/Polyphony/Routing/TransitionOutcome.cs` | New — DU union type with `ValidTransition`/`InvalidTransition` cases |
| `src/Polyphony/CompilerPolyfill.cs` | Conditional — only if polyfill types aren't transitively available from Twig.Domain |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `Directory.Build.props` | Change `LangVersion` from `latest` to `preview` |
| `src/Polyphony/Routing/PhaseDetector.cs` | Return DU cases instead of `new RoutingDecision { ... }` |
| `src/Polyphony/Routing/TransitionValidator.cs` | Return `TransitionOutcome` DU instead of `ValidateResult` |
| `src/Polyphony/Commands/RouteCommand.cs` | Pattern match `RoutingDecision` DU → `RouteResult` |
| `src/Polyphony/Commands/ValidateCommand.cs` | Pattern match `TransitionOutcome` DU → `ValidateResult` for JSON output |
| `tests/Polyphony.Tests/Routing/RoutingDecisionTests.cs` | Rewrite for DU construction and matching API |
| `tests/Polyphony.Tests/Routing/PhaseDetectorTests.cs` | Update assertions to check DU case types instead of string properties |
| `tests/Polyphony.Tests/Routing/TransitionValidatorTests.cs` | Update assertions for `TransitionOutcome` DU |
| `tests/Polyphony.Tests/Routing/CrossProcessPhaseDetectorTests.cs` | Update assertions for DU case types |
| `tests/Polyphony.Tests/Routing/CrossProcessTransitionValidatorTests.cs` | Update assertions for `TransitionOutcome` DU |

---

## ADO Work Item Structure

### Issue 5.1.1: Enable DU Preview Infrastructure in Polyphony

**Goal:** Set up the Polyphony project to support C# 15 discriminated unions and verify that AOT compilation, source generators, and the full test suite continue to work with the preview LangVersion.

**Prerequisites:** Phase 4 complete (all tests pass, system stable)

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 5.1.1-A | Set `LangVersion` to `preview` in `Directory.Build.props` | `Directory.Build.props` | S |
| 5.1.1-B | Verify compiler polyfill availability — test that `union` keyword compiles when referencing Twig.Domain. If polyfill types are not transitively available, add `CompilerPolyfill.cs` to `src/Polyphony/` | `src/Polyphony/CompilerPolyfill.cs` (conditional) | S |
| 5.1.1-C | Build solution (`dotnet build`) and run all tests (`dotnet test`) — verify zero new warnings/errors from preview LangVersion and source generators (ConsoleAppFramework, PolyphonyJsonContext) | N/A (build verification) | S |
| 5.1.1-D | Verify AOT publish (`dotnet publish -c Release`) succeeds with preview LangVersion — check for IL3050/IL2026 trimmer warnings | N/A (publish verification) | S |

**Acceptance Criteria:**
- [ ] `LangVersion` is `preview` in Directory.Build.props
- [ ] A trivial `union` type compiles successfully in the Polyphony project
- [ ] `dotnet build` succeeds with zero new warnings
- [ ] `dotnet test` passes all existing tests
- [ ] `dotnet publish -c Release` (AOT) succeeds with zero new trimmer warnings

---

### Issue 5.1.2: Refactor RoutingDecision to Discriminated Union

**Goal:** Replace the stringly-typed `RoutingDecision` sealed record with a discriminated union where each case encodes a specific phase+action pair. Update `PhaseDetector` to return DU cases and `RouteCommand` to map DU → JSON via exhaustive pattern matching.

**Prerequisites:** Issue 5.1.1 (DU infrastructure enabled)

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 5.1.2-A | Define `RoutingDecision` union type with 8 case records (`NeedsPlanning`, `NeedsSeeding`, `ReadyForImplementation`, `ImplementationInProgress`, `ReadyForCompletion`, `RoutingDone`, `RoutingRemoved`, `RoutingUnknown`) in `Routing/RoutingDecision.cs` — replace the existing sealed record | `src/Polyphony/Routing/RoutingDecision.cs` | M |
| 5.1.2-B | Refactor `PhaseDetector` to return DU cases instead of `new RoutingDecision { Phase=..., Action=..., Message=... }`. Update `Detect()`, `DetectPlannablePhase()`, `DetectImplementablePhase()`, and `ClassifyByChildren()` | `src/Polyphony/Routing/PhaseDetector.cs` | M |
| 5.1.2-C | Update `RouteCommand.Route()` to pattern-match the `RoutingDecision` DU into `(phase, action, message)` tuple, then construct `RouteResult` from those values. The JSON output must remain identical. | `src/Polyphony/Commands/RouteCommand.cs` | S |
| 5.1.2-D | Update all routing unit tests: `RoutingDecisionTests.cs` (rewrite for DU API), `PhaseDetectorTests.cs` (assert on DU case types), `CrossProcessPhaseDetectorTests.cs` (update assertions). Verify all command-level tests still pass unchanged. | `tests/.../RoutingDecisionTests.cs`, `tests/.../PhaseDetectorTests.cs`, `tests/.../CrossProcessPhaseDetectorTests.cs` | L |

**Acceptance Criteria:**
- [ ] `RoutingDecision` is a discriminated union with 8 exhaustive cases
- [ ] `PhaseDetector` returns DU cases with no `new RoutingDecision { ... }` construction
- [ ] `RouteCommand` uses exhaustive pattern matching (no default arm)
- [ ] All 13 `PhaseDetectorTests` pass
- [ ] All ~20 `CrossProcessPhaseDetectorTests` pass
- [ ] All `RouteCommandTests` and `CrossProcessRouteCommandTests` pass (JSON output unchanged)
- [ ] All `JsonOutputContractTests` pass (contract unchanged)

---

### Issue 5.1.3: Refactor TransitionResult to Discriminated Union

**Goal:** Replace the boolean-based `ValidateResult` as the internal validation type with a `TransitionOutcome` discriminated union. The `ValidateResult` record remains as the JSON output model — the command layer maps DU → output model.

**Prerequisites:** Issue 5.1.1 (DU infrastructure enabled)

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 5.1.3-A | Define `TransitionOutcome` union type with `ValidTransition` and `InvalidTransition` case records in a new file `Routing/TransitionOutcome.cs` | `src/Polyphony/Routing/TransitionOutcome.cs` | S |
| 5.1.3-B | Refactor `TransitionValidator.Validate()` to return `TransitionOutcome` instead of `ValidateResult`. Update all 4 precondition check methods and the main validation flow. | `src/Polyphony/Routing/TransitionValidator.cs` | M |
| 5.1.3-C | Update `ValidateCommand.Validate()` to pattern-match `TransitionOutcome` DU → `ValidateResult` (JSON output model). The JSON output and exit code logic must remain identical. | `src/Polyphony/Commands/ValidateCommand.cs` | S |
| 5.1.3-D | Update all validator unit tests: `TransitionValidatorTests.cs` (assert on DU case types instead of `IsValid` bool), `CrossProcessTransitionValidatorTests.cs` (update assertions). Verify command-level tests pass unchanged. | `tests/.../TransitionValidatorTests.cs`, `tests/.../CrossProcessTransitionValidatorTests.cs` | L |

**Acceptance Criteria:**
- [ ] `TransitionOutcome` is a discriminated union with `ValidTransition` and `InvalidTransition` cases
- [ ] `TransitionValidator.Validate()` returns `TransitionOutcome`
- [ ] `ValidateCommand` maps DU → `ValidateResult` (JSON output model) via exhaustive pattern matching
- [ ] All 15 `TransitionValidatorTests` pass
- [ ] All ~15 `CrossProcessTransitionValidatorTests` pass
- [ ] All `ValidateCommandTests` and `CrossProcessValidateCommandTests` pass (JSON output unchanged)
- [ ] All `JsonOutputContractTests` pass (contract unchanged)

---

### Issue 5.2.1: Evaluate and Optionally Adopt DU in Twig

**Goal:** Assess whether converting `StateCategory` or routing-adjacent result types in Twig.Domain to DUs provides meaningful safety improvements. Only proceed if Issue 5.1.x proves DU works well with AOT and if the refactoring adds measurable value.

**Prerequisites:** Issues 5.1.1, 5.1.2, and 5.1.3 all complete and verified

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 5.2.1-A | Assess `StateCategory` enum → DU feasibility: document whether `StateCategory` benefits from DU (enum already provides switch exhaustiveness with `TreatWarningsAsErrors`). Write recommendation with rationale. | N/A (analysis only) | S |
| 5.2.1-B | Identify any routing-adjacent result types in Twig.Domain that would benefit from DU refactoring but aren't already DUs. Catalog candidates with rationale. | N/A (analysis only) | S |
| 5.2.1-C | If assessment recommends adoption: implement the approved DU refactoring(s) in Twig.Domain, update all consumers, and verify tests pass. If not: document the decision and close. | Twig.Domain files (TBD) | M |

**Acceptance Criteria:**
- [ ] Written assessment of `StateCategory` → DU with clear recommendation
- [ ] Catalog of DU candidates in Twig.Domain with rationale
- [ ] If adopted: all Twig tests pass, AOT build succeeds
- [ ] If not adopted: decision documented with reasoning

---

## PR Groups

PR groups cluster work for reviewable pull requests. They are sized for reviewability and are independent of the ADO hierarchy.

### PG-1: Enable DU Preview Infrastructure

**Tasks:** 5.1.1-A, 5.1.1-B, 5.1.1-C, 5.1.1-D  
**Classification:** Wide (touches build config, potentially adds a file)  
**Estimated LoC:** ~20  
**Estimated Files:** 1-2  
**Successors:** PG-2, PG-3 (both depend on PG-1)

**Description:** Minimal infrastructure change — set `LangVersion: preview`, verify polyfill availability, confirm AOT build and tests pass. This is the gate: if this PR reveals incompatibilities, we stop and revert.

---

### PG-2: RoutingDecision DU Refactor

**Tasks:** 5.1.2-A, 5.1.2-B, 5.1.2-C, 5.1.2-D  
**Classification:** Deep (few files, complex type-system changes)  
**Estimated LoC:** ~400  
**Estimated Files:** ~6  
**Predecessors:** PG-1  
**Successors:** PG-4 (conditional)

**Description:** Core RoutingDecision refactoring — define the union type, update PhaseDetector to return DU cases, update RouteCommand to pattern-match, rewrite unit tests. This is the largest PR but touches only routing code. All JSON output contract tests serve as regression safety net.

---

### PG-3: TransitionOutcome DU Refactor

**Tasks:** 5.1.3-A, 5.1.3-B, 5.1.3-C, 5.1.3-D  
**Classification:** Deep (few files, complex type-system changes)  
**Estimated LoC:** ~300  
**Estimated Files:** ~5  
**Predecessors:** PG-1  
**Successors:** PG-4 (conditional)

**Description:** TransitionOutcome refactoring — define the union type, update TransitionValidator, update ValidateCommand, rewrite unit tests. Independent from PG-2 — can be reviewed in parallel.

---

### PG-4: (Conditional) Twig DU Evaluation and Adoption

**Tasks:** 5.2.1-A, 5.2.1-B, 5.2.1-C  
**Classification:** Deep (analysis + potential type refactoring)  
**Estimated LoC:** ~200 (if adopted), ~0 (if not)  
**Estimated Files:** 2-4 (if adopted)  
**Predecessors:** PG-2, PG-3  
**Successors:** None

**Description:** Conditional PR — only created if the assessment in Task 5.2.1-A/B recommends DU adoption for specific Twig types. May result in a "decision: do not adopt" document instead of code changes.

---

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|-------------|--------------|------|
| PG-1-du-preview-infrastructure | Enable DU Preview Infrastructure | 5.1.1 (A, B, C, D) | None | wide |
| PG-2-routing-decision-du | RoutingDecision DU Refactor | 5.1.2 (A, B, C, D) | PG-1 | deep |
| PG-3-transition-outcome-du | TransitionOutcome DU Refactor | 5.1.3 (A, B, C, D) | PG-1 | deep |
| PG-4-twig-du-evaluation | (Conditional) Twig DU Evaluation and Adoption | 5.2.1 (A, B, C) | PG-2, PG-3 | deep |

### Execution Order

1. **PG-1** (gate): Set `LangVersion: preview`, verify polyfill, confirm AOT build and all tests pass. If this fails, stop and revert — nothing else can proceed.
2. **PG-2 and PG-3** (parallel): Once PG-1 merges, both can be implemented and reviewed concurrently. They touch disjoint files (`RoutingDecision`/`PhaseDetector`/`RouteCommand` vs `TransitionOutcome`/`TransitionValidator`/`ValidateCommand`) with no shared state.
3. **PG-4** (conditional): Only after PG-2 and PG-3 are both merged and verified. Created only if the assessment recommends DU adoption in Twig.Domain.

### Validation Strategy per PG

**PG-1 — Enable DU Preview Infrastructure**
- `dotnet build` succeeds with zero new warnings
- `dotnet test` passes all existing tests
- `dotnet publish -c Release` (AOT) succeeds with zero new trimmer warnings (IL3050/IL2026)
- A trivial `union` type compiles in Polyphony project (smoke test)

**PG-2 — RoutingDecision DU Refactor**
- All 13 `PhaseDetectorTests` pass with DU-based assertions
- All ~20 `CrossProcessPhaseDetectorTests` pass
- All `RouteCommandTests` and `CrossProcessRouteCommandTests` pass (JSON output unchanged)
- All `JsonOutputContractTests` pass (contract unchanged)
- `dotnet publish -c Release` succeeds with no new AOT warnings

**PG-3 — TransitionOutcome DU Refactor**
- All 15 `TransitionValidatorTests` pass with DU-based assertions
- All ~15 `CrossProcessTransitionValidatorTests` pass
- All `ValidateCommandTests` and `CrossProcessValidateCommandTests` pass (JSON output unchanged)
- All `JsonOutputContractTests` and `IntegrationScenarioTests` pass
- `dotnet publish -c Release` succeeds with no new AOT warnings

**PG-4 — Twig DU Evaluation (Conditional)**
- Written recommendation with rationale for `StateCategory` → DU decision
- Catalog of DU candidates in Twig.Domain
- If adopted: all Twig tests pass, AOT build succeeds, no new trimmer warnings
- If not adopted: decision document committed to `docs/`

