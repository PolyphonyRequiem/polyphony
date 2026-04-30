# Phase 1: Polyphony Core Engine

**Epic:** #2581 — Phase 1: Polyphony Core Engine
> **Status**: ✅ Done
**Author:** Copilot (architect agent)

---

## Executive Summary

This plan implements the Polyphony deterministic routing engine — the core state machine that powers conductor SDLC workflows. Given a work item ID and a process configuration, Polyphony inspects the work item's type, state, capabilities, and child hierarchy (all read from the twig SQLite cache) to produce a structured JSON routing decision: which SDLC phase the item is in and what action to take next. Phase 1 delivers working implementations of all three CLI commands (`route`, `validate`, `hierarchy`), backed by a shared routing engine with proper DI, cache access, phase detection, transition validation, and branch-name resolution. The result is a fully deterministic, AOT-compiled binary that conductor workflows can invoke for routing decisions without hardcoding type assumptions.

## Background

### Current State

Polyphony exists as a scaffolded .NET 10 AOT CLI with three stub commands that return placeholder JSON. The project structure is sound:

| Component | State | Location |
|-----------|-------|----------|
| `RouteCommand` | Stub — returns `"phase": "not_implemented"` | `src/Polyphony/Commands/RouteCommand.cs` |
| `ValidateCommand` | Stub — returns `"is_valid": false` | `src/Polyphony/Commands/ValidateCommand.cs` |
| `HierarchyCommand` | Stub — returns `"type": "Unknown"` | `src/Polyphony/Commands/HierarchyCommand.cs` |
| `ProcessConfigLoader` | Working — parses YAML correctly | `src/Polyphony/Configuration/ProcessConfigLoader.cs` |
| `ProcessConfig` model | Working — all config types defined | `src/Polyphony/Configuration/ProcessConfig.cs` |
| Result models | Working — `RouteResult`, `ValidateResult`, `HierarchyResult` | `src/Polyphony/Models/` |
| `PolyphonyJsonContext` | Working — source-generated JSON | `src/Polyphony/PolyphonyJsonContext.cs` |
| DI container | **Not set up** — commands have no constructor injection | `src/Polyphony/Program.cs` |
| Twig cache access | **Not wired** — project references exist but not used | `Polyphony.csproj` |

### Dependencies on Twig.Domain and Twig.Infrastructure

Polyphony already has project references to `Twig.Domain` and `Twig.Infrastructure` (located at `../../../twig2/src/`). These provide the full data access layer:

**Twig.Domain provides:**
- `WorkItem` aggregate — `Id`, `Type` (`WorkItemType`), `Title`, `State`, `ParentId`, `IsSeed`, `Fields`
- `WorkItemType` value object — type-safe wrapper with well-known statics (`Epic`, `Issue`, `Task`)
- `StateCategory` enum — `Proposed`, `InProgress`, `Resolved`, `Completed`, `Removed`, `Unknown`
- `ProcessConfiguration` / `TypeConfig` — transition rules, allowed child types, state entries
- `StateEntry` — `(Name, Category, Color)` for each state in a type's workflow
- `TransitionKind` enum — `None`, `Forward`, `Cut`
- `StateCategoryResolver` — resolves state name → `StateCategory` (with fallback heuristics)
- `StateTransitionService` — evaluates whether a state transition is allowed

**Twig.Infrastructure provides:**
- `SqliteCacheStore` — WAL-mode SQLite connection with schema versioning
- `IWorkItemRepository` / `SqliteWorkItemRepository` — full CRUD for work items
  - `GetByIdAsync(int id)` → `WorkItem?`
  - `GetChildrenAsync(int parentId)` → `IReadOnlyList<WorkItem>`
  - `GetParentChainAsync(int id)` → `IReadOnlyList<WorkItem>`
- `IProcessTypeStore` / `SqliteProcessTypeStore` — cached process type metadata
- `IContextStore` / `SqliteContextStore` — active work item context
- `TwigServiceRegistration.AddTwigCoreServices()` — DI registration extension method
- `TwigPaths` — resolves `.twig/` directory and `cache.db` path

### Conductor SDLC Workflow Context

Polyphony serves as the routing oracle for conductor SDLC workflows (defined in `.github/skills/twig-sdlc/`). The workflow phases are:

