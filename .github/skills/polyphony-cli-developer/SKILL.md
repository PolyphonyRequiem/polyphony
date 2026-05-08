---
name: polyphony-cli-developer
description: >-
  Activate when adding, modifying, or testing polyphony CLI verbs (in
  `src/Polyphony/Commands/`). Covers the ConsoleAppFramework command pattern,
  primary-constructor DI, AOT JSON serialization via PolyphonyJsonContext,
  exit code conventions, the CommandTestBase scaffolding, and the JsonOutputContractTests.
user-invokable: false
---

# Polyphony CLI Developer Skill

For changes inside `src/Polyphony/Commands/` and adjacent infrastructure (DI registration,
JSON serializer context, exit codes, models). For changes to workflow YAMLs or PowerShell
helpers, use **polyphony-workflow-author**.

---

## The ConsoleAppFramework command pattern

Every command class follows the same shape (cited: `Commands/ValidateCommand.cs`,
`Commands/StateCommands.NextReady.cs`, `Commands/HierarchyCommand.cs`,
`Commands/ValidateConfigCommand.cs`):

```csharp
namespace Polyphony.Commands;

public sealed class FooCommand(
    SomeService service,
    IWorkItemRepository repository,
    ProcessConfig processConfig)            // primary-constructor DI
{
    /// <summary>Verb description for help.</summary>
    /// <param name="workItem">ADO work item ID</param>
    /// <param name="config">Path to .conductor/process-config.yaml</param>
    [Command("foo")]
    public async Task<int> Foo(
        int workItem,
        string config = ".conductor/process-config.yaml",
        CancellationToken ct = default)
    {
        // 1. Load via repository (cache lookup).
        // 2. Compute via injected service.
        // 3. Build a Result record.
        // 4. JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.<Result>);
        // 5. Console.WriteLine(...)
        // 6. return ExitCodes.<...>;
    }
}
```

Conventions:

- **`public sealed class`** with **primary constructor** for dependencies.
- **`[Command("verb-name")]`** attribute on the verb method.
- **`Task<int>` return** (or `int` if synchronous — `ValidateConfigCommand.cs:20`).
- **`CancellationToken ct = default`** as the **last** parameter on async commands.
- Parameter names map to flags via ConsoleAppFramework (e.g. `int workItem` →
  `--work-item`).
- Doc-comments on parameters become `--help` text — write them as if a workflow author
  will read them at the terminal.

---

## DI registration

Two places must change for a new command:

1. **`Program.cs:18-21`** — register the verb so ConsoleAppFramework dispatches to it:
   ```csharp
   app.Add<FooCommand>();
   ```

2. **`Infrastructure/PolyphonyServiceRegistration.cs:26-45`** — register any new
   singleton services the command depends on:
   ```csharp
   services.AddSingleton<FooService>();
   ```

`AddPolyphonyServices` already wires:

- `IWorkItemRepository`, `IProcessTypeStore`, `IContextStore`, `SqliteCacheStore`,
  `TwigPaths` — via `services.AddTwigCoreServices(twigDir)`
  (`PolyphonyServiceRegistration.cs:33`).
- `ProcessConfig` — singleton factory loaded lazily from `configPath`
  (`PolyphonyServiceRegistration.cs:37`).
- `PhaseDetector`, `HierarchyWalker`, `TransitionValidator` — singletons
  (`PolyphonyServiceRegistration.cs:40-42`).

You do not need to register the command class itself.

---

## Result records and JSON serialization

### Where models live

`src/Polyphony/Models/<Name>Result.cs` — one file per result record. Examples:
`ValidateResult.cs`, `HierarchyResult.cs`, `EdgesCheckResult.cs`. Pattern:

```csharp
namespace Polyphony;

public sealed record FooResult
{
    public required int WorkItemId { get; init; }
    public required string Status { get; init; }
    public string? Message { get; init; }    // optional → omitted from JSON when null
}
```

- `public sealed record` with `required` init-only properties.
- Nullable properties (`string?`, `Foo?`) are omitted from JSON output by virtue of
  `DefaultIgnoreCondition = WhenWritingNull`
  (`PolyphonyJsonContext.cs:14`).

### AOT-friendly serialization via PolyphonyJsonContext

`src/Polyphony/PolyphonyJsonContext.cs` is the source-generated `JsonSerializerContext`
required for trim-safe / AOT-publishable JSON. Every result type must be listed:

