# Coder Guidance — polyphony

You are implementing changes to **polyphony**: a .NET 11 CLI (preview
LangVersion) that routes work items through SDLC phases. Polyphony is the
routing brain; twig is the ADO mouth. Code accordingly.

## Hard build constraints (non-negotiable)

- `TargetFramework=net11.0`, `Nullable=enable`,
  `JsonSerializerIsReflectionEnabledByDefault=false`. AOT publish is
  currently disabled (see `src/Polyphony/Polyphony.csproj`) but every code
  path must stay AOT-friendly: source-gen JSON only, no
  `Activator.CreateInstance`, no `Type.GetType` from strings, no reflection
  on user types. Re-enabling AOT later must be a csproj-level change.
- `TreatWarningsAsErrors=true` and nullable reference types enabled. **Any
  warning fails the build.** Do not suppress with `#pragma warning disable`
  unless you have proven there is no fix.
- All JSON serialization goes through `PolyphonyJsonContext`. To add a
  serializable type, add `[JsonSerializable(typeof(NewType))]` to the
  context partial class. Do **not** call `JsonSerializer.Serialize` with
  reflection-mode options.

## Validation (session-budget aware)

Your conductor session has a hard 1800s ceiling. Full-suite test runs
blow the budget on multi-file changes (see AB#3155). **Run only the
tests relevant to the changes you just made**, then trust CI for
full-suite validation.

- For each project you edited, run `dotnet build` on **that project**
  (e.g. `dotnet build src/Polyphony/Polyphony.csproj`), not the
  solution. The solution build is CI's job.
- For tests, use `dotnet test --filter` scoped to the test class(es)
  that cover your change — typically the `*Tests` class matching the
  edited type (`FooTests` for `Foo`) plus any test class that imports
  it. Example: `dotnet test tests/Polyphony.Tests/Polyphony.Tests.csproj
  --filter "FullyQualifiedName~ResearchConfigValidatorTests"`.
- Do **not** run `dotnet test` against the solution or against a test
  project without a `--filter`. That is the full-suite path and it
  belongs to CI, not your session.
- If a build error is unrelated to the change you set out to make,
  write the diagnosis into the PR description and stop. Do not chase
  cross-cutting refactors mid-session.

CI on the PR runs the full build + full test suite + every workflow
lint. That is the authoritative gate. Your in-session validation is
proof your change works in isolation, not proof it ships.
