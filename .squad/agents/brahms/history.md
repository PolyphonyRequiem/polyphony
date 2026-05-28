# Project Context

- **Owner:** Daniel Green
- **Project:** Polyphony — type-agnostic SDLC routing engine and conductor workflow suite.
- **Stack:** C# (.NET 11), conductor YAML, PowerShell 7+, Python harness, twig CLI.
- **Created:** 2026-05-28

## Learnings

- 📌 Team formed 2026-05-28. My seat: Testability Designer.
- 📌 Test build cadence: `dotnet build src\Polyphony\Polyphony.csproj -c Release` → `dotnet build tests\Polyphony.Tests\Polyphony.Tests.csproj -c Release` → `dotnet test tests\Polyphony.Tests\Polyphony.Tests.csproj -c Release --no-build`.
- 📌 If `artifacts/verb-output-schemas.json` is missing, build `src\Polyphony.SchemaExporter\Polyphony.SchemaExporter.csproj -c Release` first.
- 📌 Harness invariant: Python harness at `tests/harness/` uses FakeProvider + .NET shim. MUST NOT call real LLMs or real ADO.
- 📌 Lint vocabulary: `.conductor\registry\tests\lint-vocabulary.ps1 -Root <repo>` enforces glossary.md forbidden terms (apex/wave/cascade/etc.).
- 📌 `.conductor/registry/tests/*.Tests.ps1` is NOT auto-discovered by ci.yml — new test files need explicit CI workflow wiring.
- 📌 Jinja-resolver lint uses dual registries: live `artifacts/verb-output-schemas.json` (CI gate) and locked `tests/lint/fixtures/verb-output-schemas.json` (Pester suite). Refresh the fixture when verb result-fields change.
- 📌 After RequiredInput sentinel migration (Move #2 / AB#3259), NO verb has `required:true` in the live registry — every required input uses an explicit default sentinel and a runtime `HaltIfMissing` guard. VERB003 test uses synthetic fixture at `tests/lint/fixtures/verb-output-schemas-verb003.json`.
- 📌 2026-05-28: Participated in squad-wide initial concerns review (10-agent fan-out). Surfaced 3 top testability concerns: fixture lifecycle drift, harness scenario coverage gaps, .Tests.ps1 discovery fragility. Nominated short-term wins: wire verb-schema regen into build, add harness scenario validator.
- 📌 2026-05-28: Implemented #527 (Brahms half) — added `VerbOutputSchemas_ArtifactIsFresh` to `VerbCatalogSanityTests`. SHA-based canonical comparison (sort keys + WriteIndented before hashing). Test is intentionally RED until Mozart's `squad/527-verbresult-attrs` backfill PR merges. PR #532.
- 📌 `VerbSchemaGenerator` is a Roslyn source generator (compile-time only) — NOT callable in-process at test runtime. "In-process" freshness test = compare `VerbOutputSchemaCatalog.Json` (compile-time constant) against on-disk artifact.
- 📌 Local build blocked by SDK mismatch: repo requires `11.0.100-preview.3.26207.106` (`rollForward: disable`), but .NET 11 preview.4 introduces a breaking `IUnion` symbol collision in `TransitionValidatorTests.cs`. CI is the authoritative gate.
- 📌 2026-05-28: Participated in implementation round 1 — shipped PR #532 on issue #527 (short-term win).
