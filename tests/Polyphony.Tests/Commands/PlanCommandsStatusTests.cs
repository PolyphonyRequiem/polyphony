using System.Text.Json;
using Polyphony;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony plan status</c>. Seeds a real
/// <c>.polyphony/run.yaml</c> in a temp directory, drives the verb via
/// <c>--manifest-path</c> (so the discovery path doesn't need to stub
/// git rev-parse), and asserts on the JSON the verb emits.
///
/// <para>The default human output is also covered to ensure all key
/// fields appear in the rendered string.</para>
/// </summary>
public sealed class PlanCommandsStatusTests : CommandTestBase, IDisposable
{
    private readonly string _tempDir;
    private readonly string _manifestPath;

    public PlanCommandsStatusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "polyphony-plan-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _manifestPath = Path.Combine(_tempDir, "run.yaml");
    }

    void IDisposable.Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        base.Dispose();
    }

    private PlanCommands CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        return new PlanCommands(walker, Repository, Config, twig, new GitClient(runner), new GhClient(runner));
    }

    private void SaveManifest(
        int rootId,
        string platformProject = "github.com/owner/repo",
        Dictionary<string, int>? planGenerations = null,
        List<MergedPlanPrEntry>? ledger = null)
    {
        var manifest = new RunManifest
        {
            Schema = 1,
            RootId = rootId,
            PlatformProject = platformProject,
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

    private static PlanStatusResult ParseJson(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanStatusResult)!;

    // ─── Input validation ───────────────────────────────────────────────

    [Fact]
    public async Task RootId_NonPositive_EmitsError()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Status(rootId: 0, manifestPath: _manifestPath, json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("--root-id must be positive");
        result.Items.ShouldBeEmpty();
    }

    // ─── Manifest discovery / load failures ─────────────────────────────

    [Fact]
    public async Task ManifestPath_DoesNotExist_EmitsError()
    {
        var cmd = CreateCommand();
        var missing = Path.Combine(_tempDir, "no-such-manifest.yaml");
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Status(rootId: 100, manifestPath: missing, json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("not found");
        result.ManifestPath.ShouldBe(missing);
        result.RootId.ShouldBe(100);
    }

    [Fact]
    public async Task Manifest_MalformedYaml_EmitsError()
    {
        File.WriteAllText(_manifestPath, "this: is: not: valid: yaml: [\n  - broken");
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Status(rootId: 100, manifestPath: _manifestPath, json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.Error.ShouldNotBeNull();
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Manifest_RootIdMismatch_EmitsError()
    {
        SaveManifest(rootId: 100);
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Status(rootId: 999, manifestPath: _manifestPath, json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("100");
        result.Error!.ShouldContain("999");
    }

    // ─── Happy paths ────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyLedger_EmitsZeroItems_NoError()
    {
        SaveManifest(rootId: 100, planGenerations: new() { ["root"] = 0 });
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Status(rootId: 100, manifestPath: _manifestPath, json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.Error.ShouldBeNull();
        result.RootId.ShouldBe(100);
        result.ManifestPath.ShouldBe(_manifestPath);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task SingleRootEntry_AggregatesAndEmitsRow()
    {
        var recorded = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        SaveManifest(
            rootId: 100,
            planGenerations: new() { ["root"] = 1 },
            ledger: new()
            {
                MakeEntry(prNumber: 42, itemKey: "root", previousGeneration: 0, currentGeneration: 1, recordedAt: recorded),
            });

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Status(rootId: 100, manifestPath: _manifestPath, json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.Error.ShouldBeNull();
        result.Items.Count.ShouldBe(1);
        var row = result.Items[0];
        row.ItemId.ShouldBe(100);
        row.CurrentGeneration.ShouldBe(1);
        row.MergedPrCount.ShouldBe(1);
        row.LatestPrUrl.ShouldBe("https://github.com/owner/repo/pull/42");
        row.LatestMergedAt.ShouldNotBeNull();
        row.LatestMergedAt!.Value.ToUniversalTime().ShouldBe(recorded);
    }

    [Fact]
    public async Task MultipleEntriesSameItem_PicksMostRecentByRecordedAt()
    {
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
            () => cmd.Status(rootId: 100, manifestPath: _manifestPath, json: true));
        var result = ParseJson(output);
        var row = result.Items.Single();
        row.ItemId.ShouldBe(100);
        row.MergedPrCount.ShouldBe(2);
        row.CurrentGeneration.ShouldBe(2);
        row.LatestPrUrl.ShouldBe("https://github.com/owner/repo/pull/51");
        row.LatestMergedAt!.Value.ToUniversalTime().ShouldBe(late);
    }

    [Fact]
    public async Task MultipleItems_SortedByItemIdAscending()
    {
        var t = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        SaveManifest(
            rootId: 100,
            planGenerations: new()
            {
                ["root"] = 1,
                ["250"] = 1,
                ["310"] = 1,
            },
            ledger: new()
            {
                MakeEntry(prNumber: 50, itemKey: "310", previousGeneration: 0, currentGeneration: 1, recordedAt: t),
                MakeEntry(prNumber: 42, itemKey: "root", previousGeneration: 0, currentGeneration: 1, recordedAt: t),
                MakeEntry(prNumber: 47, itemKey: "250", previousGeneration: 0, currentGeneration: 1, recordedAt: t),
            });

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(rootId: 100, manifestPath: _manifestPath, json: true));
        var result = ParseJson(output);
        result.Items.Select(i => i.ItemId).ShouldBe(new[] { 100, 250, 310 });
        result.Items[0].LatestPrUrl.ShouldBe("https://github.com/owner/repo/pull/42");
        result.Items[1].LatestPrUrl.ShouldBe("https://github.com/owner/repo/pull/47");
        result.Items[2].LatestPrUrl.ShouldBe("https://github.com/owner/repo/pull/50");
    }

    [Fact]
    public async Task GenerationsMapMissingKey_FallsBackToLedgerEntry()
    {
        // Manifest's plan_generations map has no entry for "250" but the ledger does.
        // The verb should still produce a row using the ledger's CurrentGeneration.
        SaveManifest(
            rootId: 100,
            planGenerations: new() { ["root"] = 0 },
            ledger: new()
            {
                MakeEntry(prNumber: 47, itemKey: "250", previousGeneration: 0, currentGeneration: 1,
                    recordedAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
            });

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(rootId: 100, manifestPath: _manifestPath, json: true));
        var result = ParseJson(output);
        var row = result.Items.Single();
        row.ItemId.ShouldBe(250);
        row.CurrentGeneration.ShouldBe(1);
    }

    [Fact]
    public async Task NonGitHubPlatformProject_LeavesLatestPrUrlNull()
    {
        SaveManifest(
            rootId: 100,
            platformProject: "dev.azure.com/myorg/MyProject",
            planGenerations: new() { ["root"] = 1 },
            ledger: new()
            {
                MakeEntry(prNumber: 42, itemKey: "root", previousGeneration: 0, currentGeneration: 1,
                    recordedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            });

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(rootId: 100, manifestPath: _manifestPath, json: true));
        var result = ParseJson(output);
        var row = result.Items.Single();
        row.LatestPrUrl.ShouldBeNull();
    }

    // ─── Output mode ────────────────────────────────────────────────────

    [Fact]
    public async Task JsonFlag_EmitsRoundTrippableJson()
    {
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
            () => cmd.Status(rootId: 100, manifestPath: _manifestPath, json: true));

        // Must be valid JSON parseable by the source-gen context.
        var roundTripped = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanStatusResult);
        roundTripped.ShouldNotBeNull();
        roundTripped!.RootId.ShouldBe(100);
        roundTripped.Items.Count.ShouldBe(1);

        // Must use snake_case keys (PolyphonyJsonContext convention).
        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"merged_pr_count\"");
        output.ShouldContain("\"latest_pr_url\"");
        output.ShouldContain("\"manifest_path\"");
    }

    [Fact]
    public async Task DefaultHumanOutput_ContainsKeyFields()
    {
        var recorded = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        SaveManifest(
            rootId: 100,
            planGenerations: new()
            {
                ["root"] = 2,
                ["250"] = 1,
            },
            ledger: new()
            {
                MakeEntry(prNumber: 42, itemKey: "root", previousGeneration: 0, currentGeneration: 1, recordedAt: recorded),
                MakeEntry(prNumber: 51, itemKey: "root", previousGeneration: 1, currentGeneration: 2, recordedAt: recorded),
                MakeEntry(prNumber: 47, itemKey: "250", previousGeneration: 0, currentGeneration: 1, recordedAt: recorded),
            });

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Status(rootId: 100, manifestPath: _manifestPath, json: false));
        exit.ShouldBe(ExitCodes.Success);

        // Header.
        output.ShouldContain("plan status:");
        output.ShouldContain("root=100");
        output.ShouldContain(_manifestPath);

        // Per-item rows.
        output.ShouldContain("item 100");
        output.ShouldContain("item 250");
        output.ShouldContain("generation=2");
        output.ShouldContain("generation=1");
        output.ShouldContain("merged_prs=2");
        output.ShouldContain("merged_prs=1");
        output.ShouldContain("https://github.com/owner/repo/pull/51");
        output.ShouldContain("https://github.com/owner/repo/pull/47");
    }

    [Fact]
    public async Task DefaultHumanOutput_EmptyLedger_RendersEmptyMarker()
    {
        SaveManifest(rootId: 100);
        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(rootId: 100, manifestPath: _manifestPath, json: false));
        output.ShouldContain("no merged plan PRs");
    }

    [Fact]
    public async Task DefaultHumanOutput_OnError_RendersErrorPrefix()
    {
        var missing = Path.Combine(_tempDir, "nope.yaml");
        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(rootId: 100, manifestPath: missing, json: false));
        output.ShouldContain("error:");
    }
}