| Phase | Agent/Script | Polyphony Role |
|-------|-------------|----------------|
| 0. Preflight | `preflight-check.ps1` | None (pre-routing) |
| 1. Intake | Sonnet agent | Provides `route` to detect current phase |
| 2. Planning | Opus agent | `validate` confirms `begin_planning` is legal |
| 3. Seeding | `seed-from-plan.ps1` | `route` confirms seeding is needed |
| 4. Implementation | Multiple agents | `route` identifies tasks, `validate` checks transitions |
| 5. Close-out | Opus agent | `validate` confirms completion transition |
| 6. Filing | Sonnet agent | None (post-routing) |

### Process Config Semantics

The `.conductor/process-config.yaml` defines the type system:

- **Capabilities**: `plannable` (can have a plan document), `implementable` (can have code changes)
- **Transitions**: Event → target state mappings per type (e.g., `begin_planning` → `Doing`)
- **Branch strategy**: Templates for feature, planning, and PG branches
- **Review policies**: Agent/human review requirements per PR category

### Key Design Principle: P8 — Scripts Over Agents

Per conductor design principle P8, Polyphony is explicitly a **deterministic script** (not an agent). It makes routing decisions based on observable state without LLM involvement. This means:
- All phase detection is rule-based
- No ambiguity in routing decisions
- Same inputs always produce same outputs
- Exit codes drive conductor branching

## Problem Statement

Conductor SDLC workflows currently cannot invoke Polyphony for routing decisions because all three commands are stubs. The `route` command returns `"phase": "not_implemented"`, `validate` always returns `"is_valid": false`, and `hierarchy` returns `"type": "Unknown"`. This means:

1. **No automated phase detection** — Conductor cannot determine whether a work item needs planning, seeding, implementation, or close-out. Routing logic remains hardcoded in agent prompts and shell scripts, tightly coupled to the Basic process template.

2. **No transition validation** — There's no way to programmatically verify that a lifecycle event (e.g., `begin_planning`, `implementation_complete`) is legal for a given work item's current state. Agents must guess or hardcode.

3. **No hierarchy introspection** — Conductor cannot query the work item tree with capability annotations (plannable/implementable), making it impossible to determine which children need work.

4. **No DI or cache access** — The commands don't connect to the twig SQLite cache at all, so they have no access to work item state, hierarchy, or process metadata.

## Goals and Non-Goals

### Goals

1. **Implement the `route` command** — Given a work item ID, determine its SDLC phase (`needs_planning`, `needs_seeding`, `ready_for_implementation`, `in_progress`, `ready_for_completion`, `done`, `removed`) and the next action to take (`plan`, `seed`, `implement`, `monitor`, `close`, `none`).

2. **Implement the `validate` command** — Given a work item ID and a lifecycle event name, determine whether the transition is legal based on the process config and current state, returning the target state if valid.

3. **Implement the `hierarchy` command** — Given a work item ID and depth, walk the hierarchy and return each node annotated with capabilities from the process config.

4. **Establish DI and cache access** — Wire up Twig.Infrastructure services so commands can read from the twig SQLite cache. Commands receive services via constructor injection.

5. **Define exit code semantics** — Establish a clear exit code scheme that conductor scripts can branch on (0 = success, 1 = routing error, 2 = config error, 3 = cache error).

6. **Achieve ≥90% test coverage** on routing logic — Phase detection is the critical path; it must be thoroughly tested with unit tests covering all type/state combinations.

### Non-Goals

- **Write access to the twig cache** — Polyphony is read-only. State transitions are executed by twig, not Polyphony.
- **ADO API calls** — Polyphony reads the local cache only. It never contacts Azure DevOps directly.
- **Plan file parsing** — Polyphony does not read or parse `.plan.md` files. Phase detection is based on work item state and hierarchy, not filesystem artifacts.
- **LLM/AI integration** — Polyphony is fully deterministic (P8).
- **Custom process template support** — Phase 1 targets the Basic process template. Agile/Scrum/CMMI support is Phase 2.
- **Interactive output** — All output is structured JSON to stdout. No Spectre.Console rendering in Phase 1 (the dependency is there for future use).

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-1 | `route` command loads work item from twig cache, determines SDLC phase and action, outputs JSON to stdout |
| FR-2 | `route` command resolves workspace hints (feature branch, PG branch) from process config branch strategy |
| FR-3 | `validate` command checks lifecycle event against process config transitions and current work item state |
| FR-4 | `validate` command evaluates preconditions (e.g., `all_children_complete` requires all children in Completed category) |
| FR-5 | `hierarchy` command walks the work item tree to specified depth, annotating each node with capabilities |
| FR-6 | All commands load process config from `--config` path (default `.conductor/process-config.yaml`) |
| FR-7 | All commands accept `--twig-dir` to locate the twig cache directory (default `.twig`) |
| FR-8 | Exit codes follow a defined scheme: 0 (success), 1 (routing/validation failure), 2 (config error), 3 (cache error) |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | AOT-compatible — no reflection, no dynamic code generation |
| NFR-2 | All JSON serialization through `PolyphonyJsonContext` (source-generated) |
| NFR-3 | Startup-to-output latency < 100ms for cached work items |
| NFR-4 | Cache-only — no network calls |
| NFR-5 | Deterministic — same inputs always produce same outputs |
| NFR-6 | TreatWarningsAsErrors — zero warnings in build |

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────┐
│                    CLI Layer                         │
│  RouteCommand  │  ValidateCommand  │  HierarchyCmd  │
│  (ConsoleAppFramework source-gen commands)           │
└────────┬───────────────┬──────────────────┬──────────┘
         │               │                  │
         ▼               ▼                  ▼
