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
- 📌 2026-05-31: Conducted deep testability gap analysis for conductor → polyphony seams. Identified 5 critical gaps (reported in `.squad/handoffs/brahms-conductor-gaps-20260531.md`):
  1. **Workflow-level integration testing** — harness is Python-only; no conductor .NET SDK; .NET test suite has zero workflow tests.
  2. **Error paths untestable** — 19 trivial error gates should be on_error declarations; harness lacks error-simulation support; all 6 workflows have untestable error routing.
  3. **Resume / checkpoint untestable** — no way to verify workflows skip already-completed steps on re-entry; requires conductor checkpoint model.
  4. **Notifications untestable** — harness doesn't capture notification payloads; workflows emit but we cannot assert correctness or ordering.
  5. **Sub-workflow output schema untestable** — no contract registry for workflow outputs; output drift silently breaks parents at runtime; missing schema validation + linting.
- 📌 Gap prioritization: #2 (on_error, 1–2 weeks) and #5 (output schema, 2–3 weeks) are must-have for v1 release. #4 (notifications, 1–2 weeks) and #1 (workflow .NET SDK, 3–4 weeks) are should-have for Phase 4. #3 (resume checkpoint, 2–3 months) deferred to Phase 5.
- 📌 Harness infrastructure strength: real conductor + FakeProvider + .NET shim is gold. Weakness: Python-only, no error simulation, no resume checkpointing, no notification capture. Coverage map shows 13 scenarios covering 6 of 15 workflows; 9 workflows untested (feature-pr, implement-merge-group, polyphony are critical).
- 📌 Root cause for all gaps: conductor is not designed for testing (no test mode, no deterministic seeding, no event replay). Upstream conductor PRs needed: test-mode SDK, error-event exposure, checkpoint API, notification-payload exposure.
