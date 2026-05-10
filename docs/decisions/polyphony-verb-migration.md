# Polyphony Verb Migration — Locked Decisions

> **Context:** Driving Epic 2978 (Polyphony Self-Contained Orchestration). Source plan:
> [`docs/projects/polyphony-self-contained-orchestration.user-plan.md`](../projects/polyphony-self-contained-orchestration.user-plan.md).
> This file captures the design contract the migration honours.

## Lifecycle groups

Migrated scripts cluster into four lifecycle groups, registered via
`app.Add<TCommands>("<group>")`:

| Group | Purpose | Verbs |
|---|---|---|
| `polyphony state ...` | Pre-flight, lifecycle phase detection, type-context loading | `preflight`, `detect`, `load-type`, `load-guidance` |
| `polyphony plan ...` | Planning-stage helpers | `depth-guard`, `next-child`, `review`, `seed-children` |
| `polyphony branch ...` | Branch / PG / task / dependency lifecycle on a feature branch | `load-tree`, `route`, `next-impl`, `check-deps`, `close-scope` |
| `polyphony pr ...` | PR submission and feature-PR creation | `create-feature-pr` (gh helpers internalised, not exposed) |

Existing top-level verbs (`route`, `validate`, `validate-config`, `hierarchy`, `health`)
**stay top-level** — they pre-date the group taxonomy and serve broad operator workflows,
not workflow scripts.

## Verb naming inside groups

Match the script name where possible so the migration mapping is obvious in PRs and
post-mortems:

| Script | Verb |
|---|---|
| `depth-guard.ps1` | `polyphony plan depth-guard` |
| `child-router.ps1` | `polyphony plan next-child` |
| `preflight-check.ps1` | `polyphony state preflight` |
| `preflight-lite.ps1` | `polyphony state preflight --lite` (sub-flag, not separate verb) |
| `detect-state.ps1` | `polyphony state detect` *(later removed in the SdlcPhase cutover; superseded by `polyphony state next-ready`)* |
| `load-type-context.ps1` | `polyphony state load-type` |
| `load-agent-guidance.ps1` | `polyphony state load-guidance` |
| `load-work-tree.ps1` | `polyphony branch load-tree` |
| `pg-router.ps1` | `polyphony branch route` |
| `impl-router.ps1` | `polyphony branch next-impl` |
| `dependency-check.ps1` | `polyphony branch check-deps` |
| `scope-closer.ps1` | `polyphony branch close-scope` |
| `feature-pr-creator.ps1` | `polyphony pr create-feature-pr` |
| `review-router.ps1` | `polyphony plan review` |
| `seeder.ps1` | `polyphony plan seed-children` |
| `invoke-gh.ps1` / `resolve-gh-token.ps1` | internal helpers, not exposed verbs |
| `bootstrap-conductor.ps1` | **deferred** — out of scope for this Epic |

## JSON contract preservation

Every migrated verb preserves the exact JSON output schema of its predecessor script
because workflow YAMLs route on specific top-level keys (e.g. `allowed`,
`has_plannable_children`, `phase`). Phase 6 swaps YAML invocations from
`pwsh -File scripts/foo.ps1` to `polyphony <group> foo`; until then both must coexist.

- All result records: `public sealed record` with `required` properties.
- Property naming: snake_case via `PropertyNamingPolicy = SnakeCaseLower`.
- Optional fields: nullable (`string?`, `T?`) → omitted when null.
- **Every result type MUST be registered in `PolyphonyJsonContext`** for AOT publish.
  The architect review of the abandoned SDLC run flagged this as a blocking
  acceptance criterion; surfacing it here so it's not missed in any PG.

## Routing-script exit-code convention

Scripts authored as `type: script` agents in the plan-level / implement-merge-group workflows
**always exit 0** — the workflow routes on JSON output, not exit code. Migrated verbs
in this category preserve that behaviour (returning `ExitCodes.Success` even on the
"work item not found" path), and surface the error inline as an `error` field plus
sensible defaults for the routing keys. Examples: `plan depth-guard`, `plan next-child`.

Operator-facing verbs (`route`, `validate`, `hierarchy`, `health`, `validate-config`)
keep the standard exit-code convention (`CacheError`/`ConfigError`/`RoutingFailure`).

## Test conventions

- Inherit `CommandTestBase` for in-memory SQLite + stdout capture.
- Always add five tests per verb in `JsonOutputContractTests`:
  - `Foo_SnakeCaseFieldNames_PresentInRawJson`
  - `Foo_NullFieldsOmitted_WhenWritingNull`
  - `Foo_DeserializationRoundTrip_FieldsMapped`
  - `Foo_NotFound_ReturnsErrorJson_WithCacheErrorExitCode` *(operator verbs only;
    routing scripts assert `Success` + empty payload + `error` field instead)*
  - extend `AllCommands_NotFound_ErrorJsonFormatConsistent` *(operator verbs only)*
- Verbs that shell out to external CLIs (`twig`, `gh`) take an `IProcessRunner`
  dependency for substitutability in tests — established as the testing pattern from
  Phase 4 onward (no shells out in Phases 1–2).

## P5 — no hardcoded process-config values

Two scripts hard-code state names that should be sourced from `process-config.yaml`:

- `scope-closer.ps1:63` — hardcodes `'Done'`.
- (audit other scripts during their migration phase.)

Migrated verbs **must** look up the done-equivalent state from
`ProcessConfig.Transitions[<type>]["implementation_complete"]`. The reviewer flagged
this as a blocking issue for the abandoned SDLC run; carrying the constraint forward
explicitly so each PG honours it.

## In-scope script stores

Two stores must be drained:

1. `polyphony/scripts/*.ps1` (15 scripts excluding `bootstrap-conductor.ps1`).
2. `polyphony/.conductor/registry/scripts/*.ps1` (`review-router.ps1`, `seeder.ps1`).

Phase 6 deletes both directories' contents once all verbs ship and YAMLs reference
them. The reviewer's blocking-issue list flagged that the second store had been
omitted from the abandoned plan's success criteria; adding it here so it isn't lost.

## Hierarchy taxonomy

Per Epic-type definition in `process-config.yaml`, **Epics may contain only Issues**
(never Tasks directly). Phase task-IDs are mapped to Issue work-item types when seeded
under the Epic. This was the lead reviewer-blocking-issue from the abandoned SDLC run.

---

## Why an ADR rather than re-running the SDLC pipeline

The SDLC pipeline (the polyphony workflow suite) was launched against this Epic, hit a
PowerShell encoding bug (cp1252 stdout corrupting `→` to byte 0x1A in JSON), and
required iterative human gates that don't fit the Copilot-CLI agent's notification
model. The migration is being implemented directly to deliver the structural fix the
SDLC pipeline depends on. Once the migration lands, the SDLC pipeline will be a
defensible tool for future Epics.