┌─────────────────────────────────────────────────────┐
│                  Routing Engine                      │
│  PhaseDetector  │  TransitionValidator  │  HierWalk │
│  ActionResolver │  BranchNameResolver               │
└────────┬───────────────┬──────────────────┬──────────┘
         │               │                  │
         ▼               ▼                  ▼
┌─────────────────────────────────────────────────────┐
│              Data Access (read-only)                 │
│  IWorkItemRepository  │  IProcessTypeStore           │
│  (from Twig.Infrastructure via DI)                   │
└────────┬───────────────────────────────────┬─────────┘
         │                                   │
         ▼                                   ▼
┌──────────────────┐              ┌─────────────────────┐
│  ProcessConfig   │              │  SQLite Cache        │
│  (YAML file)     │              │  (.twig/cache.db)    │
└──────────────────┘              └─────────────────────┘
```

### Key Components

#### 1. SDLC Phase and Action Types (`Routing/SdlcPhase.cs`, `Routing/SdlcAction.cs`)

Static string constants (not enums, for JSON serialization simplicity and forward compatibility):

```csharp
public static class SdlcPhase
{
    public const string NeedsPlanning = "needs_planning";
    public const string NeedsSeeding = "needs_seeding";
    public const string ReadyForImplementation = "ready_for_implementation";
    public const string InProgress = "in_progress";
    public const string ReadyForCompletion = "ready_for_completion";
    public const string Done = "done";
    public const string Removed = "removed";
    public const string Unknown = "unknown";
}

public static class SdlcAction
{
    public const string Plan = "plan";
    public const string Seed = "seed";
    public const string Implement = "implement";
    public const string Monitor = "monitor";
    public const string Close = "close";
    public const string None = "none";
}
```

**Design Decision:** String constants rather than enums avoid `JsonStringEnumConverter` (which requires reflection in some modes) and allow conductor scripts to match on string values directly. Forward-compatible if new phases are added.

#### 2. PhaseDetector (`Routing/PhaseDetector.cs`)

The core state machine. Determines the SDLC phase for a work item based on its type, capabilities, state, and children:

```csharp
public sealed class PhaseDetector(ProcessConfig processConfig)
{
    public RoutingDecision Detect(WorkItem item, IReadOnlyList<WorkItem> children);
}

