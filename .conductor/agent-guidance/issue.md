# Issue Guidance — polyphony

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
