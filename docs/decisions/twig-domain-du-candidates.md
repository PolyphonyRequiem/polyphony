# Catalog: DU Candidates in Twig.Domain

**Task:** #2808 — Catalog DU candidates in Twig.Domain with rationale  
**Issue:** #2794 — Evaluate and Optionally Adopt DU in Twig  
**Date:** 2026-05-01

---

## Context: Existing DU Types (6 — no action needed)

These types already use `public union` and demonstrate the pattern working with AOT:

| # | Type | File | Cases |
|---|------|------|-------|
| 1 | `ActiveItemResult` | `Services/Navigation/ActiveItemResult.cs` | Found, ActiveNoContext, FetchedFromAdo, ActiveUnreachable |
| 2 | `BranchLinkResult` | `ValueObjects/BranchLinkResult.cs` | Linked, AlreadyLinked, GitContextUnavailable, LinkFailed |
| 3 | `MatchResult` | `Services/Navigation/PatternMatcher.cs` | SingleMatch, MultipleMatches, NoMatch |
| 4 | `MergeResult` | `Services/Sync/ConflictResolver.cs` | NoConflict, AutoMergeable, HasConflicts |
| 5 | `StatusResult` | `Services/Workspace/StatusResult.cs` | StatusNoContext, StatusUnreachable, StatusSuccess |
| 6 | `SyncResult` | `Services/Sync/SyncResult.cs` | UpToDate, Updated, SyncFailed, Skipped, PartiallyUpdated |

---

## Candidate Catalog

### 1. `MutationResult` — **Adopt**

**File:** `Services/Mutation/MutationResult.cs`

**Current representation:**

```csharp
public sealed record MutationResult(bool IsSuccess, string? ErrorMessage = null, int? NewRevision = null)
{
    public static MutationResult Success(int newRevision) => new(true, NewRevision: newRevision);
    public static MutationResult Error(string message) => new(false, message);
}
```

**Anti-pattern:** Bool + nullable fields. Nothing prevents `IsSuccess=true` with `ErrorMessage="oops"` or `IsSuccess=false` with `NewRevision=42`. The static factory methods encode the correct invariants, but the primary constructor does not enforce them.

**Proposed DU:**

```csharp
public union MutationOutcome(Succeeded, Failed);
public sealed record Succeeded(int NewRevision);
public sealed record Failed(string ErrorMessage);
```

**Invariant encoded:** Success always carries a revision; failure always carries an error message. Impossible states become unrepresentable.

**Consumer impact:** 5 files across Twig.Domain and Twig.Infrastructure (`IMutationProvider`, `SeedMutationProvider`, `AdoMutationProvider`, plus tests). All consumers already branch on `IsSuccess` — refactoring to pattern matching is mechanical.

**JSON contract:** No — internal service result only. Not registered in `TwigJsonContext`.

**Recommendation:** ✅ **Adopt.** Small blast radius (5 files), clear safety improvement, no JSON impact. Ideal first candidate.

---

### 2. `Result` / `Result<T>` — **Defer**

**File:** `Common/Result.cs`

**Current representation:**

```csharp
public readonly record struct Result
{
    public bool IsSuccess { get; }
    public string Error { get; }
}

public readonly record struct Result<T>
{
    public bool IsSuccess { get; }
    private readonly T _value;
    public string Error { get; }

    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException($"Cannot access Value on a failed result. Error: {Error}");
}
```

