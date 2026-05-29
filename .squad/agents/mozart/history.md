# Project Context

- **Owner:** Daniel Green
- **Project:** Polyphony — type-agnostic SDLC routing engine and conductor workflow suite.
- **Stack:** C# (.NET 11, ConsoleAppFramework, AOT JSON, PolyphonyJsonContext), conductor YAML, PowerShell 7+, Python harness, twig CLI.
- **Created:** 2026-05-28

## Learnings

- 📌 Team formed 2026-05-28. My seat: .NET/C# Expert.
- 📌 Build: `dotnet build src\Polyphony\Polyphony.csproj -c Release`. Tests: `dotnet build tests\Polyphony.Tests\Polyphony.Tests.csproj -c Release` then `dotnet test ... --no-build`.
- 📌 Schema export: build `src\Polyphony.SchemaExporter\Polyphony.SchemaExporter.csproj -c Release` to regenerate `artifacts/verb-output-schemas.json`. Tests need this artifact (VerbCatalogSanityTests).
- 📌 Verb pattern: ConsoleAppFramework Command attribute + primary-constructor DI + PolyphonyJsonContext for AOT-friendly JSON. New verbs get CommandTestBase scaffolding + JsonOutputContractTests.
- 📌 RequiredInput sentinel pattern: `int RequiredInput.MissingInt`, `RequiredInput.HaltIfMissing(...)` in verb body. After Move #2 (AB#3259), NO verb has `required:true` in schema.
- 📌 RunManifest authoritative for: PlanGenerations, MergeGroups, TopologyHash, RetiredMergeGroupIds, HumanApprovals. Log-shaped (NOT hashed): Rebases, MergedPlanPrs. TopologyHash recomputed on every Save.
- 📌 Polyphony NEVER writes to ADO. All writes via twig CLI. Twig.Domain + Twig.Infrastructure are ProjectReferences for reads only (wired via `services.AddTwigCoreServices(twigDir: twigDir)`).
- 📌 2026-05-28: Delivered issue #527 (Mozart's half). All 109 [Command] methods were already annotated with [VerbResult]; no annotation gap at time of delivery. Artifact was missing (artifacts/ is .gitignored) — regenerated and force-added. PR: https://github.com/PolyphonyRequiem/polyphony/pull/534.
- 📌 SDK pin matters: global.json rollForward=disable means the exact SDK version must be installed. When squad environment has a different preview, global.json needs a bump — first thing to check if `dotnet build` fails with SDK-not-found.
- 📌 IUnion ambiguity (CS0433) in .NET 11 preview.4: Twig.Domain 0.77.4 bundles its own System.IUnion polyfill (compiled against preview.3 before it shipped in System.Runtime). Upgrading to preview.4 produces CS0433. Fix: replace ((IUnion)x).Value.ShouldBeOfType<T>() with is-pattern matching — more idiomatic for union types and avoids the cast entirely.
- 📌 artifacts/ is in .gitignore; verb-output-schemas.json must be force-added (`git add -f`). Follow-up: consider adding `!artifacts/verb-output-schemas.json` to .gitignore so it's tracked without force.
- 📌 VerbCatalogSanityTests (5 tests) verify: JSON parses, version=1, verbs+types non-empty, known group prefixes present, no dangling result_type references. Run with --filter "FullyQualifiedName~VerbCatalogSanity" after building tests.
- 📌 2026-05-28: Participated in implementation round 1 — shipped PR #534 on issue #527 (short-term win).
- 📌 2026-05-29: Seeding plan error circuit analysis. Key findings: (1) Squad briefing's exit code summary ("3=auth, 4=not-found") does NOT match the actual ADR (3=twig unavailable, 4=git failure) — this needs a correction. (2) Auth failures against ADO surface as code 5, not a distinct auth code. (3) 404 from ADO during creation is a domain outcome (exit 0), not code 5. (4) Code 1 (unclassified crash) must be permanent/no-retry — retrying unknown failures risks state corruption. (5) `polyphony seed` is almost certainly not idempotent today; the seeding plan artifact (durable per work root ID) is what enables safe retry. (6) Proposed split: `plan-seed` (writes plan, no ADO calls) + `apply-seeding-plan` (read-compare-act, retryable). (7) Retry counter belongs at the workflow node (`on_error:` in YAML), not inside the verb. (8) Deferred partial-success exit code (7) — convergence loop is sufficient. (9) Exit codes are platform-agnostic; platform detail goes in `CONDUCTOR_ERROR_OUT` `kind` field. Wrote: `.squad/handoffs/mozart-seeding-plan-error-circuit.md`, `.squad/decisions/inbox/mozart-seeding-plan-error-stance.md`.
- 📌 2026-05-29: Seed Manifest ADR shipped (`seed-manifest-as-durable-state.md`). Your error-code amendments are encoded (retry classification, idempotency invariant, attempt context on CONDUCTOR_ERROR_OUT). When touching the seeder verb or error-circuit retrofit (AB#3257), reference ADR section "Mozart — Seeding Plan Error Stance" for constraint checklist (code 1 permanent, 3/5 transient + idempotent).
