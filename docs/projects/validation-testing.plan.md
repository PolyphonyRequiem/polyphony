# Phase 4: Validation & Testing

**Epic:** #2584 — Phase 4: Validation & Testing
> **Status**: ✅ Done
**Author:** Copilot (architect agent)
**Revision:** 1 (Incorporated user feedback on open questions)

---

## Executive Summary

This plan establishes the quality gate for the type-agnostic Polyphony system before cross-repo adoption. It delivers comprehensive xUnit tests for the Polyphony .NET engine (state machine transitions, hierarchy discovery, transition validation, multi-facet routing), integration tests against pre-seeded SQLite databases, workflow YAML validation via `conductor validate`, and cross-process-template verification for Agile (Epic/User Story/Task), Scrum (Epic/PBI/Task), and CMMI (Scenario/Deliverable/Task) configurations. All tests run deterministically with `dotnet test` and Pester — no live ADO calls. Success means every process template routes correctly through the same codebase without code changes, proving the system is ready for production use across repositories.

## Background

### Current State

Phases 1–3 delivered a working type-agnostic SDLC system:

| Phase | Deliverable | Status |
|-------|-------------|--------|
| Phase 1 | Polyphony Core Engine — `route`, `validate`, `hierarchy` commands | ✅ Done |
| Phase 2 | Generic Workflow Scripts — `detect-state`, `pg-router`, `impl-router`, etc. | ✅ Done |
| Phase 3 | Workflow YAML Refactoring — 9 conductor YAMLs, recursive planning, parallel PG execution | ✅ Done |

### Existing Test Coverage

The test suite currently has **119+ test cases** across 21 test files:

| Layer | Files | Tests | Coverage |
|-------|-------|-------|----------|
| Commands (E2E) | 4 | 36 | Route, Validate, Hierarchy commands with in-memory SQLite |
| Routing (Unit) | 6 | 58 | PhaseDetector, TransitionValidator, HierarchyWalker, BranchNameResolver |
| Infrastructure | 2 | 16 | DI registration, TwigCacheLocator |
| Models/Serialization | 3 | 11 | Exit codes, JSON round-trip, tags |
| Test Fixtures | 2 | 9 | WorkItemBuilder, ProcessConfigBuilder |

PowerShell scripts have Pester test suites:

| Script | Test File | Key Scenarios |
|--------|-----------|---------------|
| `detect-state.ps1` | 43 KB test file | Phase detection, intent conflicts, plan discovery |
| `impl-router.ps1` | 367 KB test file | 4 fallback levels, branch naming |
| `pg-router.ps1` | 28.7 KB test file | PG grouping, PR status, stale branch |
| `child-router.ps1` | 13 tests | Plannable discovery, error handling |
| Lint scripts | 6 test files | Type-agnostic compliance, routing, PR flows |

### What's Missing (Gap Analysis)

The existing tests cover the **Basic** process template (Epic/Issue/Task) thoroughly but have these gaps:

1. **No cross-process-template tests** — Only Basic template is tested. No Agile (User Story), Scrum (PBI), or CMMI (Scenario/Deliverable) configs exist as test fixtures.
2. **No 2-tier or 4-tier hierarchy tests** — All tests assume 3-tier (Epic→Issue→Task). No coverage for 2-tier (Issue→Task) or 4-tier (Epic→Feature→Issue→Task) patterns.
3. **No depth budget enforcement tests** — No tests verify that >4 plannable levels return an error.
4. **No freshness enforcement tests** — Cache staleness is not tested (delegated to Twig, but the integration contract is unverified).
5. **No workflow YAML validation** — `conductor validate` has not been run against the refactored YAMLs.
6. **No integration tests with pre-seeded SQLite** — All E2E tests use in-memory databases, never disk-based pre-seeded databases.
7. **No composite hierarchy routing tests** — plannable→plannable→implementable chains not explicitly tested across process configs.

### Test Infrastructure

| Component | Technology | Notes |
|-----------|-----------|-------|
| Framework | xUnit 2.9.3 | `[Fact]`, `[Theory]` + `[InlineData]` |
| Assertions | Shouldly 4.3.0 | Fluent: `.ShouldBe()`, `.ShouldContain()` |
| Mocking | NSubstitute 5.3.0 | `Substitute.For<IWorkItemRepository>()` |
| E2E base | `CommandTestBase` | In-memory SQLite, console capture, thread-safe |
| Fixtures | `WorkItemBuilder`, `ProcessConfigBuilder` | Fluent builders with defaults |
| Build | .NET 11.0, `TreatWarningsAsErrors=true` | AOT-compatible, central package management |

## Problem Statement

The Polyphony type-agnostic routing engine has been built and integrated into conductor workflows, but it has only been validated against one process template (Basic). Before the system can be adopted by other repositories that may use Agile, Scrum, or CMMI processes, we must prove:

1. **State machine correctness** — Transitions work for every supported process template's state names and facets.
2. **Hierarchy flexibility** — 2-tier, 3-tier, and 4-tier hierarchies are discovered and routed correctly.
3. **Configuration-driven behavior** — Changing the process config YAML is sufficient to support a new template; no code changes needed.
4. **Workflow integrity** — All refactored conductor YAMLs pass validation and produce correct exit codes.
5. **Edge case safety** — Depth budgets, missing mappings, and stale caches produce deterministic error behavior.

Without this validation phase, adopting the system in non-Basic repos risks silent routing failures, incorrect state transitions, or workflow crashes.

## Goals and Non-Goals

### Goals

1. **Cross-template state machine verification** — PhaseDetector and TransitionValidator produce correct results for Basic, Agile, Scrum, and CMMI process templates.
2. **Hierarchy tier coverage** — Tests cover 2-tier (parent→leaf), 3-tier (grandparent→parent→leaf), and 4-tier hierarchies.
3. **Integration validation** — Commands work end-to-end against pre-seeded SQLite databases with realistic hierarchies.
4. **Workflow YAML validation** — All 9 conductor YAMLs pass `conductor validate`.
5. **Regression safety** — A new test suite that runs in CI and catches regressions before merge.
6. **Zero code changes for new templates** — Prove that adding a process config is sufficient for a new template.

### Non-Goals