**Anti-pattern:** Bool + conditional fields. `Value` throws at runtime if `IsSuccess=false` — a class of bugs that DUs eliminate at compile time. `Error` is accessible even when `IsSuccess=true` (it's just `string.Empty`, but nothing prevents misuse).

**Proposed DU:**

```csharp
public union Result(Ok, Fail);
public sealed record Ok;
public sealed record Fail(string Error);

public union Result<T>(Ok, Fail);
public sealed record Ok<T>(T Value);
public sealed record Fail(string Error);
```

**Invariant encoded:** Success always carries a value (for `Result<T>`); failure always carries an error. No runtime throws on value access.

**Consumer impact:** ~40 files across Twig.Domain, Twig.Infrastructure, Twig.Mcp, and Twig CLI. This is the most widely-used result type in the codebase. Every consumer does `if (!result.IsSuccess)` → refactoring requires touching nearly every service.

**JSON contract:** No — internal only.

**Risk:** `readonly record struct` → DU would be a reference type. This changes allocation semantics for a very hot path. The generic `Result<T>` adds complexity because C# DUs don't yet support generic union cases cleanly. The sheer number of consumers (~40 files, 100+ usage sites) makes this a high-disruption refactoring with significant merge-conflict risk during development.

**Recommendation:** ⏸️ **Defer.** The safety benefit is real, but the blast radius (~40 files), struct-to-class allocation change, and generic type complexity make this a poor candidate for this iteration. Revisit when DU generics support matures and the codebase is in a lower-churn state. Consider as a dedicated refactoring effort.

---

### 3. `ParentPropagationResult` — **Adopt**

**File:** `Services/Navigation/ParentStatePropagationService.cs`

**Current representation:**

```csharp
public enum ParentPropagationOutcome
{
    NotApplicable,
    NoParent,
    AlreadyActive,
    Propagated,
    Failed,
}

public sealed record ParentPropagationResult
{
    public ParentPropagationOutcome Outcome { get; init; }
    public string? ParentOldState { get; init; }
    public string? ParentNewState { get; init; }
    public int? ParentId { get; init; }
    public string? Error { get; init; }
}
```

**Anti-pattern:** Enum + conditional nullable fields. Field validity depends entirely on the `Outcome` value:

- `NotApplicable`, `NoParent` → only `Outcome` is meaningful; all other fields should be null
- `AlreadyActive` → `ParentId` and `ParentOldState` are set; `ParentNewState` and `Error` should be null
- `Propagated` → `ParentId`, `ParentOldState`, and `ParentNewState` are all set; `Error` should be null
- `Failed` → `Error` and optionally `ParentId` are set; state fields should be null

Nothing prevents constructing `Outcome=Propagated` with `ParentNewState=null`, or `Outcome=NotApplicable` with `Error="oops"`.

**Proposed DU:**

```csharp
public union ParentPropagationResult(NotApplicable, NoParent, AlreadyActive, Propagated, Failed);
public sealed record NotApplicable;
public sealed record NoParent;
public sealed record AlreadyActive(int ParentId, string ParentOldState);
public sealed record Propagated(int ParentId, string ParentOldState, string ParentNewState);
public sealed record Failed(int? ParentId, string Error);
```

**Invariant encoded:** Each outcome variant carries exactly the data relevant to that case. `Propagated` always has both old and new states. `Failed` always has an error.

**Consumer impact:** 1 file (the service itself — `ParentStatePropagationService.cs`). This is a fully internal type with no external consumers beyond tests.

**JSON contract:** No — internal service result only. Not registered in `TwigJsonContext`.

**Recommendation:** ✅ **Adopt.** Textbook DU candidate — enum-with-conditional-fields is the exact anti-pattern DUs solve. Minimal blast radius (1 consumer file), strong invariant encoding, no JSON impact. Also eliminates the `ParentPropagationOutcome` enum entirely.

---

### 4. `TransitionResult` — **Adopt**

**File:** `Services/Process/StateTransitionService.cs`

**Current representation:**

```csharp
public record TransitionResult
{
    public TransitionKind Kind { get; init; }
    public bool IsAllowed { get; init; }
}
```

Where `TransitionKind` is:

```csharp
public enum TransitionKind { None = 0, Forward, Cut }
```

**Anti-pattern:** Enum + redundant bool. `IsAllowed` is semantically derived from `Kind`: `None` means disallowed, `Forward`/`Cut` mean allowed. The type permits impossible states like `Kind=None, IsAllowed=true` or `Kind=Forward, IsAllowed=false`.

**Proposed DU:**

```csharp
public union TransitionResult(Allowed, Denied);
public sealed record Allowed(TransitionKind Kind); // Kind is Forward or Cut
public sealed record Denied;
```

**Invariant encoded:** Allowed transitions always carry a valid `TransitionKind` (Forward/Cut). Denied transitions carry no kind — the enum's `None` value is replaced by the `Denied` case itself.

**Consumer impact:** 1 file (`StateTransitionService.cs`). `TransitionResult` is returned by `StateTransitionService.Evaluate()` and consumed by `ParentStatePropagationService`. Very small scope.

**JSON contract:** No — internal only.

**Recommendation:** ✅ **Adopt.** Small scope (1 consumer file), eliminates redundant bool, makes the allowed/denied distinction type-safe. `TransitionKind` enum is retained for the Forward/Cut classification.

---

### 5. `DescendantVerificationResult` — **Skip**

**File:** `ReadModels/DescendantVerificationResult.cs`

**Current representation:**

```csharp
public sealed record DescendantVerificationResult(
    int RootId,
    bool Verified,
    int TotalChecked,
    IReadOnlyList<IncompleteItem> Incomplete);
```

**Assessment:** The `Verified` flag correlates with `Incomplete.Count == 0`, but this is a weak invariant — it's a data carrier reporting the outcome of a verification scan. Both `RootId` and `TotalChecked` are present regardless of the verification outcome, which means a DU split would duplicate shared fields:

```csharp
// Hypothetical — note the duplication
public sealed record AllVerified(int RootId, int TotalChecked);
public sealed record HasIncomplete(int RootId, int TotalChecked, IReadOnlyList<IncompleteItem> Incomplete);
```

**Consumer impact:** 6 files across Twig.Domain, Twig.Infrastructure, and Twig.Mcp.

**JSON contract:** ⚠️ **Yes** — registered in `TwigJsonContext` as `[JsonSerializable(typeof(DescendantVerificationResult))]`. Converting to a DU would require a custom `JsonConverter` and potentially break API consumers.

**Recommendation:** ❌ **Skip.** Weak invariant (empty list vs. non-empty list), shared fields across variants cause duplication, and JSON serialization contract makes conversion costly. The bool + list pattern is idiomatic for data carriers.

---

### 6. `SeedPublishResult` — **Skip**

**File:** `ValueObjects/SeedPublishResult.cs`

**Current representation:**

```csharp
public sealed class SeedPublishResult
{
    public int OldId { get; init; }
    public int NewId { get; init; }
    public string Title { get; init; } = string.Empty;
    public SeedPublishStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<string> LinkWarnings { get; init; } = [];
    public IReadOnlyList<SeedValidationFailure> ValidationFailures { get; init; } = [];
    public bool IsSuccess => Status is SeedPublishStatus.Created or SeedPublishStatus.Skipped or SeedPublishStatus.DryRun;
}
```

**Assessment:** While this does exhibit the enum + conditional fields anti-pattern (ErrorMessage for Error status, ValidationFailures for ValidationFailed status), it is a data-rich type with many shared fields (`OldId`, `NewId`, `Title`, `LinkWarnings`). A DU would require duplicating these shared fields across 5 variants, or extracting a base record — both add complexity.

**Consumer impact:** 11 files across Twig.Domain, Twig.Mcp, Twig CLI (formatters, commands).

**JSON contract:** Serialized through multiple output formatters (`JsonOutputFormatter`, `JsonCompactOutputFormatter`, etc.). Schema change would impact CLI output contracts.

**Recommendation:** ❌ **Skip.** Data-rich type with many shared fields, 5 enum variants, wide consumer surface, and JSON contract obligations. DU conversion would add complexity without proportional safety gain.

---

### 7. `SeedValidationResult` — **Skip**

**File:** `ValueObjects/SeedValidationResult.cs`

**Current representation:**

```csharp
public sealed class SeedValidationResult
{
    public int SeedId { get; init; }
    public string Title { get; init; } = string.Empty;
    public bool Passed => Failures.Count == 0;
    public IReadOnlyList<SeedValidationFailure> Failures { get; init; } = [];
}
```

**Assessment:** `Passed` is a computed property derived from `Failures.Count`. This is a simple aggregator, not a variant type — it always carries the same fields regardless of outcome. There is no impossible state to eliminate.

**Consumer impact:** 10 files.

**JSON contract:** Serialized through output formatters.

**Recommendation:** ❌ **Skip.** Not a variant type — it's an aggregator with a computed property. No impossible states exist.

---

## Summary

| Type | File | Current Form | Proposed DU | Benefit | Consumers | JSON? | Recommendation |
|------|------|-------------|------------|---------|-----------|-------|----------------|
| `MutationResult` | `Services/Mutation/MutationResult.cs` | `sealed record` + bool + nullables | `MutationOutcome(Succeeded, Failed)` | Eliminates success-with-error impossible state | 5 files | No | ✅ **Adopt** |
| `Result` / `Result<T>` | `Common/Result.cs` | `readonly record struct` + bool + nullable | `Result(Ok, Fail)` | Eliminates runtime throws on value access | ~40 files | No | ⏸️ **Defer** |
| `ParentPropagationResult` | `Services/Navigation/ParentStatePropagationService.cs` | `sealed record` + enum + nullables | `ParentPropagationResult(5 cases)` | Each variant carries only relevant data | 1 file | No | ✅ **Adopt** |
| `TransitionResult` | `Services/Process/StateTransitionService.cs` | `record` + enum + redundant bool | `TransitionResult(Allowed, Denied)` | Eliminates redundant bool; makes allowed/denied type-safe | 1 file | No | ✅ **Adopt** |
| `DescendantVerificationResult` | `ReadModels/DescendantVerificationResult.cs` | `sealed record` + bool + list | — | Weak invariant; shared fields | 6 files | **Yes** | ❌ **Skip** |
| `SeedPublishResult` | `ValueObjects/SeedPublishResult.cs` | `sealed class` + enum + nullables | — | Data-rich; shared fields; 5 variants | 11 files | **Yes** | ❌ **Skip** |
| `SeedValidationResult` | `ValueObjects/SeedValidationResult.cs` | `sealed class` + computed bool | — | Aggregator, not variant type | 10 files | **Yes** | ❌ **Skip** |

### Adoption Priority

1. **MutationResult** → `MutationOutcome` — Clear bool+nullable anti-pattern, small scope, ideal first candidate
2. **ParentPropagationResult** → DU with 5 typed cases — Textbook enum+conditional-fields, eliminates enum and all nullable fields
3. **TransitionResult** → `TransitionResult(Allowed, Denied)` — Eliminates redundant bool, 1 consumer file

### Deferred

4. **Result / Result\<T\>** — High value but ~40-file blast radius, struct-to-class change, generic complexity. Revisit as dedicated effort.

### Skipped (not recommended)

5. **DescendantVerificationResult** — Weak invariant, JSON contract, shared fields
6. **SeedPublishResult** — Data-rich, JSON contract, 5 variants with shared fields
7. **SeedValidationResult** — Aggregator, not a variant type
