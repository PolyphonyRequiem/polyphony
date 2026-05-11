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
