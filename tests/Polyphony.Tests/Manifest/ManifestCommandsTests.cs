using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Manifest;
using Polyphony.Tests.Commands;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Manifest;

/// <summary>
/// End-to-end tests for the <c>polyphony manifest</c> verbs. The verbs
/// take no constructor dependencies and operate on a path passed via
/// <c>--path</c>, so each test uses a temp file to stay hermetic.
/// </summary>
public sealed class ManifestCommandsTests : IDisposable
{
    private readonly string tempDir;
    private readonly string manifestPath;

    public ManifestCommandsTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), "polyphony-manifest-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.tempDir);
        this.manifestPath = Path.Combine(this.tempDir, "run.yaml");
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    // The pre-existing manifest verbs (init/read/topology-hash/record-*) do
    // not invoke the injected IGitClient, so a stub backed by an empty
    // FakeProcessRunner is safe — any unstubbed git call would throw
    // immediately and surface as a test failure rather than a false pass.
    private static ManifestCommands NewCommand() =>
        new(new Polyphony.Infrastructure.Processes.GitClient(
            new Polyphony.Tests.Infrastructure.Processes.FakeProcessRunner()));

    /// <summary>
    /// Runs an async command while capturing stdout. Mirrors
    /// <c>CommandTestBase.CaptureConsoleAsync</c>; we can't inherit
    /// because we don't need the SQLite/DI scaffolding.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> CaptureAsync(Func<Task<int>> action)
    {
        await ConsoleTestLock.AsyncLock.WaitAsync();
        try
        {
            using var writer = new StringWriter();
            var original = Console.Out;
            Console.SetOut(writer);
            try
            {
                var exit = await action();
                return (exit, writer.ToString().Trim());
            }
            finally
            {
                Console.SetOut(original);
            }
        }
        finally
        {
            ConsoleTestLock.AsyncLock.Release();
        }
    }

    [Fact]
    public async Task Init_FreshPath_CreatesManifestAndEmitsResult()
    {
        var cmd = NewCommand();
        var (exit, output) = await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "dev.azure.com/org/project", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        File.Exists(this.manifestPath).ShouldBeTrue();

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestInitResult)!;
        result.RootId.ShouldBe(1234);
        result.PlatformProject.ShouldBe("dev.azure.com/org/project");
        result.Created.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.TopologyHash.ShouldStartWith("sha256:");
    }

    [Fact]
    public async Task Init_NonPositiveRootId_ReturnsConfigError()
    {
        var cmd = NewCommand();
        var (exit, output) = await CaptureAsync(() =>
            cmd.Init(rootId: 0, platformProject: "dev.azure.com/org/project", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.ConfigError);
        File.Exists(this.manifestPath).ShouldBeFalse();

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestInitResult)!;
        result.Error!.ShouldContain("rootId");
    }

    [Fact]
    public async Task Init_EmptyPlatformProject_ReturnsConfigError()
    {
        var cmd = NewCommand();
        var (exit, output) = await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "  ", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestInitResult)!;
        result.Error!.ShouldContain("platform-project");
    }

    [Fact]
    public async Task Init_AlreadyExists_WithoutForce_ReturnsConfigError()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.Init(rootId: 5678, platformProject: "x/y", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestInitResult)!;
        result.Error!.ShouldContain("already exists");
    }

    [Fact]
    public async Task Init_AlreadyExists_WithForce_OverwritesAndReportsCreatedFalse()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.Init(rootId: 5678, platformProject: "x/y", path: this.manifestPath, force: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestInitResult)!;
        result.RootId.ShouldBe(5678);
        result.Created.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
    }

    [Fact]
    public async Task Read_HappyPath_RoundTripsAndReportsHashMatch()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() => cmd.Read(path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadResult)!;
        result.Manifest.RootId.ShouldBe(1234);
        result.TopologyHashMatches.ShouldBeTrue();
        result.ComputedTopologyHash.ShouldBe(result.Manifest.TopologyHash);
    }

    [Fact]
    public async Task Read_MissingFile_ReturnsCacheError()
    {
        var cmd = NewCommand();
        var (exit, output) = await CaptureAsync(() =>
            cmd.Read(path: Path.Combine(this.tempDir, "missing.yaml")));

        exit.ShouldBe(ExitCodes.CacheError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadResult)!;
        result.Error!.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Read_MalformedYaml_ReturnsConfigError()
    {
        File.WriteAllText(this.manifestPath, "schema: 1\nroot_id: not-a-number\n");

        var cmd = NewCommand();
        var (exit, output) = await CaptureAsync(() => cmd.Read(path: this.manifestPath));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadResult)!;
        result.Error!.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task TopologyHash_FreshManifest_MatchesStored()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() => cmd.TopologyHash(path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestTopologyHashResult)!;
        result.Matches.ShouldBeTrue();
        result.TopologyHash.ShouldBe(result.StoredTopologyHash);
    }

    [Fact]
    public async Task TopologyHash_ManuallyMutatedManifest_ReportsMismatch()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        // Replace the stored topology_hash line with a known-bad value.
        var lines = File.ReadAllLines(this.manifestPath)
            .Select(line => line.StartsWith("topology_hash:", StringComparison.Ordinal)
                ? "topology_hash: sha256:0000000000000000000000000000000000000000000000000000000000000000"
                : line);
        File.WriteAllLines(this.manifestPath, lines);

        var (exit, output) = await CaptureAsync(() => cmd.TopologyHash(path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestTopologyHashResult)!;
        result.Matches.ShouldBeFalse();
        result.TopologyHash.ShouldNotBe(result.StoredTopologyHash);
    }

    [Fact]
    public async Task RecordRebase_HappyPath_AppendsAndReportsCount()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() => cmd.RecordRebase(
            branch: "mg/1234_data-layer",
            onto: "feature/1234",
            reason: "cross_mg_code_dep",
            commit: "0b1f3e9",
            path: this.manifestPath,
            at: "2026-05-06T18:00:00Z"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestRebaseRecordResult)!;
        result.RebaseCount.ShouldBe(1);
        result.Branch.ShouldBe("mg/1234_data-layer");

        // Persistence check: the next call should report count 2.
        var (_, output2) = await CaptureAsync(() => cmd.RecordRebase(
            branch: "mg/1234_other",
            onto: "feature/1234",
            reason: "manual",
            commit: "abc1234",
            path: this.manifestPath));
        var result2 = JsonSerializer.Deserialize(output2, PolyphonyJsonContext.Default.ManifestRebaseRecordResult)!;
        result2.RebaseCount.ShouldBe(2);
    }

    [Fact]
    public async Task RecordRebase_MissingField_ReturnsConfigError()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() => cmd.RecordRebase(
            branch: "",
            onto: "feature/1234",
            reason: "manual",
            commit: "abc",
            path: this.manifestPath));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("manifest record-rebase");
        envelope.MissingArgs.ShouldContain("--branch");
    }

    [Fact]
    public async Task RecordRebase_BadTimestamp_ReturnsConfigError()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() => cmd.RecordRebase(
            branch: "mg/1234_data",
            onto: "feature/1234",
            reason: "manual",
            commit: "abc",
            path: this.manifestPath,
            at: "not-a-timestamp"));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestRebaseRecordResult)!;
        result.Error!.ShouldContain("ISO-8601");
    }

    [Fact]
    public async Task RecordApproval_HappyPath_AppendsAndReportsCount()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() => cmd.RecordApproval(
            gate: "deep_nesting_depth_4",
            approvedBy: "dangreen",
            path: this.manifestPath,
            detail: "approved at depth 4",
            at: "2026-05-06T17:00:00Z"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestApprovalRecordResult)!;
        result.ApprovalCount.ShouldBe(1);
        result.Gate.ShouldBe("deep_nesting_depth_4");
        result.Detail.ShouldBe("approved at depth 4");
    }

    [Fact]
    public async Task RecordApproval_MissingField_ReturnsConfigError()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() => cmd.RecordApproval(
            gate: "",
            approvedBy: "dangreen",
            path: this.manifestPath));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("manifest record-approval");
        envelope.MissingArgs.ShouldContain("--gate");
    }

    // -- record-plan-merge --

    [Fact]
    public async Task RecordPlanMerge_RootKey_FromZeroBumpsToOne()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.RecordPlanMerge(item: "root", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestRecordPlanMergeResult)!;
        result.ItemKey.ShouldBe("root");
        result.PreviousGeneration.ShouldBe(0);
        result.CurrentGeneration.ShouldBe(1);

        var manifest = RunManifestStore.LoadOrThrow(this.manifestPath);
        manifest.PlanGenerations["root"].ShouldBe(1);
    }

    [Fact]
    public async Task RecordPlanMerge_DescendantKey_AccumulatesAcrossInvocations()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        await CaptureAsync(() => cmd.RecordPlanMerge(item: "5678", path: this.manifestPath));
        await CaptureAsync(() => cmd.RecordPlanMerge(item: "5678", path: this.manifestPath));
        var (exit, output) = await CaptureAsync(() =>
            cmd.RecordPlanMerge(item: "5678", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestRecordPlanMergeResult)!;
        result.PreviousGeneration.ShouldBe(2);
        result.CurrentGeneration.ShouldBe(3);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    [InlineData("ROOT")]   // case-sensitive: only literal lowercase "root"
    [InlineData("Root")]
    public async Task RecordPlanMerge_InvalidItem_ReturnsConfigError(string item)
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.RecordPlanMerge(item: item, path: this.manifestPath));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestRecordPlanMergeResult)!;
        result.Error!.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task RecordPlanMerge_EmptyItem_RoutesRequiredInputHalt()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.RecordPlanMerge(item: "", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("manifest record-plan-merge");
        envelope.MissingArgs.ShouldContain("--item");
    }

    [Fact]
    public async Task RecordPlanMerge_MissingFile_ReturnsCacheError()
    {
        var cmd = NewCommand();
        var (exit, _) = await CaptureAsync(() =>
            cmd.RecordPlanMerge(item: "root", path: Path.Combine(this.tempDir, "absent.yaml")));

        exit.ShouldBe(ExitCodes.CacheError);
    }

    // -- record-plan-merge: idempotency ledger --

    [Fact]
    public async Task RecordPlanMerge_LegacyModeWithoutPrIdentity_BumpsAndWritesNoLedgerEntry()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.RecordPlanMerge(item: "root", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestRecordPlanMergeResult)!;
        result.Recorded.ShouldBeTrue();
        result.PrNumber.ShouldBe(0);
        result.MergeCommit.ShouldBe(string.Empty);

        var manifest = RunManifestStore.LoadOrThrow(this.manifestPath);
        manifest.MergedPlanPrs.ShouldBeEmpty();
    }

    [Fact]
    public async Task RecordPlanMerge_FirstCallWithPrIdentity_BumpsAndAppendsLedgerEntry()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.RecordPlanMerge(
                item: "5678",
                path: this.manifestPath,
                prNumber: 42,
                mergeCommit: "abc1234"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestRecordPlanMergeResult)!;
        result.ItemKey.ShouldBe("5678");
        result.PreviousGeneration.ShouldBe(0);
        result.CurrentGeneration.ShouldBe(1);
        result.Recorded.ShouldBeTrue();
        result.PrNumber.ShouldBe(42);
        result.MergeCommit.ShouldBe("abc1234");

        var manifest = RunManifestStore.LoadOrThrow(this.manifestPath);
        manifest.PlanGenerations["5678"].ShouldBe(1);
        manifest.MergedPlanPrs.Count.ShouldBe(1);

        var entry = manifest.MergedPlanPrs[0];
        entry.PrNumber.ShouldBe(42);
        entry.ItemKey.ShouldBe("5678");
        entry.MergeCommit.ShouldBe("abc1234");
        entry.PreviousGeneration.ShouldBe(0);
        entry.CurrentGeneration.ShouldBe(1);
        entry.RecordedAt.ShouldNotBe(default);
    }

    [Fact]
    public async Task RecordPlanMerge_RepeatedCallWithSamePrIdentity_IdempotentSkip()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));
        await CaptureAsync(() => cmd.RecordPlanMerge(
            item: "root", path: this.manifestPath, prNumber: 42, mergeCommit: "abc1234"));

        var (exit, output) = await CaptureAsync(() =>
            cmd.RecordPlanMerge(
                item: "root",
                path: this.manifestPath,
                prNumber: 42,
                mergeCommit: "abc1234"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestRecordPlanMergeResult)!;
        result.Recorded.ShouldBeFalse();
        result.PreviousGeneration.ShouldBe(0);
        result.CurrentGeneration.ShouldBe(1);

        // No double-bump: generation is still 1, ledger still has only one entry.
        var manifest = RunManifestStore.LoadOrThrow(this.manifestPath);
        manifest.PlanGenerations["root"].ShouldBe(1);
        manifest.MergedPlanPrs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RecordPlanMerge_SamePrNumberDifferentItemKey_ReturnsConfigError()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));
        await CaptureAsync(() => cmd.RecordPlanMerge(
            item: "root", path: this.manifestPath, prNumber: 42, mergeCommit: "abc1234"));

        var (exit, output) = await CaptureAsync(() =>
            cmd.RecordPlanMerge(
                item: "5678",
                path: this.manifestPath,
                prNumber: 42,
                mergeCommit: "abc1234"));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestRecordPlanMergeResult)!;
        result.Error!.ShouldContain("PR #42");
        result.Error!.ShouldContain("root");
        result.Error!.ShouldContain("5678");
    }

    [Fact]
    public async Task RecordPlanMerge_SamePrNumberDifferentMergeCommit_ReturnsConfigError()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));
        await CaptureAsync(() => cmd.RecordPlanMerge(
            item: "root", path: this.manifestPath, prNumber: 42, mergeCommit: "abc1234"));

        var (exit, output) = await CaptureAsync(() =>
            cmd.RecordPlanMerge(
                item: "root",
                path: this.manifestPath,
                prNumber: 42,
                mergeCommit: "deadbeef"));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestRecordPlanMergeResult)!;
        result.Error!.ShouldContain("PR #42");
        result.Error!.ShouldContain("abc1234");
        result.Error!.ShouldContain("deadbeef");
    }

    [Fact]
    public async Task RecordPlanMerge_PrNumberWithoutMergeCommit_ReturnsConfigError()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.RecordPlanMerge(item: "root", path: this.manifestPath, prNumber: 42, mergeCommit: ""));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestRecordPlanMergeResult)!;
        result.Error!.ShouldContain("--merge-commit");
    }

    [Fact]
    public async Task RecordPlanMerge_MergeCommitWithoutPrNumber_ReturnsConfigError()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.RecordPlanMerge(item: "root", path: this.manifestPath, prNumber: 0, mergeCommit: "abc1234"));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestRecordPlanMergeResult)!;
        result.Error!.ShouldContain("--pr-number");
    }

    [Fact]
    public async Task RecordPlanMerge_TwoDifferentPrsForSameItem_AccumulatesBoth()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));
        await CaptureAsync(() => cmd.RecordPlanMerge(
            item: "5678", path: this.manifestPath, prNumber: 42, mergeCommit: "abc1234"));
        await CaptureAsync(() => cmd.RecordPlanMerge(
            item: "5678", path: this.manifestPath, prNumber: 43, mergeCommit: "def5678"));

        var manifest = RunManifestStore.LoadOrThrow(this.manifestPath);
        manifest.PlanGenerations["5678"].ShouldBe(2);
        manifest.MergedPlanPrs.Count.ShouldBe(2);

        manifest.MergedPlanPrs[0].PrNumber.ShouldBe(42);
        manifest.MergedPlanPrs[0].PreviousGeneration.ShouldBe(0);
        manifest.MergedPlanPrs[0].CurrentGeneration.ShouldBe(1);

        manifest.MergedPlanPrs[1].PrNumber.ShouldBe(43);
        manifest.MergedPlanPrs[1].PreviousGeneration.ShouldBe(1);
        manifest.MergedPlanPrs[1].CurrentGeneration.ShouldBe(2);
    }

    [Fact]
    public async Task RecordPlanMerge_LegacyAndPrIdentityCallsCoexist()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        // Legacy bump (no PR identity, no ledger entry).
        await CaptureAsync(() => cmd.RecordPlanMerge(item: "root", path: this.manifestPath));

        // PR-identity bump on the same item (separate transaction).
        await CaptureAsync(() => cmd.RecordPlanMerge(
            item: "root", path: this.manifestPath, prNumber: 99, mergeCommit: "feedface"));

        var manifest = RunManifestStore.LoadOrThrow(this.manifestPath);
        manifest.PlanGenerations["root"].ShouldBe(2);
        manifest.MergedPlanPrs.Count.ShouldBe(1);
        manifest.MergedPlanPrs[0].PrNumber.ShouldBe(99);
        manifest.MergedPlanPrs[0].PreviousGeneration.ShouldBe(1);
        manifest.MergedPlanPrs[0].CurrentGeneration.ShouldBe(2);
    }

    // -- read-plan-generation --

    [Fact]
    public async Task ReadPlanGeneration_MissingKey_ReturnsZeroAndPresentFalse()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.ReadPlanGeneration(item: "root", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadPlanGenerationResult)!;
        result.Generation.ShouldBe(0);
        result.Present.ShouldBeFalse();
    }

    [Fact]
    public async Task ReadPlanGeneration_AfterRecord_ReflectsCurrentValue()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));
        await CaptureAsync(() => cmd.RecordPlanMerge(item: "5678", path: this.manifestPath));
        await CaptureAsync(() => cmd.RecordPlanMerge(item: "5678", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.ReadPlanGeneration(item: "5678", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadPlanGenerationResult)!;
        result.Generation.ShouldBe(2);
        result.Present.ShouldBeTrue();
    }

    // -- read-plan-generation-snapshot --

    [Fact]
    public async Task ReadPlanGenerationSnapshot_RootPlan_EmptyChain_ReturnsEmptySnapshot()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.ReadPlanGenerationSnapshot(item: "root", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadPlanGenerationSnapshotResult)!;
        result.ItemKey.ShouldBe("root");
        result.ParentItemKey.ShouldBeNull();
        result.ParentPlanGeneration.ShouldBe(0);
        result.AncestorPlanGenerations.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ReadPlanGenerationSnapshot_ChildOfRoot_ProjectsRootGeneration()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));
        await CaptureAsync(() => cmd.RecordPlanMerge(item: "root", path: this.manifestPath));
        await CaptureAsync(() => cmd.RecordPlanMerge(item: "root", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.ReadPlanGenerationSnapshot(item: "5678", ancestorIds: "root", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadPlanGenerationSnapshotResult)!;
        result.ItemKey.ShouldBe("5678");
        result.ParentItemKey.ShouldBe("root");
        result.ParentPlanGeneration.ShouldBe(2);
        result.AncestorPlanGenerations.Count.ShouldBe(1);
        result.AncestorPlanGenerations["root"].ShouldBe(2);
    }

    [Fact]
    public async Task ReadPlanGenerationSnapshot_Grandchild_ChainOrderingPreserved()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));
        await CaptureAsync(() => cmd.RecordPlanMerge(item: "root", path: this.manifestPath));
        await CaptureAsync(() => cmd.RecordPlanMerge(item: "5678", path: this.manifestPath));
        await CaptureAsync(() => cmd.RecordPlanMerge(item: "5678", path: this.manifestPath));
        await CaptureAsync(() => cmd.RecordPlanMerge(item: "5678", path: this.manifestPath));

        // Grandchild item 9999, immediate parent = 5678, grandparent = root.
        var (exit, output) = await CaptureAsync(() =>
            cmd.ReadPlanGenerationSnapshot(item: "9999", ancestorIds: "5678,root", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadPlanGenerationSnapshotResult)!;
        result.ParentItemKey.ShouldBe("5678");
        result.ParentPlanGeneration.ShouldBe(3);
        result.AncestorPlanGenerations["5678"].ShouldBe(3);
        result.AncestorPlanGenerations["root"].ShouldBe(1);
    }

    [Fact]
    public async Task ReadPlanGenerationSnapshot_AncestorWithoutRecordedGeneration_DefaultsToZero()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.ReadPlanGenerationSnapshot(item: "5678", ancestorIds: "root", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadPlanGenerationSnapshotResult)!;
        result.ParentItemKey.ShouldBe("root");
        result.ParentPlanGeneration.ShouldBe(0);
        result.AncestorPlanGenerations["root"].ShouldBe(0);
    }

    [Fact]
    public async Task ReadPlanGenerationSnapshot_RootPlanWithAncestors_ReturnsConfigError()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.ReadPlanGenerationSnapshot(item: "root", ancestorIds: "1234", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadPlanGenerationSnapshotResult)!;
        result.Error!.ShouldContain("root plan must not declare ancestors");
    }

    [Fact]
    public async Task ReadPlanGenerationSnapshot_SelfInAncestorChain_ReturnsConfigError()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.ReadPlanGenerationSnapshot(item: "5678", ancestorIds: "root,5678", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadPlanGenerationSnapshotResult)!;
        result.Error!.ShouldContain("must not appear in --ancestor-ids");
    }

    [Fact]
    public async Task ReadPlanGenerationSnapshot_DuplicateAncestor_ReturnsConfigError()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.ReadPlanGenerationSnapshot(item: "9999", ancestorIds: "5678,5678", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadPlanGenerationSnapshotResult)!;
        result.Error!.ShouldContain("duplicate");
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-7")]
    public async Task ReadPlanGenerationSnapshot_InvalidAncestorEntry_ReturnsConfigError(string ancestor)
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.ReadPlanGenerationSnapshot(item: "5678", ancestorIds: ancestor, path: this.manifestPath));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadPlanGenerationSnapshotResult)!;
        result.Error!.ShouldContain("--ancestor-ids");
    }

    [Fact]
    public async Task ReadPlanGenerationSnapshot_AncestorsWithSpaces_AreTrimmed()
    {
        var cmd = NewCommand();
        await CaptureAsync(() => cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));
        await CaptureAsync(() => cmd.RecordPlanMerge(item: "root", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.ReadPlanGenerationSnapshot(item: "5678", ancestorIds: " root , 1234 ", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadPlanGenerationSnapshotResult)!;
        result.ParentItemKey.ShouldBe("root");
        result.AncestorPlanGenerations.Count.ShouldBe(2);
        result.AncestorPlanGenerations["root"].ShouldBe(1);
        result.AncestorPlanGenerations["1234"].ShouldBe(0);
    }
}
