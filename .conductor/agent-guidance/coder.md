# Coder Guidance — polyphony

You are implementing changes to **polyphony**: an AOT-compiled .NET 10 CLI
(C# 14) that routes work items through SDLC phases. Polyphony is the routing
brain; twig is the ADO mouth. Code accordingly.

## Hard build constraints (non-negotiable)

- `PublishAot=true`, `TrimMode=full`, `StripSymbols=true`,
  `InvariantGlobalization=true`,
  `JsonSerializerIsReflectionEnabledByDefault=false`.
- `TreatWarningsAsErrors=true` and nullable reference types enabled. **Any
  warning fails the build.** Do not suppress with `#pragma warning disable`
  unless you have proven there is no fix.
- All JSON serialization goes through `PolyphonyJsonContext`. To add a
  serializable type, add `[JsonSerializable(typeof(NewType))]` to the
  context partial class. Do **not** call `JsonSerializer.Serialize` with
  reflection-mode options.
- ConsoleAppFramework v5 (source-gen). No manual argument parsing.
- YamlDotNet for YAML; System.Text.Json (source-gen) for JSON. No
  Newtonsoft.Json, no second YAML library.
- SQLite with WAL for any new persistence (mirrors the twig infrastructure).
- Prefer `sealed` classes / records, primary constructors, file-scoped
  namespaces. Register DI in `PolyphonyServiceRegistration.cs` or `Program.cs`.

## Style and patterns

- Public APIs return structured result types — never throw across a public
  boundary for an expected outcome. Throw only for true invariant violations.
- Pattern matching > if/else ladders for typed dispatch.
- Use `record` for value-equality types, `class` for entities with identity.
- Tests use xUnit + Shouldly + NSubstitute. PowerShell scripts have Pester
  test files alongside them (`*.Tests.ps1`).

## P5 / P8 first principles (implementation consequences)

- **No hard-coded state names anywhere.** Strings like `"Done"`, `"Doing"`,
  `"Removed"`, `"Active"` must come from the validator output (e.g.
  `polyphony validate --event <name> --json`) — never typed inline. Three
  recent regressions came from exactly this:
  `workflows/implement-pg.yaml:370` (commit `9f96f8b`),
  `scripts/task-router.ps1:106` (`03aab89`),
  `.conductor/process-config.yaml` (`5ea9929`). Don't be the fourth.
- **The validator is the oracle.** If you find yourself encoding "what state
  follows X" in script or YAML, stop and call `polyphony validate` instead.
  Canonical pattern: `scripts/scope-closer.ps1:54-60`.
- The `$script:TemplateTypes` and `$script:TemplateTransitions` hashtables in
  `scripts/bootstrap-conductor.ps1` are known P5 violations on the deferred
  list — do not extend them, and prefer to remove them when touched.

## Commit conventions

- All commits go on a feature branch. **Never commit directly to main.**
- Each commit must compile and pass tests on its own. No "WIP" commits in PRs.
- Include AB# work-item references in commit messages where applicable.
- After each meaningful checkpoint, add a `twig note --text "..."` summarizing
  what changed and why.
- Do not transition Epics. Only the close-out agent transitions Epics. You
  may transition Tasks via the configured event names.

## Test discipline

- Run both test suites before considering work done:
  `dotnet test --no-restore` (currently 684/684 passing) and Pester for any
  modified PowerShell script (e.g. `Invoke-Pester scripts/...Tests.ps1`).
- Fixtures under `tests/Polyphony.Tests/TestFixtures/ProcessConfigs/` are
  schema-locked. If you change config shape, update every fixture and the
  onboarding guide in the same commit.
- New CLI verb → integration test that invokes it. New validator rule →
  positive test, negative test, and a fixture row demonstrating the rule.

## Output rules

Never return null for any output field. Use 0 for numbers, "" for strings,
[] for arrays, false for booleans.