public sealed record RoutingDecision
{
    public required string Phase { get; init; }
    public required string Action { get; init; }
    public string? Message { get; init; }
}
```

**Phase Detection Rules:**

| Type Capabilities | State Category | Children | Phase | Action |
|-------------------|---------------|----------|-------|--------|
| plannable | Proposed | none | `needs_planning` | `plan` |
| plannable | Proposed | has children | `needs_planning` | `plan` |
| plannable | InProgress | none | `needs_seeding` | `seed` |
| plannable | InProgress | all Proposed | `ready_for_implementation` | `implement` |
| plannable | InProgress | mixed | `in_progress` | `monitor` |
| plannable | InProgress | all Completed | `ready_for_completion` | `close` |
| implementable only | Proposed | — | `ready_for_implementation` | `implement` |
| implementable only | InProgress | — | `in_progress` | `monitor` |
| any | Completed | — | `done` | `none` |
| any | Removed | — | `removed` | `none` |

For items with both `plannable` and `implementable` capabilities (like Issue in Basic process):
- **Proposed + no children** → `needs_planning` (plan first, then decide on decomposition)
- **InProgress + no children** → `ready_for_implementation` (direct implementation, no decomposition needed)
- **InProgress + children** → follows the children-based logic above

**Design Decision:** Phase detection operates on the Polyphony-level `ProcessConfig` (from YAML), not the Twig.Domain `ProcessConfiguration` (from ADO API cache). This is intentional — Polyphony's routing rules are defined in the repo-local process config, while Twig.Domain's `ProcessConfiguration` represents the ADO process template's raw state machine. The two may diverge (e.g., Polyphony adds `plannable`/`implementable` capabilities that ADO doesn't know about).

However, **state category resolution** uses `StateCategoryResolver` from Twig.Domain, which maps state names (like "To Do", "Doing", "Done") to `StateCategory` values. This is the bridge between ADO state names and Polyphony's phase logic.

#### 3. TransitionValidator (`Routing/TransitionValidator.cs`)

Validates lifecycle event transitions:

```csharp
public sealed class TransitionValidator(ProcessConfig processConfig)
{
    public ValidateResult Validate(WorkItem item, string eventName, IReadOnlyList<WorkItem> children);
}
```

**Validation Logic:**
1. Look up the event in `ProcessConfig.Transitions[item.Type]`
2. If event not found → invalid, "unknown event for type"
3. If event found → target state is `transitions[eventName]`
4. Check preconditions:
   - `all_children_complete`: All children must be in `Completed` state category
   - `begin_planning`: Item must be in `Proposed` state category
   - `begin_implementation`: Item must be in `Proposed` or `InProgress` category
   - `implementation_complete`: Item must be in `InProgress` category
5. Return `ValidateResult` with `IsValid`, target state, and message

#### 4. HierarchyWalker (`Routing/HierarchyWalker.cs`)

Walks the work item tree and annotates nodes:

```csharp
public sealed class HierarchyWalker(ProcessConfig processConfig, IWorkItemRepository repository)
{
    public async Task<HierarchyResult> WalkAsync(int rootId, int maxDepth, CancellationToken ct);
}
```

Recursively loads children via `IWorkItemRepository.GetChildrenAsync()` up to `maxDepth`, annotating each node with `Capabilities` from `ProcessConfig.Types[type].Capabilities`.

#### 5. BranchNameResolver (`Routing/BranchNameResolver.cs`)

Resolves branch name templates from `ProcessConfig.BranchStrategy`:

```csharp
public static class BranchNameResolver
{
    public static WorkspaceHint Resolve(ProcessConfig config, WorkItem rootItem, string slug);
}
```

Substitutes `{root_id}`, `{slug}` in templates like `feature/{root_id}-{slug}`.

#### 6. TwigCacheLocator (`Infrastructure/TwigCacheLocator.cs`)

Locates the twig cache database:

```csharp
public static class TwigCacheLocator
{
    public static string ResolveCachePath(string? twigDir = null);
}
```

Searches for `.twig/cache.db` starting from the specified directory (or CWD), walking up to find the repo root.

#### 7. Exit Codes (`ExitCodes.cs`)

```csharp
public static class ExitCodes
{
    public const int Success = 0;
    public const int RoutingFailure = 1;
    public const int ConfigError = 2;
    public const int CacheError = 3;
}
```

#### 8. DI Registration (`PolyphonyServiceRegistration.cs`)

```csharp
public static class PolyphonyServiceRegistration
{
    public static IServiceCollection AddPolyphonyServices(
        this IServiceCollection services, string configPath, string? twigDir);
}
```

Registers:
- `ProcessConfig` (from YAML via `ProcessConfigLoader`)
- `SqliteCacheStore` (from twig cache path)
- `IWorkItemRepository` (via `SqliteWorkItemRepository`)
- `IProcessTypeStore` (via `SqliteProcessTypeStore`)
- `PhaseDetector`
- `TransitionValidator`
- `HierarchyWalker`

### Data Flow

#### Route Command Flow

```
1. CLI receives: --work-item 1234 --config .conductor/process-config.yaml
2. DI resolves: ProcessConfig, IWorkItemRepository, PhaseDetector
3. Load work item: repository.GetByIdAsync(1234)
   → If null: exit 3 (cache error — work item not in cache)
4. Load children: repository.GetChildrenAsync(1234)
5. Resolve state category: StateCategoryResolver.Resolve(item.State, stateEntries)
6. Detect phase: PhaseDetector.Detect(item, children)
   → Returns RoutingDecision { Phase, Action, Message }
