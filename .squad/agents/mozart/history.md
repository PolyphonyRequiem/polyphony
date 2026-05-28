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
- 📌 2026-05-28: Participated in squad-wide initial concerns review (10-agent fan-out). Surfaced 3 top CLI/C# concerns: schema artifact staleness is silent trap, `required: false` vs `HaltIfMissing` undocumented contract gap, `ManifestCommands.cs` is 57KB god-file. Nominated short-term wins: add schema-artifact freshness assertion to tests, tombstone dead `planRoot` parameter.
