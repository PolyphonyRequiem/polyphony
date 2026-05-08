# Verb Output Schema Registry — Generation, Mapping, Distribution

> **Status:** Proposed. Implements [`#173`](https://github.com/PolyphonyRequiem/polyphony/issues/173)
> (prong 1 of [`#172`](https://github.com/PolyphonyRequiem/polyphony/issues/172)).
> **Driver:** Workflow YAMLs reference verb output fields by Jinja path
> (`{{ agent.output.foo }}`) and nothing today checks that the field
> exists, that it can be omitted at runtime, or that the path resolves
> against the producer's actual wire shape. Bugs #6 (field name drift)
> and #8 (`WhenWritingNull` elision) are the targets — both are
> mechanically detectable from the C# DTO graph. This ADR locks the
> design of the registry that makes that contract explicit.
> **Supersedes:** none — greenfield.

## Context

`PolyphonyJsonContext` (`src/Polyphony/PolyphonyJsonContext.cs`) is
the source of truth for every verb output's wire shape. It declares
~150 `[JsonSerializable(typeof(...))]` attributes and sets a context-
level `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull`.
That default has a load-bearing consequence: a `string?` / `int?`
property silently disappears from the JSON object when its value is
null, even though the schema "has" it. Authors only discover this
when conductor's `strict_undefined` raises mid-run.

The C# DTOs are well-tested in isolation. The contract between them
and the workflow YAMLs that consume them is checked only by
execution. This ADR establishes the artifact that closes the gap;
the consuming lint is [`#175`](https://github.com/PolyphonyRequiem/polyphony/issues/175).

## Decision

Ship a **build-time source generator** that emits a structured
**verb→DTO→field** registry as both a generated C# constant and a
**CI-only JSON artifact** (`verb-output-schemas.json`), with the
verb→DTO mapping carried by an explicit
**`[VerbResult(typeof(X))]` attribute** on each `[Command]` method.

Concretely — the four design forks:

| Fork | Decision | Why |
|---|---|---|
| **Generation mechanism** | Roslyn incremental source generator (new project `src/Polyphony.SchemaGenerator/`). | AOT-clean, no runtime reflection over types, deterministic per-build output, runs in normal `dotnet build` without extra CI orchestration. |
| **Verb→DTO mapping** | Explicit `[VerbResult(typeof(X))]` attribute on each `[Command]` method; `[VerbGroup("name")]` on each `*Commands` partial class. | Makes the contract visible at the call site; survives refactors; trivial for the source generator to read; one-time mechanical pass over the existing ~50 methods. |
| **Artifact distribution** | CI-generated JSON artifact only (not checked in). Local devs regenerate via `dotnet build` (which writes the file to `artifacts/verb-output-schemas.json`). Pester lint and humans read from that path. | Avoids checked-in artifact drift; never blocks a PR on a "regenerate the JSON" review nit. The C# constant produced by the generator is the in-memory source of truth; the JSON is a convenience export. |
| **PR scope** | Full backfill in one PR — every `[Command]` method annotated, every nested DTO walked. | Mechanical, contained, splitting just creates more PRs and a partial registry has limited value (resolver lint can only enforce annotated verbs). |

## Mechanics

### New project: `src/Polyphony.SchemaGenerator/`

Roslyn 4.x incremental source generator. References
`Microsoft.CodeAnalysis.CSharp` only; no runtime dependency on
`Polyphony` itself. Wired into `Polyphony.csproj` via
`<ProjectReference ... OutputItemType="Analyzer" ReferenceOutputAssembly="false" />`.

The generator does three passes:

1. **Discover verb roots.** Find every method bearing
   `[Command(...)]` (ConsoleAppFramework's attribute) inside a class
   bearing `[VerbGroup("name")]`. Read the method's
   `[VerbResult(typeof(X))]` to learn its result type. Synthesize the
   canonical verb path: `"<group> <command-name>"` (e.g.
   `"agent compose-addendum"`). Discovery is **syntax-first** — find
   the `[Command]` attribute by name, then validate semantically.
   This insulates the generator from cross-generator ordering with
   ConsoleAppFramework v5 (which is itself a source generator and
   may not have a stable runtime metadata reference at our generator's
   pass time).
2. **Cross-check against `PolyphonyJsonContext`.** Find the partial
   class derived from `JsonSerializerContext` and read every
   `[JsonSerializable(typeof(X))]` attribute. Diagnostic-fail if any
   `[VerbResult]` type is **not** registered on the context (the verb
   would not actually serialize at runtime).
3. **Walk fields.** For each `[VerbResult]` root and each nested
   record-type it references transitively (BFS), extract per-property:
   - **JSON name** — after `[JsonPropertyName]`, falling back to
     context-level `PropertyNamingPolicy.SnakeCaseLower`.
   - **CLR type** — full name, for diagnostics only.
   - **`kind`** — one of `scalar`, `object`, `list`, `map`,
     `nullable`. Drives how `#175` walks `{{ X.output.field.path }}`:
     `object`/`map`/`list` indicate the lint may continue walking;
     `scalar` is a leaf.
   - **`type_ref`** — for `kind: object`, fully-qualified name of the
     nested record (resolvable in the registry's `types` map).
   - **`element_type_ref`** + **`element_kind`** — for `kind: list`.
   - **`key_kind`** + **`value_kind`** + **`value_type_ref`** — for
     `kind: map`. Map keys are serialized as strings under
     `SnakeCaseLower`; `key_kind` is informational.
   - **`nullable_annotation`** — Roslyn's `NullableAnnotation` flag
     (`Annotated` / `NotAnnotated` / `None`). Compiler intent.
   - **`ignore_condition`** — per-property `[JsonIgnore(Condition=…)]`
     if present, else context-level `DefaultIgnoreCondition`. The
     load-bearing field for `#175`.
   - **`can_omit_when_null`** — derived: `true` unless
     `ignore_condition == Never`. Honest about the reality that even
     a `required string Foo` can be null at runtime under
     `WhenWritingNull`. The lint uses this — *not* nullability — to
     decide whether a `default()` guard is required.

   We deliberately do **not** publish a derived `always_serialized`
   field. The duck flagged the original definition as unsound for
   reference types: the C# nullable annotation is compiler intent,
   not a runtime guarantee. Consumers compute their own definition
   from `ignore_condition` if needed.

### Generated artifact: C# constant

The generator emits a single file `VerbOutputSchemaCatalog.g.cs`
under `Polyphony` namespace:

```csharp
internal static class VerbOutputSchemaCatalog
{
    public const string Json = """
        {
          "version": 1,
          "verbs": { ... },
          "types": { ... }
        }
        """;
}
```

The constant is the in-memory source of truth. The JSON file written
to disk (below) is a convenience export of this constant.

### Generated artifact: JSON file

A separate console-app project `src/Polyphony.SchemaExporter/` (one
file: `Program.cs`, ~10 lines) references `Polyphony` and prints
`VerbOutputSchemaCatalog.Json` to a path passed via `--out`:

```csharp
var outPath = args[Array.IndexOf(args, "--out") + 1];
File.WriteAllText(outPath, VerbOutputSchemaCatalog.Json);
```

`Polyphony.csproj` declares an MSBuild AfterBuild target that
invokes the exporter via `dotnet run`:

```xml
<Target Name="ExportVerbOutputSchemas" AfterTargets="Build"
        Condition="'$(SkipSchemaExport)' != 'true'">
  <Exec Command="dotnet run --project $(MSBuildThisFileDirectory)..\Polyphony.SchemaExporter\Polyphony.SchemaExporter.csproj --no-build -- --out $(MSBuildThisFileDirectory)..\..\artifacts\verb-output-schemas.json" />
</Target>
```

Why an exporter project rather than a raw MSBuild task:

- **Stable contract.** The exporter reads a public C# symbol
  (`Polyphony.VerbOutputSchemaCatalog.Json`). We never parse
  generator output (`.g.cs`) from `obj/`, which is fragile and
  depends on `EmitCompilerGeneratedFiles`.
- **AOT-clean.** No reflection in the polyphony binary itself —
  the exporter touches one well-known constant.
- **Composable.** The same exporter can be invoked manually by a
  local developer (`dotnet run --project Polyphony.SchemaExporter`)
  or by CI without any extra orchestration.
- **`SkipSchemaExport=true` opt-out.** Useful in tight inner-loop
  builds where the artifact isn't needed (e.g. a `dotnet test`
  pass that doesn't touch lint).

The contract: `artifacts/verb-output-schemas.json` exists after
`dotnet build src/Polyphony/Polyphony.csproj` (relative to repo
root) unless `/p:SkipSchemaExport=true` is set.

### Two new attributes

Defined in `src/Polyphony/Annotations/`:

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class VerbGroupAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class VerbResultAttribute(Type resultType) : Attribute
{
    public Type ResultType { get; } = resultType;
}
```

Both are runtime-loadable but the source generator reads them at
compile time via Roslyn syntax/symbol APIs — no reflection at runtime.

### JSON shape

```json
{
  "version": 1,
  "verbs": {
    "agent compose-addendum": {
      "result_type": "Polyphony.Models.AgentComposeAddendumResult",
      "command_class": "Polyphony.Commands.AgentCommands"
    },
    "plan derive-ancestor-chain": {
      "result_type": "Polyphony.Models.PlanDeriveAncestorChainResult",
      "command_class": "Polyphony.Commands.PlanCommands"
    }
  },
  "types": {
    "Polyphony.Models.PlanDeriveAncestorChainResult": {
      "fields": [
        { "name": "root_id",        "kind": "scalar", "clr_type": "System.Int32",  "nullable_annotation": "NotAnnotated", "ignore_condition": "WhenWritingNull", "can_omit_when_null": true },
        { "name": "is_root_plan",   "kind": "scalar", "clr_type": "System.Boolean","nullable_annotation": "NotAnnotated", "ignore_condition": "WhenWritingNull", "can_omit_when_null": true },
        { "name": "parent_item_id", "kind": "scalar", "clr_type": "System.Int32",  "nullable_annotation": "Annotated",    "ignore_condition": "Never",            "can_omit_when_null": false },
        { "name": "ancestor_ids",   "kind": "scalar", "clr_type": "System.String", "nullable_annotation": "NotAnnotated", "ignore_condition": "WhenWritingNull", "can_omit_when_null": true },
        { "name": "ancestor_chain", "kind": "list",   "clr_type": "System.Collections.Generic.IReadOnlyList<System.String>", "element_kind": "scalar", "element_clr_type": "System.String", "nullable_annotation": "NotAnnotated", "ignore_condition": "WhenWritingNull", "can_omit_when_null": true },
        { "name": "error",          "kind": "scalar", "clr_type": "System.String", "nullable_annotation": "Annotated",    "ignore_condition": "WhenWritingNull", "can_omit_when_null": true }
      ]
    },
    "Polyphony.Models.PlanStatusResult": {
      "fields": [
        { "name": "items",   "kind": "list",   "element_kind": "object", "element_type_ref": "Polyphony.Models.PlanStatusItem", "nullable_annotation": "NotAnnotated", "ignore_condition": "WhenWritingNull", "can_omit_when_null": true },
        { "name": "summary", "kind": "object", "type_ref":     "Polyphony.Models.PlanStatusSummary",                            "nullable_annotation": "NotAnnotated", "ignore_condition": "WhenWritingNull", "can_omit_when_null": true }
      ]
    },
    "Polyphony.Models.ManifestReadPlanGenerationSnapshotResult": {
      "fields": [
        { "name": "ancestor_plan_generations", "kind": "map", "key_kind": "scalar", "key_clr_type": "System.String", "value_kind": "scalar", "value_clr_type": "System.Int32", "nullable_annotation": "NotAnnotated", "ignore_condition": "WhenWritingNull", "can_omit_when_null": true }
      ]
    }
  }
}
```

The lint (`#175`) consumes this directly. For `{{ X.output.foo.bar.baz }}`:

1. Resolve `X.command` → look up `verbs[...].result_type`.
2. Walk `foo` in that type's `fields`:
   - **Missing field** → fail.
   - **`can_omit_when_null: true`** + reference not wrapped in `default()` / `is defined` / `is not none` → fail.
3. If `foo.kind` is `object`, follow `type_ref` → repeat at `bar`.
4. If `foo.kind` is `list`, allow `bar` only as an integer index or a known list-method form (`length`, etc.).
5. If `foo.kind` is `map`, treat `bar` as a value lookup; recurse on `value_kind` / `value_type_ref` for `baz`.
6. If `foo.kind` is `scalar` and there's a `bar`, that's a deep reference into a primitive → fail.

### `error` / routing-style envelope

Most polyphony verbs follow the routing-style pattern:

```csharp
public string?  Error     { get; init; }
public string?  ErrorCode { get; init; }
```

Both are `WhenWritingNull` by default — they disappear from the
JSON object on success. That's intentional (success path stays
clean), but workflow YAMLs **must** route on them, which means
every reference needs a guard. The registry surfaces this
universally as `can_omit_when_null: true` on these fields, and
`#175` will require a `default()` guard or `is defined` check at
each reference site. There's nothing magic about `error` /
`error_code`; they're just the most common case of the general
omit-when-null rule.

### Distribution and consumption

- **Local dev:** `dotnet build` writes `artifacts/verb-output-schemas.json`. Pester lint resolves the path from a known location.
- **CI:** the same `dotnet build` writes the same file; CI optionally uploads it as a build artifact for downstream jobs and operator inspection.
- **Not checked in.** No PR review burden, no merge conflicts, no "did you regenerate" lint. The C# constant in the generated `.g.cs` carries the in-memory contract; the JSON is a convenience export.

#### Local-lint failure mode

If a contributor runs the Pester lint without first running
`dotnet build`, `artifacts/verb-output-schemas.json` won't exist
and the lint must fail with a clear, actionable message — not a
file-not-found stack trace:

> ❌ `artifacts/verb-output-schemas.json` not found.
>
> Run `dotnet build src/Polyphony/Polyphony.csproj` to generate
> it, then re-run the lint. (CI does this automatically; local
> first-run after a fresh checkout requires the build.)

This message belongs in the consuming lint (`#175`'s scope), but
the ADR commits us to it so the trade-off is honest at the
registry level.

### Compile-time diagnostics

The generator emits the following Roslyn diagnostics so
contract-violations surface at build time, not at Pester or runtime:

| Code | Severity | Trigger |
|---|---|---|
| `POLY1001` | Error | `[Command]` method without `[VerbResult]`. |
| `POLY1002` | Error | `[VerbResult]` type not registered in `PolyphonyJsonContext`. |
| `POLY1003` | Error | `[VerbGroup]` missing on a class containing `[Command]` methods. |
| `POLY1004` | Error | Multiple distinct `[VerbGroup]` declarations across partial-class declarations of the same type. |
| `POLY1005` | Warning | `[Command]` with multiple aliases (e.g. `[Command("check\|c")]`) — the registry can only key on one verb path. |
| `POLY1006` | Error | `[Command]` with empty name. |

`POLY1003` has one explicit exception: top-level commands
(`ValidateCommand`, `HealthCommand`, `HierarchyCommand`,
`ValidateConfigCommand`) are registered via `app.Add<T>()` with no
prefix. They carry `[VerbGroup("")]` and the verb path becomes the
`[Command]` name alone (`"validate"`, `"health"`, etc.).

### Test strategy

- **Unit tests** (in `tests/Polyphony.SchemaGenerator.Tests/`):
  - Generator round-trips a small fixture context to the expected JSON shape.
  - Each diagnostic (`POLY1001`–`POLY1006`) has a fixture that triggers it and asserts the diagnostic is reported.
- **Verb-coverage test** (in `tests/Polyphony.Tests/`): for every
  method bearing `[Command]` in the polyphony assembly, assert it
  bears `[VerbResult]` and the referenced type appears as a key in
  `verbs` in the parsed catalog. Catches an author shipping a new
  verb without annotation. Reflection-based; lives in
  `Polyphony.Tests` because it asserts on assembly state.
- **Verb-group ↔ runtime registration test** (in `tests/Polyphony.Tests/`):
  for every `app.Add<T>("group")` registration in `Program.cs`,
  assert the type `T` carries `[VerbGroup("group")]` (or
  `[VerbGroup("")]` for top-level). Catches drift between the
  attribute and the runtime registration — without this test, the
  registry would silently key verbs under the wrong path.
- **Sanity test:** the JSON file exists at
  `artifacts/verb-output-schemas.json` after the test project's
  build, parses as JSON, and has at least one verb under each known
  top-level group (`agent`, `branch`, `pr`, `plan`, `state`,
  `manifest`, `policy`, `worktree`, `lock`, `scope`, `edges`).
- **Golden tests** for the structural shapes that `#175` will rely on:
  - `PlanDeriveAncestorChainResult.parent_item_id` — `Annotated` +
    `Never` → `can_omit_when_null: false`.
  - `PlanDeriveAncestorChainResult.ancestor_chain` — `kind: list` of
    `scalar` strings.
  - `PlanStatusResult.items` — `kind: list` of `object` with the
    correct `element_type_ref`.
  - `PlanStatusResult.summary.plan_n_a` — `[JsonPropertyName]`
    override (the snake-case default would emit `plan_na`, not
    `plan_n_a`).
  - `ManifestReadPlanGenerationSnapshotResult.ancestor_plan_generations`
    — `kind: map`.
  - `BranchLoadTreeResult.work_tree.work_items.tasks` — chained
    `kind: object` → `kind: list` → `element_type_ref` → `id`.

### What this ADR does NOT cover

- The resolver lint itself — that's `#175`, scheduled next.
- The conductor Jinja inventory pin — that's `#174`.
- CLI strict-mode — that's `#165`.
- Sub-workflow `input_mapping` resolution — `#175`'s problem, listed in its body as design-TBD.
- Versioning of the schema. `version: 1` is reserved; bumping it is its own conversation. The schema is greenfield so a v1 ship is trivial.

### What this registry does NOT catch

The umbrella issue (`#172`) groups several recently-discovered bugs
as "the same disease" — a missing cross-layer contract. That framing
is right at the disease level but the registry treats only **two of
the three sub-symptoms**. Being explicit so we don't claim wins this
PR doesn't earn:

| Bug | Class | Caught by `#173` + `#175`? |
|---|---|---|
| **#6** (PR #168) | YAML reads `type_loader.output.type_name`; verb emits `type`. | ✅ Field-name drift — registry knows the verb's actual fields. |
| **#8** (PR #170) | `derive-ancestor-chain.parent_item_id` (`int?`) elided by `WhenWritingNull` on the root path. | ✅ `can_omit_when_null` flag tells the lint to require a guard. |
| **#11** (PR #183) | apex-item-dispatch terminal nodes emitted `{}` instead of the canonical 12-field envelope. | ❌ Workflow-author bug. The terminals are not polyphony verbs; their output schemas live in YAML, not C#. A separate workflow-level "terminal envelope conformance" lint is needed (out of scope here). |
| **#13a** (iter 9) | `preflight_failure_gate.prompt` references `commit_and_push_manifest.output.error` before the agent has run. | ❌ Control-flow availability, not schema resolution. Requires dominance/reachability analysis over the workflow graph (separate lint). |

Bugs #11 and #13a still belong to the same disease (cross-layer
validation gaps), but they need different cures. The registry
unblocks `#175`, and `#175` is honest about its scope: it
resolves field paths, it does not analyze control flow. Two
follow-up issues should be filed for the workflow-level lints
those bugs require.

## Consequences

**Wins**

- Bugs of the form "YAML references a verb output field that doesn't exist (#6) or that's silently elided at runtime (#8, #11)" become lint failures, not runtime crashes during dogfood.
- The C# DTO + serializer-context combination retains its position as the wire-shape source of truth; no parallel hand-maintained schema to keep in sync.
- The author of a new verb writes one attribute (`[VerbResult]`) — same cost as today's hand-curated `Emit(...)` call selecting the right `JsonTypeInfo`.

**Costs**

- One-time mechanical pass annotating ~50 existing `[Command]` methods.
- A new project (`Polyphony.SchemaGenerator`) and dev-time dependency on `Microsoft.CodeAnalysis.CSharp`. Not shipped in the polyphony binary (analyzer-only reference).
- Source generators add a small build-time cost; bounded by the size of `PolyphonyJsonContext` (~150 types).

**What we're betting on**

- That the JSON file is the right *export* shape — most non-C# consumers (lint, docs, any future external tooling) want JSON, not C#.
- That tying the verb→DTO mapping to an attribute pinned at the `[Command]` site is more durable than runtime introspection of `Emit(...)` callsites or hand-curated tables.
- That CI-only distribution is fine — local dev builds always write the file, so a clean checkout running Pester doesn't need a separate "fetch the schema" step.

## Migration

None — greenfield. The ADR ships in the same PR as the generator, the
attributes, and the annotation pass. Pester `lint-*.ps1` files don't
read the schema until `#175` lands.
