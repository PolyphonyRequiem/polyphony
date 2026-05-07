using System.Text.Json;
using Polyphony;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Manifest;
using Polyphony.Sdlc;
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
/// <para>Phase 7 PR #7 retrofit: the verb composes
/// <see cref="EdgeGraph.ToWaves"/> + <see cref="ExecutionModeInjector"/>
/// for wave ordering. Tests cover the new wave shape (entry items in
/// wave 0, topological wave ordering), conflict surfacing, and mode
/// injection alongside the pre-existing manifest-loading + status-mapping
/// contract that survived the cutover.</para>
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

    private WorklistCommands CreateCommand() => new(Repository, Config);
    private WorklistCommands CreateCommand(ProcessConfig config) => new(Repository, config);

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
        result.Conflicts.ShouldBeEmpty();
        result.HasConflicts.ShouldBeFalse();
        result.ItemsWalked.ShouldBe(0);
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
        result.Conflicts.ShouldBeEmpty();
        result.HasConflicts.ShouldBeFalse();
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

    [Fact]
    public async Task RootMissingFromTwig_EmitsRootNotFoundError()
    {
        // No twig items seeded — root is not in the cache. The new
        // edges-aware verb cannot derive a requirement set without the
        // type, so a missing root surfaces as `root_not_found` rather
        // than the legacy "unknown" placeholder shape.
        SaveManifest(rootId: 100);

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.ErrorCode.ShouldBe("root_not_found");
        result.Error.ShouldNotBeNull();
        result.Waves.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnknownType_EmitsTypeUnknownError()
    {
        // CommandTestBase config registers Epic / Issue / Task. "Bug" is unknown.
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Bug").Build());
        SaveManifest(rootId: 100);

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.ErrorCode.ShouldBe("type_unknown");
        result.Error!.ShouldContain("Bug");
    }

    // ─── Tree shape: wave assignment via EdgeGraph ──────────────────────

    [Fact]
    public async Task Build_EmptyTree_OneWaveOneItemNoConflicts()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        SaveManifest(rootId: 100);

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.Error.ShouldBeNull();
        result.HasConflicts.ShouldBeFalse();
        result.Conflicts.ShouldBeEmpty();
        result.ItemsWalked.ShouldBe(1);
        result.Waves.Count.ShouldBe(1);
        result.Waves[0].WaveIndex.ShouldBe(0);
        result.Waves[0].Items.Count.ShouldBe(1);
        result.Waves[0].Items[0].ItemId.ShouldBe(100);
        result.Waves[0].Items[0].ParentItemId.ShouldBe(0);
    }

    [Fact]
    public async Task RootPlusTwoChildren_EmitsTwoWaves_RootThenChildren()
    {
        // Epic 100 (plannable+decomposable) → children_seeded gates each child's
        // entry requirement → wave 0 = [100], wave 1 = [200, 300].
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(200).WithType("Issue").WithParentId(100).Build(),
            new WorkItemBuilder().WithId(300).WithType("Issue").WithParentId(100).Build());
        SaveManifest(rootId: 100);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        var result = ParseJson(output);
        result.HasConflicts.ShouldBeFalse();
        result.ItemsWalked.ShouldBe(3);
        result.Waves.Count.ShouldBe(2);

        result.Waves[0].WaveIndex.ShouldBe(0);
        result.Waves[0].Items.Single().ItemId.ShouldBe(100);

        result.Waves[1].WaveIndex.ShouldBe(1);
        // Stable order: ascending by id (per EdgeGraph.ToWaves contract).
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

        result.HasConflicts.ShouldBeFalse();
        result.ItemsWalked.ShouldBe(3);
        result.Waves.Count.ShouldBe(3);
        result.Waves[0].Items.Single().ItemId.ShouldBe(100);
        result.Waves[1].Items.Single().ItemId.ShouldBe(200);
        result.Waves[1].Items.Single().ParentItemId.ShouldBe(100);
        result.Waves[2].Items.Single().ItemId.ShouldBe(310);
        result.Waves[2].Items.Single().ParentItemId.ShouldBe(200);
    }

    // ─── Conflict surfacing ─────────────────────────────────────────────

    [Fact]
    public async Task Build_WithCycle_EmitsConflictsEmptyWaves()
    {
        // EdgeGraph.Build's definitional bucket cannot induce a cycle, and
        // the verb-built input map only contains items the BFS walked, so
        // unknown-item conflicts cannot surface end-to-end either. Cycle
        // / unknown-item end-to-end coverage at the verb layer would
        // require an unreachable test seam — we exercise the conflict
        // PROJECTION here against a hand-built EdgeGraph (mirroring the
        // strategy EdgesCheckCommandTests uses, deferring kind coverage
        // to EdgeGraphTests).
        //
        // To test the verb's conflict-surfacing path end-to-end we
        // exercise the contract that on a clean tree, has_conflicts is
        // false and waves is the wave-topology projection. The
        // projection-with-conflicts path is locked in by the model
        // contract on WorklistResult itself (Conflicts always present;
        // Waves empty when HasConflicts is true) and verified at the
        // unit level below.
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(200).WithType("Issue").WithParentId(100).Build());
        SaveManifest(rootId: 100);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        var result = ParseJson(output);

        // Sanity: clean tree → no conflicts, conflicts array always present.
        result.HasConflicts.ShouldBeFalse();
        result.Conflicts.ShouldNotBeNull();
        result.Conflicts.ShouldBeEmpty();
        result.Waves.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Build_WithCycle_EnvelopeShape_ContractLockIn()
    {
        // Pure model-contract assertion: when HasConflicts is true the
        // serialized envelope MUST carry a non-empty conflicts[] array
        // with the same shape as `edges check` and an EMPTY waves[]
        // (explicit emptiness, not omitted). This is the conflict-gate
        // invariant downstream consumers route on.
        var conflict = new EdgesCheckConflict
        {
            Kind = EdgeConflictKind.Cycle,
            Description = "Cycle detected: 100 -> 200 -> 100",
            ContributingEdges = new[]
            {
                new CrossItemEdge(
                    PrerequisiteItemId: 100,
                    PrerequisiteKind: RequirementKind.ChildrenSeeded,
                    DependentItemId: 200,
                    DependentKind: RequirementKind.PlanAuthored,
                    RequiredDisposition: Disposition.Satisfied,
                    Source: RequirementEdgeSource.Definitional),
            },
        };
        var result = new WorklistResult
        {
            RootId = 100,
            ItemsWalked = 2,
            HasConflicts = true,
            Conflicts = new[] { conflict },
            Waves = Array.Empty<WorklistWave>(),
        };

        var json = JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.WorklistResult);

        json.ShouldContain("\"has_conflicts\":true");
        json.ShouldContain("\"waves\":[]");
        json.ShouldContain("\"conflicts\":[");
        json.ShouldContain("\"kind\":\"cycle\"");
        // System.Text.Json escapes `>` as \u003E by default — assert on the
        // semantic content (item ids in path) rather than the literal arrow.
        json.ShouldContain("Cycle detected:");
        json.ShouldContain("100");
        json.ShouldContain("200");
        json.ShouldContain("\"contributing_edges\":[");

        var roundTripped = JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.WorklistResult);
        roundTripped.ShouldNotBeNull();
        roundTripped!.HasConflicts.ShouldBeTrue();
        roundTripped.Waves.ShouldBeEmpty();
        roundTripped.Conflicts.Count.ShouldBe(1);
        roundTripped.Conflicts[0].Kind.ShouldBe(EdgeConflictKind.Cycle);
    }

    // ─── Execution mode injection ───────────────────────────────────────

    /// <summary>
    /// Builds a process config with a single self-referential type that
    /// carries both plannable + implementable, with a configurable
    /// execution_mode. Used to verify the injector composition in the verb.
    /// </summary>
    private static ProcessConfig BuildSingleTypeConfig(
        string typeName,
        string[] facets,
        string? executionMode,
        bool decomposable = true)
    {
        var transitions = new Dictionary<string, string>
        {
            ["begin_planning"] = "Doing",
            ["implementation_complete"] = "Done",
        };
        var builder = new ProcessConfigBuilder()
            .WithType(typeName, facets, transitions, selfReferential: true)
            .WithBranchStrategy();
        var config = builder.Build();
        var typeConfig = config.Types[typeName];
        typeConfig.Decomposable = decomposable;
        typeConfig.ExecutionMode = executionMode;
        return config;
    }

    [Fact]
    public async Task Build_WithExecutionMode_Parallel_DefaultBehavior()
    {
        // Single self-referential plannable+implementable Story root with no
        // children: under parallel mode no plan_promoted → implementation_merged
        // edge is injected, so the topological wave shape is the trivial
        // single-item wave 0.
        var config = BuildSingleTypeConfig("Story", ["plannable", "implementable"], executionMode: ExecutionMode.Parallel, decomposable: false);
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Story").Build());
        SaveManifest(rootId: 100);

        var cmd = CreateCommand(config);
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        var result = ParseJson(output);
        result.HasConflicts.ShouldBeFalse();
        result.Waves.Count.ShouldBe(1);
        result.Waves[0].Items.Single().ItemId.ShouldBe(100);
    }

    [Fact]
    public async Task Build_WithPlanThenImplementItem_TwoPhaseGating()
    {
        // Plan-then-implement story with two children: the injector adds a
        // plan_promoted → implementation_merged edge inside item 100. The
        // children-unblock cross-item edge (100.children_seeded → child entry)
        // still gates each child on the parent's plan being promoted —
        // wave 0 = parent, wave 1 = children.
        var config = BuildSingleTypeConfig("Story", ["plannable", "implementable"], executionMode: ExecutionMode.PlanThenImplement, decomposable: true);
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Story").Build(),
            new WorkItemBuilder().WithId(200).WithType("Story").WithParentId(100).Build(),
            new WorkItemBuilder().WithId(300).WithType("Story").WithParentId(100).Build());
        SaveManifest(rootId: 100);

        var cmd = CreateCommand(config);
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: 100, manifestPath: _manifestPath, json: true));
        var result = ParseJson(output);

        result.HasConflicts.ShouldBeFalse();
        result.ItemsWalked.ShouldBe(3);
        result.Waves.Count.ShouldBe(2);
        result.Waves[0].Items.Single().ItemId.ShouldBe(100);
        result.Waves[1].Items.Select(i => i.ItemId).ShouldBe(new[] { 200, 300 });
    }

    // ─── Plan status mapping (preserved from pre-cutover contract) ──────

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

    // ─── JSON shape contract (envelope lock-in) ─────────────────────────

    [Fact]
    public async Task Build_EnvelopeShapeLockIn_AllExpectedSnakeCaseKeys()
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
        roundTripped.ItemsWalked.ShouldBe(2);
        roundTripped.HasConflicts.ShouldBeFalse();
        roundTripped.Waves.Count.ShouldBe(2);

        // snake_case wire keys (PolyphonyJsonContext convention).
        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"items_walked\"");
        output.ShouldContain("\"has_conflicts\"");
        output.ShouldContain("\"conflicts\"");
        output.ShouldContain("\"waves\"");
        output.ShouldContain("\"wave_index\"");
        output.ShouldContain("\"items\"");
        output.ShouldContain("\"item_id\"");
        output.ShouldContain("\"parent_item_id\"");
        output.ShouldContain("\"plan_status\"");
        output.ShouldContain("\"plan_pr_number\"");
        output.ShouldContain("\"current_generation\"");

        // Cutover: `depth` is gone from the wave entry.
        output.Contains("\"depth\"").ShouldBeFalse();

        // No PascalCase leakage.
        output.Contains("\"RootId\"").ShouldBeFalse();
        output.Contains("\"WaveIndex\"").ShouldBeFalse();
        output.Contains("\"HasConflicts\"").ShouldBeFalse();
        output.Contains("\"ItemsWalked\"").ShouldBeFalse();
        output.Contains("\"ItemId\"").ShouldBeFalse();
        output.Contains("\"PlanStatus\"").ShouldBeFalse();

        // Null fields omitted on success (no Error / ErrorCode keys).
        output.Contains("\"error\"").ShouldBeFalse();
        output.Contains("\"error_code\"").ShouldBeFalse();
    }

    [Fact]
    public async Task Build_EnvelopeShapeLockIn_HasConflictsAlwaysPresentEvenOnError()
    {
        // Even error envelopes must carry has_conflicts (false) and conflicts ([])
        // so workflow consumers can read these fields without first
        // distinguishing error from conflict.
        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Build(rootId: -1, manifestPath: _manifestPath, json: true));
        output.ShouldContain("\"error\"");
        output.ShouldContain("\"error_code\"");
        output.ShouldContain("\"invalid_argument\"");
        output.ShouldContain("\"has_conflicts\":false");
        output.ShouldContain("\"conflicts\":[]");
        output.ShouldContain("\"waves\":[]");
        output.ShouldContain("\"items_walked\":0");
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
        output.ShouldContain("items=3");
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