1. **Live ADO integration** — No tests will call real ADO APIs. All data is pre-seeded.
2. **Performance benchmarking** — No load tests or latency measurements (deferred to Phase 5).
3. **UI/UX testing** — No testing of console output formatting beyond JSON correctness.
4. **Conductor runtime testing** — We validate YAML structure, not conductor execution. Dry-run coverage is limited to what `conductor validate` provides.
5. **A/B comparison tooling** — Building automated old-vs-new comparison infrastructure is out of scope; manual verification suffices.

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-1 | State machine tests cover all 8 phases × 4 process templates (Basic, Agile, Scrum, CMMI) |
| FR-2 | Transition validation tests cover all event types × 4 templates with legal/illegal permutations |
| FR-3 | Hierarchy tests cover 2-tier, 3-tier, and 4-tier trees with facet annotation |
| FR-4 | Integration tests use pre-seeded SQLite databases (not in-memory) |
| FR-5 | Cross-process config fixtures exist as YAML files loadable by `ProcessConfigLoader` |
| FR-6 | Depth budget tests verify >4 plannable levels produce an error |
| FR-7 | All 9 workflow YAMLs pass `conductor validate` |
| FR-8 | At least one non-Basic process template routes correctly end-to-end |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | All tests pass with `dotnet test` in < 30 seconds |
| NFR-2 | Tests are deterministic — no flaky behavior from timing, ordering, or external state |
| NFR-3 | Test fixtures are self-documenting — each process template config includes comments |
| NFR-4 | No new runtime dependencies — test-only packages are acceptable |
| NFR-5 | Build with `TreatWarningsAsErrors=true` — no warnings introduced |

## Proposed Design

### Architecture Overview

The testing strategy is layered to match the system architecture:

```
┌─────────────────────────────────────────────────────────────────┐
│  Layer 4: Workflow Validation (conductor validate + dry-run)     │
│  - YAML structural validation                                    │
│  - Script contract verification                                  │
├─────────────────────────────────────────────────────────────────┤
│  Layer 3: Integration Tests (pre-seeded SQLite + commands)       │
│  - Full routing scenario: Epic→Issue→Task                        │
│  - RouteCommand + HierarchyCommand output parsing                │
│  - Cross-process-template end-to-end                             │
├─────────────────────────────────────────────────────────────────┤
│  Layer 2: Cross-Process Unit Tests (PhaseDetector + Validator)   │
│  - Process config fixtures: Agile, Scrum, CMMI                   │
│  - State machine transitions per template                        │
│  - Facet-based routing per template                         │
├─────────────────────────────────────────────────────────────────┤
│  Layer 1: Extended Unit Tests (edge cases + depth budget)         │
│  - 2-tier, 3-tier, 4-tier hierarchy tests                        │
│  - Depth budget enforcement (>4 plannable levels)                │
│  - Missing mapping = hard error                                  │
│  - Multi-facet routing (plannable+implementable)            │
└─────────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. Process Config Test Fixtures (YAML files)

New YAML fixture files, one per process template, stored in `tests/Polyphony.Tests/TestFixtures/ProcessConfigs/`:

| File | Process Template | Types | Hierarchy |
|------|-----------------|-------|-----------|
| `basic.yaml` | Basic | Epic→Issue→Task | 3-tier |
| `agile.yaml` | Agile | Epic→User Story→Task | 3-tier |
| `scrum.yaml` | Scrum | Epic→Product Backlog Item→Task | 3-tier |
| `cmmi.yaml` | CMMI | Epic→Requirement→Task | 3-tier |

Each config defines: `types` with facets, `transitions` with state names matching the ADO process template's actual states, and `branch_strategy`.

**State→Category mapping is configurable and semantically mapped.** `StateCategoryResolver.Resolve()` uses a two-tier strategy:
1. **Primary:** Authoritative `StateEntry` data from `ProcessTypeRecord.States` (sourced from ADO via twig's process type store)
2. **Fallback:** Hardcoded heuristics for common state names when entries are not available

For tests, we leverage both tiers:
- **Unit tests** (PhaseDetector, TransitionValidator): Use `WorkItemBuilder` with state strings. `StateCategoryResolver` is called with `entries: null`, so the hardcoded fallback handles mapping. This works because the fallback already covers all four templates' state names.
- **Integration tests** (Commands): Seed `ProcessTypeRecord` entries into the test database so the full authoritative resolution path is exercised.

**Verified state name coverage in `StateCategoryResolver` fallback:**

| Template | Proposed | InProgress | Resolved | Completed | Removed |
|----------|----------|------------|----------|-----------|---------|
| Basic | To Do ✅ | Doing ✅ | — | Done ✅ | Removed ✅ |
| Agile | New ✅ | Active ✅ | Resolved ✅ | Closed ✅ | Removed ✅ |
| Scrum | New ✅ | Committed ✅ / In Progress ✅ | — | Done ✅ | Removed ✅ |
| CMMI | Proposed ✅ | Active ✅ | Resolved ✅ | Closed ✅ | Removed ✅ |

All state names are handled by the hardcoded fallback. No code changes needed.

**`WorkItemType.Parse()` accepts arbitrary type names.** Investigation confirmed it has well-known constants for cross-process types (User Story, Product Backlog Item, Requirement, etc.) but accepts any non-empty string. Test fixtures can use any type name without restriction.

#### 2. ProcessConfigFixture Helper

A static helper class that loads process configs from embedded YAML files:

```csharp
public static class ProcessConfigFixture
{
    public static ProcessConfig Basic() => Load("basic.yaml");
    public static ProcessConfig Agile() => Load("agile.yaml");
    public static ProcessConfig Scrum() => Load("scrum.yaml");
    public static ProcessConfig Cmmi() => Load("cmmi.yaml");
}
```

**Note:** For unit tests, `StateCategoryResolver` uses its hardcoded fallback (entries=null) which already covers all four templates' state names. For integration tests that exercise the full command pipeline, `ProcessTypeRecord` entries with `StateEntry` data should be seeded into the SQLite database so the authoritative resolution path is validated. The YAML fixtures define process config (types, facets, transitions); state→category mapping comes from `StateCategoryResolver` automatically.

#### 3. Cross-Process PhaseDetector Tests

A `[Theory]`-based test class that parameterizes across process templates:

- Each template provides its own state names (e.g., "New" vs "To Do" for Proposed)
- Tests cover all 8 SDLC phases per template
- Uses `[MemberData]` to generate test cases from all templates

#### 4. Cross-Process TransitionValidator Tests

Theory-based tests covering:
- Legal transitions per template (e.g., `begin_planning` on Proposed item)
- Illegal transitions per template (e.g., `begin_planning` on Active item)
- Missing event mappings returning structured errors
- All precondition types (all_children_complete, begin_planning, etc.)

#### 5. Hierarchy Tier Tests

Extended `HierarchyWalkerTests` covering:
- **2-tier**: Issue→Task (no grandparent)
- **3-tier**: Epic→Issue→Task (standard)
- **4-tier**: Epic→Feature→Issue→Task (deep hierarchy)
- Facet annotation correct at each level
- Depth budget: >4 plannable levels returns error

#### 6. Integration Test Infrastructure

A new `IntegrationTestBase` extending `CommandTestBase`:
- Seeds realistic multi-level hierarchies
- Verifies full routing pipeline (Route→Validate→Hierarchy)
- Tests cross-process routing end-to-end
- Validates JSON output contracts

#### 7. Workflow YAML Validation

A Pester test suite that:
- Runs `conductor validate` against each of the 9 workflow YAMLs
- Validates input/output schemas
- Checks for broken references between workflows

### Data Flow

```
Process Config YAML ──→ ProcessConfigLoader ──→ ProcessConfig
                                                      │
