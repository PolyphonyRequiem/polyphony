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