7. Resolve workspace hint: BranchNameResolver.Resolve(config, item, slug)
8. Build RouteResult and serialize via PolyphonyJsonContext
9. Write JSON to stdout, return exit code 0
```

#### Validate Command Flow

```
1. CLI receives: --work-item 1234 --event begin_planning --config ...
2. DI resolves: ProcessConfig, IWorkItemRepository, TransitionValidator
3. Load work item: repository.GetByIdAsync(1234)
4. Load children (for precondition checks): repository.GetChildrenAsync(1234)
5. Validate: TransitionValidator.Validate(item, eventName, children)
6. Build ValidateResult and serialize
7. Write JSON to stdout, return exit code 0 (valid) or 1 (invalid)
```

#### Hierarchy Command Flow

```
1. CLI receives: --work-item 1234 --depth 3 --config ...
2. DI resolves: ProcessConfig, HierarchyWalker
3. Walk: HierarchyWalker.WalkAsync(1234, 3)
   → Recursively loads children, annotates with capabilities
4. Build HierarchyResult tree and serialize
5. Write JSON to stdout, return exit code 0
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| String constants for phases/actions (not enums) | Avoids reflection-based enum serialization; forward-compatible; conductor scripts match on strings |
| Read Polyphony ProcessConfig from YAML, not Twig.Domain ProcessConfiguration | Polyphony's routing concepts (plannable/implementable capabilities) are repo-specific, not ADO-native |
| Use Twig.Domain's `StateCategoryResolver` for state → category | Reuses battle-tested state name mapping; avoids duplicating fallback heuristics |
| Read-only cache access | Polyphony is an observer, not a mutator; state changes go through twig CLI |
| Per-command service resolution (not global static) | Testable, injectable, AOT-safe |
| `--twig-dir` parameter on all commands | Allows worktree scenarios where `.twig/` is not at CWD |

## Dependencies

### External Dependencies

| Dependency | Version | Purpose |
|------------|---------|---------|
| ConsoleAppFramework | 5.7.13 | Source-gen CLI framework |
| Microsoft.Data.Sqlite | 10.0.6 | SQLite cache access |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.11 | Native SQLite binding |
| YamlDotNet | 16.3.0 | Process config parsing |
| Microsoft.Extensions.DependencyInjection | 10.0.6 | DI container |

### Internal Dependencies

| Dependency | Purpose |
|------------|---------|
| Twig.Domain | Work item models, state categories, process configuration types |
| Twig.Infrastructure | SQLite cache access, repository implementations, DI registration |

### Sequencing Constraints

- Twig.Domain and Twig.Infrastructure must be buildable (they are — project references exist and build today)
- The twig SQLite cache must be populated (via `twig sync`) before Polyphony can route

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| ConsoleAppFramework DI integration pattern may differ from assumed API | Medium | Medium | Verify DI integration in PG-1; ConsoleAppFramework v5 supports `IServiceProvider` — validate exact pattern early |
| Twig.Domain/Infrastructure API changes break Polyphony | Low | High | Pin to specific twig2 commit; add integration tests that validate interface compatibility |
| State category resolution edge cases for custom states | Medium | Low | Rely on `StateCategoryResolver.FallbackCategory()` for unknown states; log warnings |
| AOT trimming removes needed types | Low | High | All serialized types registered in `PolyphonyJsonContext`; test with `PublishAot` in CI |

## Open Questions