```csharp
[JsonSerializable(typeof(ValidateResult))]
[JsonSerializable(typeof(HierarchyResult))]
[JsonSerializable(typeof(HierarchyResult[]))]
[JsonSerializable(typeof(EdgesCheckResult))]
[JsonSerializable(typeof(ConfigValidationResult))]
[JsonSerializable(typeof(ConfigValidationDiagnostic[]))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class PolyphonyJsonContext : JsonSerializerContext;
```

Adding a new `FooResult`? Add `[JsonSerializable(typeof(FooResult))]` here. The
test `JsonOutputContractTests` (see below) will fail loudly if the serializer is
missing the type.

### snake_case is mandatory

Property names in C# are PascalCase; in JSON they're snake_case. This is enforced by
`PropertyNamingPolicy = SnakeCaseLower` and verified per-command by
`JsonOutputContractTests.AssertNoPascalCase`
(`tests/Polyphony.Tests/Commands/JsonOutputContractTests.cs:497-501`).

---

## Exit codes

`src/Polyphony/ExitCodes.cs` — these are the **only** exit codes a command may return:

```text
0 Success         — JSON output is valid
1 RoutingFailure  — invalid lifecycle event / illegal state transition
2 ConfigError     — config file missing, malformed, or invalid
3 CacheError      — twig cache inaccessible / work item not found
```

Pattern when a work item is not found (cited: `StateCommands.NextReady.cs:25-32`,
`ValidateCommand.cs:24-29`, `HierarchyCommand.cs:22-27`):

```csharp
var item = await repository.GetByIdAsync(workItem, ct);
if (item is null)
{
    Console.WriteLine($$"""{"error":"Work item {{workItem}} not found","work_item_id":{{workItem}}}""");
    return ExitCodes.CacheError;
}
```

Note the literal raw-string interpolation: this exact format
(`{"error":"…","work_item_id":N}`) is required by `JsonOutputContractTests
.AllCommands_NotFound_ErrorJsonFormatConsistent`
(`JsonOutputContractTests.cs:427-453`).

---

## Post-condition verification — when origin must hold blob X

Some verbs have a post-condition stronger than "we ran successfully": they
must guarantee that **after the verb returns, the remote holds a specific
blob at a specific path**. Examples:

- `manifest commit-and-push` — downstream verbs read the manifest from
  `origin/{branch}`; if the local commit was created in a prior run but
  never pushed (crash, transient network failure, retry), the next run
  silently no-ops on `git add` and the manifest never reaches origin.
  Issue #192 was this exact pattern.
- `plan commit-and-push` — same Class B bug shape for plan files.

Without an explicit remote-side check, the verb stages, sees nothing
changed, declares no_op, and exits 0. Downstream readers then fail with
"file not found at origin/...".

### The contract

Use `IPostconditionVerifier` from `Polyphony.Postconditions`:

```csharp
public sealed class FooCommands(IGitClient git, IPostconditionVerifier postconditions)
{
    public async Task<int> CommitAndPush(...)
    {
        // ... stage, classify changes, commit-if-needed ...

        if (nothingStaged)
        {
            // HEAD already has the right blob locally. But does origin?
            var expectations = paths
                .Select(p => new PostconditionExpectation(p, File.ReadAllText(p)))
                .ToList();
            var outcome = await postconditions.VerifyAsync(branch, expectations, "origin", ct);

            return outcome switch
            {
                PostconditionOutcome.Satisfied        => EmitNoOp(...),  // truly idempotent
                PostconditionOutcome.NeedsPush        => PushAndReport(...),
                PostconditionOutcome.Conflict         => PushAndReport(...),  // let git reject if non-ff
                _ => throw new InvalidOperationException($"unhandled outcome: {outcome.GetType().Name}")
            };
        }

        // ... normal commit + push path ...
    }
}
```

### Three outcomes

`PostconditionOutcome` is a discriminated union (abstract record + sealed
nested cases, same shape as `RebaseOutcome` in
`src/Polyphony/Infrastructure/Processes/`):

| Case        | Meaning                                       | Typical action     |
| ----------- | --------------------------------------------- | ------------------ |
| `Satisfied` | Origin holds the expected blob at every path. | No-op success.     |
| `NeedsPush` | Origin is missing one or more paths.          | Push HEAD.         |
| `Conflict`  | Origin has different content for some paths.  | Push (let git reject as non-ff) or escalate. |

