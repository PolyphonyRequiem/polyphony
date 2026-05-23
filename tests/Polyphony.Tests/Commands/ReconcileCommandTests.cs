using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Journal;
using Polyphony.Journal.Drift;
using Polyphony.Journal.Observers;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// W13 (AB#3294): coverage for <c>polyphony reconcile</c> — dry-run
/// report, --accept-external journal synthesis, --adopt single-resource
/// ownership transfer, and refusal paths.
/// </summary>
public sealed class ReconcileCommandTests : CommandTestBase
{
    private readonly string _tempDir;
    private readonly JournalStore _store;

    public ReconcileCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-reconcile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new JournalStore(Path.Combine(_tempDir, ".polyphony-state", "journal.db"));
    }

    [Fact]
    public async Task Reconcile_RootMissing_ReturnsRequiredInputHalt()
    {
        var command = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => command.Run());

        exitCode.ShouldNotBe(ExitCodes.Success);
        output.ShouldContain("--root");
    }

    [Fact]
    public async Task Reconcile_RootNotFound_ReturnsCacheError()
    {
        var command = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => command.Run(99_999));

        exitCode.ShouldBe(ExitCodes.CacheError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ReconcileResult)!;
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Reconcile_DryRun_ReportsDriftWithoutMutating()
    {
        await SeedAsync(new WorkItemBuilder().WithId(4001).WithType("Epic").WithTitle("Root").Build());
        await SeedEntryAsync(rootId: 4001, effects:
        [
            new JournalResourceEffect
            {
                Kind = ResourceKind.GitBranch,
                Id = "feature/4001",
                Intent = ResourceIntent.EnsurePresent,
                Mutation = ResourceMutation.CreatedNow,
                PolyphonyOwned = true,
            },
        ]);

        var observer = new FakeResourceObserver(new ResourceObservationBatch
        {
            Kind = ResourceKind.GitBranch,
            Observations = [
                new ObservedResourceState
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/4001",
                    Exists = true,
                    MatchesExpectedState = false,
                    ActualState = "def456",
                },
            ],
            DiscoveredResources = [],
        });
        var command = CreateCommand(observer);

        var (exitCode, output) = await CaptureConsoleAsync(() => command.Run(4001));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ReconcileResult)!;

        exitCode.ShouldBe(ExitCodes.Success);
        result.Success.ShouldBeTrue();
        result.Executed.ShouldBeFalse();
        result.AcceptExternal.ShouldBeFalse();
        result.AdoptTarget.ShouldBeNull();
        result.Drift.Findings.ShouldHaveSingleItem();
        result.Drift.Findings[0].Classification.ShouldBe(DriftClassifications.ExternalMutation);
        result.AcceptedExternal.ShouldBeEmpty();
        result.Adopted.ShouldBeEmpty();

        var entries = await _store.QueryAsync(new JournalQuery { RootId = 4001 }, CancellationToken.None);
        entries.Count.ShouldBe(1); // only the seeded fixture row, no reconcile rows
    }

    [Fact]
    public async Task Reconcile_AcceptExternal_DryRun_ListsPlannedAcceptsButDoesNotWrite()
    {
        await SeedAsync(new WorkItemBuilder().WithId(4002).WithType("Epic").Build());
        await SeedEntryAsync(rootId: 4002, effects:
        [
            new JournalResourceEffect
            {
                Kind = ResourceKind.GitBranch,
                Id = "feature/4002",
                Intent = ResourceIntent.EnsurePresent,
                Mutation = ResourceMutation.CreatedNow,
                PolyphonyOwned = true,
            },
        ]);

        var observer = new FakeResourceObserver(new ResourceObservationBatch
        {
            Kind = ResourceKind.GitBranch,
            Observations = [
                new ObservedResourceState
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/4002",
                    Exists = true,
                    MatchesExpectedState = false,
                    ActualState = "zzz999",
                },
            ],
            DiscoveredResources = [],
        });
        var command = CreateCommand(observer);

        var (exitCode, output) = await CaptureConsoleAsync(() => command.Run(4002, acceptExternal: true));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ReconcileResult)!;

        exitCode.ShouldBe(ExitCodes.Success);
        result.AcceptExternal.ShouldBeTrue();
        result.Executed.ShouldBeFalse();
        result.AcceptedExternal.ShouldHaveSingleItem();
        result.AcceptedExternal[0].Action.ShouldBe("reconcile_accept_external");
        result.AcceptedExternal[0].Id.ShouldBe("feature/4002");

        var entries = await _store.QueryAsync(new JournalQuery { RootId = 4002 }, CancellationToken.None);
        entries.Count.ShouldBe(1); // dry-run wrote nothing
    }

    [Fact]
    public async Task Reconcile_AcceptExternal_Execute_WritesSynthesizedRowsThatProjectionPicksUp()
    {
        await SeedAsync(new WorkItemBuilder().WithId(4003).WithType("Epic").Build());
        await SeedEntryAsync(rootId: 4003, effects:
        [
            new JournalResourceEffect
            {
                Kind = ResourceKind.GitBranch,
                Id = "feature/4003",
                Intent = ResourceIntent.EnsurePresent,
                Mutation = ResourceMutation.CreatedNow,
                PolyphonyOwned = true,
            },
        ]);

        var observer = new FakeResourceObserver(new ResourceObservationBatch
        {
            Kind = ResourceKind.GitBranch,
            Observations = [
                new ObservedResourceState
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/4003",
                    Exists = true,
                    MatchesExpectedState = false,
                    ActualState = "actual-sha",
                },
            ],
            DiscoveredResources = [],
        });
        var command = CreateCommand(observer);

        var (exitCode, output) = await CaptureConsoleAsync(() =>
            command.Run(4003, acceptExternal: true, execute: true));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ReconcileResult)!;

        exitCode.ShouldBe(ExitCodes.Success);
        result.Executed.ShouldBeTrue();
        result.AcceptedExternal.ShouldHaveSingleItem();

        var entries = await _store.QueryAsync(new JournalQuery { RootId = 4003 }, CancellationToken.None);
        entries.Count.ShouldBe(2);
        var reconcileRow = entries.Single(e => e.Action == "reconcile_accept_external");
        reconcileRow.Outcome.ShouldBe(JournalOutcome.Success);
        reconcileRow.Effects.ShouldHaveSingleItem();
        reconcileRow.Effects[0].Kind.ShouldBe(ResourceKind.GitBranch);
        reconcileRow.Effects[0].Id.ShouldBe("feature/4003");
        reconcileRow.Effects[0].PolyphonyOwned.ShouldBeTrue();
    }

    [Fact]
    public async Task Reconcile_AdoptUnknownResource_RefusesWithRoutingFailure()
    {
        await SeedAsync(new WorkItemBuilder().WithId(4004).WithType("Epic").Build());
        var command = CreateCommand();

        var (exitCode, output) = await CaptureConsoleAsync(() =>
            command.Run(4004, adopt: "git_branch:does-not-exist", execute: true));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ReconcileResult)!;

        exitCode.ShouldBe(ExitCodes.RoutingFailure);
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("--adopt");
    }

    [Fact]
    public async Task Reconcile_AdoptMalformed_ReturnsConfigError()
    {
        await SeedAsync(new WorkItemBuilder().WithId(4005).WithType("Epic").Build());
        var command = CreateCommand();

        var (exitCode, output) = await CaptureConsoleAsync(() =>
            command.Run(4005, adopt: "no-colon-here"));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ReconcileResult)!;

        exitCode.ShouldBe(ExitCodes.ConfigError);
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("KIND:ID");
    }

    [Fact]
    public async Task Reconcile_AdoptOrphan_Execute_MarksResourceOwnedUnderCurrentLineage()
    {
        await SeedAsync(new WorkItemBuilder().WithId(4006).WithType("Epic").Build());
        // Seed a separate expected resource so the observer leg runs;
        // the orphan is then surfaced via DiscoveredResources alongside it.
        await SeedEntryAsync(rootId: 4006, effects:
        [
            new JournalResourceEffect
            {
                Kind = ResourceKind.GitBranch,
                Id = "plan/4006",
                Intent = ResourceIntent.EnsurePresent,
                Mutation = ResourceMutation.CreatedNow,
                PolyphonyOwned = true,
            },
        ]);
        var observer = new FakeResourceObserver(new ResourceObservationBatch
        {
            Kind = ResourceKind.GitBranch,
            Observations = [
                new ObservedResourceState
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "plan/4006",
                    Exists = true,
                    MatchesExpectedState = true,
                    ActualState = "consistent",
                },
            ],
            DiscoveredResources = [
                new DiscoveredResourceState
                {
                    Kind = ResourceKind.GitBranch,
                    Id = "feature/4006",
                    ActualState = "present",
                    MatchesPolyphonyPattern = true,
                },
            ],
        });
        var command = CreateCommand(observer);
        var runContext = new RunContext();
        // Re-create command with a known RunContext we can inspect.
        command = new ReconcileCommand(_store, Repository, new JournalDriftAnalyzer([observer]), runContext);

        var (exitCode, output) = await CaptureConsoleAsync(() =>
            command.Run(4006, adopt: $"{ResourceKind.GitBranch}:feature/4006", execute: true));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ReconcileResult)!;

        exitCode.ShouldBe(ExitCodes.Success);
        result.Executed.ShouldBeTrue();
        result.Adopted.ShouldHaveSingleItem();
        result.Adopted[0].Action.ShouldBe("reconcile_adopt");
        result.Adopted[0].Classification.ShouldBe(DriftClassifications.ExternalCreatePolyphonyNamed);

        var entries = await _store.QueryAsync(new JournalQuery { RootId = 4006 }, CancellationToken.None);
        var adoptRow = entries.Single(e => e.Action == "reconcile_adopt");
        adoptRow.RunId.ShouldBe(runContext.RunId);
        adoptRow.Outcome.ShouldBe(JournalOutcome.Success);
        adoptRow.Effects.ShouldHaveSingleItem();
        adoptRow.Effects[0].PolyphonyOwned.ShouldBeTrue();
    }

    private ReconcileCommand CreateCommand(params IResourceObserver[] observers)
        => new(_store, Repository, new JournalDriftAnalyzer(observers), new RunContext());

    private async Task SeedEntryAsync(int rootId, IReadOnlyList<JournalResourceEffect>? effects = null)
    {
        var actionId = await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = "run-reconcile",
                RootId = rootId,
                Action = "fixture",
                Target = $"root:{rootId}",
                StartedAt = 1_700_000_000_000,
            },
            CancellationToken.None);
        await _store.RecordEndAsync(actionId, JournalOutcome.Success, null, null, null, effects, CancellationToken.None);
    }

    private sealed class FakeResourceObserver(ResourceObservationBatch batch) : IResourceObserver
    {
        public string Kind => batch.Kind;
        public bool CanObserve => true;
        public string? DeferredReason => null;
        public Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
            => Task.FromResult(batch);
    }
}
