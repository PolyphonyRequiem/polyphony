using System.Text.Json;
using NSubstitute;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <see cref="StatusCommand"/> — the routing-style dashboard verb.
/// Routing-style means: ALWAYS exits 0; failure modes flow through per-section
/// <c>error</c> fields and the cross-signal <c>Warnings</c> array. Tests pin
/// that contract plus the five warning codes the command currently computes.
/// </summary>
[Collection("CwdSerial")]
public sealed class StatusCommandTests : CommandTestBase
{
    private readonly IGitClient _git = Substitute.For<IGitClient>();
    private readonly IGhClient _gh = Substitute.For<IGhClient>();

    public StatusCommandTests()
    {
        // Default: no remote URL → no slug → feature_pr section returns
        // {exists:false, error:"no_slug"}. Tests that exercise the gh leg
        // override this on the substitute directly.
        _git.GetRemoteUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
    }

    private StatusCommand CreateCommand() => new(
        Repository,
        new Polyphony.Sdlc.Observers.RepoIdentityResolver(_git),
        new Polyphony.Sdlc.Observers.PullRequestReader(_gh, null));

    [Fact]
    public async Task Status_MissingApex_ReturnsRoutingFailure_AndDoesNotEmitJson()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Status());

        exitCode.ShouldBe(ExitCodes.RoutingFailure);
        output.ShouldNotContain("\"apex_id\"");
    }

    [Fact]
    public async Task Status_WorkItemNotFound_ReturnsResultWithFoundFalse_AndExitsZero()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Status(apex: 999_999));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatusResult);
        result.ShouldNotBeNull();
        result.ApexId.ShouldBe(999_999);
        result.Ado.Found.ShouldBeFalse();
        result.Ado.Error.ShouldNotBeNull();
        result.Ado.Error!.ShouldContain("999999");
        result.Headline.ShouldContain("not found");
    }

    [Fact]
    public async Task Status_PlannedTagWithZeroChildren_EmitsFalseSatisfiedWarning()
    {
        // The AB#3064 false-satisfied bug: planned tag stamped on an apex
        // that has no children. Headline takes the warning's wording.
        var apex = new WorkItemBuilder()
            .WithId(3064)
            .WithType("Issue")
            .WithTitle("Test apex")
            .WithState("Doing")
            .WithTags("polyphony; polyphony:root; polyphony:planned")
            .Build();
        await SeedAsync(apex);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Status(apex: 3064));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatusResult);
        result.ShouldNotBeNull();
        result.Ado.HasPlannedTag.ShouldBeTrue();
        result.Ado.ChildrenCount.ShouldBe(0);
        result.Warnings.ShouldContain(w => w.Code == "planned_tag_zero_children");
        result.Headline.ShouldContain("planned but no children");
    }

    [Fact]
    public async Task Status_ApexNotInScope_EmitsNotInScopeWarning()
    {
        // An ADO work item that exists but doesn't carry the polyphony tag.
        // The dashboard catches this — it usually means the operator pointed
        // status at the wrong work item.
        var apex = new WorkItemBuilder()
            .WithId(7)
            .WithType("Issue")
            .WithTitle("Wrong target")
            .WithState("To Do")
            .Build();
        await SeedAsync(apex);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Status(apex: 7));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatusResult);
        result.ShouldNotBeNull();
        result.Ado.InScope.ShouldBeFalse();
        result.Warnings.ShouldContain(w => w.Code == "apex_not_in_scope");
        result.Headline.ShouldContain("not in polyphony scope");
        result.NextAction.ShouldNotBeNull();
        result.NextAction!.ShouldContain("polyphony root declare");
    }

    [Fact]
    public async Task Status_InScopeButNotRoot_EmitsNotRootWarning()
    {
        var apex = new WorkItemBuilder()
            .WithId(42)
            .WithType("Task")
            .WithTitle("descendant")
            .WithState("Doing")
            .WithTags("polyphony")
            .Build();
        await SeedAsync(apex);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Status(apex: 42));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatusResult);
        result.ShouldNotBeNull();
        result.Ado.InScope.ShouldBeTrue();
        result.Ado.IsRoot.ShouldBeFalse();
        result.Warnings.ShouldContain(w => w.Code == "apex_not_root");
    }

    [Fact]
    public async Task Status_ManifestMissing_EmitsManifestMissingWarning_AndStillExitsZero()
    {
        var apex = new WorkItemBuilder()
            .WithId(100)
            .WithType("Issue")
            .WithTitle("Healthy apex")
            .WithState("Doing")
            .WithTags("polyphony; polyphony:root")
            .Build();
        await SeedAsync(apex);

        using var tempDir = new TempDirectory();
        var missingManifest = Path.Combine(tempDir.Path, ".polyphony", "run.yaml");

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(
            () => cmd.Status(apex: 100, manifestPath: missingManifest));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatusResult);
        result.ShouldNotBeNull();
        result.Manifest.Exists.ShouldBeFalse();
        result.Warnings.ShouldContain(w => w.Code == "manifest_missing");
    }

    [Fact]
    public async Task Status_ManifestPresent_RootGenerationAndCountsSurfaced()
    {
        var apex = new WorkItemBuilder()
            .WithId(200)
            .WithType("Issue")
            .WithTitle("Run-in-flight")
            .WithState("Doing")
            .WithTags("polyphony; polyphony:root")
            .Build();
        await SeedAsync(apex);

        // Manifest exists, plan PRs merged, but no feature PR returned by gh.
        // That's the unmerged-progress signal — pin both the surfaced fields
        // and the warning.
        _git.GetRemoteUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("https://github.com/owner/repo"));
        _gh.ListPullRequestsAsync(
                Arg.Any<string>(), Arg.Any<PrListFilters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PullRequestSummary>>([]));

        using var tempDir = new TempDirectory();
        var manifestPath = Path.Combine(tempDir.Path, ".polyphony", "run.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, """
            schema: 1
            root_id: 200
            platform_project: dev.azure.com/test/Test
            created_at: 2026-05-09T00:00:00Z
            created_by: test
            branch_model_version: 1
            plan_generations:
              root: 3
            merged_plan_prs:
              - pr_number: 1
                item_key: root
                merge_commit: abc123
                previous_generation: 0
                current_generation: 1
                recorded_at: 2026-05-09T01:00:00Z
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(
            () => cmd.Status(apex: 200, manifestPath: manifestPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatusResult);
        result.ShouldNotBeNull();
        if (result.Manifest.Error is not null)
        {
            // Surface the parser's own message so a schema drift surfaces
            // as the test's failure message rather than a downstream null.
            throw new Xunit.Sdk.XunitException(
                $"Manifest failed to parse: {result.Manifest.Error}");
        }
        result.Manifest.Exists.ShouldBeTrue();
        result.Manifest.FeatureBranch.ShouldBe("feature/200");
        result.Manifest.PlanGenerationsRoot.ShouldBe(3);
        result.Manifest.MergedPlanPrsCount.ShouldBe(1);
        result.Warnings.ShouldContain(w => w.Code == "feature_pr_unmerged_progress");
    }

    [Fact]
    public async Task Status_FeaturePrMerged_NoUnmergedProgressWarning_AndHeadlineReportsMerged()
    {
        var apex = new WorkItemBuilder()
            .WithId(300)
            .WithType("Issue")
            .WithTitle("Shipped")
            .WithState("Done")
            .WithTags("polyphony; polyphony:root")
            .Build();
        await SeedAsync(apex);

        using var tempDir = new TempDirectory();
        var manifestPath = Path.Combine(tempDir.Path, ".polyphony", "run.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, """
            schema: 1
            root_id: 300
            platform_project: dev.azure.com/test/Test
            created_at: 2026-05-09T00:00:00Z
            created_by: test
            branch_model_version: 1
            merged_plan_prs:
              - pr_number: 1
                item_key: root
                merge_commit: abc123
                previous_generation: 0
                current_generation: 1
                recorded_at: 2026-05-09T01:00:00Z
            """);

        // gh reports a MERGED feature PR — exists+MergedAt populated.
        _git.GetRemoteUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("git@github.com:owner/repo.git"));
        _gh.ListPullRequestsAsync(
                "owner/repo", Arg.Any<PrListFilters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PullRequestSummary>>([
                new PullRequestSummary(
                    Number: 42,
                    HeadRefName: "feature/300",
                    Url: "https://github.com/owner/repo/pull/42",
                    MergedAt: DateTimeOffset.Parse("2026-05-09T02:00:00Z"))
            ]));

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(
            () => cmd.Status(apex: 300, manifestPath: manifestPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatusResult);
        result.ShouldNotBeNull();
        result.FeaturePr.Exists.ShouldBeTrue();
        result.FeaturePr.Number.ShouldBe(42);
        result.FeaturePr.State.ShouldBe("MERGED");
        result.Warnings.ShouldNotContain(w => w.Code == "feature_pr_unmerged_progress");
        result.Headline.ShouldContain("merged");
    }

    [Fact]
    public async Task Status_GhFails_FeaturePrSectionCarriesError_AndExitZero()
    {
        var apex = new WorkItemBuilder()
            .WithId(400)
            .WithType("Issue")
            .WithTitle("gh wedged")
            .WithState("Doing")
            .WithTags("polyphony; polyphony:root")
            .Build();
        await SeedAsync(apex);

        _git.GetRemoteUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("https://github.com/owner/repo"));
        _gh.ListPullRequestsAsync(
                Arg.Any<string>(), Arg.Any<PrListFilters>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<PullRequestSummary>>>(_ =>
                throw new InvalidOperationException("gh hung — buffered stderr: ..."));

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Status(apex: 400));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatusResult);
        result.ShouldNotBeNull();
        result.FeaturePr.Exists.ShouldBeFalse();
        result.FeaturePr.Error.ShouldNotBeNull();
        result.FeaturePr.Error!.ShouldContain("gh hung");
    }

    [Fact]
    public async Task Status_BinarySection_AlwaysPopulated()
    {
        var apex = new WorkItemBuilder().WithId(500).Build();
        await SeedAsync(apex);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Status(apex: 500));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StatusResult);
        result.ShouldNotBeNull();
        result.Binary.Version.ShouldNotBeNullOrEmpty();
        result.Binary.InformationalVersion.ShouldNotBeNullOrEmpty();
    }

    /// <summary>Self-contained scratch directory that deletes on dispose.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "polyphony-status-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