| # | Question | Severity | Context |
|---|----------|----------|---------|
| OQ-1 | What is the exact ConsoleAppFramework v5 API for DI integration? Is it `ConsoleApp.ServiceProvider = sp` or builder pattern? | Low | Easily validated by reading ConsoleAppFramework source/docs during PG-1. Will not block design. |
| OQ-2 | Should `route` produce different exit codes for different phases (e.g., exit 10 for needs_planning, 11 for needs_seeding) to allow shell-level branching? | Low | Current design uses exit 0 for all successful routes with phase in JSON. Conductor scripts parse JSON. Can be added later if needed. |
| OQ-3 | How should Polyphony handle seed work items (negative IDs) in the hierarchy? | Low | Seeds are virtual items in the twig cache. For Phase 1, treat them as regular work items. Phase 2 can add seed-specific routing if needed. |
| OQ-4 | Should the `route` command accept a `--slug` parameter for branch name resolution, or derive it from the work item title? | Low | For Phase 1, derive from title (lowercase, hyphenated, truncated). Can add `--slug` override later. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Polyphony/Routing/SdlcPhase.cs` | SDLC phase string constants |
| `src/Polyphony/Routing/SdlcAction.cs` | SDLC action string constants |
| `src/Polyphony/Routing/RoutingDecision.cs` | Phase + action result record |
| `src/Polyphony/Routing/PhaseDetector.cs` | Core state machine — determines phase from work item state and children |
| `src/Polyphony/Routing/TransitionValidator.cs` | Lifecycle event validation against process config and preconditions |
| `src/Polyphony/Routing/HierarchyWalker.cs` | Recursive hierarchy traversal with capability annotations |
| `src/Polyphony/Routing/BranchNameResolver.cs` | Branch name template resolution from process config |
| `src/Polyphony/Infrastructure/TwigCacheLocator.cs` | Locates `.twig/cache.db` from working directory or `--twig-dir` |
| `src/Polyphony/Infrastructure/PolyphonyServiceRegistration.cs` | DI registration for all Polyphony + Twig services |
| `src/Polyphony/ExitCodes.cs` | Exit code constants (0=success, 1=routing failure, 2=config error, 3=cache error) |
| `tests/Polyphony.Tests/Routing/PhaseDetectorTests.cs` | Unit tests for phase detection — all type/state/children combinations |
| `tests/Polyphony.Tests/Routing/TransitionValidatorTests.cs` | Unit tests for transition validation |
| `tests/Polyphony.Tests/Routing/HierarchyWalkerTests.cs` | Unit tests for hierarchy walking |
| `tests/Polyphony.Tests/Routing/BranchNameResolverTests.cs` | Unit tests for branch name resolution |
| `tests/Polyphony.Tests/Infrastructure/TwigCacheLocatorTests.cs` | Unit tests for cache location logic |
| `tests/Polyphony.Tests/TestFixtures/WorkItemBuilder.cs` | Test helper — fluent builder for `WorkItem` instances |
| `tests/Polyphony.Tests/TestFixtures/ProcessConfigBuilder.cs` | Test helper — fluent builder for `ProcessConfig` instances |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Polyphony/Program.cs` | Add DI setup, configure `ConsoleApp` with service provider |
| `src/Polyphony/Commands/RouteCommand.cs` | Replace stub with full implementation using injected `PhaseDetector`, `BranchNameResolver` |
| `src/Polyphony/Commands/ValidateCommand.cs` | Replace stub with full implementation using injected `TransitionValidator` |
| `src/Polyphony/Commands/HierarchyCommand.cs` | Replace stub with full implementation using injected `HierarchyWalker` |
| `src/Polyphony/PolyphonyJsonContext.cs` | Add `RoutingDecision` and any new serialized types |
| `src/Polyphony/Models/RouteResult.cs` | Potentially add fields (e.g., `Capabilities`, `ChildSummary`) |
| `tests/Polyphony.Tests/Polyphony.Tests.csproj` | Add project reference to Twig.Domain (for test builders) |

---

## ADO Work Item Structure

### Issue 1: Core Infrastructure — DI, Cache Access, and Routing Types

**Goal:** Establish the foundational infrastructure that all commands depend on: DI container setup, twig cache connectivity, SDLC phase/action type definitions, and exit code semantics.

**Prerequisites:** None (first Issue to implement)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-1.1 | Define SdlcPhase, SdlcAction string constants and RoutingDecision record | `src/Polyphony/Routing/SdlcPhase.cs`, `SdlcAction.cs`, `RoutingDecision.cs` | 1-2 hours |
| T-1.2 | Define ExitCodes constants | `src/Polyphony/ExitCodes.cs` | 0.5 hours |
| T-1.3 | Create TwigCacheLocator to find `.twig/cache.db` | `src/Polyphony/Infrastructure/TwigCacheLocator.cs`, `tests/.../TwigCacheLocatorTests.cs` | 1-2 hours |
| T-1.4 | Create PolyphonyServiceRegistration and wire DI in Program.cs | `src/Polyphony/Infrastructure/PolyphonyServiceRegistration.cs`, `src/Polyphony/Program.cs` | 2-3 hours |
| T-1.5 | Create test fixtures (WorkItemBuilder, ProcessConfigBuilder) | `tests/.../TestFixtures/WorkItemBuilder.cs`, `ProcessConfigBuilder.cs` | 1-2 hours |