Test Work Items ──→ WorkItemBuilder ──→ SQLite/Mock ──→│
                                                      ▼
                                              PhaseDetector.Detect()
                                              TransitionValidator.Validate()
                                              HierarchyWalker.WalkAsync()
                                                      │
                                                      ▼
                                              RouteResult / ValidateResult / HierarchyResult
                                                      │
                                                      ▼
                                              Assertions (Shouldly)
```

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| YAML fixtures vs builder-only | YAML files loaded by `ProcessConfigLoader` | Tests the real loader path; configs are self-documenting and reviewable |
| Pre-seeded SQLite vs in-memory | Pre-seeded for integration, in-memory for unit | Integration tests prove disk I/O path; unit tests stay fast |
| `[Theory]` parameterization | `[MemberData]` with template enum | Enables adding templates without test code changes |
| Depth budget in PhaseDetector | Not in PhaseDetector — depth guard is in `depth-guard.ps1` | Follows existing architecture; test the script, not the engine |
| Workflow validation tool | `conductor validate` CLI | Uses the actual validation tool rather than hand-parsing YAML |
| State→Category mapping | Leverage `StateCategoryResolver` fallback for unit tests; seed `ProcessTypeRecord` entries for integration | Unit tests stay simple; integration tests exercise the authoritative ADO-sourced resolution path |
| Validation gap tracking | Note gaps for postmortem via close-out workflow | Per user direction: discovered validation gaps should feed into postmortem observations rather than blocking this phase |

## Dependencies

### External Dependencies

| Dependency | Version | Purpose |
|-----------|---------|---------|
| xUnit | 2.9.3 | Test framework (existing) |
| Shouldly | 4.3.0 | Fluent assertions (existing) |
| NSubstitute | 5.3.0 | Mocking (existing) |
| conductor CLI | Latest | Workflow YAML validation |

### Internal Dependencies

| Dependency | Purpose |
|-----------|---------|
| Polyphony CLI (Phase 1) | System under test |
| Workflow scripts (Phase 2) | Script-level testing |
| Workflow YAMLs (Phase 3) | Validation targets |
| `Twig.Domain` + `Twig.Infrastructure` | Work item model, SQLite repository |

### Sequencing Constraints

- Phases 1–3 must be complete before this work begins
- The `conductor` CLI must be available for workflow validation (Issue 4.3)
- Any validation gaps discovered during testing should be tracked as postmortem observations

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| ADO state names vary by org customization | Medium | High | Document tested state names; `StateCategoryResolver` fallback covers all 4 templates. Custom org states need config-level `StateEntry` data from ADO sync. |
| `conductor validate` not available in CI | Low | Medium | Make workflow validation tests skippable via env flag; validate locally as fallback. Note gaps for postmortem. |
| Pre-seeded SQLite schema drift from Twig.Infrastructure | Low | Medium | Generate test databases programmatically using `SqliteCacheStore`, not static `.db` files |
| CMMI "Resolved" state creates unexpected routing | Low | Medium | `StateCategoryResolver` already maps "Resolved" → `StateCategory.Resolved`. PhaseDetector handles Resolved category. Add explicit test case. |

## Open Questions

| # | Question | Severity | Status | Resolution |
|---|----------|----------|--------|------------|
| 1 | Does `StateCategoryResolver` correctly map Agile/Scrum/CMMI state names? | ~~Major~~ | **Resolved** | Yes. The hardcoded fallback maps "New"→Proposed, "Active"→InProgress, "Committed"→InProgress, "Proposed"→Proposed, "Closed"→Completed, "Resolved"→Resolved. All four templates' states are covered. State mappings are also configurable via `ProcessTypeRecord.States` entries from twig's process type store. |
| 2 | Does `WorkItemType.Parse()` accept arbitrary type names? | ~~Major~~ | **Resolved** | Yes. `WorkItemType` is a readonly record struct that accepts any non-empty string. It has well-known constants for User Story, Product Backlog Item, Requirement, etc. but treats them as case-normalization hints, not restrictions. |
| 3 | Is `conductor validate` available in CI? | ~~Moderate~~ | **Resolved** | Yes, per user confirmation. If validation gaps are discovered during testing, they should be noted for the postmortem (via the close-out workflow's observation filer). |
| 4 | Should freshness enforcement be tested at Polyphony or Twig level? | Low | Open | Freshness is managed by Twig infrastructure. Polyphony is read-only. Testing at Twig level is more appropriate. |
| 5 | Are there CMMI-specific states beyond Proposed/Active/Closed? | Low | **Resolved** | CMMI has "Resolved" as a distinct state. `StateCategoryResolver` maps it to `StateCategory.Resolved`. PhaseDetector already handles the Resolved category. Explicit test case added in Issue 4.4. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `tests/Polyphony.Tests/TestFixtures/ProcessConfigs/basic.yaml` | Basic process template test config (Epic/Issue/Task) |
| `tests/Polyphony.Tests/TestFixtures/ProcessConfigs/agile.yaml` | Agile process template test config (Epic/User Story/Task) |
| `tests/Polyphony.Tests/TestFixtures/ProcessConfigs/scrum.yaml` | Scrum process template test config (Epic/Product Backlog Item/Task) |
| `tests/Polyphony.Tests/TestFixtures/ProcessConfigs/cmmi.yaml` | CMMI process template test config (Epic/Requirement/Task) |
| `tests/Polyphony.Tests/TestFixtures/ProcessConfigFixture.cs` | Static helper to load YAML fixtures via `ProcessConfigLoader` |
| `tests/Polyphony.Tests/Routing/CrossProcessPhaseDetectorTests.cs` | Cross-template PhaseDetector Theory tests |
| `tests/Polyphony.Tests/Routing/CrossProcessTransitionValidatorTests.cs` | Cross-template TransitionValidator Theory tests |
| `tests/Polyphony.Tests/Routing/HierarchyTierTests.cs` | 2-tier, 3-tier, 4-tier hierarchy tests |
| `tests/Polyphony.Tests/Routing/DepthBudgetTests.cs` | >4 plannable levels returns error |
| `tests/Polyphony.Tests/Commands/CrossProcessRouteCommandTests.cs` | E2E routing across process templates |
| `tests/Polyphony.Tests/Commands/CrossProcessValidateCommandTests.cs` | E2E validation across process templates |
| `tests/Polyphony.Tests/Commands/IntegrationScenarioTests.cs` | Full routing scenario: Epic→Issues→Tasks lifecycle |
| `tests/lint-conductor-validate.ps1` | Pester: `conductor validate` on all 9 workflow YAMLs |
| `tests/lint-conductor-validate.Tests.ps1` | Pester tests for the validation lint script |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `tests/Polyphony.Tests/Polyphony.Tests.csproj` | Add `<EmbeddedResource>` or `<Content>` items for YAML fixtures; ensure copy-to-output |
| `tests/Polyphony.Tests/TestFixtures/ProcessConfigBuilder.cs` | Minor: add `WithFilingEligible()` and `WithMaxNestingDepth()` methods if needed |

## ADO Work Item Structure

**Epic #2584** — Phase 4: Validation & Testing

---

### Issue 4.1: Polyphony Unit Tests

**Goal:** Comprehensive unit test coverage for the Polyphony routing engine across all process templates, hierarchy tiers, and edge cases.

**Prerequisites:** None (first issue to implement)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 4.1.1 | Create YAML process config fixtures for Basic, Agile, Scrum, CMMI | `tests/Polyphony.Tests/TestFixtures/ProcessConfigs/*.yaml` | S |
| 4.1.2 | Create `ProcessConfigFixture` helper class to load YAML fixtures | `tests/Polyphony.Tests/TestFixtures/ProcessConfigFixture.cs`, `.csproj` | S |
| 4.1.3 | Create cross-process `PhaseDetectorTests` with `[Theory]` parameterization across 4 templates | `tests/Polyphony.Tests/Routing/CrossProcessPhaseDetectorTests.cs` | M |
| 4.1.4 | Create cross-process `TransitionValidatorTests` covering legal/illegal/missing transitions per template | `tests/Polyphony.Tests/Routing/CrossProcessTransitionValidatorTests.cs` | M |
| 4.1.5 | Create `HierarchyTierTests` for 2-tier, 3-tier, 4-tier hierarchies with facet annotation | `tests/Polyphony.Tests/Routing/HierarchyTierTests.cs` | M |
| 4.1.6 | Create `DepthBudgetTests` verifying >4 plannable levels produce error behavior | `tests/Polyphony.Tests/Routing/DepthBudgetTests.cs` | S |

**Acceptance Criteria:**
- [ ] 4 YAML process config fixtures exist and load without error
- [ ] PhaseDetector tests pass for all 8 phases × 4 process templates
- [ ] TransitionValidator tests pass for all event types × 4 templates (legal + illegal)
- [ ] Hierarchy tests pass for 2-tier, 3-tier, and 4-tier trees
- [ ] Depth budget test verifies >4 plannable levels produce correct behavior
- [ ] All tests pass with `dotnet test` and zero warnings

---

### Issue 4.2: Integration Tests

**Goal:** Prove Polyphony commands work end-to-end with realistic pre-seeded data and cross-process routing.

**Prerequisites:** Issue 4.1 (config fixtures and unit tests established)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 4.2.1 | Create `CrossProcessRouteCommandTests` — E2E routing per template with in-memory SQLite | `tests/Polyphony.Tests/Commands/CrossProcessRouteCommandTests.cs` | M |
| 4.2.2 | Create `CrossProcessValidateCommandTests` — E2E validation per template | `tests/Polyphony.Tests/Commands/CrossProcessValidateCommandTests.cs` | M |
| 4.2.3 | Create `IntegrationScenarioTests` — Full lifecycle: seed Epic→Issues→Tasks, route through NeedsPlanning→ReadyForImpl→InProgress→ReadyForCompletion→Done | `tests/Polyphony.Tests/Commands/IntegrationScenarioTests.cs` | L |
| 4.2.4 | Verify JSON output contracts (snake_case, null omission, exit codes) in integration tests | Within `IntegrationScenarioTests.cs` | S |

**Acceptance Criteria:**
- [ ] Route command returns correct phase for each process template
- [ ] Validate command returns correct validity for each template's transitions
- [ ] Full lifecycle scenario walks an Epic through all phases
- [ ] JSON output matches documented contract (snake_case, null omission)
- [ ] Exit codes match `ExitCodes` constants (0, 1, 2, 3)

---

### Issue 4.3: Workflow Validation

**Goal:** Validate all 9 conductor workflow YAMLs pass structural validation and scripts produce correct output contracts.

**Prerequisites:** Issue 4.1 (to establish test infrastructure patterns)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 4.3.1 | Create `lint-conductor-validate.ps1` — runs `conductor validate` on all 9 workflow YAMLs | `tests/lint-conductor-validate.ps1` | S |
| 4.3.2 | Create Pester tests for the workflow validation lint script | `tests/lint-conductor-validate.Tests.ps1` | S |
| 4.3.3 | Verify script output contracts match workflow expectations (JSON schema, required fields) | Within existing Pester test suites | M |
| 4.3.4 | Run Basic process template end-to-end validation: twig repo workflows route correctly | Manual verification + documented results | M |

**Acceptance Criteria:**
- [ ] `conductor validate` passes for all 9 workflow YAMLs
- [ ] Lint script exits 0 when all YAMLs are valid, exits 1 when any fails
- [ ] Script output contracts verified against workflow input expectations
- [ ] Basic process template routes correctly end-to-end (documented)
- [ ] Any validation gaps discovered are documented for the postmortem

---

### Issue 4.4: Cross-Process Validation

**Goal:** Prove that non-Basic process templates route correctly without code changes — only config changes.

**Prerequisites:** Issues 4.1 and 4.2 (unit + integration tests established)

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 4.4.1 | Verify Agile config (Epic/User Story/Task) routes correctly through full PhaseDetector + TransitionValidator | Within `CrossProcessPhaseDetectorTests.cs`, `CrossProcessTransitionValidatorTests.cs` | S |
| 4.4.2 | Verify Scrum config (Epic/Product Backlog Item/Task) routes correctly | Same cross-process test files | S |
| 4.4.3 | Verify CMMI config (Epic/Requirement/Task) routes correctly, including "Resolved" state→`StateCategory.Resolved` mapping | Same cross-process test files | S |
| 4.4.4 | Create composite hierarchy test: plannable→plannable→implementable chain verifies across all configs | `tests/Polyphony.Tests/Routing/CrossProcessPhaseDetectorTests.cs` | M |
| 4.4.5 | Document cross-process validation results and any validation gaps for postmortem | `docs/projects/validation-testing.plan.md` (update) | S |

**Acceptance Criteria:**
- [ ] Agile routes correctly for all 8 phases without code changes
- [ ] Scrum routes correctly for all 8 phases without code changes
- [ ] CMMI routes correctly for all 8 phases without code changes (including "Resolved" state)
- [ ] Composite hierarchy (plannable→plannable→implementable) routes correctly for all configs
- [ ] Zero code changes required — only config fixtures differ
- [ ] Any validation gaps documented for the postmortem

---

## PR Groups

### PG-1: Process Config Fixtures & Test Infrastructure
**Type:** Deep
**Scope:** Test fixture files and infrastructure only — no production code changes
**Estimated LoC:** ~500
**Successors:** PG-2, PG-3, PG-4

**Contains:**
- Task 4.1.1 — YAML process config fixtures (basic, agile, scrum, cmmi)
- Task 4.1.2 — ProcessConfigFixture helper class
- `.csproj` modification for embedded resources

**Rationale:** All subsequent PGs depend on these fixtures. Small, focused, easy to review.

---

### PG-2: Cross-Process Unit Tests
**Type:** Deep
**Scope:** Unit test files for PhaseDetector, TransitionValidator, hierarchy tiers, depth budget
**Estimated LoC:** ~1200
**Predecessors:** PG-1
**Successors:** PG-3

**Contains:**
- Task 4.1.3 — CrossProcessPhaseDetectorTests
- Task 4.1.4 — CrossProcessTransitionValidatorTests
- Task 4.1.5 — HierarchyTierTests
- Task 4.1.6 — DepthBudgetTests
- Task 4.4.1 — Agile routing verification
- Task 4.4.2 — Scrum routing verification
- Task 4.4.3 — CMMI routing verification
- Task 4.4.4 — Composite hierarchy tests

**Rationale:** Groups all unit-level cross-process tests. ~1200 LoC across 4 test files. Tests are self-contained and reviewable as a single set of routing validations.

---

### PG-3: Integration & E2E Tests
**Type:** Deep
**Scope:** Command-level integration tests with pre-seeded data
**Estimated LoC:** ~800
**Predecessors:** PG-2
**Successors:** PG-4

**Contains:**
- Task 4.2.1 — CrossProcessRouteCommandTests
- Task 4.2.2 — CrossProcessValidateCommandTests
- Task 4.2.3 — IntegrationScenarioTests (full lifecycle)
- Task 4.2.4 — JSON contract verification

**Rationale:** Integration tests build on the fixtures from PG-1 and validate the same configs tested in PG-2, but at the command level with real SQLite.

---

### PG-4: Workflow Validation & Documentation
**Type:** Wide
**Scope:** Pester scripts for conductor YAML validation, documentation updates
**Estimated LoC:** ~400
**Predecessors:** PG-3

**Contains:**
- Task 4.3.1 — lint-conductor-validate.ps1
- Task 4.3.2 — Pester tests for the lint script
- Task 4.3.3 — Script output contract verification
- Task 4.3.4 — Basic template end-to-end documentation
- Task 4.4.5 — Cross-process validation documentation

**Rationale:** Final PR group delivers the workflow-level validation and documentation. Depends on all prior work being merged.

---

## Execution Plan

### PR Group Summary

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|-------------|--------------|------|
| PG-1-process-config-fixtures | Process Config Fixtures & Test Infrastructure | 4.1 (Tasks 4.1.1, 4.1.2) | None | Deep |
| PG-2-cross-process-unit-tests | Cross-Process Unit Tests | 4.1 (Tasks 4.1.3–4.1.6), 4.4 (Tasks 4.4.1–4.4.4) | PG-1 | Deep |
| PG-3-integration-e2e-tests | Integration & E2E Tests | 4.2 (Tasks 4.2.1–4.2.4) | PG-2 | Deep |
| PG-4-workflow-validation | Workflow Validation & Documentation | 4.3 (Tasks 4.3.1–4.3.4), 4.4 (Task 4.4.5) | PG-3 | Wide |

### Execution Order

**PG-1 → PG-2 → PG-3 → PG-4** (fully sequential)

1. **PG-1** establishes the process config YAML fixtures (basic, agile, scrum, cmmi) and the `ProcessConfigFixture` helper. This is the foundation everything else builds on — no subsequent PG can compile without it.
2. **PG-2** adds all unit-level cross-process tests (PhaseDetector, TransitionValidator, HierarchyTier, DepthBudget). These tests depend on PG-1's fixtures but introduce no new production code.
3. **PG-3** adds command-level integration tests against pre-seeded SQLite. These build on PG-1 fixtures and validate the same routing logic as PG-2 at the command API level.
4. **PG-4** adds Pester workflow validation scripts and documentation. It is logically last because it documents results from all prior groups and the `conductor validate` checks are independent of the .NET test suite.

### Validation Strategy Per PG

**PG-1:** `dotnet build` passes with zero warnings. `ProcessConfigFixture.Basic()`, `Agile()`, `Scrum()`, `Cmmi()` all load without exception (smoke-tested by any PG-2 test that uses them).

**PG-2:** `dotnet test` passes all new Theory tests. Coverage: 8 phases × 4 templates for PhaseDetector; legal + illegal transitions × 4 templates for TransitionValidator; 2-tier/3-tier/4-tier for HierarchyTier; depth budget enforcement for DepthBudget.

**PG-3:** `dotnet test` passes all integration scenario tests. Full lifecycle (NeedsPlanning → ReadyForImpl → InProgress → ReadyForCompletion → Done) verified for at least one non-Basic template. JSON output contracts (snake_case, null omission, exit codes) asserted in-test.

**PG-4:** `Invoke-Pester tests/lint-conductor-validate.Tests.ps1` exits 0. `conductor validate` passes for all 9 workflow YAMLs. Any gaps are captured as postmortem observations.

---

## Validation Results

> **Last updated:** 2026-05-01 | **Test suite:** 533 tests, 0 failures, 0 skipped | **Duration:** ~8s | **Conductor validate:** 9/9 pass | **Pester lint-conductor-validate:** 7/7 pass

### Cross-Process Template Results

All four ADO process templates were validated across unit tests, integration tests, and E2E command tests. The system correctly routes work items using configuration-only changes — zero code modifications were required.

| Template | PhaseDetector | TransitionValidator | Route Command (E2E) | Validate Command (E2E) | Integration Lifecycle | Overall |
|----------|:---:|:---:|:---:|:---:|:---:|:---:|
| **Basic** (Epic→Issue→Task) | ✅ Pass | ✅ Pass | ✅ Pass | ✅ Pass | — | ✅ **Pass** |
| **Agile** (Epic→User Story→Task) | ✅ Pass | ✅ Pass | ✅ Pass | ✅ Pass | ✅ Pass | ✅ **Pass** |
| **Scrum** (Epic→Product Backlog Item→Task) | ✅ Pass | ✅ Pass | ✅ Pass | ✅ Pass | — | ✅ **Pass** |
| **CMMI** (Epic→Requirement→Task) | ✅ Pass | ✅ Pass | ✅ Pass | ✅ Pass | — | ✅ **Pass** |

**Key findings:**
- All 8 SDLC phases route correctly for every template without code changes
- Scrum-specific state variants (Approved, Committed, In Progress) all resolve correctly via `StateCategoryResolver` fallback
- CMMI "Resolved" state maps to `StateCategory.Resolved` and routes correctly through all child combinations
- `WorkItemType.Parse()` accepts all cross-process type names (User Story, Product Backlog Item, Requirement) without restriction

### Hierarchy Tier Results

| Tier | Configuration | Status |
|------|--------------|:---:|
| 2-tier | Issue→Task | ✅ Pass |
| 3-tier | Epic→{Issue\|User Story\|PBI\|Requirement}→Task (parameterized across all 4 templates) | ✅ Pass |
| 4-tier | Epic→Feature→Issue→Task | ✅ Pass |
| Depth budget | >4 plannable levels produce correct enforcement behavior | ✅ Pass |

### Test Coverage Summary

| Test File | Tests | Scope |
|-----------|------:|-------|
| CrossProcessPhaseDetectorTests | 34 | 8 phases × 4 templates, composite hierarchies, unregistered types |
| CrossProcessTransitionValidatorTests | 29 | Legal/illegal transitions × 4 templates, Scrum InProgress variants, CMMI resolve event |
| HierarchyTierTests | 14 | 2-tier, 3-tier (all templates), 4-tier, facet annotation, branching hierarchies |
| DepthBudgetTests | 12 | Plannable depth counting, deep chains (5 levels), implementable caps, unregistered types |
| CrossProcessRouteCommandTests | 15 | E2E route per template (×4 = 60 executions), JSON contracts, exit codes |
| CrossProcessValidateCommandTests | 8 | E2E validate per template (×4 = 32 executions), legal/illegal/unknown events |
| IntegrationScenarioTests | 13 | Full lifecycle (Agile), StateCategoryResolver authoritative path, 3-tier routing |
| JsonOutputContractTests | 24 | Exit codes, snake_case, null omission, round-trip deserialization, PascalCase leak detection |
| **Total (Phase 4 new)** | **149** | |

### Conductor Workflow YAML Validation

All 9 workflow YAMLs pass `conductor validate`:

| Workflow YAML | Status | Description |
|---------------|:---:|-------------|
| `twig-sdlc-v2-full.yaml` | ✅ Pass | Root workflow: preflight → state detection → phase routing |
| `twig-sdlc-v2-planning.yaml` | ✅ Pass | Planning orchestration: preflight-lite → plan-level → seed check |
| `twig-sdlc-v2-implement.yaml` | ✅ Pass | Implementation orchestration: load work tree → parallel PG dispatch |
| `plan-level.yaml` | ✅ Pass | Recursive planning core for any plannable hierarchy level |
| `implement-pg.yaml` | ✅ Pass | Single PG lifecycle: task loop → PR → merge → scope close |
| `feature-pr.yaml` | ✅ Pass | Feature PR lifecycle with remediation cycles (max 3) |
| `github-pr.yaml` | ✅ Pass | GitHub PR lifecycle: review → fix loop → merge |
| `ado-pr.yaml` | ✅ Pass | ADO PR lifecycle stub with human gate |
| `close-out.yaml` | ✅ Pass | Post-implementation close-out and observation filing |

**Automated guard:** `tests/lint-conductor-validate.ps1` runs `conductor validate` against all workflow YAMLs. Pester test suite (`tests/lint-conductor-validate.Tests.ps1`) verifies the lint script behavior: 7 tests covering all-pass, failure detection, per-YAML reporting, and edge cases (no YAMLs, missing directory).

### Basic Template End-to-End Workflow Routing

Full trace of the v2 SDLC workflow routing for the twig repo's own Basic process template (Epic/Issue/Task) as configured in `.conductor/process-config.yaml`.

#### Process Config Verification

The twig repo's `.conductor/process-config.yaml` defines:

| Field | Value | Validated |
|-------|-------|:---------:|
| `process_template` | Basic | ✅ |
| `platform` | github | ✅ Routes to `github-pr.yaml` (not `ado-pr.yaml`) |
| Epic facets | `[plannable]` | ✅ Plannable-only; decomposed into Issues |
| Issue facets | `[plannable, implementable]` | ✅ Dual-facet; can be planned or implemented directly |
| Task facets | `[implementable]` | ✅ Leaf implementable items |
| `branch_strategy.target` | main | ✅ |
| `branch_strategy.feature_branch` | `feature/{root_id}-{slug}` | ✅ |
| `branch_strategy.pg_branch` | `pg-{n}/{root_id}-{slug}` | ✅ |

#### Phase Transition Routing — Root Workflow

Routing from `twig-sdlc-v2-full.yaml` for each detected lifecycle phase:

| Phase | Source | Sub-Workflow Target | Status |
|-------|--------|---------------------|:------:|
| `needs_planning` | `detect-state.ps1` | `twig-sdlc-v2-planning.yaml` | ✅ Correct |
| `needs_seeding` | `detect-state.ps1` | `twig-sdlc-v2-planning.yaml` | ✅ Correct |
| `ready_for_implementation` | `detect-state.ps1` | `twig-sdlc-v2-implement.yaml` | ✅ Correct |
| `in_progress` | `detect-state.ps1` | `twig-sdlc-v2-implement.yaml` | ✅ Correct |
| `ready_for_completion` | `detect-state.ps1` | `close-out.yaml` | ✅ Correct |
| `done` | `detect-state.ps1` | `$end` | ✅ Correct |
| `removed` | `detect-state.ps1` | `$end` | ✅ Correct |

All 7 phase values produced by `detect-state.ps1` are handled by the root workflow's route table. No unhandled phase values exist.

#### Planning Sub-Workflow Routing

Flow through `twig-sdlc-v2-planning.yaml`:

```
preflight_lite (preflight-lite.ps1)
  ├─ ready=true  → plan_level (plan-level.yaml, depth=0, max_depth=6)
  └─ ready=false → preflight_lite_gate (human: retry/abort)

plan_level → seed_check (detect-state.ps1 re-invoked with intent=resume)
  ├─ phase=needs_seeding        → work_tree_seeder (agent: seed children from plan)
  └─ phase=ready_for_implementation → $end (planning complete)
```

**Basic template behavior:** An Epic in "To Do" state → `StateCategoryResolver` maps "To Do" → `Proposed` → `PhaseDetector` routes to `needs_planning`. After planning completes and children are seeded, re-detection produces `ready_for_implementation`.

#### Implementation Sub-Workflow Routing

Flow through `twig-sdlc-v2-implement.yaml`:

```
preflight_lite (preflight-lite.ps1)
  ├─ ready=true  → load_work_tree (load-work-tree.ps1)
  └─ ready=false → preflight_lite_gate (human: retry/abort)

load_work_tree
  ├─ pending_pgs > 0 → pg_dispatcher → pg_execution_group (for_each → implement-pg.yaml)
  └─ pending_pgs = 0 → $end (all PGs already complete)

pg_execution_group (max_concurrent=3, failure_mode=fail_fast)
  ├─ failed_count = 0 → $end (success)
  └─ failed_count > 0 → pg_summary_gate (human: retry/abort)
```

**Basic template behavior:** `load-work-tree.ps1` uses `polyphony hierarchy` to discover the work tree. `pg-router.ps1` groups items by PG tag using `Group-ByPG` from `lib/pg-helpers.ps1` — facet-based, not type-name-based.

#### PG Implementation Routing (implement-pg.yaml)

Flow through `implement-pg.yaml` for each PG:

```
pg_router (pg-router.ps1)
  ├─ action=create_branch   → branch_manager → task_router
  ├─ action=implement_tasks → task_router
  ├─ action=submit_pr       → pr_submit (re-entry: PR not yet created)
  └─ action=all_complete    → $end

task_router (impl-router.ps1)
  ├─ action=implement_task → coder → reducer_code → task_reviewer
  │   ├─ verdict=approved          → task_completer → task_router (loop)
  │   └─ verdict=changes_requested → coder (fix loop)
  └─ action=all_tasks_done → dependency_check
      ├─ status=not_blocked → reducer_issue → issue_reviewer
      │   ├─ verdict=approved → user_acceptance → pr_submit
      │   └─ verdict=changes_requested → task_router (rework)
      └─ status=blocked → dependency_gate (human: wait/override/reassign)

pr_submit → pr_platform_router
  ├─ platform=github → pr_lifecycle_github (github-pr.yaml) → scope_closer
  └─ platform=ado    → pr_lifecycle_ado (ado-pr.yaml) → scope_closer
```

**PR platform routing for twig repo:** `process-config.yaml` specifies `platform: github`. The `pr_platform_router` in `implement-pg.yaml` routes to `github-pr.yaml` (lines 672–675). Confirmed: `ado-pr.yaml` is never selected for this repo.

**Task routing:** `impl-router.ps1` filters by `implementable` facet and PG tag, with 4 fallback levels: direct tag match → children of tagged containers → issue-as-task (plannable+implementable) → all implementable items. This is type-agnostic — no hardcoded type names.

#### Script Output Contract Verification

Key scripts produce JSON output consumed by workflow route conditions:

| Script | Key Output Fields | Consumer | Verified |
|--------|-------------------|----------|:--------:|
| `preflight-check.ps1` | `ready`, `summary`, `required_checks[]`, `failed_count` | `twig-sdlc-v2-full.yaml` routes on `ready` | ✅ |
| `preflight-lite.ps1` | `ready`, `summary`, `checks[]` | Planning/implement preflight gates | ✅ |
| `detect-state.ps1` | `phase`, `work_item_id`, `work_item_type`, `work_item_state`, `intent` | Root workflow phase routing | ✅ |
| `pg-router.ps1` | `action`, `pr_groups[]`, `feature_branch` | `implement-pg.yaml` routes on `action` | ✅ |
| `impl-router.ps1` | `action`, `task_id`, `task_title`, `branch_name` | `implement-pg.yaml` routes on `action` | ✅ |
| `load-work-tree.ps1` | `pr_groups[]`, `completed_pgs`, `pending_pgs` | `twig-sdlc-v2-implement.yaml` routes on `pending_pgs` length | ✅ |
| `scope-closer.ps1` | exit code 0/1 | `implement-pg.yaml` merged output | ✅ |
| `dependency-check.ps1` | `status`, `blocking_items[]` | `implement-pg.yaml` dependency gate | ✅ |

All script output field names align with their consumers' Jinja2 template references in the workflow YAMLs. No field name mismatches were found.

#### Close-Out Routing

Flow through `close-out.yaml`:

```
close_out (agent: post-mortem analysis, reads hierarchy + plan + PR history)
  └─ closeout_filer (agent: files observations as new work items)
      └─ output.filed_count → $end
```

**Basic template behavior:** `close-out.yaml` uses `filing_eligible` from process-config.yaml to determine which types can receive filed observations. For Basic: Issue (`true`) and Task (`true`) are eligible; Epic (`false`) is not. This is type-agnostic — the workflow reads the config, not hardcoded type lists.

#### End-to-End Routing Summary

The complete lifecycle for a Basic template work item flows through these phases:

```
Epic (To Do) ──detect-state──→ needs_planning
  └─ twig-sdlc-v2-planning.yaml
       ├─ plan-level.yaml (recursive planning)
       └─ work_tree_seeder (seed Issues/Tasks from plan)

Epic (Doing, children seeded) ──detect-state──→ ready_for_implementation
  └─ twig-sdlc-v2-implement.yaml
       ├─ load-work-tree.ps1 (discover PG structure)
       └─ implement-pg.yaml × N (parallel, max 3 concurrent)
            ├─ impl-router.ps1 (next implementable Task)
            ├─ coder → reviewer → completer (per Task)
            ├─ pr_platform_router → github-pr.yaml (platform: github)
            └─ scope-closer.ps1 (transition Tasks to Done)

Epic (all children Done) ──detect-state──→ ready_for_completion
  └─ close-out.yaml
       ├─ Post-mortem analysis
       └─ Observation filing (Issue/Task eligible)

Epic (Done) ──detect-state──→ done → $end
```

**Result:** All major phase transitions route correctly for the Basic template. The v2 workflow correctly handles the twig repo's own work items using the configuration in `.conductor/process-config.yaml`.

### Validation Gaps — Postmortem Observations

The following gaps were identified during Phase 4 validation. These should be filed as postmortem observations by the close-out workflow.

#### Gap 1: Conductor YAML Validation Not Automated *(Resolved)*

**Severity:** ~~Moderate~~ → **Resolved**
**Description:** Task 4.3.1 (`lint-conductor-validate.ps1`) and Task 4.3.2 (Pester tests for the lint script) have been implemented. All 9 workflow YAMLs pass `conductor validate`. The lint script (`tests/lint-conductor-validate.ps1`) provides an automated regression guard, and its Pester test suite (`tests/lint-conductor-validate.Tests.ps1`) validates the lint script's own behavior with 7 test cases covering pass/fail detection, per-YAML reporting, and edge cases.
**Resolution:** Implemented in AB#2778. Validated end-to-end in AB#2780.

#### Gap 2: Full Lifecycle Coverage Limited to Agile Template

**Severity:** Low
**Description:** `IntegrationScenarioTests` exercises the complete lifecycle (NeedsPlanning → NeedsSeeding → ReadyForImplementation → InProgress → ReadyForCompletion → Done) only for the Agile template. Basic, Scrum, and CMMI templates are covered at the unit and E2E command level but do not have dedicated lifecycle walkthrough tests.
**Impact:** Low — the unit and E2E tests cover all templates at the routing layer. The integration lifecycle test primarily validates the *sequencing* of state transitions, which is template-independent (the same PhaseDetector and TransitionValidator code paths execute regardless of template). The Agile test is representative.
**Recommendation:** No immediate action needed. If org-specific state customizations introduce sequencing differences, add lifecycle tests per template at that time.

#### Gap 3: Freshness Enforcement Not Tested

**Severity:** Low
**Description:** Cache staleness enforcement is managed by Twig infrastructure, not Polyphony. No tests verify the freshness contract at the Polyphony boundary. Open Question #4 in this plan acknowledges this as a Twig-level concern.
**Impact:** Minimal — Polyphony is a read-only consumer of cached work item data. Staleness detection and enforcement are architectural responsibilities of the Twig cache layer.
**Recommendation:** If freshness issues arise in production, add contract tests at the Twig.Infrastructure level to verify staleness detection behavior.

#### Gap 4: Org-Specific State Customizations Not Covered

**Severity:** Low
**Description:** All tests use standard ADO process template state names. Organizations with customized states (e.g., "In Review" instead of "Active") rely on `ProcessTypeRecord.States` entries seeded from ADO sync. The integration tests exercise this authoritative resolution path for Agile states, but custom state names are not tested.
**Impact:** Low — the `StateCategoryResolver` two-tier strategy (authoritative entries → hardcoded fallback) handles this by design. The authoritative path is exercised in integration tests. Custom states would use the same authoritative path.
**Recommendation:** If a specific org customization causes routing failures, add targeted test fixtures with that org's state names and `StateEntry` data.

#### Gap 5: Script Output Contract Verification Incomplete *(Partially Resolved)*

**Severity:** ~~Low~~ → **Informational**
**Description:** Task 4.3.3 planned to verify script output contracts (JSON schema, required fields) against workflow expectations across the PowerShell script suite. The existing Pester lint tests (lint-type-agnostic, lint-root-routing, etc.) validate script behavior but do not systematically verify output contract alignment with conductor workflow input schemas. The Basic template end-to-end trace (AB#2780) manually verified all 8 script output field names align with their consumer Jinja2 template references — no mismatches found.
**Impact:** Informational — manual verification complete for the Basic template. Automated contract assertions would guard against future drift.
**Recommendation:** Consider adding output contract assertions to existing Pester lint suites in a future maintenance pass.

#### Gap 6: No Live Conductor Dry-Run Execution

**Severity:** Low
**Description:** The end-to-end validation (AB#2780) traces workflow routing by examining YAML definitions, script logic, and process-config.yaml. `conductor validate` confirms structural validity of all 9 YAMLs. However, no `conductor dry-run` or live conductor execution was performed to verify runtime Jinja2 template rendering, variable passing between agents, or for_each dispatch behavior.
**Impact:** Low — structural validation via `conductor validate` catches the most common issues (missing agents, broken routes, schema violations). Runtime issues (template rendering errors, variable scoping) would surface during the first real workflow execution.
**Recommendation:** Perform a `conductor dry-run` against one workflow (e.g., `twig-sdlc-v2-full.yaml`) with mock inputs when conductor dry-run support is available. Track as a postmortem observation.

---

## References

- [Phase 1 Plan: Polyphony Core Engine](polyphony-core-engine.plan.md)
- [Phase 2 Plan: Type-Agnostic SDLC](type-agnostic-sdlc.plan.md)
- [Phase 3 Plan: Workflow YAML Refactoring](workflow-yaml-refactoring.plan.md)
- [Azure DevOps Process Templates](https://learn.microsoft.com/en-us/azure/devops/boards/work-items/guidance/choose-process)
- [Conductor Workflow Validation](https://github.com/github/conductor)