The verifier treats `git fetch` failure (most often "remote ref doesn't
exist yet" — first push of a fresh branch) as `NeedsPush` for all
expectations. Per-path read failures are also treated as missing.

### Switch hygiene

Always include a `default` arm that throws — the compiler can't prove
exhaustiveness across the assembly boundary, and a future `PostconditionOutcome`
case must fail loudly rather than silently fall through.

### Tests

- For verbs that drive the verifier from a single path:
  `FakePostconditionVerifier` in `tests/Polyphony.Tests/TestFixtures/`
  records calls and lets you set `NextOutcome` to drive the switch arms
  without stubbing `git fetch` + `git show`.
- For the verifier itself: stub at the `FakeProcessRunner` level and use
  the real `PostconditionVerifier(GitClient(runner))`. See
  `tests/Polyphony.Tests/Postconditions/PostconditionVerifierTests.cs`.
- Verbs that mutate `Environment.CurrentDirectory` (most file-reading
  ones) must opt into `[Collection("CwdSerial")]` so they don't race
  with sibling cwd-mutating classes.

---

## Test scaffolding

### `CommandTestBase` (`tests/Polyphony.Tests/Commands/CommandTestBase.cs`)

Inherits to give you:

- `SqliteCacheStore Store` — in-memory SQLite (`Data Source=:memory:`).
- `SqliteWorkItemRepository Repository` — twig repository wired to `Store`.
- `ProcessConfig Config` — a default 3-tier hierarchy with `To Do`/`Doing`/`Done`
  transitions; type names are loaded from the canonical defaults at
  `CommandTestBase.cs:93-113` (do NOT bake the names into your own assertions —
  use `Config.Types.Keys` or facets instead).
- `SeedAsync(params WorkItem[])` — persist work items into the store
  (`CommandTestBase.cs:87-91`).
- `CaptureConsoleAsync(Func<Task<int>>)` — runs an async command body, captures
  stdout, returns `(exitCode, output)` (`CommandTestBase.cs:58-82`). Holds
  `ConsoleTestLock.Lock` to prevent stdout races across parallel tests.

### Template for a new `FooCommandTests` class

```csharp
using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class FooCommandTests : CommandTestBase
{
    private FooCommand CreateCommand() => new(/* deps from base */ Repository, Config);

    [Fact]
    public async Task Foo_WorkItemNotFound_ReturnsCacheErrorExitCode()
    {
        var cmd = CreateCommand();
        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.Foo(999));
        exitCode.ShouldBe(ExitCodes.CacheError);
    }

    [Fact]
    public async Task Foo_HappyPath_ReturnsExpectedJson()
    {
        var item = new WorkItemBuilder()
            .WithId(100).WithType("Epic").WithTitle("Test").WithState("To Do")
            .Build();
        await SeedAsync(item);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Foo(100));
        exitCode.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.FooResult);
        result.ShouldNotBeNull();
        // …
    }
}
```

(Pattern from `tests/Polyphony.Tests/Commands/ValidateCommandTests.cs:15-75`,
`tests/Polyphony.Tests/Commands/StateNextReadyTests.cs`.)

### `JsonOutputContractTests` — what to add when you ship a new verb

`tests/Polyphony.Tests/Commands/JsonOutputContractTests.cs` enforces the cross-cutting
JSON contract. When adding `polyphony foo`, add equivalents of these existing tests
(cited line spans):

| Required test                                         | Pattern from                                                                       |
|--------------------------------------------------------|------------------------------------------------------------------------------------|
| `Foo_SnakeCaseFieldNames_PresentInRawJson`             | `Validate_SnakeCaseFieldNames_PresentInRawJson`                                     |
| `Foo_NullFieldsOmitted_WhenWritingNull`                | `Validate_ValidTransition_NullFieldsOmitted` / `Hierarchy_NullTagsOmitted_WhenWritingNull` |
| `Foo_DeserializationRoundTrip_FieldsMapped`            | `Validate_DeserializationRoundTrip_FieldsMapped` / `Hierarchy_DeserializationRoundTrip_FieldsMapped` |
| `Foo_NotFound_ReturnsErrorJson_WithCacheErrorExitCode` | `Validate_NotFound_ReturnsErrorJson_WithCacheErrorExitCode` / `Hierarchy_NotFound_ReturnsErrorJson_WithCacheErrorExitCode` |
| Update `AllCommands_NotFound_ErrorJsonFormatConsistent` | extend the `foreach` loop to also exercise `FooCommand`                            |

Use the helper `AssertNoPascalCase` — verifies `"WorkItemId"` etc. do
**not** appear in the raw JSON.

---

## Worked example: scaffolding `polyphony note`

A hypothetical command: `polyphony note --work-item N --text "…"`. (This is not a real
command — it's an exercise. `twig note` already exists; this is purely to walk the file
shape.) Files to touch, in order:

1. **`src/Polyphony/Models/NoteResult.cs`** — new file.
   ```csharp
   namespace Polyphony;
   public sealed record NoteResult
   {
       public required int WorkItemId { get; init; }
       public required string Text { get; init; }
       public required bool Posted { get; init; }
       public string? Message { get; init; }
   }
   ```

2. **`src/Polyphony/PolyphonyJsonContext.cs`** — add the `[JsonSerializable]`:
   ```diff
    [JsonSerializable(typeof(HierarchyResult[]))]
   +[JsonSerializable(typeof(NoteResult))]
    [JsonSerializable(typeof(ConfigValidationResult))]
   ```

3. **`src/Polyphony/Commands/NoteCommand.cs`** — new file.
   ```csharp
   using System.Text.Json;
   using ConsoleAppFramework;
   using Twig.Domain.Interfaces;

   namespace Polyphony.Commands;

   public sealed class NoteCommand(IWorkItemRepository repository /* , INotePoster poster */)
   {
       /// <summary>Add a comment to a work item.</summary>
       /// <param name="workItem">ADO work item ID</param>
       /// <param name="text">Comment text</param>
       [Command("note")]
       public async Task<int> Note(int workItem, string text, CancellationToken ct = default)
       {
           var item = await repository.GetByIdAsync(workItem, ct);
           if (item is null)
           {
               Console.WriteLine($$"""{"error":"Work item {{workItem}} not found","work_item_id":{{workItem}}}""");
               return ExitCodes.CacheError;
           }

           // … invoke poster …

           var result = new NoteResult { WorkItemId = workItem, Text = text, Posted = true };
           Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.NoteResult));
           return ExitCodes.Success;
       }
   }
   ```

4. **`src/Polyphony/Program.cs`** — register the verb:
   ```diff
    app.Add<HierarchyCommand>();
   +app.Add<NoteCommand>();
    app.Run(args);
   ```

5. **`src/Polyphony/Infrastructure/PolyphonyServiceRegistration.cs`** — register any
   new dependency:
   ```diff
    services.AddSingleton<TransitionValidator>();
   +services.AddSingleton<INotePoster, AdoNotePoster>();
    return services;
   ```

6. **`tests/Polyphony.Tests/Commands/NoteCommandTests.cs`** — full template above. Cover
   at minimum: not-found → `CacheError`, happy path → `Success` with deserialized JSON,
   stdout contains snake_case field names.

7. **`tests/Polyphony.Tests/Commands/JsonOutputContractTests.cs`** — add the five
   required tests in the table above for `Note_*`, and extend
   `AllCommands_NotFound_ErrorJsonFormatConsistent` to include `noteCmd.Note(missingId,
   "x")`.

8. **`workflows/`** — no change required for the verb to be reachable; helper scripts
   shell out by name.

That's the entire diff shape for any new verb.

---

## Version reporting and min-version enforcement

The CLI reports its own version via `AssemblyInformationalVersion` (where
MinVer writes the real SemVer including pre-release / build-metadata),
**not** `Assembly.GetName().Version` (the numeric AssemblyVersion that
MinVer pins to a stable `X.Y.Z.0` for binder-stability). Two places read
this:

- `Commands/StateCommands.CheckPolyphonyCli()` — the canonical pattern.
- `Commands/HealthCommand.ResolvePolyphonyVersion()` — mirrors the same
  shape.

If you need the CLI version anywhere new, copy that 3-line pattern; do
not reach for `Assembly.GetName().Version`.

### `state preflight` / `state preflight-lite` own min-version enforcement

`polyphony state preflight` and `polyphony state preflight-lite` accept
two optional flags that drive workflow-vs-CLI version compatibility:

- `--workflow-yaml <path>` — when supplied, the verb reads
  `workflow.metadata.min_polyphony_version` from the YAML and uses it as
  the required floor.
- `--required-version <semver>` — explicit override (testing seam). Wins
  when both are supplied.

Comparison is SemVer-aware and ignores `+build-metadata`. Mismatch
manifests as a preflight check with `passed: false` and a `detail`
message; the existing `preflight_gate` routes a failed preflight to
retry/abort. **There is no Proceed Anyway.**

This deliberately lives in `state preflight*` rather than `health`
because `preflight_gate` already understands the preflight JSON shape,
and because sub-workflows that invoke `preflight-lite` standalone get
coverage too (no root-only hole).

See [`docs/decisions/versioning-strategy.md`](../../../docs/decisions/versioning-strategy.md)
for the full rationale.