**Acceptance Criteria:**
- [ ] `dotnet build` succeeds with zero warnings
- [ ] DI container resolves `IWorkItemRepository`, `ProcessConfig`, and `PhaseDetector`
- [ ] `TwigCacheLocator` finds cache.db from CWD and from explicit `--twig-dir`
- [ ] `ExitCodes`, `SdlcPhase`, `SdlcAction` constants are defined and compile
- [ ] Test builders create valid `WorkItem` and `ProcessConfig` instances

### Issue 2: Phase Detection and Routing Engine

**Goal:** Implement the core state machine that determines SDLC phase and next action for any work item type, plus branch name resolution for workspace hints.

**Prerequisites:** Issue 1 (DI, types, test fixtures)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-2.1 | Implement PhaseDetector with rules for all type/state/children combinations | `src/Polyphony/Routing/PhaseDetector.cs` | 3-4 hours |
| T-2.2 | Implement BranchNameResolver for workspace hint generation | `src/Polyphony/Routing/BranchNameResolver.cs`, `tests/.../BranchNameResolverTests.cs` | 1-2 hours |
| T-2.3 | Add comprehensive PhaseDetector unit tests (all paths in the rules table) | `tests/.../Routing/PhaseDetectorTests.cs` | 3-4 hours |
| T-2.4 | Update PolyphonyJsonContext with RoutingDecision type | `src/Polyphony/PolyphonyJsonContext.cs` | 0.5 hours |

**Acceptance Criteria:**
- [ ] PhaseDetector correctly classifies all 8+ phase/action combinations from the rules table
- [ ] Epic with no children → `needs_planning` / `plan`
- [ ] Epic with Proposed children → `ready_for_implementation` / `implement`
- [ ] Epic with all Done children → `ready_for_completion` / `close`
- [ ] Task in Proposed → `ready_for_implementation` / `implement`
- [ ] BranchNameResolver correctly substitutes `{root_id}` and `{slug}` in templates
- [ ] All tests pass with `dotnet test`

### Issue 3: Command Implementations

**Goal:** Wire the routing engine into all three CLI commands with proper error handling, exit codes, and structured JSON output.

**Prerequisites:** Issue 2 (PhaseDetector, TransitionValidator, HierarchyWalker)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-3.1 | Implement TransitionValidator with precondition checking | `src/Polyphony/Routing/TransitionValidator.cs`, `tests/.../TransitionValidatorTests.cs` | 2-3 hours |
| T-3.2 | Implement HierarchyWalker with recursive traversal | `src/Polyphony/Routing/HierarchyWalker.cs`, `tests/.../HierarchyWalkerTests.cs` | 2-3 hours |
| T-3.3 | Rewrite RouteCommand with full routing logic and DI | `src/Polyphony/Commands/RouteCommand.cs` | 1-2 hours |
| T-3.4 | Rewrite ValidateCommand with TransitionValidator and DI | `src/Polyphony/Commands/ValidateCommand.cs` | 1-2 hours |
| T-3.5 | Rewrite HierarchyCommand with HierarchyWalker and DI | `src/Polyphony/Commands/HierarchyCommand.cs` | 1-2 hours |
| T-3.6 | Add end-to-end command tests with in-memory SQLite | `tests/.../Commands/RouteCommandTests.cs`, `ValidateCommandTests.cs`, `HierarchyCommandTests.cs` | 3-4 hours |

**Acceptance Criteria:**
- [ ] `polyphony route --work-item 1234` outputs valid JSON with phase, action, and workspace_hint
- [ ] `polyphony validate --work-item 1234 --event begin_planning` outputs valid JSON with is_valid and target_state
- [ ] `polyphony hierarchy --work-item 1234 --depth 3` outputs valid JSON tree with capabilities per node
- [ ] All commands return appropriate exit codes (0 on success, 1-3 on errors)
- [ ] Missing work item returns exit code 3 with error JSON
- [ ] Invalid config path returns exit code 2 with error JSON
- [ ] `dotnet build` succeeds, `dotnet test` passes all tests
- [ ] `dotnet publish` with AOT succeeds without trim warnings

---

## PR Groups

### PG-1: Core Infrastructure and Routing Types

**Scope:** Issue 1 (T-1.1 through T-1.5)
**Classification:** Deep — foundational DI and infrastructure setup
**Estimated LoC:** ~400
**Files:** ~10

**What's in this PR:**
- SDLC phase/action constants, RoutingDecision record, ExitCodes
- TwigCacheLocator
- PolyphonyServiceRegistration (DI wiring)
- Program.cs DI setup
- Test fixtures (builders)
- Unit tests for TwigCacheLocator

