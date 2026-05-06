using System.Text.Json;
using Polyphony;
using Polyphony.Commands;
using Polyphony.Manifest;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony worklist build</c>. Seeds work items
/// into the in-memory SQLite cache (via <see cref="CommandTestBase"/>)
/// and a real <c>run.yaml</c> in a temp directory, drives the verb via
/// <c>--manifest-path</c>, and asserts on the JSON the verb emits.
///
/// <para>The default human output is also covered to ensure the wave
/// summary renders the key fields.</para>
/// </summary>
public sealed class WorklistCommandsBuildTests : CommandTestBase, IDisposable
{
    private readonly string _tempDir;
    private readonly string _manifestPath;

    public WorklistCommandsBuildTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "polyphony-worklist-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _manifestPath = Path.Combine(_tempDir, "run.yaml");
    }

    void IDisposable.Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        base.Dispose();
    }

    private WorklistCommands CreateCommand() => new(Repository);

    private void SaveManifest(
        int rootId,
        Dictionary<string, int>? planGenerations = null,
        List<MergedPlanPrEntry>? ledger = null)
    {
        var manifest = new RunManifest
        {
            Schema = 1,
            RootId = rootId,
            PlatformProject = "github.com/owner/repo",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "test",
            BranchModelVersion = 1,
            PlanGenerations = planGenerations ?? new Dictionary<string, int>(StringComparer.Ordinal),
            MergedPlanPrs = ledger ?? new List<MergedPlanPrEntry>(),
        };
        RunManifestStore.Save(_manifestPath, manifest);
    }

    private static MergedPlanPrEntry MakeEntry(
        int prNumber,
        string itemKey,
        int previousGeneration,
        int currentGeneration,
        DateTime recordedAt)
        => new()
        {
            PrNumber = prNumber,
            ItemKey = itemKey,
            MergeCommit = $"deadbeef{prNumber:x8}",
            PreviousGeneration = previousGeneration,
            CurrentGeneration = currentGeneration,
            RecordedAt = recordedAt,
        };

    private static WorklistResult ParseJson(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.WorklistResult)!;

    // ─── Input validation ───────────────────────────────────────────────

    [Fact]
    public async Task RootId_NonPositive_EmitsInvalidArgumentError()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 0, manifestPath: _manifestPath, json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("--root-id must be positive");
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Waves.ShouldBeEmpty();
    }

    // ─── Manifest discovery / load failures ─────────────────────────────

    [Fact]
    public async Task ManifestPath_DoesNotExist_EmitsManifestNotFoundError()
    {
        var cmd = CreateCommand();
        var missing = Path.Combine(_tempDir, "no-such-manifest.yaml");
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: missing, json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.ErrorCode.ShouldBe("manifest_not_found");
        result.Error.ShouldNotBeNull();
        result.RootId.ShouldBe(100);
        result.Waves.ShouldBeEmpty();
    }

    [Fact]
    public async Task Manifest_MalformedYaml_EmitsManifestInvalidError()
    {
        File.WriteAllText(_manifestPath, "this: is: not: valid: yaml: [\n  - broken");
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.ErrorCode.ShouldBe("manifest_invalid");
        result.Error.ShouldNotBeNull();
        result.Waves.ShouldBeEmpty();
    }

    [Fact]
    public async Task Manifest_RootIdMismatch_EmitsRootIdMismatchError()
    {
        SaveManifest(rootId: 100);
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 999, manifestPath: _manifestPath, json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.ErrorCode.ShouldBe("root_id_mismatch");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("100");
        result.Error!.ShouldContain("999");
    }

    // ─── Tree shape: wave assignment ────────────────────────────────────

    [Fact]
    public async Task SingleRoot_NoChildren_EmitsOneWaveWithRootOnly()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        SaveManifest(rootId: 100);

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.Error.ShouldBeNull();
        result.Waves.Count.ShouldBe(1);
        result.Waves[0].WaveIndex.ShouldBe(0);
        result.Waves[0].Items.Count.ShouldBe(1);
        result.Waves[0].Items[0].ItemId.ShouldBe(100);
        result.Waves[0].Items[0].ParentItemId.ShouldBe(0);
    }

    [Fact]
    public async Task RootPlusTwoChildren_EmitsTwoWaves()
    {
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(200).WithType("Issue").WithParentId(100).Build(),
            new WorkItemBuilder().WithId(300).WithType("Issue").WithParentId(100).Build());
        SaveManifest(rootId: 100);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        var result = ParseJson(output);
        result.Waves.Count.ShouldBe(2);

        result.Waves[0].WaveIndex.ShouldBe(0);
        result.Waves[0].Items.Single().ItemId.ShouldBe(100);

        result.Waves[1].WaveIndex.ShouldBe(1);
        // Stable order: ascending by id.
        result.Waves[1].Items.Select(i => i.ItemId).ShouldBe(new[] { 200, 300 });
        result.Waves[1].Items.ShouldAllBe(i => i.ParentItemId == 100);
    }

    [Fact]
    public async Task RootPlusChildPlusGrandchild_EmitsThreeWaves()
    {
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(200).WithType("Issue").WithParentId(100).Build(),
            new WorkItemBuilder().WithId(310).WithType("Task").WithParentId(200).Build());
        SaveManifest(rootId: 100);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        var result = ParseJson(output);

        result.Waves.Count.ShouldBe(3);
        result.Waves[0].Items.Single().ItemId.ShouldBe(100);
        result.Waves[1].Items.Single().ItemId.ShouldBe(200);
        result.Waves[1].Items.Single().ParentItemId.ShouldBe(100);
        result.Waves[2].Items.Single().ItemId.ShouldBe(310);
        result.Waves[2].Items.Single().ParentItemId.ShouldBe(200);
    }

    [Fact]
    public async Task RootMissingFromTwig_EmitsUnknownStatusButStillWalks()
    {
        // No twig items seeded — root is not in the cache.
        SaveManifest(rootId: 100);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        var result = ParseJson(output);
        result.Waves.Count.ShouldBe(1);
        var rootRow = result.Waves[0].Items.Single();
        rootRow.ItemId.ShouldBe(100);
        rootRow.PlanStatus.ShouldBe("unknown");
    }

    // ─── Plan status mapping ────────────────────────────────────────────

    [Fact]
    public async Task PlanStatus_NoLedgerEntry_IsPending()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        SaveManifest(rootId: 100);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        var result = ParseJson(output);
        var row = result.Waves[0].Items.Single();
        row.PlanStatus.ShouldBe("pending");
        row.PlanPrNumber.ShouldBeNull();
        row.CurrentGeneration.ShouldBe(0);
    }

    [Fact]
    public async Task PlanStatus_LedgerEntryForRoot_IsMergedWithLatestPrAndGeneration()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        var recorded = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        SaveManifest(
            rootId: 100,
            planGenerations: new() { ["root"] = 1 },
            ledger: new()
            {
                MakeEntry(prNumber: 42, itemKey: "root", previousGeneration: 0, currentGeneration: 1, recordedAt: recorded),
            });

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        var result = ParseJson(output);
        var row = result.Waves[0].Items.Single();
        row.ItemId.ShouldBe(100);
        row.PlanStatus.ShouldBe("merged");
        row.PlanPrNumber.ShouldBe(42);
        row.CurrentGeneration.ShouldBe(1);
    }

    [Fact]
    public async Task PlanStatus_MultipleLedgerEntries_PicksMostRecentByRecordedAt()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        var early = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var late = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        SaveManifest(
            rootId: 100,
            planGenerations: new() { ["root"] = 2 },
            ledger: new()
            {
                MakeEntry(prNumber: 42, itemKey: "root", previousGeneration: 0, currentGeneration: 1, recordedAt: early),
                MakeEntry(prNumber: 51, itemKey: "root", previousGeneration: 1, currentGeneration: 2, recordedAt: late),
            });

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        var result = ParseJson(output);
        var row = result.Waves[0].Items.Single();
        row.PlanPrNumber.ShouldBe(51);
        row.CurrentGeneration.ShouldBe(2);
    }

    [Fact]
    public async Task PlanStatus_MixedAcrossWaves_PerItemStatusCorrect()
    {
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(200).WithType("Issue").WithParentId(100).Build(),
            new WorkItemBuilder().WithId(300).WithType("Issue").WithParentId(100).Build());
        var t = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        SaveManifest(
            rootId: 100,
            planGenerations: new()
            {
                ["root"] = 1,
                ["200"] = 1,
                // 300 has no generation entry — should default to 0.
            },
            ledger: new()
            {
                MakeEntry(prNumber: 42, itemKey: "root", previousGeneration: 0, currentGeneration: 1, recordedAt: t),
                MakeEntry(prNumber: 47, itemKey: "200", previousGeneration: 0, currentGeneration: 1, recordedAt: t),
            });

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        var result = ParseJson(output);
        result.Waves.Count.ShouldBe(2);

        var root = result.Waves[0].Items.Single();
        root.PlanStatus.ShouldBe("merged");
        root.PlanPrNumber.ShouldBe(42);

        var wave1 = result.Waves[1].Items.ToDictionary(i => i.ItemId);
        wave1[200].PlanStatus.ShouldBe("merged");
        wave1[200].PlanPrNumber.ShouldBe(47);
        wave1[200].CurrentGeneration.ShouldBe(1);
        wave1[300].PlanStatus.ShouldBe("pending");
        wave1[300].PlanPrNumber.ShouldBeNull();
        wave1[300].CurrentGeneration.ShouldBe(0);
    }

    // ─── JSON shape contract ────────────────────────────────────────────

    [Fact]
    public async Task JsonFlag_EmitsRoundTrippableSnakeCaseJson()
    {
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(200).WithType("Issue").WithParentId(100).Build());
        SaveManifest(
            rootId: 100,
            planGenerations: new() { ["root"] = 1 },
            ledger: new()
            {
                MakeEntry(prNumber: 42, itemKey: "root", previousGeneration: 0, currentGeneration: 1,
                    recordedAt: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)),
            });

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));

        // Round-trips through the source-gen context.
        var roundTripped = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.WorklistResult);
        roundTripped.ShouldNotBeNull();
        roundTripped!.RootId.ShouldBe(100);
        roundTripped.Waves.Count.ShouldBe(2);

        // snake_case wire keys (PolyphonyJsonContext convention).
        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"waves\"");
        output.ShouldContain("\"wave_index\"");
        output.ShouldContain("\"items\"");
        output.ShouldContain("\"item_id\"");
        output.ShouldContain("\"parent_item_id\"");
        output.ShouldContain("\"plan_status\"");
        output.ShouldContain("\"plan_pr_number\"");
        output.ShouldContain("\"current_generation\"");

        // No PascalCase leakage.
        output.Contains("\"RootId\"").ShouldBeFalse();
        output.Contains("\"WaveIndex\"").ShouldBeFalse();
        output.Contains("\"ItemId\"").ShouldBeFalse();
        output.Contains("\"PlanStatus\"").ShouldBeFalse();

        // Null fields omitted on success (no Error / ErrorCode keys).
        output.Contains("\"error\"").ShouldBeFalse();
        output.Contains("\"error_code\"").ShouldBeFalse();
    }

    [Fact]
    public async Task JsonFlag_OnError_EmitsErrorAndErrorCode()
    {
        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: -1, manifestPath: _manifestPath, json: true));
        output.ShouldContain("\"error\"");
        output.ShouldContain("\"error_code\"");
        output.ShouldContain("\"invalid_argument\"");
    }

    // ─── Human output ───────────────────────────────────────────────────

    [Fact]
    public async Task DefaultHumanOutput_ContainsKeyFields()
    {
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(200).WithType("Issue").WithParentId(100).Build(),
            new WorkItemBuilder().WithId(300).WithType("Issue").WithParentId(100).Build());
        SaveManifest(
            rootId: 100,
            planGenerations: new() { ["root"] = 2, ["200"] = 1 },
            ledger: new()
            {
                MakeEntry(prNumber: 42, itemKey: "root", previousGeneration: 0, currentGeneration: 1,
                    recordedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                MakeEntry(prNumber: 51, itemKey: "root", previousGeneration: 1, currentGeneration: 2,
                    recordedAt: new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)),
                MakeEntry(prNumber: 47, itemKey: "200", previousGeneration: 0, currentGeneration: 1,
                    recordedAt: new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc)),
            });

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: false));
        exit.ShouldBe(ExitCodes.Success);

        output.ShouldContain("worklist:");
        output.ShouldContain("root=100");
        output.ShouldContain("waves=2");
        output.ShouldContain("wave 0:");
        output.ShouldContain("wave 1:");
        output.ShouldContain("item 100");
        output.ShouldContain("item 200");
        output.ShouldContain("item 300");
        output.ShouldContain("parent=0");
        output.ShouldContain("parent=100");
        output.ShouldContain("status=merged");
        output.ShouldContain("status=pending");
        output.ShouldContain("pr=#51");
        output.ShouldContain("pr=#47");
        output.ShouldContain("generation=2");
        output.ShouldContain("generation=1");
        output.ShouldContain("generation=0");
    }

    [Fact]
    public async Task DefaultHumanOutput_OnError_RendersErrorPrefixAndCode()
    {
        var cmd = CreateCommand();
        var missing = Path.Combine(_tempDir, "nope.yaml");
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: missing, json: false));
        output.ShouldContain("error:");
        output.ShouldContain("manifest_not_found");
    }
}
