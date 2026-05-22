using System.Text.Json;
using System.Text.Json.Nodes;
using Polyphony.Commands;
using Polyphony.Journal;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class JournalQueryVerbTests : CommandTestBase
{
    private readonly string _tempDir;
    private readonly JournalStore _store;

    public JournalQueryVerbTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-journal-query-verbs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new JournalStore(Path.Combine(_tempDir, ".polyphony-state", "journal.db"));
    }

    [Fact]
    public async Task Has_RootNotFound_ReturnsCacheErrorWithCanonicalErrorJson()
    {
        var command = CreateHasCommand();

        var (exitCode, output) = await CaptureConsoleAsync(() => command.Has(99_991, "branch_create"));

        exitCode.ShouldBe(ExitCodes.CacheError);
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
        doc.RootElement.GetProperty("work_item_id").GetInt32().ShouldBe(99_991);
    }

    [Fact]
    public async Task Has_PresentAction_ReturnsFilteredMatches()
    {
        await SeedRootAsync(3254);
        var command = CreateHasCommand();

        await SeedEntryAsync(
            runId: "run-keep",
            rootId: 3254,
            workItemId: 3254,
            action: "branch_create",
            target: "entry-target-1",
            startedAt: 1_700_000_000_000,
            effects:
            [
                new JournalResourceEffect
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/3254",
                    Intent = ResourceIntent.EnsurePresent,
                    Mutation = ResourceMutation.CreatedNow,
                    PolyphonyOwned = true,
                },
            ]);
        await SeedEntryAsync(
            runId: "run-drop",
            rootId: 3254,
            workItemId: 3254,
            action: "branch_create",
            target: "entry-target-2",
            startedAt: 1_700_000_000_100,
            effects:
            [
                new JournalResourceEffect
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/other",
                    Intent = ResourceIntent.EnsurePresent,
                    Mutation = ResourceMutation.CreatedNow,
                    PolyphonyOwned = true,
                },
            ]);

        var (exitCode, output) = await CaptureConsoleAsync(() => command.Has(3254, "branch_create", target: "feature/3254", run: "run-keep"));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.JournalHasResult);

        exitCode.ShouldBe(ExitCodes.Success);
        result.ShouldNotBeNull();
        result.Present.ShouldBeTrue();
        result.Matches.ShouldHaveSingleItem();
        result.Matches[0].Action.ShouldBe("branch_create");
        result.Matches[0].Target.ShouldBe("entry-target-1");
        result.Matches[0].RunId.ShouldBe("run-keep");
    }

    [Fact]
    public async Task Has_TargetFilter_UsesEffectIdsRatherThanEntryTarget()
    {
        await SeedRootAsync(3254);
        await SeedEntryAsync(
            runId: "run-target",
            rootId: 3254,
            workItemId: 3254,
            action: "branch_create",
            target: "opaque-target",
            startedAt: 1_700_000_000_200,
            effects:
            [
                new JournalResourceEffect
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/3254-special",
                    Intent = ResourceIntent.EnsurePresent,
                    Mutation = ResourceMutation.CreatedNow,
                    PolyphonyOwned = true,
                },
            ]);

        var (exitCode, output) = await CaptureConsoleAsync(() => CreateHasCommand().Has(3254, "branch_create", target: "feature/3254-special"));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.JournalHasResult);

        exitCode.ShouldBe(ExitCodes.Success);
        result.ShouldNotBeNull();
        result.Present.ShouldBeTrue();
        result.Matches.ShouldHaveSingleItem();
        result.Matches[0].Target.ShouldBe("opaque-target");
    }

    [Fact]
    public async Task Has_AbsentAction_ReturnsFalseJsonWithCacheErrorExitCode()
    {
        await SeedRootAsync(3254);
        await SeedEntryAsync(
            runId: "run-other",
            rootId: 3254,
            workItemId: 3254,
            action: "branch_other",
            target: "feature/3254",
            startedAt: 1_700_000_000_300);

        var (exitCode, output) = await CaptureConsoleAsync(() => CreateHasCommand().Has(3254, "branch_create"));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.JournalHasResult);

        exitCode.ShouldBe(ExitCodes.CacheError);
        result.ShouldNotBeNull();
        result.Present.ShouldBeFalse();
        result.Matches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Query_RootNotFound_ReturnsCacheErrorWithCanonicalErrorJson()
    {
        var (exitCode, output) = await CaptureConsoleAsync(() => CreateQueryCommand().Query(99_992));

        exitCode.ShouldBe(ExitCodes.CacheError);
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
        doc.RootElement.GetProperty("work_item_id").GetInt32().ShouldBe(99_992);
    }

    [Fact]
    public async Task Query_HappyPath_ReturnsProjectedLatestEffects()
    {
        await SeedRootAsync(3254);
        await SeedEntryAsync(
            runId: "run-1",
            rootId: 3254,
            workItemId: 3254,
            action: "branch_create",
            target: "feature/3254",
            startedAt: 1_700_000_000_000,
            effects:
            [
                new JournalResourceEffect
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/3254",
                    Intent = ResourceIntent.EnsurePresent,
                    Mutation = ResourceMutation.CreatedNow,
                    PolyphonyOwned = true,
                    Attributes = new JsonObject { ["new_sha"] = "sha-one" },
                },
            ]);
        await SeedEntryAsync(
            runId: "run-2",
            rootId: 3254,
            workItemId: 3254,
            action: "branch_update",
            target: "feature/3254",
            startedAt: 1_700_000_000_100,
            effects:
            [
                new JournalResourceEffect
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/3254",
                    Intent = ResourceIntent.AdvancePointer,
                    Mutation = ResourceMutation.Changed,
                    PolyphonyOwned = true,
                    Attributes = new JsonObject { ["new_sha"] = "sha-two" },
                },
                new JournalResourceEffect
                {
                    Kind = ResourceKind.AdoWorkItemState,
                    Id = "workitem:3254",
                    Intent = ResourceIntent.SetState,
                    Mutation = ResourceMutation.Changed,
                    PolyphonyOwned = true,
                    Attributes = new JsonObject { ["target_state"] = "Done" },
                },
            ]);
        await SeedEntryAsync(
            runId: "run-fail",
            rootId: 3254,
            workItemId: 3254,
            action: "branch_failed",
            target: "feature/3254",
            startedAt: 1_700_000_000_200,
            outcome: JournalOutcome.Failure,
            effects:
            [
                new JournalResourceEffect
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/ignored",
                    Intent = ResourceIntent.EnsurePresent,
                    Mutation = ResourceMutation.CreatedNow,
                    PolyphonyOwned = true,
                },
            ]);

        var (exitCode, output) = await CaptureConsoleAsync(() => CreateQueryCommand().Query(3254));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.JournalQueryResult);

        exitCode.ShouldBe(ExitCodes.Success);
        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result.Effects.Select(effect => effect.Id).ShouldBe(["workitem:3254", "feature/3254"]);
        result.Effects.Single(effect => effect.Id == "feature/3254").ExpectedState.ShouldBe("present@sha-two");
        result.Effects.Single(effect => effect.Id == "feature/3254").Action.ShouldBe("branch_update");
    }

    [Fact]
    public async Task Query_KindFilter_Applies()
    {
        await SeedQueryFixtureAsync();

        var (_, output) = await CaptureConsoleAsync(() => CreateQueryCommand().Query(3254, kind: ResourceKind.GitBranch));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.JournalQueryResult);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result.Effects.All(effect => effect.Kind == ResourceKind.GitBranch).ShouldBeTrue();
    }

    [Fact]
    public async Task Query_OwnedOnlyFilter_Applies()
    {
        await SeedQueryFixtureAsync();

        var (_, output) = await CaptureConsoleAsync(() => CreateQueryCommand().Query(3254, ownedOnly: true));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.JournalQueryResult);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result.Effects.All(effect => effect.PolyphonyOwned).ShouldBeTrue();
    }

    [Fact]
    public async Task Query_StateFilter_Applies()
    {
        await SeedQueryFixtureAsync();

        var (_, output) = await CaptureConsoleAsync(() => CreateQueryCommand().Query(3254, state: "Done"));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.JournalQueryResult);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(1);
        result.Effects[0].Id.ShouldBe("workitem:3254");
        result.Effects[0].ExpectedState.ShouldBe("Done");
    }

    [Fact]
    public async Task Query_SinceUntilAndCompositionFilters_ApplyAndStyle()
    {
        await SeedQueryFixtureAsync();
        var since = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_090).ToString("O");
        var until = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_110).ToString("O");

        var (_, output) = await CaptureConsoleAsync(() => CreateQueryCommand().Query(
            3254,
            kind: ResourceKind.GitBranch,
            ownedOnly: true,
            state: "present@sha-two",
            since: since,
            until: until));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.JournalQueryResult);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(1);
        result.Effects[0].Id.ShouldBe("feature/3254");
        result.Effects[0].ExpectedState.ShouldBe("present@sha-two");
    }

    [Fact]
    public async Task Query_InvalidKind_ReturnsConfigError()
    {
        await SeedRootAsync(3254);

        var (exitCode, output) = await CaptureConsoleAsync(() => CreateQueryCommand().Query(3254, kind: "not_a_kind"));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var doc = JsonDocument.Parse(output);
        var error = doc.RootElement.GetProperty("error").GetString();
        error.ShouldNotBeNull();
        error.ShouldContain("kind must be one of");
    }

    [Fact]
    public async Task Query_InvalidSince_ReturnsConfigError()
    {
        await SeedRootAsync(3254);

        var (exitCode, output) = await CaptureConsoleAsync(() => CreateQueryCommand().Query(3254, since: "not-a-date"));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var doc = JsonDocument.Parse(output);
        var error = doc.RootElement.GetProperty("error").GetString();
        error.ShouldNotBeNull();
        error.ShouldContain("--since must be a valid ISO-8601 timestamp");
    }

    [Fact]
    public async Task Owned_RootNotFound_ReturnsCacheErrorWithCanonicalErrorJson()
    {
        var (exitCode, output) = await CaptureConsoleAsync(() => CreateOwnedCommand().Owned(99_993));

        exitCode.ShouldBe(ExitCodes.CacheError);
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
        doc.RootElement.GetProperty("work_item_id").GetInt32().ShouldBe(99_993);
    }

    [Fact]
    public async Task Owned_HappyPath_ReturnsOwnedProjection()
    {
        await SeedQueryFixtureAsync();

        var (exitCode, output) = await CaptureConsoleAsync(() => CreateOwnedCommand().Owned(3254));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.JournalOwnedResult);

        exitCode.ShouldBe(ExitCodes.Success);
        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result.OwnedResources.Select(resource => resource.Id).ShouldBe(["workitem:3254", "feature/3254"]);
        result.OwnedResources.Single(resource => resource.Id == "workitem:3254").ExpectedState.ShouldBe("Done");
    }

    [Fact]
    public async Task Owned_KindFilter_Applies()
    {
        await SeedQueryFixtureAsync();

        var (_, output) = await CaptureConsoleAsync(() => CreateOwnedCommand().Owned(3254, kind: ResourceKind.AdoWorkItemState));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.JournalOwnedResult);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(1);
        result.OwnedResources[0].Kind.ShouldBe(ResourceKind.AdoWorkItemState);
    }

    [Fact]
    public async Task Owned_InvalidKind_ReturnsConfigError()
    {
        await SeedRootAsync(3254);

        var (exitCode, output) = await CaptureConsoleAsync(() => CreateOwnedCommand().Owned(3254, kind: "nope"));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var doc = JsonDocument.Parse(output);
        var error = doc.RootElement.GetProperty("error").GetString();
        error.ShouldNotBeNull();
        error.ShouldContain("kind must be one of");
    }

    public override void Dispose()
    {
        base.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private JournalHasCommand CreateHasCommand() => new(_store, new RepositoryServiceProvider(Repository));

    private JournalQueryCommand CreateQueryCommand() => new(_store, new RepositoryServiceProvider(Repository));

    private JournalOwnedCommand CreateOwnedCommand() => new(_store, new RepositoryServiceProvider(Repository));

    private async Task SeedRootAsync(int rootId)
    {
        await SeedAsync(new WorkItemBuilder().WithId(rootId).WithType("Epic").WithTitle($"Root {rootId}").WithState("Doing").Build());
    }

    private async Task SeedQueryFixtureAsync()
    {
        await SeedRootAsync(3254);
        await SeedEntryAsync(
            runId: "run-a",
            rootId: 3254,
            workItemId: 3254,
            action: "branch_create",
            target: "feature/3254",
            startedAt: 1_700_000_000_000,
            effects:
            [
                new JournalResourceEffect
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/3254",
                    Intent = ResourceIntent.EnsurePresent,
                    Mutation = ResourceMutation.CreatedNow,
                    PolyphonyOwned = true,
                    Attributes = new JsonObject { ["new_sha"] = "sha-one" },
                },
                new JournalResourceEffect
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/external",
                    Intent = ResourceIntent.EnsurePresent,
                    Mutation = ResourceMutation.CreatedNow,
                    PolyphonyOwned = false,
                },
            ]);
        await SeedEntryAsync(
            runId: "run-b",
            rootId: 3254,
            workItemId: 3254,
            action: "branch_update",
            target: "feature/3254",
            startedAt: 1_700_000_000_100,
            effects:
            [
                new JournalResourceEffect
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/3254",
                    Intent = ResourceIntent.AdvancePointer,
                    Mutation = ResourceMutation.Changed,
                    PolyphonyOwned = true,
                    Attributes = new JsonObject { ["new_sha"] = "sha-two" },
                },
                new JournalResourceEffect
                {
                    Kind = ResourceKind.AdoWorkItemState,
                    Id = "workitem:3254",
                    Intent = ResourceIntent.SetState,
                    Mutation = ResourceMutation.Changed,
                    PolyphonyOwned = true,
                    Attributes = new JsonObject { ["target_state"] = "Done" },
                },
            ]);
    }

    private async Task<long> SeedEntryAsync(
        string runId,
        int? rootId,
        int? workItemId,
        string action,
        string target,
        long startedAt,
        JournalOutcome outcome = JournalOutcome.Success,
        IReadOnlyList<JournalResourceEffect>? effects = null)
    {
        var actionId = await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = runId,
                RootId = rootId,
                WorkItemId = workItemId,
                Action = action,
                Target = target,
                StartedAt = startedAt,
            },
            CancellationToken.None);

        await _store.RecordEndAsync(actionId, outcome, null, null, null, effects, CancellationToken.None);
        return actionId;
    }
}