**Reviewability:** Self-contained foundation. No existing behavior changes — all new files plus minimal Program.cs modification. Reviewer can verify DI wiring and type definitions independently.

**Successors:** PG-2

### PG-2: Phase Detection Engine

**Scope:** Issue 2 (T-2.1 through T-2.4)
**Classification:** Deep — core algorithmic logic
**Estimated LoC:** ~500
**Files:** ~5

**What's in this PR:**
- PhaseDetector implementation (the core state machine)
- BranchNameResolver
- Comprehensive PhaseDetector unit tests (all rule paths)
- BranchNameResolver unit tests
- PolyphonyJsonContext updates

**Reviewability:** The heart of Polyphony. Reviewer should focus on the phase detection rules table and verify each test case maps to a real SDLC scenario. Pure logic — no I/O or side effects.

**Predecessors:** PG-1
**Successors:** PG-3

### PG-3: Command Implementations

**Scope:** Issue 3 (T-3.1 through T-3.6)
**Classification:** Deep — integration of all components
**Estimated LoC:** ~700
**Files:** ~12

**What's in this PR:**
- TransitionValidator with preconditions
- HierarchyWalker with recursive traversal
- RouteCommand, ValidateCommand, HierarchyCommand rewrites
- End-to-end command tests
- TransitionValidator and HierarchyWalker unit tests

**Reviewability:** Largest PR but structurally straightforward — each command follows the same pattern (resolve services → load data → compute → serialize). Reviewer can evaluate each command independently.

**Predecessors:** PG-2

---

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|-------------|--------------|------|
| PG-1 | Core Infrastructure and Routing Types | Issue 1: T-1.1, T-1.2, T-1.3, T-1.4, T-1.5 | None | deep |
| PG-2 | Phase Detection Engine | Issue 2: T-2.1, T-2.2, T-2.3, T-2.4 | PG-1 | deep |
| PG-3 | Command Implementations | Issue 3: T-3.1, T-3.2, T-3.3, T-3.4, T-3.5, T-3.6 | PG-2 | deep |

### Execution Order

**PG-1 → PG-2 → PG-3** (strictly sequential)

1. **PG-1** establishes the foundational layer: SDLC type constants, exit codes, DI wiring, twig cache locator, test fixtures. All new files plus minimal `Program.cs` change. No existing behavior modified. Once merged, the project builds with DI configured and all type definitions in place.

2. **PG-2** builds the core algorithmic heart: `PhaseDetector` state machine and `BranchNameResolver`. Depends only on types from PG-1. Pure logic with comprehensive unit tests covering every rule-table path. No I/O or command changes — the routing engine exists independently of the commands.

3. **PG-3** integrates everything into the CLI: `TransitionValidator`, `HierarchyWalker`, and all three command rewrites. Also includes end-to-end command tests using in-memory SQLite. The AOT publish target is validated here as the final gate.

### Validation Strategy per PG

**PG-1 Validation:**
- `dotnet build` with zero warnings (TreatWarningsAsErrors)
- `dotnet test` — `TwigCacheLocatorTests` pass
- DI container resolves without exceptions in a smoke test
- `ExitCodes`, `SdlcPhase`, `SdlcAction` constants compile and are accessible

**PG-2 Validation:**
- `dotnet build` with zero warnings
- `dotnet test` — `PhaseDetectorTests` cover all 8+ rows of the phase detection rules table; `BranchNameResolverTests` verify template substitution
- No command behavior changes (route/validate/hierarchy still return stubs — that's expected)

**PG-3 Validation:**
- `dotnet build` with zero warnings
- `dotnet test` — `TransitionValidatorTests`, `HierarchyWalkerTests`, and end-to-end `RouteCommandTests`/`ValidateCommandTests`/`HierarchyCommandTests` all pass
- `dotnet publish` with AOT succeeds with no trim warnings
- Smoke-test outputs: valid JSON from all three commands against a real twig cache

---

## References

- [Conductor Design Principles](../../.github/skills/conductor-design/SKILL.md) — Especially P5 (type-agnostic), P8 (scripts over agents)
- [Twig SDLC Workflow](../../.github/skills/twig-sdlc/SKILL.md) — Full workflow definition
- [Process Config](../../.conductor/process-config.yaml) — Type capabilities, transitions, branch strategy
- [Work Item Type Definitions](../../.conductor/work-item-types/) — Epic, Issue, Task definitions
